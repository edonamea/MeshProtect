#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// Grafts this avatar's generated decode onto a shader family that is not lilToon.
    ///
    /// lilToon has an official extension mechanism - a .lilcontainer names the family in one line
    /// and every pass routes through LIL_CUSTOM_VERTEX_OS - so MeshProtectShaderGen can build a
    /// whole family without touching a byte of lilToon's own source. Nobody else ships one. For
    /// every other shader the only way in is to copy the source and edit it, which is what the
    /// previous generation of these tools did, and what their support matrices died of: they
    /// patched blind against literal anchors that moved with each upstream release, and a patch
    /// that half-applied produced an avatar that renders as noise for everyone, permanently.
    ///
    /// This does not patch against literal anchors. It reads the shader the way the compiler does:
    ///
    ///   every `#pragma vertex NAME` in the .shader is a pass that draws the mesh
    ///     -> find EVERY definition of NAME in its include tree, not the first
    ///     -> read each one's input struct and locate POSITION/NORMAL/TANGENT/TEXCOORD0
    ///        BY SEMANTIC, choosing the struct nearest that function in the include graph
    ///     -> add TEXCOORD6 for the amplitude, and decode at the top of the body
    ///   and then require that every single one of them was covered.
    ///
    /// That last line is the whole safety argument. A mesh is displaced permanently, so a pass
    /// that draws it without decoding draws noise; enumerating the passes from the one directive
    /// that defines them is what makes "we got them all" a fact rather than a hope. Anything this
    /// cannot do in full it refuses, and the material ships unprotected - which is this tool's
    /// rule everywhere: a check that fails degrades, it does not block the upload.
    ///
    /// Reading by semantic rather than by field name is what lets one implementation cover
    /// families that look nothing alike - Poiyomi's five identical appdata, Sunao's two different
    /// ones, GTAvaToon's three plus a `const` qualifier, XSToon's `centroid` modifiers.
    ///
    /// The patched copy goes into the variant's own folder under the build output. The author's
    /// installed shader is opened read-only and never written to.
    /// </summary>
    public static class MeshProtectForeignShader
    {
        private const string MarkerFile = "graft.txt";
        private const string DecodeInclude = "mp_decode.hlsl";
        private const string PropertyBlockToken = "//__MP_PROPERTIES__";

        /// <summary>The field this adds to every vertex input struct, carrying the amplitude.</summary>
        private const string AmplitudeField = "mpUv6";

        public class Recipe
        {
            public string id;
            public string displayName;

            /// <summary>
            /// Shader name prefixes this recipe claims, tested after stripping "Hidden/Locked/".
            /// Poiyomi's optimizer ("locking") writes a per-material copy of the shader under that
            /// prefix, and a locked material is the normal state of a shipped Poiyomi avatar - so a
            /// recipe that only matched the unlocked name would miss almost every real one.
            /// </summary>
            public string[] shaderNamePrefixes;

            /// <summary>
            /// Vertex entry points to leave alone, by name. Only for passes that provably do not
            /// draw the protected mesh - a fullscreen blit, a helper the family never binds to a
            /// renderer. Every name here is a hole in the coverage argument, so the list is empty
            /// unless there is a reason written next to it.
            /// </summary>
            public string[] skipEntryPoints = new string[0];
        }

        /// <summary>
        /// The families this can graft onto.
        ///
        /// These five are the ones Kanna/AntiRip covered, which is the market's own answer to
        /// which shaders matter. Every entry is a promise that a mesh displaced by this tool comes
        /// back correct in every pass, and the only thing that honours it is having read the
        /// source and run MPGraftCheck against it.
        /// </summary>
        public static readonly Recipe[] Recipes =
        {
            new Recipe
            {
                id = "poi",
                displayName = "Poiyomi Toon",
                shaderNamePrefixes = new[] { ".poiyomi/", "Poiyomi/" }
            },
            new Recipe
            {
                id = "xst",
                displayName = "Xiexe's Toon Shader",
                shaderNamePrefixes = new[] { "Xiexe/" }
            },
            new Recipe
            {
                id = "uts",
                displayName = "UnityChanToonShader",
                shaderNamePrefixes = new[] { "UnityChanToonShader/" }
            },
            new Recipe
            {
                id = "sun",
                displayName = "Sunao Shader",
                shaderNamePrefixes = new[] { "Sunao Shader/" }
            },
            new Recipe
            {
                id = "gta",
                displayName = "GTAvaToon",
                shaderNamePrefixes = new[] { "GeoTetra/GTAvaToon" }
            },

            // Not a toon shader - a stencil anti-clip shell, twelve passes of it, none of which
            // draw the mesh in the ordinary sense. Grafting it anyway is the point: an anti-clip
            // shell is usually an extra material slot re-drawing the body's last sub-mesh, and
            // that sub-mesh cannot be displaced while the slot over it has no decode. The two
            // alternatives are both compromises - leave it alone and lose the coverage, or put it
            // on Invisible Materials and have it write its mask from scrambled geometry. Carrying
            // the decode is the only answer that gives up neither.
            new Recipe
            {
                id = "bbd",
                displayName = "blackbody",
                shaderNamePrefixes = new[] { "wataameya/blackbody" }
            }
        };

        /// <summary>The recipe that claims this shader, or null.</summary>
        public static Recipe RecipeFor(Shader shader)
        {
            if (shader == null) return null;
            string name = StripLocked(shader.name);
            return Recipes.FirstOrDefault(
                r => r.shaderNamePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)));
        }

        private static string StripLocked(string name)
        {
            const string locked = "Hidden/Locked/";
            return name.StartsWith(locked, StringComparison.Ordinal)
                ? name.Substring(locked.Length) : name;
        }

        // ------------------------------------------------------------------ lookup and build

        /// <summary>
        /// The graft that already exists for this shader, or null.
        ///
        /// Look-up only, and that is the whole point of it being separate from EnsureGraft.
        /// Creating an asset while the SDK is building is the one thing this project has hard
        /// evidence against: an avatar built that way uploaded successfully and then could not
        /// stand up in game, and the identical code copying to a permanent folder beforehand was
        /// fine. The cause was never found, so the build only ever looks things up.
        /// </summary>
        public static Shader FindGraft(MeshProtectRoot settings, MeshProtectVariant variant,
                                       Shader source, out string why)
        {
            why = null;

            var recipe = RecipeFor(source);
            if (recipe == null)
            {
                why = $"'{source.name}' is not a shader family this tool knows how to graft onto";
                return null;
            }

            string sourcePath = RealPath(AssetDatabase.GetAssetPath(source));
            if (sourcePath == null)
            {
                why = $"'{source.name}' has no source file this build can read - it is built into " +
                      "Unity, or it lives somewhere the package manager does not unpack";
                return null;
            }

            string markerPath = Path.Combine(GraftFolder(settings, variant, recipe, sourcePath),
                                             MarkerFile);
            if (!File.Exists(markerPath) ||
                File.ReadAllText(markerPath).Trim() != Signature(variant, recipe, sourcePath))
            {
                why = $"this build has no prepared graft for '{source.name}' - press 'Rebuild " +
                      "Shader' on the Mesh Protect Root component and upload again. (Shaders are " +
                      "prepared before the build on purpose; making them during one has broken " +
                      "avatars in a way nobody has explained)";
                return null;
            }

            var grafted = FindCompiled(GraftedShaderName(variant, recipe, sourcePath));
            if (grafted == null)
                why = $"the prepared graft for '{source.name}' is not a shader Unity can use - it " +
                      "failed to compile, or its folder was deleted. Press 'Rebuild Shader' and " +
                      "watch the Console for a shader error";

            return grafted;
        }

        /// <summary>
        /// Build the graft if it is missing or stale. Editor-time only; see FindGraft for why the
        /// build never calls this. Returns true when something was written.
        /// </summary>
        public static bool EnsureGraft(MeshProtectRoot settings, MeshProtectVariant variant,
                                       Shader source, out string why, bool force = false)
        {
            why = null;

            var recipe = RecipeFor(source);
            if (recipe == null) return false;

            string sourcePath = RealPath(AssetDatabase.GetAssetPath(source));
            if (sourcePath == null)
            {
                why = $"'{source.name}' has no source file this build can read - it is built into " +
                      "Unity, or it lives somewhere the package manager does not unpack";
                return false;
            }

            string folder = GraftFolder(settings, variant, recipe, sourcePath);
            string signature = Signature(variant, recipe, sourcePath);
            string markerPath = Path.Combine(folder, MarkerFile);
            string shaderName = GraftedShaderName(variant, recipe, sourcePath);

            // The signature says the inputs are unchanged, which says nothing about whether the
            // shader Unity holds right now actually compiled. Ask for the shader as well.
            if (!force && File.Exists(markerPath) &&
                File.ReadAllText(markerPath).Trim() == signature &&
                FindCompiled(shaderName) != null)
                return false;

            var files = Collect(sourcePath, out why);
            if (files == null) return false;

            if (!Patch(files, recipe, shaderName, out why)) return false;

            foreach (var file in files)
                file.text = file.text.Replace(PropertyBlockToken, EmitProperties(variant));

            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);

            File.WriteAllText(Path.Combine(folder, DecodeInclude), EmitHeader(variant));
            foreach (var file in files)
            {
                string target = Path.Combine(folder, file.relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, file.text);
            }

            MeshProtectShaderGen.ImportGenerated(folder);

            if (FindCompiled(shaderName) == null)
            {
                why = $"the grafted copy of '{source.name}' did not compile - the Console will " +
                      "have the shader error above this. Nothing was changed about the original, " +
                      "and materials using it will simply ship unprotected";
                // No marker, so this is retried rather than remembered as done.
                return true;
            }

            File.WriteAllText(markerPath, signature);
            return true;
        }

        /// <summary>
        /// Prepare a graft for every foreign shader this avatar wears. Driven off the avatar
        /// rather than a list, because which shaders matter changes when the author changes an
        /// outfit.
        /// </summary>
        public static bool EnsureAllGrafts(MeshProtectRoot settings, MeshProtectVariant variant,
                                           GameObject avatar, List<string> problems,
                                           bool force = false)
        {
            bool wrote = false;
            foreach (var shader in ForeignShadersOn(avatar, variant))
            {
                if (EnsureGraft(settings, variant, shader, out string why, force)) wrote = true;
                if (why != null) problems?.Add(why);
            }
            return wrote;
        }

        /// <summary>
        /// The foreign shaders this avatar wears that have no usable graft prepared.
        ///
        /// Asked once, before the bake, for the same reason FamilyIsComplete is. An author who
        /// added a Poiyomi outfit after generating their password gets one warning per material
        /// otherwise - each individually unremarkable, none of them saying the cause is shared and
        /// is one button away. Creates nothing, so it is safe to call from inside a build.
        /// </summary>
        public static List<string> MissingGrafts(MeshProtectRoot settings,
                                                 MeshProtectVariant variant, GameObject avatar)
        {
            return ForeignShadersOn(avatar, variant)
                   .Where(s => FindGraft(settings, variant, s, out string _) == null)
                   .Select(s => s.name)
                   .ToList();
        }

        private static List<Shader> ForeignShadersOn(GameObject avatar, MeshProtectVariant variant)
        {
            var found = new List<Shader>();
            if (avatar == null) return found;

            var seen = new HashSet<Shader>();
            void Consider(Material material)
            {
                if (material == null || material.shader == null) return;
                if (RecipeFor(material.shader) == null) return;
                if (IsGraftedFamily(material, variant.shaderName)) return;
                if (seen.Add(material.shader)) found.Add(material.shader);
            }

            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            foreach (var material in renderer.sharedMaterials)
                Consider(material);

            // A material can reach the avatar through a wardrobe toggle alone, never sitting in a
            // renderer's slot in the scene. RewriteClipMaterials converts those too, so a foreign
            // one there needs a graft like any other - but this survey walked renderers only, so
            // none was ever prepared: the material shipped unprotected, and the warning naming it
            // sent the author to 'Rebuild Shader', which looks in this same place and would not
            // have found it either.
#if LILMP_VRCSDK3_AVATARS
            foreach (var clip in ClipsOn(avatar))
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (!binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal))
                    continue;
                // Guarded the way MeshProtectPipeline does it on the same call. The null is
                // documented, this survey only ever produces warnings, and an exception escaping
                // from here would surface instead as a REFUSED upload reading "Object reference
                // not set to an instance of an object".
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keys == null) continue;
                foreach (var key in keys)
                    Consider(key.value as Material);
            }
