#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using MeshProtect;

namespace MPTest
{
    /// <summary>
    /// Does a protected mesh survive an AssetBundle build with its UV0 bit pattern intact?
    ///
    /// The whole design hangs on this, and nothing else in the suite can catch it. Vertex identity
    /// is the raw bit pattern of UV0, read back in the shader with asuint(). Unity has two
    /// build-time knobs that rewrite vertex data:
    ///
    ///   Vertex Compression   - squeezes selected channels to half precision
    ///   Optimize Mesh Data   - strips channels no shader appears to read
    ///
    /// If UV0 is compressed, every vertex hashes to a different identity in game than it did at
    /// bake time and the avatar never decodes. If UV6 is stripped, the amplitude reads as zero, the
    /// decode returns early, and the mesh stays scrambled whatever password is entered. Both look
    /// identical to the wearer: "I typed the password and nothing happened."
    ///
    /// Every other test runs on meshes that live in memory or in the AssetDatabase, so every other
    /// test would pass while the uploaded avatar was permanently locked. This is the only one that
    /// puts the mesh through the same serialisation VRChat does.
    ///
    /// Two bundles are built. The first holds the mesh alone, which is the case where Unity has no
    /// shader to tell it which channels matter. The second holds a prefab with a renderer and a
    /// generated material, which is what VRChat actually uploads and the case where channel
    /// stripping has the information it would act on.
    /// </summary>
    public static class BundleRoundTripTest
    {
        private const string Folder = "Assets/_BundleProbe";
        private const string Output = "Temp/BundleProbeOut";

        private static readonly StringBuilder Log = new StringBuilder();
        private static int failures;

        private static void Say(string s) { Log.AppendLine(s); Debug.Log("[BUNDLE] " + s); }

        private static void Check(bool ok, string label, string detail)
        {
            if (!ok) failures++;
            Say($"{(ok ? "PASS" : "FAIL")}  {label}  {detail}");
        }

        /// <summary>
        /// Entry point for a Unity launch of its own: run, then end the process with the result.
        /// The work is in RunCore so that a single launch can run every suite - each Exit here is an
        /// editor start, an asset import and a script compile, and four of those is most of the
        /// time a change costs to verify.
        /// </summary>
        public static void Run() => EditorApplication.Exit(RunCore());

