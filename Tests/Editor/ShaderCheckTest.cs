#if UNITY_EDITOR && LILMP_VRCSDK3_AVATARS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using MeshProtect;

namespace MPTest
{
    /// <summary>
    /// The shader check: the measurement every conversion passes through before a material is
    /// allowed onto a shader that claims to carry the decode.
    ///
    /// A check like this has two ways to be worse than nothing. It can pass what is broken, and
    /// then it is a line in a report that means nothing. Or it can refuse what works, and then
    /// an update of this tool quietly takes protection away from avatars that had it. So both
    /// directions are tested, and each from two sides:
    ///
    ///   - on shaders written here for the purpose, where exactly one thing is wrong at a time
    ///     - a pass with no decode, a pass that decodes twice, a pass that still draws while
    ///     locked, a variant that only a headset compiles - and the answer has to name it;
    ///   - on the real thing: stock lilToon onto a generated family has to pass, and so does
    ///     lilSSRT where it is installed, because those are the avatars people already have.
    ///
    /// Then the whole road once: a family broken the way lilSSRT 1.4 broke one, after the last
    /// Rebuild Shader, with nothing on record to say so. The upload has to notice by itself,
    /// leave that material alone, protect the rest, and say what it did.
    /// </summary>
    public static class ShaderCheckTest
    {
        private static readonly StringBuilder Log = new StringBuilder();
        private static int failures;

        private const string Scratch = "Assets/_MPCheckScratch";
        private const string MadeUp = Scratch + "/MadeUp";

        private static void Say(string line)
        {
            Log.AppendLine(line);
            Debug.Log("[CHECK] " + line);
        }

        private static void Check(bool ok, string label, string detail)
        {
            if (!ok) failures++;
            Say($"{(ok ? "PASS" : "FAIL")}  {label}  {detail}");
        }

        public static void Run() => EditorApplication.Exit(RunCore());

        public static int RunCore()
        {
            int exit = 0;
            failures = 0;
            Log.Length = 0;

            GameObject owner = null;
            try
            {
                Say("=== Shader check ===");

                var lilToon = Shader.Find("lilToon");
                if (lilToon == null) throw new Exception("lilToon shader not found");

                if (AssetDatabase.IsValidFolder(Scratch)) AssetDatabase.DeleteAsset(Scratch);
                AssetDatabase.CreateFolder("Assets", "_MPCheckScratch");

                owner = new GameObject("MPShaderCheck");
                var settings = owner.AddComponent<MeshProtectRoot>();
                settings.outputFolder = Scratch;
                settings.keyDigits = new[] { 4, 8, 1, 2, 7, 3 };
                settings.variant = MeshProtectVariantGenerator.Generate(new System.Random(31415));

                // An answer on record would turn every case below into a look-up.
                MeshProtectShaderCheck.Forget();

                OnMadeUpShaders(settings);
                OnRealShaders(settings, lilToon);
                InAnUpload(settings, lilToon);

                Say(failures == 0 ? "=== ALL PASSED ===" : $"=== {failures} FAILURE(S) ===");
                exit = failures == 0 ? 0 : 1;
            }
            catch (Exception e)
            {
                Say("EXCEPTION: " + e);
                exit = 2;
            }
            finally
            {
                if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
                MeshProtectShaderCheck.Forget();
                MeshProtectLilHost.Forget();
                foreach (var f in new[] { Scratch, MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(f)) AssetDatabase.DeleteAsset(f);
                AssetDatabase.Refresh();
            }

            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "shadercheck-result.txt"),
                              Log.ToString());
            return exit;
        }

        private static MeshProtectShaderCheck.Verdict Ask(Material source, Material converted,
                                                          MeshProtectRoot settings)
            => MeshProtectShaderCheck.Measure(source, converted, settings, settings.variant,
                   new Dictionary<string, MeshProtectShaderCheck.Verdict>());

        private static string Said(MeshProtectShaderCheck.Verdict verdict)
            => verdict.Failed ? "refused: " + verdict.failure
             : verdict.caution != null ? "passed with a note: " + verdict.caution
             : "passed" + (verdict.compiled ? "" : " (compiling was on record)");

