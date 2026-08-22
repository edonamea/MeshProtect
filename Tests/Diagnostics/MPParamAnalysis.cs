#if UNITY_EDITOR
// READ-ONLY diagnostic. Writes one text file and nothing else. Delete Assets/_MPDiag when done.
//
// It answers one question: for each parameter this avatar uses, is EVERY place that names it
// somewhere MeshProtect would rewrite? Only then may the parameter be renamed - a parameter
// is avatar-global, so a single missed reference is a broken avatar, not one less obfuscated name.
//
// Three rewrite scopes are evaluated in one pass, because the interesting number is the difference
// between them:
//
//   Today  - only the FX controller is ours (it is copied during the build).
//   Mode2a - plus the Base/Gesture/Action/special copies prepared before the build. Expression
//            parameters and submenus are NOT rewritten, so anything they name is out.
//   Mode2b - plus the expression parameter list and a deep copy of the whole menu tree.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace MPDiag
{
    public static class MPParamAnalysis
    {
        // Printed first thing, so a run that used stale compiled code is obvious rather than
        // silently wasted (pitfall 7 in the handoff).
        private const string Stamp = "MPParamAnalysis build 3";

        // ------------------------------------------------------------------ scopes

        /// <summary>Where a reference to a parameter name lives, in terms of who could rewrite it.</summary>
        private enum Scope
        {
            /// <summary>Inside the FX controller's own file. Copied during the build, always ours.</summary>
            OwnedFx,

            /// <summary>Inside a Base/Gesture/Action/special controller's own file. Ours only once
            /// the copies are prepared.</summary>
            OwnedPrepared,

            /// <summary>An object a controller points at that lives in a DIFFERENT file - the blend
            /// tree saved as its own .asset. A file copy of the controller does not copy it, so the
            /// copy still points at the artist's asset and we may not write to it. This is the wall
            /// Kanna hit: rename in the controller, miss the blend tree, VRChat rejects the upload
            /// with "uses parameter X which is not float type".</summary>
            SeparateAsset,

            /// <summary>A playable layer controller that is not copied at all - a default layer, or
            /// something that is not an AnimatorController.</summary>
            ForeignController,

            /// <summary>A clip that is never cloned, so its curves cannot be rewritten: VRChat's own
            /// proxy animations and anything shipped inside a com.vrchat package.</summary>
            ClipNotCloned,

            ExpressionList,
            RootMenu,
            SubMenu,

            /// <summary>PhysBone prefix / contact receiver on the avatar. Rewritten on the build
            /// clone, which never touches the scene.</summary>
            Component,

            /// <summary>An Animator somewhere under the avatar that is not a playable layer. Its
            /// parameters are a separate space, so this is recorded for information only.</summary>
            ChildAnimator,
        }

        private static bool Rewritable(Scope scope, int mode)
        {
            switch (scope)
            {
                case Scope.OwnedFx: return true;
                case Scope.OwnedPrepared: return mode >= 1;
                case Scope.Component: return mode >= 1;
                case Scope.RootMenu: return mode >= 1;   // the build already clones the root menu
                case Scope.ExpressionList: return mode >= 2;
                case Scope.SubMenu: return mode >= 2;

                // Mode 3 is the same as 2b plus cloning a blend tree that lives in its own file
                // into the controller copy - the technique the obfuscator already uses on clips.
                case Scope.SeparateAsset: return mode >= 3;

                default: return false;
            }
        }

        // ------------------------------------------------------------------ the VRChat name lists

        /// <summary>
        /// Driven by the client by name. Copied from Modular Avatar's ParameterPolicy.VRCSDKParameters
        /// (Packages/nadena.dev.modular-avatar/Editor/ParameterPolicy.cs) on 2026-08-12. There is no
        /// SDK API for this, so both tools maintain a hand list and both can go stale.
        /// </summary>
        private static readonly HashSet<string> VrcBuiltIn = new HashSet<string>
        {
            "IsLocal", "PreviewMode", "Viseme", "Voice",
            "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight",
            "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude",
            "Upright", "Grounded", "Seated", "AFK", "TrackingType", "VRMode", "MuteSelf",
            "InStation", "Earmuffs", "IsOnFriendsList", "AvatarVersion",
            "ScaleModified", "ScaleFactor", "ScaleFactorInverse",
            "EyeHeightAsMeters", "EyeHeightAsPercent", "IsAnimatorEnabled",
        };

        /// <summary>Action menu convention names, which the client also drives.</summary>
        private static readonly HashSet<string> VrcMenuConvention = new HashSet<string>(
            new[] { "VRCEmote", "VRCFaceBlendH", "VRCFaceBlendV", "VRCFaceBlendL", "VRCFaceBlendR" }
                .Concat(Enumerable.Range(1, 16).Select(i => "Expression" + i)));

        private static readonly string[] PhysBoneSuffixes =
        {
            "_IsGrabbed", "_Angle", "_Stretch", "_IsPosed", "_Squish", "_Hit", "_Ratio", "_Distance"
        };

        // ------------------------------------------------------------------ model

        private sealed class Site
        {
            public Scope scope;
            public string where;
        }

        private sealed class Param
        {
            public string name;
            public bool isPhysBonePrefix;
            public readonly List<Site> sites = new List<Site>();
            public bool inExpressionList;
            public string expressionType = "";
        }

        private sealed class Avatar
        {
            public VRCAvatarDescriptor descriptor;
            public readonly Dictionary<string, Param> parameters = new Dictionary<string, Param>();
            public readonly List<string> notes = new List<string>();

            /// <summary>Files holding blend trees a controller points at from outside itself.</summary>
            public readonly HashSet<string> separateAssetPaths = new HashSet<string>();

            public Param Get(string name, bool prefix = false)
            {
                if (!parameters.TryGetValue(name, out var p))
                {
                    p = new Param { name = name, isPhysBonePrefix = prefix };
                    parameters[name] = p;
                }
                if (prefix) p.isPhysBonePrefix = true;
                return p;
            }

            public void Add(string name, Scope scope, string where, bool prefix = false)
            {
                if (string.IsNullOrEmpty(name)) return;
                Get(name, prefix).sites.Add(new Site { scope = scope, where = where });
            }
        }

        // ------------------------------------------------------------------ entry points

        [MenuItem("Tools/MeshProtect Diag/Analyze Parameters")]
        public static void RunMenu() { Run(); }

        /// <summary>
        /// Batch entry point. A batchmode Unity starts on an empty scene, so the avatar has to be
        /// opened first - read only, and never saved.
        /// </summary>
        public static void RunBatch()
        {
            var scenes = AssetDatabase.FindAssets("t:Scene")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            foreach (var scene in scenes)
            {
                Debug.Log("[" + Stamp + "] opening " + scene);
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    scene, UnityEditor.SceneManagement.OpenSceneMode.Single);
                if (FindDescriptors().Count > 0) break;
            }

            Run();
        }

        public static void Run()
        {
            var text = new StringBuilder();
            text.AppendLine("=== " + Stamp + " ===");
            text.AppendLine("run at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine();

            var descriptors = FindDescriptors();
            if (descriptors.Count == 0) text.AppendLine("No VRCAvatarDescriptor in any loaded scene.");

            foreach (var descriptor in descriptors)
            {
                try { Report(Collect(descriptor), text); }
                catch (Exception e)
                {
                    text.AppendLine("FAILED on " + descriptor.name + ": " + e);
                }
                text.AppendLine();
            }

            string path = Path.Combine(Directory.GetCurrentDirectory(), "mp-param-report.txt");
            File.WriteAllText(path, text.ToString());
            Debug.Log("[" + Stamp + "] wrote " + path);
        }

        /// <summary>
        /// How many times each name is mentioned anywhere in the avatar. For the harness: after a
        /// build that renames parameters, an old name mentioned even once is a missed reference.
        /// </summary>
        public static Dictionary<string, int> CountReferences(VRCAvatarDescriptor descriptor)
        {
            var avatar = Collect(descriptor);
            return avatar.parameters.ToDictionary(pair => pair.Key, pair => pair.Value.sites.Count);
        }

        /// <summary>
        /// Names declared by the FX playable layer, and names declared by all the others.
        ///
        /// A parameter both groups declare has to keep ONE name across them, whatever that name is.
        /// Renaming it in FX and not in Base is the failure the whole proof exists to prevent, and
        /// it shows up here as the intersection of the two sets losing a member.
        /// </summary>
        public static void DeclaredByPlayableLayer(VRCAvatarDescriptor descriptor,
                                                   out HashSet<string> fx, out HashSet<string> others)
        {
            fx = new HashSet<string>(StringComparer.Ordinal);
            others = new HashSet<string>(StringComparer.Ordinal);

            foreach (var layers in new[] { descriptor.baseAnimationLayers, descriptor.specialAnimationLayers })
            {
                if (layers == null) continue;
                foreach (var layer in layers)
                {
                    if (layer.isDefault || layer.animatorController == null) continue;

                    var controller = layer.animatorController as AnimatorController
                                     ?? (layer.animatorController as AnimatorOverrideController)
                                        ?.runtimeAnimatorController as AnimatorController;
                    if (controller == null) continue;

                    var into = layer.type == VRCAvatarDescriptor.AnimLayerType.FX ? fx : others;
                    foreach (var p in controller.parameters) into.Add(p.name);
                }
            }
        }

        /// <summary>
        /// Animation paths that do not lead to an object.
        ///
        /// An avatar can legitimately carry a few - a clip animating something the artist deleted -
        /// so the number on its own means nothing. What means something is the number going UP
        /// between the scene and the build: that is a path this upload broke. It is also the only
        /// check that reaches the clips this tool generates itself, which bind to renderer paths and
        /// carry the digits to the material - if object renaming leaves those behind, the avatar
        /// uploads and can never be unlocked, and nothing else notices.
        /// </summary>
        public static HashSet<string> UnresolvablePaths(VRCAvatarDescriptor descriptor)
        {
            var bad = new HashSet<string>(StringComparer.Ordinal);
            var root = descriptor.transform;
            var seen = new HashSet<Motion>();

            void Walk(Motion motion)
            {
                if (motion == null || !seen.Add(motion)) return;

                if (motion is BlendTree tree)
                {
                    foreach (var child in tree.children) Walk(child.motion);
                    return;
                }

                if (!(motion is AnimationClip clip)) return;

                foreach (var binding in AnimationUtility.GetCurveBindings(clip)
                             .Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                {
                    if (string.IsNullOrEmpty(binding.path)) continue;
                    if (root.Find(binding.path) != null) continue;
                    bad.Add(binding.path);
                }
            }

            void WalkMachine(AnimatorStateMachine machine, HashSet<AnimatorStateMachine> machines)
            {
                if (machine == null || !machines.Add(machine)) return;
                foreach (var child in machine.states) Walk(child.state?.motion);
                foreach (var child in machine.stateMachines) WalkMachine(child.stateMachine, machines);
            }

            foreach (var layers in new[] { descriptor.baseAnimationLayers, descriptor.specialAnimationLayers })
            {
                if (layers == null) continue;
                foreach (var layer in layers)
                {
                    if (layer.isDefault) continue;

                    var controller = layer.animatorController as AnimatorController
                                     ?? (layer.animatorController as AnimatorOverrideController)
                                        ?.runtimeAnimatorController as AnimatorController;
                    if (controller == null) continue;

                    var machines = new HashSet<AnimatorStateMachine>();
                    foreach (var l in controller.layers) WalkMachine(l.stateMachine, machines);
                }
            }

            return bad;
        }

        /// <summary>Where each name is mentioned, for a failure that needs explaining.</summary>
        public static List<string> WhereMentioned(VRCAvatarDescriptor descriptor, string name)
        {
            var avatar = Collect(descriptor);
            return avatar.parameters.TryGetValue(name, out var param)
                ? param.sites.Select(s => s.scope + ": " + s.where).Distinct().ToList()
                : new List<string>();
        }

        public static List<VRCAvatarDescriptor> FindDescriptors()
        {
            var found = new List<VRCAvatarDescriptor>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    found.AddRange(root.GetComponentsInChildren<VRCAvatarDescriptor>(true));
            }
            return found;
        }

        // ------------------------------------------------------------------ collection

        private static Avatar Collect(VRCAvatarDescriptor descriptor)
        {
            var avatar = new Avatar { descriptor = descriptor };

            var playable = new List<AnimatorController>();

            // Every parameter any playable layer declares, gathered before the walk.
            //
            // A clip can animate a parameter, and the binding for that is an Animator-typed curve on
            // the avatar root - but so is every muscle curve in a humanoid clip. "RootT.x" and
            // "LeftHand.Index.1 Stretch" arrive through the same API and are indistinguishable from
            // a parameter by shape alone. Unity only drives a parameter from a curve when the name
            // matches a declared one, so that is the test. Without it this counted 150 muscle curves
            // as parameters that could not be renamed.
            var declared = new HashSet<string>();
            foreach (var layers in new[] { descriptor.baseAnimationLayers, descriptor.specialAnimationLayers })
            {
                if (layers == null) continue;
                foreach (var layer in layers)
                {
                    if (layer.isDefault) continue;

                    var controller = layer.animatorController as AnimatorController
                                     ?? (layer.animatorController as AnimatorOverrideController)
                                        ?.runtimeAnimatorController as AnimatorController;
                    if (controller == null) continue;

                    foreach (var p in controller.parameters) declared.Add(p.name);
                }
            }

            Sweep(avatar, descriptor.baseAnimationLayers, playable, declared);
            Sweep(avatar, descriptor.specialAnimationLayers, playable, declared);

            CollectExpressionParameters(avatar, descriptor);
            CollectMenu(avatar, descriptor.expressionsMenu, true, new HashSet<VRCExpressionsMenu>(), "menu");
            CollectComponents(avatar, descriptor.gameObject, playable);

            return avatar;
        }

        private static void Sweep(Avatar avatar, VRCAvatarDescriptor.CustomAnimLayer[] layers,
                                  List<AnimatorController> playable, HashSet<string> declared)
        {
            if (layers == null) return;

            foreach (var layer in layers)
            {
                string label = layer.type.ToString();

                if (layer.isDefault || layer.animatorController == null)
                {
                    avatar.notes.Add(label + ": default / empty, nothing to rewrite");
                    continue;
                }

                var controller = layer.animatorController as AnimatorController;
                if (controller == null)
                {
                    // An override controller's parameters come from the controller it overrides.
                    // Skipping it would leave those parameters looking unreferenced, which is the
                    // same blind spot this analysis exists to find in the tool.
                    var overriding = layer.animatorController as AnimatorOverrideController;
                    controller = overriding?.runtimeAnimatorController as AnimatorController;

                    if (controller == null)
                    {
                        avatar.notes.Add(label + ": holds a " + layer.animatorController.GetType().Name +
                                         ", cannot be read");
                        continue;
                    }

                    avatar.notes.Add(label + ": an override controller over " + controller.name);
                }

                playable.Add(controller);

                bool fx = layer.type == VRCAvatarDescriptor.AnimLayerType.FX;
                Scope owned = fx ? Scope.OwnedFx : Scope.OwnedPrepared;

                string path = AssetDatabase.GetAssetPath(controller);
                avatar.notes.Add(label + ": " + path + "  (" + controller.layers.Length + " layers, " +
                                 controller.parameters.Length + " parameters" +
                                 (fx ? ", copied during the build" : ", needs Prepare") + ")");

                // GoGo Loco addresses its parameters from world-side scripts by name. Kanna decides
                // this on the CONTROLLER name rather than the parameter name, which is the sharper
                // test - the parameters themselves are not distinctively named.
                bool gogo = (path ?? "").IndexOf("gogo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (controller.name ?? "").IndexOf("gogo", StringComparison.OrdinalIgnoreCase) >= 0;
                if (gogo) avatar.notes.Add("  ^ looks like GoGo Loco, every parameter it names is excluded");

                CollectController(avatar, controller, owned, label, gogo, declared);
            }
        }

        private static void CollectController(Avatar avatar, AnimatorController controller, Scope owned,
                                              string label, bool gogo, HashSet<string> declared)
        {
            string controllerPath = AssetDatabase.GetAssetPath(controller);

            // An object a controller points at is only ours if a file copy of the controller would
            // bring it along - that is, if it lives in the same file.
            Scope ScopeOf(UnityEngine.Object o)
            {
                if (gogo) return Scope.ForeignController;
                if (o == null) return owned;
                string p = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(p) || p == controllerPath) return owned;
                return Scope.SeparateAsset;
            }

            foreach (var p in controller.parameters)
                avatar.Add(p.name, gogo ? Scope.ForeignController : owned,
                           label + ": declared (" + p.type + ")");

            var seenMachines = new HashSet<AnimatorStateMachine>();
            var seenMotions = new HashSet<Motion>();

            foreach (var layer in controller.layers)
                CollectStateMachine(avatar, layer.stateMachine, label + "/" + layer.name,
                                    ScopeOf, seenMachines, seenMotions, gogo, declared);
        }

        private static void CollectStateMachine(Avatar avatar, AnimatorStateMachine machine, string where,
                                                Func<UnityEngine.Object, Scope> scopeOf,
                                                HashSet<AnimatorStateMachine> seenMachines,
                                                HashSet<Motion> seenMotions, bool gogo,
                                                HashSet<string> declared)
        {
            if (machine == null || !seenMachines.Add(machine)) return;

            foreach (var t in machine.anyStateTransitions) CollectTransition(avatar, t, where + "/AnyState", scopeOf);
            foreach (var t in machine.entryTransitions) CollectTransition(avatar, t, where + "/Entry", scopeOf);
            foreach (var b in machine.behaviours) CollectBehaviour(avatar, b, where, scopeOf(machine));

            foreach (var child in machine.states)
            {
                var state = child.state;
                if (state == null) continue;

                string stateWhere = where + "/" + state.name;
                var scope = scopeOf(state);

                // Recorded whether or not the *Active flag is set: the flag is a checkbox the artist
                // can turn on later, and a name left behind in a disabled field is still a name that
                // would have to be rewritten to stay consistent.
                Field(state.mirrorParameter, "mirror");
                Field(state.speedParameter, "speed");
                Field(state.cycleOffsetParameter, "cycleOffset");
                Field(state.timeParameter, "time");

                void Field(string name, string kind)
                {
                    if (!string.IsNullOrEmpty(name)) avatar.Add(name, scope, stateWhere + " (" + kind + ")");
                }

                foreach (var t in state.transitions) CollectTransition(avatar, t, stateWhere, scopeOf);
                foreach (var b in state.behaviours) CollectBehaviour(avatar, b, stateWhere, scope);

                CollectMotion(avatar, state.motion, stateWhere, scopeOf, seenMotions, gogo, declared);
            }

            foreach (var child in machine.stateMachines)
            {
                foreach (var t in machine.GetStateMachineTransitions(child.stateMachine))
                    CollectTransition(avatar, t, where + "/->" + SafeName(child.stateMachine), scopeOf);

                CollectStateMachine(avatar, child.stateMachine, where + "/" + SafeName(child.stateMachine),
                                    scopeOf, seenMachines, seenMotions, gogo, declared);
            }
        }

        private static string SafeName(AnimatorStateMachine m) => m == null ? "(null)" : m.name;

        private static void CollectTransition(Avatar avatar, AnimatorTransitionBase transition, string where,
                                              Func<UnityEngine.Object, Scope> scopeOf)
        {
            if (transition == null || transition.conditions == null) return;
            var scope = scopeOf(transition);
            foreach (var condition in transition.conditions)
                avatar.Add(condition.parameter, scope, where + " (condition)");
        }

        private static void CollectMotion(Avatar avatar, Motion motion, string where,
                                          Func<UnityEngine.Object, Scope> scopeOf,
                                          HashSet<Motion> seen, bool gogo, HashSet<string> declared)
        {
            if (motion == null || !seen.Add(motion)) return;

            if (motion is BlendTree tree)
            {
                var scope = scopeOf(tree);
                if (scope == Scope.SeparateAsset)
                    avatar.separateAssetPaths.Add(AssetDatabase.GetAssetPath(tree));
                if (!string.IsNullOrEmpty(tree.blendParameter))
                    avatar.Add(tree.blendParameter, scope, where + "/tree " + tree.name + " (blend)");
                if (!string.IsNullOrEmpty(tree.blendParameterY) &&
                    tree.blendParameterY != tree.blendParameter)
                    avatar.Add(tree.blendParameterY, scope, where + "/tree " + tree.name + " (blendY)");

                foreach (var child in tree.children)
                {
                    if (!string.IsNullOrEmpty(child.directBlendParameter))
                        avatar.Add(child.directBlendParameter, scope,
                                   where + "/tree " + tree.name + " (direct)");
                    CollectMotion(avatar, child.motion, where + "/tree " + tree.name, scopeOf, seen, gogo,
                                  declared);
                }
                return;
            }

            if (motion is AnimationClip clip)
            {
                // A clip can animate an animator parameter: an Animator-typed curve on the root with
                // the parameter as its property name. Our obfuscator clones project clips into the
                // controller it owns, so those are rewritable - except the ones it deliberately
                // never clones, which the client matches by name.
                Scope scope = gogo || IsClientOwned(clip) ? Scope.ClipNotCloned : scopeOf(null);

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(Animator) || !string.IsNullOrEmpty(binding.path)) continue;

                    // Muscle curves arrive here too and are not parameters. See the note in Collect.
                    if (!declared.Contains(binding.propertyName)) continue;

                    avatar.Add(binding.propertyName, scope, where + "/clip " + clip.name + " (curve)");
                }
            }
        }

        private static bool IsClientOwned(UnityEngine.Object o)
        {
            if (o == null) return false;
            if (o.name != null && o.name.StartsWith("proxy_", StringComparison.OrdinalIgnoreCase)) return true;
            string path = AssetDatabase.GetAssetPath(o);
            return !string.IsNullOrEmpty(path) &&
                   path.StartsWith("Packages/com.vrchat.", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// State machine behaviours, by reflection rather than by type.
        ///
        /// The parameter driver is the one that matters and it has four name fields, but a hard
        /// reference to it would make this file depend on which SDK version is installed, and any
        /// behaviour that grows a parameter field in a future release would be missed silently.
        /// Reading string fields whose names say "parameter" catches both.
        /// </summary>
        private static void CollectBehaviour(Avatar avatar, StateMachineBehaviour behaviour, string where,
                                             Scope scope)
        {
            if (behaviour == null) return;
            var type = behaviour.GetType();

            if (type.Name.IndexOf("AvatarParameterDriver", StringComparison.Ordinal) >= 0)
            {
                var field = type.GetField("parameters", BindingFlags.Public | BindingFlags.Instance);
                var list = field?.GetValue(behaviour) as IEnumerable;
                if (list != null)
                {
                    foreach (var entry in list)
                    {
                        if (entry == null) continue;
                        foreach (var name in new[] { "name", "source", "destParam", "sourceParam" })
                        {
                            var f = entry.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                            if (f == null || f.FieldType != typeof(string)) continue;
                            avatar.Add((string)f.GetValue(entry), scope, where + " (driver." + name + ")");
                        }
                    }
                }
                return;
            }

            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType != typeof(string)) continue;
                if (f.Name.IndexOf("arameter", StringComparison.Ordinal) < 0) continue;
                avatar.Add((string)f.GetValue(behaviour), scope, where + " (" + type.Name + "." + f.Name + ")");
            }
        }

        private static void CollectExpressionParameters(Avatar avatar, VRCAvatarDescriptor descriptor)
        {
            var asset = descriptor.expressionParameters;
            if (asset == null || asset.parameters == null)
            {
                avatar.notes.Add("expression parameters: none");
                return;
            }

            avatar.notes.Add("expression parameters: " + asset.parameters.Length + " entries, cost " +
                             asset.CalcTotalCost() + " / " + VRCExpressionParameters.MAX_PARAMETER_COST);

            foreach (var p in asset.parameters)
            {
                if (p == null || string.IsNullOrEmpty(p.name)) continue;
                var param = avatar.Get(p.name);
                param.inExpressionList = true;
                param.expressionType = p.valueType.ToString();
                param.sites.Add(new Site { scope = Scope.ExpressionList, where = "expression parameter list" });
            }
        }

        private static void CollectMenu(Avatar avatar, VRCExpressionsMenu menu, bool root,
                                        HashSet<VRCExpressionsMenu> seen, string where)
        {
            if (menu == null || !seen.Add(menu)) return;
            if (menu.controls == null) return;

            var scope = root ? Scope.RootMenu : Scope.SubMenu;

            foreach (var control in menu.controls)
            {
                if (control == null) continue;

                if (control.parameter != null)
                    avatar.Add(control.parameter.name, scope, where + "/" + control.name);

                if (control.subParameters != null)
                    foreach (var sub in control.subParameters)
                        if (sub != null)
                            avatar.Add(sub.name, scope, where + "/" + control.name + " (sub)");

                CollectMenu(avatar, control.subMenu, false, seen, where + "/" + control.name);
            }
        }

        private static void CollectComponents(Avatar avatar, GameObject root,
                                              List<AnimatorController> playable)
        {
            int physBones = 0, contacts = 0, ma = 0;

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                var type = component.GetType();
                string typeName = type.Name;
                string path = HierarchyPath(component.transform, root.transform);

                if (typeName == "VRCPhysBone")
                {
                    string value = StringField(component, "parameter");
                    if (!string.IsNullOrEmpty(value))
                    {
                        physBones++;
                        avatar.Add(value, Scope.Component, "PhysBone " + path, true);
                    }
                    continue;
                }

                if (typeName == "VRCRaycast")
                {
                    string value = StringField(component, "Parameter") ?? StringField(component, "parameter");
                    if (!string.IsNullOrEmpty(value))
                        avatar.Add(value, Scope.Component, "Raycast " + path, true);
                    continue;
                }

                if (typeName == "VRCContactReceiver")
                {
                    string value = StringField(component, "parameter");
                    if (!string.IsNullOrEmpty(value))
                    {
                        contacts++;
                        avatar.Add(value, Scope.Component, "ContactReceiver " + path);
                    }
                    continue;
                }

                if (type.FullName != null && type.FullName.StartsWith("nadena.dev.", StringComparison.Ordinal))
                {
                    ma++;
                    continue;
                }

                if (component is Animator animator)
                {
                    var controller = animator.runtimeAnimatorController as AnimatorController;
                    if (controller == null || playable.Contains(controller)) continue;
                    foreach (var p in controller.parameters)
                        avatar.Add(p.name, Scope.ChildAnimator, "Animator " + path + " (" + controller.name + ")");
                }
            }

            avatar.notes.Add("components: " + physBones + " PhysBone prefix(es), " + contacts +
                             " contact receiver(s), " + ma + " Modular Avatar / NDMF component(s)");
            if (ma > 0)
                avatar.notes.Add("  ^ those run before this tool during a build and can add or rename " +
                                 "parameters, so the build-time recheck is what has to catch them");
        }

        private static string StringField(Component component, string field)
        {
            var f = component.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            return f != null && f.FieldType == typeof(string) ? (string)f.GetValue(component) : null;
        }

        private static string HierarchyPath(Transform t, Transform root)
        {
            var parts = new List<string>();
            while (t != null && t != root) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ------------------------------------------------------------------ verdicts

        private static string Excuse(Avatar avatar, Param param, int mode)
        {
            string bare = param.isPhysBonePrefix ? param.name : Strip(param.name);

            if (VrcBuiltIn.Contains(param.name) || VrcBuiltIn.Contains(bare))
                return "vrc-builtin";
            if (VrcMenuConvention.Contains(param.name))
                return "vrc-menu-convention";
            if (param.name.StartsWith("FT/", StringComparison.OrdinalIgnoreCase) ||
                param.name.StartsWith("v2/", StringComparison.OrdinalIgnoreCase))
                return "face-tracking-convention";
            if (param.name.Contains("/"))
                return "namespaced (third party convention)";

            if (param.inExpressionList && mode < 2)
                return "in the expression parameter list";

            var blocked = param.sites.Where(s => !Rewritable(s.scope, mode))
                                     .Select(s => s.scope)
                                     .Distinct()
                                     .ToList();
            if (blocked.Count > 0)
                return string.Join(", ", blocked.Select(s => "not rewritable: " + s));

            if (param.sites.Count == 0) return "no reference found";
            return null;
        }

        /// <summary>A PhysBone parameter appears in animators with the eight suffixes attached.</summary>
        private static string Strip(string name)
        {
            foreach (var suffix in PhysBoneSuffixes)
                if (name.EndsWith(suffix, StringComparison.Ordinal))
                    return name.Substring(0, name.Length - suffix.Length);
            return name;
        }

        private static void Report(Avatar avatar, StringBuilder text)
        {
            text.AppendLine("################ " + avatar.descriptor.name + " ################");
            foreach (var note in avatar.notes) text.AppendLine("  " + note);
            text.AppendLine();

            var all = avatar.parameters.Values.OrderBy(p => p.name, StringComparer.Ordinal).ToList();
            var names = new[]
            {
                "Today (FX only)", "Mode 2a (+prepared copies)", "Mode 2b (+expressions+menus)",
                "Mode 2b + cloned blend trees"
            };

            text.AppendLine("---- summary ----");
            text.AppendLine("  parameters seen anywhere: " + all.Count +
                            "  (in the expression list: " + all.Count(p => p.inExpressionList) + ")");
            for (int mode = 0; mode < names.Length; mode++)
            {
                int ok = all.Count(p => Excuse(avatar, p, mode) == null);
                text.AppendLine("  " + names[mode].PadRight(30) + " renameable " + ok + " / " + all.Count);
            }
            text.AppendLine();

            for (int mode = 0; mode < names.Length; mode++)
            {
                text.AppendLine("---- why not, in " + names[mode] + " ----");
                var reasons = all.Select(p => Excuse(avatar, p, mode))
                                 .Where(r => r != null)
                                 .GroupBy(r => r)
                                 .OrderByDescending(g => g.Count());
                foreach (var group in reasons)
                    text.AppendLine("  " + group.Count().ToString().PadLeft(4) + "  " + group.Key);
                text.AppendLine();
            }

            if (avatar.separateAssetPaths.Count > 0)
            {
                text.AppendLine("---- blend trees stored outside the controller that points at them ----");
                foreach (var path in avatar.separateAssetPaths.OrderBy(p => p, StringComparer.Ordinal))
                    text.AppendLine("  " + path);
                text.AppendLine();
            }

            text.AppendLine("---- every parameter ----");
            foreach (var param in all)
            {
                string verdict2a = Excuse(avatar, param, 1) == null ? "2a" : "  ";
                string verdict2b = Excuse(avatar, param, 2) == null ? "2b" : "  ";
                string verdict3 = Excuse(avatar, param, 3) == null ? "+tree" : "     ";
                string reason = Excuse(avatar, param, 3) ?? "RENAMEABLE";

                text.AppendLine("[" + verdict2a + " " + verdict2b + " " + verdict3 + "] " + param.name +
                                (param.isPhysBonePrefix ? "  (physbone prefix)" : "") +
                                (param.inExpressionList ? "  (expression " + param.expressionType + ")" : "") +
                                "  -- " + reason);

                foreach (var group in param.sites.GroupBy(s => s.scope))
                {
                    var examples = group.Select(s => s.where).Distinct().Take(3).ToList();
                    text.AppendLine("        " + group.Count().ToString().PadLeft(4) + " x " + group.Key +
                                    "   e.g. " + string.Join(" | ", examples));
                }
            }
        }
    }
}
#endif
