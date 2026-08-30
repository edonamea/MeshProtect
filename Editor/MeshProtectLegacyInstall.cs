#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// The previous release is still in the project, and importing this one did not remove it.
    ///
    /// lilToonMeshProtect installed to Assets/lilToonMeshProtect/ and MeshProtect installs to
    /// Assets/MeshProtect/. The rename kept every GUID so an existing component would stay bound
    /// across the upgrade - which is only safe if the old copy is GONE. A .unitypackage import
    /// never deletes anything, and legacyPackages in package.json only speaks to VCC, so a beta
    /// user who double-clicks the new package ends up with both.
    ///
    /// Nothing tells them. Measured, by installing v0.6.1 beside 1.0.0 in a real project: both
    /// assemblies compiled with zero errors, both registered an IVRCSDKPreprocessAvatarCallback at
    /// callbackOrder -1000, and Unity logged a GUID conflict for every file and silently
    /// renumbered one side - MeshProtectRoot.cs included, so the component on the avatar binds to
    /// whichever copy kept 11d021edd6eae644aba4bc51c91c9553. Which one that is follows import
    /// order, not chance, but the author can neither see it nor choose it.
    ///
    /// WHY THIS LOOKS FOR THE FILES AND NOT THE ASSEMBLY. Keying on the loaded assembly reads
    /// better, but it answers the wrong question. Two of this package's own look-ups resolve by
    /// FILE - MeshProtectShaderGen.PluginFolder() and LocateTemplateFolder() both take the first
    /// FindAssets hit for MeshProtectShaderGen.cs - and both installs match, in whatever order the
    /// AssetDatabase feels like. So the current release can write its generated tree into the old
    /// folder and build the avatar's shader family from v0.6.1 templates while the old assembly is
    /// not even loaded. The files are what does the damage; the assembly is one of the ways.
    ///
    /// All 24 pre-rename releases (v0.2.0 through beta.37) shipped the same asmdef name and the
    /// same install folder, so one lookup covers every version anyone can have.
    /// </summary>
    [InitializeOnLoad]
    internal static class MeshProtectLegacyInstall
    {
        private const string LegacyAssembly = "lilToonMeshProtect.Editor";
        private const string LegacyAsmdef = "lilToonMeshProtect";

        static MeshProtectLegacyInstall()
        {
            // Not from the static constructor itself: it runs during the domain reload, before the
            // AssetDatabase can be asked where anything lives.
            EditorApplication.delayCall += () => { if (Present) Warn(); };
        }

        /// <summary>The old release is in this project, compiled or not.</summary>
        internal static bool Present => Located() != null || AssemblyLoaded;

        private static bool AssemblyLoaded =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == LegacyAssembly);

        /// <summary>
        /// The folder to delete - not the .asmdef inside it, which is what the author would have
        /// had to work back from. Both of the old package's asmdefs sit one level down
        /// (Runtime/ and Editor/), so the folder is two directories up from either.
        /// </summary>
        private static string Located()
        {
            string asmdef = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset")
                                         .Select(AssetDatabase.GUIDToAssetPath)
                                         .FirstOrDefault(p => !string.IsNullOrEmpty(p) &&
                                             (Path.GetFileNameWithoutExtension(p) == LegacyAsmdef ||
                                              Path.GetFileNameWithoutExtension(p) == LegacyAssembly));
            if (asmdef == null) return null;

            // Two levels up holds for every layout this ever shipped in, and for nothing else. An
            // asmdef somebody flattened one level under a root resolves to "Assets" or "Packages",
            // and the sentence after this one tells the author to delete what it names. Answering
            // "I do not know" is the only safe reading of a hand-arranged install: the message
            // drops the location clause and still names the folder it is talking about.
            string folder = Path.GetDirectoryName(Path.GetDirectoryName(asmdef));
            if (string.IsNullOrEmpty(folder)) return null;
            folder = folder.Replace('\\', '/');
            return folder.IndexOf('/') < 0 ? null : folder;
        }

        /// <summary>
        /// English, like every other build warning. No "[MeshProtect] " prefix: Drain adds one to
        /// every report warning, and the console said it twice.
        ///
        /// The remedy is conditional on purpose. Whether the avatar's component survives the
        /// deletion depends on which copy Unity let keep the GUID, and telling everyone to re-add
        /// it would throw away the password and variant of the half whose component is fine.
        ///
        /// It also has to say "write the digits down" FIRST. The previous wording sent the other
        /// half - the ones whose component was bound to the old script - to delete the folder and
        /// only then discover a missing script, by which point the digits sit in scene YAML under
        /// a dead m_Script GUID and no inspector will draw them. Following it literally minted a
        /// new password for an avatar whose buyers already hold the old one. Every other place
        /// this tool mentions the password says to write it down; this was the one place that
        /// instructed a destructive step and did not.
        ///
        /// And the last action is 'Generate Password' then typing over what it rolls, not typing
        /// the password straight in: DrawPasswordBox draws no digit field until a key exists.
        /// </summary>
        internal static string Message
        {
            get
            {
                string where = Located();
                return "The older lilToonMeshProtect is still installed" +
                       (where == null ? "" : " at '" + where + "'") +
                       ". Importing MeshProtect did not remove it - they use different folders - so " +
                       "both are in this project, both add a step to every avatar build, and they " +
                       "share every asset GUID. FIRST read the six digits off the Mesh Protect Root " +
                       "and write them down: they may not survive what comes next. Then delete the " +
                       "old lilToonMeshProtect folder and restart Unity. If the component now reads " +
                       "'Missing (Mono Script)', add it again, press 'Generate Password', and type " +
                       "your six digits over the ones it rolls; if it still draws normally, there " +
                       "is nothing else to do.";
            }
        }

        internal static void Warn() => Debug.LogWarning("[MeshProtect] " + Message);
    }
}
#endif
