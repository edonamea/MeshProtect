#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// The entire user-facing surface: a password, and a note about where to type it.
    ///
    /// There is no Protect button and no Validate button any more. Protection happens during
    /// Build &amp; Publish, on the clone the SDK builds from, so there is nothing to press, nothing
    /// to keep in sync, and no second avatar in the scene to upload by mistake. The checks that
    /// used to live behind Validate now run inside that build and stop it on failure, which is
    /// strictly better: a check nobody remembers to press protects nobody.
    /// </summary>
    [CustomEditor(typeof(MeshProtectRoot))]
    public class MeshProtectRootEditor : Editor
    {
        private static readonly string[] HiddenProperties =
        {
            "m_Script", "keyDigits", "variant",
            // Drawn by DrawParameterBox, which also has to react to them being changed.
            "obfuscateParameterNames", "obfuscateExpressionParameters", "copySeparateBlendTrees",
            "obfuscateObjectNames",
            // Generated, not authored. Editing any of these from the inspector puts the copies and
            // the record of them out of step - clearing preparedSurfaceHash alone is enough to have
            // every repaint report the copies as stale and the watcher remake them for nothing.
            "renamedParameters", "renamedObjects", "preparedWithParameterNames",
            "preparedWithObjectNames", "preparedWithExpressionParameters",
            "preparedWithSeparateBlendTrees", "preparedSurfaceHash",
            // Including copyHash, which is the one record here that Undo cannot reach. A text field
            // an author can edit takes that back: the check it feeds exists precisely because the
            // component can be rolled back and the files on disk cannot.
            "preparedControllers",
            // Drawn by DrawSettingsFields with localized labels and tooltips. The generic pass
            // after it stays as the safety net for any field added later and not yet listed.
            "distortRatio", "mode", "recalculateMissingTangents", "attenuateAtJoints",
            "rigidityExponent", "obfuscateAnimatorNames", "targetRenderers", "ignoredMaterials",
            "invisibleMaterials", "outputFolder",
        };
        private static bool showAdvanced;

        /// <summary>What is in the text field right now, which is not yet what is committed.</summary>
        private string draft;
        private string draftError;

        public override void OnInspectorGUI()
        {
            var settings = (MeshProtectRoot)target;

            MeshProtectL10n.DrawSelector();
            EditorGUILayout.Space(2);

            DrawEnableToggle(settings);
            EditorGUILayout.Space();

            DrawPasswordBox(settings);
            EditorGUILayout.Space();

            DrawAutoFixWarning();
            EditorGUILayout.Space();

            // Everything below the fold. The released version's whole surface was a switch, a
            // password and a note, and the obfuscation work put two more boxes in front of the
            // author - one of them with a button they had to press for the protection to be
            // complete. Both are still here, because an author who wants to turn a piece off or
            // read the report needs somewhere to do it, but neither is part of using the tool: the
            // options are on by default and the copies are made as soon as there is a password.
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, MeshProtectL10n.Tr("advanced"), true);
            if (showAdvanced)
            {
                serializedObject.Update();
                DrawSettingsFields(serializedObject);
                DrawPropertiesExcluding(serializedObject, HiddenProperties);
                serializedObject.ApplyModifiedProperties();

#if LILMP_VRCSDK3_AVATARS
                if (settings.HasKey)
                {
                    EditorGUILayout.Space();
                    DrawControllerBox(settings);
                    EditorGUILayout.Space();
                    DrawParameterBox(settings);
                }
#endif

                EditorGUILayout.Space();
                DrawAdvancedActions(settings);
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(MeshProtectL10n.Tr("footer"), MessageType.Info);
        }

        /// <summary>
        /// The on/off switch, drawn here because Unity will not draw it.
        ///
        /// Unity only puts an enable checkbox on a MonoBehaviour's header when the script defines
        /// one of its message methods - Start, Update, OnEnable and so on. This component is pure
        /// serialised data and defines none, so the header has no checkbox and the `enabled` flag,
        /// which the build hook honours, was unreachable from the inspector. Adding an empty Start
        /// purely to make Unity draw a checkbox would work and would also be a lie about what the
        /// component does; a labelled toggle says what it is for.
        /// </summary>
        private void DrawEnableToggle(MeshProtectRoot settings)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool on = EditorGUILayout.ToggleLeft(
                    MeshProtectL10n.Tr("enable.toggle"), settings.enabled,
                    EditorStyles.boldLabel);

                if (on != settings.enabled)
                {
                    Undo.RecordObject(settings, on ? "Enable Mesh Protect" : "Disable Mesh Protect");
                    settings.enabled = on;
                    EditorUtility.SetDirty(settings);
                }

                if (!settings.enabled)
                {
                    EditorGUILayout.HelpBox(MeshProtectL10n.Tr("enable.off"), MessageType.Warning);
                }
            }
        }

        /// <summary>
        /// The SDK lists every component type it does not recognise and offers a button that
        /// deletes them from the scene. Pressing it here removes this component, and the password
        /// and the generated algorithm go with it - the next upload is a plain, unprotected avatar.
        ///
        /// The error itself is harmless: it describes the scene, and the build hook strips this
        /// component from the temporary clone before the avatar is packaged, so it never reaches
        /// the upload. But a red error next to a one-click fix is a trap, and the fix is the one
        /// thing a user must not do. Saying so here is the only place they will read it in time.
        ///
        /// Moving the component onto a child tagged EditorOnly makes the SDK stop listing it -
        /// measured, it flags nothing and the hook still finds the component. That is not done,
        /// because whether the build strips EditorOnly objects before this hook runs at -1000 is
        /// not established, and if it does the component is gone by then and this tool treats a
        /// missing component as "the user did not install it" and uploads unprotected in silence.
        /// A quieter inspector is not worth that.
        /// </summary>
        private static void DrawAutoFixWarning()
        {
            EditorGUILayout.HelpBox(MeshProtectL10n.Tr("autofix"), MessageType.Info);
        }

