#if UNITY_EDITOR && LILMP_VRCSDK3_AVATARS
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace MeshProtect
{
    /// <summary>
    /// Keep the prepared copies current on their own, so the author never has to remember.
    ///
    /// The copies have to be made before the build, which means there is a window in which the
    /// author's controller and its protected copy can drift apart: add a toggle to the Base layer
    /// on Tuesday, upload on Wednesday, and the copy is of Tuesday morning. The build notices and
    /// falls back to the author's own controller, so nothing breaks and nothing is lost - but that
    /// layer ships readable, and the only way back is a button they have no reason to think about.
    ///
    /// A protection that quietly stops applying because someone edited an animator is not
    /// protection. So this watches for exactly that and redoes the copy in the editor, where doing
    /// it is safe. It makes the first copy too, for an avatar that has a password and none yet -
    /// the password is the request, and the alternative was a button whose entire job was to be
    /// remembered. An avatar with no password is left alone.
    ///
    /// It also never runs during a build. Creating these assets mid-build is the one thing three
    /// uploads established you cannot do, and a build writes assets, which is exactly what wakes
    /// this up.
    /// </summary>
    public class MeshProtectControllerWatcher : AssetPostprocessor
    {
        /// <summary>Set while the build hook owns the AssetDatabase. See the class comment.</summary>
        internal static bool BuildInProgress;

        private static bool queued;

        /// <summary>
        /// Avatars whose first copy failed, by instance id. Session-scoped on purpose: the reasons
        /// this fails - a controller another tool built in memory, a locked file, a project mid
        /// import - are the kind that a domain reload or an author doing something about it clears,
        /// and the Prepare Controllers button is never gated by this.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<int> failed =
            new System.Collections.Generic.HashSet<int>();

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted,
                                                   string[] moved, string[] movedFrom)
        {
            if (BuildInProgress || queued) return;

            // .asset as well as .controller: which parameters are safe to rename depends on the
            // expression parameter list and the menu tree, and both of those are .asset files. An
            // author who adds a parameter and uploads would otherwise find the copies quietly
            // dropped by the build's own safety check, over an edit nothing connects to this.
            bool worthChecking = imported.Concat(moved).Any(
                p => (p.EndsWith(".controller", System.StringComparison.OrdinalIgnoreCase) ||
                      p.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase)) &&
                     !IsOurs(p));
            if (!worthChecking) return;

            // Out of the import callback before touching the AssetDatabase: preparing imports
            // assets, and importing from inside an import notification is how Unity ends up
            // reporting assets in an inconsistent state.
            queued = true;
            EditorApplication.delayCall += Refresh;
        }

        /// <summary>
        /// Assets this tool wrote. Preparing copies controllers into the project, which arrives
        /// here as an import, which would prepare again.
        /// </summary>
        private static bool IsOurs(string path) =>
            path.Contains("/_Controllers/") || path.Contains("/_MeshProtectBuild/");

        private static void Refresh()
        {
            queued = false;

            // A build that never reached its postprocess callback - the SDK cancelled it, Unity
            // reloaded, the upload was abandoned at the dialog - leaves the flag set, and a flag
            // set forever means the copies quietly stop being maintained for the rest of the
            // session. The build's own folder is the evidence: gone means the build is over.
            if (BuildInProgress && !AssetDatabase.IsValidFolder(MeshProtectBuildHook.TempRoot))
                BuildInProgress = false;

            if (BuildInProgress || EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            foreach (var settings in Loaded())
            {
                // A password, and nothing else, is what makes this the author's request. It used to
                // be an existing copy - the first one had to be made by hand - which meant an author
                // who never found that button uploaded their own layers readable with nothing to say
                // so. A component with no password is still left alone: somebody dropped it on an
                // avatar to look at, and that is not a request.
                if (!settings.HasKey) continue;

                var descriptor = settings.GetComponentInParent<VRCAvatarDescriptor>();
                if (descriptor == null) continue;

                var state = MeshProtectControllers.Inspect(settings, descriptor, out string detail);
                if (state != MeshProtectControllers.State.Stale &&
                    state != MeshProtectControllers.State.NotPrepared)
                    continue;

                // A first copy that failed is not retried this session. Preparing runs off
                // .controller and .asset imports, which a real project produces all day, and an
                // avatar this cannot prepare cannot be prepared by trying again either: it fails,
                // records nothing, reads as NotPrepared, and comes straight back round. That is a
                // console filling with the same yellow line, which is a thing authors uninstall
                // over. Stale is deliberately not gated - the copies exist, so preparing them
                // worked once, and keeping them current is this class's whole job.
                if (state == MeshProtectControllers.State.NotPrepared &&
                    failed.Contains(settings.GetInstanceID()))
                    continue;

                var warnings = new System.Collections.Generic.List<string>();
                try
                {
                    int renamed = MeshProtectControllers.Prepare(settings, descriptor, warnings);

                    // Every warning, because this is now where preparing happens. The button is a
                    // fallback nobody has to find, so anything only it reported is not reported.
                    foreach (var warning in warnings) Debug.LogWarning("[MeshProtect] " + warning);

                    if (settings.preparedControllers.Count == 0)
                    {
                        // Nothing was copied. Saying the copies were remade here is how an avatar
                        // ships its own layers readable while the console says it is protected -
                        // Prepare has already said why in the warnings above.
                        failed.Add(settings.GetInstanceID());
                        continue;
                    }

                    failed.Remove(settings.GetInstanceID());
                    Debug.Log($"[MeshProtect] {(state == MeshProtectControllers.State.NotPrepared ? "This avatar had no protected copies yet" : detail)} - " +
                              $"they were made automatically ({renamed} object(s) renamed). Your own " +
                              "controllers were not modified.", settings);
                }
                catch (System.Exception e)
                {
                    failed.Add(settings.GetInstanceID());
                    foreach (var warning in warnings) Debug.LogWarning("[MeshProtect] " + warning);

                    // The button still works, and the build falls back to the author's own
                    // controllers either way, so this is a note rather than a failure.
                    Debug.LogWarning($"[MeshProtect] Could not make the protected copies " +
                                     $"automatically: {e.Message} Press Prepare Controllers on " +
                                     "the Mesh Protect Root component when convenient. This will " +
                                     "not be tried again until Unity restarts.", settings);
                }
            }
        }

        /// <summary>
        /// Components in the open scenes. Prefabs on disk are deliberately not searched: preparing
        /// one would write copies for an avatar nobody is working on, and there can be hundreds.
        /// </summary>
        private static MeshProtectRoot[] Loaded()
        {
            var found = new System.Collections.Generic.List<MeshProtectRoot>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    found.AddRange(root.GetComponentsInChildren<MeshProtectRoot>(true));
            }
            return found.ToArray();
        }
    }
}
#endif
