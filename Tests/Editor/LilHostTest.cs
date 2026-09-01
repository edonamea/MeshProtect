#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using MeshProtect;
using UnityEditor;
using UnityEngine;

namespace MPTest
{
    /// <summary>
    /// The merged families built on somebody else's lilToon custom shader - lilSSAO, lilSSRT.
    ///
    /// What can go wrong here is not arithmetic, it is text: the merge writes a custom.hlsl that
    /// has to contain BOTH sets of LIL_CUSTOM_* macros without either one shadowing the other,
    /// and the only honest way to know is to let Unity compile the result and then check that a
    /// material lands on it with the host's own properties still present.
    ///
    /// The host packages are commercial, so this cannot assume they are installed. Without them
    /// it checks the parts that do not need them and says which host it skipped, rather than
    /// passing quietly and looking like coverage it does not have.
    /// </summary>
    public static class LilHostTest
    {
        public static void Run() => EditorApplication.Exit(RunCore());

        public static int RunCore()
        {
            int failed = 0;
            var log = new System.Text.StringBuilder();
            void Check(bool ok, string what)
            {
                string line = (ok ? "PASS " : "FAIL ") + what;
                log.AppendLine(line);
                Debug.Log("[LILHOST] " + line);
                if (!ok) failed++;
            }
            void Note(string what)
            {
                log.AppendLine("SKIP " + what);
                Debug.Log("[LILHOST] SKIP " + what);
            }

            try
            {
                foreach (string family in new[] { "lilToon/lilSSAO", "lilSSRT" })
                {
                    var shader = FindHostShader(family);
                    if (shader == null)
                    {
                        Note($"{family} is not installed in this project - nothing to merge with");
                        continue;
                    }

                    Check(MeshProtectLilHost.HostFor(shader) != null,
                          $"{family}: recognised as a host family");

                    var root = new GameObject("MPLilHost").AddComponent<MeshProtectRoot>();
                    try
                    {
                        root.variant = MeshProtectVariantGenerator.Generate(new System.Random(4711));
                        var host = MeshProtectLilHost.HostFor(shader);

                        MeshProtectLilHost.EnsureMerged(root, root.variant, host, out string why,
                                                        force: true);
                        Check(why == null, $"{family}: merged without complaint" +
                                           (why == null ? "" : " - " + why));

                        string merged = MeshProtectLilHost.FamilyName(root.variant, host);
                        var mergedShader = Shader.Find(merged + "/lilToon");
                        Check(mergedShader != null, $"{family}: merged family compiled");
                        if (mergedShader == null) continue;

                        // Both halves have to survive the merge. The host's marker is one of its
                        // own properties; ours is the check vector every protected material has.
                        var material = new Material(shader);
                        string hostProperty = HostProperty(family);
                        Check(material.HasProperty(hostProperty),
                              $"{family}: the host's own material has {hostProperty}");

                        var copy = new Material(material);
                        var target = MeshProtectLilHost.MergedShaderFor(root, root.variant,
                                                                        host, material.shader);
                        bool moved = target != null;
                        if (moved) copy.shader = target;
                        Check(moved, $"{family}: a material moves onto the merged family");

                        if (moved)
                        {
                            Check(copy.HasProperty(hostProperty),
                                  $"{family}: {hostProperty} survives the merge");
                            Check(copy.HasProperty(root.variant.macProperty),
                                  $"{family}: the decode's properties are on the merged family");
                            Check(MeshProtectPipeline.IsProtectShader(copy, root.variant.shaderName),
                                  $"{family}: the merged family counts as protected");
                        }

                        // Sub-families (lilSSRT/GTAO, /RTAO) travel inside the host's folder
                        // and are renamed onto the merged family, so the copy cannot shadow the
                        // installed ones and the decode reaches them through "../custom.hlsl".
                        var sub = Shader.Find(family + "/GTAO/lilToon");
                        if (sub != null)
                        {
                            var subHost = MeshProtectLilHost.HostFor(sub);
                            Check(subHost != null && subHost.family == family,
                                  $"{family}: a sub-family resolves to its parent, not itself");

                            var subTarget = MeshProtectLilHost.MergedShaderFor(root,
                                                                               root.variant,
                                                                               subHost, sub);
                            Check(subTarget != null && subTarget.name.StartsWith(merged + "/GTAO"),
                                  $"{family}: the sub-family maps into the merged family");
                            Check(sub.name == family + "/GTAO/lilToon",
                                  $"{family}: the installed sub-family still answers to its own name");

                            // The sub-family resolves its property block against its OWN folder,
                            // so the root's merged copy never reaches it. A material that lands
                            // here without the decode's properties reads zero for every digit and
                            // for the verifier: no password opens it and the mesh ships invisible.
                            // This is the check that was missing when that hole was open.
                            if (subTarget != null)
                            {
                                var subMaterial = new Material(sub);
                                var subCopy = new Material(subMaterial) { shader = subTarget };
                                Check(subCopy.HasProperty(root.variant.macProperty),
                                      $"{family}: the sub-family declares the decode's verifier");
                                Check(root.variant.digitProperties.All(subCopy.HasProperty),
                                      $"{family}: the sub-family declares all six digits");
                                Check(subCopy.HasProperty(hostProperty),
                                      $"{family}: the sub-family keeps {hostProperty} too");
                                UnityEngine.Object.DestroyImmediate(subCopy);
                                UnityEngine.Object.DestroyImmediate(subMaterial);
                            }
                        }

                        // Tessellating variants used to be refused outright; the wiring pass
                        // now defines LILMP_TESS_POST in the copied containers and appends the
                        // vertTess override to their Post blocks, so the decode runs once per
                        // ORIGINAL vertex, before the tessellator. These assertions used to
                        // demand null; a wired family owes a compiled, protect-shaped shader.
                        var tess = Shader.Find("Hidden/" + family + "/GTAO/AOTessellation/Opaque")
                                   ?? Shader.Find("Hidden/" + family + "/AOTessellation/Opaque");
                        if (tess != null)
                        {
                            var tessTarget = MeshProtectLilHost.MergedShaderFor(
                                root, root.variant, host, tess);
                            Check(tessTarget != null,
                                  $"{family}: an AO-tessellating variant is protected now");
                            if (tessTarget != null)
                            {
                                var tessCopy = new Material(tess) { shader = tessTarget };
                                Check(tessCopy.HasProperty(root.variant.macProperty),
                                      $"{family}: the AO-tess variant declares the decode's verifier");
                                UnityEngine.Object.DestroyImmediate(tessCopy);
                            }
                        }

                        var plainTess = Shader.Find("Hidden/" + family + "/Tessellation/Opaque");
                        if (plainTess != null)
                            Check(MeshProtectLilHost.MergedShaderFor(root, root.variant, host,
                                                                     plainTess) != null,
                                  $"{family}: a plain Tessellation variant is protected now");

                        // The wiring record is the delivery mechanism: MergedShaderFor consults
                        // it, EnsureMerged re-merges without it, and FindMerged ignores it so an
                        // old merged family keeps serving what it already served. Deleting it
                        // must bring back the old refusal WITHOUT touching anything else.
                        string mergedRoot = MeshProtectShaderGen.FolderFor(root, root.variant);
                        string tessRecord = System.IO.Directory
                            .GetFiles(mergedRoot, "tess.txt", System.IO.SearchOption.AllDirectories)
                            .FirstOrDefault(p => System.IO.File.ReadAllText(
                                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(p), "host.txt"))
                                .Contains("host=" + family + ";"));
                        Check(tessRecord != null && System.IO.File.ReadAllText(tessRecord)
                                  .Trim().StartsWith("tessgen=2;"),
                              $"{family}: the wiring record exists and carries the current format");
                        if (tessRecord != null && tess != null)
                        {
                            string saved = System.IO.File.ReadAllText(tessRecord);
                            System.IO.File.Delete(tessRecord);
                            Check(MeshProtectLilHost.MergedShaderFor(root, root.variant, host,
                                                                     tess) == null,
                                  $"{family}: without the record, tessellation is refused again");
                            Check(MeshProtectLilHost.MergedShaderFor(root, root.variant, host,
                                                                     material.shader) != null,
                                  $"{family}: without the record, everything else still serves");
                            System.IO.File.WriteAllText(tessRecord, saved);

                            string mergedDir = System.IO.Path.GetDirectoryName(tessRecord);

                            // The tripwire for the one mutation the whole suite was measured
                            // blind to: skip the LILMP_TESS_POST insertion and everything still
                            // passes - wiring "succeeds", the record says ok, the shaders
                            // compile, MergedShaderFor serves them - and every tessellated mesh
                            // ships shattered with the right password. The define is what arms
                            // the rename; assert it is physically present in every tessellating
                            // container of the copy.
                            int tessContainers = 0, defined = 0;
                            foreach (string cpath in System.IO.Directory.GetFiles(
                                         mergedDir, "*.lilcontainer",
                                         System.IO.SearchOption.AllDirectories))
                            {
                                string ctext = System.IO.File.ReadAllText(cpath);
                                if (!ctext.Contains("Tessellation")) continue;
                                if (!ctext.Contains("HLSLINCLUDE")) continue;
                                tessContainers++;
                                int def = ctext.IndexOf("#define LILMP_TESS_POST",
                                                        StringComparison.Ordinal);
                                int inc = ctext.IndexOf("#include \"custom.hlsl\"",
                                                        StringComparison.Ordinal);
                                if (def >= 0 && inc > def) defined++;
                            }
                            Check(tessContainers > 0 && defined == tessContainers,
                                  $"{family}: every tessellating container is armed " +
                                  $"({defined}/{tessContainers} carry #define LILMP_TESS_POST " +
                                  "before custom.hlsl - an unarmed one compiles, serves, and " +
                                  "ships shattered with the right password)");

                            // The deny path, exercised - no shipped container fails wiring, so
                            // inject a record and prove the matching actually refuses. The
                            // entries are root-relative on purpose: MergedShaderFor compares
                            // suffixes relative to the ROOT family, and an unqualified
                            // sub-family entry can never match (that was a shipped-blocking
                            // defect once already).
                            var gtaoTess = Shader.Find(
                                "Hidden/" + family + "/GTAO/AOTessellation/Opaque");
                            if (gtaoTess != null)
                            {
                                System.IO.File.WriteAllText(tessRecord,
                                    "tessgen=2;deny=GTAO/AOTessellation/Opaque");
                                Check(MeshProtectLilHost.MergedShaderFor(root, root.variant,
                                          host, gtaoTess) == null,
                                      $"{family}: a qualified deny entry refuses its variant");
                                Check(MeshProtectLilHost.MergedShaderFor(root, root.variant,
                                          host, material.shader) != null,
                                      $"{family}: a deny entry does not spill onto other variants");

                                System.IO.File.WriteAllText(tessRecord, "tessgen=2;deny=*");
                                Check(MeshProtectLilHost.MergedShaderFor(root, root.variant,
                                          host, gtaoTess) == null,
                                      $"{family}: deny=* falls back to the name refusal");
                                Check(MeshProtectLilHost.MergedShaderFor(root, root.variant,
                                          host, material.shader) != null,
                                      $"{family}: deny=* still serves non-tessellating variants");

                                System.IO.File.WriteAllText(tessRecord, saved);
                            }

                            // Delivery: an old merged folder re-merges on the next unforced
                            // EnsureMerged - that is the entire mechanism by which existing
                            // users receive the wiring, and nothing asked it without force.
                            System.IO.File.Delete(tessRecord);
                            bool remerged = MeshProtectLilHost.EnsureMerged(
                                root, root.variant, host, out _, false);
                            bool recordBack = System.IO.File.Exists(tessRecord) &&
                                System.IO.File.ReadAllText(tessRecord).Trim()
                                    .StartsWith("tessgen=2;");
                            Check(remerged && recordBack,
                                  $"{family}: a record-less merge re-merges without force " +
                                  (remerged
                                      ? (recordBack ? "(re-merged, record back)"
                                                    : "(re-merged but wrote no current record)")
                                      : "(SKIPPED - existing users would never receive " +
                                        "tessellation support)"));
                        }

                        // FakeShadow is the variant the renderer survey used to lose. lilSSRT's
                        // family name carries no "lilToon", and DefaultFakeShadow is the one
                        // lilToon property set without _LightMinLimit - so both clues the cheap
                        // first pass looks for were missing and the renderer was dropped before
                        // anything could warn about it, leaving a shadow plane drawn under an
                        // avatar the decode had hidden.
                        var fake = Shader.Find(family + "/[Optional] FakeShadow")
                                   ?? Shader.Find("Hidden/" + family + "/[Optional] FakeShadow");
                        if (fake != null)
                        {
                            var fakeMaterial = new Material(fake);
                            Check(MeshProtectPipeline.CanCarryTheDecode(fakeMaterial),
                                  $"{family}: a FakeShadow material is still worth looking at");
                            Check(MeshProtectLilHost.MergedShaderFor(root, root.variant, host, fake) != null,
                                  $"{family}: a FakeShadow material moves onto the merged family");
                            UnityEngine.Object.DestroyImmediate(fakeMaterial);
                        }

                        UnityEngine.Object.DestroyImmediate(copy);
                        UnityEngine.Object.DestroyImmediate(material);
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(root.gameObject);
                    }
                }

                // Everything above tests the merge in isolation - a shader is asked for and its
                // properties are inspected. None of it runs a BUILD, so the whole distance between
                // "the merged family exists" and "this avatar came out protected" was uncovered,
                // and a user reporting that their avatar did not disappear was standing in exactly
                // that gap. A locked mesh collapses every vertex to the origin; an avatar that
                // still renders normally was never protected at all.
                foreach (string family in new[] { "lilToon/lilSSAO", "lilSSRT" })
                {
                    var hostShader = FindHostShader(family);
                    if (hostShader == null) continue;
                    Check(BuildsProtected(hostShader, family, Check), $"{family}: a whole build protects it");
                }

                // Independent of the hosts: an unknown custom family must still be refused, which
                // is what stops a second protection tool from displacing the same mesh twice.
                Check(MeshProtectLilHost.HostFor(Shader.Find("Standard")) == null,
                      "an unrelated shader is not treated as a host family");
            }
            catch (Exception e)
            {
                Check(false, "EXCEPTION: " + e);
            }