#if LILMP_VRCSDK3_AVATARS
        /// <summary>
        /// The avatar's own Base, Gesture and Action layers: renamed too, or left readable.
        ///
        /// This is a button rather than something the upload does, and that is not a UI preference.
        /// Renaming inside those controllers means copying them first, and copying them during the
        /// upload produced an avatar that held a pose it was not in and could not stand up - three
        /// uploads of one avatar, differing only in when the copy was made, and no explanation
        /// found for why. So the copies are made here, before the build, and the build does nothing
        /// but point at them.
        ///
        /// Editing a controller afterwards would mean the upload carried the older version, so the
        /// build falls back to the author's own controller: the upload always works, and that
        /// layer ships readable. The copies are also remade automatically whenever a controller is
        /// imported, so this box mostly reports state - and is there to press on the rare occasion
        /// the automatic pass could not.
        /// </summary>
        private static void DrawControllerBox(MeshProtectRoot settings)
        {
            var descriptor = settings.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            var state = MeshProtectControllers.Inspect(settings, descriptor, out string detail);
            if (state == MeshProtectControllers.State.NothingToPrepare) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(MeshProtectL10n.Tr("controllers.title"), EditorStyles.boldLabel);

                switch (state)
                {
                    case MeshProtectControllers.State.UpToDate:
                        EditorGUILayout.HelpBox(
                            MeshProtectL10n.Tr("controllers.uptodate", detail), MessageType.Info);
                        break;

                    case MeshProtectControllers.State.Stale:
                        EditorGUILayout.HelpBox(
                            MeshProtectL10n.Tr("controllers.stale", detail), MessageType.Warning);
                        break;

                    default:
                        EditorGUILayout.HelpBox(
                            MeshProtectL10n.Tr("controllers.notprepared"), MessageType.None);
                        break;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(state == MeshProtectControllers.State.NotPrepared
                                             ? MeshProtectL10n.Tr("controllers.prepare")
                                             : MeshProtectL10n.Tr("controllers.prepareagain")))
                        PrepareControllers(settings, descriptor);

                    using (new EditorGUI.DisabledScope(settings.preparedControllers.Count == 0))
                        if (GUILayout.Button(MeshProtectL10n.Tr("controllers.remove")))
                        {
                            Undo.RecordObject(settings, "Remove Prepared Controllers");
                            MeshProtectControllers.Clear(settings);
                            Debug.Log("[MeshProtect] Removed the prepared controllers. Your own " +
                                      "layers will ship with their original names.");
                        }
                }
            }
        }

        /// <summary>
        /// Parameter renaming, and the report that says what was left alone.
        ///
        /// The report is the point of the box. Kanna's answer to the same problem is a list of
        /// regular expressions the author fills in and hopes is complete; this one goes the other
        /// way - it renames only what it can account for - and the useful thing to show is therefore
        /// the refusals. An author who sees "kept 'FaceLock' because a blend tree in its own file
        /// uses it" can move that tree and get the parameter back, and one who sees a name they know
        /// their OSC app uses can tick the option off before uploading rather than after.
        ///
        /// Changing any of these remakes the copies straight away. The map is baked into them, so a
        /// changed option that only took effect at the next manual press would leave the copies
        /// saying one thing and the checkbox another.
        /// </summary>
        private static void DrawParameterBox(MeshProtectRoot settings)
        {
            var descriptor = settings.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(MeshProtectL10n.Tr("params.title"), EditorStyles.boldLabel);

                if (!settings.obfuscateAnimatorNames)
                {
                    EditorGUILayout.HelpBox(MeshProtectL10n.Tr("params.off"), MessageType.None);
                    return;
                }

                bool rename = EditorGUILayout.ToggleLeft(
                    MeshProtectL10n.Tr("params.rename"), settings.obfuscateParameterNames);

                bool expressions = settings.obfuscateExpressionParameters;
                bool trees = settings.copySeparateBlendTrees;

                using (new EditorGUI.DisabledScope(!rename))
                using (new EditorGUI.IndentLevelScope())
                {
                    expressions = EditorGUILayout.ToggleLeft(
                        MeshProtectL10n.Tr("params.expressions"), expressions);
                    trees = EditorGUILayout.ToggleLeft(
                        MeshProtectL10n.Tr("params.trees"), trees);
                }

                bool objects = EditorGUILayout.ToggleLeft(
                    MeshProtectL10n.Tr("params.objects"),
                    settings.obfuscateObjectNames);

                if (rename != settings.obfuscateParameterNames ||
                    expressions != settings.obfuscateExpressionParameters ||
                    trees != settings.copySeparateBlendTrees ||
                    objects != settings.obfuscateObjectNames)
                {
                    Undo.RecordObject(settings, "Change Mesh Protect Parameter Options");
                    settings.obfuscateParameterNames = rename;
                    settings.obfuscateExpressionParameters = expressions;
                    settings.copySeparateBlendTrees = trees;
                    settings.obfuscateObjectNames = objects;
                    EditorUtility.SetDirty(settings);

                    // Only when copies already exist. Preparing an avatar that never asked for it
                    // would write into the author's project because they ticked a checkbox.
                    //
                    // Out of the GUI pass before touching the AssetDatabase: preparing imports
                    // assets and puts a progress bar up, and doing that in the middle of laying out
                    // an inspector is how Unity ends up reporting mismatched layout groups. The
                    // watcher defers for the same reason.
                    if (settings.preparedControllers.Count > 0)
                        EditorApplication.delayCall += () =>
                        {
                            if (settings != null) PrepareControllers(settings, descriptor);
                        };
                }

                if (settings.obfuscateExpressionParameters)
                {
                    EditorGUILayout.HelpBox(MeshProtectL10n.Tr("params.expwarn"), MessageType.Warning);
                }

                if (settings.renamedObjects.Count > 0)
                {
                    EditorGUILayout.LabelField(
                        MeshProtectL10n.Tr("params.objcount", settings.renamedObjects.Count),
                        EditorStyles.miniLabel);
                }

                if (settings.renamedParameters.Count > 0)
                {
                    // Counted as the author counts them: a PhysBone prefix is one parameter here
                    // and nine names in the map.
                    var map = settings.renamedParameters
                        .Where(r => !string.IsNullOrEmpty(r.original))
                        .ToDictionary(r => r.original, r => r.obfuscated);

                    EditorGUILayout.LabelField(
                        MeshProtectL10n.Tr("params.paramcount", MeshProtectParameters.DistinctParameters(map)),
                        EditorStyles.miniLabel);
                }
                else if (settings.obfuscateParameterNames && settings.preparedControllers.Count > 0)
                {
                    EditorGUILayout.HelpBox(MeshProtectL10n.Tr("params.nothing"), MessageType.None);
                }

                // Stacked, not side by side: half an inspector is not enough for these captions
                // in most of the four languages, and a clipped caption hides which report is which.
                if (GUILayout.Button(MeshProtectL10n.Tr("params.reportparams")))
                    LogParameterReport(settings, descriptor);

                // Not behind the checkbox. The report is most worth reading before the option is
                // ticked, by an author deciding whether renaming objects is going to cost them
                // anything on this particular avatar.
                if (GUILayout.Button(MeshProtectL10n.Tr("params.reportobjects")))
                    LogObjectReport(settings, descriptor);
            }
        }

        private static void LogParameterReport(
            MeshProtectRoot settings, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor)
        {
            var options = MeshProtectControllers.SurveyOptions(settings, descriptor);
            var findings = MeshProtectParameters.Survey(descriptor, options);

            var renamed = findings.Where(f => f.blockedBy == null).ToList();
            var kept = findings.Where(f => f.blockedBy != null).ToList();

            var text = new System.Text.StringBuilder();
            text.AppendLine($"[MeshProtect] Parameter report for '{descriptor.name}': " +
                            $"{renamed.Count} renameable, {kept.Count} kept.");

            text.AppendLine();
            text.AppendLine("KEPT, and why:");
            foreach (var group in kept.GroupBy(f => f.blockedBy).OrderByDescending(g => g.Count()))
            {
                text.AppendLine($"  {group.Count()}x  {group.Key}");
                foreach (var finding in group.Take(12)) text.AppendLine("        " + finding.name);
                if (group.Count() > 12) text.AppendLine("        ...");
            }

            text.AppendLine();
            text.AppendLine("RENAMED: " + string.Join(", ", renamed.Select(f => f.name)));

            Debug.Log(text.ToString(), settings);
        }

        /// <summary>
        /// The same report for object names, which needs one thing the parameter report does not.
        ///
        /// A name is the unit, not an object: every object called "Cube" gets the same replacement,
        /// so a path is rebuilt segment by segment and one refusal anywhere covers all of them. An
        /// author reading "kept 'Hair' because a mask lists it" and looking at three objects called
        /// Hair is otherwise entitled to think two of them were missed.
        ///
        /// The renamed side is a count and a sample rather than a list. On a normal avatar it runs
        /// to several hundred names, and a Console entry that long is one nobody reads to the end -
        /// while the refusals, which are the part an author can do something about, are short.
        /// </summary>
        private static void LogObjectReport(
            MeshProtectRoot settings, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor)
        {
            var options = MeshProtectControllers.ObjectSurveyOptions(settings, descriptor);
            var findings = MeshProtectObjectNames.Survey(descriptor, options);

            var renamed = findings.Where(f => f.blockedBy == null).ToList();
            var kept = findings.Where(f => f.blockedBy != null).ToList();

            var text = new System.Text.StringBuilder();
            text.AppendLine($"[MeshProtect] Object name report for '{descriptor.name}': " +
                            $"{renamed.Count} renameable, {kept.Count} kept. These are names, not " +
                            "objects - every object sharing a name shares its answer.");

            if (!settings.obfuscateObjectNames)
                text.AppendLine("Object renaming is switched off, so this is what it would do.");

            text.AppendLine();
            text.AppendLine("KEPT, and why:");
            foreach (var group in kept.GroupBy(f => f.blockedBy).OrderByDescending(g => g.Count()))
            {
                text.AppendLine($"  {group.Count()}x  {group.Key}");
                foreach (var finding in group.Take(12)) text.AppendLine("        " + finding.name);
                if (group.Count() > 12) text.AppendLine("        ...");
            }

            text.AppendLine();
            text.Append($"RENAMED: {renamed.Count} name(s)");
            if (renamed.Count > 0)
                text.Append(", such as " + string.Join(", ", renamed.Take(15).Select(f => f.name)) +
                            (renamed.Count > 15 ? ", ..." : ""));

            Debug.Log(text.ToString(), settings);
        }

        /// <summary>
        /// Make the copies the moment there is a password, so there is nothing to press.
        ///
        /// The copies have to exist before Build &amp; Publish - three uploads established that
        /// creating them mid-build does not work - so something has to make them in the editor. It
        /// used to be a button, and a button is a step: an author who never found it uploaded with
        /// their Base, Gesture and Action layers readable and nothing said so, because from the
        /// build's point of view nothing had failed.
        ///
        /// Setting a password is the author asking for protection, and it is the only thing this
        /// tool asks them to do. That is taken as the invitation to write the copies - into this
        /// avatar's own folder under the plugin's output root, never anywhere else, and never
        /// touching what is already there. Without a password nothing happens at all: a component
        /// someone dropped on an avatar to look at is not a request.
        /// </summary>
        private static void EnsurePrepared(MeshProtectRoot settings)
        {
            if (settings == null || !settings.HasKey) return;
            if (settings.preparedControllers.Count > 0) return;

            var descriptor = settings.GetComponentInParent<
                VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null) return;

            // NotPrepared and nothing else. NothingToPrepare means this avatar has no layers of its
            // own to copy, and Stale is the watcher's business - it knows how to say what changed.
            if (MeshProtectControllers.Inspect(settings, descriptor, out _) !=
                MeshProtectControllers.State.NotPrepared) return;

            // Not through PrepareControllers, which belongs to the button. It puts a progress bar
            // up, and this runs off a keystroke in the password field: the bar takes focus, and the
            // rest of the password goes into nothing. It also shows a modal dialog on failure,
            // which is not something to do to somebody who is typing. Same shape as the watcher.
            var warnings = new System.Collections.Generic.List<string>();
            try
            {
                Undo.RecordObject(settings, "Prepare Mesh Protect Controllers");
                int renamed = MeshProtectControllers.Prepare(settings, descriptor, warnings);

                foreach (var warning in warnings) Debug.LogWarning("[MeshProtect] " + warning);

                // Nothing copied is not something to announce as a success - Prepare has already
                // said why in the warnings above.
                if (settings.preparedControllers.Count > 0)
                    Debug.Log("[MeshProtect] Prepared this avatar's own controllers and renamed " +
                              $"{renamed} object(s) inside the copies. Your scene was not modified.",
                              settings);
            }
            catch (System.Exception e)
            {
                foreach (var warning in warnings) Debug.LogWarning("[MeshProtect] " + warning);
                Debug.LogWarning("[MeshProtect] Could not make the protected copies automatically: " +
                                 e.Message + " Press Prepare Controllers under Advanced when " +
                                 "convenient.", settings);
            }
        }

        private static void PrepareControllers(MeshProtectRoot settings,
                                               VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor)
        {
            if (descriptor == null)
            {
                EditorUtility.DisplayDialog(MeshProtectL10n.Tr("progress.title"),
                    MeshProtectL10n.Tr("dialog.noavatar"), "OK");
                return;
            }

            var warnings = new System.Collections.Generic.List<string>();
            try
            {
                EditorUtility.DisplayProgressBar(MeshProtectL10n.Tr("progress.title"),
                    MeshProtectL10n.Tr("progress.controllers"), 0.5f);

                Undo.RecordObject(settings, "Prepare Mesh Protect Controllers");
                int renamed = MeshProtectControllers.Prepare(settings, descriptor, warnings);

                foreach (var warning in warnings) Debug.LogWarning("[MeshProtect] " + warning);
                Debug.Log($"[MeshProtect] Prepared this avatar's own controllers and renamed " +
                          $"{renamed} object(s) inside the copies. Your scene was not modified.");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog(MeshProtectL10n.Tr("progress.title"), e.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
#endif

        /// <summary>
        /// Out of the GUI pass before anything touches the AssetDatabase. Preparing imports assets
        /// and puts a progress bar up, and doing either while an inspector is being laid out is how
        /// Unity ends up reporting mismatched layout groups. Without the avatar SDK there are no
        /// playable layers to copy and this does nothing at all.
        /// </summary>
        private static void PrepareAfterThisRepaint(MeshProtectRoot settings)
        {
#if LILMP_VRCSDK3_AVATARS
            EditorApplication.delayCall += () => EnsurePrepared(settings);
#endif
        }

        private void DrawPasswordBox(MeshProtectRoot settings)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!settings.HasKey)
                {
                    EditorGUILayout.HelpBox(MeshProtectL10n.Tr("password.none"), MessageType.Warning);

                    if (GUILayout.Button(MeshProtectL10n.Tr("password.generate"), GUILayout.Height(28)))
                    {
                        Regenerate(settings);
                        draft = settings.KeyAsString();
                        draftError = null;
                        PrepareAfterThisRepaint(settings);
                    }
                    return;
                }

                EditorGUILayout.LabelField(MeshProtectL10n.Tr("password.title"), EditorStyles.boldLabel);

                var style = new GUIStyle(EditorStyles.textField)
                {
                    fontSize = 22,
                    alignment = TextAnchor.MiddleCenter,
                    fixedHeight = 34
                };

                // NOT delayed, and this is the second attempt at it. A delayed field only commits on
                // Enter or when it loses focus, so what the author reads on screen and what the
                // upload uses are two different things, and the gap is invisible: type a new
                // password, press Build & Publish, and the avatar ships keyed to the previous one.
                // It happened, twice in a row - the second upload shipped the password typed before
                // the first - and the only way to find out is to fail to unlock in game.
                //
                // The comment here used to say the delay bought nothing because nothing on the way
                // to a password parses - "1", "11111" - which was true while a password was exactly
                // six digits and stopped being true the moment one to six were allowed. Every
                // prefix of a password is now a password, so typing 123456 commits six times.
                //
                // That is still the right trade, and it is worth saying why rather than leaving the
                // old reason behind. What the component holds is always what the field shows, which
                // is the property the upload depends on. The intermediates are transient and cost
                // nothing: the prepared copies do not depend on the password's VALUE, only on there
                // being one, and preparing is idempotent. What they must not do is talk - a line in
                // the console per keystroke is how a tool teaches people to ignore its console - so
                // nothing is logged here. The digits and their check code are on screen already.
                if (draft == null) draft = settings.KeyAsString();
                string typed = EditorGUILayout.TextField(draft, style, GUILayout.Height(34));

                if (typed != draft)
                {
                    draft = typed;
                    if (MeshProtectCipher.TryParsePassword(draft, out var parsed, out draftError))
                    {
                        Undo.RecordObject(settings, "Set Mesh Protect Password");
                        settings.keyDigits = parsed;
                        EditorUtility.SetDirty(settings);
                        PrepareAfterThisRepaint(settings);
                    }
                }
                else if (draftError == null && draft != settings.KeyAsString())
                {
                    // Something else changed it - an undo, another inspector - so follow along.
                    draft = settings.KeyAsString();
                }

                // Every length from one to six parses, so there is no half-typed state to be quiet
                // about: an error here means a digit outside 1-8 or more than six of them, and both
                // are worth saying at once.
                if (draftError != null)
                {
                    EditorGUILayout.HelpBox(draftError, MessageType.Error);
                }
                else
                {
                    string weak = MeshProtectCipher.DescribeWeakness(settings.keyDigits);
                    if (weak != null) EditorGUILayout.HelpBox(weak, MessageType.Warning);
                }

                EditorGUILayout.LabelField(MeshProtectL10n.Tr("password.help"),
                    EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(4);

                // The same four characters the upload prints. Comparing them is how an author
                // checks that the avatar being built is keyed to the password in front of them,
                // without the digits themselves going into a log people paste into help threads.
                EditorGUILayout.LabelField(
                    MeshProtectL10n.Tr("password.check"),
                    MeshProtectCipher.CheckCode(settings.keyDigits, settings.variant),
                    EditorStyles.miniLabel);

                EditorGUILayout.LabelField(MeshProtectL10n.Tr("password.id"), settings.variant.shaderName,
                                           EditorStyles.miniLabel);

                EditorGUILayout.Space(2);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(MeshProtectL10n.Tr("password.random")))
                    {
                        Undo.RecordObject(settings, "Randomise Mesh Protect Password");
                        settings.keyDigits = MeshProtectCipher.GeneratePassword(new System.Random());
                        EditorUtility.SetDirty(settings);
                        draft = settings.KeyAsString();
                        draftError = null;
                        Debug.Log("[MeshProtect] Password set, check " +
                                  MeshProtectCipher.CheckCode(settings.keyDigits, settings.variant) +
                                  ". Upload again and re-enter it in game. The digits stay on the " +
                                  "component and out of this log - Editor.log gets shared.");
                    }

                    if (GUILayout.Button(MeshProtectL10n.Tr("password.new")))
                    {
                        if (EditorUtility.DisplayDialog(
                                MeshProtectL10n.Tr("dialog.replace.title"),
                                MeshProtectL10n.Tr("dialog.replace.body", settings.KeyAsString()),
                                MeshProtectL10n.Tr("dialog.replace.ok"),
                                MeshProtectL10n.Tr("dialog.cancel")))
                        {
                            Regenerate(settings);
                            draft = settings.KeyAsString();
                            draftError = null;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The component's own fields, drawn by hand so their labels and tooltips can follow the
        /// language. The [Tooltip] attributes on the fields keep the English text as the fallback
        /// anyone sees in the debug inspector.
        /// </summary>
        private static void DrawSettingsFields(SerializedObject so)
        {
            void Field(string name, string label, string tip) =>
                EditorGUILayout.PropertyField(so.FindProperty(name),
                                              MeshProtectL10n.TrC(label, tip));

            Field("distortRatio", "field.distort", "field.distort.tip");
            EditorGUILayout.PropertyField(so.FindProperty("mode"), MeshProtectL10n.TrC("field.mode"));
            Field("recalculateMissingTangents", "field.tangents", "field.tangents.tip");
            Field("attenuateAtJoints", "field.attenuate", "field.attenuate.tip");
            Field("rigidityExponent", "field.rigidity", "field.rigidity.tip");
            Field("obfuscateAnimatorNames", "field.animnames", "field.animnames.tip");
            Field("targetRenderers", "field.targets", "field.targets.tip");
            Field("ignoredMaterials", "field.ignored", "field.ignored.tip");
            Field("invisibleMaterials", "field.invisible", "field.invisible.tip");
            Field("outputFolder", "field.output", "field.output.tip");
        }

        private void DrawAdvancedActions(MeshProtectRoot settings)
        {
            using (new EditorGUI.DisabledScope(!settings.HasKey))
            {
                if (GUILayout.Button(MeshProtectL10n.Tr("adv.rebuild")))
                {
                    // Unconditionally. Somebody pressing this has already decided the family is
                    // wrong, and is usually here because the build told them to press it - so
                    // answering "already up to date" is the tool disagreeing with the tool. It
                    // costs a few seconds and it is the only lever they have.
                    MeshProtectShaderGen.EnsureGenerated(settings, settings.variant,
                                                         out string folder, force: true);
                    Debug.Log($"[MeshProtect] Rebuilt the shader family in '{folder}'.");

                    RebuildGrafts(settings, force: true);
                }

                if (GUILayout.Button(MeshProtectL10n.Tr("adv.skincheck")))
                    MeshProtectSkinCheck.RunAndReport(settings);
            }
        }

        /// <summary>
        /// Prepare a decode-carrying copy of every non-lilToon shader this avatar wears.
        ///
        /// Here, and not in the build, for the same reason the lilToon family is generated here:
        /// an avatar whose assets were created during the SDK build uploaded fine and then could
        /// not stand up in game, and the identical code writing to a permanent folder beforehand
        /// was fine. The cause was never found, so the shape that is known to work is the one this
        /// keeps.
        ///
        /// A shader that cannot be grafted is reported and nothing else happens: those materials
        /// ship unprotected, which is a smaller loss than an author who cannot upload.
        /// </summary>
        private static void RebuildGrafts(MeshProtectRoot settings, bool force)
        {
            var problems = new System.Collections.Generic.List<string>();

            try
            {
                EditorUtility.DisplayProgressBar(MeshProtectL10n.Tr("progress.title"),
                    MeshProtectL10n.Tr("progress.grafts"), 0.5f);

                MeshProtectForeignShader.EnsureAllGrafts(
                    settings, settings.variant, settings.gameObject, problems, force);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            foreach (string problem in problems)
                Debug.LogWarning("[MeshProtect] " + problem + ".");
        }

        /// <summary>
        /// Replace the algorithm, and keep whatever password is already set.
        ///
        /// Changing only the password is a different and much cheaper thing, and the two are worth
        /// keeping apart. The password is not part of the shader's identity, so setting one is
        /// instant and leaves the expression parameters - and therefore their saved values on every
        /// machine that already has them - alone. Regenerating the variant renames those
        /// parameters, which discards those saved values everywhere, and costs a shader import.
        ///
        /// Reach for this when the password has leaked to somebody who might reverse the shader, or
        /// when cutting a copy for someone else. A password change alone does not help against an
        /// attacker who has already reversed this avatar's shader: the key is 18 bits and they can
        /// brute-force a new one in seconds. A new variant makes them start over.
        ///
        /// These are two different things and only one of them is being asked for. The variant is
        /// the algorithm; the password is a secret the author chose and wrote down. Regenerating
        /// one has never required discarding the other, and doing it anyway cost a real upload: an
        /// author typed their own password, pressed this button to refresh the protection, and
        /// shipped an avatar keyed to a random password they had never seen. The one they had
        /// written down did not open it, and nothing in the build said why.
        ///
        /// Someone who wants a new password has a button for that, right next to this one.
        /// </summary>
        public static void ReplaceProtection(MeshProtectRoot settings, System.Random rng)
        {
            if (!settings.HasPassword)
                settings.keyDigits = MeshProtectCipher.GeneratePassword(rng);

            settings.variant = MeshProtectVariantGenerator.Generate(rng);
        }

        private static void Regenerate(MeshProtectRoot settings)
        {
            Undo.RecordObject(settings, "Generate Mesh Protect Password");

            ReplaceProtection(settings, new System.Random());
            EditorUtility.SetDirty(settings);

#if LILMP_VRCSDK3_AVATARS
            // The prepared copies carry the names the old algorithm generated. Keeping them would
            // ship an avatar whose Base layer was scrambled by one algorithm and whose unlock
            // layers were built by another - and the inspector would call it up to date.
            MeshProtectControllers.Clear(settings);
#endif

            // Generate the shader family now, in the editor, rather than during the upload: the
            // import needs an AssetDatabase refresh, and doing that in the middle of a build is a
            // good way to break it.
            try
            {
                EditorUtility.DisplayProgressBar(MeshProtectL10n.Tr("progress.title"),
                    MeshProtectL10n.Tr("progress.shader"), 0.5f);

                if (MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out string folder))
                    Debug.Log($"[MeshProtect] Generated the shader family in '{folder}'.");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            RebuildGrafts(settings, force: false);

            // The digits are on the component, in a field the author is looking at. Repeating
            // them here only puts them somewhere that gets pasted into help threads.
            Debug.Log($"[MeshProtect] Rolled a new password - check " +
                      $"{MeshProtectCipher.CheckCode(settings.keyDigits, settings.variant)}, " +
                      $"protection id {settings.variant.shaderName}. Read the digits off the " +
                      "component and write them down.");
        }
    }
}
#endif