        /// <summary>Runs the suite and returns the exit code, without ending the process.</summary>
        public static int RunCore()
        {
            int exit = 0;
            bool previousOptimize = PlayerSettings.stripUnusedMeshComponents;
            try
            {
                // SDK initialization turns this off in a fresh Avatar project. Exercise the
                // stricter serialization setting explicitly, then restore the caller's choice.
                PlayerSettings.stripUnusedMeshComponents = true;
                Say("=== AssetBundle vertex round trip ===");

                // This suite exists to show that UV0 survives a real bundle build bit-exact with
                // Optimize Mesh Data on and vertex compression enabled. bundle/probe-is-sensitive
                // shows the probe would notice if the bits changed; it cannot show that anything
                // was asked to change them. With both settings off, every check below passes for
                // the least interesting reason available, and the README's claim would be resting
                // on a project configuration nobody stated. So they are stated.
                bool optimize = PlayerSettings.stripUnusedMeshComponents;

                // PlayerSettings has no accessor for the compression mask on 2022.3, so it is read
                // off the settings asset itself. -1 means it could not be read at all, which is
                // reported rather than asserted either way: a check that cannot see what it is
                // checking should say so, not pass.
                int compression = -1;
                var playerSettings = AssetDatabase
                    .LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
                if (playerSettings != null)
                {
                    var mask = new SerializedObject(playerSettings)
                        .FindProperty("VertexChannelCompressionMask");
                    if (mask != null) compression = mask.intValue;
                }
                Say($"      Optimize Mesh Data = {optimize}, vertex compression mask = " +
                    (compression < 0 ? "unreadable" : compression.ToString()));
                Check(optimize, "bundle/optimize-mesh-data-is-on",
                      optimize
                          ? "Optimize Mesh Data is on, so this run tests what the README claims"
                          : "Optimize Mesh Data is OFF in this project - the checks below would " +
                            "pass without exercising the thing they exist for");
                Check(compression > 0, "bundle/vertex-compression-is-on",
                      compression > 0
                          ? $"vertex compression is enabled (mask {compression})"
                          : compression == 0
                              ? "vertex compression is OFF in this project - nothing here would be " +
                                "asked to rewrite UV0, so bit-exactness proves nothing"
                              : "the compression mask could not be read, so this run cannot say " +
                                "whether it tested anything");

                var lilToon = Shader.Find("lilToon");
                if (lilToon == null) throw new Exception("lilToon shader not found in this project.");

                if (!AssetDatabase.IsValidFolder(Folder))
                    AssetDatabase.CreateFolder("Assets", "_BundleProbe");

                var mesh = BuildProbeMesh(out var uv0, out var uv6);

                // Before trusting a pass: prove the probe could detect the thing it is looking for.
                // If these coordinates happened to be exactly representable in half precision, the
                // round trip would come back bit-identical even under full compression and the test
                // would report success while learning nothing.
                int survivesHalf = uv0.Count(v =>
                    Mathf.HalfToFloat(Mathf.FloatToHalf(v.x)) == v.x &&
                    Mathf.HalfToFloat(Mathf.FloatToHalf(v.y)) == v.y);
                Check(survivesHalf == 0, "bundle/probe-is-sensitive",
                      survivesHalf == 0
                          ? $"all {uv0.Length} probe coordinates change under fp16, so compression " +
                            "cannot pass unnoticed"
                          : $"{survivesHalf}/{uv0.Length} probe coordinates are fp16-exact - this " +
                            "test cannot detect compression and its passes mean nothing");

                string meshPath = Folder + "/ProbeMesh.asset";
                AssetDatabase.CreateAsset(mesh, meshPath);

                // A real protected avatar: our own generated shader on a skinned renderer, which is
                // the arrangement channel stripping would be reasoning about.
                var settings = new GameObject("ProbeSettings").AddComponent<MeshProtectRoot>();
                try
                {
                    var rng = new System.Random(31337);
                    settings.outputFolder = Folder;
                    settings.keyDigits = MeshProtectCipher.GeneratePassword(rng);
                    settings.variant = MeshProtectVariantGenerator.Generate(rng);
                    if (MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out _))
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                    var protectShader = Shader.Find(settings.variant.shaderName + "/lilToon");
                    if (protectShader == null)
                        throw new Exception("generated shader family did not import");

                    var material = new Material(protectShader) { name = "ProbeMat" };
                    AssetDatabase.CreateAsset(material, Folder + "/ProbeMat.mat");

                    string prefabPath = BuildProbePrefab(mesh, material);

                    AssetDatabase.SaveAssets();
                    Directory.CreateDirectory(Output);

                    // Two separate builds, not two bundles in one build. Tagging the mesh into its
                    // own bundle makes the prefab bundle merely REFERENCE it, so the prefab comes
                    // back with a null mesh and the interesting case never gets tested.
                    Tag(meshPath, "probe_mesh");
                    Tag(prefabPath, "");
                    BuildPipeline.BuildAssetBundles(Output, BuildAssetBundleOptions.None,
                                                    BuildTarget.StandaloneWindows64);
                    CheckBundle("mesh-only", "probe_mesh", uv0, uv6,
                                b => b.LoadAllAssets<Mesh>().FirstOrDefault());

                    Tag(meshPath, "");
                    Tag(prefabPath, "probe_prefab");
                    BuildPipeline.BuildAssetBundles(Output, BuildAssetBundleOptions.None,
                                                    BuildTarget.StandaloneWindows64);
                    CheckBundle("with-renderer", "probe_prefab", uv0, uv6, b =>
                    {
                        var go = b.LoadAllAssets<GameObject>().FirstOrDefault();
                        return go == null
                            ? null
                            : go.GetComponentInChildren<SkinnedMeshRenderer>(true)?.sharedMesh;
                    });
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(settings.gameObject);
                }

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
                PlayerSettings.stripUnusedMeshComponents = previousOptimize;
                if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
                AssetDatabase.RemoveUnusedAssetBundleNames();
                AssetDatabase.Refresh();
            }

            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "bundle-result.txt"),
                              Log.ToString());
            return exit;
        }

        private static void Tag(string assetPath, string bundleName)
        {
            var importer = AssetImporter.GetAtPath(assetPath);
            importer.assetBundleName = bundleName;
            importer.SaveAndReimport();
        }

        private static void CheckBundle(string label, string bundleName, Vector2[] uv0, Vector2[] uv6,
                                        Func<AssetBundle, Mesh> extract)
        {
            AssetBundle bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromFile(Path.Combine(Output, bundleName));
                if (bundle == null) { Check(false, "bundle/" + label, "bundle did not load"); return; }

                var loaded = extract(bundle);
                if (loaded == null) { Check(false, "bundle/" + label, "no mesh in the bundle"); return; }

                var backUv0 = loaded.uv;
                var backUv6 = new List<Vector2>();
                loaded.GetUVs(6, backUv6);

                int changed = 0;
                double worst = 0;
                for (int i = 0; i < Math.Min(uv0.Length, backUv0.Length); i++)
                {
                    if (MeshProtectCipher.AsUInt(backUv0[i].x) != MeshProtectCipher.AsUInt(uv0[i].x) ||
                        MeshProtectCipher.AsUInt(backUv0[i].y) != MeshProtectCipher.AsUInt(uv0[i].y))
                        changed++;
                    worst = Math.Max(worst, Math.Abs(backUv0[i].x - uv0[i].x));
                    worst = Math.Max(worst, Math.Abs(backUv0[i].y - uv0[i].y));
                }

                Check(backUv0.Length == uv0.Length && changed == 0, $"bundle/{label}/uv0-bit-exact",
                      backUv0.Length != uv0.Length
                          ? $"uv0 came back with {backUv0.Length} of {uv0.Length} entries"
                          : changed == 0
                              ? $"all {uv0.Length} identities survive the bundle"
                              : $"{changed}/{uv0.Length} identities changed, worst delta {worst:E3} - " +
                                "the avatar would never decode in game");

                Check(backUv6.Count == uv6.Length, $"bundle/{label}/uv6-present",
                      backUv6.Count == uv6.Length
                          ? $"amplitude survives on all {uv6.Length} vertices"
                          : $"uv6 came back with {backUv6.Count} of {uv6.Length} entries - the mesh " +
                            "would stay scrambled whatever password is entered");
            }
            finally
            {
                if (bundle != null) bundle.Unload(true);
            }
        }

        private static Mesh BuildProbeMesh(out Vector2[] uv0, out Vector2[] uv6)
        {
            const int n = 64;
            var mesh = new Mesh { name = "ProbeMesh" };
            var verts = new Vector3[n];
            uv0 = new Vector2[n];
            uv6 = new Vector2[n];

            for (int i = 0; i < n; i++)
            {
                verts[i] = new Vector3(i * 0.017f, Mathf.Sin(i) * 0.31f, Mathf.Cos(i) * 0.29f);
                // Awkward on purpose: each value needs more mantissa than fp16 carries, so any
                // compression shows up as a changed bit pattern rather than a lucky round trip.
                uv0[i] = new Vector2(0.1234567f + i * 0.0011113f, 0.7654321f - i * 0.0007771f);
                uv6[i] = new Vector2(0.0412345f + i * 0.0001f, 0f);
            }

            mesh.vertices = verts;
            mesh.uv = uv0;
            mesh.SetUVs(6, uv6);

            var tris = new int[(n - 2) * 3];
            for (int i = 0; i < n - 2; i++)
            {
                tris[i * 3] = i;
                tris[i * 3 + 1] = i + 1;
                tris[i * 3 + 2] = i + 2;
            }
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();

            var weights = new BoneWeight[n];
            for (int i = 0; i < n; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { Matrix4x4.identity };
            return mesh;
        }

        private static string BuildProbePrefab(Mesh mesh, Material material)
        {
            var root = new GameObject("ProbeAvatar");
            try
            {
                var bone = new GameObject("Bone");
                bone.transform.SetParent(root.transform, false);

                var body = new GameObject("Body");
                body.transform.SetParent(root.transform, false);
                var smr = body.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mesh;
                smr.bones = new[] { bone.transform };
                smr.rootBone = bone.transform;
                smr.sharedMaterials = new[] { material };

                string path = Folder + "/ProbeAvatar.prefab";
                PrefabUtility.SaveAsPrefabAsset(root, path);
                return path;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
#endif
