#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// Measures the one thing neither the C# self check nor the GPU check can: whether the restore
    /// survives skinning on THIS avatar.
    ///
    /// SkinnedMeshRenderer.BakeMesh() applies blend shapes and linear blend skinning exactly as the
    /// renderer does. Posing the source and the protected copy identically, decoding the latter and
    /// differencing them gives the true residual at that pose.
    ///
    /// The displacement axes were chosen to be skinning invariant, but that argument rests on how
    /// Unity normalises skinned normals and tangents, and on your model's weighting. Measure it.
    /// </summary>
    public static class MeshProtectSkinCheck
    {
        public class PoseResult
        {
            public string pose;
            public double meanError;
            public double worstError;
            public string worstMesh = "-";
            public double maxDisplacement;
            public double RatioPercent => maxDisplacement > 1e-12 ? worstError / maxDisplacement * 100 : 0;
        }

        public class Result
        {
            public bool ran;
            public string skipReason;
            public readonly List<PoseResult> poses = new List<PoseResult>();
            public double Worst => poses.Count == 0 ? 0 : poses.Max(p => p.worstError);
            public double WorstRatio => poses.Count == 0 ? 0 : poses.Max(p => p.RatioPercent);
        }

        /// <summary>
        /// Build a throwaway protected copy of the avatar, measure against it, and clean up.
        ///
        /// Protection now happens during the upload, so there is no protected avatar sitting in
        /// the scene to measure any more - this makes one on demand. Everything it creates, in the
        /// scene and on disk, is gone by the time the method returns.
        /// </summary>
        public static void RunAndReport(MeshProtectRoot settings)
        {
            GameObject copy = null;
            string folder = null;

            // Out here so the catch can still read it. Apply throws on several guards, and the
            // warnings gathered before one of them are often what explains it.
            var report = new MeshProtectPipeline.Report();

            try
            {
                EditorUtility.DisplayProgressBar(MeshProtectL10n.Tr("progress.title"),
                    MeshProtectL10n.Tr("progress.skincheck.copy"), 0.3f);

                // Scratch space under the plugin's own output root, like everything else this
                // tool writes; deleted again in the finally below.
                string outputRoot = MeshProtectShaderGen.OutputRoot(settings);
                folder = outputRoot + "/_SkinCheck";
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                if (!AssetDatabase.IsValidFolder(outputRoot))
                {
                    string built = null;
                    foreach (string segment in outputRoot.Split('/'))
                    {
                        string next = built == null ? segment : built + "/" + segment;
                        if (built != null && !AssetDatabase.IsValidFolder(next))
                            AssetDatabase.CreateFolder(built, segment);
                        built = next;
                    }
                }
                AssetDatabase.CreateFolder(outputRoot, "_SkinCheck");

                copy = UnityEngine.Object.Instantiate(settings.gameObject);
                copy.name = settings.gameObject.name + " (skin check)";
                copy.SetActive(true);

                // A button the author presses to look at their own avatar, so it is a likely first
                // meeting with the UV6 refusal or the note about Target Renderers that are not part
                // of the avatar - and that note is often what explains the refusal after it.
                MeshProtectPipeline.Apply(copy, copy.GetComponentInChildren<MeshProtectRoot>(true),
                                          folder, report);

                EditorUtility.DisplayProgressBar(MeshProtectL10n.Tr("progress.title"),
                    MeshProtectL10n.Tr("progress.skincheck.measure"), 0.7f);
                var result = Run(settings, copy, report.mode);

                EditorUtility.ClearProgressBar();

                // The same drain the catch does, on the path where nothing threw. Apply just ran
                // for real on a throwaway clone, so everything it would say at upload time - a
                // refused tessellating material, a missing graft, a UV6 conflict - has already
                // been said, into a list nobody read. This button was always a full dry run of
                // the build; it only forgot to hand over the findings. At upload time the same
                // warnings land in a console the SDK modal-blocks; here the author is actually
                // looking.
                foreach (var warning in report.warnings)
                    Debug.LogWarning("[MeshProtect] " + warning);
                report.warnings.Clear();

                if (!result.ran)
                {
                    EditorUtility.DisplayDialog(MeshProtectL10n.Tr("dialog.skincheck.title"),
                        MeshProtectL10n.Tr("dialog.skincheck.cantrun", result.skipReason), "OK");
                    return;
                }

                string table = string.Join("\n", result.poses.Select(p =>
                    $"{p.pose,-18} worst {p.worstError * 1000:F3} mm  ({p.RatioPercent:F2}% of displacement)"));

                // The Console copy stays English - it is what gets pasted into help threads -
                // while the dialog the author reads follows the language. Same numbers in both.
                string message =
                    $"Worst residual: {result.Worst * 1000:F3} mm ({result.WorstRatio:F2}% of the " +
                    $"displacement)\n\n" + table +
                    "\n\nA fraction of a percent is floating point noise. Percentages in the single " +
                    "digits mean the displacement is fighting the skinning at that joint - turn on " +
                    "Attenuate At Joints, or lower Distort Ratio.";

                Debug.Log("[MeshProtect] Skinning residual: " + message.Replace("\n", " "));
                EditorUtility.DisplayDialog(MeshProtectL10n.Tr("dialog.skincheck.title"),
                    MeshProtectL10n.Tr("dialog.skincheck.result",
                        (result.Worst * 1000).ToString("F3"), result.WorstRatio.ToString("F2"), table),
                    "OK");
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();

                // Whatever was collected before the throw, which is often the sentence that
                // explains it - the renderers that were skipped for not belonging to the avatar,
                // say, which is why an unexpected mesh is being complained about.
                foreach (var warning in report.warnings)
                    Debug.LogWarning("[MeshProtect] " + warning);
                report.warnings.Clear();

                Debug.LogError("[MeshProtect] Skinning check failed: " + e);
                EditorUtility.DisplayDialog(MeshProtectL10n.Tr("dialog.skincheck.title"),
                    MeshProtectL10n.Tr("dialog.skincheck.failed", e.Message), "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
                if (!string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder))
                {
                    AssetDatabase.DeleteAsset(folder);
                    AssetDatabase.Refresh();
                }
            }
        }

        public static Result Run(MeshProtectRoot settings, GameObject protectedCopy,
                                 MeshProtectRoot.DisplacementMode mode)
        {
            var result = new Result();

            var sourceAnimator = settings.GetComponent<Animator>();
            if (sourceAnimator == null || !sourceAnimator.isHuman)
            {
                result.skipReason = "The avatar is not Humanoid, so no standard joints can be posed. " +
                                    "Check extreme poses by hand instead.";
                return result;
            }

            uint key = MeshProtectCipher.PackDigits(settings.keyDigits, settings.variant);
            var variant = settings.variant;
            var source = settings.gameObject;

            var restore = new List<(Transform t, Quaternion rotation, Vector3 scale)>();
            foreach (var avatar in new[] { source, protectedCopy })
            foreach (var bone in TouchedBones)
            {
                var t = avatar.GetComponent<Animator>()?.GetBoneTransform(bone);
                if (t != null) restore.Add((t, t.localRotation, t.localScale));
            }

            try
            {
                foreach (var pose in Poses())
                {
                    ApplyPose(source, pose);
                    ApplyPose(protectedCopy, pose);
                    result.poses.Add(Measure(pose.name, source, protectedCopy, mode, key, variant));
                }
                result.ran = true;
            }
            finally
            {
                foreach (var (t, rotation, scale) in restore)
                {
                    t.localRotation = rotation;
                    t.localScale = scale;
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ poses

        private static readonly HumanBodyBones[] TouchedBones =
        {
            HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg
        };

        private class Pose
        {
            public string name;
            public readonly List<(HumanBodyBones bone, Vector3 euler)> rotations =
                new List<(HumanBodyBones, Vector3)>();

            /// <summary>
            /// Bone scale, which rotation alone cannot stand in for.
            ///
            /// The displacement axes are skinned by the same matrix as the position, and under a
            /// pure rotation |R*n| is 1, so the shader's normalised basis and the baked one agree
            /// exactly - which is what every rotation pose measures. A scaled bone breaks that tie:
            /// the position picks up the scale and a renormalised basis does not, so if Unity
            /// normalises skinned normals the restore is off by (scale - 1) times the displacement.
            /// Avatars scale bones constantly - hiding parts, body sliders - so this has to be
            /// measured rather than assumed.
            /// </summary>
            public readonly List<(HumanBodyBones bone, Vector3 scale)> scales =
                new List<(HumanBodyBones, Vector3)>();
        }

        private static IEnumerable<Pose> Poses()
        {
            yield return new Pose { name = "rest" };

            foreach (float angle in new[] { 45f, 90f, 135f })
            {
                var p = new Pose { name = $"elbow {angle:F0}" };
                p.rotations.Add((HumanBodyBones.LeftLowerArm, new Vector3(0, angle, 0)));
                p.rotations.Add((HumanBodyBones.RightLowerArm, new Vector3(0, -angle, 0)));
                yield return p;
            }

            var knee = new Pose { name = "knee 90" };
            knee.rotations.Add((HumanBodyBones.LeftLowerLeg, new Vector3(-90, 0, 0)));
            knee.rotations.Add((HumanBodyBones.RightLowerLeg, new Vector3(-90, 0, 0)));
            yield return knee;

            foreach (var (label, scale) in new[]
                     {
                         ("scale 0.5", new Vector3(0.5f, 0.5f, 0.5f)),
                         ("scale 2.0", new Vector3(2f, 2f, 2f)),
                         ("scale 1,2,1", new Vector3(1f, 2f, 1f))
                     })
            {
                var p = new Pose { name = label };
                p.scales.Add((HumanBodyBones.LeftLowerArm, scale));
                p.scales.Add((HumanBodyBones.RightLowerArm, scale));
                yield return p;
            }

            var extreme = new Pose { name = "extreme combined" };
            extreme.rotations.Add((HumanBodyBones.LeftUpperArm, new Vector3(0, 0, 60)));
            extreme.rotations.Add((HumanBodyBones.LeftLowerArm, new Vector3(0, 120, 0)));
            extreme.rotations.Add((HumanBodyBones.RightUpperArm, new Vector3(0, 0, -60)));
            extreme.rotations.Add((HumanBodyBones.RightLowerArm, new Vector3(0, -120, 0)));
            extreme.rotations.Add((HumanBodyBones.LeftLowerLeg, new Vector3(-120, 0, 0)));
            yield return extreme;
        }

        private static void ApplyPose(GameObject avatar, Pose pose)
        {
            var animator = avatar.GetComponent<Animator>();
            if (animator == null) return;

            foreach (var bone in TouchedBones)
            {
                var t = animator.GetBoneTransform(bone);
                if (t == null) continue;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }
            foreach (var (bone, euler) in pose.rotations)
            {
                var t = animator.GetBoneTransform(bone);
                if (t != null) t.localRotation = Quaternion.Euler(euler);
            }
            foreach (var (bone, scale) in pose.scales)
            {
                var t = animator.GetBoneTransform(bone);
                if (t != null) t.localScale = scale;
            }
        }

        // ------------------------------------------------------------------ measurement

        private static PoseResult Measure(string poseName, GameObject source, GameObject copy,
                                          MeshProtectRoot.DisplacementMode mode,
                                          uint key, MeshProtectVariant variant)
        {
            bool tangentSpace = mode == MeshProtectRoot.DisplacementMode.TangentSpace;
            var result = new PoseResult { pose = poseName };

            double sum = 0; long counted = 0;
            var referenceBake = new Mesh();
            var protectedBake = new Mesh();

            try
            {
                foreach (var protectedRenderer in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (protectedRenderer.sharedMesh == null) continue;
                    if (!protectedRenderer.sharedMaterials.Any(
                            m => MeshProtectPipeline.IsProtectShader(m, variant.shaderName))) continue;

                    string path = AnimationUtility.CalculateTransformPath(
                        protectedRenderer.transform, copy.transform);
                    var referenceTransform = string.IsNullOrEmpty(path)
                        ? source.transform : source.transform.Find(path);
                    var referenceRenderer = referenceTransform != null
                        ? referenceTransform.GetComponent<SkinnedMeshRenderer>() : null;
                    if (referenceRenderer == null || referenceRenderer.sharedMesh == null) continue;

                    referenceRenderer.BakeMesh(referenceBake);
                    protectedRenderer.BakeMesh(protectedBake);

                    var referenceVertices = referenceBake.vertices;
                    var protectedVertices = protectedBake.vertices;
                    var normals = protectedBake.normals;
                    var tangents = protectedBake.tangents;
                    if (referenceVertices.Length != protectedVertices.Length) continue;

                    var uv6 = new List<Vector2>(); protectedRenderer.sharedMesh.GetUVs(6, uv6);
                    // UV0 is untouched by the bake, so the baked pose still carries the identity
                    // the shader hashes.
                    var uv0 = protectedRenderer.sharedMesh.uv;
                    if (uv6.Count != protectedVertices.Length) continue;
                    if (uv0 == null || uv0.Length != protectedVertices.Length) continue;

                    for (int v = 0; v < protectedVertices.Length; v++)
                    {
                        Vector4 tangent = tangents != null && tangents.Length == protectedVertices.Length
                            ? tangents[v] : new Vector4(1, 0, 0, 1);

                        Vector3 displacement = MeshProtectSelfCheck.Decode(
                            tangentSpace, key, variant, normals[v], tangent, uv0[v], uv6[v]);
                        double error = ((protectedVertices[v] - displacement) - referenceVertices[v]).magnitude;

                        if (error > result.worstError)
                        {
                            result.worstError = error;
                            result.worstMesh = protectedRenderer.name;
                        }
                        result.maxDisplacement = Math.Max(result.maxDisplacement, displacement.magnitude);
                        sum += error; counted++;
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(referenceBake);
                UnityEngine.Object.DestroyImmediate(protectedBake);
            }

            result.meanError = counted > 0 ? sum / counted : 0;
            return result;
        }

    }
}
#endif
