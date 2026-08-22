using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// Authoring component. Lives on the SOURCE avatar only.
    /// The baker never copies this onto the generated avatar. If it is ever found during a build
    /// the upload is blocked rather than fixed up: it holds the key, and its presence means the
    /// source avatar is being uploaded instead of the protected copy.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MeshProtect/Mesh Protect Root")]
    public class MeshProtectRoot : MonoBehaviour
    {
        public enum DisplacementMode
        {
            /// <summary>1 DOF, displaces along the vertex normal. Works without tangents.</summary>
            Normal = 0,

            /// <summary>2 DOF, displaces in the tangent/normal plane. Harder to auto-recover.</summary>
            TangentSpace = 1
        }

        public const int PasswordLength = 6;

        /// <summary>
        /// Four bits per digit: values 1-8 are the digit, and 0 means "never entered".
        ///
        /// Eight choices is not a coincidence - a VRChat radial has eight sectors and a menu page
        /// holds eight items, so every digit fits one page with no overflow sub-menu, and 8^6 is
        /// 262144 combinations.
        ///
        /// The fourth bit buys the zero. Three bits held exactly the eight digits with no spare
        /// pattern, so a fresh avatar was indistinguishable from one whose password was all ones,
        /// and it rendered as a scrambled mesh. Being able to say "nothing has been entered" is
        /// what lets the shader collapse the avatar to a point instead - see the locked check in
        /// the generated decode. It costs six more synced bits and is worth every one of them:
        /// an exploded mesh is an eyesore for everyone standing nearby and a signature that says
        /// "this avatar is protected".
        /// </summary>
        public const int BitsPerDigit = 4;
        public const int KeyBitLength = PasswordLength * BitsPerDigit;
        public const int MinDigit = 1;
        public const int MaxDigit = 8;

        /// <summary>
        /// The value a position holds when nobody has entered it, and the one value the menu cannot
        /// send. It goes into the key exactly like any other value, which is what makes a password
        /// shorter than six digits possible: the positions the author left out are part of the key
        /// as "not entered", and a wearer who touches one of them can never put it back.
        ///
        /// It has not always gone into the key. The packing used to store digit-minus-one, so zero
        /// and one arrived at the same four bits and a separate check on the raw properties carried
        /// the difference. That worked while every password was six digits of 1-8. It would have
        /// made "1234" and "123411" the same password, and it would have read a correct short
        /// password as "not entered yet", so it has a pattern of its own now - see KeyNibble.
        /// </summary>
        public const int NotEntered = 0;

        /// <summary>
        /// A position's four bits inside the key: digits 1-8 keep the 0-7 they have always had, and
        /// "not entered" takes the spare eighth pattern.
        ///
        /// Mapping the digit straight in - 1 through 8 - is the obvious way to do it and it is
        /// wrong. Key bits stop being uniform: the top bit of every position would be set only for
        /// the digit 8, and the hash programs this tool generates are accepted on a measured
        /// avalanche. Two dozen generated variants failed that measurement the moment the encoding
        /// changed, which is exactly what the check is for. Keeping the real digits on 0-7 leaves a
        /// six digit password's key bit for bit what it was before any of this - an avatar uploaded
        /// with the same password before and after gets the same key.
        ///
        /// What that measurement covers is worth stating exactly, because it did not always match
        /// this. It samples the nine values a position can hold and perturbs across the boundary
        /// between them and NotEntered, so the spare pattern is measured like any other. It used to
        /// sample 1-8 only: the top bit of every position was then stuck at zero through the whole
        /// gate, and the digit-to-NotEntered transition - the difference between a position a short
        /// password leaves out and one a wearer has tapped - was never measured. Variants generated
        /// before that was fixed passed the narrower gate; nothing re-measures one already sitting
        /// on somebody's component, and Replace Protection is what re-rolls it.
        /// </summary>
        public static int KeyNibble(int digit) =>
            digit == NotEntered ? MaxDigit : digit - MinDigit;

        [Tooltip("How far vertices are pushed, as a fraction of the mesh bounding box diagonal. " +
                 "Larger = more damage to a ripped mesh, but also uglier for viewers who have shaders turned off.")]
        [Range(0.005f, 0.3f)]
        public float distortRatio = 0.04f;

        public DisplacementMode mode = DisplacementMode.TangentSpace;

        [Tooltip("Many VRChat models ship without tangents. TangentSpace mode needs them, and the " +
                 "mode has to be uniform across the avatar because it is a material property. " +
                 "With this on, missing tangents are recalculated during the bake. With it off, an " +
                 "avatar containing any mesh without tangents falls back to Normal mode as a whole.")]
        public bool recalculateMissingTangents = true;

        [Tooltip("Escape hatch, off by default. Scales displacement down where several bones share a " +
                 "vertex, at the cost of protection there. Measurement on a real avatar showed the " +
                 "restore is already exact at every pose, so this is not normally needed - every bake " +
                 "reports its own skinning residual, so turn this on only if that number is not tiny.")]
        public bool attenuateAtJoints = false;

        [Tooltip("Higher = protection falls off faster near joints. 4 is a reasonable default.")]
        [Range(1f, 12f)]
        public float rigidityExponent = 4f;

        [Tooltip("Rename every animator layer, state, blend tree and animation clip in the uploaded " +
                 "avatar to meaningless words, so the bundle no longer says 'Dance' or " +
                 "'Outfit_Swimsuit'. Only the upload is affected - your project is not touched. " +
                 "Turn it off if you need readable layer names while debugging a build.")]
        public bool obfuscateAnimatorNames = true;

        [Tooltip("Also rename parameters, but only the ones this tool can prove nothing outside the " +
                 "avatar reads: every place that names them has to be something the upload rewrites, " +
                 "and the name must not be one VRChat drives itself. Anything it cannot account for " +
                 "keeps its name, and the report says which and why. Parameters in the expression " +
                 "list are left alone unless the option below is on.")]
        public bool obfuscateParameterNames = true;

        [Tooltip("Include the expression parameters - the ones in your parameter asset and menus. " +
                 "This is the only option here with a cost outside your own project, because this " +
                 "list is the avatar's public API: OSC applications and face tracking address these " +
                 "names directly. Names VRChat drives, namespaced names and the ones VRCFaceTracking " +
                 "is documented to use are all kept, and the report says what was renamed. What " +
                 "remains: saved parameter values reset for everyone who already has the avatar (a " +
                 "renamed parameter is a new parameter), and an OSC setup keyed to your own names " +
                 "stops working until it is repointed. Turn this one off if either matters to you.")]
        public bool obfuscateExpressionParameters = true;

        [Tooltip("Some avatars keep blend trees in their own .asset files instead of inside the " +
                 "animator controller. Copying the controller does not bring those along, so they " +
                 "cannot be renamed and neither can any parameter they use - on the avatar this was " +
                 "measured against, that was 43 parameters and 320 trees. With this on, copies of " +
                 "them go inside the protected controller. Your own files are never modified.")]
        public bool copySeparateBlendTrees = true;

        [Tooltip("Rename the objects in the uploaded avatar, so the hierarchy no longer reads " +
                 "'Hair_Long' or 'Outfit_Swimsuit' - the first thing anyone who opens the file sees. " +
                 "Only names this tool can prove nothing outside the avatar reads: every animation " +
                 "that names one has to be one this upload rewrites. The skeleton, the object MMD " +
                 "worlds drive the face through, and anything an animation this upload does not own " +
                 "refers to are all left alone. Your scene is not touched - only the upload.")]
        public bool obfuscateObjectNames = true;

        /// <summary>
        /// A renamed copy of one of the avatar's own animator controllers, made before the upload.
        ///
        /// Renaming inside Base, Gesture or Action means copying the controller first, and for a
        /// long time this tool refused to, because doing it during the upload produced an avatar
        /// that held a pose it was not in and could not stand up. Three uploads of one avatar -
        /// same code, same password, differing only in when the copy was made - showed that copying
        /// ahead of the upload works and copying during it does not. The written files are
        /// equivalent and the finished avatar's object graph is identical either way, so the reason
        /// is still unknown; what is established is that the copies have to exist before the build
        /// starts, and that during the build nothing may be created - only pointed at.
        ///
        /// That is what this list is: made by a button, kept in the project, and at build time
        /// swapped in on the copy the SDK builds from. The avatar in the scene keeps pointing at
        /// its own controllers and is never modified.
        /// </summary>
        [Serializable]
        public class PreparedController
        {
            /// <summary>Which playable layer this stands in for - Base, Gesture, Action, Sitting.</summary>
            public string layerType;

            /// <summary>The controller in the project it was made from, so the swap can find it
            /// again on the build clone even after other tools have run.</summary>
            public string sourceGuid;

            /// <summary>What that controller's file looked like when the copy was taken. A copy of
            /// a controller the author has edited since is a copy of the wrong thing, and the
            /// symptom - a toggle that silently does nothing in the upload - is not one anybody
            /// would trace back to here.</summary>
            public string sourceHash;

            /// <summary>Where the renamed copy lives.</summary>
            public string copyPath;

            /// <summary>
            /// What the copy's own bytes looked like when it was written.
            ///
            /// The point of this one is WHERE it is not. Every other record of what the copies
            /// carry - the parameter map, the object map, the option flags - is serialised state on
            /// this component, and Undo rewrites serialised state. The copies are files, and Undo
            /// does not touch files. Comparing this against the file on disk therefore catches any
            /// way the record and the copies can come apart, including the ways a future map nobody
            /// has written yet would come apart, rather than one check per kind of map.
            /// </summary>
            public string copyHash;
        }

        /// <summary>Generated by the Prepare button, not authored. Empty means the avatar's own
        /// controllers ship with their original names, which is what every release so far did.</summary>
        public List<PreparedController> preparedControllers = new List<PreparedController>();

        /// <summary>
        /// One parameter the prepared copies already carry under a different name.
        ///
        /// A parameter is avatar-global: the Base copy, the FX controller built during the upload,
        /// the expression list and the contact receivers all have to agree on it or the avatar stops
        /// working. The copies are renamed before the build and everything else during it, so the
        /// two passes need the same answer, and this is where it is kept between them.
        /// </summary>
        [Serializable]
        public class RenamedParameter
        {
            public string original;
            public string obfuscated;
        }

        /// <summary>
        /// Written by Prepare. The build re-checks every entry against the avatar it is actually
        /// building - another tool may have added a reference in between - and if any of them no
        /// longer holds, it drops the prepared copies entirely and uploads the author's own
        /// controllers rather than a half-renamed avatar.
        /// </summary>
        public List<RenamedParameter> renamedParameters = new List<RenamedParameter>();

        /// <summary>
        /// Object names the prepared copies have already been rewritten for.
        ///
        /// The same shape of record as the parameters and for the same reason, but what carries it
        /// is different: an object's name is written into every animation path that reaches it, so
        /// the copies hold clips whose paths already say the new names, while the objects themselves
        /// are not renamed until the upload. The two only line up because both use this map.
        /// </summary>
        public List<RenamedParameter> renamedObjects = new List<RenamedParameter>();

        /// <summary>
        /// The options the copies were prepared under. A change to either of these invalidates them
        /// as surely as editing a controller does: the map was decided under the old answer, and
        /// half of it is already written into the copies.
        /// </summary>
        /// <summary>
        /// Recorded separately from <see cref="renamedParameters"/> being empty, because those are
        /// different states: prepared with renaming off, and prepared with it on when no parameter
        /// could be cleared, both leave the list empty. Only the first is out of date when the
        /// option is on, and without this the editor cannot tell them apart - it reported an avatar
        /// prepared with renaming off as up to date after the author turned renaming on.
        /// </summary>
        public bool preparedWithParameterNames;

        public bool preparedWithObjectNames;
        public bool preparedWithExpressionParameters;
        public bool preparedWithSeparateBlendTrees;

        /// <summary>
        /// What the avatar's expression parameters and root menu looked like when the copies were
        /// made.
        ///
        /// Which parameters were safe to rename depends on those two as much as on the controllers,
        /// and the watcher only wakes up for controllers. Adding a parameter to the expression list
        /// can turn a name that was safe into one that is not, and the build would then drop the
        /// copies to stay correct - losing every renamed layer over an edit nobody connected to
        /// this. Noticing it in the editor means the copies are simply remade instead.
        ///
        /// It covers the root menu only, not the whole tree, so it is a convenience rather than a
        /// guarantee: the check the build makes before it trusts the copies is the one that has to
        /// be complete.
        /// </summary>
        public string preparedSurfaceHash;

        [Tooltip("Renderers to protect. Leave empty to protect every renderer under this object " +
                 "that uses a lilToon material.")]
        public List<Renderer> targetRenderers = new List<Renderer>();

        [Tooltip("Materials that should be skipped (eyes, teeth, third party effects, anything whose " +
                 "mesh you do not mind losing).")]
        public List<Material> ignoredMaterials = new List<Material>();

        [Tooltip("Materials that change NOTHING on screen. The build treats them as absent when " +
                 "deciding whether a mesh may be displaced, so they stop blocking the encryption " +
                 "of sub-meshes they share or re-draw.\n\n" +
                 "Drawing no colour is NOT enough. A material that writes stencil or depth decides " +
                 "what OTHER materials draw, and its geometry is displaced along with everything " +
                 "else - so the mask lands in the wrong place and the damage shows up on the body " +
                 "or the clothes, not on the material you listed. Most anti-clip shells work " +
                 "exactly this way; do NOT list one.\n\n" +
                 "A wrong entry does not necessarily show itself while the avatar is locked: " +
                 "everything this tool protects is hidden then, so there is nothing for a stray " +
                 "mask to spoil. Test unlocked.")]
        public List<Material> invisibleMaterials = new List<Material>();

        [Tooltip("Where generated meshes, materials, animations and the shader-facing assets go. " +
                 "Empty means a Generated folder inside the plugin's own folder, so deleting the " +
                 "plugin folder removes everything it ever made (a plugin installed under " +
                 "Packages/ falls back to Assets/_MeshProtect instead). Components saved by " +
                 "older versions carry the folder they were already using and keep it.")]
        public string outputFolder = "";

        // ---- generated, not authored ----

        [Tooltip("The six-digit password. Every digit is 1-8. Generated by 'Generate New Password' " +
                 "and never copied to the built avatar.")]
        [SerializeField] public int[] keyDigits = new int[0];

        /// <summary>
        /// This avatar's protection variant: hash program, identifiers, digit ordering.
        ///
        /// It lives HERE, in the project, and nowhere else. The uploaded avatar carries only the
        /// consequences of it - a displaced mesh and a shader with the constants compiled in - so
        /// the source project holds information the shipped file does not.
        ///
        /// Regenerated only together with the password. Regenerating it on its own would rename
        /// the expression parameters, and renamed parameters lose their saved values, which would
        /// force the owner to re-enter the password after every re-bake.
        /// </summary>
        [SerializeField] public MeshProtectVariant variant = new MeshProtectVariant();

        /// <summary>
        /// Is there a usable password, regardless of the variant?
        ///
        /// Separate from <see cref="HasKey"/> on purpose. The password is the author's secret and
        /// the variant is the algorithm; they are replaced for different reasons and at different
        /// times, and a question about one should never be answered by the state of the other.
        /// </summary>
        public bool HasPassword => TypedLength(keyDigits) > 0;

        /// <summary>
        /// How many digits the author actually chose, or 0 if this is not a usable password.
        ///
        /// The array is always six long. A password shorter than that is stored as its digits
        /// followed by zeros, and zero is the value the menu can never send - so the positions the
        /// author left out can only ever be wrong once somebody touches them, and the only way back
        /// is to reset the avatar. That is the intended behaviour and not a side effect: the six
        /// dials stay on the menu whatever the length is, so the dial count never says how long the
        /// password is.
        ///
        /// Zeros have to be trailing. A zero in the middle would be a position the wearer must
        /// leave alone while entering the ones on either side of it, which is a password nobody can
        /// describe over voice chat, and it buys nothing an extra digit would not.
        /// </summary>
        public static int TypedLength(int[] digits)
        {
            if (digits == null || digits.Length != PasswordLength) return 0;

            int length = PasswordLength;
            while (length > 0 && digits[length - 1] == NotEntered) length--;
            if (length == 0) return 0;

            for (int i = 0; i < length; i++)
                if (digits[i] < MinDigit || digits[i] > MaxDigit) return 0;

            return length;
        }

        public bool HasKey
        {
            get
            {
                if (!HasPassword) return false;
                return variant != null && variant.IsValid(PasswordLength);
            }
        }

        public string KeyAsString()
        {
            int length = TypedLength(keyDigits);
            if (length == 0) return "(none)";

            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = (char)('0' + keyDigits[i]);
            return new string(chars);
        }
    }
}
