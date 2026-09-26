using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MeshProtect;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace MPTest
{
    /// <summary>Real controller assets: source preservation, shared references and prepared snapshots.</summary>
    public static class OwnershipTest
    {
        private static int failures;

        public static void Run() => EditorApplication.Exit(RunCore());

        public static void RunPreparedSnapshots()
        {
            failures = 0;
            Run("prepared snapshots and partial preparation", PreparedSnapshots);
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        public static int RunCore()
        {
            failures = 0;
            Run("external assets without tree copies", () => ExternalAssets(false));
            Run("external assets with tree copies", () => ExternalAssets(true));
            Run("playable overrides", PlayableOverrides);
            Run("synchronized-layer overrides", SyncedOverrides);
            Run("prepared snapshots and partial preparation", PreparedSnapshots);
            Debug.Log($"[OWNERSHIP] {failures} failure(s)");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                failures++;
                Debug.LogError($"[OWNERSHIP] FAIL {name}: {e}");
            }
        }

        private static void Check(bool success, string label)
        {
            if (!success) failures++;
            Debug.Log($"[OWNERSHIP] {(success ? "PASS" : "FAIL")} {label}");
        }

        private static void ExternalAssets(bool copyTrees)
        {
            using (var f = new Fixture())
            {
                f.Settings.copySeparateBlendTrees = copyTrees;
                f.Child("TreeProp");
                f.Child("AudioProp");
                var source = f.Controller("Base", "TreeCurve", "TreeBlend", "DriverKeep", "OwnedDriver");
                var clip = f.Clip("Shared", "TreeProp", "TreeCurve");
                var tree = new BlendTree { name = "ExternalTree", blendParameter = "TreeBlend" };
                tree.children = new[] { new ChildMotion { motion = clip, threshold = 0, timeScale = 1 } };
                f.Asset(tree, "Tree.asset");

                // Owned occurrence first: the later, foreign occurrence must still block renaming.
                var direct = source.layers[0].stateMachine.AddState("Direct");
                direct.motion = clip;
                source.layers[0].stateMachine.AddState("ThroughTree").motion = tree;
                var ownedDriver = direct.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
                ownedDriver.parameters.Add(Parameter("OwnedDriver"));
                var foreignDriver = f.Driver("DriverKeep", "Driver.asset");

                var audioType = TypeCache.GetTypesDerivedFrom<StateMachineBehaviour>()
                    .First(t => !t.IsAbstract && t.Name.Contains("PlayAudio") &&
                                t.GetField("SourcePath")?.FieldType == typeof(string));
                var audio = (StateMachineBehaviour)ScriptableObject.CreateInstance(audioType);
                var sourcePath = audioType.GetField("SourcePath");
                sourcePath.SetValue(audio, "AudioProp");
                f.Asset(audio, "Audio.asset");
                direct.behaviours = new StateMachineBehaviour[] { ownedDriver, foreignDriver, audio };
                f.Layers(Layer(VRCAvatarDescriptor.AnimLayerType.Base, source));
                var before = f.CaptureSources();

                f.Prepare();
                var copy = f.Copy(VRCAvatarDescriptor.AnimLayerType.Base);
                Check(!Has(f.Settings.renamedParameters, "DriverKeep"), "external driver name retained");
                Check(!Has(f.Settings.renamedObjects, "AudioProp"), "external PlayAudio path retained");
                Check(Has(f.Settings.renamedParameters, "TreeCurve") == copyTrees,
                      "shared clip parameter follows tree-copy capability");
                Check(Has(f.Settings.renamedObjects, "TreeProp") == copyTrees,
                      "shared clip path follows tree-copy capability");
                string ownedName = Mapped(f.Settings.renamedParameters, "OwnedDriver");
                Check(ownedName != "OwnedDriver" && copy.layers[0].stateMachine.states
                          .SelectMany(s => s.state.behaviours).OfType<VRCAvatarParameterDriver>()
                          .Any(d => d != foreignDriver && d.parameters.Any(p => p.name == ownedName)),
                      "owned driver is still rewritten");

                var copiedTree = copy.layers[0].stateMachine.states.Select(s => s.state.motion)
                    .OfType<BlendTree>().Single();
                Check(copyTrees ? copiedTree != tree : copiedTree == tree,
                      "external tree copied only when enabled");
                var directCopy = copy.layers[0].stateMachine.states.Select(s => s.state.motion)
                    .OfType<AnimationClip>().Single();
                Check(directCopy != clip, "direct clip is an independent copy");
                Check(copyTrees ? copiedTree.children[0].motion == directCopy
                                : copiedTree.children[0].motion == clip,
                      "shared clip references remain closed in their owning graph");
                Check(AnimationUtility.GetCurveBindings(directCopy).Any(b => b.type == typeof(Animator) &&
                          b.propertyName == Mapped(f.Settings.renamedParameters, "TreeCurve")),
                      "copied parameter curve matches the controller parameter map");
                f.SampleOnRenamedAvatar(directCopy, "TreeProp");

                // Exercise write guards independently of the survey: even a bad caller's map must
                // not modify behaviour assets still referenced by the protected controller.
                Invoke("MeshProtectParameters", "RewriteController", copy,
                       new Dictionary<string, string> { ["DriverKeep"] = "ForcedDriver" });
                Invoke("MeshProtectObjectNames", "RewriteController", copy,
                       new Dictionary<string, string> { ["AudioProp"] = "ForcedAudio" },
                       Path.GetDirectoryName(AssetDatabase.GetAssetPath(copy)).Replace('\\', '/'));
                Check(foreignDriver.parameters[0].name == "DriverKeep" &&
                      (string)sourcePath.GetValue(audio) == "AudioProp",
                      "direct rewrite cannot mutate external behaviours");
                f.CheckSources(before);
            }
        }

        private static void PlayableOverrides()
        {
            using (var f = new Fixture())
            {
                f.Child("BaseProp");
                f.Child("OverrideProp");
                var source = f.Controller("Base");
                var baseClip = f.Clip("Original", "BaseProp");
                source.layers[0].stateMachine.AddState("State").motion = baseClip;
                var replacement = f.Clip("Replacement", "OverrideProp");
                var overriding = new AnimatorOverrideController(source);
                overriding[baseClip] = replacement;
                f.Asset(overriding, "Gesture.overrideController");
                f.Layers(Layer(VRCAvatarDescriptor.AnimLayerType.Base, source),
                         Layer(VRCAvatarDescriptor.AnimLayerType.Gesture, overriding));
                var before = f.CaptureSources();
                f.Prepare();
                Check(!Has(f.Settings.renamedObjects, "OverrideProp"),
                      "playable override replacement path is preserved during Prepare");

                // At build time Base will be adopted, but Gesture remains an override of Base.
                var engine = typeof(MeshProtectControllers).Assembly.GetType("MeshProtect.MeshProtectObjectNames");
                var options = Activator.CreateInstance(engine.GetNestedType("Options", BindingFlags.NonPublic), true);
                ((HashSet<AnimatorController>)options.GetType().GetField("replaced").GetValue(options)).Add(source);
                var findings = (IEnumerable)Invoke("MeshProtectObjectNames", "Survey", f.Descriptor, options);
                object finding = findings.Cast<object>().Single(x =>
                    (string)x.GetType().GetField("name").GetValue(x) == "OverrideProp");
                Check(finding.GetType().GetField("blockedBy").GetValue(finding) != null,
                      "adopting a base controller does not hide its foreign override clip");
                f.SampleOnRenamedAvatar(replacement, "OverrideProp");
                f.CheckSources(before);
            }
        }

        private static void SyncedOverrides()
        {
            using (var f = new Fixture())
            {
                f.Child("BaseProp");
                f.Child("SyncedProp");
                var source = f.Controller("Base", "SyncCurve", "SyncDriver");
                var baseClip = f.Clip("Original", "BaseProp");
                var state = source.layers[0].stateMachine.AddState("State");
                state.motion = baseClip;
                var replacement = f.Clip("Synchronized", "SyncedProp", "SyncCurve");
                var driver = f.Driver("SyncDriver", "SyncDriver.asset");
                source.AddLayer("Synced");
                var layers = source.layers;
                layers[1].syncedLayerIndex = 0;
                layers[1].SetOverrideMotion(state, replacement);
                layers[1].SetOverrideBehaviours(state, new StateMachineBehaviour[] { driver });
                source.layers = layers;
                f.Layers(Layer(VRCAvatarDescriptor.AnimLayerType.Base, source));
                var before = f.CaptureSources();
                f.Prepare();
                Check(!Has(f.Settings.renamedObjects, "SyncedProp"), "synced override path retained");
                Check(!Has(f.Settings.renamedParameters, "SyncCurve") &&
                      !Has(f.Settings.renamedParameters, "SyncDriver"),
                      "synced override curve and behaviour parameters retained");
                var copy = f.Copy(VRCAvatarDescriptor.AnimLayerType.Base);
                var copiedState = copy.layers[0].stateMachine.states.Single().state;
                Check(copy.layers[1].GetOverrideMotion(copiedState) == replacement &&
                      copy.layers[1].GetOverrideBehaviours(copiedState).Contains(driver),
                      "synced layer keeps its existing motion and behaviour references");
                f.SampleOnRenamedAvatar(replacement, "SyncedProp");
                f.CheckSources(before);
            }
        }

        private static void PreparedSnapshots()
        {
            using (var f = new Fixture())
            {
                f.Child("OwnedProp");
                f.Child("MemoryProp");
                var source = f.Controller("Base", "BaseOnly");
                var clip = f.Clip("Owned", "OwnedProp");
                source.layers[0].stateMachine.AddState("State").motion = clip;
                // Adding another main object imports the controller container. Persist its
                // existing state/motion before that import can reload the serialized graph.
                AssetDatabase.SaveAssets();
                var sub = new AnimatorController { name = "GestureSubasset" };
                AssetDatabase.AddObjectToAsset(sub, source);
                sub.AddLayer("SubLayer");
                sub.AddParameter("GestureOnly", AnimatorControllerParameterType.Float);
                var memory = f.Remember(new AnimatorController { name = "Unsaved" });
                memory.AddLayer("Memory");
                memory.AddParameter("MemoryOnly", AnimatorControllerParameterType.Float);
                var memoryClip = f.Remember(new AnimationClip { name = "MemoryClip" });
                SetCurve(memoryClip, "MemoryProp", typeof(Transform), "m_LocalPosition.x");
                memory.layers[0].stateMachine.AddState("State").motion = memoryClip;
                f.Layers(Layer(VRCAvatarDescriptor.AnimLayerType.Base, source),
                         Layer(VRCAvatarDescriptor.AnimLayerType.Gesture, sub),
                         Layer(VRCAvatarDescriptor.AnimLayerType.Action, memory));
                f.Prepare();
                Check(f.Settings.preparedControllers.Count == 2 &&
                      !Has(f.Settings.renamedParameters, "MemoryOnly") &&
                      !Has(f.Settings.renamedObjects, "MemoryProp"),
                      "a skipped memory layer is excluded from preparation's writable scope");
                Check(f.Copy(VRCAvatarDescriptor.AnimLayerType.Base).parameters
                          .Any(p => p.name == Mapped(f.Settings.renamedParameters, "BaseOnly")) &&
                      f.Copy(VRCAvatarDescriptor.AnimLayerType.Gesture).parameters
                          .Any(p => p.name == Mapped(f.Settings.renamedParameters, "GestureOnly")),
                      "controllers sharing one GUID retain their distinct identities");
                Check(MeshProtectControllers.Inspect(f.Settings, f.Descriptor, out _) ==
                      MeshProtectControllers.State.UpToDate, "new snapshot is current");

                var originalPaths = f.Settings.preparedControllers.Select(e => e.copyPath).ToArray();
                var originalBytes = originalPaths.ToDictionary(p => p, File.ReadAllBytes);
                var duplicate = f.Remember(Object.Instantiate(f.Avatar));
                var duplicateSettings = duplicate.GetComponent<MeshProtectRoot>();
                MeshProtectControllers.Prepare(duplicateSettings, duplicate.GetComponent<VRCAvatarDescriptor>());
                Check(!duplicateSettings.preparedControllers.Any(e => originalPaths.Contains(e.copyPath)),
                      "duplicating an avatar produces an independent snapshot on re-prepare");
                MeshProtectControllers.Clear(duplicateSettings);
                Check(originalBytes.All(pair => File.Exists(pair.Key) &&
                          pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))),
                      "re-prepare and Clear preserve snapshots referenced by the original avatar");

                Undo.IncrementCurrentGroup();
                Undo.RecordObject(f.Settings, "Forget ownership fixture snapshot");
                MeshProtectControllers.Clear(f.Settings);
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Check(f.Settings.preparedControllers.Select(e => e.copyPath).SequenceEqual(originalPaths) &&
                      MeshProtectControllers.Inspect(f.Settings, f.Descriptor, out _) ==
                      MeshProtectControllers.State.UpToDate,
                      "Undo restores a complete, still-valid snapshot record");
                Undo.ClearUndo(f.Settings);

                var entry = f.Settings.preparedControllers[0];
                string current = entry.copyHash;
                entry.copyHash = "ownership-v1:" + current.Substring(current.IndexOf(':') + 1);
                Check(MeshProtectControllers.Inspect(f.Settings, f.Descriptor, out _) ==
                      MeshProtectControllers.State.Stale, "old ownership rules require re-preparation");
                entry.copyHash = current;

                string sourcePath = AssetDatabase.GetAssetPath(source);
                string clipPath = AssetDatabase.GetAssetPath(clip);
                bool referencesClip = source.animationClips.Contains(clip);
                bool dependencyIncludesClip = AssetDatabase.GetDependencies(sourcePath, true).Contains(clipPath);
                byte[] clipBytesBefore = File.ReadAllBytes(clipPath);
                Check(referencesClip && dependencyIncludesClip,
                      $"dependency fixture retains its clip: motion={referencesClip}, dependency={dependencyIncludesClip}");
                SetCurve(clip, "OwnedProp", typeof(Transform), "m_LocalPosition.x", 8f);
                EditorUtility.SetDirty(clip);
                AssetDatabase.SaveAssets();
                Check(!clipBytesBefore.SequenceEqual(File.ReadAllBytes(clipPath)),
                      "external clip edit changes the saved source asset");
                var state = MeshProtectControllers.Inspect(f.Settings, f.Descriptor, out string detail);
                Check(state == MeshProtectControllers.State.Stale,
                      $"editing an external clip invalidates its prepared copy: {state}; {detail}");
            }
        }

        private static VRC_AvatarParameterDriver.Parameter Parameter(string name) =>
            new VRC_AvatarParameterDriver.Parameter { name = name, type = VRC_AvatarParameterDriver.ChangeType.Set };

        private static VRCAvatarDescriptor.CustomAnimLayer Layer(
            VRCAvatarDescriptor.AnimLayerType type, RuntimeAnimatorController controller) =>
            new VRCAvatarDescriptor.CustomAnimLayer { type = type, isDefault = false, animatorController = controller };

        private static bool Has(List<MeshProtectRoot.RenamedParameter> map, string name) =>
            map.Any(p => p.original == name);

        private static string Mapped(List<MeshProtectRoot.RenamedParameter> map, string name) =>
            map.FirstOrDefault(p => p.original == name)?.obfuscated ?? name;

        private static object Invoke(string type, string method, params object[] arguments) =>
            typeof(MeshProtectControllers).Assembly.GetType("MeshProtect." + type)
                .GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Invoke(null, arguments);

        private static void SetCurve(AnimationClip clip, string path, Type type, string property, float value = 4f) =>
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property),
                                            AnimationCurve.Constant(0, 1, value));

        private sealed class Fixture : IDisposable
        {
            public readonly string Folder;
            public readonly GameObject Avatar;
            public readonly MeshProtectRoot Settings;
            public readonly VRCAvatarDescriptor Descriptor;
            private readonly List<Object> transient = new List<Object>();
            private readonly List<string> sources = new List<string>();

            public Fixture()
            {
                Folder = "Assets/MP_Ownership_" + Guid.NewGuid().ToString("N");
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(Folder));
                Avatar = Remember(new GameObject("OwnershipFixture"));
                Avatar.AddComponent<Animator>();
                Descriptor = Avatar.AddComponent<VRCAvatarDescriptor>();
                Settings = Avatar.AddComponent<MeshProtectRoot>();
                Settings.outputFolder = Folder + "/Generated";
                Settings.keyDigits = new[] { 1, 2, 3, 4, 5, 6 };
                Settings.variant = MeshProtectVariantGenerator.Generate(new System.Random(1729));
                Settings.obfuscateAnimatorNames = true;
                Settings.obfuscateObjectNames = true;
                Settings.obfuscateParameterNames = true;
                Settings.copySeparateBlendTrees = true;
            }

            public T Remember<T>(T value) where T : Object { transient.Add(value); return value; }
            public void Child(string name) => new GameObject(name).transform.SetParent(Avatar.transform, false);
            public void Asset(Object value, string name)
            {
                string path = Folder + "/" + name;
                AssetDatabase.CreateAsset(value, path);
                sources.Add(path);
            }

            public AnimatorController Controller(string name, params string[] parameters)
            {
                string path = Folder + "/" + name + ".controller";
                var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
                sources.Add(path);
                foreach (string parameter in parameters)
                    controller.AddParameter(parameter, AnimatorControllerParameterType.Float);
                return controller;
            }

            public AnimationClip Clip(string name, string objectPath, string parameter = null)
            {
                var clip = new AnimationClip { name = name };
                SetCurve(clip, objectPath, typeof(Transform), "m_LocalPosition.x");
                if (parameter != null) SetCurve(clip, "", typeof(Animator), parameter);
                Asset(clip, name + ".anim");
                return clip;
            }

            public VRCAvatarParameterDriver Driver(string parameter, string name)
            {
                var driver = ScriptableObject.CreateInstance<VRCAvatarParameterDriver>();
                driver.parameters.Add(Parameter(parameter));
                Asset(driver, name);
                return driver;
            }

            public void Layers(params VRCAvatarDescriptor.CustomAnimLayer[] layers)
            {
                Descriptor.customizeAnimationLayers = true;
                Descriptor.baseAnimationLayers = layers;
                Descriptor.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];
            }

            public void Prepare()
            {
                AssetDatabase.SaveAssets();
                MeshProtectControllers.Prepare(Settings, Descriptor, new List<string>());
            }

            public AnimatorController Copy(VRCAvatarDescriptor.AnimLayerType layer)
            {
                var record = Settings.preparedControllers.Single(e => e.layerType == layer.ToString());
                return AssetDatabase.LoadAllAssetsAtPath(record.copyPath).OfType<AnimatorController>()
                    .Single(c => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(c, out _, out long id) &&
                                 id == record.copyLocalId);
            }

            public Dictionary<string, byte[]> CaptureSources()
            {
                AssetDatabase.SaveAssets();
                return sources.ToDictionary(p => p, File.ReadAllBytes);
            }

            public void CheckSources(Dictionary<string, byte[]> before)
            {
                AssetDatabase.SaveAssets();
                Check(before.All(pair => pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))),
                      "source controller, clips, tree and behaviours remain byte-identical after SaveAssets");
            }

            public void SampleOnRenamedAvatar(AnimationClip clip, string targetName)
            {
                var clone = Remember(Object.Instantiate(Avatar));
                var target = clone.transform.Find(targetName);
                var map = Settings.renamedObjects.ToDictionary(p => p.original, p => p.obfuscated);
                Invoke("MeshProtectObjectNames", "RewriteObjects", clone, map);
                clip.SampleAnimation(clone, 0.5f);
                Check(Mathf.Abs(target.localPosition.x - 4f) < 0.001f,
                      "animation still reaches '" + targetName + "' after object renaming");
            }

            public void Dispose()
            {
                for (int i = transient.Count - 1; i >= 0; i--)
                    if (transient[i] != null) Object.DestroyImmediate(transient[i]);
                AssetDatabase.DeleteAsset(Folder);
            }
        }
    }
}
