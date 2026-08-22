using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// Builds a fresh protection variant: identifiers, a vertex identity mix, a digit ordering and
    /// a hash program.
    ///
    /// A randomly assembled hash is not automatically a good one, so nothing leaves this class
    /// until it has been measured. Every candidate has to pass an avalanche test on both of its
    /// inputs; candidates that do not are discarded and regenerated. Without that gate a build
    /// could silently ship a program where, say, digit 4 barely affects the output - which is the
    /// separable-bits weakness the whole rewrite exists to remove.
    /// </summary>
    public static class MeshProtectVariantGenerator
    {
        private const int MinOps = 12;
        private const int MaxOps = 16;
        private const int MaxAttempts = 200;

        /// <summary>
        /// How far the flip rate may sit from half, pooled over all 32 output bits and again over
        /// each 16-bit half. 65,536 and 32,768 observations respectively, so the standard error of
        /// the rate is about 0.002 and 0.003 - this is ten and seven sigma, which is a gate rather
        /// than a tripwire.
        /// </summary>
        private const double MaxBitBias = 0.02;

        /// <summary>
        /// The same thing for a single bit position, which only has AvalancheSamples observations
        /// behind it - a standard error of about 0.011 against 0.002 for the pooled numbers. This
        /// is a backstop for a bit that is stuck or strongly lopsided, not a sensitive instrument;
        /// the per-half number is the sensitive one, and it is the one that answers the question
        /// worth asking, because the halves are the two displacement amplitudes.
        ///
        /// It has to clear the noise floor by a wide margin or it is worse than nothing. The suite
        /// re-measures each variant with fresh samples, so a threshold sitting near that floor
        /// makes a check that passes and fails at random - and the maximum is taken over roughly
        /// two hundred bit measurements per variant, where sampling alone reaches three to five
        /// sigma routinely. Measured across 24 generated variants the worst single bit came to
        /// 0.067, with the halves at 0.005: no axis imbalance, just one bit a few percent off
        /// centre in a twelve-operation program, which is ordinary. The threshold itself sits
        /// eleven sigma from centre, so 0.067 is comfortably inside it, and a bit that never flips
        /// at all - a deviation of 0.5, forty-five sigma - is nowhere near.
        /// </summary>
        private const double MaxSingleBitBias = 0.12;

        private const int AvalancheSamples = 2048;

        public static MeshProtectVariant Generate(System.Random rng)
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var variant = Assemble(rng);
                if (Measure(variant, rng, out string _)) return variant;
            }

            throw new InvalidOperationException(
                "[MeshProtect] Could not generate a hash program that passes the avalanche test in " +
                $"{MaxAttempts} attempts. This should be impossible; please report it.");
        }

        /// <summary>Exposed so the test harness can report why a candidate was rejected.</summary>
        public static bool Measure(MeshProtectVariant variant, System.Random rng, out string report)
        {
            // Every value a position can hold, which is nine and not eight. NotEntered is one of
            // them: it is what a fresh avatar carries, what the reset control writes, and - since
            // passwords can be shorter than six digits - part of the correct answer. Sampling only
            // 1..8 left the top bit of all six nibbles stuck at zero, so a quarter of the key was
            // never measured, and the one transition that separates "the author left this position
            // out" from "somebody tapped it" was never measured at all.
            var digits = new int[MeshProtectRoot.PasswordLength];
            for (int i = 0; i < digits.Length; i++)
                digits[i] = rng.Next(MeshProtectRoot.NotEntered, MeshProtectRoot.MaxDigit + 1);

            // PackReachable, not PackDigits: these combinations are what the shader can be handed,
            // and most of them are not valid passwords - a wearer can set the fourth position while
            // the third is untouched.
            uint key = MeshProtectCipher.PackReachable(digits, variant);

            double worstKeyBias = 0;
            double worstKeyHalfBias = 0;
            double worstKeySingleBitBias = 0;
            double worstKeyChanged = 1;

            // 1. Changing any one password digit must move every vertex, and must flip each output
            //    bit about half the time.
            for (int position = 0; position < MeshProtectRoot.PasswordLength; position++)
            {
                // Two changes per position, not one. Stepping through 1..8 never crosses between a
                // digit and NotEntered, and that crossing is the only difference between a position
                // the author left out of a short password and one a wearer has tapped - so a
                // program that mixed that bit badly would hash the two the same, and "472" would
                // also be opened by "4721". Each poorly mixed bit is one extra working password,
                // while the strength this tool prints to the author counts none of them.
                int current = digits[position];
                int steppedDigit = current == MeshProtectRoot.NotEntered ||
                                   current == MeshProtectRoot.MaxDigit
                    ? MeshProtectRoot.MinDigit
                    : current + 1;
                int toggledEntry = current == MeshProtectRoot.NotEntered
                    ? MeshProtectRoot.MaxDigit
                    : MeshProtectRoot.NotEntered;

                var pairs = new List<(int[] a, int[] b)>();
                foreach (int replacement in new[] { steppedDigit, toggledEntry })
                {
                    var altered = (int[])digits.Clone();
                    altered[position] = replacement;
                    pairs.Add((digits, altered));
                }

                // The hardest single change this key can undergo, measured at every position rather
                // than waited for. MinDigit is nibble 0 and NotEntered is nibble 8, so these two
                // keys differ in exactly one bit - the top bit of this position - and a one bit
                // change is the worst case for an avalanche. Leaving it to the random base above
                // meant a program could be accepted without it ever being tried, and then fail when
                // the same measurement ran again with a different draw. It is also the change that
                // matters most: it is the whole difference between a position a short password
                // leaves out and one somebody has tapped.
                var low = (int[])digits.Clone();
                var high = (int[])digits.Clone();
                low[position] = MeshProtectRoot.MinDigit;
                high[position] = MeshProtectRoot.NotEntered;
                pairs.Add((low, high));

                foreach (var (a, b) in pairs)
                {
                    Compare(variant, rng,
                            MeshProtectCipher.PackReachable(a, variant),
                            MeshProtectCipher.PackReachable(b, variant),
                            sameKeyDifferentId: false,
                            out double changed, out double bias, out double halfBias, out double bitBias);
                    worstKeyChanged = Math.Min(worstKeyChanged, changed);
                    worstKeyBias = Math.Max(worstKeyBias, bias);
                    worstKeyHalfBias = Math.Max(worstKeyHalfBias, halfBias);
                    worstKeySingleBitBias = Math.Max(worstKeySingleBitBias, bitBias);
                }
            }

            // 2. Two vertices whose UV0 differs by a single mantissa bit must land somewhere
            //    unrelated. Without this, adjacent vertices could receive correlated displacement
            //    and the surface would stay locally smooth - which is exactly the signal a
            //    smoothness-based recovery attack looks for.
            Compare(variant, rng, key, key, sameKeyDifferentId: true,
                    out double idChanged, out double idBias, out double idHalfBias,
                    out double idSingleBitBias);

            // NOT PINNED BY ANY TEST: worstKeyHalfBias and idHalfBias. The case they exist for is
            // halves leaning opposite ways - 0.62 against 0.38, say - where the pooled rate is
            // exactly half and every individual bit is still inside MaxSingleBitBias. Building a
            // program that does that on purpose needs a mixing function biased by a few percent
            // across a whole half, and every simple construction fails a cheaper check first: kill
            // a half outright and its bits sit at a flip rate of zero, which the per-bit check
            // catches. So the fixture in the suite pins the per-bit check, and this pair is
            // argued for rather than demonstrated. It is kept because it catches something the
            // per-bit check provably cannot: a half leaning 60/40 is 0.10 per bit, inside
            // MaxSingleBitBias, and 0.10 per half, five times outside MaxBitBias.
            //
            // The pooled numbers are deliberately absent from this decision. Pooled is the mean
            // of the two half rates, computed from the same counters with no sampling in between,
            // so |pooled - 0.5| is at most the larger half's deviation - halfBias <= MaxBitBias
            // implies bitBias <= MaxBitBias as algebra. A conjunct that cannot be the one that
            // fails is not telling anybody anything, and it reads as though it were guarding
            // something. They are still measured and still reported, because the number is worth
            // seeing in a log and because a test reads it.
            bool ok = worstKeyChanged == 1.0
                   && worstKeyHalfBias <= MaxBitBias
                   && worstKeySingleBitBias <= MaxSingleBitBias
                   && idChanged == 1.0
                   && idHalfBias <= MaxBitBias
                   && idSingleBitBias <= MaxSingleBitBias;

            report = $"keyChanged={worstKeyChanged:P1} keyBias={worstKeyBias:F4} " +
                     $"keyHalfBias={worstKeyHalfBias:F4} keyBitBias={worstKeySingleBitBias:F4} " +
                     $"idChanged={idChanged:P1} idBias={idBias:F4} idHalfBias={idHalfBias:F4} " +
                     $"idBitBias={idSingleBitBias:F4} ops={variant.ops.Length}";
            return ok;
        }

        /// <summary>
        /// Three readings of the same samples, because one of them cannot see what the other two
        /// are for. Pooling all 32 positions hides a program whose halves lean opposite ways, and
        /// those halves are the two displacement amplitudes: Coefficients() reads the low 16 bits
        /// as the tangent-direction one and the high 16 as the normal-direction one.
        /// </summary>
        private static void Compare(MeshProtectVariant variant, System.Random rng,
                                    uint keyA, uint keyB, bool sameKeyDifferentId,
                                    out double fractionChanged, out double bitBias,
                                    out double halfBias, out double singleBitBias)
        {
            int changed = 0;
            var flippedAt = new long[32];

            for (int i = 0; i < AvalancheSamples; i++)
            {
                var uv = new Vector2((float)rng.NextDouble(), (float)rng.NextDouble());
                uint idA = MeshProtectCipher.VertexId(uv, variant);

                uint idB = idA;
                if (sameKeyDifferentId)
                {
                    // Perturb UV0 by one unit in the last place - the smallest difference two
                    // genuinely distinct vertices can have.
                    var neighbour = new Vector2(FromBits(MeshProtectCipher.AsUInt(uv.x) + 1u), uv.y);
                    idB = MeshProtectCipher.VertexId(neighbour, variant);
                }

                uint hA = MeshProtectCipher.Hash(idA, keyA, variant);
                uint hB = MeshProtectCipher.Hash(idB, keyB, variant);

                if (hA != hB) changed++;
                uint diff = hA ^ hB;
                for (int bit = 0; bit < 32; bit++)
                    if (((diff >> bit) & 1u) != 0) flippedAt[bit]++;
            }

            fractionChanged = changed / (double)AvalancheSamples;

            long all = 0, low = 0, high = 0;
            for (int bit = 0; bit < 32; bit++)
            {
                all += flippedAt[bit];
                if (bit < 16) low += flippedAt[bit]; else high += flippedAt[bit];
            }

            bitBias = Math.Abs(all / (double)(AvalancheSamples * 32) - 0.5);
            halfBias = Math.Max(Math.Abs(low / (double)(AvalancheSamples * 16) - 0.5),
                                Math.Abs(high / (double)(AvalancheSamples * 16) - 0.5));

            singleBitBias = 0.0;
            for (int bit = 0; bit < 32; bit++)
                singleBitBias = Math.Max(singleBitBias,
                    Math.Abs(flippedAt[bit] / (double)AvalancheSamples - 0.5));
        }

        private static float FromBits(uint bits)
        {
            var b = BitConverter.GetBytes(bits);
            return BitConverter.ToSingle(b, 0);
        }

        // ------------------------------------------------------------------ assembly

        private static MeshProtectVariant Assemble(System.Random rng)
        {
            string id = Hex(rng, 8);
            var taken = new HashSet<string>();

            var variant = new MeshProtectVariant
            {
                format = MeshProtectVariant.CurrentFormat,
                id = id,
                // No slash: this becomes the first path segment of the lilToon shader family, and
                // lilToon appends its own subdirectories after it. Letters only, and drawn from the
                // same pool as every other generated name - it used to be "MP" plus the id, which
                // matched every avatar this tool produced with one regex.
                shaderName = Word(rng, taken, 10),
                idMulX = unchecked((int)OddConstant(rng)),
                idMulY = unchecked((int)OddConstant(rng)),
                idAdd = unchecked((int)Constant(rng)),
                idShift = rng.Next(11, 20),
                idSwapChannels = rng.Next(2) == 0,
                coefficientXor = unchecked((int)Constant(rng)),
                swapCoefficients = rng.Next(2) == 0
            };

            variant.bypassProperty = Property(rng, taken);
            variant.modeProperty = Property(rng, taken);
            variant.macSalt = unchecked((int)Constant(rng));
            variant.digitProperties = new string[MeshProtectRoot.PasswordLength];
            variant.parameterNames = new string[MeshProtectRoot.PasswordLength];
            for (int i = 0; i < MeshProtectRoot.PasswordLength; i++)
            {
                variant.digitProperties[i] = Property(rng, taken);
                // No shared prefix and, above all, no index. The old names ended in _0 to _5, which
                // told anyone reading the parameter list exactly which one was digit four.
                variant.parameterNames[i] = Word(rng, taken, 9);
            }

            variant.macProperty = Property(rng, taken);

            variant.bitNames = new string[MeshProtectRoot.PasswordLength * MeshProtectRoot.BitsPerDigit];
            for (int i = 0; i < variant.bitNames.Length; i++)
                variant.bitNames[i] = Word(rng, taken, 9);

            variant.packLayerNames = new string[MeshProtectRoot.PasswordLength];
            variant.decodeLayerNames = new string[MeshProtectRoot.PasswordLength];
            variant.padLayerNames = new string[3];
            variant.digitMenuAssetNames = new string[MeshProtectRoot.PasswordLength];
            for (int i = 0; i < MeshProtectRoot.PasswordLength; i++)
            {
                variant.packLayerNames[i] = Word(rng, taken, 8);
                variant.decodeLayerNames[i] = Word(rng, taken, 8);
                variant.digitMenuAssetNames[i] = Word(rng, taken, 8);
            }
            for (int i = 0; i < variant.padLayerNames.Length; i++)
                variant.padLayerNames[i] = Word(rng, taken, 8);
            variant.menuAssetName = Word(rng, taken, 8);
            variant.rootMenuAssetName = Word(rng, taken, 8);
            variant.wrapperMenuAssetName = Word(rng, taken, 8);
            variant.parametersAssetName = Word(rng, taken, 8);
            variant.controllerAssetName = Word(rng, taken, 8);

            variant.digitBitOrder = Shuffle(rng, MeshProtectRoot.PasswordLength);
            variant.ops = BuildProgram(rng);
            return variant;
        }

        /// <summary>
        /// Assemble a hash program under structural constraints. The constraints are not decoration
        /// - they are what makes the avalanche test pass often enough to terminate quickly, and
        /// what stops a degenerate program (all additions, key injected once at the very end) from
        /// even being proposed.
        /// </summary>
        private static MeshProtectHashOp[] BuildProgram(System.Random rng)
        {
            int length = rng.Next(MinOps, MaxOps + 1);

            // Reserve the last two slots for a finaliser: a multiply spreads low bits upward, the
            // xor-shift then folds the high bits back down.
            int body = length - 2;

            var kinds = new List<int>();

            // Guaranteed material first, then filler, then shuffle - this is how the minimum counts
            // are met without rejection sampling.
            for (int i = 0; i < 3; i++) kinds.Add(MeshProtectHashOp.MulConst);
            for (int i = 0; i < 3; i++) kinds.Add(MeshProtectHashOp.XorShiftRight);
            kinds.Add(MeshProtectHashOp.XorKeyMul);
            kinds.Add(MeshProtectHashOp.AddKeyXor);

            while (kinds.Count < body)
            {
                kinds.Add(new[]
                {
                    MeshProtectHashOp.XorConst,
                    MeshProtectHashOp.AddConst,
                    MeshProtectHashOp.MulConst,
                    MeshProtectHashOp.XorShiftRight,
                    MeshProtectHashOp.XorShiftLeft,
                    MeshProtectHashOp.XorKeyMul,
                    MeshProtectHashOp.AddKeyXor,
                    MeshProtectHashOp.Rotate
                }[rng.Next(8)]);
            }

            // Fisher-Yates.
            for (int i = kinds.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (kinds[i], kinds[j]) = (kinds[j], kinds[i]);
            }

            // The key has to enter early enough that the rounds after it can diffuse its influence,
            // and again later so that no prefix of the program is key-independent.
            EnsureKeyInjection(rng, kinds, 0, Math.Max(1, body / 3));
            EnsureKeyInjection(rng, kinds, body / 2, body);

            kinds.Add(MeshProtectHashOp.MulConst);
            kinds.Add(MeshProtectHashOp.XorShiftRight);

            var ops = new MeshProtectHashOp[kinds.Count];
            for (int i = 0; i < kinds.Count; i++)
            {
                int kind = kinds[i];
                ops[i] = new MeshProtectHashOp
                {
                    kind = kind,
                    constant = unchecked((int)(kind == MeshProtectHashOp.MulConst ||
                                               kind == MeshProtectHashOp.XorKeyMul
                        ? OddConstant(rng)
                        : Constant(rng))),
                    shift = kind == MeshProtectHashOp.XorShiftRight ? rng.Next(11, 20)
                          : kind == MeshProtectHashOp.XorShiftLeft ? rng.Next(5, 14)
                          : kind == MeshProtectHashOp.Rotate ? rng.Next(5, 28)
                          : 0
                };
            }
            return ops;
        }

        private static void EnsureKeyInjection(System.Random rng, List<int> kinds, int from, int to)
        {
            for (int i = from; i < to && i < kinds.Count; i++)
                if (kinds[i] == MeshProtectHashOp.XorKeyMul || kinds[i] == MeshProtectHashOp.AddKeyXor)
                    return;

            int slot = from + rng.Next(Math.Max(1, Math.Min(to, kinds.Count) - from));
            kinds[Math.Min(slot, kinds.Count - 1)] =
                rng.Next(2) == 0 ? MeshProtectHashOp.XorKeyMul : MeshProtectHashOp.AddKeyXor;
        }

        private static int[] Shuffle(System.Random rng, int count)
        {
            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            for (int i = count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            return order;
        }

        private static string Property(System.Random rng, HashSet<string> taken)
        {
            // Shader properties conventionally start with an underscore and every shader has some,
            // so that leading character carries no information. What follows used to be "z" plus
            // hex, which did.
            return "_" + Word(rng, taken, 8);
        }

        /// <summary>
        /// A pronounceable random word: consonant, vowel, consonant, vowel.
        ///
        /// Deliberately not hex. A block of names like "z8e2b3a4" reads as machine-generated even
        /// without knowing the tool, which is most of the way to a signature; "takeromi" reads as
        /// something the avatar's author called a toggle. Nothing here is trying to be
        /// unguessable - the point is only that a scanner cannot match a pattern across avatars.
        /// </summary>
        private static string Word(System.Random rng, HashSet<string> taken, int length)
        {
            const string consonants = "bcdfghjklmnprstvwz";
            const string vowels = "aeiou";

            for (int attempt = 0; attempt < 256; attempt++)
            {
                var chars = new char[length];
                for (int i = 0; i < length; i++)
                    chars[i] = (i % 2 == 0) ? consonants[rng.Next(consonants.Length)]
                                            : vowels[rng.Next(vowels.Length)];
                string word = new string(chars);
                if (taken.Add(word)) return word;
            }
            throw new InvalidOperationException("Could not find a free generated name.");
        }

        private static string Hex(System.Random rng, int digits)
        {
            const string alphabet = "0123456789abcdef";
            var chars = new char[digits];
            for (int i = 0; i < digits; i++) chars[i] = alphabet[rng.Next(16)];
            return new string(chars);
        }

        private static uint Constant(System.Random rng)
        {
            // System.Random tops out below 2^31, so build the full width from two draws.
            uint value = ((uint)rng.Next(1 << 16) << 16) | (uint)rng.Next(1 << 16);
            return value == 0 ? 0xA5A5A5A5u : value;
        }

        private static uint OddConstant(System.Random rng)
        {
            // Odd multipliers are invertible modulo 2^32, so a multiply round never collapses the
            // state space onto a smaller set.
            return Constant(rng) | 1u;
        }
    }
}
