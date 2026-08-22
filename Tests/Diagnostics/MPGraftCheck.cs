#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshProtect;
using UnityEditor;
using UnityEngine;

namespace MPDiag
{
    /// <summary>
    /// Three questions about a foreign-shader graft that cannot be answered by reading the code,
    /// asked once per shader family installed in this project:
    ///
    ///   1. does the patched shader compile
    ///   2. did the decode land in EVERY pass
    ///   3. does that decode still agree with the C# cipher on the GPU
    ///
    /// Nothing else. The refusal logic is checked by breaking a source by hand and watching it
    /// refuse, which is not worth keeping as code. (3) is the one that is never optional: the mesh
    /// is displaced permanently, so a decode wrong by one bit is an avatar nobody can reopen.
    ///
    ///   Unity.exe -batchmode -projectPath &lt;proj&gt; -executeMethod MPDiag.MPGraftCheck.Run
    ///
    /// NOT with -nographics (the GPU comparison needs a real device, and a null one reads back
    /// zeroes that look like a catastrophic mismatch) and NOT with -quit (this exits itself).
    /// </summary>
    public static class MPGraftCheck
    {
        public static void Run()
        {
            var go = new GameObject("MPGraftCheck") { hideFlags = HideFlags.HideAndDontSave };
            int failed = 0;

            try
            {
                var settings = go.AddComponent<MeshProtectRoot>();
                settings.outputFolder = "Assets/_MPGraftCheck";

                var rng = new System.Random(20260815);
                settings.keyDigits = MeshProtectCipher.GeneratePassword(rng);
                settings.variant = MeshProtectVariantGenerator.Generate(rng);
                var variant = settings.variant;

                MeshProtectShaderGen.EnsureGenerated(settings, variant, out string _, force: true);

                // One representative per family, found rather than named: a hard-coded list goes
                // stale the moment a family renames a variant, and reports it as a pass.
                var hosts = Representatives();
                if (hosts.Count == 0) throw new Exception("no graftable shader is installed.");

                foreach (var (recipe, host) in hosts)
                {
                    Debug.Log($"[graft] --- {recipe.displayName}: {host.name}");
                    failed += CheckOne(settings, variant, go, host);
                }

                failed += CheckWholeBake(settings, variant, hosts[0].Item2);

                var gpu = MeshProtectGpuCheck.Run(variant);
                failed += Say($"the decode matches the cipher (worst {gpu.worstError:E3})",
                              gpu.Passed, gpu.ran ? null : "SKIPPED - " + gpu.skipReason);
            }
            catch (Exception e)
            {
                failed++;
                Debug.LogError("[graft] EXCEPTION " + e);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }

            Debug.Log(failed == 0 ? "[graft] === PASSED ===" : $"[graft] === {failed} FAILED ===");
            EditorApplication.Exit(failed == 0 ? 0 : 1);
        }