        // ------------------------------------------------------------------ made-up shaders

        private const string DecodeLine =
            "                v.vertex = mpDecodeAppdata(v.normal, v.tangent, v.uv, v.uv6, v.vertex);";

        private const string InstancedOnlyError =
            "            #if defined(INSTANCING_ON)\n" +
            "            float4 mpCheckBroken() { return mpCheckNeverDeclared; }\n" +
            "            #endif";

        private const string HeadsetOnlyError =
            "            #if defined(STEREO_INSTANCING_ON)\n" +
            "            float4 mpCheckBroken() { return mpCheckNeverDeclared; }\n" +
            "            #endif";

        /// <summary>
        /// Three passes. The second is an outline-like shell pushed out along the normal - the
        /// shape that draws again after the decode has collapsed a locked mesh, which is what
        /// a pass has to be hidden for and not merely collapsed. The third writes depth and no
        /// colour, the way a shadow caster does, and decodes unless told otherwise. The last
        /// has a light mode nothing ever asks for, like the one lilToon puts in every shader.
        /// </summary>
        private static string MadeUpShader(string name, MeshProtectVariant variant, bool decodes,
                                           string body = "", string shell = "",
                                           string shellFragment = "", string extra = "",
                                           string depth = null, string never = "")
        {
            depth = depth ?? (decodes ? DecodeLine : "");

            string pass =
                "            CGPROGRAM\n" +
                "            #pragma vertex vert\n" +
                "            #pragma fragment frag\n" +
                "            #pragma multi_compile_instancing\n" +
                "            #include \"UnityCG.cginc\"\n" +
                (decodes ? "            #include \"mp_decode.hlsl\"\n" : "") +
                "@EXTRA\n" +
                "            struct appdata\n" +
                "            {\n" +
                "                float4 vertex : POSITION; float3 normal : NORMAL; float4 tangent : TANGENT;\n" +
                "                float2 uv : TEXCOORD0; float2 uv6 : TEXCOORD6;\n" +
                "                UNITY_VERTEX_INPUT_INSTANCE_ID\n" +
                "            };\n" +
                "            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };\n" +
                "            fixed4 _Tint;\n" +
                "            v2f vert(appdata v)\n" +
                "            {\n" +
                "                v2f o;\n" +
                "                UNITY_SETUP_INSTANCE_ID(v);\n" +
                "                UNITY_INITIALIZE_OUTPUT(v2f, o);\n" +
                "                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);\n" +
                "@VERTEX\n" +
                "                o.pos = UnityObjectToClipPos(v.vertex);\n" +
                "                o.uv = v.uv;\n" +
                "                return o;\n" +
                "            }\n" +
                "            fixed4 frag(v2f i) : SV_Target\n" +
                "            {\n" +
                "@FRAGMENT\n" +
                "            }\n" +
                "            ENDCG\n";

            return
                "Shader \"" + name + "\"\n{\n" +
                "    Properties\n    {\n" +
                "        _Tint (\"Tint\", Color) = (0.2, 0.6, 0.9, 1)\n" +
                (decodes ? MeshProtectForeignShader.EmitProperties(variant) + "\n" : "") +
                "    }\n" +
                "    SubShader\n    {\n" +
                "        Pass\n        {\n            Name \"BODY\"\n            Cull Off\n" +
                pass.Replace("@EXTRA", extra).Replace("@VERTEX", body)
                    .Replace("@FRAGMENT", "                return fixed4(i.uv, _Tint.b, 1);") +
                "        }\n" +
                "        Pass\n        {\n            Name \"SHELL\"\n            Cull Front\n" +
                pass.Replace("@EXTRA", extra)
                    .Replace("@VERTEX", shell + "\n                v.vertex.xyz += v.normal * 0.05;")
                    .Replace("@FRAGMENT", shellFragment + "\n                return fixed4(0.1, 0.1, 0.1, 1);") +
                "        }\n" +
                "        Pass\n        {\n            Name \"DEPTH\"\n            ColorMask 0\n" +
                pass.Replace("@EXTRA", extra).Replace("@VERTEX", depth)
                    .Replace("@FRAGMENT", "                return 0;") +
                "        }\n" +
                "        Pass\n        {\n            Name \"IDLE\"\n" +
                "            Tags { \"LightMode\" = \"Never\" }\n" +
                pass.Replace("@EXTRA", never).Replace("@VERTEX", "")
                    .Replace("@FRAGMENT", "                return 0;") +
                "        }\n" +
                "    }\n}\n";
        }