            Debug.Log(failed == 0 ? "[LILHOST] === PASS ===" : $"[LILHOST] === FAIL ({failed}) ===");
            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "lilhost-result.txt"),
                              log.ToString());
            return failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// One avatar, one host material, the real pipeline. Mirrors what an author does: add the
        /// component, press the button that prepares the families, then Build and Publish.
        /// </summary>
        private static bool BuildsProtected(Shader hostShader, string family, Action<bool, string> Check)
        {
            string folder = "Assets/_MPHostBuild";
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            AssetDatabase.CreateFolder("Assets", "_MPHostBuild");

            var root = new GameObject("HostBuild");
            try
            {
                root.AddComponent<UnityEngine.Animator>();
                var descriptor = root.AddComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
                descriptor.customExpressions = false;

                var bone = new GameObject("Bone");
                bone.transform.SetParent(root.transform, false);
                var body = new GameObject("Body");
                body.transform.SetParent(root.transform, false);

                var mesh = MPTestMesh();
                var smr = body.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mesh;
                smr.bones = new[] { bone.transform };
                smr.rootBone = bone.transform;
                smr.sharedMaterials = new[] { new Material(hostShader) { name = "HostMaterial" } };

                var settings = root.AddComponent<MeshProtectRoot>();
                settings.variant = MeshProtectVariantGenerator.Generate(new System.Random(90210));
                settings.keyDigits = new[] { 1, 2, 3, 4, 5, 6 };

                var problems = new System.Collections.Generic.List<string>();
                MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out _, true);
                MeshProtectLilHost.Forget();
                MeshProtectLilHost.EnsureAllMerged(settings, settings.variant, root, problems, true);

                var report = new MeshProtectPipeline.Report();
                MeshProtectPipeline.Apply(root, settings, folder, report);

                var after = smr.sharedMaterials.FirstOrDefault();
                bool moved = after != null &&
                             MeshProtectPipeline.IsProtectShader(after, settings.variant.shaderName);
                bool baked = smr.sharedMesh != null && smr.sharedMesh != mesh;
                bool displaced = false;
                if (baked)
                {
                    var uv6 = new System.Collections.Generic.List<Vector4>();
                    smr.sharedMesh.GetUVs(6, uv6);
                    displaced = uv6.Any(v => v != Vector4.zero);
                }
                bool keyed = after != null && after.HasProperty(settings.variant.macProperty)
                             && settings.variant.digitProperties.All(after.HasProperty);

                Check(report.appliedProtection, $"{family}: the build reports protection applied");
                Check(moved, $"{family}: the material ends up on the merged family");
                Check(displaced, $"{family}: the mesh ships displaced (UV6 written)");
                Check(keyed, $"{family}: the shipped material carries the verifier and all six digits");
                return report.appliedProtection && moved && displaced && keyed;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }

        private static Mesh MPTestMesh()
        {
            const int side = 16;
            int count = side * side;
            var v = new Vector3[count]; var n = new Vector3[count];
            var t = new Vector4[count]; var uv = new Vector2[count];
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    int i = y * side + x;
                    v[i] = new Vector3(x * 0.05f, y * 0.05f, Mathf.Sin(i) * 0.01f);
                    n[i] = new Vector3(Mathf.Sin(i * 0.3f), 1f, Mathf.Cos(i * 0.3f)).normalized;
                    var tan = Vector3.Cross(n[i], Vector3.forward).normalized;
                    t[i] = new Vector4(tan.x, tan.y, tan.z, 1f);
                    uv[i] = new Vector2(x / (float)side, y / (float)side);
                }
            var tris = new System.Collections.Generic.List<int>();
            for (int y = 0; y < side - 1; y++)
                for (int x = 0; x < side - 1; x++)
                {
                    int i = y * side + x;
                    tris.Add(i); tris.Add(i + side); tris.Add(i + 1);
                    tris.Add(i + 1); tris.Add(i + side); tris.Add(i + side + 1);
                }
            var mesh = new Mesh { name = "HostBuildMesh" };
            mesh.vertices = v; mesh.normals = n; mesh.tangents = t; mesh.uv = uv;
            mesh.triangles = tris.ToArray();
            var w = new BoneWeight[count];
            for (int i = 0; i < count; i++) w[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            mesh.boneWeights = w;
            mesh.bindposes = new[] { Matrix4x4.identity };
            return mesh;
        }

        /// <summary>Any shader belonging to the family, whatever variant lilToon generated.</summary>
        private static Shader FindHostShader(string family)
        {
            foreach (string suffix in new[] { "/lilToon", "/lilToonLite", "/lilToonMulti" })
            {
                var s = Shader.Find(family + suffix);
                if (s != null) return s;
            }
            return Shader.Find(family);
        }

        private static string HostProperty(string family)
        {
            return family == "lilSSRT" ? "_LilSSRTAO" : "_SSAO";
        }
    }
}
#endif