#endif
            return found;
        }

#if LILMP_VRCSDK3_AVATARS
        /// <summary>Every clip the avatar's own layers can play.</summary>
        private static IEnumerable<AnimationClip> ClipsOn(GameObject avatar)
        {
            var descriptor =
                avatar.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>()
                ?? avatar.GetComponentInChildren<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true);
            if (descriptor == null) yield break;

            var empty = new VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.CustomAnimLayer[0];
            foreach (var layer in (descriptor.baseAnimationLayers ?? empty)
                                  .Concat(descriptor.specialAnimationLayers ?? empty))
            {
                var controller = layer.animatorController;
                if (controller == null) continue;
                foreach (var clip in controller.animationClips)
                    if (clip != null) yield return clip;
            }
        }
#endif

        // ------------------------------------------------------------------ the file set

        private class GraftFile
        {
            public string relative;    // path inside the graft folder
            public string text;
            public bool isShader;

            /// <summary>Files this one includes, and files that include it. Undirected on
            /// purpose: a .cginc's types can come from either side - Sunao defines its vertex
            /// function in one file and the struct it takes in the file that includes it.</summary>
            public readonly List<GraftFile> neighbours = new List<GraftFile>();
        }

        /// <summary>
        /// The .shader and every local file it includes, transitively.
        ///
        /// Needed because only Poiyomi ships its passes in one file. Everyone else keeps the
        /// vertex function in a .cginc shared by several shaders, so patching the .shader alone
        /// would change nothing at all.
        ///
        /// Unity's own includes and package-rooted paths are left where they are - they resolve
        /// through the compiler's search path, and copying them would pin a version.
        /// </summary>
        private static List<GraftFile> Collect(string shaderPath, out string why)
        {
            why = null;
            var byFullPath = new Dictionary<string, GraftFile>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(Path.GetFullPath(shaderPath));

            while (queue.Count > 0)
            {
                string full = queue.Dequeue();
                if (byFullPath.ContainsKey(full)) continue;

                string text;
                try { text = File.ReadAllText(full); }
                catch (Exception e)
                {
                    why = $"could not read '{Path.GetFileName(full)}': {e.Message}";
                    return null;
                }

                byFullPath[full] = new GraftFile
                {
                    text = text,
                    isShader = full.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)
                };

                foreach (Match m in Regex.Matches(text, "#include\\s+\"([^\"]+)\""))
                {
                    string path = m.Groups[1].Value;
                    if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string resolved;
                    try { resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full), path)); }
                    catch { continue; }

                    if (File.Exists(resolved)) queue.Enqueue(resolved);
                }
            }

            // The copy reproduces the tree below the deepest folder that contains ALL of these
            // files, so every relative #include between them still resolves and none has to be
            // rewritten. That root is computed rather than assumed to be the .shader's own folder:
            // XSToon keeps its shaders in Main/Shaders and its includes in Main/CGIncludes, so
            // half the set sits above the .shader and the assumption refused the whole family.
            string root = CommonRoot(byFullPath.Keys.ToList());
            foreach (var pair in byFullPath)
                pair.Value.relative = Relative(root, pair.Key);

            // Second pass for the graph: every file is read by now, so both ends of each edge
            // exist to be linked.
            foreach (var pair in byFullPath)
            {
                string dir = Path.GetDirectoryName(pair.Key);
                foreach (Match m in Regex.Matches(pair.Value.text, "#include\\s+\"([^\"]+)\""))
                {
                    string resolved;
                    try { resolved = Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value)); }
                    catch { continue; }

                    if (!byFullPath.TryGetValue(resolved, out var other) || other == pair.Value)
                        continue;

                    if (!pair.Value.neighbours.Contains(other)) pair.Value.neighbours.Add(other);
                    if (!other.neighbours.Contains(pair.Value)) other.neighbours.Add(pair.Value);
                }
            }

            return byFullPath.Values.ToList();
        }

        // ------------------------------------------------------------------ the patch

        private class EntryPoint
        {
            public string name;
            public GraftFile file;
            public int bodyStart;      // just after the opening brace
            public string inputType;
            public string parameter;
        }

        /// <summary>
        /// Patch the whole file set, refusing unless every pass was covered.
        ///
        /// "Every pass" is not a number written here: it is every `#pragma vertex` in the .shader,
        /// which is the compiler's own definition of what draws. If any of them cannot be found,
        /// or its input struct cannot be read, or TEXCOORD6 is already taken, nothing is written
        /// at all - a graft that covers four passes of five is worse than no graft, because the
        /// mesh gets displaced either way and the fifth pass draws the noise.
        /// </summary>
        private static bool Patch(List<GraftFile> files, Recipe recipe, string shaderName,
                                  out string why)
        {
            why = null;

            var shader = files.FirstOrDefault(f => f.isShader);
            if (shader == null) { why = "there is no .shader in the copied set"; return false; }

            // A pass borrowed from ANOTHER shader by name. Enumerating "#pragma vertex" cannot see
            // it - the vertex program is not in this file or its includes - and the name it borrows
            // from still points at the author's own installed shader, which has no decode. So the
            // graft would patch the passes it can see, count them as complete, and ship a mesh that
            // four of UTS2's transparent variants then draw raw in three more passes. Compiles,
            // uploads, permanent noise.
            //
            // Rewriting the reference would mean grafting the borrowed shader too, resolving its
            // generated name, and handling the case where THAT one is refused - a whole dependency
            // graph for a handful of tessellation variants. Refusing is the honest price.
            if (Regex.IsMatch(shader.text, @"(?m)^\s*UsePass\s+"""))
            {
                why = $"this {recipe.displayName} shader borrows passes from another shader with " +
                      "UsePass, and those passes would draw the displaced mesh without a decode";
                return false;
            }

            var wanted = Regex.Matches(shader.text, @"#pragma\s+vertex\s+(\w+)")
                              .Cast<Match>()
                              .Select(m => m.Groups[1].Value)
                              .Where(n => !recipe.skipEntryPoints.Contains(n))
                              .Distinct()
                              .ToList();

            if (wanted.Count == 0)
            {
                why = $"this {recipe.displayName} file declares no vertex stage, so there is " +
                      "nowhere to put the decode";
                return false;
            }

            // GTAvaToon declares its vertex parameters `const`, and the decode assigns the
            // position back into them - "l-value specifies const object". Dropping the qualifier
            // is safe in a way editing around it would not be: it is a by-value parameter, so
            // const was never doing anything an outside caller could observe.
            //
            // Done before anything is located, so no offset measured later is invalidated by it.
            foreach (string name in wanted)
            foreach (var file in files)
                file.text = Regex.Replace(
                    file.text, @"(\b\w+\s+" + Regex.Escape(name) + @"\s*\(\s*)const\s+", "$1");

            if (!NoPassDrawsWithoutAProgram(shader, out why)) return false;
            if (!NormaliseBareParameters(files, wanted, out why)) return false;

            // EVERY definition of every entry point, not the first one found.
            //
            // Sunao binds "#pragma vertex vert" in all five of its passes and defines "vert" three
            // separate times, in the three .cginc files those passes include. Taking the first
            // match would have patched the base pass and left the outline and the shadow caster
            // drawing the displaced mesh raw - which compiles, uploads, and looks like noise
            // exactly where an author is least likely to test.
            var entries = new List<EntryPoint>();
            foreach (string name in wanted)
            {
                var found = FindEntryPoints(files, name);
                if (found.Count == 0)
                {
                    why = $"'{name}' is bound as a vertex stage but its definition is not in " +
                          $"{recipe.displayName}'s own files, so that pass could not be made to " +
                          "decode";
                    return false;
                }
                entries.AddRange(found);
            }

            // Likewise every definition of every input struct. Sunao has three "struct VIN" that
            // do not even agree on which attributes they carry.
            foreach (string type in entries.Select(e => e.inputType).Distinct())
            {
                if (!AddAmplitudeField(files, type, out string structWhy))
                {
                    why = $"{recipe.displayName}: {structWhy}";
                    return false;
                }
            }

            // Re-resolve. AddAmplitudeField just inserted into structs, and a struct usually sits
            // ABOVE the function that takes it in the same file - so every bodyStart measured
            // before that insertion now points short of where the body begins, and the decode
            // would have been spliced into the middle of a declaration.
            entries = wanted.SelectMany(n => FindEntryPoints(files, n)).ToList();

            // Backwards, so an earlier insertion does not move a later one's offset.
            foreach (var entry in entries.OrderByDescending(e => e.bodyStart))
            {
                // The struct nearest THIS function in the include graph. The field names are not
                // shared across definitions - Sunao's outline VIN has no tangent at all, so the
                // one this graft added for it is called something else than the base pass's own.
                var input = ReadStructNear(files, entry.inputType, entry.file);

                // All five, not just POSITION. AddAmplitudeField has already added whatever was
                // missing, so a gap here means the struct that got patched is not the one this
                // function actually sees - and the alternative to refusing is a KeyNotFound
                // thrown from the middle of a half-written graft.
                string absent = new[] { Position, Normal, Tangent, Uv0, "TEXCOORD6" }
                    .FirstOrDefault(s => input == null || !input.ContainsKey(s));
                if (absent != null)
                {
                    why = $"'{entry.name}' takes '{entry.inputType}', which still has no {absent} " +
                          "after patching - the definition it sees is not the one that was patched";
                    return false;
                }

                string call = $"\n\t{entry.parameter}.{input[Position]} = mpDecodeAppdata(" +
                              $"{entry.parameter}.{input[Normal]}, {entry.parameter}.{input[Tangent]}, " +
                              $"{entry.parameter}.{input[Uv0]}, {entry.parameter}.{input["TEXCOORD6"]}, " +
                              $"{entry.parameter}.{input[Position]});";
                entry.file.text = entry.file.text.Insert(entry.bodyStart, call);
            }

            // The decode has to be declared before the functions that call it, and every pass is
            // its own compilation unit. Where that declaration goes depends on the file:
            //
            //   .cginc/.hlsl - the top of the file. It is already inside a program block by the
            //                  time it is included.
            //   .shader      - just after each CGPROGRAM/HLSLPROGRAM. The top of a .shader is
            //                  ShaderLab, not HLSL, and an #include out there does not compile -
            //                  which matters because Poiyomi keeps its vertex functions in the
            //                  .shader itself.
            //
            // The include guard makes the overlap between the two harmless.
            foreach (var file in entries.Select(e => e.file).Distinct())
            {
                string include = "#include \"" +
                                 Relative(Path.GetDirectoryName(file.relative), DecodeInclude) +
                                 "\"";

                if (!file.isShader) { Prepend(file, include + "\n"); continue; }

                file.text = Regex.Replace(
                    file.text, @"(?m)^([ \t]*)(CGPROGRAM|HLSLPROGRAM)[ \t\r]*$",
                    m => m.Groups[1].Value + m.Groups[2].Value + "\n" +
                         m.Groups[1].Value + include);
            }

            // The declaration, rewritten whole. Not re-prefixed: keeping "Poiyomi Toon" on the end
            // would put the host's name in the bundle, which is one of the two strings a scanner
            // needs - the other being anything that marks the tool.
            var declaration = Regex.Match(shader.text, "(?m)^\\s*Shader\\s+\"[^\"]+\"");
            if (!declaration.Success)
            {
                why = "the copy has no Shader declaration to rename, so it would have collided " +
                      "with the author's own installed shader";
                return false;
            }
            shader.text = shader.text.Remove(declaration.Index, declaration.Length)
                                     .Insert(declaration.Index, "Shader \"" + shaderName + "\"");

            var properties = Regex.Match(shader.text, @"(?m)^([ \t]*)Properties[ \t]*\r?\n?[ \t]*\{");
            if (!properties.Success)
            {
                why = "the copy has no Properties block, so the material could not carry the key";
                return false;
            }
            shader.text = shader.text.Insert(properties.Index + properties.Length,
                                             "\n" + PropertyBlockToken + "\n");

            // ShaderLab's Fallback names a shader to use when none of this one's SubShaders can run
            // on the hardware. That shader has no decode, so falling back to it draws the displaced
            // mesh as a cloud of noise - and silently, because a fallback is not an error.
            //
            // Off instead. If the graft cannot run, drawing nothing is the better failure: it is
            // the same choice the generated materials already make with VRCFallback = Hidden, for
            // viewers who have custom shaders switched off. 68 of the shaders across these five
            // families carry one - Poiyomi and GTAvaToon are the only two that do not, which is why
            // the check that first grafted them did not notice.
            shader.text = Regex.Replace(shader.text, "(?im)^([ \t]*)Fallback\\s+\"[^\"]*\"",
                                        "$1Fallback Off");

            // A custom inspector that offers to regenerate the shader from its own source - Thry's
            // "lock" button on Poiyomi - would silently drop the graft. Nothing opens these
            // materials, but leaving the invitation in a file whose job is to stay patched is
            // asking for it.
            shader.text = Regex.Replace(shader.text, "(?m)^\\s*CustomEditor\\s+\"[^\"]+\"\\s*$", "");

            // Shader files ship with CRLF and everything inserted above uses LF, which Unity
            // reports as "inconsistent line endings" on every import - a warning in the author's
            // Console about a file they did not write and cannot fix.
            foreach (var file in files)
                if (file.text.Contains("\r\n"))
                    file.text = file.text.Replace("\r\n", "\n").Replace("\n", "\r\n");

            return true;
        }

        // ---- structural reading

        private const string Position = "POSITION";
        private const string Normal = "NORMAL";
        private const string Tangent = "TANGENT";
        private const string Uv0 = "TEXCOORD0";

        /// <summary>
        /// A vertex function's definition: where its body starts, and what it takes.
        ///
        /// Deliberately tolerant of the shapes these five families actually use - a `const`
        /// qualifier (GTAvaToon), `in`, and any amount of whitespace - and deliberately not a real
        /// parser. A signature it cannot read is refused, which costs one unprotected material.
        /// </summary>
        /// <summary>
        /// Refuse a shader that has a Pass with no program block, unless that pass provably draws
        /// nothing.
        ///
        /// This is a hole the `#pragma vertex` enumeration cannot see, and it is the worst kind:
        /// a Pass with no CGPROGRAM still renders. Unity supplies a default vertex stage that
        /// transforms POSITION and nothing else, so such a pass draws the displaced mesh with no
        /// decode - permanent noise - while every count this tool makes says full coverage,
        /// because there was no `#pragma vertex` to count.
        ///
        /// The exception is narrow and provable from the text rather than reasoned about: a
        /// stencil test of `Comp Never` always fails, so no colour, depth or stencil is written -
        /// provided the failure path is the default Keep. blackbody has exactly one such pass and
        /// it is inert; anything else is refused.
        /// </summary>
        private static bool NoPassDrawsWithoutAProgram(GraftFile shader, out string why)
        {
            why = null;

            // [ \t] rather than \s: with (?m) the anchor is a line start, but \s would then eat
            // blank lines and report a Pass several lines below the index it matched at, which
            // moves the chunk boundaries this splits on. The \r? is the CRLF these files ship with.
            //
            // "Pass" also names a stencil OPERATION - "Pass Replace" sits at line start inside
            // every Stencil block in blackbody. Requiring a brace or end-of-line straight after it
            // is what tells the two apart.
            var starts = Regex.Matches(shader.text, @"(?im)^[ \t]*Pass[ \t]*(\{|\r?$)")
                              .Cast<Match>().Select(m => m.Index).ToList();
            if (starts.Count == 0) return true;

            for (int i = 0; i < starts.Count; i++)
            {
                int end = i + 1 < starts.Count ? starts[i + 1] : shader.text.Length;
                string body = shader.text.Substring(starts[i], end - starts[i]);

                if (Regex.IsMatch(body, @"\b(CGPROGRAM|HLSLPROGRAM)\b")) continue;

                bool never = Regex.IsMatch(body, @"(?i)\bComp\s+Never\b");
                bool writesOnFailure = Regex.IsMatch(body, @"(?i)\b(Z?Fail(Front|Back)?)\s+(?!Keep\b)\w+");

                if (never && !writesOnFailure) continue;

                why = "one of its passes has no vertex program, so Unity draws it with the default " +
                      "one - which would put the displaced mesh on screen with no decode, and " +
                      "nothing here can inject a decode into a pass that has no program to inject " +
                      "into";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Turn `vert(float4 vertex : POSITION)` into `vert(mpBareN v)`, so everything downstream
        /// sees the struct it expects.
        ///
        /// A vertex function may take its attributes as loose parameters with inline semantics
        /// instead of a struct - blackbody does it in all twelve of its passes. There is nowhere
        /// to add TEXCOORD6 in that form, and no struct for the decode to read, so the family read
        /// as "no vertex function here" and was refused outright.
        ///
        /// Rewriting it into the struct form is a pre-pass rather than a special case threaded
        /// through the rest: after it runs, the amplitude field, the semantic lookup and the
        /// injection are the same code paths every other family uses. The original parameter is
        /// re-declared as a local of the same name and type, so the body needs no edits at all -
        /// and the decode, which is injected at the top of the body, runs BEFORE that local is
        /// taken, so the body reads the restored position.
        ///
        /// Only single-parameter functions. More than one loose attribute is a shape nothing here
        /// has needed yet, and guessing at it is how a graft half-applies.
        /// </summary>
        private static bool NormaliseBareParameters(List<GraftFile> files, List<string> wanted,
                                                    out string why)
        {
            why = null;
            int index = 0;

            foreach (string name in wanted)
            {
                var pattern = new Regex(
                    @"\b(\w+)\s+" + Regex.Escape(name) +
                    @"\s*\(\s*(?:const\s+|in\s+)*(\w+)\s+(\w+)\s*:\s*(\w+)\s*\)\s*(:\s*\w+)?\s*\{");

                foreach (var file in files)
                {
                    // One at a time, re-matching after each rewrite: the replacement is longer
                    // than what it replaces, so every offset behind it moves.
                    for (var match = pattern.Match(file.text); match.Success;
                         match = pattern.Match(file.text))
                    {
                        string type = match.Groups[2].Value;
                        string parameter = match.Groups[3].Value;
                        string semantic = match.Groups[4].Value;
                        string returns = match.Groups[5].Value;

                        string structName = "mpBare" + index++;
                        // The field on its own line. AddAmplitudeField inserts straight after the
                        // brace, so a one-line struct ends up with two declarations sharing a line
                        // - legal, but only readable to a reader that expects it.
                        string replacement =
                            $"struct {structName}\n{{\n\t{type} {parameter} : {semantic};\n}};\n" +
                            $"{match.Groups[1].Value} {name}({structName} mpIn) {returns} {{\n" +
                            $"\t{type} {parameter} = mpIn.{parameter};";

                        file.text = file.text.Remove(match.Index, match.Length)
                                             .Insert(match.Index, replacement);
                    }
                }
            }

            return true;
        }

        private static List<EntryPoint> FindEntryPoints(List<GraftFile> files, string name)
        {
            // The trailing "(?::\s*\w+\s*)?" is a RETURN semantic - "float4 vert(...) : SV_POSITION".
            // A vertex function that returns a bare value rather than a struct writes one, and
            // without this the whole family reads as "no vertex function here" and is refused.
            var pattern = new Regex(
                @"\b\w+\s+" + Regex.Escape(name) +
                @"\s*\(\s*(?:const\s+|in\s+)*(\w+)\s+(\w+)\s*\)\s*(?::\s*\w+\s*)?\{");

            var found = new List<EntryPoint>();
            foreach (var file in files)
            foreach (Match match in pattern.Matches(file.text))
            {
                found.Add(new EntryPoint
                {
                    name = name,
                    file = file,
                    bodyStart = match.Index + match.Length,
                    inputType = match.Groups[1].Value,
                    parameter = match.Groups[2].Value
                });
            }
            return found;
        }

        /// <summary>Every place this struct is defined, in file order.</summary>
        private static List<(GraftFile file, int open, int close)> StructSites(
            List<GraftFile> files, string type)
        {
            var pattern = new Regex(@"\bstruct\s+" + Regex.Escape(type) + @"\s*\{");
            var sites = new List<(GraftFile, int, int)>();

            foreach (var file in files)
            foreach (Match match in pattern.Matches(file.text))
            {
                int open = match.Index + match.Length;
                int close = file.text.IndexOf('}', open);
                if (close >= 0) sites.Add((file, open, close));
            }
            return sites;
        }

        /// <summary>
        /// A struct's fields, keyed by SEMANTIC rather than by name.
        ///
        /// By semantic because the names are not shared: "vertex" here, "positionOS" there, "uv"
        /// or "uv0" or "texcoord0". The semantic is what the GPU binds against and what the mesh
        /// actually fills, so it is the only thing these families agree on.
        /// </summary>
        private static Dictionary<string, string> ReadFields(string body)
        {
            return ReadFields(body, out List<string> _);
        }

        /// <summary>
        /// A struct's fields, and separately the semantics that only exist behind a preprocessor
        /// conditional.
        ///
        /// The distinction is load-bearing. UTS2's shadow caster declares its TEXCOORD0 inside
        /// `#ifdef _IS_CLIPPING_MODE` and repeats it in one more branch, with a third branch that
        /// declares nothing - so whether the field exists depends on a shader keyword. Reading the
        /// raw text sees it and emits a decode call naming it, which then fails to compile in the
        /// variant where it is absent: "invalid subscript 'texcoord0'".
        ///
        /// Adding one of our own does not fix it either. Two members cannot share an input
        /// semantic, so a field we add unconditionally collides in exactly the variants where
        /// theirs is present.
        /// </summary>
        private static Dictionary<string, string> ReadFields(string body, out List<string> conditional)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            conditional = new List<string>();

            int depth = 0;
            foreach (string line in body.Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("#if", StringComparison.Ordinal)) { depth++; continue; }
                if (trimmed.StartsWith("#endif", StringComparison.Ordinal))
                {
                    if (depth > 0) depth--;
                    continue;
                }
                if (trimmed.StartsWith("#el", StringComparison.Ordinal)) continue;

                // "centroid float2 uv : TEXCOORD0;" - modifiers before the type, semantic after.
                //
                // EVERY declaration on the line, not the first: two fields on one line is legal,
                // and reading only the first made a struct whose POSITION happened to sit second
                // look like it had none at all.
                foreach (Match m in Regex.Matches(line, @"(\w+)\s*:\s*([A-Za-z_]\w*)\s*;"))
                {
                    string semantic = Normalise(m.Groups[2].Value);
                    if (depth > 0)
                    {
                        if (!conditional.Contains(semantic)) conditional.Add(semantic);
                        continue;
                    }
                    if (!fields.ContainsKey(semantic)) fields[semantic] = m.Groups[1].Value;
                }
            }
            return fields;
        }

        /// <summary>
        /// TEXCOORD and TEXCOORD0 are the same register. Sunao writes the bare form, and reading
        /// it as its own semantic made this think TEXCOORD0 was missing - so it added one, giving
        /// the struct two fields on the same register, which does not compile.
        /// </summary>
        private static string Normalise(string semantic)
        {
            semantic = semantic.ToUpperInvariant();

            // POSITION and POSITION0 are the same register, as are NORMAL/NORMAL0 and
            // TANGENT/TANGENT0. GTAvaToon writes the indexed form throughout, and reading those as
            // separate semantics made this conclude its appdata had no POSITION at all.
            if (semantic.Length > 1 && semantic[semantic.Length - 1] == '0')
            {
                string bare = semantic.Substring(0, semantic.Length - 1);
                if (bare == Position || bare == Normal || bare == Tangent || bare == "COLOR")
                    return bare;
            }

            // TEXCOORD and TEXCOORD0 likewise. Sunao writes the bare form, and reading it as its
            // own semantic made this think TEXCOORD0 was missing - so it added one, giving the
            // struct two fields on the same register, which does not compile.
            return semantic == "TEXCOORD" ? Uv0 : semantic;
        }

        /// <summary>
        /// The definition of this struct nearest <paramref name="from"/> in the include graph.
        ///
        /// "Nearest" rather than "first" because several definitions of one name is normal and
        /// they do not have to agree: Sunao's three VIN carry different attributes, so a call
        /// generated from the wrong one names a field that pass does not have. Breadth-first over
        /// includes in both directions, because the struct can live in a file this one includes or
        /// in the file that includes this one.
        /// </summary>
        private static Dictionary<string, string> ReadStructNear(List<GraftFile> files, string type,
                                                                 GraftFile from)
        {
            var sites = StructSites(files, type);
            if (sites.Count == 0) return null;

            var seen = new HashSet<GraftFile> { from };
            var queue = new Queue<GraftFile>();
            queue.Enqueue(from);

            while (queue.Count > 0)
            {
                var file = queue.Dequeue();
                foreach (var site in sites)
                    if (site.file == file)
                        return ReadFields(file.text.Substring(site.open, site.close - site.open));

                foreach (var neighbour in file.neighbours)
                    if (seen.Add(neighbour)) queue.Enqueue(neighbour);
            }

            // Not reachable through includes at all. Unusual, but a single definition elsewhere is
            // still the only thing it can mean.
            var only = sites[0];
            return ReadFields(only.file.text.Substring(only.open, only.close - only.open));
        }

        /// <summary>
        /// Give a vertex input struct everything the decode reads.
        ///
        /// Missing attributes are ADDED rather than refused. A vertex input is bound by semantic,
        /// not by layout, so an extra one is free and Unity fills it from the mesh - and every
        /// family here is missing something somewhere: Sunao has a struct with no TEXCOORD0 and
        /// another with no TANGENT, UnlitWF makes its tangent conditional. Refusing on those would
        /// give up most of the coverage for a field that costs nothing to add.
        ///
        /// TEXCOORD6 is the exception. If it is already taken, the shader reads something out of
        /// the channel the amplitude goes into, and the bake would overwrite it - the same reason
        /// MeshProtectMesh refuses a mesh whose UV6 is occupied.
        /// </summary>
        private static bool AddAmplitudeField(List<GraftFile> files, string type, out string why)
        {
            why = null;

            var sites = StructSites(files, type);
            if (sites.Count == 0)
            {
                why = $"the vertex input struct '{type}' is not in this family's own files";
                return false;
            }

            // Backwards, so patching one site does not move the offsets of the ones before it.
            foreach (var site in sites.OrderByDescending(s => s.open))
            {
                var fields = ReadFields(site.file.text.Substring(site.open, site.close - site.open),
                                        out List<string> conditional);

                // A needed attribute that only exists in some compile-time branches has to be
                // made unconditional before anything can read it - see ReadFields and Hoist.
                foreach (string semantic in conditional.ToList())
                {
                    if (semantic != Position && semantic != Normal && semantic != Tangent &&
                        semantic != Uv0)
                        continue;

                    // The end is re-found each time: hoisting changes the body's length, so a
                    // struct with two conditional attributes would have handed the second call an
                    // end offset from before the first one edited it.
                    if (!Hoist(site.file, site.open, EndOfStruct(site.file, site.open), semantic,
                               out string hoistWhy))
                    {
                        why = $"'{type}': {hoistWhy}";
                        return false;
                    }

                    fields = ReadFields(site.file.text.Substring(site.open,
                                                                 EndOfStruct(site.file, site.open)
                                                                 - site.open));
                }

                if (fields.ContainsKey("TEXCOORD6"))
                {
                    why = $"'{type}' already reads TEXCOORD6, which is the channel the " +
                          "displacement amplitude goes into - protecting a mesh drawn by it would " +
                          "overwrite that";
                    return false;
                }
                if (!fields.ContainsKey(Position))
                {
                    why = $"'{type}' has no POSITION, so it is not a vertex input this can " +
                          "decode into";
                    return false;
                }

                var added = new StringBuilder();
                added.Append("\n\tfloat2 ").Append(AmplitudeField).Append(" : TEXCOORD6;");

                // Missing attributes are ADDED rather than refused. A vertex input is bound by
                // semantic, not by layout, so an extra one is free and Unity fills it from the
                // mesh - and most of these families are missing something somewhere: Sunao's
                // outline and shadow structs carry no tangent at all. Refusing on those would give
                // up most of the coverage over a field that costs nothing.
                if (!fields.ContainsKey(Normal)) added.Append("\n\tfloat3 mpNormal : NORMAL;");
                if (!fields.ContainsKey(Tangent)) added.Append("\n\tfloat4 mpTangent : TANGENT;");
                if (!fields.ContainsKey(Uv0)) added.Append("\n\tfloat2 mpUv0 : TEXCOORD0;");

                site.file.text = site.file.text.Insert(site.open, added.ToString());
            }

            return true;
        }

        private static int EndOfStruct(GraftFile file, int open)
        {
            int close = file.text.IndexOf('}', open);
            return close < 0 ? file.text.Length : close;
        }

        /// <summary>
        /// Lift a vertex attribute out of the #if branches it was declared in, so it exists in
        /// every variant.
        ///
        /// UTS2's shadow caster declares TEXCOORD0 twice, in two branches of a three-way chain
        /// whose third branch declares nothing - so the field exists or not depending on a
        /// keyword, and neither reading it nor adding one of our own works in both cases. Hoisting
        /// one copy out and deleting the conditional ones keeps the host's own field NAME, which
        /// is what the rest of its code refers to, and leaves the (now empty) branches in place
        /// because an empty #if branch is legal and rewriting the chain is not this tool's
        /// business.
        ///
        /// Only when every conditional declaration of that semantic is the same declaration.
        /// Branches that disagree mean the shader is doing something this cannot reason about.
        /// </summary>
        private static bool Hoist(GraftFile file, int open, int close, string semantic,
                                  out string why)
        {
            why = null;

            string body = file.text.Substring(open, close - open);
            var lines = body.Split('\n');

            var declarations = lines
                .Select((line, index) => (line, index))
                .Where(l =>
                {
                    var m = Regex.Match(l.line, @"(\w+)\s*:\s*([A-Za-z_]\w*)\s*;");
                    return m.Success && Normalise(m.Groups[2].Value) == semantic;
                })
                .ToList();

            if (declarations.Count == 0) { why = $"no {semantic} to lift out"; return false; }

            var distinct = declarations.Select(d => d.line.Trim()).Distinct().ToList();
            if (distinct.Count > 1)
            {
                why = $"its {semantic} is declared differently in different #if branches " +
                      $"({string.Join(" / ", distinct)}), so there is no single one to lift out";
                return false;
            }

            var kept = lines.ToList();
            foreach (var declaration in declarations.OrderByDescending(d => d.index))
                kept.RemoveAt(declaration.index);

            kept.Insert(0, "\n\t" + distinct[0]);

            file.text = file.text.Remove(open, close - open)
                                 .Insert(open, string.Join("\n", kept));
            return true;
        }

        private static void Prepend(GraftFile file, string text)
        {
            // Past a leading block comment - every one of these families opens with its licence,
            // and an #include above it is legal but reads as vandalism in a file we copied.
            int at = 0;
            var lead = Regex.Match(file.text, @"\A\s*/\*.*?\*/", RegexOptions.Singleline);
            if (lead.Success) at = lead.Length;

            file.text = file.text.Insert(at, "\n" + text);
        }

        // ------------------------------------------------------------------ emitters

        /// <summary>
        /// The decode, plus the property declarations lilToon's cbuffer would otherwise provide,
        /// plus the entry point the injected call uses.
        ///
        /// The decode body comes from MeshProtectShaderGen, unchanged and uncopied. That is not
        /// tidiness: the C# baker and the HLSL decode are two interpreters of one op list, and a
        /// second emitter for foreign hosts would be a third - free to drift from both, and
        /// drifting by one bit produces an avatar no password reopens.
        /// </summary>
        public static string EmitHeader(MeshProtectVariant variant)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// Generated by MeshProtect. Do not edit.");
            sb.AppendLine("// Grafted onto a shader family that is not lilToon; the decode below is the same");
            sb.AppendLine("// text the lilToon family gets, emitted from the same place.");
            sb.AppendLine("#ifndef MP_FOREIGN_DECODE_INCLUDED");
            sb.AppendLine("#define MP_FOREIGN_DECODE_INCLUDED");
            sb.AppendLine();

            // lilToon declares these through LIL_CUSTOM_PROPERTIES. A foreign host has no such
            // hook, so they are plain globals - which is what every built-in pipeline shader in
            // this list uses, and what the material binds against.
            sb.AppendLine($"float {variant.bypassProperty};");
            sb.AppendLine($"float {variant.modeProperty};");
            foreach (var property in variant.digitProperties)
                sb.AppendLine($"float {property};");
            sb.AppendLine($"float4 {variant.macProperty};");
            sb.AppendLine();

            sb.Append(MeshProtectShaderGen.EmitDecodeHlsl(variant));
            sb.AppendLine();

            // Returns the position rather than taking it inout, so the injected call is a single
            // assignment and does not care whether the host's field is float3 or float4.
            sb.AppendLine("float4 mpDecodeAppdata(float3 normalOS, float4 tangentOS, float2 uv0, float2 uv6, float4 positionOS)");
            sb.AppendLine("{");
            sb.AppendLine("    float3 p = positionOS.xyz;");
            sb.AppendLine("    lilMeshProtectDecode(normalOS, tangentOS, uv0, uv6, p);");
            sb.AppendLine("    return float4(p, positionOS.w);");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("float3 mpDecodeAppdata(float3 normalOS, float4 tangentOS, float2 uv0, float2 uv6, float3 positionOS)");
            sb.AppendLine("{");
            sb.AppendLine("    float3 p = positionOS;");
            sb.AppendLine("    lilMeshProtectDecode(normalOS, tangentOS, uv0, uv6, p);");
            sb.AppendLine("    return p;");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("#endif");
            return sb.ToString();
        }

        /// <summary>
        /// The Properties entries the material needs. Hidden, because a foreign host's inspector
        /// draws whatever it finds and these are all baker-owned: hand-editing any of them
        /// desynchronises the material from its baked mesh.
        /// </summary>
        public static string EmitProperties(MeshProtectVariant variant)
        {
            var sb = new StringBuilder();
            sb.AppendLine("        // MeshProtect (generated)");
            sb.AppendLine($"        [HideInInspector] {variant.bypassProperty} (\"\", Float) = 0");
            sb.AppendLine($"        [HideInInspector] {variant.modeProperty} (\"\", Float) = 0");
            foreach (var property in variant.digitProperties)
                sb.AppendLine($"        [HideInInspector] {property} (\"\", Float) = 0");
            sb.Append($"        [HideInInspector] {variant.macProperty} (\"\", Vector) = (0,0,0,0)");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ names and caching

        /// <summary>
        /// The family name a graft ships under.
        ///
        /// A suffix rather than the variant's own name, because two hosts cannot share a shader
        /// name and the lilToon family already holds that one. Drawn from the same pronounceable
        /// pool as everything else, and deliberately NOT "&lt;variant&gt;_poi": the family name
        /// travels inside the uploaded bundle, and "_poi" would tell a scanner both which shader
        /// the avatar really uses and that this tool processed it.
        /// </summary>
        public static string FamilyName(MeshProtectVariant variant, Recipe recipe)
        {
            return variant.shaderName + Token(recipe.id);
        }

        /// <summary>
        /// Unique per source file, which is not a detail: one avatar routinely wears several
        /// shaders of the same family, and one that survived an upgrade can carry two different
        /// VERSIONS whose files have the same name.
        /// </summary>
        private static string GraftedShaderName(MeshProtectVariant variant, Recipe recipe,
                                                string sourcePath)
        {
            return FamilyName(variant, recipe) + "/" + Token(SourceKey(sourcePath));
        }

        /// <summary>
        /// One folder per source shader. EnsureGraft clears it before writing, so a per-recipe
        /// folder meant the second shader of a family deleted the first - and Toon on the body
        /// with Two Pass on the hair is the ordinary case, not a corner one.
        /// </summary>
        private static string GraftFolder(MeshProtectRoot settings, MeshProtectVariant variant,
                                          Recipe recipe, string sourcePath)
        {
            return MeshProtectShaderGen.FolderFor(settings, variant) + "/" +
                   Token(recipe.id) + Token(SourceKey(sourcePath));
        }

        /// <summary>
        /// The real file behind an asset path, or null.
        ///
        /// "Packages/&lt;id&gt;/..." is a virtual path. For a VPM package it happens to be a real
        /// folder in the project too - which is how all five of these families normally install,
        /// and why this went unnoticed - but a package resolved from a registry or a git URL lives
        /// in Library/PackageCache under a versioned name, and File.Exists on the virtual path is
        /// simply false. That would have reported "no source file on disk to copy" about a shader
        /// sitting right there in the project window.
        /// </summary>
        public static string RealPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            if (File.Exists(assetPath)) return assetPath;

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            if (package == null) return null;

            string inside = assetPath.Substring(package.assetPath.Length).TrimStart('/');
            string full = Path.Combine(package.resolvedPath, inside);
            return File.Exists(full) ? full : null;
        }

        private static string SourceKey(string sourcePath)
        {
            string guid = AssetDatabase.AssetPathToGUID(sourcePath);
            return string.IsNullOrEmpty(guid) ? sourcePath.ToLowerInvariant() : guid;
        }

        internal static string Token(string seed)
        {
            const string consonants = "bcdfghjklmnprstvwz";
            const string vowels = "aeiou";

            unchecked
            {
                int hash = 17;
                foreach (char c in seed ?? "") hash = hash * 33 + c;

                var rng = new System.Random(hash);
                var chars = new char[6];
                for (int i = 0; i < chars.Length; i++)
                    chars[i] = i % 2 == 0
                        ? consonants[rng.Next(consonants.Length)]
                        : vowels[rng.Next(vowels.Length)];

                return new string(chars);
            }
        }

        /// <summary>
        /// Is this material on one of THIS avatar's grafted families?
        ///
        /// Every "is this ours" test in the pipeline goes through here. A graft it did not
        /// recognise would be converted a second time on the next bake - displacing an
        /// already-displaced mesh, which no password undoes.
        /// </summary>
        public static bool IsGraftedFamily(Material material, string baseFamily)
        {
            if (material == null || string.IsNullOrEmpty(baseFamily)) return false;

            string family = MeshProtectInspector.FamilyOf(material.shader);
            if (family == null) return false;

            return Recipes.Any(r => family == baseFamily + Token(r.id));
        }

        /// <summary>
        /// A shader Unity holds under this name AND has compiled. Shader.Find returns one that
        /// failed to compile just as happily as one that did, and a material moved onto a broken
        /// shader renders magenta rather than unprotected.
        /// </summary>
        internal static Shader FindCompiled(string name)
        {
            var shader = Shader.Find(name);
            if (shader == null) return null;

            foreach (var info in ShaderUtil.GetAllShaderInfo())
            {
                if (info.name != name) continue;
                return info.hasErrors || !info.supported ? null : shader;
            }
            return null;
        }

        /// <summary>Content identity of a graft: the variant, the recipe, and the host source.</summary>
        private static string Signature(MeshProtectVariant variant, Recipe recipe, string sourcePath)
        {
            var sb = new StringBuilder();
            sb.Append("recipe=").Append(recipe.id).Append(';');
            sb.Append("family=").Append(FamilyName(variant, recipe)).Append(';');

            // The host's own bytes. A shader upgrade under the author's feet has to invalidate
            // this: a stale graft would keep decoding with a copy of the old shader while the rest
            // of the avatar renders with the new one. Only the .shader is hashed - an include
            // changing without it is possible, and Rebuild Shader is the answer to that.
            using (var sha = SHA256.Create())
            {
                sb.Append("host=")
                  .Append(Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(sourcePath))))
                  .Append(';');
            }

            sb.Append("variant=").Append(MeshProtectShaderGen.VariantSignature(variant));
            return sb.ToString();
        }

        /// <summary>The deepest folder that contains every one of these files.</summary>
        private static string CommonRoot(List<string> paths)
        {
            var shared = Path.GetDirectoryName(paths[0]).Split('/', '\\').ToList();

            foreach (string path in paths.Skip(1))
            {
                var parts = Path.GetDirectoryName(path).Split('/', '\\');
                int keep = 0;
                while (keep < shared.Count && keep < parts.Length &&
                       string.Equals(shared[keep], parts[keep], StringComparison.OrdinalIgnoreCase))
                    keep++;
                shared.RemoveRange(keep, shared.Count - keep);
            }

            return string.Join("/", shared);
        }

        /// <summary>
        /// The path from one folder to one file, in the forward-slash form includes use. Written
        /// out rather than taken from Path.GetRelativePath, which Unity's profile does not
        /// reliably have.
        /// </summary>
        private static string Relative(string fromDir, string toPath)
        {
            if (string.IsNullOrEmpty(fromDir)) return toPath.Replace('\\', '/');

            var from = fromDir.TrimEnd('/', '\\').Split('/', '\\');
            var to = toPath.Split('/', '\\');

            int shared = 0;
            while (shared < from.Length && shared < to.Length &&
                   string.Equals(from[shared], to[shared], StringComparison.OrdinalIgnoreCase))
                shared++;

            var parts = new List<string>();
            for (int i = shared; i < from.Length; i++) parts.Add("..");
            for (int i = shared; i < to.Length; i++) parts.Add(to[i]);
            return string.Join("/", parts);
        }
    }
}
#endif
