using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// Re-implements the shader's decode in C# and checks that it returns the original mesh.
    ///
    /// SCOPE - read this before trusting it:
    ///
    /// Everything here is C#. The decode below is a hand-written copy of the HLSL, not the HLSL
    /// itself, so this proves the baker, MeshProtectCipher and this copy agree with each other -
    /// nothing more. Editing only custom_insert.hlsl, or making the same mistake in the baker and
    /// in this file, passes silently.
    ///
    /// What it does catch, which is most of what goes wrong in practice: channel layout mistakes,
    /// mode mismatches, a wrong vertex identity, and broken blend shape compensation.
    ///
    /// For the C#-vs-HLSL question use Tools > Mesh Protect > Verify Shader Against C#, which runs
    /// the real custom_insert.hlsl on the GPU and compares it against MeshProtectCipher.
    /// For skinning, use Tools > Mesh Protect > Measure Skinning Residual.
    /// </summary>
    public static class MeshProtectSelfCheck
    {
        public class Result
        {
            public double worstVertexError;
            public double worstBlendShapeError;
            public string worstBlendShapeName = "-";
            public int blendShapeFramesChecked;
            public double tolerance;

            public bool Passed => worstVertexError <= tolerance && worstBlendShapeError <= tolerance;
        }

        public static Result Verify(Mesh source, Mesh baked, MeshProtectRoot.DisplacementMode mode,
                                    uint key, MeshProtectVariant variant)
        {
            bool tangentSpace = mode == MeshProtectRoot.DisplacementMode.TangentSpace;
            int count = source.vertexCount;

            var result = new Result
            {
                tolerance = Math.Max(1e-4, source.bounds.size.magnitude * 1e-5)
            };

            var originalVertices = source.vertices;
            var bakedVertices = baked.vertices;
            var normals = baked.normals;
            var tangents = baked.tangents;

            // UV0 is never modified by the bake, so this is the same identity the shader will see.
            var uv0 = baked.uv;
            var uv6 = new List<Vector2>(); baked.GetUVs(6, uv6);

            if (uv0 == null || uv0.Length != count || uv6.Count != count || bakedVertices.Length != count)
                throw new InvalidOperationException(
                    $"Self check: '{source.name}' channel sizes do not match the vertex count.");

            for (int v = 0; v < count; v++)
            {
                Vector3 restored = bakedVertices[v] -
                    Decode(tangentSpace, key, variant, normals[v], Tangent(tangents, v, count),
                           uv0[v], uv6[v]);
                double error = (restored - originalVertices[v]).magnitude;
                if (error > result.worstVertexError) result.worstVertexError = error;
            }

            VerifyBlendShapes(source, baked, tangentSpace, key, variant, normals, tangents,
                              originalVertices, bakedVertices, uv0, uv6, result);

            return result;
        }

        /// <summary>
        /// Applies each shape at 50% and 100%, decodes against the DEFORMED basis - which is what
        /// the vertex shader actually receives - and compares against the original mesh with the
        /// same shape applied.
        ///
        /// Shapes whose delta normals and tangents are all zero cannot be affected by the
        /// compensation, so they are skipped. On a typical avatar that is nearly all of them.
        ///
        /// Multi-frame shapes are checked frame by frame rather than by simulating Unity's
        /// interpolation between adjacent frames. That is sufficient here because every term is
        /// linear in the weight: the displacement is a*tangent + b*normal, the compensation is
        /// a*deltaTangent + b*deltaNormal, and Unity's frame interpolation is itself a linear
        /// combination of frames. A linear function that is exact at each frame is exact at every
        /// blend between them. (This argument would NOT hold if the bitangent were still in use -
        /// cross(n + dn, t + dt) has a term quadratic in the weight, which is one more reason it
        /// was removed.)
        /// </summary>
        private static void VerifyBlendShapes(Mesh source, Mesh baked, bool tangentSpace,
                                              uint key, MeshProtectVariant variant,
                                              Vector3[] normals, Vector4[] tangents,
                                              Vector3[] originalVertices, Vector3[] bakedVertices,
                                              Vector2[] uv0, List<Vector2> uv6, Result result)
        {
            int shapeCount = source.blendShapeCount;
            if (shapeCount == 0) return;

            int count = source.vertexCount;
            var sourceDeltaV = new Vector3[count];
            var sourceDeltaN = new Vector3[count];
            var sourceDeltaT = new Vector3[count];
            var bakedDeltaV = new Vector3[count];
            var bakedDeltaN = new Vector3[count];
            var bakedDeltaT = new Vector3[count];

            for (int shape = 0; shape < shapeCount; shape++)
            {
                int frames = source.GetBlendShapeFrameCount(shape);
                for (int frame = 0; frame < frames; frame++)
                {
                    source.GetBlendShapeFrameVertices(shape, frame, sourceDeltaV, sourceDeltaN, sourceDeltaT);

                    bool relevant = false;
                    for (int v = 0; v < count && !relevant; v++)
                        relevant = sourceDeltaN[v].sqrMagnitude > 1e-12f ||
                                   (tangentSpace && sourceDeltaT[v].sqrMagnitude > 1e-12f);
                    if (!relevant) continue;

                    baked.GetBlendShapeFrameVertices(shape, frame, bakedDeltaV, bakedDeltaN, bakedDeltaT);
                    result.blendShapeFramesChecked++;

                    foreach (float weight in new[] { 0.5f, 1.0f })
                    {
                        for (int v = 0; v < count; v++)
                        {
                            Vector3 deformedNormal = normals[v] + sourceDeltaN[v] * weight;
                            Vector4 deformedTangent = Tangent(tangents, v, count);
                            deformedTangent += new Vector4(sourceDeltaT[v].x, sourceDeltaT[v].y,
                                                           sourceDeltaT[v].z, 0f) * weight;

                            Vector3 restored = bakedVertices[v] + bakedDeltaV[v] * weight -
                                Decode(tangentSpace, key, variant, deformedNormal, deformedTangent,
                                       uv0[v], uv6[v]);
                            Vector3 expected = originalVertices[v] + sourceDeltaV[v] * weight;

                            double error = (restored - expected).magnitude;
                            if (error > result.worstBlendShapeError)
                            {
                                result.worstBlendShapeError = error;
                                result.worstBlendShapeName = source.GetBlendShapeName(shape) + " @" + weight;
                            }
                        }
                    }
                }
            }
        }

        private static Vector4 Tangent(Vector4[] tangents, int index, int count)
        {
            return tangents != null && tangents.Length == count ? tangents[index] : new Vector4(1, 0, 0, 1);
        }

        /// <summary>Mirror of lilMeshProtectDecode() in Shaders/custom_insert.hlsl.</summary>
        public static Vector3 Decode(bool tangentSpace, uint key, MeshProtectVariant variant,
                                     Vector3 normal, Vector4 tangent, Vector2 uv0, Vector2 uv6)
        {
            float amplitude = uv6.x;
            if (!(amplitude > 0f)) return Vector3.zero;

            MeshProtectCipher.CoefficientsForVertex(uv0, key, variant, out float a, out float b);

            if (tangentSpace)
            {
                Vector3 t = new Vector3(tangent.x, tangent.y, tangent.z);
                return t * (amplitude * a) + normal * (amplitude * b);
            }
            return normal * (amplitude * a);
        }
    }
}
