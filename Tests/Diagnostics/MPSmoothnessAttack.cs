#if UNITY_EDITOR
// How much of a protected mesh can somebody recover WITHOUT the password?
//
// This is not a test. It is a measurement of the one claim the whole product rests on, and it
// exists because MeshProtectCipher's own class comment makes that claim in a form that does not
// survive being read from the attacker's side:
//
//     "Here the only unknown is the key itself, mixed through integer avalanche rounds, so there
//      is nothing continuous left to fit."
//
// The attacker does not have to recover the key. Per vertex they are handed:
//
//     P'   the displaced position
//     n    the ORIGINAL normal - the decode needs it, so it cannot be withheld
//     A    the displacement amplitude in metres, in TEXCOORD6.x, in the clear
//
// and the relation is P' = P + n·(A·a) with a in [-1, 1). So the original P sits somewhere on a
// segment of length 2A along a known line, and the unknown is ONE BOUNDED SCALAR per vertex. That
// is continuous, and it is exactly what a smoothness objective fits: real surfaces are locally
// smooth, the displacement is not, and choosing each a to flatten the surface is a linear least
// squares problem. Fewer constraints per unknown than the four-scalar design this replaced - which
// is a real improvement - but "nothing to fit" is not what it is.
//
// So: run the attack and see. Gauss-Seidel on the Laplacian energy, which is the natural and
// cheapest form of it - for each vertex, hold the neighbours still and slide it along its own line
// to sit as close as possible to the average of its neighbours.
//
// THE CONTROL MATTERS MORE THAN THE ATTACK. Smoothing damages a clean mesh too, so "the recovered
// mesh is smooth" proves nothing on its own. The same solver is therefore run against the ORIGINAL
// mesh, and that error is the floor: no fit of this kind can beat what it does to the truth. What
// the attack is worth is the distance between those two numbers, not either one alone.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using MeshProtect;

namespace MPDiag
{
    public static class MPSmoothnessAttack
    {
        private static readonly StringBuilder Log = new StringBuilder();

        private static void Say(string line)
        {
            Log.AppendLine(line);
            Debug.Log("[ATTACK] " + line);
        }

        [MenuItem("Tools/MeshProtect Diag/Measure Smoothness Attack")]
        public static void Run()
        {
            Log.Clear();
            Say("=== how much comes back without the password ===");
            Say("run at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            var meshes = Candidates();
            if (meshes.Count == 0)
            {
                Say("no readable skinned mesh in the open scene to measure against");
                Write();
                return;
            }

            foreach (var mode in new[] { MeshProtectRoot.DisplacementMode.Normal,
                                         MeshProtectRoot.DisplacementMode.TangentSpace })
            {
                Say("");
                Say("---- " + mode + " ----");
                foreach (var mesh in meshes) Measure(mesh, mode);
            }

            Write();
        }

        /// <summary>The biggest few readable meshes on the avatar, which is what a ripper wants.</summary>
        private static List<Mesh> Candidates()
        {
            var found = new List<Mesh>();
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                if (mesh.vertexCount < 500) continue;
                if (mesh.uv == null || mesh.uv.Length != mesh.vertexCount) continue;
                if (mesh.normals == null || mesh.normals.Length != mesh.vertexCount) continue;
                if (!found.Contains(mesh)) found.Add(mesh);
            }

            return found.OrderByDescending(m => m.vertexCount).Take(3).ToList();
        }

        private static void Measure(Mesh source, MeshProtectRoot.DisplacementMode mode)
        {
            var probe = new GameObject("MPAttackProbe");
            try
            {
                var settings = probe.AddComponent<MeshProtectRoot>();
                settings.mode = mode;
                settings.distortRatio = 0.04f;
                settings.attenuateAtJoints = false;
                settings.recalculateMissingTangents = true;

                var rng = new System.Random(20260814);
                settings.keyDigits = MeshProtectCipher.GeneratePassword(rng);
                settings.variant = MeshProtectVariantGenerator.Generate(rng);

                uint key = MeshProtectCipher.PackDigits(settings.keyDigits, settings.variant);

                var result = MeshProtectMesh.Bake(source, settings, mode, key, settings.variant);
                var baked = result.mesh;
                if (baked == null) { Say($"  {source.name}: bake produced nothing"); return; }

                var original = source.vertices;
                var displaced = baked.vertices;
                var normals = source.normals;

                var amplitude = new List<Vector2>();
                baked.GetUVs(6, amplitude);
                if (amplitude.Count != displaced.Length)
                {
                    Say($"  {source.name}: {amplitude.Count} amplitudes for {displaced.Length} vertices");
                    return;
                }

                var tangents = source.tangents;
                if (tangents == null || tangents.Length != displaced.Length)
                    tangents = new Vector4[displaced.Length];
                bool tangentMode = mode == MeshProtectRoot.DisplacementMode.TangentSpace;

                var neighbours = Neighbours(source);

                // What it costs to do nothing. Every number below has to beat this one to mean
                // anything at all.
                double untouched = Mean(original, displaced);

                // The attacker picks how hard to smooth, so the measurement has to pick for them.
                // The first version of this ran a fixed 400 passes and reported how close the
                // result came to a control - which hid the answer completely: on two of three
                // meshes 400 passes left the mesh FURTHER from the original than the displacement
                // did. Smoothing is not free, the attacker knows that, and they would stop early.
                var passes = new[] { 2, 5, 10, 25, 50, 100, 400 };

                double best = untouched;
                int bestAt = 0;
                var line = new StringBuilder();

                foreach (int n in passes)
                {
                    var recovered = SlideInTheBox(displaced, normals, tangents, amplitude, neighbours, n, tangentMode);
                    double error = Mean(original, recovered);
                    line.Append($"  {n}:{error:F4}");
                    if (error < best) { best = error; bestAt = n; }
                }

                // The same solver handed the truth, at whatever setting served the attacker best.
                // Smoothing damages a clean mesh too, so this is the floor: no fit of this shape
                // can land closer to the original than this, and a "recovery" that only matches it
                // has recovered nothing it did not also destroy.
                double floor = bestAt == 0
                    ? 0
                    : Mean(original, SlideInTheBox(original, normals, tangents, amplitude, neighbours, bestAt, tangentMode));

                Say($"  {source.name}  ({displaced.Length} verts)");
                Say($"      do nothing              : {untouched:F4} m");
                Say($"      by pass count           :{line}");
                Say(bestAt == 0
                    ? "      best for the attacker   : doing nothing - every amount of smoothing " +
                      "left it further from the original"
                    : $"      best for the attacker   : {best:F4} m at {bestAt} pass(es), " +
                      $"{(1 - best / untouched) * 100:F0}% closer than doing nothing");
                if (bestAt != 0)
                    Say($"      same fit on the truth   : {floor:F4} m   <- the floor, not skill");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }
        }

