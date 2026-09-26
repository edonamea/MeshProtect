using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MeshProtect
{
    /// <summary>
    /// Bakes the displacement into a copy of a mesh.
    ///
    /// The displacement is GENERATED per vertex from the key, not stored. See MeshProtectCipher
    /// for why - in short, storing per-vertex factors makes the restore linear in a handful of
    /// shared unknowns and therefore solvable by least squares without the password.
    ///
    /// The only per-vertex datum written here is a displacement AMPLITUDE in TEXCOORD6.x. The
    /// shader needs it for two reasons that cannot be derived from the mesh at runtime: vertices
    /// shared with an unprotected sub-mesh must not be displaced at all, and joint attenuation
    /// depends on bone weights, which the vertex shader no longer has. The amplitude leaks only
    /// "how far this vertex moved", never the direction, so it adds no solvable degree of freedom.
    ///
    /// TEXCOORD7 is left untouched, unlike the previous version.
    /// </summary>
    public static class MeshProtectMesh
    {
        public class Result
        {
            public Mesh mesh;
            public string warning;
        }

        /// <summary>True when the mesh can be displaced in tangent space, possibly after a recalculation.</summary>
        public static bool CanUseTangentSpace(Mesh mesh, bool allowRecalculate)
        {
            if (mesh == null) return false;
            var tangents = mesh.tangents;
            if (tangents != null && tangents.Length == mesh.vertexCount) return true;
            if (!allowRecalculate) return false;
            var uv = mesh.uv;
            return uv != null && uv.Length == mesh.vertexCount;   // RecalculateTangents needs UV0
        }

        /// <summary>Vertex identity is derived from UV0, so a mesh without one cannot be protected.</summary>
        public static bool HasVertexIdentity(Mesh mesh)
        {
            if (mesh == null) return false;
            var uv = mesh.uv;
            return uv != null && uv.Length == mesh.vertexCount;
        }

        /// <param name="skipVertex">
        /// Vertices belonging to sub-meshes whose material is excluded. They keep their original
        /// position and get a zero amplitude, so an unprotected material on the same mesh still
        /// renders correctly.
        /// </param>
        /// <param name="effectiveMode">
        /// Decided once for the whole avatar by the baker, not per mesh. It has to match the
        /// `_KP_Mode` written to the materials - the shader reads the mode from the material, so a
        /// per-mesh fallback would bake with one formula and decode with the other.
        /// </param>
        /// <summary>
        /// Can this mesh be protected at all, and if not, what does the author need to hear?
        ///
        /// The same conditions Bake refuses on, asked BEFORE anything has been changed. Bake finds
        /// them far too late: by then the renderer is wearing the decode shader, so there is no
        /// longer a way to leave the mesh alone - the only exits are a broken avatar or a refused
        /// upload, and refusing was what this tool did. A mesh with no UV0, or a mesh somebody
        /// forgot to tick Read/Write on, is not a reason nobody can upload their avatar. It is a
        /// reason that one mesh ships the way it already was.
        ///
        /// Bake keeps its own checks. They are unreachable now, and that is what they are for: if
        /// one ever fires, this function and Bake have drifted apart, and stopping is right because
        /// the alternative is a half-displaced mesh.
        /// </summary>
        public static bool CanProtect(Mesh source, out string why)
        {
            why = null;

            if (source == null) { why = "there is no mesh"; return false; }

            if (!source.isReadable)
            {
                why = "it is not readable - tick Read/Write Enabled on its importer to protect it";
                return false;
            }

            var normals = source.normals;
            if (normals == null || normals.Length != source.vertexCount)
            {
                why = "it has no usable normals - set its importer to Import or Calculate normals " +
                      "to protect it";
                return false;
            }

            if (!HasVertexIdentity(source))
            {
                why = "it has no UV0, and every vertex's identity is derived from UV0";
                return false;
            }

            // UV6 is written for EVERY vertex by the bake, so whatever is in it now would be gone -
            // including on sub-meshes the bake was told to skip. That is why "add the material to
            // Ignored Materials" cannot save a shared mesh: the material stops being converted and
            // the channel is overwritten anyway. The whole mesh has to be left alone instead.
            //
            // Rather than enumerate who reads UV6 - lilToon's ID Mask is the one consumer this tool
            // knows about, and upstream tools and hand-baked data do not announce themselves - ask
            // the mesh whether the channel is occupied. Read as Vector4 so a three- or
            // four-component channel is not judged on its first two, and require a non-zero value:
            // some importers hand back an all-zero UV6 that carries nothing.
            var existingUv6 = new List<Vector4>();
            source.GetUVs(6, existingUv6);
            if (existingUv6.Any(v => v != Vector4.zero))
            {
                why = "it already has data in UV6, which is the channel the displacement amplitude " +
                      "goes into - protecting it would overwrite that for the whole mesh";
                return false;
            }

            return true;
        }

        public static Result Bake(Mesh source, MeshProtectRoot settings,
                                  MeshProtectRoot.DisplacementMode effectiveMode,
                                  uint key, MeshProtectVariant variant, bool[] skipVertex = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!source.isReadable)
                throw new InvalidOperationException(
                    $"Mesh '{source.name}' is not readable. Enable Read/Write Enabled on its importer and try again.");

            var normals = source.normals;
            if (normals == null || normals.Length != source.vertexCount)
                throw new InvalidOperationException(
                    $"Mesh '{source.name}' has no usable normals. Set the importer to Import or Calculate normals.");

            if (!HasVertexIdentity(source))
                throw new InvalidOperationException(
                    $"Mesh '{source.name}' has no UV0. Vertex identity is derived from it, so the mesh " +
                    "cannot be protected. Exclude its material, or give the mesh a UV0.");

            // UV6 is written for EVERY vertex further down, so whatever is in it now is gone -
            // including on sub-meshes this bake was told to skip. skipVertex holds the position
            // still; it does not hold the channel. That is why "add the material to Ignored
            // Materials" cannot save a shared mesh: the material stops being converted and the
            // channel is overwritten anyway.
            //
            // Rather than enumerate who reads UV6 - lilToon's ID Mask is the one consumer this tool
            // knows about, and upstream tools and hand-baked data are not going to announce
            // themselves - ask the mesh whether the channel is occupied. Read as Vector4 so a
            // three- or four-component channel is not judged on its first two, and require a
            // non-zero value: some importers hand back an all-zero UV6 that carries nothing.
            var existingUv6 = new List<Vector4>();
            source.GetUVs(6, existingUv6);
            if (existingUv6.Any(v => v != Vector4.zero))
                throw new InvalidOperationException(
                    $"Mesh '{source.name}' already has data in UV6, and that is the channel this " +
                    "tool writes each vertex's displacement amplitude into. Protecting it would " +
                    "overwrite that data for the whole mesh - every sub-mesh, including any this " +
                    "bake was told to leave alone - and the avatar would upload looking wrong with " +
                    "nothing saying why.\n\n" +
                    "Either clear UV6 on this mesh, or leave this renderer out by listing the ones " +
                    "you do want in Target Renderers.");

            var uv0 = source.uv;

            bool tangentMode = effectiveMode == MeshProtectRoot.DisplacementMode.TangentSpace;
            string warning = null;

            var mesh = Object.Instantiate(source);
            mesh.name = source.name + "_protected";

            var tangents = mesh.tangents;
            if (tangentMode && (tangents == null || tangents.Length != mesh.vertexCount))
            {
                // The baker already established this is possible; recalculating on the copy also
                // means the shader reads back exactly the tangents used here.
                mesh.RecalculateTangents();
                tangents = mesh.tangents;
                warning = $"Mesh '{source.name}' had no tangents, they were recalculated for the bake.";

                if (tangents == null || tangents.Length != mesh.vertexCount)
                    throw new InvalidOperationException(
                        $"Mesh '{source.name}' has no tangents and they could not be recalculated. " +
                        "Switch Mode to Normal, or enable Recalculate Missing Tangents.");
            }

            int count = mesh.vertexCount;
            var vertices = mesh.vertices;
            var rigidity = ComputeRigidity(source, count, settings);

            float scale = source.bounds.size.magnitude;
            if (scale <= 0f || float.IsNaN(scale)) scale = 1f;
            float maxDisplacement = scale * settings.distortRatio;

            // x = amplitude in metres, y reserved.
            var amplitudeChannel = new Vector2[count];

            // Per-vertex displacement expressed in the shader's basis, kept for blend shape compensation.
            var compTangent = new float[count];
            var compNormal = new float[count];

            for (int v = 0; v < count; v++)
            {
                float amplitude = maxDisplacement * rigidity[v];
                if (skipVertex != null && v < skipVertex.Length && skipVertex[v]) amplitude = 0f;
                if (amplitude < 1e-6f)
                {
                    amplitudeChannel[v] = Vector2.zero;
                    continue;
                }

                amplitudeChannel[v] = new Vector2(amplitude, 0f);

                MeshProtectCipher.CoefficientsForVertex(uv0[v], key, variant,
                                                        out float a, out float b);

                if (tangentMode)
                {
                    // Tangent and normal are the only two per-vertex directions Unity skins with
                    // the same blended matrix it applies to the position, so they are the only two
                    // that restore exactly. The bitangent is deliberately absent: it would have to
                    // be rebuilt in the shader as cross() of the SKINNED basis, and
                    // cross(A*n, A*t) != A*cross(n, t) unless A is orthogonal, which a blended
                    // skinning matrix is not. Measured on a real avatar, that third axis was the
                    // entire source of the residual (up to 4.4% of the displacement at extreme
                    // joint angles); dropping it takes the error to floating point noise.
                    float dt = amplitude * a;
                    float dn = amplitude * b;

                    compTangent[v] = dt;
                    compNormal[v] = dn;

                    Vector4 t4 = tangents[v];
                    vertices[v] += new Vector3(t4.x, t4.y, t4.z) * dt + normals[v] * dn;
                }
                else
                {
                    float dn = amplitude * a;
                    compNormal[v] = dn;
                    vertices[v] += normals[v] * dn;
                }
            }

            mesh.vertices = vertices;
            mesh.SetUVs(6, amplitudeChannel);   // TEXCOORD6 -> input.uv6

            CompensateBlendShapes(source, mesh, count, tangentMode, compTangent, compNormal);

            // CPU culling must cover both the displaced mesh and its shader-restored shape.
            // Keep the source bounds too, including any extra room supplied by the author.
            mesh.RecalculateBounds();
            var bounds = mesh.bounds;
            bounds.Encapsulate(source.bounds.min);
            bounds.Encapsulate(source.bounds.max);
            mesh.bounds = bounds;

            return new Result { mesh = mesh, warning = warning };
        }

        /// <summary>
        /// Blend shapes deform the mesh before skinning, and they carry delta normals/tangents.
        /// The shader subtracts the DEFORMED basis, but we added the UNDEFORMED one, leaving a
        /// residual proportional to the delta. Both sides are linear in the blend weight, so
        /// folding the same term into deltaVertices cancels it exactly at every weight.
        /// </summary>
        private static void CompensateBlendShapes(Mesh source, Mesh mesh, int count, bool tangentMode,
                                                  float[] compTangent, float[] compNormal)
        {
            int shapeCount = source.blendShapeCount;
            if (shapeCount == 0) return;

            mesh.ClearBlendShapes();

            var deltaVertices = new Vector3[count];
            var deltaNormals = new Vector3[count];
            var deltaTangents = new Vector3[count];

            for (int shape = 0; shape < shapeCount; shape++)
            {
                string name = source.GetBlendShapeName(shape);
                int frames = source.GetBlendShapeFrameCount(shape);

                for (int frame = 0; frame < frames; frame++)
                {
                    source.GetBlendShapeFrameVertices(shape, frame, deltaVertices, deltaNormals, deltaTangents);
                    float weight = source.GetBlendShapeFrameWeight(shape, frame);

                    for (int v = 0; v < count; v++)
                    {
                        if (tangentMode)
                            deltaVertices[v] += deltaTangents[v] * compTangent[v] + deltaNormals[v] * compNormal[v];
                        else
                            deltaVertices[v] += deltaNormals[v] * compNormal[v];
                    }

                    mesh.AddBlendShapeFrame(name, weight, deltaVertices, deltaNormals, deltaTangents);
                }
            }
        }

        /// <summary>
        /// 1 where a single bone owns the vertex, falling off where bones blend.
        ///
        /// Linear blend skinning contracts the basis in blend regions (|sum(w*A)*n| &lt; 1), so the
        /// shader's restore leaves a residual proportional to the displacement there. Scaling the
        /// displacement down is what keeps elbows and shoulders from bulging.
        /// </summary>
        private static float[] ComputeRigidity(Mesh mesh, int count, MeshProtectRoot settings)
        {
            var rigidity = new float[count];
            for (int i = 0; i < count; i++) rigidity[i] = 1f;

            if (!settings.attenuateAtJoints) return rigidity;

            try
            {
                var bonesPerVertex = mesh.GetBonesPerVertex();
                if (bonesPerVertex.Length == count)
                {
                    var weights = mesh.GetAllBoneWeights();
                    int cursor = 0;
                    for (int v = 0; v < count; v++)
                    {
                        int influences = bonesPerVertex[v];
                        // Unity stores influences sorted by descending weight.
                        float dominant = influences > 0 ? weights[cursor].weight : 1f;
                        cursor += influences;
                        rigidity[v] = Mathf.Pow(Mathf.Clamp01(dominant), settings.rigidityExponent);
                    }
                    return rigidity;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MeshProtect] Falling back to legacy bone weights on '{mesh.name}': {e.Message}");
            }

            var legacy = mesh.boneWeights;
            if (legacy != null && legacy.Length == count)
            {
                for (int v = 0; v < count; v++)
                    rigidity[v] = Mathf.Pow(Mathf.Clamp01(legacy[v].weight0), settings.rigidityExponent);
            }

            return rigidity;
        }
    }
}
