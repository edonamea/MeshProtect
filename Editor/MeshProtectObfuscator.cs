#if UNITY_EDITOR && LILMP_VRCSDK3_AVATARS
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace MeshProtect
{
    /// <summary>
    /// Strip the semantics out of the animator before it ships.
    ///
    /// The mesh is displaced, but the animator that drives it is not, and it is the most readable
    /// thing in the bundle. A layer called "Dance", a state called "Kiss_On", a clip called
    /// "Outfit_Swimsuit" tells anyone who opens the file exactly what the avatar does and which
    /// piece to lift. Renaming costs nothing at runtime and removes all of it.
    ///
    /// WHAT IS RENAMED HERE: layers, state machines, states, blend trees, and animation clips.
    ///
    /// Parameters are renamed too, but not here - see MeshProtectParameters. They are a different
    /// problem and it is worth saying why. Everything in this file belongs to one controller, so
    /// renaming it in the FX copy and leaving Base alone is merely less thorough. A parameter is
    /// avatar-global: rename it in one place and every other place stops agreeing. So it needs a
    /// pass that can see the whole avatar at once and prove that every mention of a name is
    /// somewhere the upload rewrites, which is not a question this file is in a position to ask.
    ///
    /// WHAT IS NEVER RENAMED, and why:
    ///
    /// - Menu control labels. Those are what the wearer reads in the radial menu.
    ///
    /// - Blend shape names. Visemes, lip sync and MMD worlds all drive them by name.
    ///
    /// SCOPE. Only the FX controller, and only because this build made it: the unlock layers had
    /// to live somewhere, so a copy of the avatar's FX already exists in the temporary folder.
    /// Base, Gesture, Action and the special layers are left exactly as the avatar had them.
    ///
    /// They were not, once. Renaming them required copying them first, and that copy is what left a
    /// real avatar holding a pose it was not in, unable to stand up - while every structural
    /// comparison of the two builds, tens of thousands of properties, came back identical apart
    /// from the names. Whatever the copy costs does not show up in the animator's contents, which
    /// also means nothing here could rule out the next thing it costs.
    ///
    /// So it does not copy anyone else's controller. Modular Avatar and VRCFury add layers to
    /// avatars all day without duplicating one, and the names worth hiding - outfits, toggles,
    /// gimmicks - are in FX anyway.
    ///
    /// Clips inside that FX copy are cloned in memory and stored in it rather than copied file by
    /// file, because a large avatar has hundreds and each CopyAsset is a disk round trip.
    /// </summary>
    public static class MeshProtectObfuscator
    {
        public static int Run(VRCAvatarDescriptor descriptor, MeshProtectVariant variant, string folder,
                              ICollection<string> alreadyPrepared = null)
        {
            if (descriptor == null) return 0;
            return Run(ref descriptor.baseAnimationLayers, ref descriptor.specialAnimationLayers,
                       variant, folder, alreadyPrepared);
        }

        /// <summary>
        /// The same work on layer arrays that are not attached to an avatar yet.
        ///
        /// Preparing the copies happens outside a build, against controllers the scene's descriptor
        /// must go on pointing at, so there is no descriptor to hand this and faking one would mean
        /// putting a VRCAvatarDescriptor in the user's scene to take it out again.
        /// </summary>
        internal static int Run(ref VRCAvatarDescriptor.CustomAnimLayer[] baseLayers,
                                ref VRCAvatarDescriptor.CustomAnimLayer[] specialLayers,
                                MeshProtectVariant variant, string folder,
                                ICollection<string> alreadyPrepared = null)
        {

            // Our own layers are already meaningless, and the validator looks them up by the exact
            // strings held in the variant. Renaming them here would break that lookup for nothing.
            var reserved = new HashSet<string>(
                variant.packLayerNames
                    .Concat(variant.decodeLayerNames)
                    .Concat(variant.padLayerNames)
                    .Where(n => !string.IsNullOrEmpty(n)));

            var namer = new Namer(variant.macSalt, reserved);
            var clipClones = new Dictionary<AnimationClip, AnimationClip>();
            var skipped = new List<string>();
            var wholeControllers = new List<string>();
            int renamed = 0;

            renamed += Sweep(folder, namer, clipClones, ref baseLayers, skipped, wholeControllers,
                             alreadyPrepared);
            renamed += Sweep(folder, namer, clipClones, ref specialLayers, skipped, wholeControllers,
                             alreadyPrepared);

            AssetDatabase.SaveAssets();

            // A whole playable layer and a blend tree inside one are two different situations with
            // two different answers, and they used to share a message. "Move these into the animator
            // controller that uses them" cannot be done to an animator controller, and it was being
            // printed for one - next to the warning that explains the real reason, so the same event
            // arrived as two unrelated problems and one of them could not be acted on.
            if (wholeControllers.Count > 0)
            {
                Debug.LogWarning(
                    $"[MeshProtect] {wholeControllers.Count} of this avatar's own animator " +
                    "controller(s) ship with their original names:\n  " +
                    string.Join("\n  ", wholeControllers.Distinct().Take(20)) +
                    "\n\nThat is what happens without prepared copies of them, and it is safe - the " +
                    "FX controller, where outfits and toggles live, is renamed either way. Press " +
                    "Prepare Controllers on the Mesh Protect Root component to cover these too. If " +
                    "you already did, look further up: this upload gave the copies back for a reason " +
                    "it will have named.");
            }

            if (skipped.Count > 0)
            {
                // Naming them, not just counting them. These are almost always blend trees saved as
                // their own .asset file, and the author can close the gap themselves by moving them
                // into the controller - which is only actionable if they know which ones.
                Debug.LogWarning(
                    $"[MeshProtect] {skipped.Count} animator object(s) ship with their original " +
                    "names, because they are stored as separate assets in your project and renaming " +
                    "them would have modified your project:\n  " +
                    string.Join("\n  ", skipped.Distinct().Take(20)) +
                    (skipped.Distinct().Count() > 20 ? "\n  ..." : "") +
                    "\n\nEverything else was renamed. To close the gap, move these into the " +
                    "animator controller that uses them (Unity keeps them as sub-assets there) and " +
                    "upload again.");
            }

            return renamed;
        }

        private static int Sweep(string folder, Namer namer,
                                 Dictionary<AnimationClip, AnimationClip> clipClones,
                                 ref VRCAvatarDescriptor.CustomAnimLayer[] layers,
                                 List<string> skipped, List<string> wholeControllers,
                                 ICollection<string> alreadyPrepared)
        {
            if (layers == null) return 0;

            int renamed = 0;
            for (int i = 0; i < layers.Length; i++)
            {
                // isDefault means VRChat substitutes its own controller and ignores whatever is
                // referenced here, so copying it would add a controller to the bundle for nothing.
                if (layers[i].isDefault) continue;

                var controller = layers[i].animatorController as AnimatorController;
                if (controller == null) continue;

                // Only a controller this build already owns. The FX one is ours because the unlock
                // layers had to go somewhere; Base, Gesture, Action and the special layers belong
                // to the avatar, and renaming them meant copying them first.
                //
                // That copying is what broke the avatar. With it, the avatar held a pose it was not
                // in and could not stand up; with obfuscation off - the only difference being that
                // these controllers are left alone - it behaved correctly. Every structural
                // comparison of the two builds came back identical except for the names, so what
                // the copy costs is not visible in the animator's contents, and nothing here can
                // rule out the next thing it costs either.
                //
                // Modular Avatar and VRCFury add layers to avatars all day without duplicating
                // somebody else's controllers. Neither should this. The names inside the FX
                // controller are the ones worth hiding anyway - that is where the outfits and
                // toggles are.
                if (!Owned(controller, folder))
                {
                    // A controller prepared before the build is already renamed, and the build is
                    // pointing at it rather than at the avatar's own. Reporting it as skipped would
                    // tell the author their layers ship readable when the opposite is true.
                    if (alreadyPrepared == null ||
                        !alreadyPrepared.Contains(AssetDatabase.GetAssetPath(controller)))
                        Note(wholeControllers, controller);
                    continue;
                }

                layers[i].animatorController = controller;
                renamed += Obfuscate(controller, namer, folder, clipClones, skipped);
            }
            return renamed;
        }

        /// <summary>
        /// Does the VRChat client look for this asset by name?
        ///
        /// Two tests, because either alone leaks. A proxy animation copied into the project keeps
        /// its name but loses its package path; an asset added to a future SDK release may matter
        /// to the client without being called proxy_ at all.
        /// </summary>
        internal static bool IsClientOwned(Object o)
        {
            if (o == null) return false;

            if (o.name != null &&
                o.name.StartsWith("proxy_", System.StringComparison.OrdinalIgnoreCase))
                return true;

            string path = AssetDatabase.GetAssetPath(o);
            return !string.IsNullOrEmpty(path) &&
                   path.StartsWith("Packages/com.vrchat.", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when this object lives in the build's own folder, or nowhere on disk at all.
        ///
        /// This is checked immediately before every rename, and it is not redundant with copying
        /// the controller first. Copying an .controller file does NOT guarantee the copy's layers
        /// point at sub-objects inside the copy: a controller whose state machines are stored as
        /// separate assets - which real avatars do have - produces a copy that still references the
        /// originals by GUID. Renaming through those references rewrote 228 names in a real
        /// project's animation folder during testing, with the file copy having succeeded and every
        /// object-identity check saying the copy was distinct.
        ///
        /// So ownership is decided per object, at the point of mutation, rather than inferred once
        /// from the container. Anything that fails this test keeps its name; a readable name in the
        /// bundle is a disclosure, but writing to the artist's project is damage.
        /// </summary>
        private static bool Owned(Object o, string folder)
        {
            if (o == null) return false;
            string path = AssetDatabase.GetAssetPath(o);
            return string.IsNullOrEmpty(path) ||
                   path.StartsWith(folder + "/", System.StringComparison.Ordinal);
        }

        /// <summary>Record what kept its name, and where it lives, so the warning is actionable.</summary>
        private static void Note(List<string> skipped, Object o)
        {
            if (o == null) return;
            string path = AssetDatabase.GetAssetPath(o);
            skipped.Add($"{o.GetType().Name} '{o.name}'" +
                        (string.IsNullOrEmpty(path) ? "" : $"  ({path})"));
        }

        private static int Obfuscate(AnimatorController controller, Namer namer, string folder,
                                     Dictionary<AnimationClip, AnimationClip> clipClones,
                                     List<string> skipped)
        {
            int renamed = 0;
            var layers = controller.layers;

            for (int i = 0; i < layers.Length; i++)
            {
                // Our own unlock layers keep their LAYER name, because the validator finds them by
                // the exact string in the variant. Everything inside them is still renamed: they
                // shipped with states called "Locked", "1" .. "8", which named the mechanism and
                // said which layer decoded which digit. The tests locate those states through
                // transition conditions, not names, so nothing depends on them.
                bool reservedName = namer.IsReserved(layers[i].name);

                // A layer is part of the controller asset itself, so the controller's ownership is
                // the layer's ownership.
                if (reservedName) { }
                else if (Owned(controller, folder))
                {
                    layers[i].name = namer.Next();
                    renamed++;
                }
                else Note(skipped, controller);

                renamed += ObfuscateStateMachine(layers[i].stateMachine, controller, namer, folder,
                                                 clipClones, new HashSet<AnimatorStateMachine>(),
                                                 skipped);
            }

            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            return renamed;
        }

        private static int ObfuscateStateMachine(AnimatorStateMachine machine, AnimatorController owner,
                                                 Namer namer, string folder,
                                                 Dictionary<AnimationClip, AnimationClip> clipClones,
                                                 HashSet<AnimatorStateMachine> seen,
                                                 List<string> skipped)
        {
            if (machine == null || !seen.Add(machine)) return 0;

            int renamed = 0;
            if (Owned(machine, folder)) { machine.name = namer.Next(); renamed++; }
            else Note(skipped, machine);

            foreach (var child in machine.states)
            {
                if (child.state == null) continue;

                if (Owned(child.state, folder)) { child.state.name = namer.Next(); renamed++; }
                else Note(skipped, child.state);

                // The motion swap is a write to the state, so it needs the same permission as the
                // rename - otherwise a clip clone would be spliced into the project's own state.
                if (Owned(child.state, folder))
                    child.state.motion = ObfuscateMotion(child.state.motion, owner, namer, folder,
                                                         clipClones, ref renamed, skipped);
            }

            foreach (var child in machine.stateMachines)
                renamed += ObfuscateStateMachine(child.stateMachine, owner, namer, folder, clipClones,
                                                 seen, skipped);

            return renamed;
        }

        private static Motion ObfuscateMotion(Motion motion, AnimatorController owner, Namer namer,
                                              string folder,
                                              Dictionary<AnimationClip, AnimationClip> clipClones,
                                              ref int renamed, List<string> skipped)
        {
            if (motion == null) return null;

            if (motion is BlendTree tree)
            {
                if (Owned(tree, folder)) { tree.name = namer.Next(); renamed++; }
                else { Note(skipped, tree); return tree; }

                var children = tree.children;
                for (int i = 0; i < children.Length; i++)
                    children[i].motion = ObfuscateMotion(children[i].motion, owner, namer, folder,
                                                         clipClones, ref renamed, skipped);
                tree.children = children;
                return tree;
            }

            if (motion is AnimationClip clip)
            {
                // VRChat swaps proxy animations for real locomotion at runtime, and the client
                // finds them by name in the bundle. Rename one and the swap never happens: the
                // avatar plays the placeholder pose instead, so crouch does nothing and standing
                // back up does nothing. This broke pose switching on a real avatar - 28 proxy
                // clips renamed, locomotion dead - and nothing in the build reported a problem,
                // because from the build's point of view nothing had gone wrong.
                //
                // The rule is wider than proxy_ on purpose. Anything shipped inside a VRChat
                // package exists because the client expects it, and the client matches on names
                // this tool does not get to choose. Leaving those alone costs a handful of
                // readable names out of hundreds.
                if (IsClientOwned(clip)) return clip;

                // A clip already in the build folder is one we generated. Rename it where it is
                // rather than cloning a second copy of it into the bundle.
                if (Owned(clip, folder))
                {
                    if (!clipClones.ContainsKey(clip))
                    {
                        clip.name = namer.Next();
                        clipClones[clip] = clip;
                        renamed++;
                    }
                    return clip;
                }

                // Everything else belongs to the project and is never renamed in place. A clone
                // goes inside the controller we own, so the project's .anim files keep both their
                // names and their contents.
                //
                // Owned() is not the right question here. It answers "may I write to this", and an
                // object that exists only in memory passes because there is no file to damage - but
                // AddObjectToAsset needs a file to add TO, and fails on a controller an upstream
                // tool built in memory. VRCFury and Modular Avatar both hand over controllers like
                // that. Two different questions; asking one predicate both is how the playable
                // layers ended up swapped.
                if (!Owned(owner, folder) || !AssetDatabase.Contains(owner))
                {
                    Note(skipped, clip);
                    return clip;
                }

                if (!clipClones.TryGetValue(clip, out var clone))
                {
                    clone = Object.Instantiate(clip);
                    clone.name = namer.Next();
                    AssetDatabase.AddObjectToAsset(clone, owner);
                    clipClones[clip] = clone;
                    renamed++;
                }
                return clone;
            }

            return motion;
        }

        /// <summary>
        /// Pronounceable nonsense, seeded from the variant so a re-bake of the same avatar produces
        /// the same names rather than a fresh set every upload.
        /// </summary>
        private sealed class Namer
        {
            private const string Consonants = "bcdfghjklmnprstvwz";
            private const string Vowels = "aeiou";

            private readonly System.Random rng;
            private readonly HashSet<string> reserved;
            private readonly HashSet<string> used = new HashSet<string>();

            public Namer(int seed, HashSet<string> reserved)
            {
                rng = new System.Random(unchecked(seed * 31 + 0x5f3a));
                this.reserved = reserved;
            }

            public bool IsReserved(string name) => name != null && reserved.Contains(name);

            public string Next()
            {
                for (int attempt = 0; attempt < 64; attempt++)
                {
                    var chars = new char[8];
                    for (int i = 0; i < chars.Length; i++)
                        chars[i] = i % 2 == 0
                            ? Consonants[rng.Next(Consonants.Length)]
                            : Vowels[rng.Next(Vowels.Length)];

                    string candidate = new string(chars);
                    if (!reserved.Contains(candidate) && used.Add(candidate)) return candidate;
                }

                // 18*5 alternating gives far more than any avatar needs, but a name must still come
                // back rather than an exception if the pool ever does run dry.
                string fallback = "x" + used.Count;
                used.Add(fallback);
                return fallback;
            }
        }
    }
}
#endif