        private static void OnMadeUpShaders(MeshProtectRoot settings)
        {
            var variant = settings.variant;
            const string hide = "                if (lilMPIsLocked()) discard;";

            AssetDatabase.CreateFolder(Scratch, "MadeUp");
            string real = Path.Combine(Directory.GetCurrentDirectory(), MadeUp);
            File.WriteAllText(Path.Combine(real, "mp_decode.hlsl"),
                              MeshProtectForeignShader.EmitHeader(variant));

            var sources = new Dictionary<string, string>
            {
                ["Original"] = MadeUpShader("Hidden/MPCheck/Original", variant, false),
                ["Sound"] = MadeUpShader("Hidden/MPCheck/Sound", variant, true,
                                         DecodeLine, DecodeLine, hide),
                ["Forgetful"] = MadeUpShader("Hidden/MPCheck/Forgetful", variant, true,
                                             DecodeLine, "", hide),
                ["Twice"] = MadeUpShader("Hidden/MPCheck/Twice", variant, true,
                                         DecodeLine + "\n" + DecodeLine, DecodeLine, hide),
                ["Showing"] = MadeUpShader("Hidden/MPCheck/Showing", variant, true,
                                           DecodeLine, DecodeLine, ""),
                ["Blind"] = MadeUpShader("Hidden/MPCheck/Blind", variant, true,
                                         DecodeLine, DecodeLine, hide, depth: ""),
                ["Headset"] = MadeUpShader("Hidden/MPCheck/Headset", variant, true,
                                           DecodeLine, DecodeLine, hide, HeadsetOnlyError),
                ["OriginalHeadset"] = MadeUpShader("Hidden/MPCheck/OriginalHeadset", variant, false,
                                                   extra: HeadsetOnlyError),
                ["OriginalBroken"] = MadeUpShader("Hidden/MPCheck/OriginalBroken", variant, false,
                    extra: "            float4 mpCheckBroken() { return mpCheckNeverDeclared; }"),
                ["Instanced"] = MadeUpShader("Hidden/MPCheck/Instanced", variant, true,
                                             DecodeLine, DecodeLine, hide, InstancedOnlyError),
                ["Idle"] = MadeUpShader("Hidden/MPCheck/Idle", variant, true,
                    DecodeLine, DecodeLine, hide,
                    never: "            float4 mpCheckBroken() { return mpCheckNeverDeclared; }"),
                ["FragmentOnly"] = MadeUpShader("Hidden/MPCheck/FragmentOnly", variant, true,
                    DecodeLine, DecodeLine, hide,
                    "            #if defined(SHADER_STAGE_FRAGMENT)\n" +
                    "            float4 mpCheckBroken() { return mpCheckNeverDeclared; }\n" +
                    "            #endif"),
            };
            foreach (var entry in sources)
                File.WriteAllText(Path.Combine(real, entry.Key + ".shader"), entry.Value);
            MeshProtectShaderGen.ImportGenerated(MadeUp);

            Shader Find(string name)
            {
                var shader = Shader.Find("Hidden/MPCheck/" + name);
                if (shader == null) throw new Exception("made-up shader '" + name + "' did not import");
                return shader;
            }

            var original = new Material(Find("Original"));
            Material On(string name, Material source = null)
                => new Material(source ?? original) { shader = Find(name) };

            // ---- nothing wrong
            var sound = Ask(original, On("Sound"), settings);
            Check(!sound.Failed && sound.caution == null && sound.compiled, "made-up/sound-passes",
                  "decode in both passes, hidden while locked: " + Said(sound));

            var soundAgain = Ask(original, On("Sound"), settings);
            Check(!soundAgain.Failed && !soundAgain.compiled, "made-up/compiling-goes-on-record",
                  "the next build asks about the same source and is not made to wait for the " +
                  "compiler again: " + Said(soundAgain));

            var once = new Dictionary<string, MeshProtectShaderCheck.Verdict>();
            var first = MeshProtectShaderCheck.Measure(original, On("Sound"), settings, variant, once);
            var second = MeshProtectShaderCheck.Measure(original, On("Sound"), settings, variant, once);
            Check(ReferenceEquals(first, second) && once.Count == 1, "made-up/one-answer-per-build",
                  "two materials on one shader are one question");

            // ---- the record is about the text, not the name
            File.WriteAllText(Path.Combine(real, "Sound.shader"),
                              MadeUpShader("Hidden/MPCheck/Sound", variant, true, DecodeLine, DecodeLine,
                                           hide, "            float mpCheckUnused() { return 1.0; }"));
            AssetDatabase.ImportAsset(MadeUp + "/Sound.shader", ImportAssetOptions.ForceSynchronousImport |
                                                                ImportAssetOptions.ForceUpdate);
            var edited = Ask(original, On("Sound"), settings);
            Check(!edited.Failed && edited.compiled, "made-up/changed-source-is-compiled-again",
                  "same shader name, one more line in it - the record must not answer for it: " + Said(edited));

            // ---- one thing wrong at a time
            var forgetful = Ask(original, On("Forgetful"), settings);
            Check(forgetful.Failed && forgetful.failure.Contains("'SHELL'") &&
                  forgetful.failure.Contains("once unlocked"),
                  "made-up/a-pass-without-the-decode-is-refused", Said(forgetful));

            var blind = Ask(original, On("Blind"), settings);
            Check(blind.Failed && blind.failure.Contains("'DEPTH'"),
                  "made-up/a-depth-only-pass-without-the-decode-is-refused",
                  "it writes no colour at all, and every shadow in the world is made from it: " + Said(blind));

            var twice = Ask(original, On("Twice"), settings);
            Check(twice.Failed && twice.failure.Contains("'BODY'"),
                  "made-up/a-pass-that-decodes-twice-is-refused", Said(twice));

            var twiceAgain = Ask(original, On("Twice"), settings);
            Check(twiceAgain.Failed && !twiceAgain.compiled, "made-up/the-pictures-are-taken-every-time",
                  "that it compiles is on record by now; what it draws is asked again all the same");

            var showing = Ask(original, On("Showing"), settings);
            Check(!showing.Failed && showing.caution != null && showing.caution.Contains("'SHELL'") &&
                  showing.caution.Contains("while locked"),
                  "made-up/drawing-while-locked-is-said-but-not-refused",
                  "the mesh is still protected; unprotecting it to hide a shell would be the worse trade: " +
                  Said(showing));

            var showingAgain = Ask(original, On("Showing"), settings);
            Check(!showingAgain.compiled && showingAgain.caution == showing.caution,
                  "made-up/the-note-is-said-on-every-build", Said(showingAgain));

            // ---- the variant only a headset draws
            var headset = Ask(original, On("Headset"), settings);
            Check(headset.Failed && headset.failure.Contains("does not compile") &&
                  headset.failure.Contains("stereo-instanced") &&
                  headset.failure.Contains("mpCheckBroken"),
                  "made-up/a-variant-only-vr-compiles-is-refused",
                  "the desktop variant is fine, which is exactly how this got out last time: " + Said(headset));

            var headsetAgain = Ask(original, On("Headset"), settings);
            Check(headsetAgain.Failed && headsetAgain.compiled, "made-up/a-refusal-is-never-on-record",
                  "it is compiled again every time, so nothing transient can refuse a material for good");

            var inherited = new Material(Find("OriginalHeadset"));
            var notOurs = Ask(inherited, On("Headset", inherited), settings);
            Check(!notOurs.Failed, "made-up/what-the-original-cannot-compile-either-is-not-refused",
                  "the same error in the shader it was made from is not this tool's doing: " + Said(notOurs));

            // Asking marks a shader as having errors, and that mark is what the rest of the
            // build reads as "this shader is unusable". Left behind, the author's own shader
            // shows as broken in their project and the next upload refuses the decode shader.
            Check(!ShaderUtil.ShaderHasError(Find("OriginalHeadset")) &&
                  !ShaderUtil.ShaderHasError(Find("Headset")),
                  "made-up/asking-leaves-no-mark-on-either-shader",
                  $"original marked: {ShaderUtil.ShaderHasError(Find("OriginalHeadset"))}, decode " +
                  $"shader marked: {ShaderUtil.ShaderHasError(Find("Headset"))} - both must be False " +
                  "after a question that refused nothing");

            // ---- the variant only GPU instancing draws, asked about only where it can be drawn
            var plainMaterial = Ask(original, On("Instanced"), settings);
            Check(!plainMaterial.Failed, "made-up/instancing-is-not-asked-about-where-it-is-off",
                  "the material has GPU instancing unticked, so that variant is never drawn: " +
                  Said(plainMaterial));

            var ticked = new Material(original) { enableInstancing = true };
            var instanced = Ask(ticked, On("Instanced", ticked), settings);
            Check(instanced.Failed && instanced.failure.Contains("GPU instancing"),
                  "made-up/a-variant-only-instancing-compiles-is-refused-where-it-is-on", Said(instanced));

            // ---- a pass the game never runs is not a reason. lilToon ships one in every
            // shader, and its own build-time optimisation breaks it in every custom family.
            var idle = Ask(original, On("Idle"), settings);
            Check(!idle.Failed, "made-up/a-broken-pass-that-never-runs-is-not-refused",
                  "light mode 'Never', does not compile, and cannot matter: " + Said(idle));

            // ---- the stage the compile half leaves to the pictures
            var fragmentOnly = Ask(original, On("FragmentOnly"), settings);
            Check(fragmentOnly.Failed && fragmentOnly.failure.Contains("once it is drawn"),
                  "made-up/a-fragment-stage-that-does-not-compile-is-refused-and-called-that",
                  "every stage before the rasteriser builds; the picture is Unity's error shader: " +
                  Said(fragmentOnly));

            // ---- an original that draws as Unity's error shader is nothing to compare with.
            // Found the hard way: an editor started so that it could not open its own includes
            // drew every shader magenta, and the comparison refused every material there was.
            var broken = new Material(Find("OriginalBroken"));
            var noReference = Ask(broken, On("Sound", broken), settings);
            Check(!noReference.Failed, "made-up/an-original-that-does-not-compile-is-no-reference",
                  "a sound decode shader beside an original that only draws magenta: " + Said(noReference));
        }

