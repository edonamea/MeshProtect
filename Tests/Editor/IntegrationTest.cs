#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MeshProtect;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;
using Object = UnityEngine.Object;

namespace MPTest
{
    /// <summary>A synthetic test copy through the real SDK callback chain; never uploads.</summary>
    public static class IntegrationTest
    {
        public static void Run() => EditorApplication.Exit(RunCore());

        public static int RunCore()
        {
            var furyApi = FindType("com.vrcfury.api.FuryComponents");
            var mergeType = FindType("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
            if (furyApi == null || mergeType == null || FindType("nadena.dev.ndmf.BuildContext") == null)
            {
                Debug.Log("[INTEGRATION] SKIP: VRCFury, Modular Avatar and NDMF are required.");
                return 0;
            }

            int failed = 0;
            void Check(bool ok, string label, string detail)
            {
                if (!ok) failed++;
                Debug.Log($"[INTEGRATION] {(ok ? "PASS" : "FAIL")} {label}: {detail}");
            }

            GameObject source = null, built = null;
            try
            {
                var lilToon = Shader.Find("lilToon");
                if (lilToon == null) throw new InvalidOperationException("lilToon is required.");

                // Reuse the existing small mesh/shader fixture without running its direct-hook test.
                var fixture = typeof(EndToEndTest).GetMethod("BuildMinimalAvatar",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (fixture == null) throw new MissingMethodException("EndToEndTest.BuildMinimalAvatar");
                var arguments = new object[] { lilToon, "IntegrationAvatar", 20260926, null, null };
                source = (GameObject)fixture.Invoke(null, arguments);
                var sourceRenderer = (SkinnedMeshRenderer)arguments[3];
                var settings = (MeshProtectRoot)arguments[4];
                var variant = settings.variant;
                uint key = MeshProtectCipher.PackDigits(settings.keyDigits, variant);
                settings.obfuscateAnimatorNames = false;
                settings.obfuscateObjectNames = false;
                settings.copySeparateBlendTrees = false;
                settings.unlockMenuName = "Open Test Avatar";
                settings.unlockMenuPath = @"Integration/Accessories\/Props";
                settings.unlockMenuPosition = 1;

                var sourceDescriptor = source.GetComponent<VRCAvatarDescriptor>();
                // A scripted AddComponent has never opened the SDK inspector, which normally
                // initializes these arrays. VRCFury also inspects the source while cleaning its
                // temporary controllers and requires the ordinary non-humanoid default layout.
                sourceDescriptor.baseAnimationLayers = new[]
                {
                    VRCAvatarDescriptor.AnimLayerType.Base,
                    VRCAvatarDescriptor.AnimLayerType.Action,
                    VRCAvatarDescriptor.AnimLayerType.FX
                }.Select(type => new VRCAvatarDescriptor.CustomAnimLayer
                {
                    type = type, isDefault = true
                }).ToArray();
                sourceDescriptor.specialAnimationLayers = new[]
                {
                    VRCAvatarDescriptor.AnimLayerType.Sitting,
                    VRCAvatarDescriptor.AnimLayerType.TPose,
                    VRCAvatarDescriptor.AnimLayerType.IKPose
                }.Select(type => new VRCAvatarDescriptor.CustomAnimLayer
                {
                    type = type, isDefault = true
                }).ToArray();
                var sourceMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                sourceMenu.name = "IntegrationSourceMenu";
                sourceMenu.controls = new List<VRCExpressionsMenu.Control>();
                Save(sourceMenu, "IntegrationSourceMenu.asset");
                sourceDescriptor.customExpressions = true;
                sourceDescriptor.expressionsMenu = sourceMenu;

                var prop = new GameObject("ToggleTarget");
                prop.transform.SetParent(source.transform, false);
                prop.SetActive(false);
                var toggle = furyApi.GetMethod("CreateToggle", new[] { typeof(GameObject) })
                    ?.Invoke(null, new object[] { source });
                if (toggle == null) throw new MissingMethodException("VRCFury CreateToggle");
                // VRCFury splits every unescaped '/', including the one in a closing rich-text tag.
                Invoke(toggle, "SetMenuPath", @"<b>Integration<\/b>/Accessories\/Props/Generated Toggle");
                Invoke(toggle, "SetGlobalParameter", "IntegrationToggle");
                var actions = Invoke(toggle, "GetActions");
                Invoke(actions, "AddTurnOn", prop);

                // A real MA merge, with an external tree and a material-swap clip, exercises asset
                // ownership even with both optional obfuscation/tree-copy switches disabled.
                var originalMesh = sourceRenderer.sharedMesh;
                var originalMaterial = sourceRenderer.sharedMaterials[0];
                Save(originalMesh, "IntegrationSourceMesh.asset");
                Save(originalMaterial, "IntegrationSourceMaterial.mat");
                var alternateMaterial = new Material(originalMaterial) { name = "IntegrationAlternateMaterial" };
                alternateMaterial.SetColor("_Color", Color.blue);
                Save(alternateMaterial, "IntegrationAlternateMaterial.mat");
                var clip = new AnimationClip { name = "IntegrationSourceSwap" };
                var materialBinding = new EditorCurveBinding
                {
                    path = AnimationUtility.CalculateTransformPath(sourceRenderer.transform, source.transform),
                    type = typeof(SkinnedMeshRenderer),
                    propertyName = "m_Materials.Array.data[0]"
                };
                AnimationUtility.SetObjectReferenceCurve(clip, materialBinding,
                    new[] { new ObjectReferenceKeyframe { time = 0f, value = alternateMaterial } });
                Save(clip, "IntegrationSourceSwap.anim");
                var offClip = new AnimationClip { name = "IntegrationSourceOff" };
                AnimationUtility.SetObjectReferenceCurve(offClip, materialBinding,
                    new[] { new ObjectReferenceKeyframe { time = 0f, value = originalMaterial } });
                Save(offClip, "IntegrationSourceOff.anim");
                var tree = new BlendTree
                {
                    name = "IntegrationExternalTree",
                    blendType = BlendTreeType.Simple1D,
                    blendParameter = "IntegrationBlend",
                    children = new[] { new ChildMotion { motion = clip, threshold = 0f, timeScale = 1f } }
                };
                Save(tree, "IntegrationExternalTree.asset");
                var controller = AnimatorController.CreateAnimatorControllerAtPath(
                    AssetDatabase.GenerateUniqueAssetPath("Assets/_MPTestScratch/IntegrationSource.controller"));
                controller.AddParameter("IntegrationBlend", AnimatorControllerParameterType.Float);
                controller.AddParameter("IntegrationToggle", AnimatorControllerParameterType.Bool);
                var state = controller.layers[0].stateMachine.AddState("Material swap");
                state.motion = tree;
                state.writeDefaultValues = true;
                var offState = controller.layers[0].stateMachine.AddState("Original material");
                offState.motion = offClip;
                offState.writeDefaultValues = true;
                controller.layers[0].stateMachine.defaultState = offState;
                var turnOn = offState.AddTransition(state);
                turnOn.hasExitTime = false;
                turnOn.duration = 0f;
                turnOn.AddCondition(AnimatorConditionMode.If, 0f, "IntegrationToggle");
                var turnOff = state.AddTransition(offState);
                turnOff.hasExitTime = false;
                turnOff.duration = 0f;
                turnOff.AddCondition(AnimatorConditionMode.IfNot, 0f, "IntegrationToggle");
                var merge = source.AddComponent(mergeType);
                mergeType.GetField("animator").SetValue(merge, controller);
                var pathMode = mergeType.GetField("pathMode");
                pathMode.SetValue(merge, Enum.Parse(pathMode.FieldType, "Absolute"));
                AssetDatabase.SaveAssets();

                var originals = new Object[]
                {
                    originalMesh, originalMaterial, alternateMaterial, sourceMenu, clip, offClip,
                    tree, controller, state, offState, settings, merge, sourceDescriptor
                }.Concat(source.GetComponents<Component>().Where(c => c != null &&
                    c.GetType().FullName == "VF.Model.VRCFury")).Distinct().ToArray();
                var snapshots = originals.Select(asset => EditorJsonUtility.ToJson(asset)).ToArray();
                var originalVertices = originalMesh.vertices;

                built = Object.Instantiate(source);
                built.name = source.name + "(Clone)";
                bool ok = VRCBuildPipelineCallbacks.OnPreprocessAvatar(built);
                Check(ok, "sdk-callback-chain", "SDK/NDMF/VRCFury/MeshProtect preprocess returned " + ok);
                if (ok)
                {
                    var descriptor = built.GetComponent<VRCAvatarDescriptor>();
                    var parent = SubMenu(SubMenu(descriptor.expressionsMenu, "<b>Integration</b>"),
                                         "Accessories/Props");
                    var unlock = parent?.controls?.FirstOrDefault();
                    bool menuInstalled = unlock != null && unlock.name == "Open Test Avatar" &&
                        unlock.type == VRCExpressionsMenu.Control.ControlType.SubMenu &&
                        unlock.subMenu != null && unlock.subMenu.controls.Count == MeshProtectRoot.PasswordLength;
                    Check(menuInstalled, "generated-menu-install",
                        "custom Unlock occupies slot 1 under VRCFury's rich-text/slash parent");
                    Check(parent?.controls?.Any(c => c != null && c.name == "Generated Toggle" &&
                          c.type == VRCExpressionsMenu.Control.ControlType.Toggle) == true,
                        "vrcfury-toggle-preserved", "the real generated toggle remains beside Unlock");

                    var renderer = built.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single();
                    var mesh = renderer.sharedMesh;
                    bool moved = mesh != null && mesh != originalMesh &&
                        mesh.vertexCount == originalVertices.Length && mesh.vertices
                            .Where((vertex, i) => (vertex - originalVertices[i]).sqrMagnitude > 1e-12f).Any();
                    Check(moved, "mesh-displaced", "the full callback chain produced a different displaced mesh");
                    if (moved)
                    {
                        var mode = renderer.sharedMaterials[0].GetFloat(variant.modeProperty) > 0.5f
                            ? MeshProtectRoot.DisplacementMode.TangentSpace : MeshProtectRoot.DisplacementMode.Normal;
                        var restored = MeshProtectSelfCheck.Verify(originalMesh, mesh, mode, key, variant);
                        Check(restored.Passed, "mesh-decodes", "restore error " + restored.worstVertexError.ToString("E3"));
                    }
                    Check(renderer.sharedMaterials.All(m => MeshProtectPipeline.IsProtectShader(m, variant.shaderName)),
                        "decode-material", "the built renderer carries this variant's decode shader");
                    var swapMaterials = descriptor.baseAnimationLayers
                        .Where(layer => !layer.isDefault && layer.animatorController != null)
                        .SelectMany(layer => layer.animatorController.animationClips).Distinct()
                        .SelectMany(c => AnimationUtility.GetObjectReferenceCurveBindings(c)
                            .Where(binding => binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal))
                            .SelectMany(binding => AnimationUtility.GetObjectReferenceCurve(c, binding)))
                        .Select(frame => frame.value as Material).Where(m => m != null).ToArray();
                    Check(swapMaterials.Length > 0 && swapMaterials.All(m =>
                            MeshProtectPipeline.IsProtectShader(m, variant.shaderName)),
                        "ma-material-swap-rewritten", "MA's merged material animation uses decode copies");
                    Check(built.GetComponentsInChildren<MeshProtectRoot>(true).Length == 0 &&
                          built.GetComponentsInChildren(mergeType, true).Length == 0,
                        "authoring-components-removed", "MeshProtect and MA authoring components were consumed");
                    var fatal = MeshProtectValidator.Validate(built, variant).Where(issue => issue.fatal).ToArray();
                    Check(fatal.Length == 0, "final-unlock-chain", string.Join("; ", fatal.Select(issue => issue.message)));
                }

                bool untouched = originals.Select((asset, i) =>
                    asset != null && EditorJsonUtility.ToJson(asset) == snapshots[i]).All(same => same) &&
                    sourceRenderer.sharedMesh == originalMesh &&
                    sourceRenderer.sharedMaterials.SequenceEqual(new[] { originalMaterial }) &&
                    sourceDescriptor.expressionsMenu == sourceMenu && !prop.activeSelf;
                Check(untouched, "source-assets-unchanged",
                    "source mesh/material/menu/clip/tree/controller/settings and toggle target remain intact");
            }
            catch (Exception e)
            {
                Check(false, "exception", (e is TargetInvocationException && e.InnerException != null
                    ? e.InnerException : e).ToString());
            }
            finally
            {
                if (built != null) Object.DestroyImmediate(built);
                if (source != null) Object.DestroyImmediate(source);
            }
            Debug.Log(failed == 0 ? "[INTEGRATION] === PASS ===" : $"[INTEGRATION] === FAIL ({failed}) ===");
            return failed == 0 ? 0 : 1;
        }

        private static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null);

        private static object Invoke(object target, string method, params object[] args)
        {
            var found = target?.GetType().GetMethod(method,
                args.Select(arg => arg.GetType()).ToArray());
            if (found == null) throw new MissingMethodException(target?.GetType().FullName, method);
            return found.Invoke(target, args);
        }

        private static void Save(Object asset, string file) => AssetDatabase.CreateAsset(asset,
            AssetDatabase.GenerateUniqueAssetPath("Assets/_MPTestScratch/" + file));

        private static VRCExpressionsMenu SubMenu(VRCExpressionsMenu menu, string name) =>
            menu?.controls?.SingleOrDefault(control => control != null && control.name == name &&
                control.type == VRCExpressionsMenu.Control.ControlType.SubMenu)?.subMenu;
    }
}
#endif