        /// <summary>
        /// Slide every vertex inside the box the cipher could have moved it in, to sit as close as
        /// possible to the average of its neighbours. Gauss-Seidel on the Laplacian energy.
        ///
        /// Two things here are what make it a fair attack rather than a token one.
        ///
        /// It clamps the ACCUMULATED offset from the shipped position, not each step. Clamping the
        /// step lets a vertex walk past the bound over many passes, which is not something the
        /// attacker's own constraint would allow - and it would have made this measurement flatter
        /// than the truth in the attacker's favour.
        ///
        /// And in tangent space it solves BOTH axes. The displacement there is t·(A·a) + n·(A·b),
        /// so an attack that only slides along the normal cannot undo the tangent half no matter
        /// how long it runs - it would report that mode as strong when all that happened is that
        /// nobody tried. Whether the two modes really differ is the question this exists to answer,
        /// so the attack has to be allowed to answer it.
        /// </summary>
        private static Vector3[] SlideInTheBox(Vector3[] shipped, Vector3[] normals,
                                               Vector4[] tangents, List<Vector2> amplitude,
                                               int[][] neighbours, int iterations, bool tangentMode)
        {
            var current = (Vector3[])shipped.Clone();
            var alongT = new float[shipped.Length];
            var alongN = new float[shipped.Length];

            for (int pass = 0; pass < iterations; pass++)
            {
                for (int v = 0; v < current.Length; v++)
                {
                    var near = neighbours[v];
                    if (near.Length == 0) continue;

                    float amp = amplitude[v].x;
                    if (amp <= 1e-6f) continue;

                    Vector3 mean = Vector3.zero;
                    foreach (int u in near) mean += current[u];
                    mean /= near.Length;

                    Vector3 want = mean - current[v];
                    Vector3 n = normals[v];

                    float newN = Mathf.Clamp(alongN[v] + Vector3.Dot(want, n), -amp, amp);
                    float newT = alongT[v];

                    if (tangentMode)
                    {
                        Vector4 t4 = tangents[v];
                        Vector3 tangent = new Vector3(t4.x, t4.y, t4.z);
                        newT = Mathf.Clamp(alongT[v] + Vector3.Dot(want, tangent), -amp, amp);
                        current[v] += tangent * (newT - alongT[v]);
                        alongT[v] = newT;
                    }

                    current[v] += n * (newN - alongN[v]);
                    alongN[v] = newN;
                }
            }

            return current;
        }

        private static int[][] Neighbours(Mesh mesh)
        {
            var sets = new HashSet<int>[mesh.vertexCount];
            for (int i = 0; i < sets.Length; i++) sets[i] = new HashSet<int>();

            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var indices = mesh.GetIndices(sub);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    if (a >= sets.Length || b >= sets.Length || c >= sets.Length) continue;
                    sets[a].Add(b); sets[a].Add(c);
                    sets[b].Add(a); sets[b].Add(c);
                    sets[c].Add(a); sets[c].Add(b);
                }
            }

            return sets.Select(s => s.ToArray()).ToArray();
        }

        private static double Mean(Vector3[] a, Vector3[] b)
        {
            double total = 0;
            for (int i = 0; i < a.Length; i++) total += (a[i] - b[i]).magnitude;
            return total / Math.Max(1, a.Length);
        }

        private static void Write()
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), "mp-attack-report.txt");
            File.WriteAllText(path, Log.ToString());
            Debug.Log("[ATTACK] wrote " + path);
        }

        public static void RunBatch()
        {
            foreach (var scene in AssetDatabase.FindAssets("t:Scene")
                         .Select(AssetDatabase.GUIDToAssetPath)
                         .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal))
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    scene, UnityEditor.SceneManagement.OpenSceneMode.Single);
                if (UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true).Length > 0) break;
            }

            Run();
        }
    }
}
#endif
