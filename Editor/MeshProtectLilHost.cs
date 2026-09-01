#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
#if LILMP_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace MeshProtect
{
    /// <summary>
    /// lilToon custom shader families that are not stock lilToon: lilSSAO and lilSSRT today.
    ///
    /// These are built exactly the way this tool builds its own family - lilToon's official
    /// custom shader mechanism, a folder of .lilcontainer files beside lilCustomShaderDatas /
    /// Properties / Insert blocks and a custom.hlsl of LIL_CUSTOM_* macros. A material can only
    /// be on one such family at a time, so an avatar that used one used to lose something either
    /// way: lilSSAO's shader name begins "lilToon/", so it read as stock and the material was
    /// moved onto our family with its screen space occlusion silently gone, while lilSSRT was
    /// refused and shipped unprotected.
    ///
    /// Neither is necessary. The two sets of macros barely overlap: what the decode needs is
    /// LIL_CUSTOM_VERTEX_OS, and neither host writes it. So this emits a THIRD family - the
    /// host's own folder with the decode merged in - and the material moves onto that, keeping
    /// both. Where both sides define a macro the host's body is kept and ours chains onto it.
    ///
    /// Whitelisted rather than general, and a host that DOES write the vertex hook is refused,
    /// because such a host may itself be a mesh protection tool and displacing a mesh twice
    /// destroys it. That was the whole reason unknown custom families were left alone.
    /// </summary>
    public static class MeshProtectLilHost
    {
        private const string MarkerFile = "host.txt";

        /// <summary>
        /// The tessellation wiring record, written into the merged folder by PatchTessellation.
        /// "tessgen=1;ok" when every tessellating container was wired; "tessgen=1;deny=a,b,c"
        /// naming the family-relative suffixes that could not be, wrappers included.
        ///
        /// FindMerged never reads this, ON PURPOSE: a family merged by an older version keeps
        /// serving every material it already served, so an update never costs a working avatar
        /// its protection. MergedShaderFor reads it to decide tessellating variants, and
        /// EnsureMerged treats a missing or older record as "re-merge on the next Rebuild
        /// Shader" - which is how the wiring reaches existing users.
        /// </summary>
        private const string TessMarkerFile = "tess.txt";

        /// <summary>
        /// Bump when the wiring changes shape; stale records re-merge, not refuse. 2: deny
        /// entries are qualified by their sub-family segment ("GTAO/AOTessellation/Opaque"),
        /// because MergedShaderFor compares root-relative names and an unqualified entry could
        /// never match one - the reviewer's proof used the exact containers lilSSRT ships.
        /// </summary>
        private const int TessFormat = 2;

        /// <summary>The host's own LIL_CUSTOM_PROPERTIES, renamed so ours can chain onto it.</summary>
        private const string HostProperties = "LIL_MPHOST_PROPERTIES";

        /// <summary>
        /// Matched against the ShaderName the host declares in its own lilCustomShaderDatas
        /// block, so a sub-family (lilSSRT/GTAO, lilSSRT/RTAO) is covered by its parent's entry.
        /// The id is what the generated family name is derived from; it never reaches the
        /// uploaded avatar in readable form, exactly like the graft recipes' ids.
        /// </summary>
        private static readonly (string family, string id)[] Known =
        {
            ("lilToon/lilSSAO", "lilSSAO"),
            ("lilSSRT", "lilSSRT"),
        };

        public sealed class Host
        {
            public string family;      // ShaderName as the host declares it
            public string id;          // whitelist id, used for naming only
            public string folder;      // asset folder holding the containers and blocks
            public string realFolder;  // the same folder on disk - RealPath answers for files only
            public string editorName;  // the host's material inspector, kept so its UI survives
        }

        // ------------------------------------------------------------------ discovery

        private static List<Host> cache;
        private static double cachedAt;

        /// <summary>
        /// One content digest per host folder, keyed on what a directory listing already knows.
        /// Signature runs once per MATERIAL through FindMerged, and hashing a vendor folder of a
        /// few hundred files that many times is time spent to reach the same answer - lilSSRT is
        /// 244 files, and nothing stops a product shipping a demo scene or a video in there.
        /// The key is cheap (the enumeration carries length and mtime already) and the VALUE is
        /// still the content hash, so a git clone that rewrites every mtime pays for exactly one
        /// re-hash and then matches the marker as before.
        /// </summary>
        private static readonly Dictionary<string, (string key, string digest)> digests =
            new Dictionary<string, (string, string)>();

        /// <summary>
        /// Every whitelisted host family in the project, found by reading the ShaderName out of
        /// each lilCustomShaderDatas.lilblock. Reading the block rather than guessing from the
        /// shader name is what makes sub-families and renamed installs work.
        /// </summary>
        private static List<Host> All()
        {
            if (cache != null && EditorApplication.timeSinceStartup - cachedAt < 5.0) return cache;

            var found = new List<Host>();
            foreach (string guid in AssetDatabase.FindAssets("lilCustomShaderDatas"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/lilCustomShaderDatas.lilblock", StringComparison.Ordinal)) continue;

                string real = MeshProtectForeignShader.RealPath(path);
                if (real == null || !File.Exists(real)) continue;

                string text;
                try { text = File.ReadAllText(real); } catch { continue; }

                string family = Field(text, "ShaderName");
                if (string.IsNullOrEmpty(family)) continue;

                // Only the root of a product, never a sub-family: lilSSRT/GTAO lives inside
                // lilSSRT's folder and includes "../custom.hlsl", so it is not self-contained
                // and must be merged as part of its parent rather than on its own.
                string id = null;
                foreach (var k in Known)
                    if (family == k.family) id = k.id;
                if (id == null) continue;

                found.Add(new Host
                {
                    family = family,
                    id = id,
                    folder = path.Substring(0, path.LastIndexOf('/')),
                    realFolder = Path.GetDirectoryName(real),
                    editorName = Field(text, "EditorName"),
                });
            }

            // Two installs of the same product - an old copy left behind after re-importing
            // into a different folder - would otherwise make the merge depend on scan order.
            // Grouped, not compared pairwise down a sorted list: with two products installed the
            // duplicates of one need not land next to each other, and the pass that only looked
            // at neighbours would keep both and warn about neither.
            found.Sort((a, b) => string.CompareOrdinal(a.folder, b.folder));
            var byFamily = new Dictionary<string, Host>(StringComparer.Ordinal);
            foreach (var host in found)
            {
                if (byFamily.TryGetValue(host.family, out var kept))
                {
                    Debug.LogWarning(
                        $"[MeshProtect] '{host.family}' is installed more than once ('{kept.folder}' " +
                        $"and '{host.folder}'). The first is used; remove the other, because a " +
                        "project with two copies also has duplicate shader names.");
                    continue;
                }
                byFamily[host.family] = host;
            }

            cache = byFamily.Values.ToList();
            cachedAt = EditorApplication.timeSinceStartup;
            return cache;
        }

        public static void Forget() { cache = null; digests.Clear(); }

        /// <summary>The host family a material's shader belongs to, or null.</summary>
        public static Host HostFor(Shader shader)
        {
            if (shader == null) return null;

            string name = shader.name;
            if (name.StartsWith("Hidden/", StringComparison.Ordinal))
                name = name.Substring("Hidden/".Length);

            // A sub-family resolves to its PARENT: "lilSSRT/GTAO/lilToon" is merged as part of
            // the lilSSRT folder, because GTAO includes "../custom.hlsl" and is not self-contained
            // on its own. All() keeps root families only, so at most one entry can match.
            foreach (var h in All())
                if (name == h.family || name.StartsWith(h.family + "/", StringComparison.Ordinal))
                    return h;
            return null;
        }

        /// <summary>The generated family name. Says nothing about the host, like a graft's.</summary>
        public static string FamilyName(MeshProtectVariant variant, Host host)
        {
            return variant.shaderName + MeshProtectForeignShader.Token("lilhost:" + host.family);
        }

        public static bool IsMergedFamily(Material material, string baseFamily)
        {
            if (material == null || string.IsNullOrEmpty(baseFamily)) return false;

            string family = MeshProtectInspector.FamilyOf(material.shader);
            if (family == null) return false;

            return All().Any(h => family == baseFamily +
                                  MeshProtectForeignShader.Token("lilhost:" + h.family));
        }

        // ------------------------------------------------------------------ look-up

        /// <summary>
        /// The merged family for this material's host, if it has already been written. Look-up
        /// only - the build must not generate shaders while it is baking.
        /// </summary>
        public static Shader FindMerged(MeshProtectRoot settings, MeshProtectVariant variant,
                                        Shader shader, out string why)
        {
            why = null;
            var host = HostFor(shader);
            if (host == null)
            {
                why = "not a lilToon custom family this tool knows how to merge with";
                return null;
            }

            string marker = Path.Combine(Folder(settings, variant, host), MarkerFile);
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != Signature(variant, host))
            {
                why = $"the merged '{host.family}' family has not been prepared for this avatar - " +
                      "press 'Rebuild Shader' on the Mesh Protect Root component";
                return null;
            }

            var found = MeshProtectForeignShader.FindCompiled(FamilyName(variant, host) + "/lilToon");
            if (found == null)
                why = $"the merged '{host.family}' family did not compile";
            return found;
        }

        /// <summary>
        /// The merged family's counterpart of one host shader. The merged family is built from
        /// the host's own containers, so the two carry the same shader names under a different
        /// family segment and the mapping is exact: "Hidden/lilToon/lilSSAO/Cutout" becomes
        /// "Hidden/&lt;merged&gt;/Cutout". lilToon's own converter cannot do this - it maps from
        /// stock lilToon and leaves a material that already sits on a custom family alone.
        /// </summary>
        public static Shader MergedShaderFor(MeshProtectRoot settings, MeshProtectVariant variant,
                                             Host host, Shader source)
        {
            if (host == null || source == null) return null;

            string name = source.name;
            bool hidden = name.StartsWith("Hidden/", StringComparison.Ordinal);
            if (hidden) name = name.Substring("Hidden/".Length);

            if (name == host.family)
                return MeshProtectForeignShader.FindCompiled(FamilyName(variant, host));
            if (!name.StartsWith(host.family + "/", StringComparison.Ordinal))
                return null;

            string suffix = name.Substring(host.family.Length + 1);

            // Tessellation. lilToon's domain shader interpolates the appdata and only then calls
            // vert(), where the decode runs - and vertex identity is the raw bit pattern of UV0,
            // so every vertex the tessellator invents would land somewhere arbitrary. The answer,
            // same as the generated family's, is to decode in vertTess, one stage earlier; for a
            // merged family that wiring is done by PatchTessellation on the copied containers,
            // and whether it happened is recorded in tess.txt at merge time:
            //
            //   No record - the family was merged before the wiring existed. Refuse by name,
            //   exactly as that version did; a Rebuild Shader re-merges and the record appears.
            //   FindMerged still accepts the family for everything else, so updating this tool
            //   never costs an already-working avatar its protection.
            //
            //   Record present - the deny list is authoritative, and the name test is not
            //   consulted. The list names the variants whose containers could not be wired, and
            //   the wrappers that ride on them, WHATEVER they are called: a tessellating
            //   container under a name IsTessellating does not match is precisely the case a
            //   name test would wave through shattered.
            var denied = TessWiring(settings, variant, host);
            if (denied == null)
            {
                if (IsTessellating(name)) return null;
            }
            else if (denied.Contains(suffix) ||
                     (denied.Contains("*") && IsTessellating(name)))
            {
                // "*" is the wiring pass saying it met a failure it could not even name -
                // degrade to the pre-record name refusal rather than trust an "ok" that is not.
                return null;
            }

            // Shader.Find hands back a shader that failed to compile just as readily as one that
            // worked, and a material moved onto a broken shader passes every name-based check on
            // its way to an avatar that renders as nothing.
            return MeshProtectForeignShader.FindCompiled(
                (hidden ? "Hidden/" : "") + FamilyName(variant, host) + "/" + suffix);
        }

        // ------------------------------------------------------------------ generation

        /// <summary>Write the merged family for one host. Returns true when it wrote.</summary>
        public static bool EnsureMerged(MeshProtectRoot settings, MeshProtectVariant variant,
                                        Host host, out string why, bool force = false)
        {
            why = null;
            string folder = Folder(settings, variant, host);
            string signature = Signature(variant, host);

            // The tess record is part of "current": a family merged before the wiring existed
            // is complete and correct for everything it already served, but the next rebuild
            // owes it a re-merge so its tessellating variants stop being refused.
            if (!force && File.Exists(Path.Combine(folder, MarkerFile))
                && File.ReadAllText(Path.Combine(folder, MarkerFile)).Trim() == signature
                && MeshProtectForeignShader.FindCompiled(FamilyName(variant, host) + "/lilToon") != null
                && TessRecordCurrent(folder))
                return false;

            string hostReal = host.realFolder;
            if (hostReal == null || !Directory.Exists(hostReal))
            {
                why = $"'{host.family}' is installed somewhere this cannot read ({host.folder})";
                return false;
            }

            string customPath = Path.Combine(hostReal, "custom.hlsl");
            string hostCustom = File.Exists(customPath) ? File.ReadAllText(customPath) : "";

            // A host that moves vertices itself is the case the old refusal existed for.
            if (DefinesMacro(hostCustom, "LIL_CUSTOM_VERTEX_OS"))
            {
                why = $"'{host.family}' writes LIL_CUSTOM_VERTEX_OS, so it moves vertices itself. " +
                      "Merging the decode into it could displace the same mesh twice, which " +
                      "nothing can undo, so the material was left alone.";
                return false;
            }

            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);

            // Everything the host ships except the pieces this rewrites, and except .meta files:
            // copying those would put a second asset on every one of their GUIDs.
            var rewritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "custom.hlsl", "custom_insert.hlsl",
                "lilCustomShaderDatas.lilblock", "lilCustomShaderProperties.lilblock",
            };
            foreach (string src in Directory.GetFiles(hostReal, "*", SearchOption.AllDirectories))
            {
                string rel = src.Substring(hostReal.Length).TrimStart('\\', '/');
                if (rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                if (rewritten.Contains(rel)) continue;

                string dst = Path.Combine(folder, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
            }

            PatchNestedFamilies(variant, host, folder, FamilyName(variant, host));
            PatchTessellation(variant, folder, FamilyName(variant, host));
            WriteMerged(variant, host, hostReal, folder, hostCustom);

            AssetDatabase.ImportAsset(folder, ImportAssetOptions.ImportRecursive |
                                              ImportAssetOptions.ForceSynchronousImport);

            // The marker goes down only once the family is on disk AND compiled, so a partial one
            // is retried rather than remembered as done. Without the count, Rebuild Shader reports
            // success over a family that is missing shaders and the author meets it later as one
            // unexplained warning per material.
            // Counted, and SAID, but not used to withhold the marker. Withholding it would make
            // one uncompilable variant - a tessellation container this build refuses to use
            // anyway - cost every other material on the avatar its protection, which is the
            // opposite of how everything else here fails. The per-material look-up goes through
            // FindCompiled, so a variant that really is missing is refused on its own.
            int expected = Directory.GetFiles(folder, "*.lilcontainer",
                                              SearchOption.AllDirectories).Length;
            int compiled = CompiledCount(FamilyName(variant, host));
            if (compiled < expected)
                why = $"the merged '{host.family}' family has {compiled} usable shader(s) where " +
                      $"{expected} are expected - this project's lilToon and that product may be " +
                      "from versions that do not match. Materials on the variants that did not " +
                      "compile ship unprotected; the rest are protected as usual.";

            File.WriteAllText(Path.Combine(folder, MarkerFile), signature);
            return true;
        }

        /// <summary>Prepare a merged family for every host family this avatar actually uses.</summary>
        public static bool EnsureAllMerged(MeshProtectRoot settings, MeshProtectVariant variant,
                                           GameObject avatar, List<string> problems,
                                           bool force = false)
        {
            bool wrote = false;
            foreach (var host in HostsUsedBy(avatar))
            {
                if (EnsureMerged(settings, variant, host, out string why, force)) wrote = true;
                if (why != null) problems?.Add(why);
            }
            return wrote;
        }

        /// <summary>Host families this avatar uses that have no prepared merge yet.</summary>
        public static List<string> MissingMerged(MeshProtectRoot settings,
                                                 MeshProtectVariant variant, GameObject avatar)
        {
            var missing = new List<string>();
            foreach (var host in HostsUsedBy(avatar))
            {
                string marker = Path.Combine(Folder(settings, variant, host), MarkerFile);
                if (!File.Exists(marker)
                    || File.ReadAllText(marker).Trim() != Signature(variant, host)
                    || MeshProtectForeignShader.FindCompiled(FamilyName(variant, host) + "/lilToon") == null)
                    missing.Add(host.family);
            }
            return missing;
        }

        private static IEnumerable<Host> HostsUsedBy(GameObject avatar)
        {
            var seen = new Dictionary<string, Host>();
            if (avatar == null) return seen.Values;

            void Take(Material material)
            {
                var host = material == null ? null : HostFor(material.shader);
                if (host != null && !seen.ContainsKey(host.family)) seen[host.family] = host;
            }

            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.sharedMaterials)
                    Take(material);

#if LILMP_VRCSDK3_AVATARS
            // A host material can live only inside an animation - an outfit toggle that swaps a
            // lilSSRT material onto a renderer whose own material is stock lilToon. Walking
            // renderers alone never saw it, so nothing was prepared for that family and the
            // material's own warning sent the author to a button that would not have found it
            // either. The build converts clip-referenced materials, so the survey must see them.
            // The parameterless overload skips inactive objects, and an avatar parked disabled
            // in the scene is the normal editing state - so on a disabled avatar this returned
            // null and no merged family was prepared, while the graft path's identical sweep
            // found the descriptor through its fallback and prepared its half. The build then
            // named those materials as shipping unprotected and pointed at the button that had
            // just run and reported success.
            var descriptor = avatar.GetComponentInParent<VRCAvatarDescriptor>()
                             ?? avatar.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (descriptor != null)
            {
                var layers = (descriptor.baseAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
                    .Concat(descriptor.specialAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0]);

                foreach (var layer in layers)
                {
                    if (layer.animatorController == null) continue;
                    foreach (var clip in layer.animatorController.animationClips)
                    {
                        if (clip == null) continue;
                        foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        {
                            if (!binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal))
                                continue;
                            var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                            if (keys == null) continue;
                            foreach (var key in keys) Take(key.value as Material);
                        }
                    }
                }
            }
#endif
            return seen.Values;
        }

        // ------------------------------------------------------------------ the merge itself

        private static void WriteMerged(MeshProtectVariant variant, Host host,
                                        string hostReal, string real, string hostCustom)
        {
            bool hostProps = DefinesMacro(hostCustom, "LIL_CUSTOM_PROPERTIES");

            // custom.hlsl - the host's macros, then ours. Only LIL_CUSTOM_PROPERTIES needs
            // chaining: the rest of ours are either flags or macros the host leaves alone.
            var sb = new StringBuilder();
            sb.AppendLine("// " + host.family + ", merged with MeshProtect's decode. Generated; do not edit.");
            sb.AppendLine();
            sb.AppendLine(hostProps ? hostCustom.Replace("LIL_CUSTOM_PROPERTIES", HostProperties)
                                    : hostCustom);
            sb.AppendLine();
            sb.AppendLine(OurMacros(variant, hostProps,
                                    DefinesMacro(hostCustom, "LIL_CUSTOM_TEXTURES"),
                                    DefinesMacro(hostCustom, "LIL_CUSTOM_VERT_COPY")));
            File.WriteAllText(Path.Combine(real, "custom.hlsl"), sb.ToString());

            // custom_insert.hlsl - their helpers, then the decode. Their names are their own
            // (lilSSAO_*, CalcFrustumCorrection); ours are all lilMP*, so nothing collides.
            string hostInsertPath = Path.Combine(hostReal, "custom_insert.hlsl");
            string hostInsert = File.Exists(hostInsertPath) ? File.ReadAllText(hostInsertPath) : "";
            File.WriteAllText(Path.Combine(real, "custom_insert.hlsl"),
                              hostInsert + "\n\n" + MeshProtectShaderGen.EmitDecodeHlsl(variant));

            // Property declarations concatenate cleanly - they are a list, not a macro.
            string hostPropsPath = Path.Combine(hostReal, "lilCustomShaderProperties.lilblock");
            string hostPropsText = File.Exists(hostPropsPath) ? File.ReadAllText(hostPropsPath) : "";
            File.WriteAllText(Path.Combine(real, "lilCustomShaderProperties.lilblock"),
                              hostPropsText + "\n" + MeshProtectShaderGen.EmitProperties(variant));

            // Our family name, their inspector: the author keeps the UI they paid for, and the
            // generated properties simply appear under it.
            string editor = string.IsNullOrEmpty(host.editorName)
                ? "MeshProtect.MeshProtectInspector" : host.editorName;
            File.WriteAllText(Path.Combine(real, "lilCustomShaderDatas.lilblock"),
                              $"ShaderName \"{FamilyName(variant, host)}\"\nEditorName \"{editor}\"\n");
        }

        /// <summary>
        /// Sub-families copied in with the host - lilSSRT's GTAO and RTAO - each declare their
        /// own ShaderName. Left as they are, our copy would generate a second set of shaders
        /// under the host's own names and shadow the installed ones. They are renamed onto the
        /// merged family instead, which also carries the decode into them: their custom.hlsl
        /// includes "../custom.hlsl", and that one is now the merged copy.
        /// </summary>
        private static void PatchNestedFamilies(MeshProtectVariant variant, Host host,
                                                string real, string mergedFamily)
        {
            // Each sub-family resolves lilProperties against ITS OWN folder, so lilSSRT ships one
            // copy of the properties block per sub-family and the merged root's copy never reaches
            // them. Without this a GTAO or RTAO material lands on a shader that declares none of
            // the decode's properties: the digits and the verifier read zero, no password opens
            // it, and that mesh ships permanently invisible.
            // Only the nested copies are here to be found: the copy loop skips the two root
            // blocks by name, and WriteMerged writes them after this has already run.
            foreach (string file in Directory.GetFiles(real, "lilCustomShaderProperties.lilblock",
                                                       SearchOption.AllDirectories))
            {
                File.AppendAllText(file, "\n" + MeshProtectShaderGen.EmitProperties(variant));
            }

            foreach (string file in Directory.GetFiles(real, "lilCustomShaderDatas.lilblock",
                                                       SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                string family = Field(text, "ShaderName");
                if (family == null) continue;

                string renamed = family == host.family ? mergedFamily
                    : family.StartsWith(host.family + "/", StringComparison.Ordinal)
                        ? mergedFamily + family.Substring(host.family.Length)
                        : null;
                if (renamed == null) continue;

                File.WriteAllText(file, text.Replace("\"" + family + "\"", "\"" + renamed + "\""));
            }
        }


        /// <summary>
        /// Wire the vertTess decode into every tessellating container of the merged copy, and
        /// record the outcome in tess.txt.
        ///
        /// The pieces were all shipped by 1.2.0; this only connects them inside a folder this
        /// tool already owns. The merged custom_insert.hlsl carries the rename
        /// ("#define vertTess lilMPVertTessOriginal") gated on LILMP_TESS_POST, and EmitTessHlsl
        /// is the restore-and-override that decodes once per ORIGINAL vertex and zeroes uv6 so
        /// the domain shader's copy of the decode early-outs. So per tessellating container:
        /// define LILMP_TESS_POST in its HLSLINCLUDE (arming the rename), and put the override
        /// where the pass's *LIL_SUBSHADER_INSERT_POST* will paste it - appended to the host's
        /// own Post block where one exists (lilSSRT's AO containers, whose ssrt_tessellation.hlsl
        /// declares the same pass-through vertTess lilToon's does, and re-defines
        /// LIL_TESSELLATION_INCLUDED at its top so the guard holds), or in a Post block created
        /// beside the container where none does (the stock DefaultTessellation ones).
        ///
        /// What tessellates is decided by evidence, not by name: a container is tessellating
        /// when a subshader block it references is named *Tessellation* or, for a block file
        /// shipped by the host, when that file carries a hull stage. And the one assumption
        /// whose breakage would be SILENT - a hull pass entered through something other than
        /// vertTess, where the override compiles as a function nobody calls and the mesh ships
        /// shattered with the right password - is checked here in plain text: every host block
        /// with N hull stages must name vertTess as the vertex entry N times, or the container
        /// is not wired and its variants (and the wrappers riding on them) go on the deny list.
        /// Blocks that live in lilToon rather than the host cannot be read here; for those the
        /// sentinel already makes a vertTess rename a compile error.
        ///
        /// Everything here happens before the single ImportAsset in EnsureMerged, so wiring
        /// costs no extra import and no extra compile.
        /// </summary>
        private static void PatchTessellation(MeshProtectVariant variant, string folder,
                                              string mergedFamily)
        {
            string tessText = MeshProtectShaderGen.EmitTessHlsl(variant);
            var appended = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failedPasses = new List<string>();
            bool denyAll = false;

            foreach (string path in Directory.GetFiles(folder, "*.lilcontainer",
                                                       SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(path);
                string dir = Path.GetDirectoryName(path);
                if (!ContainerTessellates(dir, text)) continue;

                string why = WireTessContainer(path, dir, text, tessText, appended);
                if (why == null) continue;

                // Recorded ROOT-relative - "GTAO/ltspass_aotess_opaque", not the container's own
                // "ltspass_aotess_opaque" - because MergedShaderFor compares suffixes relative to
                // the root family (HostFor resolves a sub-family to its parent). An unqualified
                // entry can never match one, and lilSSRT's AO tessellation lives ENTIRELY in
                // sub-families. A failure that cannot even be named goes to deny-everything.
                string suffix = QualifiedSuffix(folder, mergedFamily, path, text);
                if (suffix == null) denyAll = true; else failedPasses.Add(suffix);
                Debug.LogWarning($"[MeshProtect] Tessellating container " +
                                 $"'{Path.GetFileName(path)}' was not wired for the decode: {why}. " +
                                 "Materials on its variants ship unprotected and intact.");
            }

            var denied = new List<string>(failedPasses);
            if (failedPasses.Count > 0)
            {
                // A wrapper is just a name and a UsePass; a material sits on the wrapper. If the
                // pass container under it could not be wired, the wrapper's variant is exactly as
                // broken, whatever it is called. Matched on the QUALIFIED name, exactly: the
                // root, GTAO and RTAO wrappers all reference an identically-named pass container
                // in their own folder, and an ends-with match would deny the wired root variant
                // for a sub-family failure while the broken sub-family one sailed through.
                foreach (string path in Directory.GetFiles(folder, "*.lilcontainer",
                                                           SearchOption.AllDirectories))
                {
                    string text = File.ReadAllText(path);
                    string passRef = QuotedLineValue(text, "lilPassShaderName");
                    if (passRef == null) continue;
                    const string tag = "*LIL_SHADER_NAME*/";
                    int at = passRef.IndexOf(tag, StringComparison.Ordinal);
                    if (at < 0) continue;
                    string dir = Path.GetDirectoryName(path);
                    string prefix = SubFamilyPrefix(folder, mergedFamily, dir);
                    if (prefix == null) continue;
                    if (!failedPasses.Contains(prefix + passRef.Substring(at + tag.Length)))
                        continue;
                    string suffix = QualifiedSuffix(folder, mergedFamily, path, text);
                    if (suffix == null) denyAll = true; else denied.Add(suffix);
                }
            }

            if (denyAll) denied.Add("*");
            File.WriteAllText(Path.Combine(folder, TessMarkerFile),
                "tessgen=" + TessFormat + ";" +
                (denied.Count == 0 ? "ok" : "deny=" + string.Join(",", denied.Distinct())));
        }

        /// <summary>
        /// The prefix that lifts a container-relative name into the root family's namespace:
        /// "" at the root, "GTAO/" inside the GTAO sub-family. Read from the governing
        /// lilCustomShaderDatas.lilblock, which PatchNestedFamilies has already renamed onto the
        /// merged family - so the prefix is exactly the segment MergedShaderFor's suffixes carry.
        /// Null when no governing block resolves to the merged family: such a container's
        /// variants cannot be addressed, and the caller must deny everything rather than guess.
        /// </summary>
        private static string SubFamilyPrefix(string folder, string mergedFamily, string dir)
        {
            try
            {
                string root = Path.GetFullPath(folder);
                for (string d = dir; d != null; d = Directory.GetParent(d)?.FullName)
                {
                    string datas = Path.Combine(d, "lilCustomShaderDatas.lilblock");
                    if (File.Exists(datas))
                    {
                        string family = Field(File.ReadAllText(datas), "ShaderName");
                        if (family == mergedFamily) return "";
                        if (family != null &&
                            family.StartsWith(mergedFamily + "/", StringComparison.Ordinal))
                            return family.Substring(mergedFamily.Length + 1) + "/";
                        return null;
                    }
                    if (string.Equals(Path.GetFullPath(d), root, StringComparison.OrdinalIgnoreCase))
                        return "";   // the root's own Datas block is written later, by WriteMerged
                }
            }
            catch { /* fall through to null */ }
            return null;
        }

        private static string QualifiedSuffix(string folder, string mergedFamily,
                                              string path, string text)
        {
            string prefix = SubFamilyPrefix(folder, mergedFamily, Path.GetDirectoryName(path));
            string suffix = ShaderSuffix(text);
            return prefix == null || suffix == null ? null : prefix + suffix;
        }

        private static bool ContainerTessellates(string dir, string text)
        {
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimStart();
                if (!line.StartsWith("lilSubShader", StringComparison.Ordinal)) continue;
                string value = Quoted(line);
                if (value == null) continue;
                if (value.Contains("Tessellation")) return true;
                if (!value.EndsWith(".lilblock", StringComparison.OrdinalIgnoreCase)) continue;

                // Insert blocks walk through here too; they carry no hull stage, so the scan is
                // simply false for them.
                try
                {
                    string blockPath = Path.GetFullPath(Path.Combine(dir, value));
                    if (File.Exists(blockPath) &&
                        File.ReadAllText(blockPath).Contains("#pragma hull"))
                        return true;
                }
                catch { /* an unreadable reference cannot prove tessellation */ }
            }
            return false;
        }

        /// <summary>Wire one container. Returns null on success, the reason on refusal.</summary>
        private static string WireTessContainer(string path, string dir, string text,
                                                string tessText, HashSet<string> appended)
        {
            const string include = "#include \"custom.hlsl\"";
            if (!text.Contains(include))
                return "it has no custom.hlsl include to anchor the define on";

            bool hasInsert = false;
            string postFile = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.StartsWith("lilSubShaderInsertPost", StringComparison.Ordinal))
                    postFile = Quoted(line);
                else if (line.StartsWith("lilSubShaderInsert", StringComparison.Ordinal))
                    hasInsert = true;
            }
            if (!hasInsert)
                return "it has no lilSubShaderInsert line, so the rename in custom_insert.hlsl " +
                       "would never reach it";

            // The silent assumption, checked in plain text. Everything else that could move
            // under us breaks into a compile error and the family degrades; this one would
            // compile an override nobody calls.
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimStart();
                if (!line.StartsWith("lilSubShader", StringComparison.Ordinal)) continue;
                if (line.StartsWith("lilSubShaderInsert", StringComparison.Ordinal)) continue;
                string value = Quoted(line);
                if (value == null ||
                    !value.EndsWith(".lilblock", StringComparison.OrdinalIgnoreCase)) continue;
                string blockPath;
                try { blockPath = Path.GetFullPath(Path.Combine(dir, value)); }
                catch { return $"the block reference '{value}' cannot be resolved"; }
                if (!File.Exists(blockPath)) continue;
                string block = File.ReadAllText(blockPath);
                int hulls = CountOf(block, "#pragma hull");
                if (hulls > 0 && CountOf(block, "#pragma vertex vertTess") < hulls)
                    return $"'{value}' has a hull stage whose vertex entry is not vertTess, so " +
                           "the decode would have nowhere to run";
            }

            if (postFile != null)
            {
                string postPath;
                try { postPath = Path.GetFullPath(Path.Combine(dir, postFile)); }
                catch { return $"its Post block reference '{postFile}' cannot be resolved"; }
                if (!File.Exists(postPath))
                    return $"its Post block '{postFile}' does not exist in the copy";
                if (appended.Add(postPath))
                    File.AppendAllText(postPath, "\n" + tessText);
            }
            else
            {
                // A created Post block, beside the container. The name must not contain BRP, URP
                // or HDRP: lilToon's importer tests line.Contains(rpname) before it looks for
                // lilSubShaderInsertPost, and would eat the declaration as a subshader line.
                const string postName = "lilMeshProtectTessPost.lilblock";
                string postPath = Path.Combine(dir, postName);
                if (!File.Exists(postPath)) File.WriteAllText(postPath, tessText);

                int at = text.IndexOf("lilSubShaderInsert", StringComparison.Ordinal);
                int lineStart = text.LastIndexOf('\n', at) + 1;
                int lineEnd = text.IndexOf('\n', at);
                if (lineEnd < 0)
                    return "its lilSubShaderInsert line is the last line of the file";
                string indent = text.Substring(lineStart, at - lineStart);
                text = text.Insert(lineEnd + 1,
                                   indent + "lilSubShaderInsertPost \"" + postName + "\"\n");
            }

            text = text.Replace(include,
                                "#define LILMP_TESS_POST\n\n        " + include);
            File.WriteAllText(path, text);
            return null;
        }

        private static string Quoted(string line)
        {
            int a = line.IndexOf('"');
            if (a < 0) return null;
            int b = line.IndexOf('"', a + 1);
            return b < 0 ? null : line.Substring(a + 1, b - a - 1);
        }

        private static string QuotedLineValue(string text, string key)
        {
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.StartsWith(key, StringComparison.Ordinal)) return Quoted(line);
            }
            return null;
        }

        /// <summary>The family-relative shader suffix a container declares, e.g. "ltspass_aotess_opaque".</summary>
        private static string ShaderSuffix(string containerText)
        {
            string name = QuotedLineValue(containerText, "Shader ");
            if (name == null) return null;
            const string tag = "*LIL_SHADER_NAME*/";
            int i = name.IndexOf(tag, StringComparison.Ordinal);
            return i < 0 ? null : name.Substring(i + tag.Length);
        }

        private static int CountOf(string text, string needle)
        {
            int n = 0;
            for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        /// <summary>
        /// A shader variant that subdivides the mesh on the GPU. Matched on the family-relative
        /// name, so it catches both lilToon's own Tessellation variants and lilSSRT's
        /// AOTessellation ones, in the root family and in a sub-family alike.
        /// </summary>
        internal static bool IsTessellating(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            return shaderName.Contains("/Tessellation/")
                || shaderName.Contains("/AOTessellation/")
                || shaderName.Contains("/ltspass_tess")
                || shaderName.Contains("/ltspass_aotess");
        }

        /// <summary>
        /// The deny list from this merged family's wiring record, or null when the record is
        /// missing, unreadable or from another format - all of which mean "treat tessellation
        /// the way the pre-wiring versions did, and re-merge when the next rebuild asks".
        /// An empty set means every tessellating container was wired.
        /// </summary>
        private static HashSet<string> TessWiring(MeshProtectRoot settings,
                                                  MeshProtectVariant variant, Host host)
        {
            try
            {
                string path = Path.Combine(Folder(settings, variant, host), TessMarkerFile);
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path).Trim();
                string prefix = "tessgen=" + TessFormat + ";";
                if (!text.StartsWith(prefix, StringComparison.Ordinal)) return null;
                string rest = text.Substring(prefix.Length);
                if (rest == "ok") return new HashSet<string>(StringComparer.Ordinal);
                const string deny = "deny=";
                if (!rest.StartsWith(deny, StringComparison.Ordinal)) return null;
                return new HashSet<string>(
                    rest.Substring(deny.Length)
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.Ordinal);
            }
            catch { return null; }
        }

        /// <summary>
        /// True when this merged family predates the tessellation wiring - the case where the
        /// remedy is 'Rebuild Shader', not a change to the material.
        /// </summary>
        public static bool TessSupportIsStale(MeshProtectRoot settings,
                                              MeshProtectVariant variant, Host host)
            => TessWiring(settings, variant, host) == null;

        private static bool TessRecordCurrent(string folder)
        {
            try
            {
                string path = Path.Combine(folder, TessMarkerFile);
                return File.Exists(path) && File.ReadAllText(path).TrimStart()
                    .StartsWith("tessgen=" + TessFormat + ";", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>Shaders of this family that Unity actually compiled.</summary>
        private static int CompiledCount(string family)
        {
            int n = 0;
            foreach (var info in ShaderUtil.GetAllShaderInfo())
            {
                string name = info.name;
                if (name.StartsWith("Hidden/", StringComparison.Ordinal))
                    name = name.Substring("Hidden/".Length);
                if (name != family && !name.StartsWith(family + "/", StringComparison.Ordinal))
                    continue;
                if (!info.hasErrors && info.supported) n++;
            }
            return n;
        }

        /// <summary>
        /// Our half of custom.hlsl, adjusted for what the host already defines. One emitter still
        /// writes the decode's macros - this only chains or drops whole lines of it.
        /// </summary>
        private static string OurMacros(MeshProtectVariant variant, bool hostProps,
                                        bool hostTextures, bool hostVertCopy)
        {
            string ours = MeshProtectShaderGen.EmitCustomHlsl(variant);

            if (hostProps)
                ours = ours.Replace("#define LIL_CUSTOM_PROPERTIES \\",
                                    "#define LIL_CUSTOM_PROPERTIES \\\n    " + HostProperties + " \\");

            // Ours are empty; where the host has a body, its body is the one that must survive.
            if (hostTextures) ours = ours.Replace("#define LIL_CUSTOM_TEXTURES\r\n", "")
                                         .Replace("#define LIL_CUSTOM_TEXTURES\n", "");
            if (hostVertCopy) ours = ours.Replace("#define LIL_CUSTOM_VERT_COPY\r\n", "")
                                         .Replace("#define LIL_CUSTOM_VERT_COPY\n", "");

            // Flags the host may already have raised.
            foreach (string flag in new[] { "LIL_REQUIRE_APP_NORMAL", "LIL_REQUIRE_APP_TANGENT",
                                            "LIL_REQUIRE_APP_TEXCOORD0", "LIL_REQUIRE_APP_TEXCOORD6" })
                ours = ours.Replace("#define " + flag,
                                    "#ifndef " + flag + "\n#define " + flag + "\n#endif");

            return ours;
        }

        // ------------------------------------------------------------------ plumbing

        private static string Folder(MeshProtectRoot settings, MeshProtectVariant variant, Host host)
        {
            return MeshProtectShaderGen.FolderFor(settings, variant) + "/" +
                   MeshProtectForeignShader.Token("lilhostdir:" + host.family);
        }

        /// <summary>
        /// Variant plus what the host itself is. A host update has to reach the merged copy, and
        /// the copy is a copy: nothing else would notice.
        /// </summary>
        private static string Signature(MeshProtectVariant variant, Host host)
        {
            // Content, not timestamps. A git clone, an unzipped backup or a copy onto another
            // drive rewrites every mtime without changing a byte, and a signature built on those
            // declares a perfectly good merged family stale on a machine that may not be able to
            // rebuild it. The grafts hash their host's bytes for the same reason.
            return MeshProtectShaderGen.VariantSignature(variant) +
                   $"host={host.family};content={ContentDigest(host.realFolder)};";
        }

        private static string ContentDigest(string hostReal)
        {
            if (hostReal == null || !Directory.Exists(hostReal)) return "none";

            // The cheap key first: EnumerateFiles already carries length and mtime, so this costs
            // one directory walk and no file opens.
            long count = 0, size = 0, newest = 0;
            foreach (var fi in new DirectoryInfo(hostReal).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (fi.Name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                count++;
                size += fi.Length;
                if (fi.LastWriteTimeUtc.Ticks > newest) newest = fi.LastWriteTimeUtc.Ticks;
            }
            string key = count + ":" + size + ":" + newest;

            if (digests.TryGetValue(hostReal, out var hit) && hit.key == key) return hit.digest;

            var files = Directory.GetFiles(hostReal, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string digest;
            using (var sha = SHA256.Create())
            {
                foreach (string f in files)
                {
                    // Separator-neutral: the same folder copied between a Windows and a macOS
                    // project must hash the same, or the marker written on one is stale on the
                    // other and every material there ships unprotected on the first build.
                    string relative = f.Substring(hostReal.Length).Replace('\\', '/');
                    byte[] rel = Encoding.UTF8.GetBytes(relative);
                    sha.TransformBlock(rel, 0, rel.Length, null, 0);
                    byte[] body = File.ReadAllBytes(f);
                    sha.TransformBlock(body, 0, body.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                digest = Convert.ToBase64String(sha.Hash) + ";files=" + files.Count;
            }

            digests[hostReal] = (key, digest);
            return digest;
        }

        private static string Field(string text, string key)
        {
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (!t.StartsWith(key, StringComparison.Ordinal)) continue;
                int a = t.IndexOf('"');
                int b = t.LastIndexOf('"');
                if (a >= 0 && b > a) return t.Substring(a + 1, b - a - 1);
            }
            return null;
        }

        /// <summary>True only for a live #define, not a commented-out one.</summary>
        private static bool DefinesMacro(string text, string macro)
        {
            foreach (string line in (text ?? "").Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                if (t.StartsWith("#define " + macro, StringComparison.Ordinal))
                {
                    // "#define LIL_CUSTOM_VERTEX_OS_SOMETHING" is a different macro.
                    string rest = t.Substring(("#define " + macro).Length);
                    if (rest.Length == 0 || rest[0] == ' ' || rest[0] == '\t' || rest[0] == '\\'
                        || rest[0] == '\r' || rest[0] == '(')
                        return true;
                }
            }
            return false;
        }
    }
}
#endif
