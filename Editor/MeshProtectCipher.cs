using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// The C# half of the displacement cipher.
    ///
    /// There is no fixed algorithm here any more. Every avatar gets its own hash program - see
    /// MeshProtectVariant and MeshProtectVariantGenerator - and this class is one of the two
    /// interpreters of it. The other is the generated HLSL. Because both read the same op list
    /// there is no hand-written copy of the algorithm to drift out of sync.
    ///
    /// Design notes, because the shape of this is deliberate:
    ///
    /// 1. Per-vertex displacement is GENERATED from the key, never stored. An earlier version kept
    ///    per-vertex factors in TEXCOORD6/7 and let the key contribute only four shared scalars.
    ///    That made the restore linear in those four unknowns, so an attacker could recover the
    ///    mesh with a least-squares fit against a smoothness objective without ever touching the
    ///    password.
    ///
    ///    This used to claim that the change left "nothing continuous left to fit", on the grounds
    ///    that the only unknown is now the key. That is the defender's view of it and it is wrong.
    ///    The attacker does not have to recover the key: they are handed the displaced position,
    ///    the ORIGINAL normal (the decode needs it, so it ships), and the per-vertex amplitude in
    ///    the clear in TEXCOORD6. The original therefore sits inside a known box around the shipped
    ///    position, and the unknown is one or two BOUNDED SCALARS per vertex - continuous, and
    ///    exactly what a smoothness objective fits. What actually changed is the ratio: four
    ///    unknowns against a whole mesh of constraints was trivially over-determined, and per-vertex
    ///    unknowns are not.
    ///
    ///    Measured, on one avatar, with a Laplacian fit - see Tests/Diagnostics/MPSmoothnessAttack:
    ///    in NORMAL mode that fit recovered 56-89% of the displacement and on two of three meshes
    ///    reached the floor, meaning it undid everything the displacement had done. In TANGENT
    ///    SPACE it recovered 28-45%, and on the body mesh it stayed 3.1 cm out against a floor of
    ///    0.25 cm. The reason is structural rather than lucky: a smoothness prior only says where a
    ///    surface should be along its NORMAL, so it constrains normal-direction displacement
    ///    completely and says almost nothing about the tangential half.
    ///
    ///    That is the argument for tangent space being the default, and against ever falling back
    ///    to Normal quietly.
    ///
    /// 2. Everything is integer arithmetic plus one multiplication by a power of two. Integer ops
    ///    are bit-identical between C# and HLSL, and 1/32768 is exactly representable, so the
    ///    COEFFICIENTS agree to the last bit on every GPU - measured, 0/512 samples differed on
    ///    D3D11. Do not introduce sin/cos/sqrt/normalize here.
    ///
    ///    Note the scope of that claim: the cipher is exact, the float composition that follows it
    ///    is not quite. `tangent * (amp * a) + normal * (amp * b)` is two multiplies and an add in
    ///    C# but the shader compiler is free to contract it into mad, which rounds once instead of
    ///    twice. Measured worst case on the full decode: 2.2e-8 m, one ULP at that magnitude.
    ///
    /// 3. The vertex identity comes from the RAW BIT PATTERN of UV0. That is deliberate: any
    ///    re-serialisation of the mesh - export to FBX/glTF, a trip through a DCC tool, a
    ///    re-import - requantises those floats, changes every vertex identity, and permanently
    ///    breaks the restore even for someone holding the correct password. It forces an attacker
    ///    to work in place on the original binary instead of using the usual rip-and-reimport
    ///    pipeline. Only UV0 may be used: normals and tangents reach the vertex shader already
    ///    skinned, so their bit patterns are not stable.
    /// </summary>
    public static class MeshProtectCipher
    {
        /// <summary>
        /// A power of two, so int -> float scaling is exact in both C# and HLSL. Coefficients land
        /// uniformly in [-1, 1). This one is NOT randomised per variant - exactness depends on it.
        /// </summary>
        public const float CoefficientScale = 1f / 32768f;

        // ------------------------------------------------------------------ password

        /// <summary>
        /// Six digits, each 1..8. All 262144 combinations are usable.
        ///
        /// There used to be an exception here: 111111 was refused, because three bits per digit had
        /// no pattern left for "not entered" and the shipped material's zeroes clamped to the
        /// lowest digit - so that one password would have shipped already unlocked. The four-bit
        /// encoding reserves zero for the locked state, so the collision is gone and the special
        /// case with it.
        /// </summary>
        public static int[] GeneratePassword(System.Random rng)
        {
            var digits = new int[MeshProtectRoot.PasswordLength];
            for (int i = 0; i < digits.Length; i++)
                digits[i] = rng.Next(MeshProtectRoot.MinDigit, MeshProtectRoot.MaxDigit + 1);
            return digits;
        }

        /// <summary>
        /// Pack the digits into the 24-bit key the hash consumes, in the variant's own slot order.
        /// Digits are 1..8 so that each one fits a VRChat menu page with no overflow sub-menu, and
        /// each takes four bits: 0..7 for the digits, and the spare pattern for a position nobody
        /// entered. See MeshProtectRoot.KeyNibble.
        /// </summary>
        public static uint PackDigits(int[] digits, MeshProtectVariant variant)
        {
            if (digits == null || digits.Length != MeshProtectRoot.PasswordLength)
                throw new ArgumentException(
                    $"Password must contain {MeshProtectRoot.PasswordLength} digits.", nameof(digits));
            if (variant == null || !variant.IsValid(MeshProtectRoot.PasswordLength))
                throw new ArgumentException("The protection variant is missing or malformed.", nameof(variant));

            int length = MeshProtectRoot.TypedLength(digits);
            if (length == 0)
                throw new ArgumentOutOfRangeException(nameof(digits),
                    $"A password is 1 to {MeshProtectRoot.PasswordLength} digits of " +
                    $"{MeshProtectRoot.MinDigit}-{MeshProtectRoot.MaxDigit}, and any positions left " +
                    "over must be at the end.");

            return PackReachable(digits, variant);
        }

        /// <summary>
        /// Pack any combination the wearer's menu can produce, valid password or not.
        ///
        /// PackDigits refuses anything that is not a password - zeros have to be trailing - and that
        /// is right for a password. It is wrong for the space the SHADER sees: the six positions are
        /// independent controls, so a wearer can set the fourth and leave the third alone, and every
        /// one of the 9^6 combinations is a key some client will hand the decode. The generator's
        /// acceptance measurement has to sample that space rather than the 8^6 of valid six digit
        /// passwords, or a whole bit of every position is never measured.
        /// </summary>
        public static uint PackReachable(int[] digits, MeshProtectVariant variant)
        {
            uint key = 0;
            for (int i = 0; i < digits.Length; i++)
            {
                int slot = variant.digitBitOrder[i];
                key |= (uint)MeshProtectRoot.KeyNibble(digits[i])
                       << (MeshProtectRoot.BitsPerDigit * slot);
            }
            return key;
        }

        /// <summary>
        /// Parse a typed password. Returns false with a specific reason rather than a generic
        /// "invalid": the constraint - one to six digits, each 1 to 8 - is not guessable from the
        /// field. Shorter than six is stored as the digits followed by NotEntered.
        /// </summary>
        public static bool TryParsePassword(string text, out int[] digits, out string error)
        {
            digits = null;
            error = null;

            string trimmed = (text ?? "").Trim();
            if (trimmed.Length < 1 || trimmed.Length > MeshProtectRoot.PasswordLength)
            {
                error = trimmed.Length == 0
                    ? MeshProtectL10n.Tr("password.err.empty")
                    : MeshProtectL10n.Tr("password.err.toolong",
                                         MeshProtectRoot.PasswordLength, trimmed.Length);
                return false;
            }

            // Six long whatever was typed. The positions past the end stay at NotEntered, which is
            // a value the key carries and the menu cannot produce - so they are part of the password
            // rather than a gap in it.
            var parsed = new int[MeshProtectRoot.PasswordLength];
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c < '0' + MeshProtectRoot.MinDigit || c > '0' + MeshProtectRoot.MaxDigit)
                {
                    // The range is the menu's, not an arbitrary rule: a VRChat menu page holds
                    // eight items, so nine and zero have nowhere to live.
                    error = MeshProtectL10n.Tr("password.err.digit", i + 1, c,
                                               MeshProtectRoot.MinDigit, MeshProtectRoot.MaxDigit);
                    return false;
                }
                parsed[i] = c - '0';
            }

            digits = parsed;
            return true;
        }

        /// <summary>
        /// A note if the password is one somebody would try by hand, or null.
        ///
        /// Advisory, not enforced. The password's job is stopping "downloaded it, put it on" - a
        /// ripper who reverses the shader brute-forces 18 bits in seconds whatever it is - and
        /// against that one job, a password somebody would guess in ten tries really is worse.
        /// </summary>
        public static string DescribeWeakness(int[] digits)
        {
            int length = MeshProtectRoot.TypedLength(digits);
            if (length == 0) return null;

            bool allSame = true, ascending = true, descending = true;
            for (int i = 1; i < length; i++)
            {
                allSame &= digits[i] == digits[0];
                ascending &= digits[i] == digits[i - 1] + 1;
                descending &= digits[i] == digits[i - 1] - 1;
            }

            if (length > 1 && allSame)
                return MeshProtectL10n.Tr("password.weak.same");
            if (length > 2 && (ascending || descending))
                return MeshProtectL10n.Tr("password.weak.run");

            // Said with the number rather than as advice. Somebody working through the menu by hand
            // manages an attempt every few seconds, and short passwords are tried first because
            // they are cheap - so the honest comparison is not "shorter is weaker" but how long the
            // avatar holds out. The dials stay at six whatever the length, so the menu itself never
            // says which of these the wearer is up against.
            if (length < MeshProtectRoot.PasswordLength)
            {
                long combinations = 1;
                for (int i = 0; i < length; i++) combinations *= MeshProtectRoot.MaxDigit;

                return MeshProtectL10n.Tr("password.weak.short",
                                          length, combinations.ToString("N0"), Hours(combinations),
                                          MeshProtectRoot.PasswordLength);
            }

            return null;
        }

        /// <summary>
        /// A short string that identifies a password without being one.
        ///
        /// For saying "the upload used the password on your screen" without putting the password in
        /// Editor.log. That file gets pasted whole into Discord threads and issue reports, and the
        /// console is on screen during recordings and streams - and the thing being leaked is the
        /// six digits this tool tells authors to write down and keep.
        ///
        /// Taken from the check value, which ships inside every protected material already, so this
        /// gives away nothing that reading one avatar's files would not. It is not a secret and it
        /// is not meant to be hard to reverse: 8^6 is small enough to walk through, and anybody
        /// prepared to read the generated shader was never stopped by a password. What it is for is
        /// comparing two short strings by eye.
        /// </summary>
        public static string CheckCode(int[] digits, MeshProtectVariant variant)
        {
            if (MeshProtectRoot.TypedLength(digits) == 0 ||
                variant == null || !variant.IsValid(MeshProtectRoot.PasswordLength))
                return "----";

            return (Mac(PackDigits(digits, variant), variant) & 0xFFFFu).ToString("X4");
        }

        /// <summary>Rough wall clock for guessing every combination, at four seconds a try.</summary>
        private static string Hours(long combinations)
        {
            double hours = combinations * 4.0 / 3600.0;
            if (hours < 1)
                return MeshProtectL10n.Tr("password.weak.minutes",
                                          (combinations * 4.0 / 60.0).ToString("0.#"));
            if (hours < 48)
                return MeshProtectL10n.Tr("password.weak.hours", hours.ToString("0.#"));
            return MeshProtectL10n.Tr("password.weak.days", (hours / 24.0).ToString("0.#"));
        }

        // ------------------------------------------------------------------ vertex identity

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float f;
            [FieldOffset(0)] public uint u;
        }

        public static uint AsUInt(float value)
        {
            var bits = new FloatBits { f = value };
            return bits.u;
        }

        /// <summary>
        /// Vertex identity from the raw bits of UV0. Must match the generated lilMPVertexId().
        ///
        /// Vertices that share a UV0 also share an identity and therefore a displacement. That is
        /// harmless for the restore - it stays exact - and the correlation it leaks is negligible.
        /// </summary>
        public static uint VertexId(Vector2 uv0, MeshProtectVariant variant)
        {
            unchecked
            {
                uint x = AsUInt(variant.idSwapChannels ? uv0.y : uv0.x);
                uint y = AsUInt(variant.idSwapChannels ? uv0.x : uv0.y);
                uint h = x * unchecked((uint)variant.idMulX);
                h ^= (y * unchecked((uint)variant.idMulY)) + unchecked((uint)variant.idAdd);
                h ^= h >> variant.idShift;
                return h;
            }
        }

        // ------------------------------------------------------------------ the hash itself

        /// <summary>
        /// Interpret the variant's hash program. Must match the generated lilMPHash() exactly; the
        /// shader emitter unrolls this same op list into straight-line HLSL.
        /// </summary>
        public static uint Hash(uint vertexId, uint key, MeshProtectVariant variant)
        {
            unchecked
            {
                uint h = vertexId;
                var ops = variant.ops;
                for (int i = 0; i < ops.Length; i++)
                {
                    var op = ops[i];
                    uint c = op.Constant;
                    switch (op.kind)
                    {
                        case MeshProtectHashOp.XorConst:      h ^= c; break;
                        case MeshProtectHashOp.AddConst:      h += c; break;
                        case MeshProtectHashOp.MulConst:      h *= c; break;
                        case MeshProtectHashOp.XorShiftRight: h ^= h >> op.shift; break;
                        case MeshProtectHashOp.XorShiftLeft:  h ^= h << op.shift; break;
                        case MeshProtectHashOp.XorKeyMul:     h ^= key * c; break;
                        case MeshProtectHashOp.AddKeyXor:     h += key ^ c; break;
                        case MeshProtectHashOp.Rotate:
                            h = (h << op.shift) | (h >> (32 - op.shift));
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Unknown hash op kind {op.kind}. The variant was made by a newer version.");
                    }
                }
                return h;
            }
        }

        /// <summary>
        /// The two displacement coefficients, uniform in [-1, 1). Must match lilMPCoefficients().
        ///
        /// The distribution is a square rather than a disc on purpose: normalising to a disc needs
        /// a sqrt, and sqrt is only specified to 1 ULP on D3D11, which would break bit-exactness
        /// for the sake of a distribution difference no attacker can use. A vertex whose pair
        /// happens to land near zero barely moves, but nothing identifies which vertices those are.
        /// </summary>
        public static void Coefficients(uint hash, MeshProtectVariant variant, out float a, out float b)
        {
            uint h = unchecked(hash ^ (uint)variant.coefficientXor);
            int lo = (int)(h & 0xFFFFu) - 32768;
            int hi = (int)((h >> 16) & 0xFFFFu) - 32768;
            if (variant.swapCoefficients) { a = hi * CoefficientScale; b = lo * CoefficientScale; }
            else                          { a = lo * CoefficientScale; b = hi * CoefficientScale; }
        }

        /// <summary>
        /// The value the shader checks the password against. Must match the generated lilMPMac().
        ///
        /// Just the variant's own hash run over the key with a per-variant salt in place of a
        /// vertex identity - no second algorithm to keep in sync, and it inherits the polymorphism
        /// for free. 32 bits: across the 262143 wrong passwords the chance any one of them also
        /// passes is about 6e-5.
        /// </summary>
        public static uint Mac(uint key, MeshProtectVariant variant)
        {
            return Hash(unchecked((uint)variant.macSalt), key, variant);
        }

        /// <summary>Split a 32-bit value into 16-bit halves a float material property carries exactly.</summary>
        public static Vector4 MacToVector(uint mac)
        {
            return new Vector4(mac & 0xFFFFu, mac >> 16, 0f, 0f);
        }

        /// <summary>Convenience: identity, hash and coefficients for one vertex.</summary>
        public static void CoefficientsForVertex(Vector2 uv0, uint key, MeshProtectVariant variant,
                                                 out float a, out float b)
        {
            Coefficients(Hash(VertexId(uv0, variant), key, variant), variant, out a, out b);
        }
    }
}
