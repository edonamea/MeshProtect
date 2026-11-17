#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MeshProtect
{
    /// <summary>
    /// Is a shader that carries the decode still the shader it was made from?
    ///
    /// Every way this tool puts the decode into somebody else's shader - the generated lilToon
    /// family, a merged host family, a graft - is text written against what that shader looked
    /// like on the day the code was written. The shader moves on; the text does not notice.
    /// lilSSRT 1.4 is the case that taught this: the merge reported success, the desktop variant
    /// compiled, and the stereo-instanced variant - the only one a VR viewer draws - did not.
    ///
    /// So the answer no longer rests on knowing the shader. It is measured, in every build, for
    /// every shader a material is about to be moved onto:
    ///
    ///   1. each pass the game can run compiles, for the desktop and for the stereo-instanced
    ///      variant, and with GPU instancing where the material has it on. A failure counts
    ///      only where the shader it was made from compiles the same variant - what the
    ///      original cannot do either is not this tool's doing;
    ///   2. drawn with the right password over a displaced mesh, each pass puts the same picture
    ///      on screen as the original does over the plain mesh;
    ///   3. drawn locked, it puts nothing on screen.
    ///
    /// Failing 1 or 2 refuses the material: it ships unprotected and intact, which is the rule
    /// the rest of the build already runs on - a check that fails degrades, it does not block.
    /// Failing 3 is said and nothing more. The mesh is still protected, and leaving it readable
    /// to stop a locked avatar from showing a few pixels would be the worse trade.
    ///
    /// What this cannot see. Whatever a later build step does to the shader: lilToon's own
    /// hook switches features off and trims the property block for the avatar after this
    /// tool's has returned, so what is measured is the shader before that. The
    /// stereo-instanced variant on screen - it is compiled, not drawn. And the light, shadow
    /// and fog keywords beyond one set per pass: none of them changes what a vertex is made
    /// of, which is where the decode lives, and each would cost seconds per shader.
    /// </summary>
    public static class MeshProtectShaderCheck
    {
        public sealed class Verdict
        {
            /// <summary>The shader the material was on before it was moved.</summary>
            public string shader;

            /// <summary>Why a material may not be moved onto its decode shader, or null.</summary>
            public string failure;

            /// <summary>Something worth saying that refuses nothing, or null.</summary>
            public string caution;

            /// <summary>
            /// True when this call compiled it; false when exactly this source was already on
            /// record as compiling. The pictures are taken either way.
            /// </summary>
            public bool compiled;

            public bool Failed => failure != null;

            public override string ToString() => Failed
                ? $"'{shader}' did not pass the shader check, so materials on it upload " +
                  $"UNPROTECTED and intact: {failure}"
                : $"'{shader}' passed the shader check with a note: {caution}";
        }

        // Always Direct3D for Windows: the PC client is the only place a protected avatar runs.
        // Not the active build target - that would make the answer depend on which platform the
        // project happens to be switched to.
        private const ShaderCompilerPlatform Platform = ShaderCompilerPlatform.D3D;
        private const BuildTarget Target = BuildTarget.StandaloneWindows64;

        /// <summary>Bump when what is compiled changes; everything on record is then compiled again.</summary>
        private const int RecordFormat = 1;

        private static readonly ShaderTagId LightMode = new ShaderTagId("LightMode");
        private static readonly bool[] DesktopThenStereo = { false, true };
        private const string Instancing = "INSTANCING_ON";

        /// <summary>
        /// Measure one conversion: <paramref name="source"/> is the material as the author made
        /// it, <paramref name="converted"/> the copy now on the shader that carries the decode.
        ///
        /// <paramref name="thisBuild"/> holds every answer this build has already had, so twenty
        /// materials on one shader ask once. It is the caller's, not a static, on purpose: the
        /// same shader name can hold different source the next time anybody asks.
        ///
        /// The two halves are not remembered alike. That a source compiles is a fact about the
        /// source, costs seconds to establish, and goes on record until the source changes.
        /// The pictures cost almost nothing once Unity has the variants, and what they show
        /// also depends on the material in hand, so they are taken every time.
        ///
        /// Never throws. A check that could not run refuses nothing - the alternative is an
        /// avatar that loses its protection to a fault in the thing meant to guard it.
        /// </summary>
        public static Verdict Measure(Material source, Material converted, MeshProtectRoot settings,
                                      MeshProtectVariant variant,
                                      IDictionary<string, Verdict> thisBuild)
        {
            // Every keyword set the game can draw this material with: its own, and its own with
            // GPU instancing where the material has that ticked. Instancing puts an instance id
            // into the vertex input exactly as stereo does, and a struct that grew by one member
            // is what the lilSSRT 1.4 failure was made of.
            string[] keywords = converted.shaderKeywords.OrderBy(k => k, StringComparer.Ordinal)
                                                        .ToArray();
            var keywordSets = new List<string[]> { keywords };
            if (converted.enableInstancing)
                keywordSets.Add(keywords.Concat(new[] { Instancing }).ToArray());

            string asked = source.shader.name + "\n" + converted.shader.name + "\n" +
                           string.Join(" | ", keywordSets.Select(set => string.Join(" ", set)));
            if (thisBuild.TryGetValue(asked, out var verdict)) return verdict;

            verdict = new Verdict { shader = source.shader.name };
            thisBuild[asked] = verdict;

            bool barShown = false;
            try
            {
                var marked = ShadersWithErrors();
                string print = Fingerprint(source.shader, converted.shader, keywordSets);
                if (print == null || !Record().Contains(print))
                {
                    // Seconds per shader, and the editor is not answering meanwhile.
                    barShown = true;
                    EditorUtility.DisplayProgressBar(MeshProtectL10n.Tr("progress.title"),
                        MeshProtectL10n.Tr("progress.check", source.shader.name), 0.5f);

                    verdict.compiled = true;
                    try
                    {
                        verdict.failure = CompileDifference(source.shader, converted.shader, keywordSets);
                    }
                    finally
                    {
                        Unmark(marked, verdict.Failed ? variant.shaderName : null);
                    }

                    // Only a pass goes on record. A refusal is compiled again every time: it
                    // costs seconds on a shader that is already in trouble, and it means
                    // nothing transient can leave a material refused for good.
                    if (!verdict.Failed && print != null) Remember(print);
                }

                if (!verdict.Failed) Draw(source, converted, settings, variant, verdict);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MeshProtect] The shader check could not finish on " +
                                 $"'{source.shader.name}' and was skipped for it: {e.Message}");
            }
            finally
            {
                if (barShown) EditorUtility.ClearProgressBar();
            }

            return verdict;
        }

        private static HashSet<string> ShadersWithErrors()
            => new HashSet<string>(ShaderUtil.GetAllShaderInfo().Where(i => i.hasErrors)
                                                                .Select(i => i.name));

        /// <summary>
        /// Take back the marks this check's own questions left on shaders.
        ///
        /// Asking Unity whether a variant compiles is not free of consequences: a variant that
        /// does not is recorded against the shader, which then reads as "has errors" from that
        /// moment until it is imported again - measured, and it survives a refresh. The rest of
        /// this tool decides whether a shader is usable from exactly that flag. So a variant
        /// the ORIGINAL cannot compile either, which is nobody's fault and refuses nothing,
        /// would have left the author's own shader marked broken in their project and the
        /// decode shader refused on the next upload as one that failed to compile.
        ///
        /// Everything newly marked is cleared, except - when the check did refuse - the decode
        /// family's own shaders. Those really are broken, and the mark is how the rest of the
        /// build says so and points at Rebuild Shader.
        /// </summary>
        private static void Unmark(HashSet<string> markedBefore, string refusedFamily)
        {
            foreach (var info in ShaderUtil.GetAllShaderInfo())
            {
                if (!info.hasErrors || markedBefore.Contains(info.name)) continue;

                var shader = Shader.Find(info.name);
                if (shader == null) continue;

                string family = MeshProtectInspector.FamilyOf(shader);
                if (refusedFamily != null && family != null &&
                    family.StartsWith(refusedFamily, StringComparison.Ordinal))
                    continue;

                ShaderUtil.ClearShaderMessages(shader);
            }
        }

        /// <summary>Drop everything on record, so the next build compiles again.</summary>
        public static void Forget()
        {
            record = new HashSet<string>();
            try { File.Delete(RecordPath); }
            catch (Exception) { /* nothing was on record, or it is asked again anyway */ }
        }

        // ------------------------------------------------------------------ the record

        // Under Library, not beside the generated shaders: writing an asset in the middle of a
        // build is the one thing this project has hard evidence against, and a file under
        // Library is not an asset. Losing it costs one slow build.
        private static string RecordPath => Path.Combine(
            Path.GetDirectoryName(Application.dataPath), "Library", "MeshProtect", "shader-check.txt");

        /// <summary>The fingerprint of every conversion that compiled.</summary>
        private static HashSet<string> record;

        private static HashSet<string> Record()
        {
            if (record != null) return record;

            record = new HashSet<string>();
            try
            {
                if (File.Exists(RecordPath)) record.UnionWith(File.ReadAllLines(RecordPath));
            }
            catch (Exception) { /* unreadable is the same as empty: everything is compiled */ }
            return record;
        }

        private static void Remember(string print)
        {
            Record().Add(print);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RecordPath));
                File.AppendAllText(RecordPath, print + "\n");
            }
            catch (Exception) { /* not written is only compiled again next session */ }
        }

        /// <summary>
        /// What both shaders say once every include is resolved, as one hash - or null when
        /// that cannot be had, which is answered by compiling every time.
        ///
        /// The preprocessed text rather than a file hash or a version number, because it is
        /// the only thing that changes exactly when what the compiler sees does: an include
        /// three packages away, lilToon regenerating its own settings, a host updated under a
        /// merged copy. It costs milliseconds where the compile it stands in for costs seconds.
        /// The stereo variant is hashed too, since code that exists only there is where the
        /// failure this was written for lived.
        /// </summary>
        private static string Fingerprint(Shader original, Shader mine, List<string[]> keywordSets)
        {
            using (var sha = SHA256.Create())
            {
                void Feed(string text)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }

                Feed("format " + RecordFormat);
                Feed(Application.unityVersion);

                foreach (var shader in new[] { original, mine })
                {
                    Feed(shader.name);
                    foreach (var pass in Passes(shader))
                    {
                        if (!Runs(pass)) continue;
                        foreach (var keywords in keywordSets)
                        foreach (bool stereo in DesktopThenStereo)
                        {
                            var text = pass.PreprocessVariant(ShaderType.Vertex,
                                VariantKeywords(pass, keywords, stereo), Platform, Target, true);
                            if (!text.Success || string.IsNullOrEmpty(text.PreprocessedCode))
                                return null;
                            Feed(text.PreprocessedCode);
                        }
                    }
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);
                return string.Concat(sha.Hash.Select(b => b.ToString("x2")));
            }
        }

        // ------------------------------------------------------------------ compiling

        private static List<ShaderData.Pass> Passes(Shader shader)
        {
            var passes = new List<ShaderData.Pass>();
            var data = ShaderUtil.GetShaderData(shader);
            if (data == null || data.SubshaderCount == 0) return passes;

            var subshader = data.ActiveSubshader ?? data.GetSubshader(0);
            for (int i = 0; i < subshader.PassCount; i++) passes.Add(subshader.GetPass(i));
            return passes;
        }

        // What VRChat's forward renderer draws an avatar with. Deferred, vertex-lit and the
        // scriptable pipelines' modes are never asked for there, and Meta only by a lightmapper.
        private static readonly HashSet<string> LightModesThatRun =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "", "Always", "ForwardBase", "ForwardAdd", "ShadowCaster", "MotionVectors"
            };

        /// <summary>
        /// Is this a pass that has code and that the game will ever run?
        ///
        /// A pass that never runs cannot break an avatar, so it cannot be a reason to refuse
        /// one - and there is one in every lilToon shader. lilToon adds a pass with the light
        /// mode "Never" to work around a Unity bug that strips UV channels, and its own
        /// build-time optimisation then breaks that pass in every custom family: it pastes the
        /// optimised property block after an include that this pass has commented out. Asked
        /// about a family in that state, this check answered that nothing on the avatar could
        /// be protected.
        /// </summary>
        private static bool Runs(ShaderData.Pass pass)
            => !pass.IsGrabPass && !string.IsNullOrEmpty(pass.SourceCode) &&
               LightModesThatRun.Contains(pass.FindTagValue(LightMode).name ?? "");

        /// <summary>
        /// The keywords one pass is asked for: the material's own, the light that kind of pass
        /// is drawn under, and the one that makes it the variant a headset draws.
        /// </summary>
        internal static string[] VariantKeywords(ShaderData.Pass pass, IEnumerable<string> material,
                                                 bool stereo)
        {
            var keywords = new List<string>(material ?? Enumerable.Empty<string>());

            string mode = pass.FindTagValue(LightMode).name ?? "";
            keywords.Add(mode.Equals("ForwardAdd", StringComparison.OrdinalIgnoreCase) ? "POINT"
                       : mode.Equals("ShadowCaster", StringComparison.OrdinalIgnoreCase) ? "SHADOWS_DEPTH"
                       : "DIRECTIONAL");

            if (stereo) keywords.Add("STEREO_INSTANCING_ON");
            return keywords.ToArray();
        }

        /// <summary>
        /// The first error compiling one stage of one variant, or null. A compile that comes
        /// back with no error message proves nothing and denies nothing.
        /// </summary>
        internal static string CompileError(ShaderData.Pass pass, ShaderType stage, string[] keywords)
        {
            var info = pass.CompileVariant(stage, keywords, Platform, Target);
            var error = (info.Messages ?? new ShaderMessage[0]).FirstOrDefault(
                m => m.severity == ShaderCompilerMessageSeverity.Error);
            if (string.IsNullOrEmpty(error.message)) return null;

            return error.message + (string.IsNullOrEmpty(error.file)
                ? "" : $" ({Path.GetFileName(error.file)}:{error.line})");
        }

        /// <summary>
        /// The first variant the decode-carrying shader cannot compile and its original can.
        ///
        /// Every pass that runs, because the decode is in every pass and each is built from
        /// different blocks. Every stage that sits before the rasteriser, because that is where the
        /// decode and the structs it travels in are; the fragment stage is left to the draw
        /// below, which cannot put a picture on screen without it.
        /// </summary>
        private static string CompileDifference(Shader original, Shader mine,
                                                List<string[]> keywordSets)
        {
            var passes = Passes(mine);
            var originals = Passes(original);

            for (int i = 0; i < passes.Count; i++)
            {
                var pass = passes[i];
                if (!Runs(pass)) continue;

                // The same pass in the original: by position while the two are laid out alike,
                // which they are whenever one was made from the other.
                var twin = originals.Count == passes.Count ? originals[i] : null;

                var stages = new List<ShaderType> { ShaderType.Vertex };
                foreach (var later in new[] { ShaderType.Hull, ShaderType.Domain, ShaderType.Geometry })
                    if (pass.HasShaderStage(later)) stages.Add(later);

                foreach (var keywords in keywordSets)
                foreach (bool stereo in DesktopThenStereo)
                {
                    string[] variantKeywords = VariantKeywords(pass, keywords, stereo);
                    foreach (var stage in stages)
                    {
                        string error = CompileError(pass, stage, variantKeywords);
                        if (error == null) continue;

                        bool asked = twin != null && Runs(twin);
                        if (asked && CompileError(twin, stage, variantKeywords) != null)
                            break;      // the original does not build this one either

                        return $"pass '{pass.Name}' does not compile" +
                               (stereo ? " in the stereo-instanced variant, which is the one " +
                                         "VR viewers draw" : "") +
                               (keywords.Contains(Instancing) ? " with GPU instancing" : "") +
                               $" ({stage} stage)" +
                               (asked ? " although the original does" : "") + $": {error}";
                    }
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ drawing

        private const int Size = 96;

        // A pass that puts down fewer pixels than this is not drawing the ball at all.
        private const int Drawn = 64;

        // Fewer than this still on screen while locked is a point or two of rasteriser noise.
        private const int Stray = 8;

        /// <summary>
        /// Draw a ball through every pass four ways and compare the pictures.
        ///
        /// The original over the plain ball is what the pass is supposed to look like. The
        /// original over the DISPLACED ball is what it looks like with no decode at all, and it
        /// is what makes the comparison mean something: a pass that draws the same picture
        /// either way does not care where the vertices are, and says nothing about the decode.
        /// Then the decode shader over the displaced ball, unlocked and locked.
        ///
        /// Per pass, because one camera frame hides what matters. A forward pass drawn last
        /// covers an outline pass that came out as noise, and a shadow caster never reaches
        /// the frame at all - see Lens for how one is seen here.
        /// </summary>
        private static void Draw(Material source, Material converted, MeshProtectRoot settings,
                                 MeshProtectVariant variant, Verdict verdict)
        {
            // A null device accepts the work and reads back nothing, which would look like a
            // locked avatar that passes and an unlocked one that draws nothing. The compile
            // half stands on its own there.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

            // Laid out differently, so there is no telling which pass answers to which.
            var passes = Passes(converted.shader);
            if (source.passCount != converted.passCount || passes.Count != converted.passCount)
                return;

            uint key = MeshProtectCipher.PackDigits(settings.keyDigits, variant);
            Vector4 mac = MeshProtectCipher.MacToVector(MeshProtectCipher.Mac(key, variant));

            // Tangent space reads the tangent as well as the normal, so it is the mode that
            // tests more. But the avatar falls back to Normal when one mesh has no tangents,
            // and that is not decided until every material has been converted.
            var modes = settings.mode == MeshProtectRoot.DisplacementMode.TangentSpace
                ? new[] { MeshProtectRoot.DisplacementMode.TangentSpace,
                          MeshProtectRoot.DisplacementMode.Normal }
                : new[] { MeshProtectRoot.DisplacementMode.Normal };

            var scratch = new List<Object>();
            bool async = ShaderUtil.allowAsyncCompilation;
            try
            {
                // Off already for anything a script draws, unless something further up
                // switched it on - and then the editor would draw a placeholder while the
                // variant compiles behind it, a placeholder has no decode, and every shader
                // on the avatar would be refused.
                ShaderUtil.allowAsyncCompilation = false;

                // The bake reads how far to move a vertex off the component. The author's own
                // ratio can be small enough that a missing decode moves nothing a 96 pixel
                // picture can show, so the ball is displaced by a ratio that always shows.
                var rig = new GameObject("MeshProtectShaderCheck") { hideFlags = HideFlags.HideAndDontSave };
                scratch.Add(rig);
                var displacement = rig.AddComponent<MeshProtectRoot>();
                displacement.distortRatio = 0.15f;

                var ball = Ball();
                scratch.Add(ball);

                var unlocked = new Material(converted) { hideFlags = HideFlags.HideAndDontSave };
                var locked = new Material(converted) { hideFlags = HideFlags.HideAndDontSave };
                scratch.Add(unlocked);
                scratch.Add(locked);
                foreach (var material in new[] { unlocked, locked })
                {
                    material.SetFloat(variant.bypassProperty, 0f);
                    material.SetVector(variant.macProperty, mac);
                }
                for (int i = 0; i < variant.digitProperties.Length; i++)
                {
                    unlocked.SetFloat(variant.digitProperties[i], settings.keyDigits[i]);
                    locked.SetFloat(variant.digitProperties[i], 0f);
                }

                using (var lens = new Lens())
                {
                    foreach (var mode in modes)
                    {
                        var displaced = MeshProtectMesh.Bake(ball, displacement, mode, key, variant).mesh;
                        scratch.Add(displaced);

                        float modeValue = mode == MeshProtectRoot.DisplacementMode.TangentSpace ? 1f : 0f;
                        unlocked.SetFloat(variant.modeProperty, modeValue);
                        locked.SetFloat(variant.modeProperty, modeValue);

                        for (int pass = 0; pass < converted.passCount; pass++)
                        {
                            if (!Runs(passes[pass])) continue;

                            var intended = lens.Shoot(ball, source, pass);
                            int drawn = Differing(intended, lens.empty);
                            if (drawn < Drawn || IsErrorShader(intended, lens.empty)) continue;

                            int undecoded = Differing(intended, lens.Shoot(displaced, source, pass));
                            if (undecoded < Math.Max(Drawn / 2, drawn / 50)) continue;

                            // A quarter of the way to no decode at all. A pass that decodes is
                            // off by a pixel or two along an edge; one that does not is off by
                            // everything the line above just counted.
                            var decoded = lens.Shoot(displaced, unlocked, pass);
                            int off = Differing(intended, decoded);
                            if (off * 4 > undecoded)
                            {
                                string name = converted.GetPassName(pass);
                                verdict.failure = IsErrorShader(decoded, lens.empty)
                                    ? $"pass '{name}' does not compile once it is drawn - Unity " +
                                      "puts its error shader in its place, and the Console has " +
                                      "the shader error"
                                    : $"once unlocked, pass '{name}' does not draw what the " +
                                      $"original draws ({mode} mode: {off} pixels differ where " +
                                      $"the original covers {drawn}, and a mesh with no decode at " +
                                      $"all differs on {undecoded}) - the decode is missing from " +
                                      "that pass, or runs in it twice";
                                return;
                            }

                            int showing = Differing(lens.Shoot(displaced, locked, pass), lens.empty);
                            if (showing > Stray && verdict.caution == null)
                                verdict.caution =
                                    $"pass '{converted.GetPassName(pass)}' still puts {showing} of " +
                                    $"{drawn} pixels on screen while locked";
                        }
                    }
                }
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = async;
                foreach (var made in scratch) Object.DestroyImmediate(made);
            }
        }

        /// <summary>
        /// Is this picture Unity's stand-in for a shader that did not compile?
        ///
        /// When the ORIGINAL does not compile there is nothing to compare with, and comparing
        /// anyway refuses every material on the avatar for something that is not theirs: an
        /// editor that cannot find its own includes draws everything in this one colour, the
        /// decode shader included, and "the same colour over a displaced mesh" is exactly
        /// what a missing decode looks like. A material that really is unlit magenta loses
        /// the comparison too, which costs it nothing.
        ///
        /// Asked of the decode shader's picture as well, once it has already failed the
        /// comparison, only to say why.
        /// </summary>
        private static bool IsErrorShader(Color32[] picture, Color32[] empty)
        {
            bool drawn = false;
            for (int i = 0; i < picture.Length; i++)
            {
                var pixel = picture[i];
                if (!Differs(pixel, empty[i])) continue;
                if (pixel.r != 255 || pixel.g != 0 || pixel.b != 255 || pixel.a != 255) return false;
                drawn = true;
            }
            return drawn;
        }

        private static int Differing(Color32[] a, Color32[] b)
        {
            int count = 0;
            for (int i = 0; i < a.Length; i++) if (Differs(a[i], b[i])) count++;
            return count;
        }

        // Two levels of slack: the decoded position is the original to within float rounding,
        // and that much can tip the last bit of an interpolated colour.
        private static bool Differs(Color32 a, Color32 b)
            => Math.Abs(a.r - b.r) > 2 || Math.Abs(a.g - b.g) > 2 ||
               Math.Abs(a.b - b.b) > 2 || Math.Abs(a.a - b.a) > 2;

        /// <summary>
        /// One fixed orthographic view of the origin, drawn without a Camera component: one
        /// pass of one material at a time is not something a camera can be asked for.
        ///
        /// A pass does not have to write colour to matter. A shadow caster writes depth and
        /// nothing else, and it is what every shadow and every depth effect in a world is made
        /// from. So behind each pass a sheet is drawn that inverts the colour wherever the
        /// pass left the depth buffer alone: a pixel then differs from the empty picture if
        /// the pass wrote colour there, or depth, or both.
        /// </summary>
        private sealed class Lens : IDisposable
        {
            private static readonly Vector3 Eye = new Vector3(0f, 0f, -2f);
            private const float Behind = 3f;

            private readonly RenderTexture target =
                new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
            private readonly Texture2D readback =
                new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);
            private readonly CommandBuffer commands = new CommandBuffer { name = "MeshProtect shader check" };

            /// <summary>
            /// The picture with nothing drawn into it. Read back rather than assumed: what a
            /// clear colour comes back as depends on the project's colour space.
            /// </summary>
            public readonly Color32[] empty;

            private readonly Material backdrop;
            private readonly Mesh sheet;

            // What a vertex stage reads to decide where a vertex goes, and what setting the
            // view and projection leaves holding whatever was there. A camera sets its own
            // over these while it renders - measured: a lit, shadowed frame is the same to the
            // pixel before this runs and after - but anything drawn without one reads them as
            // they are left, so they are borrowed and put back.
            private static readonly string[] Borrowed =
            {
                "_WorldSpaceCameraPos", "unity_OrthoParams", "_WorldSpaceLightPos0", "unity_LightShadowBias"
            };
            private readonly Vector4[] lent = Borrowed.Select(name => Shader.GetGlobalVector(name)).ToArray();

            public Lens()
            {
                // Unity's own shader for immediate-mode drawing: nothing but a colour, with
                // the blend and the depth test as properties. Without it only colour counts.
                var plain = Shader.Find("Hidden/Internal-Colored");
                if (plain != null)
                {
                    backdrop = new Material(plain) { hideFlags = HideFlags.HideAndDontSave };
                    backdrop.SetInt("_SrcBlend", (int)BlendMode.OneMinusDstColor);
                    backdrop.SetInt("_DstBlend", (int)BlendMode.Zero);
                    backdrop.SetInt("_Cull", (int)CullMode.Off);
                    backdrop.SetInt("_ZWrite", 0);
                    backdrop.SetInt("_ZTest", (int)CompareFunction.LessEqual);

                    sheet = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                    sheet.vertices = new[]
                    {
                        new Vector3(-1f, -1f, Behind), new Vector3(-1f, 1f, Behind),
                        new Vector3(1f, 1f, Behind), new Vector3(1f, -1f, Behind)
                    };
                    sheet.colors32 = Enumerable.Repeat(new Color32(255, 255, 255, 255), 4).ToArray();
                    sheet.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                }

                empty = Shoot(null, null, 0);
            }

            public Color32[] Shoot(Mesh mesh, Material material, int pass)
            {
                commands.Clear();
                commands.SetRenderTarget(target);
                // Nowhere near its own inverse, or a pass that writes depth and no colour would
                // leave exactly what the sheet leaves everywhere else.
                commands.ClearRenderTarget(true, true, new Color(0.2f, 0.3f, 0.9f, 0.1f));
                commands.SetViewProjectionMatrices(
                    Matrix4x4.TRS(Eye, Quaternion.identity, new Vector3(1f, 1f, -1f)).inverse,
                    Matrix4x4.Ortho(-0.5f, 0.5f, -0.5f, 0.5f, 0.05f, 10f));

                // The light matters most. A shadow caster moves every vertex along the light
                // direction, and in an editor that has not drawn a light yet that direction is
                // zero: normalised, it is not a number, and the whole pass draws nothing.
                commands.SetGlobalVector(Borrowed[0], Eye);
                commands.SetGlobalVector(Borrowed[1], new Vector4(0.5f, 0.5f, 0f, 1f));
                commands.SetGlobalVector(Borrowed[2], new Vector4(0.3f, 0.8f, -0.5f, 0f));
                commands.SetGlobalVector(Borrowed[3], Vector4.zero);

                if (mesh != null) commands.DrawMesh(mesh, Matrix4x4.identity, material, 0, pass);
                if (backdrop != null) commands.DrawMesh(sheet, Matrix4x4.identity, backdrop, 0, 0);
                Graphics.ExecuteCommandBuffer(commands);

                var previous = RenderTexture.active;
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                RenderTexture.active = previous;
                return readback.GetPixels32();
            }

            public void Dispose()
            {
                for (int i = 0; i < Borrowed.Length; i++) Shader.SetGlobalVector(Borrowed[i], lent[i]);
                commands.Release();
                target.Release();
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(readback);
                Object.DestroyImmediate(backdrop);
                Object.DestroyImmediate(sheet);
            }
        }

        /// <summary>
        /// A closed ball around the origin. Closed, so whichever way a pass culls there is a
        /// side of it facing the view; around the origin, because that is where a locked mesh
        /// collapses to, in the middle of the picture.
        /// </summary>
        private static Mesh Ball()
        {
            const int rings = 10, segments = 16;
            const float radius = 0.4f;

            int count = (rings + 1) * (segments + 1);
            var vertices = new Vector3[count];
            var normals = new Vector3[count];
            var tangents = new Vector4[count];
            var uv = new Vector2[count];
            var colors = new Color32[count];

            for (int ring = 0; ring <= rings; ring++)
            for (int segment = 0; segment <= segments; segment++)
            {
                int i = ring * (segments + 1) + segment;
                float polar = Mathf.PI * ring / rings;
                float around = 2f * Mathf.PI * segment / segments;

                normals[i] = new Vector3(Mathf.Sin(polar) * Mathf.Cos(around), Mathf.Cos(polar),
                                         Mathf.Sin(polar) * Mathf.Sin(around));
                vertices[i] = normals[i] * radius;
                tangents[i] = new Vector4(-Mathf.Sin(around), 0f, Mathf.Cos(around), 1f);
                uv[i] = new Vector2((float)segment / segments, (float)ring / rings);
                colors[i] = new Color32(255, 255, 255, 255);
            }

            var triangles = new List<int>();
            for (int ring = 0; ring < rings; ring++)
            for (int segment = 0; segment < segments; segment++)
            {
                int a = ring * (segments + 1) + segment;
                int b = a + segments + 1;
                triangles.Add(a); triangles.Add(a + 1); triangles.Add(b);
                triangles.Add(a + 1); triangles.Add(b + 1); triangles.Add(b);
            }

            var mesh = new Mesh { name = "MeshProtectShaderCheck", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.uv = uv;
            mesh.colors32 = colors;
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
#endif
