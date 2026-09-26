#if UNITY_EDITOR && LILMP_VRCSDK3_AVATARS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace MeshProtect
{
    /// <summary>
    /// Decides which of the avatar's object names may be replaced.
    ///
    /// WHY THE NAME AND NOT THE OBJECT. An animation addresses an object by its path -
    /// "Armature/Hips/Spine/Chest/Hair" - so renaming one object invalidates that path and every
    /// path of everything beneath it. Handled object by object, one rename cascades through the
    /// hierarchy and the bookkeeping is the whole problem.
    ///
    /// Kanna Protecc found the way out and it is worth stating plainly: rename by NAME, globally.
    /// Two objects called "Hair" both become the same new name, and rewriting a path is then a
    /// token-wise lookup - split on '/', replace each segment that has a replacement, join. The
    /// hierarchy never enters into it. The unit of decision becomes the name, which is exactly the
    /// shape the parameter pass already works in.
    ///
    /// WHAT ADDRESSES AN OBJECT BY NAME. Less than one might fear. Contacts, PhysBones, constraints,
    /// the descriptor's viseme and eye-look references and every other component hold direct object
    /// references, which survive a rename untouched. What is left is:
    ///
    ///   - every animation curve's path, of both kinds (float and object reference)
    ///   - every avatar mask on a layer, which is a list of transform paths
    ///   - VRCAnimatorPlayAudio.SourcePath, which is NDMF's entire list of path-bearing STATE
    ///     BEHAVIOUR fields
    ///
    /// - and the names the client itself knows, which is where this is stricter than Kanna. Kanna
    ///   excludes what the author lists and the humanoid bones; it does not exclude "Body", and MMD
    ///   dance worlds address the face mesh at exactly that path. This project has already paid
    ///   once for an MMD regression it did not see coming.
    ///
    /// THE MASK WAS MISSED ON THE FIRST PASS, and how is worth keeping. The list above used to end
    /// at the behaviour field, with a comment calling that list complete - it is, for state
    /// behaviours, which is the question NDMF's function answers. The function that CALLS it
    /// rewrites the layer's avatar mask one line earlier, in the same loop. A borrowed list is only
    /// complete for the question it was written to answer, and the wider answer was one line away.
    /// </summary>
    internal static class MeshProtectObjectNames
    {
        /// <summary>Where a name is used, in terms of who is able to rewrite it.</summary>
        internal enum Scope
        {
            /// <summary>A curve path or SourcePath inside something this upload rewrites.</summary>
            Owned,

            /// <summary>A clip this upload never rewrites: VRChat's own animations, and anything
            /// reached through a controller left as the avatar had it.</summary>
            Foreign,

        }

        internal sealed class Site
        {
            public Scope scope;
            public string where;
        }

        internal sealed class Finding
        {
            public string name;
            public int objects;
            public readonly List<Site> sites = new List<Site>();

            /// <summary>Null when the name may be replaced. Otherwise why not.</summary>
            public string blockedBy;
        }

        internal sealed class Options
        {
            /// <summary>External trees are traversed by the rewrite only after being copied.</summary>
            public bool copySeparateBlendTrees = true;

            /// <summary>Controllers whose clips this upload rewrites.</summary>
            public readonly HashSet<AnimatorController> rewritable = new HashSet<AnimatorController>();

            /// <summary>Controllers to leave out entirely, because a copy already rewritten ships in
            /// their place. Same meaning as the parameter pass's.</summary>
            public readonly HashSet<AnimatorController> replaced = new HashSet<AnimatorController>();
        }

        /// <summary>
        /// The one name the VRChat client itself goes looking for.
        ///
        /// MMD dance worlds drive the avatar's face by playing animations whose curves are bound to
        /// the path "Body" - the convention every MMD-converted avatar follows. Rename that object
        /// and the wearer's face stops moving in those worlds, which is a symptom nobody traces back
        /// to an obfuscator, and one this project has already shipped once by a different route.
        /// </summary>
        private static readonly HashSet<string> ClientKnown = new HashSet<string>(StringComparer.Ordinal)
        {
            "Body",
        };

        // ------------------------------------------------------------------ survey

        internal static List<Finding> Survey(VRCAvatarDescriptor descriptor, Options options)
        {
            var found = new Dictionary<string, Finding>(StringComparer.Ordinal);

            Finding Get(string name)
            {
                if (!found.TryGetValue(name, out var finding))
                {
                    finding = new Finding { name = name };
                    found[name] = finding;
                }
                return finding;
            }

            void Add(string name, Scope scope, string where)
            {
                if (string.IsNullOrEmpty(name)) return;
                Get(name).sites.Add(new Site { scope = scope, where = where });
            }

            var root = descriptor.transform;

            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform == root) continue;
                Get(transform.name).objects++;
            }

            var protectedNames = SkeletonAndRoot(descriptor);

            foreach (var layers in new[] { descriptor.baseAnimationLayers, descriptor.specialAnimationLayers })
            {
                if (layers == null) continue;
                foreach (var layer in layers)
                {
                    if (layer.isDefault || layer.animatorController == null) continue;

                    var runtime = layer.animatorController;
                    var controller = runtime as AnimatorController;
                    if (controller == null)
                    {
                        // An override is not adopted with its base controller. Its replacement
                        // clips can address objects absent from the base clips, so inspect the
                        // effective clips even when another layer adopts that same base.
                        foreach (var clip in runtime.animationClips)
                            SurveyClipPaths(clip, Scope.Foreign, layer.type + " override", Add);

                        var seen = new HashSet<RuntimeAnimatorController>();
                        while (runtime != null && seen.Add(runtime))
                        {
                            if (runtime is AnimatorController underlying)
                                SurveyController(underlying, layer.type + " (overridden)", false, false, Add);
                            runtime = (runtime as AnimatorOverrideController)?.runtimeAnimatorController;
                        }
                        continue;
                    }
                    if (options.replaced.Contains(controller)) continue;

                    // The same question the parameter engine asks, and for the same reason. This
                    // survey hands out Owned on the strength of the controller alone, while the
                    // rewrite refuses any state or state machine that lives outside the controller's
                    // own file - real avatars do keep them that way. Without this the two disagree:
                    // a name is cleared because the clip was going to be rewritten, the rewrite
                    // skips the state holding it, and the animation goes on addressing an object
                    // that has just been renamed. Nothing reports it.
                    bool ours = options.rewritable.Contains(controller) &&
                                !MeshProtectParameters.StructureLivesElsewhere(
                                    controller, AssetDatabase.GetAssetPath(controller));

                    SurveyController(controller, layer.type.ToString(), ours,
                                     options.copySeparateBlendTrees, Add);
                }
            }

            // Independent animators and legacy animations keep their own clips. Their paths are
            // relative to their component, but the name map is global, so every path segment they
            // use must retain its name too. Include inactive objects and override-controller clips.
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator.transform == root || animator.runtimeAnimatorController == null) continue;
                var runtime = animator.runtimeAnimatorController;
                foreach (var clip in runtime.animationClips)
                    SurveyClipPaths(clip, Scope.Foreign, "Animator on " + animator.name, Add);

                var seen = new HashSet<RuntimeAnimatorController>();
                while (runtime != null && seen.Add(runtime))
                {
                    if (runtime is AnimatorController controller)
                        SurveyController(controller, "Animator on " + animator.name, false, false, Add);
                    runtime = (runtime as AnimatorOverrideController)?.runtimeAnimatorController;
                }
            }
            foreach (var animation in root.GetComponentsInChildren<Animation>(true))
            {
                SurveyClipPaths(animation.clip, Scope.Foreign, "Animation on " + animation.name, Add);
                foreach (AnimationState state in animation)
                    SurveyClipPaths(state.clip, Scope.Foreign, "Animation on " + animation.name, Add);
            }

            var findings = found.Values.ToList();
            foreach (var finding in findings)
                finding.blockedBy = Judge(finding, protectedNames);

            return findings.OrderBy(f => f.name, StringComparer.Ordinal).ToList();
        }

        private static void SurveyController(AnimatorController controller, string label, bool ours,
                                             bool copySeparateBlendTrees,
                                             Action<string, Scope, string> add)
        {
            string ownerPath = AssetDatabase.GetAssetPath(controller);
            var machines = new HashSet<AnimatorStateMachine>();
            var motions = new HashSet<(Motion motion, bool ours)>();

            foreach (var layer in controller.layers)
            {
                SurveyMask(layer.avatarMask, label + "/" + layer.name, add);

                SurveyStateMachine(layer.stateMachine, label + "/" + layer.name, ours, ownerPath,
                                   copySeparateBlendTrees, machines, motions, add);

                // The rewrite walks states, not the synchronized layer's override tables.
                foreach (var state in MeshProtectParameters.SyncedLayerStates(controller, layer))
                {
                    string where = label + "/" + layer.name + " override " + state.name;
                    SurveyMotion(layer.GetOverrideMotion(state), where, false, ownerPath, false,
                                 motions, add);
                    foreach (var behaviour in layer.GetOverrideBehaviours(state) ?? new StateMachineBehaviour[0])
                        SurveyBehaviour(behaviour, where, false, ownerPath, add);
                }
            }
        }

        /// <summary>
        /// A layer's avatar mask, which is a list of transform PATHS and therefore addresses objects
        /// by name exactly as a curve does.
        ///
        /// THIS WAS MISSED, and the way it was missed is worth writing down. The comment at the top
        /// of this file took NDMF's RemapPathsInStateBehaviour as the complete list of what holds an
        /// object path. It is complete - for state behaviours. The function that CALLS it,
        /// AnimationIndex.RewritePaths, rewrites the layer's avatar mask one line earlier in the same
        /// loop. A list was read as an answer to a wider question than it was answering, and the
        /// conclusion went into a source comment as fact.
        ///
        /// Every name in one is off limits, and the reason is simpler than ownership: nothing in
        /// this project writes a mask. Not the copy pass, not the build. Ownership was the reason
        /// the first version gave, and reasoning from it produced an exception that was wrong in
        /// every case it covered - see the note in the body.
        ///
        /// What that costs is worth stating as a measurement rather than an estimate, since the
        /// estimate that came with this fix was wrong. On the avatar it was measured against the
        /// masks add 41 names to the survey and block none that were not already blocked: 631 of
        /// 744 renameable before, 631 of 785 after. Their paths are bone chains, so every name in
        /// them is either part of the skeleton or belongs to no object on this avatar at all. A
        /// mask that points at an ordinary object - a hand-held prop, a toggled outfit piece - is
        /// the case this rule is actually for, and no avatar here has one.
        ///
        /// The failure it prevents depends on which kind of mask it is. An exclusion mask - the
        /// weights are zero, as in the test avatar's hand mask - fails mildly: the exclusion stops
        /// applying. An inclusion mask fails the other way, and that one is a silently dead toggle:
        /// the layer simply stops animating the object whose path no longer matches.
        /// </summary>
        private static void SurveyMask(AvatarMask mask, string where,
                                       Action<string, Scope, string> add)
        {
            if (mask == null) return;

            // Foreign whatever file it is in, because Foreign here means "nothing rewrites this",
            // and nothing does: there is no code in this project that writes a mask path. The first
            // version of this function asked whether the mask was a separate asset and called it
            // Owned when it was not - which let three shapes straight back into the bug this
            // function exists to close. A mask stored as a sub-asset of the controller is copied
            // along with it by CopyOutOfItsFile, so the copy's mask sits in the copy's own file and
            // passed that test; so did one another tool had built in memory. Both would have been
            // cleared for renaming with nothing to rewrite them, in a single ordinary Prepare, no
            // Undo required. Being allowed to write the controller says nothing about being allowed
            // to write what the controller points at - and here we are not even asking that much,
            // because a mask is not written at all.
            for (int i = 0; i < mask.transformCount; i++)
                AddPath(mask.GetTransformPath(i), Scope.Foreign,
                        where + " mask '" + mask.name + "'", add);
        }

        private static void SurveyStateMachine(AnimatorStateMachine machine, string where, bool ours,
                                               string ownerPath, bool copySeparateBlendTrees,
                                               HashSet<AnimatorStateMachine> machines,
                                               HashSet<(Motion motion, bool ours)> motions,
                                               Action<string, Scope, string> add)
        {
            if (machine == null || !machines.Add(machine)) return;

            foreach (var behaviour in machine.behaviours)
                SurveyBehaviour(behaviour, where, ours, ownerPath, add);

            foreach (var child in machine.states)
            {
                var state = child.state;
                if (state == null) continue;

                string stateWhere = where + "/" + state.name;
                foreach (var behaviour in state.behaviours)
                    SurveyBehaviour(behaviour, stateWhere, ours, ownerPath, add);
                SurveyMotion(state.motion, stateWhere, ours, ownerPath, copySeparateBlendTrees, motions, add);
            }

            foreach (var child in machine.stateMachines)
                SurveyStateMachine(child.stateMachine, where + "/" + SafeName(child.stateMachine), ours,
                                   ownerPath, copySeparateBlendTrees, machines, motions, add);
        }

        private static string SafeName(AnimatorStateMachine machine) =>
            machine == null ? "(null)" : machine.name;

        private static void SurveyMotion(Motion motion, string where, bool ours, string ownerPath,
                                         bool copySeparateBlendTrees,
                                         HashSet<(Motion motion, bool ours)> seen,
                                         Action<string, Scope, string> add)
        {
            if (motion == null) return;

            // An uncopied external tree stops the rewrite before any of its descendants. Survey
            // shared clips in both contexts so an owned occurrence cannot hide an untouched one.
            if (motion is BlendTree && !copySeparateBlendTrees && !Belongs(motion, ownerPath, null))
                ours = false;
            if (!seen.Add((motion, ours))) return;

            if (motion is BlendTree tree)
            {
                foreach (var child in tree.children)
                    SurveyMotion(child.motion, where + "/tree " + tree.name, ours, ownerPath,
                                 copySeparateBlendTrees, seen, add);
                return;
            }

            if (!(motion is AnimationClip clip)) return;

            // The clip is rewritable when the controller reaching it is - the obfuscator clones the
            // project's clips into a controller this build owns - except the ones it never clones,
            // which the client matches by name.
            var scope = ours && !MeshProtectObfuscator.IsClientOwned(clip) ? Scope.Owned : Scope.Foreign;

            // Both kinds. An object reference curve - a material swap, a mesh swap - carries a path
            // exactly like a float curve does, and rewriting one and not the other leaves half the
            // clip pointing at a name that no longer exists.
            SurveyClipPaths(clip, scope, where + "/clip " + clip.name, add);
        }

        private static void SurveyClipPaths(AnimationClip clip, Scope scope, string where,
                                            Action<string, Scope, string> add)
        {
            if (clip == null) return;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip)
                                                    .Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                AddPath(binding.path, scope, where, add);
        }

        /// <summary>
        /// VRChat's one path-bearing state behaviour. Taken from NDMF's platform bindings, where
        /// RemapPathsInStateBehaviour handles exactly this field and nothing else.
        /// </summary>
        private static void SurveyBehaviour(StateMachineBehaviour behaviour, string where, bool ours,
                                            string ownerPath,
                                            Action<string, Scope, string> add)
        {
            if (behaviour == null) return;
            if (behaviour.GetType().Name.IndexOf("PlayAudio", StringComparison.Ordinal) < 0) return;

            var field = behaviour.GetType().GetField("SourcePath",
                                                     BindingFlags.Public | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(string)) return;

            var scope = ours && Belongs(behaviour, ownerPath, null) ? Scope.Owned : Scope.Foreign;
            AddPath((string)field.GetValue(behaviour), scope,
                    where + " PlayAudio", add);
        }

        private static void AddPath(string path, Scope scope, string where,
                                    Action<string, Scope, string> add)
        {
            if (string.IsNullOrEmpty(path)) return;
            foreach (var segment in path.Split('/')) add(segment, scope, where);
        }

        /// <summary>
        /// The humanoid bones, everything above them, and the avatar root.
        ///
        /// Kanna excludes these and it is worth keeping. Nothing in the avatar addresses a bone by
        /// name - the rig is what the client and every animation use - but the armature is the part
        /// of an avatar that other people's props, outfits and tools reach into, and it is the one
        /// place where a name is a contract with somebody else's asset rather than with this file.
        /// </summary>
        private static HashSet<string> SkeletonAndRoot(VRCAvatarDescriptor descriptor)
        {
            var names = new HashSet<string>(StringComparer.Ordinal) { descriptor.name };

            var animator = descriptor.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) return names;

            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;

                var transform = animator.GetBoneTransform(bone);
                while (transform != null && transform != descriptor.transform)
                {
                    names.Add(transform.name);
                    transform = transform.parent;
                }
            }

            return names;
        }

        // ------------------------------------------------------------------ the verdict

        private static string Judge(Finding finding, HashSet<string> protectedNames)
        {
            if (ClientKnown.Contains(finding.name))
                return "VRChat looks for this one by name - MMD worlds drive the face through it";

            if (protectedNames.Contains(finding.name))
                return "part of the skeleton, or the avatar itself";

            // A path is split on '/', so a name containing one cannot be put back together
            // unambiguously. Refusing is cheaper than deciding which reading was meant.
            if (finding.name.IndexOf('/') >= 0)
                return "contains a slash, which is what paths are split on";

            // "or an avatar mask", because that is where a good many of these come from and the
            // report is read by somebody trying to find the thing that named it. On the avatar this
            // was measured against, all 41 names the masks contributed land in the line below.
            if (finding.objects == 0)
                return "named by an animation or an avatar mask, but no object under this avatar has it";

            var blocked = finding.sites.Where(s => s.scope == Scope.Foreign).ToList();
            if (blocked.Count > 0)
                return "used by an animation or an avatar mask this upload does not rewrite (" +
                       blocked[0].where + ")";

            return null;
        }

        // ------------------------------------------------------------------ the new names

        /// <summary>
        /// Original name to replacement, for every name the survey cleared.
        ///
        /// Derived from the name and the variant, like the parameter map and for the same reason:
        /// the prepared copies have their clip paths rewritten before the build and the FX
        /// controller during it, and the two passes have to arrive at the same answer without
        /// having seen the same avatar.
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
            }

            return map;
        }

        private const string Consonants = "bcdfghjklmnprstvwz";
        private const string Vowels = "aeiou";

        private static string Name(string original, int salt, HashSet<string> taken)
        {
            for (int attempt = 0; attempt < 64; attempt++)
            {
                unchecked
                {
                    int hash = 19 + salt * 37 + attempt * 6421;
                    foreach (char c in original) hash = hash * 31 + c;

                    var rng = new System.Random(hash);
                    var chars = new char[8];
                    for (int i = 0; i < chars.Length; i++)
                        chars[i] = i % 2 == 0
                            ? Consonants[rng.Next(Consonants.Length)]
                            : Vowels[rng.Next(Vowels.Length)];

                    string candidate = new string(chars);
                    if (!taken.Contains(candidate)) return candidate;
                }
            }

            for (int i = 0; ; i++)
                if (!taken.Contains("o" + i)) return "o" + i;
        }

        // ------------------------------------------------------------------ rewriting

        /// <summary>
        /// Rename the objects themselves. Only ever called on the build clone.
        /// </summary>
        internal static int RewriteObjects(GameObject avatar, IDictionary<string, string> map)
        {
            if (avatar == null || map == null || map.Count == 0) return 0;

            int renamed = 0;
            foreach (var transform in avatar.GetComponentsInChildren<Transform>(true))
            {
                if (transform == avatar.transform) continue;
                if (!map.TryGetValue(transform.name, out var replacement)) continue;

                transform.name = replacement;
                renamed++;
            }
            return renamed;
        }

        /// <summary>
        /// Rewrite every animation path inside one controller: the clips it reaches, and the one
        /// state behaviour that carries a path.
        ///
        /// Guarded per object like everything else that writes here - a controller this build owns
        /// can still reach clips in the artist's project, and those are read, never written.
        /// </summary>
        internal static int RewriteController(AnimatorController controller,
                                              IDictionary<string, string> map, string folder)
        {
            if (controller == null || map == null || map.Count == 0) return 0;

            string path = AssetDatabase.GetAssetPath(controller);
            int rewrites = 0;

            var machines = new HashSet<AnimatorStateMachine>();
            var motions = new HashSet<Motion>();

            foreach (var layer in controller.layers)
                RewriteStateMachine(layer.stateMachine, path, folder, map, machines, motions, ref rewrites);

            if (rewrites > 0) EditorUtility.SetDirty(controller);
            return rewrites;
        }

        private static void RewriteStateMachine(AnimatorStateMachine machine, string ownerPath,
                                                string folder, IDictionary<string, string> map,
                                                HashSet<AnimatorStateMachine> machines,
                                                HashSet<Motion> motions, ref int rewrites)
        {
            if (machine == null || !machines.Add(machine)) return;

            if (Belongs(machine, ownerPath, folder))
                foreach (var behaviour in machine.behaviours)
                    RewriteBehaviour(behaviour, ownerPath, folder, map, ref rewrites);

            foreach (var child in machine.states)
            {
                var state = child.state;
                if (state == null || !Belongs(state, ownerPath, folder)) continue;

                foreach (var behaviour in state.behaviours)
                    RewriteBehaviour(behaviour, ownerPath, folder, map, ref rewrites);
                RewriteMotion(state.motion, ownerPath, folder, map, motions, ref rewrites);
            }

            foreach (var child in machine.stateMachines)
                RewriteStateMachine(child.stateMachine, ownerPath, folder, map, machines, motions,
                                    ref rewrites);
        }

        private static void RewriteMotion(Motion motion, string ownerPath, string folder,
                                          IDictionary<string, string> map, HashSet<Motion> seen,
                                          ref int rewrites)
        {
            if (motion == null || !seen.Add(motion)) return;
            if (!Belongs(motion, ownerPath, folder)) return;

            if (motion is BlendTree tree)
            {
                foreach (var child in tree.children)
                    RewriteMotion(child.motion, ownerPath, folder, map, seen, ref rewrites);
                return;
            }

            if (!(motion is AnimationClip clip)) return;

            // Read every binding out before writing any of them back. Renaming a binding in place
            // while iterating the collection it came from is how a clip loses curves.
            var floats = AnimationUtility.GetCurveBindings(clip)
                .Select(b => (binding: b, curve: AnimationUtility.GetEditorCurve(clip, b)))
                .ToList();
            var objects = AnimationUtility.GetObjectReferenceCurveBindings(clip)
                .Select(b => (binding: b, curve: AnimationUtility.GetObjectReferenceCurve(clip, b)))
                .ToList();

            foreach (var entry in floats)
            {
                string replacement = RewritePath(entry.binding.path, map);
                if (replacement == entry.binding.path) continue;

                var moved = entry.binding;
                moved.path = replacement;

                AnimationUtility.SetEditorCurve(clip, entry.binding, null);
                AnimationUtility.SetEditorCurve(clip, moved, entry.curve);
                rewrites++;
            }

            foreach (var entry in objects)
            {
                string replacement = RewritePath(entry.binding.path, map);
                if (replacement == entry.binding.path) continue;

                var moved = entry.binding;
                moved.path = replacement;

                AnimationUtility.SetObjectReferenceCurve(clip, entry.binding, null);
                AnimationUtility.SetObjectReferenceCurve(clip, moved, entry.curve);
                rewrites++;
            }
        }

        private static void RewriteBehaviour(StateMachineBehaviour behaviour, string ownerPath, string folder,
                                             IDictionary<string, string> map, ref int rewrites)
        {
            if (!Belongs(behaviour, ownerPath, folder)) return;
            if (behaviour.GetType().Name.IndexOf("PlayAudio", StringComparison.Ordinal) < 0) return;

            var field = behaviour.GetType().GetField("SourcePath",
                                                     BindingFlags.Public | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(string)) return;

            string value = (string)field.GetValue(behaviour);
            string replacement = RewritePath(value, map);
            if (replacement == value) return;

            field.SetValue(behaviour, replacement);
            rewrites++;
        }

        /// <summary>
        /// A path, segment by segment. This is the whole trick: because the map is by name rather
        /// than by object, no segment needs to know where it sits.
        /// </summary>
        internal static string RewritePath(string path, IDictionary<string, string> map)
        {
            if (string.IsNullOrEmpty(path) || map == null || map.Count == 0) return path;
            if (path.IndexOf('/') < 0) return map.TryGetValue(path, out var single) ? single : path;

            var segments = path.Split('/');
            bool changed = false;

            for (int i = 0; i < segments.Length; i++)
            {
                if (!map.TryGetValue(segments[i], out var replacement)) continue;
                segments[i] = replacement;
                changed = true;
            }

            return changed ? string.Join("/", segments) : path;
        }

        /// <summary>
        /// May this build write to the object?
        ///
        /// Wider than the parameter pass's identical-looking predicate, and the extra clause earns
        /// its place: this build generates the clips that drive the unlock, and it writes them into
        /// its own folder as assets of their own rather than inside the FX controller. They bind to
        /// RENDERER PATHS. Rename an object without rewriting them and the digits stop reaching the
        /// material - the avatar uploads, looks right in the editor, and can never be unlocked.
        /// Nothing in the build would report it.
        /// </summary>
        private static bool Belongs(UnityEngine.Object o, string ownerPath, string folder)
        {
            if (o == null) return false;

            string path = AssetDatabase.GetAssetPath(o);
            if (string.IsNullOrEmpty(path) || path == ownerPath) return true;

            return !string.IsNullOrEmpty(folder) &&
                   path.StartsWith(folder + "/", StringComparison.Ordinal);
        }
    }
}
#endif