        /// <summary>
        /// One whole bake, on a renderer wearing a foreign material.
        ///
        /// The graft checks above test the shader in isolation and the four existing suites test a
        /// full bake on lilToon. Nothing had ever run the JOIN: a real Apply that goes through
        /// ConvertForeignMaterial, assigns the grafted material, displaces the mesh for it and
        /// leaves the two agreeing with each other. Every wiring mistake between those two halves
        /// lives exactly here, and none of it is visible by reading either half.
        /// </summary>
        private static int CheckWholeBake(MeshProtectRoot settings, MeshProtectVariant variant,
                                          Shader host)
        {
            int failed = 0;
            var avatar = new GameObject("MPGraftBake") { hideFlags = HideFlags.HideAndDontSave };

            try
            {
                // The bake refuses an avatar with no descriptor, because it cannot build the
                // unlock chain - and refusing is the whole-avatar path, so without this the bake
                // "passes" by protecting nothing at all.
                avatar.AddComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();

                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.transform.SetParent(avatar.transform);

                // A readable copy: Unity's primitives are not, and the bake refuses a mesh it
                // cannot read - which would make this pass by skipping everything.
                var source = UnityEngine.Object.Instantiate(quad.GetComponent<MeshFilter>().sharedMesh);
                source.name = "MPGraftBakeMesh";
                quad.GetComponent<MeshFilter>().sharedMesh = source;

                var renderer = quad.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = new Material(host);

                var report = MeshProtectPipeline.Apply(avatar, settings, "Assets/_MPGraftCheck");

                failed += Say("  the bake protected the foreign renderer",
                              report.appliedProtection && report.materialCount > 0,
                              $"materials={report.materialCount} skipped={report.skippedRenderers}");

                var landed = renderer.sharedMaterial;
                failed += Say("  the renderer ended up on the grafted shader",
                              landed != null &&
                              MeshProtectPipeline.IsProtectShader(landed, variant.shaderName),
                              landed == null ? "<none>" : landed.shader.name);

                // The two halves have to agree. A displaced mesh whose material cannot decode is
                // the one outcome no password recovers, so this asks both questions at once.
                var baked = quad.GetComponent<MeshFilter>().sharedMesh;
                var uv6 = new List<Vector4>();
                if (baked != null) baked.GetUVs(6, uv6);
                failed += Say("  the mesh was displaced and carries an amplitude",
                              baked != null && baked != source && uv6.Any(v => v.x > 0f),
                              $"uv6 entries={uv6.Count}");

                foreach (string w in report.warnings) Debug.Log("[graft]   bake says: " + w);
            }
            catch (Exception e)
            {
                failed++;
                Debug.LogError("[graft] bake EXCEPTION " + e);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(avatar);
            }

            return failed;
        }

        private static int CheckOne(MeshProtectRoot settings, MeshProtectVariant variant,
                                    GameObject avatar, Shader host)
        {
            int failed = 0;

            MeshProtectForeignShader.EnsureGraft(settings, variant, host, out string ensureWhy, true);
            if (ensureWhy != null) Debug.LogWarning("[graft] " + ensureWhy);

            var grafted = MeshProtectForeignShader.FindGraft(settings, variant, host, out string why);
            failed += Say("  the graft compiles", grafted != null, why);
            if (grafted == null)
            {
                // Which half failed: no shader under that name at all, or one Unity refused.
                foreach (var info in ShaderUtil.GetAllShaderInfo()
                                               .Where(i => i.name.StartsWith(variant.shaderName) &&
                                                           i.hasErrors))
                {
                    var broken = Shader.Find(info.name);
                    if (broken == null) continue;
                    for (int i = 0; i < ShaderUtil.GetShaderMessageCount(broken); i++)
                    {
                        var m = ShaderUtil.GetShaderMessages(broken)[i];
                        Debug.Log($"[graft]   {info.name}: {m.message} {m.messageDetails} " +
                                  $"({Path.GetFileName(m.file)}:{m.line})");
                    }
                }
                return failed;
            }

            // Every pass, counted off the copy: one `#pragma vertex` is one pass that draws the
            // mesh, and the mesh is already displaced, so a pass without a decode draws noise.
            //
            // Searched from the GRAFT ROOT, not the .shader's own folder: XSToon keeps its vertex
            // function in a sibling CGIncludes folder, so looking beside the .shader found zero
            // injections and reported a failure that was entirely the harness's.
            string file = Directory
                .GetFiles(MeshProtectShaderGen.FolderFor(settings, variant), "*.shader",
                          SearchOption.AllDirectories)
                .First(f => File.ReadAllText(f).Contains(grafted.name));

            var folder = GraftRoot(file);
            int passes = Count(File.ReadAllText(file), "#pragma vertex");
            int calls = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                                 .Where(f => (f.EndsWith(".shader") || f.EndsWith(".cginc") ||
                                              f.EndsWith(".hlsl")) &&
                                             // Excluded: it DEFINES mpDecodeAppdata, twice, and
                                             // counting those as injections read 7 for 5 passes.
                                             !f.EndsWith("mp_decode.hlsl"))
                                 .Sum(f => Count(File.ReadAllText(f), "mpDecodeAppdata("));

            // Not passes == calls: several passes can share one vertex function, and then one
            // injection covers all of them. What must hold is that there IS one, and that no pass
            // is left over - which is what the graft refuses on, so reaching here means it held.
            failed += Say($"  {passes} pass(es) decode through {calls} injection(s)",
                          passes > 0 && calls > 0 && calls <= passes);

            // Two ways a pass can draw the mesh without going through anything this patched, both
            // silent and both permanent. UsePass borrows a pass from the author's own un-grafted
            // shader; Fallback hands the whole draw to a shader that has no decode at all when no
            // SubShader here is supported. 68 shaders across these five families ship a Fallback.
            string patched = File.ReadAllText(file);
            failed += Say("  no pass escapes through UsePass or Fallback",
                          !patched.Contains("UsePass \"") &&
                          !Regex.IsMatch(patched, "(?im)^[ \t]*Fallback\\s+\""));

            // The material has to be able to RECEIVE the password, not just decode it. The digits
            // arrive as animated material properties, so one the shader does not expose stays on
            // its shipped zero and the avatar never opens - with nothing else looking wrong.
            var material = new Material(grafted) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var wanted = variant.digitProperties
                    .Concat(new[] { variant.bypassProperty, variant.modeProperty,
                                    variant.macProperty })
                    .ToArray();
                var absent = wanted.Where(p => !material.HasProperty(p)).ToArray();
                failed += Say($"  the material exposes all {wanted.Length} key properties",
                              absent.Length == 0, string.Join(", ", absent));

                // Every "is this ours" test in the pipeline goes through IsProtectShader. One that
                // said no would have the next bake displace an already-displaced mesh.
                failed += Say("  the pipeline recognises it as this avatar's",
                              MeshProtectPipeline.IsProtectShader(material, variant.shaderName));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }

            failed += Say("  the shipped name does not name the host",
                          !grafted.name.Contains("poiyomi") && !grafted.name.Contains("Poiyomi") &&
                          !grafted.name.Contains("Xiexe") && !grafted.name.Contains("Sunao") &&
                          !grafted.name.Contains("UnityChan") && !grafted.name.Contains("GeoTetra"),
                          grafted.name);

            return failed;
        }

        /// <summary>One installed shader per recipe, preferring the shortest name - which is
        /// reliably the family's plain variant rather than a stencil or tessellation special.</summary>
        private static List<(MeshProtectForeignShader.Recipe, Shader)> Representatives()
        {
            var found = new List<(MeshProtectForeignShader.Recipe, Shader)>();

            foreach (var recipe in MeshProtectForeignShader.Recipes)
            {
                var best = ShaderUtil.GetAllShaderInfo()
                    .Where(i => !i.hasErrors && i.supported)
                    .Select(i => Shader.Find(i.name))
                    .Where(s => s != null && MeshProtectForeignShader.RecipeFor(s) == recipe)
                    .OrderBy(s => s.name.Length)
                    .FirstOrDefault();

                if (best != null) found.Add((recipe, best));
                else Debug.Log($"[graft] (not installed: {recipe.displayName})");
            }
            return found;
        }

        /// <summary>The graft's own folder: the one holding mp_decode.hlsl, walking up from the
        /// .shader, which for XSToon sits a level down in Shaders/.</summary>
        private static string GraftRoot(string shaderFile)
        {
            for (var dir = Path.GetDirectoryName(shaderFile); dir != null;
                 dir = Path.GetDirectoryName(dir))
                if (File.Exists(Path.Combine(dir, "mp_decode.hlsl"))) return dir;

            return Path.GetDirectoryName(shaderFile);
        }

        private static int Say(string what, bool ok, string detail = null)
        {
            Debug.Log($"[graft] [{(ok ? "PASS" : "FAIL")}] {what}" +
                      (detail == null ? "" : "  <- " + detail));
            return ok ? 0 : 1;
        }

        private static int Count(string haystack, string needle)
        {
            int n = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
#endif