        // ------------------------------------------------------------------ real shaders

        private static void OnRealShaders(MeshProtectRoot settings, Shader lilToon)
        {
            var variant = settings.variant;
            MeshProtectShaderGen.EnsureGenerated(settings, variant, out string _);

            foreach (string name in new[] { "lilToon", "Hidden/lilToonCutoutOutline",
                                            "Hidden/lilToonTessellation", "Hidden/lilToonFur" })
            {
                var shader = Shader.Find(name);
                if (shader == null) { Say($"SKIP  real/{name}  this lilToon has no shader of that name"); continue; }

                var source = new Material(shader);
                var copy = new Material(source);
                if (!MeshProtectInspector.TryConvert(copy, variant))
                {
                    Check(false, "real/" + name, "did not convert onto the generated family");
                    continue;
                }

                var verdict = Ask(source, copy, settings);
                Check(!verdict.Failed && verdict.caution == null && verdict.compiled, "real/" + name,
                      $"onto {copy.shader.name}: " + Said(verdict));
            }

            var ssrt = Shader.Find("Hidden/lilSSRT/GTAO/AOTessellation/Opaque");
            if (ssrt == null)
            {
                Say("SKIP  real/lilSSRT  not installed in this project");
                return;
            }

            MeshProtectLilHost.Forget();
            var host = MeshProtectLilHost.HostFor(ssrt);
            MeshProtectLilHost.EnsureMerged(settings, variant, host, out string why, force: true);
            var target = MeshProtectLilHost.MergedShaderFor(settings, variant, host, ssrt);
            Check(target != null, "real/lilSSRT-merges", why ?? "the AO tessellation variant has a merged twin");
            if (target == null) return;

            // The variant lilSSRT assigns by itself when AO is on with its shipped defaults -
            // the one that compiled for the desktop and not for a headset.
            var worn = new Material(ssrt);
            worn.EnableKeyword("LILSSRT_EVAL_VERTEX");
            var merged = new Material(worn) { shader = target };
            var answer = Ask(worn, merged, settings);
            Check(!answer.Failed && answer.caution == null && answer.compiled,
                  "real/lilSSRT-ao-tessellation", Said(answer));
        }

