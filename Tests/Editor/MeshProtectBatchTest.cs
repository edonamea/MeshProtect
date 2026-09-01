using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using MeshProtect;

namespace MPTest
{
    /// <summary>Batch-mode harness. Run with -executeMethod MPTest.MeshProtectBatchTest.RunAll</summary>
    public static class MeshProtectBatchTest
    {
        private const string ScratchFolder = "Assets/_MPTestScratch";

        private static readonly StringBuilder Log = new StringBuilder();
        private static int failures;

        private static void Say(string line)
        {
            Log.AppendLine(line);
            Debug.Log("[MPTEST] " + line);
        }

        private static void Check(bool condition, string label, string detail)
        {
            if (!condition) failures++;
            Say($"{(condition ? "PASS" : "FAIL")}  {label}  {detail}");
        }

        /// <summary>
        /// Entry point for a Unity launch of its own: run, then end the process with the result.
        /// The work is in RunAllCore so that a single launch can run every suite - each Exit here is an
        /// editor start, an asset import and a script compile, and four of those is most of the
        /// time a change costs to verify.
        /// </summary>
        public static void RunAll() => EditorApplication.Exit(RunAllCore());

        /// <summary>Runs the suite and returns the exit code, without ending the process.</summary>
        public static int RunAllCore()
        {
            int exitCode = 0;
            try
            {
                Say("=== Mesh Protect batch verification ===");
                Say($"Unity {Application.unityVersion}  GPU: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})");

                if (AssetDatabase.IsValidFolder(ScratchFolder))
                {
                    AssetDatabase.DeleteAsset(ScratchFolder);
                    AssetDatabase.Refresh();
                }

                TestGeneratorGate();
                TestCipherDeterminism();
                TestVariantsDiffer();
                TestWriteDefaultsDetection();

                var settingsA = NewSettings(MeshProtectRoot.DisplacementMode.TangentSpace, 42);
                var settingsB = NewSettings(MeshProtectRoot.DisplacementMode.TangentSpace, 4242);
                try
                {
                    GenerateShaders(settingsA, "A");
                    GenerateShaders(settingsB, "B");

                    TestShaderFamilyExists(settingsA);
                    TestShaderFamilyExists(settingsB);
                    TestFamiliesCoexist(settingsA, settingsB);

                    TestGpu(settingsA, "A");
                    TestGpu(settingsB, "B");

                    TestBakeRoundTrip(settingsA, MeshProtectRoot.DisplacementMode.TangentSpace);
                    TestBakeRoundTrip(settingsA, MeshProtectRoot.DisplacementMode.Normal);
                    TestWrongPasswordDestroys(settingsA);
                    TestNoStoredCoefficients(settingsA);
                    TestCrossVariantIsUseless(settingsA, settingsB);
                    TestMixedSubMeshes(settingsA);
                    TestCustomPassword(settingsA);
                    TestNewProtectionKeepsPassword();
                    TestOneClickFromTheInspector();
                    TestHalfOfTheHashIsMeasured();
                    TestSharedEmittersArePinned();
                TestTessDenyListIsQualified();
                    TestTessellationEmitterIsPinned();
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(settingsA.gameObject);
                    UnityEngine.Object.DestroyImmediate(settingsB.gameObject);
                }

                Say(failures == 0 ? "=== ALL PASSED ===" : $"=== {failures} FAILURE(S) ===");
                exitCode = failures == 0 ? 0 : 1;
            }
            catch (Exception e)
            {
                Say("EXCEPTION: " + e);
                exitCode = 2;
            }

            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "mptest-result.txt"),
                              Log.ToString());
            return exitCode;
        }

        /// <summary>
        /// The three emitters a MERGED HOST FAMILY embeds, pinned by hash. A graft embeds only
        /// one of them, EmitDecodeHlsl; MeshProtectForeignShader emits its own properties and its
        /// own header, and NEITHER of those is pinned by anything - a change to them can alter
        /// what a graft compiles while this test stays green. They are not folded in here on
        /// purpose: this pin exists to ask one question about one shared text, and a hash covering
        /// family-only and graft-only emitters could not be answered with a single decision.
        ///
        /// SharedSignatureFormat is frozen apart from GeneratorFormat so a family-only change
        /// cannot tear down every prepared graft and merged host - which would ship those
        /// sub-meshes readable on the first upload after an update. The price of that freeze is
        /// that nothing invalidates them automatically any more: an edit to one of these emitters
        /// leaves graft.txt and host.txt matching, and every graft stays frozen on the old text
        /// while the pipeline writes material values for the new one. That is not an unprotected
        /// sub-mesh; it is one decoded with the wrong program, and it stays that way until
        /// somebody presses Rebuild Shader.
        ///
        /// So this test is the ask, and the only loud thing in the arrangement. It fails on any
        /// change to the emitted text and the answer is one question: does the change alter what
        /// a GRAFT compiles?
        ///   yes - bump SharedSignatureFormat, then update the hash.
        ///   no  - the new text is gated on something only this package's own containers define,
        ///         the way format 3's vertTess rename is - update the hash alone.
        /// It fails at test time and never during a build, so it cannot block anybody's upload.
        ///
        /// The variant is seeded, so a change to the variant GENERATOR moves the hash too. That
        /// is not a false alarm: the signature is computed from the variant, so its inputs
        /// changing is worth one look before the number is replaced.
        /// </summary>
        private static void TestSharedEmittersArePinned()
        {
            const string Expected = "873BBAE441A3D3B225B8B0B01DDEE69EB956D4A0C49F6A88ED832239FFAD2011";

            var variant = MeshProtectVariantGenerator.Generate(new System.Random(20260831));
            var text = new StringBuilder();
            foreach (var name in new[] { "EmitDecodeHlsl", "EmitProperties", "EmitCustomHlsl" })
            {
                var method = typeof(MeshProtectShaderGen).GetMethod(
                    name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null)
                {
                    Check(false, "signature/shared-emitters-pinned",
                          $"MeshProtectShaderGen.{name} is gone. A merged host family embeds it; " +
                          "whatever replaced it has to be pinned here, and SharedSignatureFormat " +
                          "may need to move.");
                    return;
                }
                text.Append(name).Append('\n')
                    .Append((string)method.Invoke(null, new object[] { variant })).Append('\n');
            }

            string actual;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                actual = BitConverter.ToString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");

            Check(actual == Expected, "signature/shared-emitters-pinned",
                  actual == Expected
                      ? "the text grafts and merged hosts embed is unchanged"
                      : $"the emitted text CHANGED: {actual} where {Expected} was pinned. If this " +
                        "change alters what a GRAFT or a MERGED HOST compiles, bump " +
                        "SharedSignatureFormat before " +
                        "updating the number - otherwise every prepared graft and merged host " +
                        "keeps its old decode while the pipeline writes values for the new one.");
        }

        /// <summary>
        /// EmitTessHlsl, pinned by its own hash - deliberately NOT folded into the shared pin.
        ///
        /// This text goes only into the family's own custom_insert_post.hlsl. No graft and no
        /// merged host embeds it, so a change here can never invalidate their caches and it must
        /// not be able to drag SharedSignatureFormat with it. Two texts, two questions, two hashes.
        ///
        /// It exists because the suite is blind here and was measured blind: deleting
        /// "input.uv6 = float2(0.0, 0.0);" - the one line that stops the domain shader decoding a
        /// second time - leaves every other check in this suite passing, while the same source
        /// renders the protected surface a metre away from the truth. A text pin cannot prove the
        /// decode is right; only the GPU probes do that, and they are not part of this suite. What
        /// it can do is make an edit to this text impossible to make silently, which is the way
        /// this particular line would actually be lost.
        ///
        /// If it fails: look at what changed, then re-run the tessellation correctness probes
        /// before updating the number. A green suite is not evidence about this emitter.
        /// </summary>
        private static void TestTessellationEmitterIsPinned()
        {
            const string Expected = "482345F4523C3D8B9556387BCEC461014B914C60FD5083090174827BE83CA77D";

            var variant = MeshProtectVariantGenerator.Generate(new System.Random(20260831));
            var method = typeof(MeshProtectShaderGen).GetMethod(
                "EmitTessHlsl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                Check(false, "signature/tess-emitter-pinned",
                      "MeshProtectShaderGen.EmitTessHlsl is gone. It is the whole of tessellation " +
                      "support; whatever replaced it has to be pinned here.");
                return;
            }

            string text = (string)method.Invoke(null, new object[] { variant });
            string actual;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                actual = BitConverter.ToString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");

            bool hasZeroing = text.Contains("input.uv6 = float2(0.0, 0.0);");
            bool hasSentinel = text.Contains("return lilMPVertTessOriginal(input);");
            Check(hasZeroing && hasSentinel, "signature/tess-emitter-keeps-its-two-load-bearing-lines",
                  !hasZeroing
                      ? "the uv6 zeroing is gone - the domain shader will decode a second time and " +
                        "the surface moves, with the right password, and no other check here notices"
                      : !hasSentinel
                          ? "the sentinel return is gone - if lilToon renames vertTess the family " +
                            "would silently revert to the broken decode instead of failing to compile"
                          : "both present");

            Check(actual == Expected, "signature/tess-emitter-pinned",
                  actual == Expected
                      ? "the tessellation override is unchanged"
                      : $"EmitTessHlsl CHANGED: {actual} where {Expected} was pinned. Re-run the " +
                        "tessellation correctness probes before updating this number - nothing " +
                        "else in this suite can tell you whether the new text still decodes.");
        }

        /// <summary>
        /// The deny list's RECORDING half, on a crafted merged folder. Every shipped lilSSRT and
        /// lilSSAO container wires successfully, so on real input the deny path never runs - and
        /// its first version shipped unable to match anything: it recorded container-relative
        /// names ("ltspass_aotess_opaque") while MergedShaderFor compares root-relative ones
        /// ("GTAO/AOTessellation/Opaque"), in exactly the sub-families ALL of lilSSRT's AO
        /// tessellation lives in, and its wrapper hunt matched by EndsWith across folders - so a
        /// sub-family wiring failure would have refused the WIRED root variant and shipped the
        /// broken sub-family one. This pins the fix: a sub-family container that fails the hull
        /// check must be recorded under its qualified name, drag its own wrapper with it, and
        /// leave the identically-named root wrapper alone.
        /// </summary>
        private static void TestTessDenyListIsQualified()
        {
            string folder = Path.Combine(Path.GetTempPath(),
                                         "mp-denytest-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "GTAO"));

                // The governing block PatchNestedFamilies would already have renamed.
                File.WriteAllText(Path.Combine(folder, "GTAO", "lilCustomShaderDatas.lilblock"),
                                  "ShaderName \"FakeFam/GTAO\"\n");

                // A sub-family pass container whose block fails the one SILENT invariant:
                // a hull stage entered through something other than vertTess.
                File.WriteAllText(Path.Combine(folder, "badtess.lilblock"),
                                  "#pragma vertex vertSomethingElse\n#pragma hull hull\n");
                File.WriteAllText(Path.Combine(folder, "GTAO", "post.lilblock"), "// host post\n");
                File.WriteAllText(Path.Combine(folder, "GTAO", "ltspass_aotess_opaque.lilcontainer"),
                                  "Shader \"Hidden/*LIL_SHADER_NAME*/ltspass_aotess_opaque\"\n" +
                                  "{\n    HLSLINCLUDE\n        #include \"custom.hlsl\"\n    ENDHLSL\n" +
                                  "    lilSubShaderInsert \"insert.lilblock\"\n" +
                                  "    lilSubShaderInsertPost \"post.lilblock\"\n" +
                                  "    lilSubShaderBRP \"../badtess.lilblock\"\n}\n");

                // Its wrapper, and a ROOT wrapper whose pass reference carries the same stem -
                // the shape that made EndsWith deny the wrong folder.
                File.WriteAllText(Path.Combine(folder, "GTAO", "lts_aotess.lilcontainer"),
                                  "Shader \"Hidden/*LIL_SHADER_NAME*/AOTessellation/Opaque\"\n" +
                                  "{\n    lilPassShaderName \"Hidden/*LIL_SHADER_NAME*/ltspass_aotess_opaque\"\n}\n");
                File.WriteAllText(Path.Combine(folder, "lts_roottess.lilcontainer"),
                                  "Shader \"Hidden/*LIL_SHADER_NAME*/Tessellation/Opaque\"\n" +
                                  "{\n    lilPassShaderName \"Hidden/*LIL_SHADER_NAME*/ltspass_aotess_opaque\"\n}\n");

                var variant = MeshProtectVariantGenerator.Generate(new System.Random(20260902));
                var patch = typeof(MeshProtectLilHost).GetMethod(
                    "PatchTessellation", BindingFlags.NonPublic | BindingFlags.Static);
                if (patch == null)
                {
                    Check(false, "tessdeny/patcher-exists",
                          "MeshProtectLilHost.PatchTessellation is gone");
                    return;
                }
                patch.Invoke(null, new object[] { variant, folder, "FakeFam" });

                string record = File.ReadAllText(Path.Combine(folder, "tess.txt")).Trim();
                Check(record.StartsWith("tessgen=2;deny=", StringComparison.Ordinal),
                      "tessdeny/failure-is-recorded",
                      "a hull pass entered through vertSomethingElse must land on the deny " +
                      "list, got: " + record);

                string list = record.Contains("deny=")
                    ? record.Substring(record.IndexOf("deny=", StringComparison.Ordinal) + 5) : "";
                var entries = new HashSet<string>(list.Split(','));
                Check(entries.Contains("GTAO/ltspass_aotess_opaque"),
                      "tessdeny/pass-suffix-is-qualified",
                      "recorded: " + record);
                Check(entries.Contains("GTAO/AOTessellation/Opaque"),
                      "tessdeny/wrapper-follows-its-pass",
                      "recorded: " + record);
                Check(!entries.Contains("Tessellation/Opaque") &&
                      !entries.Contains("AOTessellation/Opaque") &&
                      !entries.Contains("ltspass_aotess_opaque"),
                      "tessdeny/no-cross-folder-or-unqualified-entries",
                      "an unqualified or root entry would refuse the WIRED variant while the " +
                      "broken one sails through, recorded: " + record);
            }
            catch (Exception e)
            {
                Check(false, "tessdeny/no-exception", e.Message);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        // ------------------------------------------------------------------ setup

        private static MeshProtectRoot NewSettings(MeshProtectRoot.DisplacementMode mode, int seed)
        {
            var go = new GameObject("MPTestSettings" + seed);
            var settings = go.AddComponent<MeshProtectRoot>();
            settings.mode = mode;
            settings.distortRatio = 0.04f;
            settings.attenuateAtJoints = false;
            settings.recalculateMissingTangents = true;
            settings.outputFolder = ScratchFolder;

            var rng = new System.Random(seed);
            settings.keyDigits = MeshProtectCipher.GeneratePassword(rng);
            settings.variant = MeshProtectVariantGenerator.Generate(rng);
            return settings;
        }

        private static void GenerateShaders(MeshProtectRoot settings, string label)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool wrote = MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out string folder);
            if (wrote) AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Say($"      variant {label}: {settings.variant.shaderName}  ops={settings.variant.ops.Length}  " +
                $"generated={wrote} in {watch.ElapsedMilliseconds} ms  ({folder})");

            // A second call must be a no-op, otherwise every re-bake would pay the import again.
            bool again = MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out _);
            Check(!again, $"shadergen/{label}/idempotent", "second EnsureGenerated did not rewrite");
        }

        // ------------------------------------------------------------------ generator

        /// <summary>
        /// The generator must not be able to emit a weak program. Feed it many seeds and confirm
        /// every accepted variant passes its own avalanche gate.
        /// </summary>
        /// <summary>
        /// Which Write Defaults setting the added layers copy from the avatar.
        ///
        /// Getting this wrong shipped a release whose avatars could not move their faces in MMD
        /// worlds: those worlds switch off the FX layers holding the expressions so their dance can
        /// drive the blend shapes, and a controller with the two settings mixed does not release
        /// them. Nothing in the console said so, and the avatar was fine everywhere else.
        ///
        /// The sub-state machine case is the one that actually bit. The version this replaces
        /// counted only the states directly under each layer, so an avatar keeping its states one
        /// level down looked like a controller with no states at all and got whatever the fallback
        /// was - which is the same wrong answer, arrived at silently.
        /// </summary>
        private static void TestWriteDefaultsDetection()
        {
            var detect = typeof(MeshProtectAnimator).GetMethod(
                "DetectWriteDefaults", BindingFlags.NonPublic | BindingFlags.Static);
            if (detect == null)
            {
                Check(false, "wd/method-exists", "MeshProtectAnimator.DetectWriteDefaults is gone");
                return;
            }

            Func<UnityEditor.Animations.AnimatorController, bool> run =
                c => (bool)detect.Invoke(null, new object[] { c });

            // (states written directly on the layer, states written one level down, expected)
            var cases = new[]
            {
                new { name = "all-on",            on = 4, off = 0, nestedOn = 0, nestedOff = 0, want = true },
                new { name = "all-off",           on = 0, off = 4, nestedOn = 0, nestedOff = 0, want = false },
                new { name = "mostly-off",        on = 2, off = 5, nestedOn = 0, nestedOff = 0, want = false },
                new { name = "mostly-on",         on = 5, off = 2, nestedOn = 0, nestedOff = 0, want = true },
                new { name = "no-states-at-all",  on = 0, off = 0, nestedOn = 0, nestedOff = 0, want = true },
                new { name = "nested-all-off",    on = 0, off = 0, nestedOn = 0, nestedOff = 4, want = false },
                new { name = "nested-all-on",     on = 0, off = 0, nestedOn = 4, nestedOff = 0, want = true },
                new { name = "nested-outvotes",   on = 1, off = 0, nestedOn = 0, nestedOff = 6, want = false },
            };

            foreach (var c in cases)
            {
                var controller = new UnityEditor.Animations.AnimatorController();
                controller.AddLayer("layer");
                var machine = controller.layers[0].stateMachine;

                for (int i = 0; i < c.on; i++) machine.AddState("on" + i).writeDefaultValues = true;
                for (int i = 0; i < c.off; i++) machine.AddState("off" + i).writeDefaultValues = false;

                if (c.nestedOn + c.nestedOff > 0)
                {
                    var sub = machine.AddStateMachine("sub");
                    for (int i = 0; i < c.nestedOn; i++) sub.AddState("non" + i).writeDefaultValues = true;
                    for (int i = 0; i < c.nestedOff; i++) sub.AddState("noff" + i).writeDefaultValues = false;
                }

                bool got = run(controller);
                Check(got == c.want, "wd/" + c.name,
                      $"{c.on} on + {c.off} off directly, {c.nestedOn} on + {c.nestedOff} off nested " +
                      $"-> {(got ? "ON" : "OFF")}, expected {(c.want ? "ON" : "OFF")}");

                UnityEngine.Object.DestroyImmediate(controller);
            }
        }

        private static void TestGeneratorGate()
        {
            var rng = new System.Random(11);
            int checkedCount = 0;
            string worst = "-";
            bool allGood = true;

            for (int i = 0; i < 24; i++)
            {
                var variant = MeshProtectVariantGenerator.Generate(rng);
                bool ok = MeshProtectVariantGenerator.Measure(variant, rng, out string report);
                if (!ok) { allGood = false; worst = report; }
                checkedCount++;
            }

            Check(allGood, "generator/every-variant-avalanches",
                  $"{checkedCount} variants generated and re-measured" + (allGood ? "" : "; worst: " + worst));
        }

        private static void TestCipherDeterminism()
        {
            var rng = new System.Random(1);
            var digits = MeshProtectCipher.GeneratePassword(rng);
            var variant = MeshProtectVariantGenerator.Generate(rng);
            uint key = MeshProtectCipher.PackDigits(digits, variant);

            var uv = new Vector2(0.3141592f, 0.2718281f);
            MeshProtectCipher.CoefficientsForVertex(uv, key, variant, out float a1, out float b1);
            MeshProtectCipher.CoefficientsForVertex(uv, key, variant, out float a2, out float b2);

            Check(a1 == a2 && b1 == b2, "cipher/determinism", $"a={a1:R} b={b1:R}");
            Check(a1 >= -1f && a1 < 1f && b1 >= -1f && b1 < 1f, "cipher/range", $"a={a1:F5} b={b1:F5}");

            // Every digit must stay inside the menu's eight choices, and the generator should
            // actually reach all of them - a range bug here would quietly shrink the key space.
            var seenDigits = new HashSet<int>();
            bool inRange = true;
            for (int i = 0; i < 20000; i++)
                foreach (int d in MeshProtectCipher.GeneratePassword(rng))
                {
                    seenDigits.Add(d);
                    inRange &= d >= MeshProtectRoot.MinDigit && d <= MeshProtectRoot.MaxDigit;
                }
            Check(inRange && seenDigits.Count == MeshProtectRoot.MaxDigit - MeshProtectRoot.MinDigit + 1,
                  "cipher/digit-range",
                  $"20000 draws covered {seenDigits.Count} distinct digits, all in range: {inRange}");
        }

        /// <summary>Two variants must not agree on anything an attacker could carry between avatars.</summary>
        private static void TestVariantsDiffer()
        {
            var a = MeshProtectVariantGenerator.Generate(new System.Random(101));
            var b = MeshProtectVariantGenerator.Generate(new System.Random(202));

            Check(a.shaderName != b.shaderName, "polymorphism/shader-name",
                  $"{a.shaderName} vs {b.shaderName}");

            bool sameOps = a.ops.Length == b.ops.Length;
            for (int i = 0; sameOps && i < a.ops.Length; i++)
                sameOps = a.ops[i].kind == b.ops[i].kind && a.ops[i].constant == b.ops[i].constant;
            Check(!sameOps, "polymorphism/hash-program", $"{a.ops.Length} vs {b.ops.Length} ops");

            var shared = new HashSet<string>(a.digitProperties);
            shared.IntersectWith(b.digitProperties);
            Check(shared.Count == 0, "polymorphism/property-names",
                  $"{a.digitProperties[0]} vs {b.digitProperties[0]}");
            Check(a.parameterNames[0] != b.parameterNames[0], "polymorphism/parameter-names",
                  $"{a.parameterNames[0]} vs {b.parameterNames[0]}");

            // Nothing generated may follow a shape a scanner can match across avatars: no fixed
            // prefix, no index tying a name to a password position, no word describing a role.
            var everyName = new List<string>();
            foreach (var v in new[] { a, b })
            {
                everyName.Add(v.shaderName);
                everyName.Add(v.bypassProperty);
                everyName.Add(v.modeProperty);
                everyName.Add(v.macProperty);
                everyName.AddRange(v.digitProperties);
                everyName.AddRange(v.parameterNames);
                everyName.AddRange(v.bitNames);
                everyName.AddRange(v.packLayerNames);
                everyName.AddRange(v.decodeLayerNames);
                everyName.Add(v.rootMenuAssetName);
                everyName.Add(v.controllerAssetName);
            }

            var indexed = everyName.Where(n => n.Any(char.IsDigit)).ToList();
            Check(indexed.Count == 0, "polymorphism/no-indices",
                  indexed.Count == 0 ? $"none of {everyName.Count} generated names carries a digit"
                                     : "indexed: " + string.Join(", ", indexed.Take(4)));

            var collide = everyName.GroupBy(n => n.TrimStart('_')).Where(g => g.Count() > 1)
                                   .Select(g => g.Key).ToList();
            Check(collide.Count == 0, "polymorphism/all-names-distinct",
                  collide.Count == 0 ? $"{everyName.Count} names, all different"
                                     : "repeated: " + string.Join(", ", collide.Take(4)));

            // The old scheme was "MP" + eight hex characters.
            var oldShape = new System.Text.RegularExpressions.Regex("^_?(MP|z)[0-9a-f]{7,8}$");
            var matching = everyName.Where(n => oldShape.IsMatch(n)).ToList();
            Check(matching.Count == 0, "polymorphism/no-fixed-shape",
                  matching.Count == 0 ? "no name matches the old MP<hex> / _z<hex> pattern"
                                      : string.Join(", ", matching.Take(4)));
        }

        private static void TestShaderFamilyExists(MeshProtectRoot settings)
        {
            string n = settings.variant.shaderName;
            var missing = new List<string>();
            foreach (var probe in new[]
                     {
                         n + "/lilToon",
                         "Hidden/" + n + "/Cutout",
                         "Hidden/" + n + "/Transparent",
                         n + "/[Optional] FakeShadow",
                         MeshProtectShaderGen.ProbeShaderName(settings.variant, false),
                         MeshProtectShaderGen.ProbeShaderName(settings.variant, true)
                     })
            {
                if (Shader.Find(probe) == null) missing.Add(probe);
            }
            Check(missing.Count == 0, $"shadergen/{n}/compiles",
                  missing.Count == 0 ? "all probed shaders found" : "missing: " + string.Join(", ", missing));

            // Six names is not the family. ReplaceToCustomShaders looks up 52 of them, and any one
            // returning null lands a lilToon material on a shader that is not ours - which used to
            // be a warning and an unprotected sub-mesh. Counting rather than restating the list
            // keeps this from drifting the moment lilToon adds a variant.
            var probeNames = new HashSet<string>
            {
                MeshProtectShaderGen.ProbeShaderName(settings.variant, false),
                MeshProtectShaderGen.ProbeShaderName(settings.variant, true)
            };
            int family = ShaderUtil.GetAllShaderInfo()
                .Select(info => info.name)
                .Where(name => !probeNames.Contains(name))
                .Count(name => name.StartsWith(n + "/", StringComparison.Ordinal) ||
                               name.StartsWith("Hidden/" + n + "/", StringComparison.Ordinal));
            Check(family >= 52, $"shadergen/{n}/family-complete",
                  family >= 52
                      ? $"{family} shaders in the family, covering the 52 ReplaceToCustomShaders looks up"
                      : $"only {family} shaders compiled; ReplaceToCustomShaders looks up 52, and a " +
                        "missing one leaves a material on a shader that is not ours");

            // Every slot the converter will actually reach for, by asking the converter.
            //
            // The count above is a proxy and it has a hole exactly the shape of the bug that was
            // reported: a family can hold 61 shaders and still be missing the one a particular
            // material needs, and counting cannot tell. ReplaceToCustomShaders does one
            // Shader.Find per lilToon variant and stores each in a field; a null field is a
            // material that will be left on stock lilToon. So run it and look at the fields -
            // which also means this never has to restate the list, and cannot drift when lilToon
            // adds a variant.
            var inspectorType = typeof(MeshProtectInspector);
            var converter = Activator.CreateInstance(inspectorType);
            inspectorType.GetField("family", BindingFlags.Instance | BindingFlags.NonPublic |
                                             BindingFlags.Public)
                         .SetValue(converter, n);
            inspectorType.GetMethod("ReplaceToCustomShaders",
                                    BindingFlags.Instance | BindingFlags.NonPublic |
                                    BindingFlags.Public)
                         .Invoke(converter, null);

            var emptySlots = new List<string>();
            for (var type = inspectorType; type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static |
                                                     BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.FieldType != typeof(Shader)) continue;
                    // Unity's == overload, not C# null: a destroyed shader is not a found one.
                    var found = field.GetValue(field.IsStatic ? null : converter) as Shader;
                    if (found == null) emptySlots.Add(field.Name);
                }
            }

            Check(emptySlots.Count == 0, $"shadergen/{n}/every-slot-filled",
                  emptySlots.Count == 0
                      ? "every shader the converter looks up exists in this family"
                      : $"{emptySlots.Count} slot(s) empty: " +
                        string.Join(", ", emptySlots.Distinct().Take(8)) +
                        " - a material needing one of these is left on stock lilToon");

            // A family that has lost a shader has to be rebuilt, and this is the one that was
            // missing. "Up to date" used to mean nothing but "the marker on disk matches this
            // variant's signature" - and a variant is rolled once and then never changes, so the
            // answer was yes forever. A family written by an older release of this tool, or one
            // whose containers failed to compile, or one somebody deleted a file out of, stayed
            // exactly as it was; Rebuild Shader said "already up to date" and the build stopped on
            // a lilToon material that had nowhere to go. Deleting a container here is the same
            // shape as all three.
            string folder = MeshProtectShaderGen.FolderFor(settings, settings.variant);
            var containers = Directory.GetFiles(folder, "*.lilcontainer")
                                      .OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (containers.Count == 0)
            {
                Check(false, $"shadergen/{n}/incomplete-family-is-rebuilt",
                      "no .lilcontainer files in " + folder);
                return;
            }

            string victim = containers[0].Replace('\\', '/');
            victim = victim.Substring(victim.IndexOf("Assets/", StringComparison.Ordinal));
            AssetDatabase.DeleteAsset(victim);
            AssetDatabase.Refresh();

            bool rebuilt = MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out _);
            int after = ShaderUtil.GetAllShaderInfo()
                .Select(info => info.name)
                .Where(name => !probeNames.Contains(name))
                .Count(name => name.StartsWith(n + "/", StringComparison.Ordinal) ||
                               name.StartsWith("Hidden/" + n + "/", StringComparison.Ordinal));

            Check(rebuilt && after >= family, $"shadergen/{n}/incomplete-family-is-rebuilt",
                  rebuilt && after >= family
                      ? $"a family missing one shader was rebuilt and came back to {after}"
                      : rebuilt
                          ? $"it rebuilt but only {after} shaders came back, was {family}"
                          : "EnsureGenerated reported the family as up to date while a shader was " +
                            "missing from it - the marker matched, so nothing looked at the family");
        }

        private static void TestFamiliesCoexist(MeshProtectRoot a, MeshProtectRoot b)
        {
            var sa = Shader.Find(a.variant.shaderName + "/lilToon");
            var sb = Shader.Find(b.variant.shaderName + "/lilToon");
            Check(sa != null && sb != null && sa != sb, "shadergen/two-families-coexist",
                  $"{(sa == null ? "null" : sa.name)} / {(sb == null ? "null" : sb.name)}");
        }

        // ------------------------------------------------------------------ GPU

        /// <summary>
        /// The check that matters most now that the shader is generated: does the emitted HLSL
        /// compute exactly what the C# interpreter of the same op list computes?
        /// </summary>
        private static void TestGpu(MeshProtectRoot settings, string label)
        {
            var result = MeshProtectGpuCheck.Run(settings.variant);
            if (!result.ran)
            {
                Check(false, $"gpu/{label}/generated-hlsl-vs-csharp", "SKIPPED: " + result.skipReason);
                return;
            }
            Check(result.Passed, $"gpu/{label}/generated-hlsl-vs-csharp",
                  $"worst={result.worstError:E3} tol={result.tolerance:E3} " +
                  $"configs={result.configurations} samples={result.samples}");
        }

        // ------------------------------------------------------------------ bake

        private static Mesh BuildTestMesh(int side)
        {
            var mesh = new Mesh { name = "MPTestGrid" };
            int n = side * side;
            var vertices = new Vector3[n];
            var uv = new Vector2[n];
            var normals = new Vector3[n];

            for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int i = y * side + x;
                float fx = x / (float)(side - 1);
                float fy = y / (float)(side - 1);
                float h = 0.25f * Mathf.Sin(fx * 3.1f) * Mathf.Cos(fy * 2.7f);
                vertices[i] = new Vector3(fx * 2f - 1f, h, fy * 2f - 1f);
                uv[i] = new Vector2(fx, fy);
                normals[i] = new Vector3(fx * 0.2f - 0.1f, 1f, fy * 0.2f - 0.1f).normalized;
            }

            var tris = new List<int>();
            for (int y = 0; y < side - 1; y++)
            for (int x = 0; x < side - 1; x++)
            {
                int i = y * side + x;
                tris.Add(i); tris.Add(i + side); tris.Add(i + 1);
                tris.Add(i + 1); tris.Add(i + side); tris.Add(i + side + 1);
            }

            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.normals = normals;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateTangents();

            // A blend shape carrying delta normals AND delta tangents - the case the compensation
            // exists for. A shape with only position deltas would pass even if it were broken.
            var dv = new Vector3[n];
            var dn = new Vector3[n];
            var dt = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                dv[i] = new Vector3(0f, 0.15f * Mathf.Sin(i * 0.37f), 0.04f * Mathf.Cos(i * 0.11f));
                dn[i] = new Vector3(0.12f * Mathf.Cos(i * 0.23f), 0.05f, 0.09f * Mathf.Sin(i * 0.41f));
                dt[i] = new Vector3(0.07f * Mathf.Sin(i * 0.53f), 0.03f, 0.11f * Mathf.Cos(i * 0.29f));
            }
            mesh.AddBlendShapeFrame("TestShape", 100f, dv, dn, dt);

            mesh.RecalculateBounds();
            return mesh;
        }

        private static void TestBakeRoundTrip(MeshProtectRoot settings, MeshProtectRoot.DisplacementMode mode)
        {
            uint key = MeshProtectCipher.PackDigits(settings.keyDigits, settings.variant);

            var source = BuildTestMesh(48);
            var baked = MeshProtectMesh.Bake(source, settings, mode, key, settings.variant).mesh;

            var check = MeshProtectSelfCheck.Verify(source, baked, mode, key, settings.variant);
            Check(check.Passed, $"bake/{mode}/round-trip",
                  $"vertex={check.worstVertexError:E3} blendshape={check.worstBlendShapeError:E3} " +
                  $"tol={check.tolerance:E3} frames={check.blendShapeFramesChecked}");
            Check(check.blendShapeFramesChecked > 0, $"bake/{mode}/blendshape-exercised",
                  $"{check.blendShapeFramesChecked} frame(s) checked");

            var sv = source.vertices; var bv = baked.vertices;
            double worst = 0, mean = 0;
            for (int i = 0; i < sv.Length; i++)
            {
                double d = (sv[i] - bv[i]).magnitude;
                worst = Math.Max(worst, d); mean += d;
            }
            mean /= sv.Length;
            double diagonal = source.bounds.size.magnitude;
            Check(mean > diagonal * 0.005, $"bake/{mode}/actually-displaced",
                  $"mean={mean:F4}m worst={worst:F4}m diagonal={diagonal:F3}m");

            UnityEngine.Object.DestroyImmediate(source);
            UnityEngine.Object.DestroyImmediate(baked);
        }

        private static void TestWrongPasswordDestroys(MeshProtectRoot settings)
        {
            var variant = settings.variant;
            uint key = MeshProtectCipher.PackDigits(settings.keyDigits, variant);

            var source = BuildTestMesh(48);
            var baked = MeshProtectMesh.Bake(source, settings,
                MeshProtectRoot.DisplacementMode.TangentSpace, key, variant).mesh;

            double worstWrong = double.MaxValue;
            for (int position = 0; position < MeshProtectRoot.PasswordLength; position++)
            {
                var wrong = (int[])settings.keyDigits.Clone();
                wrong[position] = wrong[position] == MeshProtectRoot.MaxDigit
                    ? MeshProtectRoot.MinDigit : wrong[position] + 1;

                var bad = MeshProtectSelfCheck.Verify(source, baked,
                    MeshProtectRoot.DisplacementMode.TangentSpace,
                    MeshProtectCipher.PackDigits(wrong, variant), variant);
                worstWrong = Math.Min(worstWrong, bad.worstVertexError);
            }

            double displacementScale = source.bounds.size.magnitude * settings.distortRatio;
            Check(worstWrong > displacementScale * 0.5, "wrong-password/destroys",
                  $"smallest worst-error across the 6 single-digit errors = {worstWrong:F4}m, " +
                  $"displacement scale = {displacementScale:F4}m");

            UnityEngine.Object.DestroyImmediate(source);
            UnityEngine.Object.DestroyImmediate(baked);
        }

        /// <summary>
        /// A password recovered from one avatar must be worth nothing against another. This is what
        /// the per-variant hash program buys, and it is the difference between one break costing
        /// the attacker one model and costing them every model.
        /// </summary>
        private static void TestCrossVariantIsUseless(MeshProtectRoot a, MeshProtectRoot b)
        {
            var source = BuildTestMesh(32);
            uint keyA = MeshProtectCipher.PackDigits(a.keyDigits, a.variant);
            var baked = MeshProtectMesh.Bake(source, a,
                MeshProtectRoot.DisplacementMode.TangentSpace, keyA, a.variant).mesh;

            // Decode avatar A's mesh with avatar B's program, using A's own correct password.
            uint keyAUnderB = MeshProtectCipher.PackDigits(a.keyDigits, b.variant);
            var wrong = MeshProtectSelfCheck.Verify(source, baked,
                MeshProtectRoot.DisplacementMode.TangentSpace, keyAUnderB, b.variant);

            double displacementScale = source.bounds.size.magnitude * a.distortRatio;
            Check(wrong.worstVertexError > displacementScale * 0.5, "polymorphism/cross-variant-useless",
                  $"decoding A's mesh with B's program leaves {wrong.worstVertexError:F4}m error " +
                  $"(displacement scale {displacementScale:F4}m)");

            UnityEngine.Object.DestroyImmediate(source);
            UnityEngine.Object.DestroyImmediate(baked);
        }

        /// <summary>
        /// One mesh, two sub-meshes, only one of them protected.
        ///
        /// This is everywhere on real avatars - a body with a separate eye or teeth material, an
        /// outfit sharing a mesh with the skin under it - and it is the one path where displacing a
        /// vertex is actively wrong: a vertex shared with an unprotected sub-mesh is also drawn by
        /// a material that has no decode, so moving it would visibly tear the model apart with the
        /// correct password. The baker is supposed to leave every such vertex alone.
        /// </summary>
        /// <summary>
        /// A typed password has to behave exactly like a rolled one, and changing it must not
        /// require a new algorithm - that is the whole reason the two are separate buttons.
        /// </summary>
        private static void TestCustomPassword(MeshProtectRoot settings)
        {
            var bad = new (string text, string why)[]
            {
                ("",         "empty"),
                ("1234567",  "too long"),
                ("123409",   "a zero in the middle"),
                ("1204",     "a zero in the middle of a short one"),
                ("123459",   "contains 9"),
                ("12 456",   "contains a space"),
                ("abcdef",   "not digits"),
            };
            var wronglyAccepted = bad.Where(
                b => MeshProtectCipher.TryParsePassword(b.text, out _, out _)).Select(b => b.why).ToList();
            Check(wronglyAccepted.Count == 0, "password/rejects-invalid",
                  wronglyAccepted.Count == 0
                      ? $"all {bad.Length} malformed inputs rejected with a reason"
                      : "accepted: " + string.Join(", ", wronglyAccepted));

            // Any length from one to six, stored as the digits followed by "not entered". The two
            // that matter are on the last line: a short password must not be openable by the same
            // digits with anything after them, which is what the old packing would have done - it
            // put "not entered" and the digit 1 on the same four bits.
            var lengths = new List<string>();
            for (int length = 1; length <= MeshProtectRoot.PasswordLength; length++)
            {
                string text = "1234567".Substring(0, length);
                if (!MeshProtectCipher.TryParsePassword(text, out var shortDigits, out string why))
                {
                    lengths.Add($"{length} digits rejected: {why}");
                    continue;
                }

                if (MeshProtectRoot.TypedLength(shortDigits) != length)
                    lengths.Add($"'{text}' came back as {MeshProtectRoot.TypedLength(shortDigits)} digits");
                for (int i = length; i < MeshProtectRoot.PasswordLength; i++)
                    if (shortDigits[i] != MeshProtectRoot.NotEntered)
                        lengths.Add($"'{text}' left position {i + 1} at {shortDigits[i]}");
            }
            Check(lengths.Count == 0, "password/any-length-1-to-6",
                  lengths.Count == 0
                      ? $"1 to {MeshProtectRoot.PasswordLength} digits all parse, unused positions stay unentered"
                      : string.Join("; ", lengths));

            // Variable length must not have cost the six digit case anything. The nibble a digit
            // gets is still digit-minus-one, so a six digit password packs bit for bit into the key
            // it always did - the avalanche the generator measured is the same avalanche, and an
            // avatar re-uploaded with the same password is keyed the same as before. Written out
            // rather than calling KeyNibble, so that changing KeyNibble breaks this.
            var unchanged = new List<string>();
            for (int trial = 0; trial < 64; trial++)
            {
                var six = MeshProtectCipher.GeneratePassword(new System.Random(trial));
                uint packed = MeshProtectCipher.PackDigits(six, settings.variant);

                uint asBefore = 0;
                for (int i = 0; i < six.Length; i++)
                    asBefore |= (uint)(six[i] - MeshProtectRoot.MinDigit)
                                << (MeshProtectRoot.BitsPerDigit * settings.variant.digitBitOrder[i]);

                if (packed != asBefore)
                    unchanged.Add($"{string.Join("", six)}: {packed:X} vs {asBefore:X}");
            }
            Check(unchanged.Count == 0, "password/six-digits-key-unchanged",
                  unchanged.Count == 0
                      ? "64 six digit passwords pack exactly as they did before variable length"
                      : "changed: " + string.Join(", ", unchanged));

            MeshProtectCipher.TryParsePassword("1234", out var fourDigits, out _);
            MeshProtectCipher.TryParsePassword("123411", out var padded, out _);
            uint fourKey = MeshProtectCipher.PackDigits(fourDigits, settings.variant);
            uint paddedKey = MeshProtectCipher.PackDigits(padded, settings.variant);
            Check(fourKey != paddedKey, "password/short-is-not-padded-with-ones",
                  fourKey != paddedKey
                      ? $"'1234' and '123411' are different keys ({fourKey:X} vs {paddedKey:X})"
                      : $"'1234' and '123411' pack to the same key {fourKey:X} - a four digit " +
                        "password would be opened by turning the last two dials to 1");

            bool ok = MeshProtectCipher.TryParsePassword(" 471826 ", out var typed, out string err);
            Check(ok && typed != null && string.Join("", typed) == "471826", "password/parses-and-trims",
                  ok ? "\" 471826 \" -> 471826" : "rejected: " + err);

            Check(MeshProtectCipher.DescribeWeakness(new[] { 5, 5, 5, 5, 5, 5 }) != null &&
                  MeshProtectCipher.DescribeWeakness(new[] { 1, 2, 3, 4, 5, 6 }) != null &&
                  MeshProtectCipher.DescribeWeakness(new[] { 4, 7, 1, 8, 2, 6 }) == null,
                  "password/flags-guessable",
                  "all-same and straight runs warn, an ordinary password does not");

            // The important one: the same variant with a different password must produce a
            // different mesh that still decodes. If the shader had to be regenerated for this, the
            // two-button split would be a lie.
            var source = BuildTestMesh(24);
            var original = settings.keyDigits;
            try
            {
                MeshProtectCipher.TryParsePassword("471826", out var a1, out _);
                MeshProtectCipher.TryParsePassword("826471", out var a2, out _);

                settings.keyDigits = a1;
                uint k1 = MeshProtectCipher.PackDigits(a1, settings.variant);
                var m1 = MeshProtectMesh.Bake(source, settings,
                    MeshProtectRoot.DisplacementMode.TangentSpace, k1, settings.variant).mesh;

                settings.keyDigits = a2;
                uint k2 = MeshProtectCipher.PackDigits(a2, settings.variant);
                var m2 = MeshProtectMesh.Bake(source, settings,
                    MeshProtectRoot.DisplacementMode.TangentSpace, k2, settings.variant).mesh;

                var v1 = m1.vertices; var v2 = m2.vertices;
                double apart = 0;
                for (int i = 0; i < v1.Length; i++) apart = Math.Max(apart, (v1[i] - v2[i]).magnitude);
                Check(apart > 0.01, "password/change-changes-the-mesh",
                      $"two passwords on one algorithm differ by {apart:F4} m");

                var c1 = MeshProtectSelfCheck.Verify(source, m1,
                    MeshProtectRoot.DisplacementMode.TangentSpace, k1, settings.variant);
                var c2 = MeshProtectSelfCheck.Verify(source, m2,
                    MeshProtectRoot.DisplacementMode.TangentSpace, k2, settings.variant);
                Check(c1.Passed && c2.Passed, "password/both-decode",
                      $"{c1.worstVertexError:E3} and {c2.worstVertexError:E3}");

                // Cross-decode must fail, or the password would not be doing anything.
                var cross = MeshProtectSelfCheck.Verify(source, m1,
                    MeshProtectRoot.DisplacementMode.TangentSpace, k2, settings.variant);
                Check(cross.worstVertexError > 0.01, "password/wrong-one-fails",
                      $"decoding with the other password leaves {cross.worstVertexError:F4} m");

                UnityEngine.Object.DestroyImmediate(m1);
                UnityEngine.Object.DestroyImmediate(m2);
            }
            finally
            {
                settings.keyDigits = original;
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        /// <summary>
        /// Replacing the algorithm must not replace the author's password.
        ///
        /// It used to. Someone typed their own six digits, pressed "New Protection" to refresh the
        /// shader, and uploaded an avatar keyed to a random password they had never been shown -
        /// the one they had written down did not open it, and nothing in the build said why. The
        /// two are independent: a new variant defeats an attacker who reverse-engineered the old
        /// shader, and that has nothing to do with which six digits the owner chose.
        /// </summary>
        /// <summary>
        /// The only sequence most people will ever run: add the component, press the one button the
        /// panel offers, upload.
        /// </summary>
        /// <summary>
        /// A hash program whose low half is dead must be rejected, and rejected as a HALF problem.
        ///
        /// The two halves are not an arbitrary split: Coefficients() reads the low 16 bits as the
        /// tangent-direction displacement and the high 16 as the normal-direction one, so a half
        /// that stops depending on the password is one displacement axis that stops depending on
        /// it. The gate used to sum all 32 bit positions and divide once, which cannot see that at
        /// all when the halves lean opposite ways - they cancel to exactly one half and pass.
        /// </summary>
        private static void TestHalfOfTheHashIsMeasured()
        {
            var variant = MeshProtectVariantGenerator.Generate(new System.Random(31337));

            // Mix the key across the whole word, then shift everything up by sixteen. The low
            // half is zero from there on and can never flip; the high half carries what the low
            // half held, so it still moves with the key.
            //
            // The fold in the middle is not decoration. Multiplication only carries upward, so a
            // digit living at bit 16 or above cannot reach the low half at all - and the low half
            // is the only thing the final shift keeps. Without the fold those two digits change
            // the hash not at all, the gate rejects on keyChanged instead of on the halves, and
            // both numbers read 0.5 with nothing to tell them apart.
            variant.ops = new[]
            {
                new MeshProtectHashOp { kind = MeshProtectHashOp.XorKeyMul,
                                        constant = unchecked((int)0x9E3779B1) },
                new MeshProtectHashOp { kind = MeshProtectHashOp.XorShiftRight, shift = 16 },
                new MeshProtectHashOp { kind = MeshProtectHashOp.MulConst, constant = 0x10000 },
            };

            bool ok = MeshProtectVariantGenerator.Measure(variant, new System.Random(31338),
                                                          out string report);

            // Four conjuncts turn this down at once - per-bit and per-half, on the key side and
            // the id side - because a dead half is 0.5 out by every one of those measures. So this
            // pins that bias is measured, not which reading catches it; removing any single
            // conjunct leaves the other three, and two mutations aimed that way were recorded as
            // missed while the gate was working exactly as intended. What the per-half reading
            // uniquely catches - halves leaning opposite ways, where no single bit is far out -
            // has no fixture. See the note in Measure.
            Check(!ok, "avalanche/dead-half-is-rejected",
                  !ok ? "a program whose low half never flips does not get through the gate"
                      : "the gate accepted a program where one displacement axis ignores the " +
                        "password: " + report);

            // The discriminating part. Pooled, a dead half reads as a rate near 0.25 - a bias of
            // about 0.25. Per half it reads as 0.5. Anything that still measured one pooled number
            // would report the same value twice.
            double pooled = ReportValue(report, "keyBias");
            double half = ReportValue(report, "keyHalfBias");
            bool separate = half > pooled + 0.1;
            Check(separate, "avalanche/halves-counted-separately",
                  separate
                      ? $"pooled bias {pooled:F3}, worst half {half:F3} - the halves are counted apart"
                      : $"pooled {pooled:F3} and half {half:F3} agree, so the halves are not " +
                        "actually being measured separately");
        }

        /// <summary>Pull one "name=value" out of the gate's report line.</summary>
        private static double ReportValue(string report, string name)
        {
            foreach (var field in (report ?? "").Split(' '))
            {
                if (!field.StartsWith(name + "=", StringComparison.Ordinal)) continue;
                return double.TryParse(field.Substring(name.Length + 1),
                                       System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture,
                                       out double value) ? value : double.NaN;
            }
            return double.NaN;
        }

        private static void TestOneClickFromTheInspector()
        {
            var go = new GameObject("MPGuiTest");
            UnityEditor.Editor panel = null;
            try
            {
                var settings = go.AddComponent<MeshProtectRoot>();
                settings.outputFolder = "Assets/_MPTestScratch";

                Check(settings != null, "gui/component-can-be-added",
                      settings != null
                          ? "Add Component gives a MeshProtectRoot"
                          : "AddComponent returned null - the component is in an assembly Unity " +
                            "will not put on a GameObject");

                // Unity picks the drawer by attribute. Break that binding and the component still
                // works, the panel silently becomes a default field list, and the button that
                // generates the password is simply not there.
                panel = UnityEditor.Editor.CreateEditor(settings);
                Check(panel is MeshProtectRootEditor, "gui/panel-is-wired",
                      panel is MeshProtectRootEditor
                          ? "the inspector for this component is MeshProtectRootEditor"
                          : $"Unity would draw a {(panel == null ? "<null>" : panel.GetType().Name)}");

                Check(!settings.HasKey, "gui/starts-unprotected",
                      !settings.HasKey
                          ? "a new component offers the Generate button"
                          : "a new component claims it is already protected");

                // The handler the button calls, not a reconstruction of it.
                var handler = typeof(MeshProtectRootEditor).GetMethod(
                    "Regenerate", BindingFlags.NonPublic | BindingFlags.Static);
                Check(handler != null, "gui/generate-handler-exists",
                      handler != null
                          ? "MeshProtectRootEditor.Regenerate found"
                          : "the Generate Password handler is not where this test expects it, so " +
                            "nothing below actually exercised the button");
                if (handler == null) return;

                // A throw here is a failure of "one press leaves this uploadable", and letting it
                // escape stops the checks below from running at all - which is how a mutation that
                // emptied the algorithm was recorded as caught without the check that covers it
                // ever being evaluated.
                Exception thrown = null;
                try { handler.Invoke(null, new object[] { settings }); }
                catch (TargetInvocationException e) { thrown = e.InnerException ?? e; }
                catch (Exception e) { thrown = e; }

                Check(thrown == null, "gui/one-click-does-not-throw",
                      thrown == null
                          ? "the button completed"
                          : $"pressing Generate threw {thrown.GetType().Name}: {thrown.Message}");

                Check(settings.HasPassword, "gui/one-click-sets-a-password",
                      settings.HasPassword
                          ? $"password {settings.KeyAsString()}"
                          : "no password after pressing Generate");

                Check(settings.HasKey, "gui/one-click-is-uploadable",
                      settings.HasKey
                          ? $"password and algorithm both ready (id {settings.variant.shaderName})"
                          : "the algorithm is missing or malformed, so the build would refuse");

                // The handler compiles the shader family here so the upload does not have to.
                // Without it the build stops with "this avatar's shader family is missing", long
                // after the only click that could have prevented it.
                bool shaderReady = settings.variant != null &&
                                   Shader.Find(settings.variant.shaderName + "/lilToon") != null;
                Check(shaderReady, "gui/one-click-builds-the-shader",
                      shaderReady
                          ? $"{settings.variant.shaderName}/lilToon is in the project"
                          : "the shader family was not generated, so the upload would refuse");
            }
            finally
            {
                if (panel != null) UnityEngine.Object.DestroyImmediate(panel);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static void TestNewProtectionKeepsPassword()
        {
            var go = new GameObject("MPPasswordTest");
            try
            {
                var settings = go.AddComponent<MeshProtectRoot>();
                var rng = new System.Random(1234);
                settings.variant = MeshProtectVariantGenerator.Generate(rng);
                settings.keyDigits = new[] { 1, 2, 3, 4, 5, 6 };

                string before = settings.KeyAsString();
                string variantBefore = settings.variant.shaderName;

                MeshProtectRootEditor.ReplaceProtection(settings, new System.Random(9876));

                Check(settings.KeyAsString() == before, "password/survives-new-protection",
                      settings.KeyAsString() == before
                          ? $"still {before} after the algorithm was replaced"
                          : $"was {before}, became {settings.KeyAsString()} - the author's password " +
                            "was silently thrown away");

                Check(settings.variant.shaderName != variantBefore, "password/variant-did-change",
                      settings.variant.shaderName != variantBefore
                          ? $"algorithm went from {variantBefore} to {settings.variant.shaderName}"
                          : "the variant did not change, so the button did nothing");

                // ...and an avatar that has no password yet must still get one.
                var fresh = new GameObject("MPPasswordTestFresh").AddComponent<MeshProtectRoot>();
                try
                {
                    fresh.keyDigits = null;
                    MeshProtectRootEditor.ReplaceProtection(fresh, new System.Random(4321));
                    Check(fresh.HasPassword, "password/generated-when-absent",
                          fresh.HasPassword
                              ? $"a fresh component got {fresh.KeyAsString()}"
                              : "no password was generated for a component that had none");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(fresh.gameObject);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static void TestMixedSubMeshes(MeshProtectRoot settings)
        {
            var variant = settings.variant;
            uint key = MeshProtectCipher.PackDigits(settings.keyDigits, variant);

            // Six vertices in a strip. Sub-mesh 0 uses 0-3, sub-mesh 1 uses 2-5, so 2 and 3 are
            // shared, 0 and 1 belong only to the protected half, 4 and 5 only to the excluded one.
            var mesh = new Mesh { name = "MixedStrip" };
            var verts = new Vector3[6];
            var uv = new Vector2[6];
            var normals = new Vector3[6];
            for (int i = 0; i < 6; i++)
            {
                verts[i] = new Vector3(i * 0.3f, Mathf.Sin(i) * 0.1f, (i % 2) * 0.4f);
                uv[i] = new Vector2(i / 5f, (i % 2) * 0.5f);
                normals[i] = new Vector3(0.1f * i - 0.25f, 1f, 0.05f).normalized;
            }
            mesh.vertices = verts;
            mesh.uv = uv;
            mesh.normals = normals;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2, 1, 3, 2 }, 0);
            mesh.SetTriangles(new[] { 2, 3, 4, 3, 5, 4 }, 1);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            // Slot 1 is excluded, which is what BuildSkipMask keys off.
            var skip = new bool[6];
            foreach (int i in mesh.GetIndices(1)) skip[i] = true;

            var baked = MeshProtectMesh.Bake(mesh, settings,
                MeshProtectRoot.DisplacementMode.TangentSpace, key, variant, skip).mesh;

            var uv6 = new List<Vector2>(); baked.GetUVs(6, uv6);
            var before = mesh.vertices; var after = baked.vertices;

            var protectedOnly = new[] { 0, 1 };
            var untouchable = new[] { 2, 3, 4, 5 };   // shared, plus excluded-only

            bool movedRight = protectedOnly.All(i => (before[i] - after[i]).magnitude > 1e-4f
                                                     && uv6[i].x > 0f);
            var leaked = untouchable.Where(i => (before[i] - after[i]).magnitude > 1e-9f
                                                || uv6[i].x != 0f).ToList();

            Check(movedRight, "submesh/protected-half-displaced",
                  $"vertices {string.Join(",", protectedOnly)} moved and carry an amplitude");
            Check(leaked.Count == 0, "submesh/excluded-and-shared-untouched",
                  leaked.Count == 0
                      ? "vertices 2,3 (shared) and 4,5 (excluded) are byte-identical with zero amplitude"
                      : "moved or given amplitude: " + string.Join(",", leaked));

            var check = MeshProtectSelfCheck.Verify(mesh, baked,
                MeshProtectRoot.DisplacementMode.TangentSpace, key, variant);
            Check(check.Passed, "submesh/decodes",
                  $"worst restore error {check.worstVertexError:E3} (tol {check.tolerance:E3})");

            UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate(baked);
        }

        private static void TestNoStoredCoefficients(MeshProtectRoot settings)
        {
            var source = BuildTestMesh(32);
            var baked = MeshProtectMesh.Bake(source, settings,
                MeshProtectRoot.DisplacementMode.TangentSpace,
                MeshProtectCipher.PackDigits(settings.keyDigits, settings.variant),
                settings.variant).mesh;

            var uv6 = new List<Vector2>(); baked.GetUVs(6, uv6);
            var uv7 = new List<Vector2>(); baked.GetUVs(7, uv7);

            Check(uv7.Count == 0, "layout/uv7-free", $"uv7 entries = {uv7.Count}");
            Check(uv6.Count == baked.vertexCount, "layout/uv6-amplitude-present",
                  $"uv6 entries = {uv6.Count} / {baked.vertexCount}");

            bool yAllZero = true, xAllEqual = true;
            float first = uv6.Count > 0 ? uv6[0].x : 0f;
            foreach (var v in uv6)
            {
                if (v.y != 0f) yAllZero = false;
                if (Mathf.Abs(v.x - first) > 1e-9f) xAllEqual = false;
            }
            Check(yAllZero, "layout/uv6-y-reserved", "uv6.y is all zero");
            Check(xAllEqual, "layout/uv6-carries-no-detail",
                  $"all amplitudes == {first:F6} (attenuation off)");

            var su = source.uv; var bu = baked.uv;
            bool uvIdentical = su.Length == bu.Length;
            for (int i = 0; uvIdentical && i < su.Length; i++)
                uvIdentical = MeshProtectCipher.AsUInt(su[i].x) == MeshProtectCipher.AsUInt(bu[i].x)
                           && MeshProtectCipher.AsUInt(su[i].y) == MeshProtectCipher.AsUInt(bu[i].y);
            Check(uvIdentical, "layout/uv0-bit-identical", "identity source survives the bake");

            UnityEngine.Object.DestroyImmediate(source);
            UnityEngine.Object.DestroyImmediate(baked);
        }
    }
}
