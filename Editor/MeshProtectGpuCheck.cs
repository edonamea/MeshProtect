#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// Runs a variant's GENERATED decode HLSL on the GPU and compares it against the C# side.
    ///
    /// This matters more than it used to. The shader is no longer a fixed, hand-reviewed file - it
    /// is emitted per avatar from the variant's op list. A bug in the emitter, or a divergence
    /// between it and MeshProtectCipher's interpreter, would produce an avatar that simply cannot
    /// be restored, and nothing else in the pipeline would notice: the C# self check is C# on both
    /// sides. So the baker runs this before it bakes anything, and aborts on failure.
    ///
    /// The probes are generated into the variant's shader folder alongside the decode, and are
    /// never referenced by a material, so they do not reach the uploaded bundle.
    /// </summary>
    public static class MeshProtectGpuCheck
    {
        private const int SampleCount = 1024;

        private enum KeyState { Correct, CorrectShort, Locked, Wrong }

        public class Result
        {
            public bool ran;
            public string skipReason;
            public double worstError;
            public int samples;
            public int configurations;

            /// <summary>
            /// Sized from measurement, not guesswork. The cipher itself is exact - integer rounds
            /// plus a multiply by a power of two - and a dedicated probe confirmed 0/512
            /// coefficients differ at the bit level on D3D11. What remains is the float
            /// composition after it: `tangent * (amp*a) + normal * (amp*b)` is free to contract
            /// into a mad, rounding once where C# rounds twice. Measured worst case 2.2e-8 m.
            ///
            /// So 1e-6 leaves two decades over the contraction noise and six below a real formula
            /// mismatch, which lands at order 1.
            /// </summary>
            public double tolerance = 1e-6;
            public bool Passed => ran && worstError <= tolerance;
        }

        [MenuItem("Tools/Mesh Protect/Verify Shader Against C#", false, 100)]
        private static void Menu()
        {
            var settings = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<MeshProtectRoot>() : null;

            if (settings == null || !settings.HasKey)
            {
                EditorUtility.DisplayDialog(MeshProtectL10n.Tr("dialog.gpucheck.title"),
                    MeshProtectL10n.Tr("dialog.gpucheck.selectavatar"), "OK");
                return;
            }

            MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out string _);

            var result = Run(settings.variant);
            if (!result.ran)
            {
                EditorUtility.DisplayDialog(MeshProtectL10n.Tr("dialog.gpucheck.title"),
                    MeshProtectL10n.Tr("dialog.gpucheck.cantrun", result.skipReason), "OK");
                return;
            }

            string message =
                $"{(result.Passed ? "PASS" : "FAIL")}\n\n" +
                $"Variant        : {settings.variant.shaderName}\n" +
                $"Configurations : {result.configurations}\n" +
                $"Samples each   : {result.samples}\n" +
                $"Worst error    : {result.worstError:E3}\n" +
                $"Tolerance      : {result.tolerance:E3}\n\n" +
                (result.Passed
                    ? "The generated shader and MeshProtectCipher agree."
                    : "The generated shader and the C# cipher have drifted apart. Do not ship a " +
                      "bake made with this combination.");

            // The composed result body stays English on both surfaces: it is diagnostic
            // evidence, and the Console line below is its pasteable twin.
            Debug.Log("[MeshProtect] GPU check: " + message.Replace("\n", " "));
            EditorUtility.DisplayDialog(MeshProtectL10n.Tr("dialog.gpucheck.title"), message, "OK");
        }

        public static Result Run(MeshProtectVariant variant)
        {
            var result = new Result { samples = SampleCount };

            if (variant == null || !variant.IsValid(MeshProtectRoot.PasswordLength))
            {
                result.skipReason = "The protection variant is missing or malformed.";
                return result;
            }

            var shader = Shader.Find(MeshProtectShaderGen.ProbeShaderName(variant, false));
            if (shader == null)
            {
                result.skipReason = MeshProtectShaderGen.ProbeShaderName(variant, false) + " not found.";
                return result;
            }
            var fakeShadowShader = Shader.Find(MeshProtectShaderGen.ProbeShaderName(variant, true));
            if (fakeShadowShader == null)
            {
                result.skipReason = MeshProtectShaderGen.ProbeShaderName(variant, true) + " not found.";
                return result;
            }
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            {
                result.skipReason = "This device cannot render to a float render texture.";
                return result;
            }

            // A null device does not refuse the work; it accepts it and reads back zeroes, which
            // the comparison below reports as an error of about 7.5e8 against a tolerance of 1e-6.
            // The build then stops and blames the shader emitter, which is both wrong and not
            // something the reader can act on. Unity has a null device under -nographics and on a
            // headless build agent, and there is genuinely nothing to verify in either case.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                result.skipReason = "there is no graphics device - Unity was started with " +
                                    "-nographics, or this is a headless build agent. The shader " +
                                    "cannot be checked against the cipher without one.";
                return result;
            }

            var materials = new[]
            {
                new Material(shader) { hideFlags = HideFlags.HideAndDontSave },
                new Material(fakeShadowShader) { hideFlags = HideFlags.HideAndDontSave }
            };
            var rng = new System.Random(20260809);

            var normalTex = NewTex();
            var tangentTex = NewTex();
            var uvTex = NewTex();
            var readback = new Texture2D(SampleCount, 1, TextureFormat.RGBAFloat, false, true);
            var rt = new RenderTexture(SampleCount, 1, 0, RenderTextureFormat.ARGBFloat)
            {
                filterMode = FilterMode.Point
            };

            try
            {
                // Cover both modes, several passwords, and the bypass path.
                foreach (var mode in new[] { MeshProtectRoot.DisplacementMode.Normal,
                                             MeshProtectRoot.DisplacementMode.TangentSpace })
                foreach (int _ in new[] { 0, 1, 2, 3 })
                foreach (bool bypass in new[] { false, true })
                // Four states the shader must tell apart: the right password decodes, a shorter
                // right password also decodes, no password collapses, and a wrong password
                // collapses too. The last one is the whole point of the check value - a wrong
                // password used to render a scrambled mesh.
                //
                // CorrectShort is the one that earns its place. A password shorter than six digits
                // is the digits followed by "not entered", so the positions the author left out are
                // zero in the material AND zero in the key the check value was made from, and the
                // shader has to arrive at the same nibble for them as the C# does. Nothing else
                // here feeds a zero that is supposed to decode: Locked feeds one that is supposed
                // to collapse, which passes just as well if the two sides disagree about what zero
                // means. On hardware, because the mapping exists twice - KeyNibble and a ternary in
                // the generated HLSL - and this is the only thing that compares them.
                foreach (var state in new[] { KeyState.Correct, KeyState.CorrectShort,
                                              KeyState.Locked, KeyState.Wrong })
                {
                    var digits = MeshProtectCipher.GeneratePassword(rng);
                    if (state == KeyState.CorrectShort)
                        for (int i = 4; i < MeshProtectRoot.PasswordLength; i++)
                            digits[i] = MeshProtectRoot.NotEntered;

                    uint key = MeshProtectCipher.PackDigits(digits, variant);

                    // The material's check value always matches `digits`. The Wrong case types
                    // something else into the digit properties, exactly like a mis-tap in game.
                    var typed = (int[])digits.Clone();
                    if (state == KeyState.Wrong)
                        typed[3] = typed[3] == MeshProtectRoot.MaxDigit
                            ? MeshProtectRoot.MinDigit : typed[3] + 1;
                    if (state == KeyState.Locked)
                        typed[2] = 0;

                    foreach (var material in materials)
                    {
                        material.SetFloat(variant.bypassProperty, bypass ? 1f : 0f);
                        material.SetFloat(variant.modeProperty,
                            mode == MeshProtectRoot.DisplacementMode.TangentSpace ? 1f : 0f);
                        material.SetVector(variant.macProperty,
                            MeshProtectCipher.MacToVector(MeshProtectCipher.Mac(key, variant)));
                        for (int i = 0; i < MeshProtectRoot.PasswordLength; i++)
                            material.SetFloat(variant.digitProperties[i], typed[i]);
                    }

                    var normals = new Vector3[SampleCount];
                    var tangents = new Vector4[SampleCount];
                    var uv0 = new Vector2[SampleCount];
                    var uv6 = new Vector2[SampleCount];
                    FillInputs(rng, normals, tangents, uv0, uv6, normalTex, tangentTex, uvTex);

                    foreach (var material in materials)
                    {
                        material.SetTexture("_ProbeNormal", normalTex);
                        material.SetTexture("_ProbeTangent", tangentTex);
                        material.SetTexture("_ProbeUV", uvTex);

                        Graphics.Blit(null, rt, material);

                        var previous = RenderTexture.active;
                        RenderTexture.active = rt;
                        readback.ReadPixels(new Rect(0, 0, SampleCount, 1), 0, 0);
                        readback.Apply();
                        RenderTexture.active = previous;

                        var gpu = readback.GetPixels();
                        for (int i = 0; i < SampleCount; i++)
                        {
                            bool decodes = !bypass && (state == KeyState.Correct ||
                                                       state == KeyState.CorrectShort);
                            Vector3 expected = !decodes
                                ? Vector3.zero
                                : MeshProtectSelfCheck.Decode(
                                    mode == MeshProtectRoot.DisplacementMode.TangentSpace,
                                    key, variant, normals[i], tangents[i], uv0[i], uv6[i]);
                            Vector3 actual = new Vector3(gpu[i].r, gpu[i].g, gpu[i].b);

                            double error = (actual - expected).magnitude;
                            if (error > result.worstError) result.worstError = error;
                        }

                        result.configurations++;
                    }
                }

                result.ran = true;
            }
            finally
            {
                RenderTexture.active = null;
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(readback);
                UnityEngine.Object.DestroyImmediate(normalTex);
                UnityEngine.Object.DestroyImmediate(tangentTex);
                UnityEngine.Object.DestroyImmediate(uvTex);
                foreach (var material in materials)
                    UnityEngine.Object.DestroyImmediate(material);
            }

            return result;
        }

        private static Texture2D NewTex()
        {
            return new Texture2D(SampleCount, 1, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        private static void FillInputs(System.Random rng, Vector3[] normals, Vector4[] tangents,
                                       Vector2[] uv0, Vector2[] uv6,
                                       Texture2D normalTex, Texture2D tangentTex, Texture2D uvTex)
        {
            var nPixels = new Color[SampleCount];
            var tPixels = new Color[SampleCount];
            var uvPixels = new Color[SampleCount];

            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 n = Random(rng).normalized;
                Vector3 t = Vector3.Cross(n, Random(rng)).normalized;
                float w = rng.Next(2) == 0 ? 1f : -1f;

                normals[i] = n;
                tangents[i] = new Vector4(t.x, t.y, t.z, w);

                // Realistic UV0 values. Their exact bits are what the identity hash consumes, so
                // they travel through the RGBAFloat probe texture unmodified.
                uv0[i] = new Vector2((float)rng.NextDouble(), (float)rng.NextDouble());

                // Every eighth sample has zero amplitude, exercising the "never displaced" early
                // out that protects vertices shared with an unprotected sub-mesh.
                float amplitude = (i % 8 == 0) ? 0f : (float)(rng.NextDouble() * 0.2 + 0.001);
                uv6[i] = new Vector2(amplitude, 0f);

                nPixels[i] = new Color(n.x, n.y, n.z, w);
                tPixels[i] = new Color(t.x, t.y, t.z, 0f);
                uvPixels[i] = new Color(uv0[i].x, uv0[i].y, uv6[i].x, uv6[i].y);
            }

            normalTex.SetPixels(nPixels); normalTex.Apply(false, false);
            tangentTex.SetPixels(tPixels); tangentTex.Apply(false, false);
            uvTex.SetPixels(uvPixels); uvTex.Apply(false, false);
        }

        private static Vector3 Random(System.Random rng)
        {
            Vector3 v;
            do { v = new Vector3(Range(rng), Range(rng), Range(rng)); }
            while (v.sqrMagnitude < 1e-4f);
            return v;
        }

        private static float Range(System.Random rng) => (float)(rng.NextDouble() * 2.0 - 1.0);
    }
}
#endif
