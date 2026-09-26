#if UNITY_EDITOR && LILMP_VRCSDK3_AVATARS
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace MeshProtect
{
    /// <summary>
    /// The whole user-facing workflow: add the component, press Build & Publish.
    ///
    /// Protection happens here, on the temporary clone the SDK builds from, so the avatar in the
    /// scene is never modified and there is no second avatar to keep track of. Remove the
    /// component and the next upload is a plain one - nothing to undo.
    ///
    /// ORDERING. callbackOrder is the one thing in this file that must not be changed casually.
    /// NDMF runs its entire pipeline at -11000 (resolving through transforming) and -1025
    /// (optimizing), and Modular Avatar, VRCFury and Avatar Optimizer all run inside it. Anything
    /// that merges meshes, welds vertices or recalculates normals after the bake would destroy the
    /// vertex identities the restore depends on, and the avatar would be unrecoverable with no
    /// obvious cause. Running at -1000 puts this strictly after all of them, which is a stronger
    /// guarantee than declaring plugin-ordering constraints: it does not depend on knowing every
    /// other tool's name.
    /// </summary>
    public class MeshProtectBuildHook : IVRCSDKPreprocessAvatarCallback
    {
        public int callbackOrder => -1000;

        /// <summary>
        /// Generated meshes, materials, clips and menus are written here for the duration of the
        /// build. Some of them (animator controllers, clips, menus) do not survive bundle
        /// serialisation reliably as in-memory objects, which is why they touch disk at all. The
        /// folder is deleted again once the upload finishes.
        ///
        /// Inside the plugin's own output root, like everything else this tool writes, so no
        /// folder ever flashes into existence at the project root. The last path segment keeps
        /// its distinctive name on purpose: the controller watcher recognises this tool's own
        /// asset traffic by that segment, wherever the root happens to be.
        /// </summary>
        internal static string TempRoot => MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild";

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            var roots = avatarGameObject.GetComponentsInChildren<MeshProtectRoot>(true);
            if (roots.Length > 1)
            {
                string locations = string.Join("\n", roots.Select(root =>
                {
                    string path = AnimationUtility.CalculateTransformPath(root.transform, avatarGameObject.transform);
                    return "- " + avatarGameObject.name + (string.IsNullOrEmpty(path) ? "" : "/" + path);
                }));
                return Block(new[] { MeshProtectL10n.Tr("build.multipleRoots", roots.Length, locations) });
            }
            var settings = roots.FirstOrDefault();

            // Asked before the early returns below, because the case it exists for is the one that
            // takes them. If the previous release kept the shared GUID when Unity renumbered the
            // collision, the avatar's component is lilToonMeshProtect.MeshProtectRoot - a different
            // type, in a different assembly - so the line above finds nothing here and the old
            // hook, registered at the same callbackOrder, is the one that builds the avatar. That
            // is exactly when the author most needs to be told, and it is the path where there is
            // no report to hang the warning on.
            bool legacy = MeshProtectLegacyInstall.Present;

            // No component means an unprotected avatar, which is none of our business. Protection
            // is only ever applied to the build clone, so a scene avatar never carries it.
            if (settings == null)
            {
                if (legacy) MeshProtectLegacyInstall.Warn();
                return true;
            }

            // An unticked component did nothing before, which is the opposite of what unticking a
            // component means everywhere else in Unity. The only way to upload one plain build -
            // to check whether this tool is behind some behaviour, say - was to delete the
            // component, and that takes the variant and the password with it.
            //
            // Skipping silently would be worse than not offering it at all: the whole point of this
            // tool is that an avatar cannot ship unprotected by accident. So it is loud.
            if (!settings.enabled)
            {
                Debug.LogWarning(
                    "[MeshProtect] The Mesh Protect Root component is unticked, so this upload is " +
                    "NOT protected - the mesh ships exactly as it is in your project. Tick it again " +
                    "before uploading anything you meant to protect.");

                if (legacy) MeshProtectLegacyInstall.Warn();

                // Still strip it. VRChat refuses to upload an avatar carrying a component type it
                // does not know, so leaving it behind turned "skip protection" into "cannot upload
                // at all" - and the error named this component without saying what to do about it.
                // Authoring data has no business in a bundle whether or not it was used.
                StripAuthoringComponents(avatarGameObject);
                return true;
            }

            // Apply strips the component, so keep what the checks afterwards need.
            var variant = settings.variant;

            string folder = null;

            // Held out here so the catch below can still read it. Apply used to make its own and
            // return it, which meant a build that stopped partway threw away every warning it had
            // collected on the way - including the ones that explain the failure.
            var report = new MeshProtectPipeline.Report();

            // On this path there IS a report, so it goes through that instead of straight to the
            // console: Drain logs it once, and it also reaches last-upload.txt, which is the copy
            // the author can send to somebody. It is added before Apply so a build that throws
            // part-way still carries it.
            if (legacy) report.warnings.Add(MeshProtectLegacyInstall.Message);

            // The watcher remakes stale copies when a controller is imported, and a build imports
            // plenty. Preparing them in the middle of one is the single thing this whole design
            // exists to avoid, so it is held off until the build is done with the AssetDatabase.
            MeshProtectControllerWatcher.BuildInProgress = true;

            try
            {
                folder = CreateTempFolder();
                MeshProtectPipeline.Apply(avatarGameObject, settings, folder, report);

                // Kept before Drain empties the list, because the dialog below needs to repeat
                // them: they are the only thing that says WHY nothing happened.
                var spoken = report.warnings.ToList();

                Drain(report);

                // Nothing could be protected.
                //
                // Everything below assumes protection happened: it prints the password the avatar
                // was keyed to, and then validates an unlock chain that was never built. Running
                // that here reports "expected six menu parameters, found none" to somebody whose
                // avatar simply has no lilToon material on it - the tool blaming them for its own
                // decision. So this path never reaches it.
                if (!report.appliedProtection)
                {
                    string reason = string.Join("\n\n", spoken.Distinct().Take(6));

                    // The one case where not protecting is the right answer rather than a failure
                    // to reach one: an Android build cannot carry the decode at all, so the plain
                    // bundle IS the correct Quest bundle. Stopping here would put back the "untick
                    // for Quest, tick it again for PC" dance, and an author who forgets its second
                    // half ships an unprotected PC avatar - the thing this tool exists to prevent.
                    if (report.skipWasCorrect)
                    {
                        WriteReport(settings, avatarGameObject, report, spoken, false,
                                    "This was an Android (Quest) build, where that is the right " +
                                    "result, so the upload went ahead. The PC upload is protected " +
                                    "as usual.");
                        StripAuthoringComponents(avatarGameObject);
                        return true;
                    }

                    // Everything else: somebody installed a protection tool, set a password and
                    // pressed Build & Publish - and would get an avatar with no protection on it,
                    // plus a successful upload to say otherwise. This used to carry on, and the way
                    // it was found out was in game, when the unlock menu was not there, after a
                    // full build and an upload. It stops here instead, before the expensive part.
                    //
                    // There is still a way past, because "cannot be protected" is a permanent
                    // property of some avatars - every material Poiyomi, say - and refusing those
                    // forever, with the only escape being to find and untick a component, is the
                    // trap the rest of this file was rewritten to remove. The difference is that
                    // going ahead is now a deliberate answer to a dialog that says, in the one
                    // place it cannot be scrolled past, that NOTHING was protected.
                    string reportPath = WriteReport(settings, avatarGameObject, report, spoken,
                                                    false, "The upload was STOPPED over it.");

                    Debug.LogError(
                        "[MeshProtect] NOTHING ON THIS AVATAR WAS PROTECTED, so the upload was " +
                        "stopped before the build. " + reason);

                    // Batch mode has nobody to ask and takes the safe answer: every automated build
                    // that reaches here is one that would otherwise have shipped unprotected.
                    bool stop = Application.isBatchMode || EditorUtility.DisplayDialog(
                        MeshProtectL10n.Tr("dialog.stop.title"),
                        MeshProtectL10n.Tr("dialog.stop.body",
                            reason.Length > 0 ? reason + "\n\n" : "",
                            reportPath == null
                                ? ""
                                : MeshProtectL10n.Tr("dialog.stop.report", reportPath)),
                        MeshProtectL10n.Tr("dialog.stop.stop"),
                        MeshProtectL10n.Tr("dialog.stop.continue"));

                    if (stop)
                    {
                        // The postprocess callback that normally clears these only runs for a build
                        // that got as far as being packaged, and this one did not.
                        CleanUp(folder);
                        MeshProtectControllerWatcher.BuildInProgress = false;
                        return false;
                    }

                    Debug.LogWarning(
                        "[MeshProtect] Uploading UNPROTECTED at the author's request - the mesh " +
                        "ships exactly as it is and there is no unlock menu in game.");
                    WriteReport(settings, avatarGameObject, report, spoken, false,
                                "You chose 'Upload it unprotected', so the avatar went out " +
                                "exactly as it is.");
                    StripAuthoringComponents(avatarGameObject);
                    return true;
                }

                // The password goes first, and it is the password this upload actually used rather
                // than the one on screen. An author typed a new one, pressed Build & Publish without
                // committing the field, and shipped an avatar keyed to the previous password - twice
                // in a row, because the second upload then shipped the first new one. Nothing said
                // anything: from the build's point of view it succeeded, and it had. Finding out
                // costs an upload and a trip into the game, and the way back is another upload.
                Debug.Log($"[MeshProtect] Uploading with password check " +
                          $"{MeshProtectCipher.CheckCode(settings.keyDigits, settings.variant)} - the " +
                          "Mesh Protect Root component shows the same four characters next to the " +
                          "password. If they do not match, stop the upload. " +
                          $"Protected {report.meshCount} mesh(es) across {report.rendererCount} " +
                          $"renderer(s), mode {report.mode}, worst restore error " +
                          $"{report.worstRestoreError:E3} m, {report.blendShapeFramesChecked} " +
                          "blend shape frame(s) checked." +
                          // The one line an author reads if they read one. Without this it says
                          // "Protected 12 mesh(es)" on an avatar where nine renderers were left
                          // alone, and every reason is a warning further up that they scrolled past.
                          (report.skippedRenderers > 0
                              ? $" {report.skippedRenderers} renderer(s) were LEFT UNPROTECTED and " +
                                "will be visible while the avatar is locked - the warnings above " +
                                "say which and why."
                              : "") +
                          (report.unprotectedSubMeshes > 0
                              ? $" {report.unprotectedSubMeshes} sub-mesh(es) on " +
                                $"{report.unprotectedSubMeshRenderers} renderer(s) ship " +
                                "UNPROTECTED and stay visible while the avatar is locked - the " +
                                "warnings above say which and why."
                              : ""));

                WriteReport(settings, avatarGameObject, report, spoken, true);

                AssetDatabase.SaveAssets();

                bool allowed = ValidateOnly(avatarGameObject, variant);

                // Blocking returns false, the build stops, and the postprocess callback that
                // normally removes this folder never runs. The exception path below already
                // cleans up; a refusal is just as much a build that did not happen.
                if (!allowed)
                {
                    CleanUp(folder);
                    MeshProtectControllerWatcher.BuildInProgress = false;
                }
                return allowed;
            }
            catch (Exception e)
            {
                Drain(report);
                CleanUp(folder);
                MeshProtectControllerWatcher.BuildInProgress = false;
                // The dialog gets the message; the console gets the stack trace. Without the trace
                // an unexpected failure here is undiagnosable, and "Object reference not set to an
                // instance of an object" is not something a user can act on.
                Debug.LogException(e);
                return Block(new[] { e.Message });
            }
        }

        /// <summary>
        /// Run the pre-upload checks and refuse the build on anything fatal. This is the last
        /// thing standing between a mistake and a published avatar that is broken, or worse, one
        /// that ships its own password.
        /// </summary>
        private static bool ValidateOnly(GameObject avatarGameObject, MeshProtectVariant variant)
        {
            bool hasProtectedMaterial = avatarGameObject
                .GetComponentsInChildren<Renderer>(true)
                .SelectMany(r => r.sharedMaterials)
                .Any(m => MeshProtectPipeline.IsProtectShader(m, variant.shaderName));

            if (!hasProtectedMaterial) return true;

            int cleared = MeshProtectValidator.ClearKeys(avatarGameObject, variant);
            if (cleared > 0)
            {
                Debug.LogWarning($"[MeshProtect] Reset digit properties on {cleared} material(s) before " +
                                 "upload. They had been left on a non-zero value, which would have " +
                                 "shipped part of the password.");
            }

            var issues = MeshProtectValidator.Validate(avatarGameObject, variant);

            foreach (var issue in issues.Where(i => !i.fatal))
                Debug.LogWarning("[MeshProtect] " + issue.message);

            var fatal = issues.Where(i => i.fatal).Select(i => i.message).ToArray();
            return fatal.Length == 0 || Block(fatal);
        }

        /// <summary>
        /// Remove the authoring component from the build clone.
        ///
        /// It holds the password, and VRChat rejects an avatar carrying a component type it does
        /// not recognise, so it must be gone either way. The protected path does this at the end of
        /// Apply, once everything that needed the password has run.
        /// </summary>
        private static void StripAuthoringComponents(GameObject avatarGameObject)
        {
            foreach (var stray in avatarGameObject.GetComponentsInChildren<MeshProtectRoot>(true))
                UnityEngine.Object.DestroyImmediate(stray);
        }

        /// <summary>
        /// Write what this upload did to a file, and hand back the path.
        ///
        /// Because the Console is not readable when it matters. The SDK panel is modal while the
        /// preprocessors run, so nothing printed here can be seen at the time; afterwards it is
        /// there, but somewhere below several hundred lines from the SDK, NDMF and whatever else
        /// the avatar uses, and the one line that mattered is not the one that stayed on screen.
        /// That is how an author ended up finding out that nothing had been protected by going into
        /// the game and noticing the unlock menu was missing.
        ///
        /// So it goes in a file, under this tool's own folder, overwritten each upload. It can be
        /// opened at any time, and it can be sent to somebody - which the Console cannot.
        ///
        /// Never allowed to break a build: an upload that failed because a log file could not be
        /// written would be an absurd way to lose an avatar.
        /// </summary>
        private static string WriteReport(MeshProtectRoot settings, GameObject avatar,
                                          MeshProtectPipeline.Report report,
                                          System.Collections.Generic.List<string> warnings,
                                          bool protectedIt, string outcome = null)
        {
            try
            {
                string folder = MeshProtectShaderGen.OutputRoot(settings);

                Directory.CreateDirectory(folder);
                string path = folder + "/last-upload.txt";

                var text = new System.Text.StringBuilder();
                text.AppendLine("MeshProtect - what the last upload did");
                text.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                text.AppendLine("avatar: " + (avatar == null ? "?" : avatar.name));
                text.AppendLine();

                if (!protectedIt)
                {
                    text.AppendLine("NOTHING WAS PROTECTED. No mesh was changed, and the avatar has");
                    text.AppendLine("no unlock menu in game.");
                    // Which of the three ways this ended matters more than the fact itself: the
                    // upload stopped, the author waved it through, or it was a Quest build where
                    // this is simply the right answer. A file that only said "nothing was
                    // protected" left the reader guessing which.
                    if (!string.IsNullOrEmpty(outcome)) text.AppendLine(outcome);
                }
                else
                {
                    text.AppendLine($"Protected {report.meshCount} mesh(es) across " +
                                    $"{report.rendererCount} renderer(s), mode {report.mode}.");
                    text.AppendLine($"Password check: " +
                                    MeshProtectCipher.CheckCode(settings.keyDigits, settings.variant) +
                                    "  (the component shows the same four characters)");
                    if (report.skippedRenderers > 0)
                        text.AppendLine($"{report.skippedRenderers} renderer(s) were left " +
                                        "unprotected and will be visible while the avatar is locked.");
                    if (report.unprotectedSubMeshes > 0)
                        text.AppendLine($"{report.unprotectedSubMeshes} sub-mesh(es) on " +
                                        $"{report.unprotectedSubMeshRenderers} renderer(s) ship " +
                                        "unprotected and stay visible while the avatar is locked.");

                    // The roster answers the question every screenshot round-trip stalls on:
                    // which renderer is this, and what did the build do to it?
                    if (report.roster.Count > 0)
                    {
                        text.AppendLine();
                        text.AppendLine($"Every protected renderer ({report.roster.Count}):");
                        foreach (var line in report.roster)
                            text.AppendLine("  " + line);
                    }
                }

                text.AppendLine();
                if (warnings == null || warnings.Count == 0)
                {
                    text.AppendLine("Nothing else to report.");
                }
                else
                {
                    text.AppendLine($"{warnings.Count} note(s):");
                    foreach (var warning in warnings)
                    {
                        text.AppendLine();
                        text.AppendLine("  - " + warning);
                    }
                }

                File.WriteAllText(path, text.ToString());
                return path;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MeshProtect] Could not write the upload report: " + e.Message);
                return null;
            }
        }

        private static void Drain(MeshProtectPipeline.Report report)
        {
            foreach (var warning in report.warnings)
                Debug.LogWarning("[MeshProtect] " + warning);
            report.warnings.Clear();
        }

        private static string CreateTempFolder()
        {
            // Level by level: the root now sits inside the plugin folder, and
            // AssetDatabase.CreateFolder makes exactly one level per call.
            string built = null;
            foreach (string segment in TempRoot.Split('/'))
            {
                string next = built == null ? segment : built + "/" + segment;
                if (built != null && !AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(built, segment);
                built = next;
            }

            string name = Guid.NewGuid().ToString("N").Substring(0, 8);
            AssetDatabase.CreateFolder(TempRoot, name);
            return TempRoot + "/" + name;
        }

        private static void CleanUp(string folder)
        {
            if (!string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder))
                AssetDatabase.DeleteAsset(folder);
        }

        private static bool Block(string[] messages)
        {
            foreach (var message in messages) Debug.LogError("[MeshProtect] " + message);

            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog(
                    MeshProtectL10n.Tr("dialog.blocked.title"),
                    MeshProtectL10n.Tr("dialog.blocked.body",
                        string.Join("\n\n", messages.Select(m => "- " + m))),
                    "OK");

            return false;
        }
    }

    /// <summary>
    /// Removes the build's scratch assets once the upload is done.
    ///
    /// Deliberately separate from the preprocess hook: the bundle is not built until after every
    /// preprocess callback returns, so deleting these any earlier would pull the meshes out from
    /// under the avatar being packaged.
    /// </summary>
    public class MeshProtectBuildCleanup : IVRCSDKPostprocessAvatarCallback
    {
        public int callbackOrder => 0;

        public void OnPostprocessAvatar()
        {
            // Released here rather than when the preprocess returns, because the bundle is packed
            // in between and the deletion below is itself an AssetDatabase change. This callback
            // only runs for a build that got that far, so the preprocess releases it itself on the
            // paths that stop one - and the watcher releases it if neither happened, because a
            // flag stuck on means the copies quietly stop being kept up to date for the session.
            MeshProtectControllerWatcher.BuildInProgress = false;

            // The current root, and the pre-1.0 one at the project root - a build that crashed
            // under an older version leaves the old folder behind, and this is the only sweeper.
            bool removed = false;
            foreach (string root in new[] { MeshProtectBuildHook.TempRoot, "Assets/_MeshProtectBuild" })
            {
                if (!AssetDatabase.IsValidFolder(root)) continue;
                AssetDatabase.DeleteAsset(root);
                removed = true;
            }
            if (removed) AssetDatabase.Refresh();
        }
    }
}
#endif