        // ------------------------------------------------------------------ the whole road

        /// <summary>
        /// Break the generated family's tessellation in the stereo-instanced variant only,
        /// AFTER it was generated - the position an author is in when a shader they use is
        /// updated and they have not pressed Rebuild Shader. Nothing on disk says anything is
        /// wrong: the family is complete, current, and every shader in it imports.
        /// </summary>
        private static void InAnUpload(MeshProtectRoot owner, Shader lilToon)
        {
            var tessellation = Shader.Find("Hidden/lilToonTessellation");
            if (tessellation == null)
            {
                Say("SKIP  upload  this lilToon has no tessellation shader to break");
                return;
            }

            var variant = owner.variant;
            string folder = MeshProtectShaderGen.FolderFor(owner, variant);
            string post = Path.Combine(Directory.GetCurrentDirectory(), folder,
                                       "lilCustomShaderInsertPost.lilblock");
            File.AppendAllText(post,
                "\n#if defined(STEREO_INSTANCING_ON)\n" +
                "float4 lilMPCheckBroken() { return lilMPCheckNeverDeclared; }\n" +
                "#endif\n");
            AssetDatabase.ImportAsset(folder, ImportAssetOptions.ImportRecursive |
                                              ImportAssetOptions.ForceSynchronousImport |
                                              ImportAssetOptions.ForceUpdate);

            Check(MeshProtectShaderGen.FamilyIsCurrent(owner, variant) &&
                  MeshProtectShaderGen.FamilyIsComplete(variant, out int _, out int _),
                  "upload/nothing-on-disk-says-so",
                  "the broken family still reads as current and complete, which is the point");

            AssetDatabase.CreateFolder(Scratch, "build");
            AssetDatabase.CreateFolder(Scratch, "dry");

            GameObject Avatar(out MeshRenderer body, out MeshRenderer hat, out MeshProtectRoot settings)
            {
                var root = new GameObject("CheckAvatar");
                root.AddComponent<Animator>();
                root.AddComponent<VRCAvatarDescriptor>().customExpressions = false;

                body = Part(root, "Body", new Material(tessellation) { name = "BodyMaterial" });
                hat = Part(root, "Hat", new Material(lilToon) { name = "HatMaterial" });

                settings = root.AddComponent<MeshProtectRoot>();
                settings.outputFolder = owner.outputFolder;
                settings.keyDigits = owner.keyDigits;
                settings.variant = variant;
                return root;
            }

            // ---- at the button
            var rehearsal = Avatar(out _, out _, out var rehearsalSettings);
            try
            {
                var notes = MeshProtectPipeline.CheckShaders(rehearsal, rehearsalSettings, Scratch + "/dry");
                Check(notes.Count == 1 && notes[0].Contains("Hidden/lilToonTessellation") &&
                      notes[0].Contains("stereo-instanced"),
                      "upload/rebuild-shader-names-it-before-any-upload",
                      notes.Count == 0 ? "nothing was said" : string.Join(" | ", notes));
            }
            finally { UnityEngine.Object.DestroyImmediate(rehearsal); }

            // ---- in the build
            var avatar = Avatar(out var bodyRenderer, out var hatRenderer, out var settings);
            try
            {
                var bodyMesh = bodyRenderer.GetComponent<MeshFilter>().sharedMesh;
                var bodyMaterial = bodyRenderer.sharedMaterial;

                var report = new MeshProtectPipeline.Report();
                MeshProtectPipeline.Apply(avatar, settings, Scratch + "/build", report);

                Check(report.appliedProtection, "upload/the-rest-is-still-protected",
                      "one refused shader must not cost the avatar its protection");

                Check(MeshProtectPipeline.IsProtectShader(hatRenderer.sharedMaterial, variant.shaderName),
                      "upload/the-sound-material-is-converted",
                      $"hat is on '{hatRenderer.sharedMaterial.shader.name}'");

                Check(bodyRenderer.sharedMaterial == bodyMaterial &&
                      bodyRenderer.GetComponent<MeshFilter>().sharedMesh == bodyMesh,
                      "upload/the-refused-material-is-left-exactly-as-it-was",
                      "its own material and its own mesh: unprotected and intact, not protected " +
                      "and broken for everybody in a headset");

                Check(report.warnings.Any(w => w.Contains("'BodyMaterial'") &&
                                               w.Contains("did not pass the check") &&
                                               w.Contains("stereo-instanced")),
                      "upload/the-report-says-which-and-why",
                      "the material, that it was the check, and the compiler's own words");

                var answers = report.shaderChecks.Values;
                Check(answers.Count == 2 && answers.Count(a => a.Failed) == 1,
                      "upload/the-report-holds-every-answer",
                      $"{answers.Count} asked, {answers.Count(a => a.Failed)} refused");

                // The family's own broken shader stays marked - that is how the rest of the
                // build knows, and says "Rebuild Shader". Nothing of lilToon's may be.
                var marked = ShaderUtil.GetAllShaderInfo().Where(i => i.hasErrors).Select(i => i.name).ToList();
                bool familyMarked = marked.Any(n => n.Contains(variant.shaderName));
                var strangers = marked.Where(n => !n.Contains(variant.shaderName) &&
                                                  !n.StartsWith("Hidden/MPCheck/")).ToList();
                Check(familyMarked && strangers.Count == 0,
                      "upload/only-the-refused-family-is-left-marked-as-broken",
                      $"a shader of this avatar's family marked: {familyMarked}; anything else marked: " +
                      (strangers.Count == 0 ? "nothing" : string.Join(", ", strangers.Take(5))));

                Check(!bodyRenderer.enabled && hatRenderer.enabled && report.hiddenWhileLocked == 1,
                      "upload/the-lock-gate-takes-what-was-refused",
                      $"body enabled {bodyRenderer.enabled}, hat enabled {hatRenderer.enabled}, " +
                      $"hidden while locked {report.hiddenWhileLocked}: what could not be protected " +
                      "is at least not standing there on a locked avatar");
            }
            finally { UnityEngine.Object.DestroyImmediate(avatar); }
        }

        private static MeshRenderer Part(GameObject root, string name, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = Sheet(name + "Mesh");
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return renderer;
        }

        private static Mesh Sheet(string name)
        {
            const int side = 6;
            var vertices = new Vector3[side * side];
            var normals = new Vector3[side * side];
            var uv = new Vector2[side * side];
            for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int i = y * side + x;
                vertices[i] = new Vector3(x * 0.1f, y * 0.1f, Mathf.Sin(i) * 0.02f);
                normals[i] = Vector3.back;
                uv[i] = new Vector2((x + 0.5f) / side, (y + 0.5f) / side);
            }

            var triangles = new List<int>();
            for (int y = 0; y < side - 1; y++)
            for (int x = 0; x < side - 1; x++)
            {
                int i = y * side + x;
                triangles.AddRange(new[] { i, i + side, i + 1, i + 1, i + side, i + side + 1 });
            }

            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
#endif
