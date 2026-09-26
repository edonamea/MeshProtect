#if UNITY_EDITOR && LILMP_VRCSDK3_AVATARS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace MeshProtect
{
    /// <summary>
    /// Decides which of an avatar's parameters may be renamed, and rewrites the ones that may.
    ///
    /// A PARAMETER IS AVATAR-GLOBAL. Layers, states and clips belong to one controller, so renaming
    /// them in the FX copy while leaving Base alone is harmless. A parameter is not like that:
    /// rename "Action_Mode" in FX and the Base layer's "Action_Mode" becomes a different parameter,
    /// and the two stop talking to each other. So every parameter has exactly two outcomes - every
    /// place that names it gets rewritten, or none of them does. There is no partial credit, and a
    /// single missed reference is a broken avatar rather than one less obfuscated name.
    ///
    /// THE DEFAULT IS THEREFORE INVERTED. Kanna Protecc, and every blacklist design, starts from
    /// "rename unless a rule says not to" - which fails towards a broken upload discovered by the
    /// customer. This starts from the opposite end: enumerate every reference to the name in the
    /// whole avatar, and rename only if EVERY one of them lands somewhere this build rewrites, and
    /// the name is not one the client drives itself. Anything unaccounted for keeps its name. A
    /// missing rule then costs one unobfuscated parameter instead of an avatar that cannot stand up.
    ///
    /// WHAT COUNTS AS A REFERENCE was taken from Modular Avatar's RenameParametersHook, which is the
    /// only complete list anyone maintains, and checked against a real avatar. Two of them are not
    /// obvious:
    ///
    /// - A clip can drive a parameter, through an Animator-typed curve on the avatar root. So can
    ///   every muscle curve in a humanoid clip: "RootT.x" and "LeftHand.Index.1 Stretch" arrive
    ///   through the same API and look identical to a parameter. Only names a controller declares
    ///   are treated as parameters; without that test a survey of one avatar reported 150 muscle
    ///   curves as parameters that could not be renamed.
    ///
    /// - A blend tree can live in its own .asset file rather than inside the controller. Copying the
    ///   controller does not copy it - the copy still points at the artist's file by GUID - so
    ///   renaming the parameter in the controller and not in the tree produces exactly the failure
    ///   Kanna's fork documented: VRChat refuses the upload with "uses parameter X which is not
    ///   float type". On the avatar this was measured against, 320 blend trees are stored that way
    ///   and they blocked 43 parameters on their own, so they are copied into the controller copy
    ///   rather than written to.
    /// </summary>
    internal static class MeshProtectParameters
    {
        /// <summary>
        /// Where a reference lives, in terms of who is able to rewrite it.
        /// </summary>
        internal enum Scope
        {
            /// <summary>Inside a controller this build rewrites, in that controller's own file.</summary>
            Owned,

            /// <summary>An object a rewritten controller points at that lives in a different file -
            /// the blend tree saved as its own .asset. Rewritable only by copying it in first.</summary>
            SeparateAsset,

            /// <summary>A playable layer or external behaviour left exactly as the avatar had it.</summary>
            ForeignController,

            /// <summary>A clip that is never cloned, so its curves cannot be rewritten: VRChat's own
            /// proxy animations and anything shipped inside a com.vrchat package.</summary>
            ClipNotCloned,

            /// <summary>The expression parameter list. Rewritten with the expression option on -
            /// which it is by default, and that is a product decision rather than a proof. This
            /// list is the avatar's public API: OSC applications address exactly these names and
            /// nothing inside the project can prove they do not, so what stands in for a proof here
            /// is a set of name rules in Judge (VRChat's own, namespaced, VRCFaceTracking's
            /// documented list, the "Tracking" substring) plus a report naming everything renamed.
            /// The option exists so an author whose avatar is out in the world can decline.</summary>
            ExpressionList,

            /// <summary>A menu, anywhere in the tree. Rewritten with the expression option on, which
            /// is also what makes the build take its own copy of the whole tree.</summary>
            Menu,

            /// <summary>PhysBone prefix or contact receiver. Rewritten on the build clone, so the
            /// scene is not touched. Contact TAGS, which are the part other avatars see, are never
            /// renamed.</summary>
            Component,

            /// <summary>An Animator under the avatar that is not a playable layer. Its parameters
            /// are a separate space, and it is not rewritten.</summary>
            ChildAnimator,

            /// <summary>Two components whose prefixes nest - "Ear" and "Ear_Squish" - so the names
            /// they generate at runtime cannot be told apart. See MergePhysBonePrefixes.</summary>
            NestedComponentPrefix,

            /// <summary>A playable layer holding something this build cannot read the parameters
            /// of. It may drive any of them, so it blocks all of them.</summary>
            UnreadableLayer,
        }

        internal sealed class Site
        {
            public Scope scope;
            public string where;
        }

        internal sealed class Finding
        {
            public string name;
            public bool physBonePrefix;
            public bool inExpressionList;
            public readonly List<Site> sites = new List<Site>();

            /// <summary>Null when the parameter may be renamed. Otherwise why it may not, in words
            /// the avatar's author can act on.</summary>
            public string blockedBy;
        }

        internal sealed class Options
        {
            /// <summary>Playable layer controllers whose contents this build rewrites.</summary>
            public readonly HashSet<AnimatorController> rewritable = new HashSet<AnimatorController>();

            /// <summary>Controllers to leave out of the survey entirely, because what ships in their
            /// place is a copy that was already rewritten. Used at build time for the prepared
            /// copies: surveying the avatar's own controller there would report the blend trees it
            /// keeps in separate files, which the copy no longer points at.</summary>
            public readonly HashSet<AnimatorController> replaced = new HashSet<AnimatorController>();

            /// <summary>Rewrite the expression parameter list and the whole menu tree.</summary>
            public bool expressionParameters;

            /// <summary>Copy blend trees stored outside the controller into the copy.</summary>
            public bool copySeparateBlendTrees = true;

            /// <summary>Names this build generated for itself. They are already meaningless, and the
            /// validator finds them by the exact string held in the variant.</summary>
            public readonly HashSet<string> reserved = new HashSet<string>();
        }

        // ------------------------------------------------------------------ the client's own names

        /// <summary>
        /// Parameters the VRChat client drives by name. Taken from Modular Avatar's
        /// ParameterPolicy.VRCSDKParameters, which is the newest maintained copy - the SDK exposes
        /// no API for this, so every tool keeps a hand list and every hand list can go stale. A name
        /// added to a future SDK release and missing from here would be renamed and stop working,
        /// which is why the report names what it renamed rather than only counting it.
        /// </summary>
        private static readonly HashSet<string> ClientDriven = new HashSet<string>
        {
            "IsLocal", "PreviewMode", "Viseme", "Voice",
            "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight",
            "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude",
            "Upright", "Grounded", "Seated", "AFK", "TrackingType", "VRMode", "MuteSelf",
            "InStation", "Earmuffs", "IsOnFriendsList", "AvatarVersion",
            "ScaleModified", "ScaleFactor", "ScaleFactorInverse",
            "EyeHeightAsMeters", "EyeHeightAsPercent", "IsAnimatorEnabled",
        };

        /// <summary>The action menu's conventional names, which the client also drives.</summary>
        private static readonly HashSet<string> MenuConvention = new HashSet<string>(
            new[] { "VRCEmote", "VRCFaceBlendH", "VRCFaceBlendV", "VRCFaceBlendL", "VRCFaceBlendR" }
                .Concat(Enumerable.Range(1, 16).Select(i => "Expression" + i)));

        /// <summary>
        /// VRCFaceTracking's parameter names, for the versions that do not put a namespace in front.
        ///
        /// Read off the project's own documentation on 2026-08-13, not from memory:
        ///   https://docs.vrcft.io/docs/tutorial-avatars/tutorial-avatars-extras/parameters
        ///   https://docs.vrcft.io/docs/v4.0/tutorial-avatars/tutorial-avatars-extras/parameters/eye-tracking-parameters
        ///   https://docs.vrcft.io/docs/v4.0/tutorial-avatars/tutorial-avatars-extras/parameters/lip-tracking-parameters
        ///
        /// Reading them settled what the substring rule was guessing at. Current VRCFaceTracking
        /// namespaces everything - "v2/JawOpen", and any prefix an author adds sits in front of that
        /// - so the whole Unified Expressions set is already covered by the rule about names holding
        /// a slash, and none of it is repeated here. Exactly three current names have no namespace,
        /// and all three happen to contain "Tracking". The version before it namespaced NOTHING, and
        /// almost none of those names contain "Tracking" either: "JawOpen", "MouthSmileLeft",
        /// "LeftEyeLid", "EyesX". That is the set below, and it was the real gap - an avatar wearing
        /// an older face tracking setup whose controller is not named after it (merged into FX by
        /// Modular Avatar, say) had nothing standing between those names and the expression option.
        ///
        /// A list is a thing that goes stale, which is why it is not the only rule: the substring
        /// and the controller name still run, and the report still names what it renamed. Blocking a
        /// name an avatar happened to reuse - "MouthOpen" as somebody's own toggle - costs one
        /// unrenamed parameter, which is the direction this file always errs in.
        /// </summary>
        private static readonly HashSet<string> FaceTracking = new HashSet<string>(StringComparer.Ordinal)
        {
            // Current, the only three that carry no namespace.
            "EyeTrackingActive", "ExpressionTrackingActive", "LipTrackingActive",

            // v4 eye tracking.
            "EyesX", "EyesY", "LeftEyeLid", "RightEyeLid", "CombinedEyeLid", "EyesWiden",
            "EyesDilation", "EyesPupilDiameter", "EyesSqueeze", "LeftEyeX", "LeftEyeY", "RightEyeX",
            "RightEyeY", "LeftEyeWiden", "RightEyeWiden", "LeftEyeSqueeze", "RightEyeSqueeze",
            "LeftEyeLidExpanded", "RightEyeLidExpanded", "CombinedEyeLidExpanded",
            "LeftEyeLidExpandedSqueeze", "RightEyeLidExpandedSqueeze", "CombinedEyeLidExpandedSqueeze",

            // v4 lip tracking.
            "JawRight", "JawLeft", "JawForward", "JawOpen", "MouthApeShape", "MouthUpperRight",
            "MouthUpperLeft", "MouthLowerRight", "MouthLowerLeft", "MouthUpperOverturn",
            "MouthLowerOverturn", "MouthPout", "MouthSmileRight", "MouthSmileLeft", "MouthSadRight",
            "MouthSadLeft", "CheekPuffRight", "CheekPuffLeft", "CheekSuck", "MouthUpperUpRight",
            "MouthUpperUpLeft", "MouthLowerDownRight", "MouthLowerDownLeft", "MouthUpperInside",
            "MouthLowerInside", "MouthLowerOverlay", "TongueLongStep1", "TongueLongStep2",
            "TongueDown", "TongueUp", "TongueRight", "TongueLeft", "TongueRoll", "TongueUpLeftMorph",
            "TongueUpRightMorph", "TongueDownLeftMorph", "TongueDownRightMorph",

            // v4 combined lip parameters, which is where most of the length comes from.
            "JawX", "MouthUpper", "MouthLower", "MouthX", "MouthUpperInsideOverturn",
            "MouthLowerInsideOverturn", "SmileSadRight", "SmileSadLeft", "SmileSad", "TongueY",
            "TongueX", "TongueSteps", "PuffSuckRight", "PuffSuckLeft", "PuffSuck", "JawOpenApe",
            "JawOpenPuff", "JawOpenPuffRight", "JawOpenPuffLeft", "JawOpenSuck", "JawOpenForward",
            "JawOpenOverlay", "MouthUpperUpRightUpperInside", "MouthUpperUpRightPuffRight",
            "MouthUpperUpRightApe", "MouthUpperUpRightPout", "MouthUpperUpRightOverlay",
            "MouthUpperUpRightSuck", "MouthUpperUpLeftUpperInside", "MouthUpperUpLeftPuffLeft",
            "MouthUpperUpLeftApe", "MouthUpperUpLeftPout", "MouthUpperUpLeftOverlay",
            "MouthUpperUpLeftSuck", "MouthUpperUpUpperInside", "MouthUpperUpInside",
            "MouthUpperUpPuff", "MouthUpperUpPuffLeft", "MouthUpperUpPuffRight", "MouthUpperUpApe",
            "MouthUpperUpPout", "MouthUpperUpOverlay", "MouthUpperUpSuck",
            "MouthLowerDownRightLowerInside", "MouthLowerDownRightPuffRight", "MouthLowerDownRightApe",
            "MouthLowerDownRightPout", "MouthLowerDownRightOverlay", "MouthLowerDownRightSuck",
            "MouthLowerDownLeftLowerInside", "MouthLowerDownLeftPuffLeft", "MouthLowerDownLeftApe",
            "MouthLowerDownLeftPout", "MouthLowerDownLeftOverlay", "MouthLowerDownLeftSuck",
            "MouthLowerDownLowerInside", "MouthLowerDownInside", "MouthLowerDownPuff",
            "MouthLowerDownPuffLeft", "MouthLowerDownPuffRight", "MouthLowerDownApe",
            "MouthLowerDownPout", "MouthLowerDownOverlay", "MouthLowerDownSuck",
            "SmileRightUpperOverturn", "SmileRightLowerOverturn", "SmileRightOverturn",
            "SmileRightApe", "SmileRightOverlay", "SmileRightPout", "SmileLeftUpperOverturn",
            "SmileLeftLowerOverturn", "SmileLeftOverturn", "SmileLeftApe", "SmileLeftOverlay",
            "SmileLeftPout", "SmileUpperOverturn", "SmileLowerOverturn", "SmileApe", "SmileOverlay",
            "SmilePout", "PuffRightUpperOverturn", "PuffRightLowerOverturn", "PuffRightOverturn",
            "PuffLeftUpperOverturn", "PuffLeftLowerOverturn", "PuffLeftOverturn",
            "PuffUpperOverturn", "PuffLowerOverturn", "PuffOverturn",
        };

        /// <summary>
        /// What a PhysBone turns its prefix into. From Modular Avatar; the tool has to know these
        /// because the animator sees "Ear_IsGrabbed" while the component holds "Ear", and renaming
        /// one without the other silently unhooks the bone.
        /// </summary>
        internal static readonly string[] PhysBoneSuffixes =
        {
            "_IsGrabbed", "_Angle", "_Stretch", "_IsPosed", "_Squish", "_Hit", "_Ratio", "_Distance"
        };

        // ------------------------------------------------------------------ survey

        /// <summary>
        /// Every parameter the avatar mentions, every place it is mentioned, and whether that place
        /// can be rewritten. Reads only.
        /// </summary>
        internal static List<Finding> Survey(VRCAvatarDescriptor descriptor, Options options)
        {
            var found = new Dictionary<string, Finding>(StringComparer.Ordinal);

            Finding Get(string name)
            {
                if (!found.TryGetValue(name, out var f))
                {
                    f = new Finding { name = name };
                    found[name] = f;
                }
                return f;
            }

            void Add(string name, Scope scope, string where)
            {
                if (string.IsNullOrEmpty(name)) return;
                Get(name).sites.Add(new Site { scope = scope, where = where });
            }

            var declared = DeclaredParameters(descriptor);
            string unreadable = null;

            foreach (var layers in new[] { descriptor.baseAnimationLayers, descriptor.specialAnimationLayers })
            {
                if (layers == null) continue;
                foreach (var layer in layers)
                {
                    if (layer.isDefault || layer.animatorController == null) continue;

                    // A playable layer holds a RuntimeAnimatorController, not an AnimatorController,
                    // and an override controller is a legal thing to put in one. Skipping it would
                    // be the worst possible reading: a parameter only that layer uses would be
                    // recorded nowhere, look unreferenced, pass the proof, and get renamed
                    // everywhere else while that layer went on driving the old name.
                    //
                    // An override's parameters come from the controller it overrides, so reading
                    // that one and marking it as somebody else's is exact. Anything else is
                    // unreadable, and then nothing about this avatar can be proved at all.
                    var controller = layer.animatorController as AnimatorController;
                    if (controller == null)
                    {
                        var overriding = layer.animatorController as AnimatorOverrideController;
                        controller = overriding?.runtimeAnimatorController as AnimatorController;

                        if (controller == null)
                        {
                            unreadable = unreadable ?? (layer.type + " holds a " +
                                                        layer.animatorController.GetType().Name);
                            continue;
                        }

                        SurveyController(controller, layer.type + " (overridden)", false, options,
                                         declared, Add);
                        continue;
                    }

                    if (options.replaced.Contains(controller)) continue;

                    bool ours = options.rewritable.Contains(controller);
                    SurveyController(controller, layer.type.ToString(), ours, options, declared, Add);
                }
            }

            SurveyExpressionParameters(descriptor, Get, Add);
            SurveyMenu(descriptor.expressionsMenu, "menu", new HashSet<VRCExpressionsMenu>(), Add);
            // Kept as its own set rather than read back off the sites afterwards. A PhysBone records
            // its prefix AND the eight names it generates, all as Component sites, so a site cannot
            // say which of the two it is - and the merge below has to know exactly that.
            var componentPrefixes = new HashSet<string>(StringComparer.Ordinal);
            SurveyComponents(descriptor.gameObject, descriptor, componentPrefixes, Add);

            var findings = found.Values.ToList();
            MergePhysBonePrefixes(findings, componentPrefixes);

            // One layer nobody can read is enough to make every parameter unprovable: it may drive
            // any of them, and there is no way to find out which.
            if (unreadable != null)
                foreach (var finding in findings)
                    finding.sites.Add(new Site { scope = Scope.UnreadableLayer, where = unreadable });

            foreach (var finding in findings)
                finding.blockedBy = Judge(finding, options);

            return findings.OrderBy(f => f.name, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Every name any playable layer declares as a parameter.
        ///
        /// This is what separates a curve that drives a parameter from a humanoid muscle curve -
        /// they are the same kind of binding and Unity only treats the former as a parameter because
        /// the name matches a declared one.
        /// </summary>
        private static HashSet<string> DeclaredParameters(VRCAvatarDescriptor descriptor)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
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
            return declared;
        }

        private static void SurveyController(AnimatorController controller, string label, bool ours,
                                             Options options, HashSet<string> declared,
                                             Action<string, Scope, string> add)
        {
            string controllerPath = AssetDatabase.GetAssetPath(controller);

            // Systems addressed from outside the avatar, recognised by the CONTROLLER's name rather
            // than by the parameters' - the parameters themselves are not distinctively named, and
            // no amount of looking inside a project disproves a reference living in a world or in
            // an OSC application. These are blacklist entries in a design that is otherwise the
            // opposite, and they are kept because they fail in the safe direction: the only cost of
            // a false positive is a name that keeps its own.
            //
            // FACE TRACKING IS WHY THIS GREW. The modern naming is namespaced - "v2/JawOpen" - and
            // the rules in Judge catch it. VRCFaceTracking also drives plain names like "JawOpen"
            // and "EyeLidLeft" on older setups, and nothing about those names says what they are.
            // What does say it is the controller they live in. Taken from Kanna Protecc, which
            // matches the same shapes; without them, ticking the expression parameter option on an
            // avatar with an older face tracking setup renames its parameters and the tracking
            // simply stops, with the report the only place it is visible.
            // Both separators on both, which Kanna does not do - it looks for "e tracking" on the
            // controller and "e_tracking" on the layer, so a layer called "Face Tracking" and a
            // controller called "Face_Tracking" each fall through. Copying the shapes it matches was
            // deliberate; copying which half of the pair it happens to check was not.
            bool TrackingName(string name) =>
                Looks(name, "gogo") || Looks(name, "vrcft") || Looks(name, "facetrack") ||
                Looks(name, "e tracking") || Looks(name, "e_tracking");

            // The path and name tests are for OTHER people's controllers - a GoGoLoco install
            // is recognised by where it lives and what it is called. A controller this build
            // itself created carries neither signal: its temp path embeds the plugin's install
            // folder, which the author may have named anything, and its asset name comes from a
            // generator that can legitimately spell "gogo". Layer names still count either way,
            // because a merged GoGoLoco layer inside our own FX copy is still GoGoLoco's.
            bool foreignSystem =
                (!ours && (Looks(controllerPath, "gogo") || TrackingName(controller.name))) ||
                controller.layers.Any(l => TrackingName(l.name));

            bool gogo = foreignSystem;

            // A controller whose STRUCTURE is stored outside its own file is left alone entirely.
            //
            // Copying the file does not bring those objects along, so the copy still points at the
            // artist's assets and none of them may be written to. Blend trees in that position are
            // handled - they are copied inside first - but a state or a transition is not, and the
            // rename passes skip them one by one. That is correct for layer names, where the cost
            // is one readable name, and wrong for parameters, where a single skipped transition
            // means the avatar's own layers disagree about what a parameter is called. Real avatars
            // are built this way; the obfuscator has warned about it since it shipped.
            bool structureOutside = StructureLivesElsewhere(controller, controllerPath);

            Scope owned = ours && !gogo && !structureOutside ? Scope.Owned : Scope.ForeignController;

            Scope ScopeOf(UnityEngine.Object o)
            {
                if (owned != Scope.Owned) return owned;
                if (o == null) return Scope.Owned;
                string path = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(path) || path == controllerPath) return Scope.Owned;
                return Scope.SeparateAsset;
            }

            foreach (var p in controller.parameters)
                add(p.name, owned, label + ": declared " + p.type);

            var machines = new HashSet<AnimatorStateMachine>();
            var motions = new HashSet<(Motion motion, bool blockedByTree)>();
            var overrideMotions = new HashSet<(Motion motion, bool blockedByTree)>();

            foreach (var layer in controller.layers)
            {
                SurveyStateMachine(layer.stateMachine, label + "/" + layer.name, ScopeOf, machines,
                                   motions, declared, gogo, options.copySeparateBlendTrees, add);

                // Synchronized-layer overrides are stored on the layer rather than the states.
                // No rewrite pass handles those tables, even when their assets are controller-owned.
                foreach (var state in SyncedLayerStates(controller, layer))
                {
                    string where = label + "/" + layer.name + " override " + state.name;
                    SurveyMotion(layer.GetOverrideMotion(state), where, _ => Scope.ForeignController,
                                 overrideMotions, declared, false, false, false, add);
                    foreach (var behaviour in layer.GetOverrideBehaviours(state) ?? new StateMachineBehaviour[0])
                        SurveyBehaviour(behaviour, where, _ => Scope.ForeignController, add);
                }
            }
        }

        /// <summary>The states used as keys by a synchronized layer's motion/behaviour overrides.</summary>
        internal static IEnumerable<AnimatorState> SyncedLayerStates(AnimatorController controller,
                                                                     AnimatorControllerLayer layer)
        {
            int index = layer.syncedLayerIndex;
            if (index < 0) yield break;
            var layers = controller.layers;
            var visitedLayers = new HashSet<int>();
            while (index >= 0 && index < layers.Length && visitedLayers.Add(index))
            {
                var source = layers[index];
                if (source.syncedLayerIndex >= 0)
                {
                    index = source.syncedLayerIndex;
                    continue;
                }

                var pending = new Stack<AnimatorStateMachine>();
                var visited = new HashSet<AnimatorStateMachine>();
                if (source.stateMachine != null) pending.Push(source.stateMachine);
                while (pending.Count > 0)
                {
                    var machine = pending.Pop();
                    if (!visited.Add(machine)) continue;
                    foreach (var child in machine.states)
                        if (child.state != null) yield return child.state;
                    foreach (var child in machine.stateMachines)
                        if (child.stateMachine != null) pending.Push(child.stateMachine);
                }
                yield break;
            }
        }

        /// <summary>
        /// Does any state machine, state or transition of this controller live in another file?
        ///
        /// Blend trees are not asked about: those are copied inside before anything is renamed, so
        /// where they start out does not decide anything.
        /// </summary>
        internal static bool StructureLivesElsewhere(AnimatorController controller, string controllerPath)
        {
            var seen = new HashSet<AnimatorStateMachine>();

            foreach (var layer in controller.layers)
                if (Elsewhere(layer.stateMachine))
                    return true;

            return false;

            bool Elsewhere(AnimatorStateMachine machine)
            {
                if (machine == null || !seen.Add(machine)) return false;
                if (!Belongs(machine, controllerPath)) return true;

                foreach (var t in machine.anyStateTransitions)
                    if (!Belongs(t, controllerPath)) return true;
                foreach (var t in machine.entryTransitions)
                    if (!Belongs(t, controllerPath)) return true;

                foreach (var child in machine.states)
                {
                    // An empty slot is not evidence of anything; Belongs says no to null because
                    // it is asked "may I write to this", and the answer there is also no.
                    if (child.state == null) continue;
                    if (!Belongs(child.state, controllerPath)) return true;

                    foreach (var t in child.state.transitions)
                        if (!Belongs(t, controllerPath)) return true;
                }

                foreach (var child in machine.stateMachines)
                {
                    foreach (var t in machine.GetStateMachineTransitions(child.stateMachine))
                        if (!Belongs(t, controllerPath)) return true;
                    if (Elsewhere(child.stateMachine)) return true;
                }

                return false;
            }
        }

        private static bool Looks(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void SurveyStateMachine(AnimatorStateMachine machine, string where,
                                               Func<UnityEngine.Object, Scope> scopeOf,
                                               HashSet<AnimatorStateMachine> machines,
                                               HashSet<(Motion motion, bool blockedByTree)> motions,
                                               HashSet<string> declared, bool gogo,
                                               bool copySeparateBlendTrees,
                                               Action<string, Scope, string> add)
        {
            if (machine == null || !machines.Add(machine)) return;

            foreach (var t in machine.anyStateTransitions) SurveyTransition(t, where + "/AnyState", scopeOf, add);
            foreach (var t in machine.entryTransitions) SurveyTransition(t, where + "/Entry", scopeOf, add);
            foreach (var b in machine.behaviours) SurveyBehaviour(b, where, scopeOf, add);

            foreach (var child in machine.states)
            {
                var state = child.state;
                if (state == null) continue;

                string stateWhere = where + "/" + state.name;
                var scope = scopeOf(state);

                // Recorded whether or not the matching Active flag is set. The flag is a checkbox
                // the author can turn on later, and a stale name in a disabled field would still
                // have to be rewritten for the avatar to keep working when they do.
                add(state.mirrorParameter, scope, stateWhere + " mirror");
                add(state.speedParameter, scope, stateWhere + " speed");
                add(state.cycleOffsetParameter, scope, stateWhere + " cycle offset");
                add(state.timeParameter, scope, stateWhere + " time");

                foreach (var t in state.transitions) SurveyTransition(t, stateWhere, scopeOf, add);
                foreach (var b in state.behaviours) SurveyBehaviour(b, stateWhere, scopeOf, add);

                SurveyMotion(state.motion, stateWhere, scopeOf, motions, declared, gogo,
                             copySeparateBlendTrees, false, add);
            }

            foreach (var child in machine.stateMachines)
            {
                string name = child.stateMachine == null ? "(null)" : child.stateMachine.name;
                foreach (var t in machine.GetStateMachineTransitions(child.stateMachine))
                    SurveyTransition(t, where + "/->" + name, scopeOf, add);

                SurveyStateMachine(child.stateMachine, where + "/" + name, scopeOf, machines, motions,
                                   declared, gogo, copySeparateBlendTrees, add);
            }
        }

        private static void SurveyTransition(AnimatorTransitionBase transition, string where,
                                             Func<UnityEngine.Object, Scope> scopeOf,
                                             Action<string, Scope, string> add)
        {
            if (transition == null || transition.conditions == null) return;
            var scope = scopeOf(transition);
            foreach (var condition in transition.conditions)
                add(condition.parameter, scope, where + " condition");
        }

        private static void SurveyMotion(Motion motion, string where,
                                         Func<UnityEngine.Object, Scope> scopeOf,
                                         HashSet<(Motion motion, bool blockedByTree)> seen,
                                         HashSet<string> declared, bool gogo,
                                         bool copySeparateBlendTrees, bool blockedByTree,
                                         Action<string, Scope, string> add)
        {
            if (motion == null) return;

            // The rewrite stops at an external tree that was not copied, including all its clips.
            // A shared motion may also be reached through an owned branch; keep both observations.
            blockedByTree |= motion is BlendTree && !copySeparateBlendTrees &&
                             scopeOf(motion) == Scope.SeparateAsset;
            if (!seen.Add((motion, blockedByTree))) return;

            if (motion is BlendTree tree)
            {
                var scope = blockedByTree ? Scope.SeparateAsset : scopeOf(tree);
                add(tree.blendParameter, scope, where + "/tree " + tree.name + " blend");
                if (tree.blendParameterY != tree.blendParameter)
                    add(tree.blendParameterY, scope, where + "/tree " + tree.name + " blendY");

                foreach (var child in tree.children)
                {
                    add(child.directBlendParameter, scope, where + "/tree " + tree.name + " direct");
                    SurveyMotion(child.motion, where + "/tree " + tree.name, scopeOf, seen, declared,
                                 gogo, copySeparateBlendTrees, blockedByTree, add);
                }
                return;
            }

            if (motion is AnimationClip clip)
            {
                Scope scope = gogo || MeshProtectObfuscator.IsClientOwned(clip)
                    ? Scope.ClipNotCloned
                    : blockedByTree ? Scope.SeparateAsset : scopeOf(null);

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(Animator) || !string.IsNullOrEmpty(binding.path)) continue;
                    if (!declared.Contains(binding.propertyName)) continue;
                    add(binding.propertyName, scope, where + "/clip " + clip.name + " curve");
                }
            }
        }

        /// <summary>
        /// State machine behaviours, by reflection rather than by type.
        ///
        /// The parameter driver is the one that matters and it carries four separate name fields,
        /// but naming the type here would tie this file to one SDK version, and a behaviour that
        /// grows a parameter field in a future release would be missed in silence. Reading string
        /// fields whose names contain "parameter" catches both, and a false positive costs one
        /// unrenamed parameter.
        /// </summary>
        private static void SurveyBehaviour(StateMachineBehaviour behaviour, string where,
                                            Func<UnityEngine.Object, Scope> scopeOf,
                                            Action<string, Scope, string> add)
        {
            if (behaviour == null) return;
            // Copying external blend trees does not copy an external behaviour. Its names must
            // stay intact even when the state referring to it belongs to this controller.
            var scope = scopeOf(behaviour);
            if (scope == Scope.SeparateAsset) scope = Scope.ForeignController;
            foreach (var pair in BehaviourParameterFields(behaviour))
                add(pair.Value, scope, where + " " + pair.Key);
        }

        private static IEnumerable<KeyValuePair<string, string>> BehaviourParameterFields(
            StateMachineBehaviour behaviour)
        {
            if (behaviour == null) yield break;
            var type = behaviour.GetType();

            if (type.Name.IndexOf("AvatarParameterDriver", StringComparison.Ordinal) >= 0)
            {
                var list = type.GetField("parameters", BindingFlags.Public | BindingFlags.Instance)
                               ?.GetValue(behaviour) as IEnumerable;
                if (list == null) yield break;

                foreach (var entry in list)
                {
                    if (entry == null) continue;
                    foreach (var name in DriverFields)
                    {
                        var field = entry.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                        if (field == null || field.FieldType != typeof(string)) continue;
                        yield return new KeyValuePair<string, string>("driver." + name,
                                                                      (string)field.GetValue(entry));
                    }
                }
                yield break;
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType != typeof(string)) continue;
                if (field.Name.IndexOf("arameter", StringComparison.Ordinal) < 0) continue;
                yield return new KeyValuePair<string, string>(type.Name + "." + field.Name,
                                                              (string)field.GetValue(behaviour));
            }
        }

        private static readonly string[] DriverFields = { "name", "source", "destParam", "sourceParam" };

        private static void SurveyExpressionParameters(VRCAvatarDescriptor descriptor,
                                                       Func<string, Finding> get,
                                                       Action<string, Scope, string> add)
        {
            var asset = descriptor.expressionParameters;
            if (asset == null || asset.parameters == null) return;

            foreach (var p in asset.parameters)
            {
                if (p == null || string.IsNullOrEmpty(p.name)) continue;
                get(p.name).inExpressionList = true;
                add(p.name, Scope.ExpressionList, "expression parameter list");
            }
        }

        private static void SurveyMenu(VRCExpressionsMenu menu, string where,
                                       HashSet<VRCExpressionsMenu> seen,
                                       Action<string, Scope, string> add)
        {
            if (menu == null || !seen.Add(menu) || menu.controls == null) return;

            foreach (var control in menu.controls)
            {
                if (control == null) continue;
                string here = where + "/" + control.name;

                if (control.parameter != null) add(control.parameter.name, Scope.Menu, here);
                if (control.subParameters != null)
                    foreach (var sub in control.subParameters)
                        if (sub != null) add(sub.name, Scope.Menu, here + " sub");

                SurveyMenu(control.subMenu, here, seen, add);
            }
        }

        private static void SurveyComponents(GameObject root, VRCAvatarDescriptor descriptor,
                                             HashSet<string> componentPrefixes,
                                             Action<string, Scope, string> add)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                string typeName = component.GetType().Name;

                if (typeName == "VRCPhysBone" || typeName == "VRCRaycast")
                {
                    string value = StringField(component, "parameter") ?? StringField(component, "Parameter");
                    if (string.IsNullOrEmpty(value)) continue;

                    // Only these. A contact receiver holds a whole parameter name and is rewritten
                    // by looking that name up, so it stays in step whatever happens to it; a
                    // PhysBone holds half a name and the runtime supplies the rest, which is the
                    // asymmetry the merge has to be careful about.
                    componentPrefixes.Add(value);

                    // Held as the prefix. The animator only ever sees it with a suffix attached.
                    add(value, Scope.Component, typeName + " " + component.name);
                    foreach (var suffix in PhysBoneSuffixes)
                        add(value + suffix, Scope.Component, typeName + " " + component.name + suffix);
                    continue;
                }

                if (typeName == "VRCContactReceiver")
                {
                    add(StringField(component, "parameter"), Scope.Component,
                        "contact receiver " + component.name);
                    continue;
                }

                if (component is Animator animator && component.gameObject != root)
                {
                    var controller = animator.runtimeAnimatorController as AnimatorController;
                    if (controller == null) continue;
                    foreach (var p in controller.parameters)
                        add(p.name, Scope.ChildAnimator, "Animator on " + component.name);
                }
            }
        }

        private static string StringField(Component component, string field)
        {
            var f = component.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            return f != null && f.FieldType == typeof(string) ? (string)f.GetValue(component) : null;
        }

        /// <summary>
        /// Fold "Ear_IsGrabbed" into "Ear".
        ///
        /// A PhysBone is one decision, not nine: the component holds the prefix and the animator
        /// sees the suffixed names, so renaming either half alone unhooks the bone. Merging them
        /// means one blocked suffix blocks the prefix, which is the safe direction.
        /// </summary>
        private static void MergePhysBonePrefixes(List<Finding> findings,
                                                  HashSet<string> componentPrefixes)
        {
            var byName = findings.ToDictionary(f => f.name, StringComparer.Ordinal);
            var prefixes = findings
                .Where(f => f.sites.Any(s => s.scope == Scope.Component))
                .Select(f => f.name)
                .ToList();

            foreach (var prefix in prefixes)
            {
                // Already folded into another prefix, so it is no longer a finding of its own.
                if (!byName.ContainsKey(prefix)) continue;

                Finding head = null;

                foreach (var suffix in PhysBoneSuffixes)
                {
                    if (!byName.TryGetValue(prefix + suffix, out var tail)) continue;
                    if (head == null && byName.TryGetValue(prefix, out head)) head.physBonePrefix = true;
                    if (head == null) break;

                    head.sites.AddRange(tail.sites);
                    head.inExpressionList |= tail.inExpressionList;
                    findings.Remove(tail);

                    // The lookup has to follow the list. Two PhysBones whose prefixes are "A" and
                    // "A_Squish" both arrive here, and without this the second one merges into the
                    // orphan the first one already removed - taking a finding, and whatever blocked
                    // it, out of the survey entirely.
                    byName.Remove(tail.name);

                    if (componentPrefixes.Contains(tail.name))
                        BlockNestedPrefix(head, prefix, tail.name, byName);
                }
            }
        }

        /// <summary>
        /// Give up on both families when one component's prefix is another's prefix plus a suffix.
        ///
        /// "_Squish" is itself a PhysBone suffix, so a second PhysBone called "A_Squish" is folded
        /// into "A" as though it were one of the names "A" generates. The fold is not transitive and
        /// cannot be made so cheaply: "A_Squish_IsGrabbed" is neither "A" plus a suffix nor reachable
        /// once "A_Squish" has been folded away, so it stays a finding of its own and is handed an
        /// unrelated name - while the component it belongs to gets its prefix rewritten to match "A".
        /// The bone then reports grabs under a name no animation is listening for, and nothing about
        /// the avatar looks wrong.
        ///
        /// Renaming it correctly is possible and is not worth the machinery: prefixes can nest more
        /// than two deep, and the rule this file runs on is that a name it cannot account for keeps
        /// its own. Two PhysBones named this way is a rare shape; a bone that silently stops
        /// responding to being grabbed is not a rare kind of complaint.
        /// </summary>
        private static void BlockNestedPrefix(Finding head, string outer, string nested,
                                              Dictionary<string, Finding> byName)
        {
            string where = "'" + outer + "' and '" + nested + "'";
            head.sites.Add(new Site { scope = Scope.NestedComponentPrefix, where = where });

            // The nested component's own generated names, which the fold above never reached.
            foreach (var suffix in PhysBoneSuffixes)
                if (byName.TryGetValue(nested + suffix, out var generated))
                    generated.sites.Add(new Site
                    {
                        scope = Scope.NestedComponentPrefix,
                        where = where
                    });
        }

        // ------------------------------------------------------------------ the verdict

        private static string Judge(Finding finding, Options options)
        {
            if (options.reserved.Contains(finding.name))
                return "generated by this tool";

            if (ClientDriven.Contains(finding.name))
                return "VRChat drives this one by name";

            if (MenuConvention.Contains(finding.name))
                return "an action menu name VRChat drives by itself";

            // Face tracking, and every other convention that puts a namespace in front of the name.
            // These are addressed from outside the avatar - by OSC applications this project cannot
            // see - so no amount of looking inside it can prove the name is free.
            if (finding.name.IndexOf('/') >= 0)
                return "namespaced, so something outside the avatar addresses it";

            // Ahead of the substring rule, because it can say which application by name and the
            // substring rule cannot. It catches what the namespace rule above does not: the older
            // face tracking parameters, which carry no namespace at all.
            if (FaceTracking.Contains(finding.name))
                return "VRCFaceTracking drives this one by name";

            // The namespace catches the modern face tracking names - "v2/JawOpen", "FT/Brow" - and
            // misses the older ones, which are plain words an OSC application drives all the same.
            // A substring is a blunt instrument and this one is deliberate: Kanna matches the same
            // three shapes, and the trade is one occasionally unrenamed toggle against a face that
            // stops moving for a wearer who ticked the expression option.
            if (Looks(finding.name, "Tracking"))
                return "face tracking and OSC applications use names like this one";

            var blocked = finding.sites
                .Where(s => !Rewritable(s.scope, options))
                .ToList();

            if (blocked.Count == 0) return null;

            var first = blocked[0];
            switch (first.scope)
            {
                case Scope.ExpressionList:
                    return "in the expression parameter list";
                case Scope.Menu:
                    return "used by a menu";
                case Scope.SeparateAsset:
                    return "used by a blend tree stored in its own file (" + first.where + ")";
                case Scope.ForeignController:
                    return "used by a controller or behaviour this upload does not rewrite (" + first.where + ")";
                case Scope.ClipNotCloned:
                    return "used by an animation VRChat matches by name (" + first.where + ")";
                case Scope.ChildAnimator:
                    return "used by an Animator that is not a playable layer (" + first.where + ")";
                case Scope.NestedComponentPrefix:
                    return "two PhysBones are named so that one prefix is the other plus a PhysBone " +
                           "suffix (" + first.where + "), and the parameters they generate cannot be " +
                           "told apart";
                case Scope.UnreadableLayer:
                    return "this avatar has a playable layer whose parameters cannot be read (" +
                           first.where + "), and it could be driving any of them";
                default:
                    return "used by something this upload does not rewrite (" + first.where + ")";
            }
        }

        private static bool Rewritable(Scope scope, Options options)
        {
            switch (scope)
            {
                case Scope.Owned: return true;
                case Scope.Component: return true;
                case Scope.SeparateAsset: return options.copySeparateBlendTrees;
                case Scope.ExpressionList: return options.expressionParameters;
                case Scope.Menu: return options.expressionParameters;
                default: return false;
            }
        }

        // ------------------------------------------------------------------ the new names

        /// <summary>
        /// Original name to replacement, for every parameter the survey cleared.
        ///
        /// The replacement is derived from the name and the variant rather than handed out in order,
        /// so the same avatar renamed twice produces the same map. That matters because the copies
        /// are renamed before the build and the FX controller during it: the two passes never see
        /// the same list - another tool can add a parameter in between - and an ordinal scheme would
        /// hand the same parameter a different name in each.
        /// </summary>
        internal static Dictionary<string, string> BuildMap(IEnumerable<Finding> findings,
                                                            MeshProtectVariant variant,
                                                            IEnumerable<string> everyKnownName)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var taken = new HashSet<string>(everyKnownName ?? Enumerable.Empty<string>(),
                                            StringComparer.Ordinal);

            foreach (var finding in findings.Where(f => f.blockedBy == null)
                                            .OrderBy(f => f.name, StringComparer.Ordinal))
            {
                string name = Name(finding.name, variant.macSalt, taken);
                map[finding.name] = name;
                taken.Add(name);

                // A PhysBone is one decision but nine names: the component holds "Ear" and the
                // animator sees "Ear_IsGrabbed" and the rest. Those are written out here rather
                // than worked out later from the suffix, because the suffix cannot tell you which
                // of the two it is looking at. An avatar with a parameter called "Emote" and
                // another called "Emote_Hit" - not a PhysBone, just a name - would have had
                // "Emote_Hit" renamed on the strength of "Emote" being cleared, in every place
                // this build rewrites and nowhere else. The proof is only worth something if the
                // thing it cleared is the thing that gets renamed.
                if (!finding.physBonePrefix) continue;

                foreach (var suffix in PhysBoneSuffixes)
                    map[finding.name + suffix] = name + suffix;
            }

            return map;
        }

        private const string Consonants = "bcdfghjklmnprstvwz";
        private const string Vowels = "aeiou";

        private static string Name(string original, int salt, HashSet<string> taken)
        {
            for (int attempt = 0; attempt < 64; attempt++)
            {
                var rng = new System.Random(Seed(original, salt, attempt));
                var chars = new char[8];
                for (int i = 0; i < chars.Length; i++)
                    chars[i] = i % 2 == 0
                        ? Consonants[rng.Next(Consonants.Length)]
                        : Vowels[rng.Next(Vowels.Length)];

                string candidate = new string(chars);
                if (!taken.Contains(candidate)) return candidate;
            }

            // 18*5 alternating leaves far more room than any avatar needs, but a name has to come
            // back rather than an exception if the pool ever does run dry.
            for (int i = 0; ; i++)
                if (!taken.Contains("p" + i)) return "p" + i;
        }

        private static int Seed(string original, int salt, int attempt)
        {
            unchecked
            {
                int hash = 17 + salt * 31 + attempt * 7919;
                foreach (char c in original) hash = hash * 33 + c;
                return hash;
            }
        }

        /// <summary>
        /// How many parameters the map renames, as the author counts them.
        ///
        /// The map has an entry per NAME, and a PhysBone prefix contributes nine of them - the
        /// prefix plus the eight suffixed names the animator sees. Counting entries reports nine
        /// renamed parameters where the author has one.
        ///
        /// An expansion is recognised by its VALUE as well as its key, and it has to be: an avatar
        /// may have "Emote" and a separate parameter called "Emote_Hit", both cleared on their own
        /// merits, and going by the key alone would count them as one. An expansion's replacement
        /// is the prefix's replacement with the same suffix on the end, which nothing else can be -
        /// the generated names are eight letters with no underscore in them.
        /// </summary>
        internal static int DistinctParameters(IDictionary<string, string> map)
        {
            if (map == null) return 0;

            int count = 0;
            foreach (var pair in map)
            {
                bool expansion = false;
                foreach (var suffix in PhysBoneSuffixes)
                {
                    if (!pair.Key.EndsWith(suffix, StringComparison.Ordinal)) continue;

                    string prefix = pair.Key.Substring(0, pair.Key.Length - suffix.Length);
                    if (!map.TryGetValue(prefix, out var prefixValue)) continue;
                    if (pair.Value != prefixValue + suffix) continue;

                    expansion = true;
                    break;
                }
                if (!expansion) count++;
            }
            return count;
        }

        /// <summary>
        /// The replacement for a name, or the name itself.
        ///
        /// Deliberately nothing but a lookup. This used to strip PhysBone suffixes and try the
        /// prefix, which renames "Emote_Hit" because "Emote" was cleared - and those are only the
        /// same parameter when a PhysBone says so. BuildMap writes the suffixed names out when
        /// that is the case, so every name this returns was cleared under its own name.
        /// </summary>
        internal static string Remap(string name, IDictionary<string, string> map)
        {
            if (string.IsNullOrEmpty(name) || map == null) return name;
            return map.TryGetValue(name, out var renamed) ? renamed : name;
        }

        // ------------------------------------------------------------------ bringing trees inside

        /// <summary>
        /// Copy every blend tree the controller reaches from outside its own file INTO it.
        ///
        /// A controller may point at a blend tree saved as its own .asset, and copying the
        /// controller file does not bring it along - the copy goes on pointing at the artist's
        /// asset by GUID. That leaves the tree unwritable, and a parameter mentioned in it therefore
        /// unrenameable. On the avatar this was measured against, 320 trees are stored that way and
        /// they were the single largest blocker: 43 parameters, more than the expression list.
        ///
        /// Copying rather than writing is what keeps the artist's project intact - the same trade
        /// the clip path already makes. Run this BEFORE the name obfuscator so the copies are
        /// renamed with everything else; they arrive carrying names like "standing_11M".
        ///
        /// Returns how many trees were copied.
        /// </summary>
        internal static int CopySeparateBlendTrees(AnimatorController controller)
        {
            if (controller == null) return 0;

            // AddObjectToAsset needs a file to add to. A controller an upstream tool built in memory
            // has none, and VRCFury and Modular Avatar both hand those over.
            if (!AssetDatabase.Contains(controller)) return 0;

            string controllerPath = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(controllerPath)) return 0;

            var copies = new Dictionary<BlendTree, BlendTree>();
            var machines = new HashSet<AnimatorStateMachine>();
            int copied = 0;

            foreach (var layer in controller.layers)
                CopyTreesIn(layer.stateMachine, controller, controllerPath, copies, machines, ref copied);

            if (copied > 0) EditorUtility.SetDirty(controller);
            return copied;
        }

        private static void CopyTreesIn(AnimatorStateMachine machine, AnimatorController owner,
                                        string ownerPath, Dictionary<BlendTree, BlendTree> copies,
                                        HashSet<AnimatorStateMachine> seen, ref int copied)
        {
            if (machine == null || !seen.Add(machine)) return;

            foreach (var child in machine.states)
            {
                var state = child.state;
                if (state == null || !Belongs(state, ownerPath)) continue;

                var replacement = CopyTree(state.motion, owner, ownerPath, copies, ref copied);
                if (replacement != null) state.motion = replacement;
            }

            foreach (var child in machine.stateMachines)
                CopyTreesIn(child.stateMachine, owner, ownerPath, copies, seen, ref copied);
        }

        /// <summary>
        /// The copy of this motion that lives inside the controller, or null if it already does.
        ///
        /// Recursive because a tree outside the file usually points at more trees outside the file -
        /// the avatar measured had a Main_pose.asset holding one and pose0..pose31 holding the rest.
        /// </summary>
        private static Motion CopyTree(Motion motion, AnimatorController owner, string ownerPath,
                                       Dictionary<BlendTree, BlendTree> copies, ref int copied)
        {
            if (!(motion is BlendTree tree)) return null;

            if (Belongs(tree, ownerPath))
            {
                // Already inside, but its children may not be.
                var children = tree.children;
                bool changed = false;
                for (int i = 0; i < children.Length; i++)
                {
                    var replacement = CopyTree(children[i].motion, owner, ownerPath, copies, ref copied);
                    if (replacement == null) continue;
                    children[i].motion = replacement;
                    changed = true;
                }
                if (changed) tree.children = children;
                return null;
            }

            if (copies.TryGetValue(tree, out var existing)) return existing;

            // CopySerialized rather than Instantiate.
            //
            // Object.Instantiate on a blend tree makes Unity assert
            // '(metaFlags & kStrongPPtrMask) == 0' - its child list is declared as owning
            // references, and instantiating an object that owns others is outside what that API
            // promises. Copying one avatar's controllers produced 1489 of those in the console. The
            // files it wrote were structurally correct, checked object by object against the
            // original, so this is not a repair. It is that an author who sees a console full of
            // red assumes the tool broke their avatar, and that building on a call Unity asserts
            // against is how this project ended up with Instantiate(AnimationClip) quietly
            // emptying genericBindings.
            //
            // CopySerialized copies the serialised fields and leaves the child references pointing
            // where they pointed, which is exactly what is wanted here - the children are repointed
            // below, one at a time, at copies this code owns.
            var copy = new BlendTree();
            EditorUtility.CopySerialized(tree, copy);
            copy.name = tree.name;
            copy.hideFlags = HideFlags.HideInHierarchy;   // how Unity stores a controller's own trees
            AssetDatabase.AddObjectToAsset(copy, owner);
            copies[tree] = copy;
            copied++;

            // Instantiate copies the child list, but the children still point at the originals.
            var copiedChildren = copy.children;
            for (int i = 0; i < copiedChildren.Length; i++)
            {
                var replacement = CopyTree(copiedChildren[i].motion, owner, ownerPath, copies, ref copied);
                if (replacement != null) copiedChildren[i].motion = replacement;
            }
            copy.children = copiedChildren;

            return copy;
        }

        /// <summary>Is this object stored in the controller's own file, or nowhere on disk at all?</summary>
        private static bool Belongs(UnityEngine.Object o, string ownerPath)
        {
            if (o == null) return false;
            string path = AssetDatabase.GetAssetPath(o);
            return string.IsNullOrEmpty(path) || path == ownerPath;
        }

        // ------------------------------------------------------------------ rewriting

        /// <summary>
        /// Rename parameters throughout one controller.
        ///
        /// Every write is guarded by the same ownership test the name obfuscator uses, checked at
        /// the point of mutation rather than inferred once from the container: a controller copy can
        /// still reach objects in the artist's project, and writing to one of those is damage rather
        /// than a missed rename.
        /// </summary>
        internal static int RewriteController(AnimatorController controller, IDictionary<string, string> map)
        {
            if (controller == null || map == null || map.Count == 0) return 0;

            string path = AssetDatabase.GetAssetPath(controller);
            int rewrites = 0;

            var parameters = controller.parameters;
            bool touched = false;
            for (int i = 0; i < parameters.Length; i++)
            {
                string renamed = Remap(parameters[i].name, map);
                if (renamed == parameters[i].name) continue;
                parameters[i].name = renamed;
                touched = true;
                rewrites++;
            }
            if (touched) controller.parameters = parameters;

            var machines = new HashSet<AnimatorStateMachine>();
            var motions = new HashSet<Motion>();

            foreach (var layer in controller.layers)
                RewriteStateMachine(layer.stateMachine, path, map, machines, motions, ref rewrites);

            if (rewrites > 0) EditorUtility.SetDirty(controller);
            return rewrites;
        }

        private static void RewriteStateMachine(AnimatorStateMachine machine, string ownerPath,
                                                IDictionary<string, string> map,
                                                HashSet<AnimatorStateMachine> machines,
                                                HashSet<Motion> motions, ref int rewrites)
        {
            if (machine == null || !machines.Add(machine)) return;

            foreach (var t in machine.anyStateTransitions) RewriteTransition(t, ownerPath, map, ref rewrites);
            foreach (var t in machine.entryTransitions) RewriteTransition(t, ownerPath, map, ref rewrites);

            if (Belongs(machine, ownerPath))
                foreach (var b in machine.behaviours) RewriteBehaviour(b, ownerPath, map, ref rewrites);

            foreach (var child in machine.states)
            {
                var state = child.state;
                if (state == null) continue;

                if (Belongs(state, ownerPath))
                {
                    string mirror = Remap(state.mirrorParameter, map);
                    if (mirror != state.mirrorParameter) { state.mirrorParameter = mirror; rewrites++; }

                    string speed = Remap(state.speedParameter, map);
                    if (speed != state.speedParameter) { state.speedParameter = speed; rewrites++; }

                    string cycle = Remap(state.cycleOffsetParameter, map);
                    if (cycle != state.cycleOffsetParameter)
                    {
                        state.cycleOffsetParameter = cycle;
                        rewrites++;
                    }

                    string time = Remap(state.timeParameter, map);
                    if (time != state.timeParameter) { state.timeParameter = time; rewrites++; }

                    foreach (var b in state.behaviours) RewriteBehaviour(b, ownerPath, map, ref rewrites);
                    RewriteMotion(state.motion, ownerPath, map, motions, ref rewrites);
                }

                foreach (var t in state.transitions) RewriteTransition(t, ownerPath, map, ref rewrites);
            }

            foreach (var child in machine.stateMachines)
            {
                foreach (var t in machine.GetStateMachineTransitions(child.stateMachine))
                    RewriteTransition(t, ownerPath, map, ref rewrites);

                RewriteStateMachine(child.stateMachine, ownerPath, map, machines, motions, ref rewrites);
            }
        }

        private static void RewriteTransition(AnimatorTransitionBase transition, string ownerPath,
                                              IDictionary<string, string> map, ref int rewrites)
        {
            if (transition == null || !Belongs(transition, ownerPath)) return;

            var conditions = transition.conditions;
            if (conditions == null) return;

            bool touched = false;
            for (int i = 0; i < conditions.Length; i++)
            {
                string renamed = Remap(conditions[i].parameter, map);
                if (renamed == conditions[i].parameter) continue;
                conditions[i].parameter = renamed;
                touched = true;
                rewrites++;
            }
            if (touched) transition.conditions = conditions;
        }

        private static void RewriteMotion(Motion motion, string ownerPath, IDictionary<string, string> map,
                                          HashSet<Motion> seen, ref int rewrites)
        {
            if (motion == null || !seen.Add(motion)) return;
            if (!Belongs(motion, ownerPath)) return;

            if (motion is BlendTree tree)
            {
                string blend = Remap(tree.blendParameter, map);
                if (blend != tree.blendParameter) { tree.blendParameter = blend; rewrites++; }

                string blendY = Remap(tree.blendParameterY, map);
                if (blendY != tree.blendParameterY) { tree.blendParameterY = blendY; rewrites++; }

                var children = tree.children;
                bool touched = false;
                for (int i = 0; i < children.Length; i++)
                {
                    string renamed = Remap(children[i].directBlendParameter, map);
                    if (renamed != children[i].directBlendParameter)
                    {
                        children[i].directBlendParameter = renamed;
                        touched = true;
                        rewrites++;
                    }
                    RewriteMotion(children[i].motion, ownerPath, map, seen, ref rewrites);
                }
                if (touched) tree.children = children;
                return;
            }

            if (motion is AnimationClip clip)
            {
                // Only a clip stored inside this controller, which means one the obfuscator cloned
                // there. The artist's own .anim files are never written to.
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(Animator) || !string.IsNullOrEmpty(binding.path)) continue;

                    string renamed = Remap(binding.propertyName, map);
                    if (renamed == binding.propertyName) continue;

                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    AnimationUtility.SetEditorCurve(clip, binding, null);
                    AnimationUtility.SetEditorCurve(clip, new EditorCurveBinding
                    {
                        path = "",
                        type = typeof(Animator),
                        propertyName = renamed
                    }, curve);
                    rewrites++;
                }
            }
        }

        private static void RewriteBehaviour(StateMachineBehaviour behaviour, string ownerPath,
                                             IDictionary<string, string> map,
                                             ref int rewrites)
        {
            if (!Belongs(behaviour, ownerPath)) return;
            var type = behaviour.GetType();

            if (type.Name.IndexOf("AvatarParameterDriver", StringComparison.Ordinal) >= 0)
            {
                var list = type.GetField("parameters", BindingFlags.Public | BindingFlags.Instance)
                               ?.GetValue(behaviour) as IList;
                if (list == null) return;

                for (int i = 0; i < list.Count; i++)
                {
                    var entry = list[i];
                    if (entry == null) continue;

                    bool touched = false;
                    foreach (var name in DriverFields)
                    {
                        var field = entry.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                        if (field == null || field.FieldType != typeof(string)) continue;

                        string value = (string)field.GetValue(entry);
                        string renamed = Remap(value, map);
                        if (renamed == value) continue;

                        field.SetValue(entry, renamed);
                        touched = true;
                        rewrites++;
                    }

                    // The driver's entries are a class in every SDK this has been built against, so
                    // writing through the reference is enough - but a value type would have been
                    // modified on a copy and silently lost, so it is written back either way.
                    if (touched && entry.GetType().IsValueType) list[i] = entry;
                }
                return;
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType != typeof(string)) continue;
                if (field.Name.IndexOf("arameter", StringComparison.Ordinal) < 0) continue;

                string value = (string)field.GetValue(behaviour);
                string renamed = Remap(value, map);
                if (renamed == value) continue;

                field.SetValue(behaviour, renamed);
                rewrites++;
            }
        }

        /// <summary>
        /// Rename in the expression parameter list.
        ///
        /// The order of the list is left exactly as it was. VRChat packs synced parameters by their
        /// position in this list rather than by name, so reordering it would change what every
        /// remote client decodes; renaming an entry in place does not.
        /// </summary>
        internal static int RewriteExpressionParameters(VRCExpressionParameters asset,
                                                        IDictionary<string, string> map)
        {
            if (asset == null || asset.parameters == null || map == null || map.Count == 0) return 0;

            // Replaced rather than written to. Whoever built this asset may have filled it with the
            // author's own Parameter objects rather than copies of them - the build's own clone of
            // the list did exactly that until it was fixed - and writing a name into one of those
            // edits the artist's project from inside a build, invisibly, until something saves.
            int rewrites = 0;
            var replaced = new VRCExpressionParameters.Parameter[asset.parameters.Length];

            for (int i = 0; i < replaced.Length; i++)
            {
                var parameter = asset.parameters[i];
                if (parameter == null) continue;

                string renamed = Remap(parameter.name, map);
                if (renamed != parameter.name) rewrites++;

                replaced[i] = new VRCExpressionParameters.Parameter
                {
                    name = renamed,
                    valueType = parameter.valueType,
                    saved = parameter.saved,
                    networkSynced = parameter.networkSynced,
                    defaultValue = parameter.defaultValue,
                };
            }

            if (rewrites > 0)
            {
                asset.parameters = replaced;
                EditorUtility.SetDirty(asset);
            }
            return rewrites;
        }

        /// <summary>
        /// Rename through the menu tree, copying every menu that is not already this build's.
        ///
        /// The menus have to be copied rather than written to, and the reason is sharper than it
        /// looks. The build already takes a "copy" of the root menu, but it copies the LIST and
        /// leaves the Control objects themselves shared with the artist's asset - so writing a
        /// parameter name into one of those controls would edit their project, silently, from
        /// inside a build. Every control here is rebuilt instead, and every submenu that lives
        /// outside this build's folder is copied into it.
        ///
        /// Returns the menu to use as the root, which is the one handed in unless that one belonged
        /// to the artist, and how many names were rewritten.
        /// </summary>
        internal static VRCExpressionsMenu RewriteMenuTree(VRCExpressionsMenu root, string folder,
                                                           MeshProtectVariant variant,
                                                           IDictionary<string, string> map,
                                                           out int rewrites)
        {
            rewrites = 0;
            if (root == null || map == null || map.Count == 0) return root;

            int index = 0;
            var copies = new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>();

            // The root gets the same ownership question as every other menu, and it used to be the
            // one place that did not: Rewrite was called on whatever arrived and rebuilt its control
            // list. Today the build always hands over a menu it created itself, so nothing was
            // wrong - but that is a promise from the caller, and the identical promise is what
            // failed when CloneOrCreateParameters was trusted to hand over a private copy and handed
            // over the artist's Parameter objects instead. Asking here costs one branch.
            // Memoised against the menu that was HANDED IN, not against what came back. A tree is
            // allowed to point back at an ancestor, and the ancestor a descendant points at is the
            // original - so keying this by the copy would leave the original unrecognised, build a
            // second copy of the whole root, and hand the wearer a "back" entry leading into a
            // parallel duplicate of the menu they came from.
            var handedIn = root;
            root = Own(root, folder, variant, ref index);
            copies[handedIn] = root;
            copies[root] = root;

            Rewrite(root, root, folder, variant, map, copies, ref index, ref rewrites);
            return root;
        }

        /// <summary>
        /// The menu to write into: the one handed in if this build owns it, otherwise a copy of it
        /// that this build does own.
        /// </summary>
        private static VRCExpressionsMenu Own(VRCExpressionsMenu menu, string folder,
                                              MeshProtectVariant variant, ref int index)
        {
            string path = AssetDatabase.GetAssetPath(menu);
            if (string.IsNullOrEmpty(path) || path.StartsWith(folder + "/", StringComparison.Ordinal))
                return menu;

            var copy = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            copy.name = variant.menuAssetName + "_" + index++;
            copy.controls = new List<VRCExpressionsMenu.Control>(
                menu.controls ?? new List<VRCExpressionsMenu.Control>());

            AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath(
                $"{folder}/{copy.name}.asset"));
            return copy;
        }

        private static void Rewrite(VRCExpressionsMenu menu, VRCExpressionsMenu file, string folder,
                                    MeshProtectVariant variant, IDictionary<string, string> map,
                                    Dictionary<VRCExpressionsMenu, VRCExpressionsMenu> copies,
                                    ref int index, ref int rewrites)
        {
            if (menu == null || menu.controls == null) return;

            var rebuilt = new List<VRCExpressionsMenu.Control>(menu.controls.Count);

            foreach (var control in menu.controls)
            {
                if (control == null) { rebuilt.Add(null); continue; }

                VRCExpressionsMenu.Control.Parameter[] subParameters = null;
                if (control.subParameters != null)
                {
                    subParameters = new VRCExpressionsMenu.Control.Parameter[control.subParameters.Length];
                    for (int i = 0; i < subParameters.Length; i++)
                        subParameters[i] = Copy(control.subParameters[i], map, ref rewrites);
                }

                var copy = new VRCExpressionsMenu.Control
                {
                    name = control.name,
                    icon = control.icon,
                    type = control.type,
                    value = control.value,
                    style = control.style,

                    // Copied, not shared. Nothing writes through labels today, so sharing the array
                    // would be harmless right up until something did - which is exactly the shape
                    // of the bug this branch already shipped, where the expression parameter list
                    // was duplicated with ToArray() and every entry in it stayed the artist's.
                    labels = control.labels?.ToArray(),
                    parameter = Copy(control.parameter, map, ref rewrites),
                    subParameters = subParameters,
                    subMenu = Submenu(control.subMenu, file, folder, variant, map, copies, ref index,
                                      ref rewrites),
                };

                rebuilt.Add(copy);
            }

            menu.controls = rebuilt;
            EditorUtility.SetDirty(menu);
        }

        private static VRCExpressionsMenu.Control.Parameter Copy(
            VRCExpressionsMenu.Control.Parameter parameter, IDictionary<string, string> map,
            ref int rewrites)
        {
            if (parameter == null) return null;

            string renamed = Remap(parameter.name, map);
            if (renamed != parameter.name) rewrites++;
            return new VRCExpressionsMenu.Control.Parameter { name = renamed };
        }

        private static VRCExpressionsMenu Submenu(VRCExpressionsMenu submenu, VRCExpressionsMenu file,
                                                  string folder, MeshProtectVariant variant,
                                                  IDictionary<string, string> map,
                                                  Dictionary<VRCExpressionsMenu, VRCExpressionsMenu> copies,
                                                  ref int index, ref int rewrites)
        {
            if (submenu == null) return null;
            if (copies.TryGetValue(submenu, out var already)) return already;

            string path = AssetDatabase.GetAssetPath(submenu);
            bool ours = string.IsNullOrEmpty(path) || path.StartsWith(folder + "/", StringComparison.Ordinal);

            if (ours)
            {
                // Already this build's - the unlock menus, or a menu an upstream tool left in
                // memory. Its controls are still rebuilt rather than written through.
                copies[submenu] = submenu;
                Rewrite(submenu, file, folder, variant, map, copies, ref index, ref rewrites);
                return submenu;
            }

            var copy = ScriptableObject.CreateInstance<VRCExpressionsMenu>();

            // Menu asset names travel inside the uploaded avatar, so they come from this avatar's
            // generated family rather than from the artist's folder structure.
            copy.name = variant.menuAssetName + "_" + index++;
            copy.controls = new List<VRCExpressionsMenu.Control>(submenu.controls ??
                                                                 new List<VRCExpressionsMenu.Control>());

            // A sub-asset of the root when there is a root file to put it in, its own asset when
            // there is not. It used to be a sub-asset or nothing, and nothing meant handing the
            // uploaded avatar a menu that lives only in memory - which the build hook's own comment
            // says does not survive bundle serialisation reliably. The wearer would open that
            // submenu, find it empty, and the console would have said nothing at all.
            if (AssetDatabase.Contains(file))
                AssetDatabase.AddObjectToAsset(copy, file);
            else
                AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath(
                    $"{folder}/{copy.name}.asset"));

            copies[submenu] = copy;

            Rewrite(copy, file, folder, variant, map, copies, ref index, ref rewrites);
            return copy;
        }

        /// <summary>
        /// Rename on the avatar's own components: PhysBone prefixes and contact receivers.
        ///
        /// Only ever called on the build clone, so the scene keeps its own names. Contact TAGS are
        /// deliberately untouched - a tag is how one avatar's sender finds another avatar's
        /// receiver, so it is the one string here that really is addressed from outside.
        /// </summary>
        internal static int RewriteComponents(GameObject avatar, IDictionary<string, string> map)
        {
            if (avatar == null || map == null || map.Count == 0) return 0;

            int rewrites = 0;
            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                string typeName = component.GetType().Name;

                if (typeName == "VRCPhysBone" || typeName == "VRCRaycast")
                {
                    rewrites += RewriteStringField(component, "parameter", map);
                    rewrites += RewriteStringField(component, "Parameter", map);
                }
                else if (typeName == "VRCContactReceiver")
                {
                    rewrites += RewriteStringField(component, "parameter", map);
                }
            }
            return rewrites;
        }

        private static int RewriteStringField(Component component, string field,
                                              IDictionary<string, string> map)
        {
            var f = component.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f == null || f.FieldType != typeof(string)) return 0;

            string value = (string)f.GetValue(component);
            string renamed = Remap(value, map);
            if (renamed == value) return 0;

            f.SetValue(component, renamed);
            EditorUtility.SetDirty(component);
            return 1;
        }
    }
}
#endif
