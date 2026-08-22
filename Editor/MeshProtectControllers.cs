#if UNITY_EDITOR && LILMP_VRCSDK3_AVATARS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace MeshProtect
{
    /// <summary>
    /// Renamed copies of the avatar's own Base, Gesture and Action controllers, made before the
    /// upload and only pointed at during it.
    ///
    /// WHY THIS IS SPLIT IN TWO. Renaming inside those controllers requires copying them, and for
    /// most of this tool's life it refused to, because copying them during the upload produced an
    /// avatar that held a pose it was not in and could not stand up. That was eventually pinned
    /// down with three uploads of one avatar - same code, same password, same protection id -
    /// differing in one thing:
    ///
    ///     no copy at all ................................. works
    ///     copied ahead of the upload, permanent folder ... works
    ///     copied during the upload, temp folder .......... breaks
    ///
    /// The reason is still not known, and it is not for want of looking. Run both ways in one
    /// project against one variant, the two files come out equivalent - same object census, same
    /// class histogram, same number of outward references, .meta identical but for the GUID - and
    /// the finished avatar's object graph is identical at all 240 reference sites, with neither
    /// leaving a stale reference to an original behind. Nothing the editor can see distinguishes
    /// the avatar that works from the one that does not.
    ///
    /// So this does not try to be clever about it. It obeys the one rule the evidence supports:
    /// the copies exist before the build starts, in a folder that outlives it, and the build
    /// creates nothing - it reassigns a reference and stops. Kanna Protecc, which renames far more
    /// than this and works, is built the same way, and hits the same class of unexplained problem
    /// elsewhere: it skips VRChat's proxy animations with "To Do: Figure Out Why This Is Needed"
    /// written next to the skip.
    ///
    /// FX is deliberately not prepared here. The build has always made its own copy of that one,
    /// during the build, into the folder it deletes afterwards - and that has never broken an
    /// avatar. Moving it here would be a change with nothing behind it.
    ///
    /// The avatar in the scene is never modified. It goes on pointing at its own controllers; only
    /// the copy the SDK builds from is redirected.
    /// </summary>
    public static class MeshProtectControllers
    {
        /// <summary>What the author needs to know before pressing Build &amp; Publish.</summary>
        public enum State
        {
            /// <summary>No custom Base, Gesture or Action layer to rename in the first place.</summary>
            NothingToPrepare,

            /// <summary>Nothing prepared. The upload works; those layers just ship readable.</summary>
            NotPrepared,

            UpToDate,

            /// <summary>A controller was edited, or a copy was deleted, since the copies were made.</summary>
            Stale,
        }

        /// <summary>
        /// FX is the build's business, not this file's, and a default layer is one VRChat replaces
        /// with its own - copying it would put a controller in the bundle that nothing reads.
        /// </summary>
        private static bool Preparable(VRCAvatarDescriptor.CustomAnimLayer layer) =>
            !layer.isDefault &&
            layer.type != VRCAvatarDescriptor.AnimLayerType.FX &&
            layer.animatorController is AnimatorController;

        public static string Folder(MeshProtectRoot settings)
        {
            return $"{MeshProtectShaderGen.OutputRoot(settings)}/_Controllers/{settings.variant.shaderName}";
        }

        // ------------------------------------------------------------------ before the build

        /// <summary>
        /// Copy every renameable controller into this avatar's folder and rename inside the copies.
        ///
        /// Returns how many animator objects were renamed. Throws if a copy could not be taken,
        /// because a half-prepared set is worse than none: the build would adopt the controllers
        /// that copied and leave the rest, and which layers ship readable would depend on which
        /// copy happened to fail.
        /// </summary>
        public static int Prepare(MeshProtectRoot settings, VRCAvatarDescriptor descriptor,
                                  List<string> warnings = null)
        {
            if (settings == null || descriptor == null) return 0;
            if (!settings.HasKey)
                throw new InvalidOperationException(
                    "Generate a password first - the replacement names come from the algorithm it " +
                    "generates, so there is nothing to rename to yet.");

            string folder = Folder(settings);

            // Start from empty every time. Preparing twice with a layer removed in between would
            // otherwise leave the old copy behind, and the build would go on adopting it.
            Clear(settings);
            EnsureFolder(folder);

            var prepared = new List<MeshProtectRoot.PreparedController>();
            int warnedBefore = warnings?.Count ?? 0;
            var baseLayers = Copy(descriptor.baseAnimationLayers, folder, prepared, warnings);
            var specialLayers = Copy(descriptor.specialAnimationLayers, folder, prepared, warnings);

            if (prepared.Count == 0)
            {
                // Only when there was genuinely nothing to find. If Copy has just explained that the
                // layers this avatar does have are not on disk, telling the author it has no layers
                // contradicts the line above it and sends them looking for the wrong thing.
                if ((warnings?.Count ?? 0) == warnedBefore)
                    warnings?.Add("This avatar has no custom Base, Gesture or Action layer, so there " +
                                  "was nothing to prepare. The FX layer is still renamed during the " +
                                  "upload as usual.");
                return 0;
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var copies = baseLayers.Concat(specialLayers)
                .Select(l => l.animatorController as AnimatorController)
                .Where(c => c != null)
                .ToList();

            // Before the rename pass, so the trees that come inside are renamed with everything
            // else. They arrive carrying the artist's names - "standing_11M", "pose17" - and those
            // ship in the bundle today.
            if (settings.copySeparateBlendTrees)
            {
                int trees = copies.Sum(MeshProtectParameters.CopySeparateBlendTrees);
                if (trees > 0)
                    warnings?.Add($"Copied {trees} blend tree(s) that were stored outside the " +
                                  "controllers into the protected copies, so their names and the " +
                                  "parameters they use can be protected too. Your own files were " +
                                  "not modified.");
            }

            int renamed = MeshProtectObfuscator.Run(ref baseLayers, ref specialLayers,
                                                    settings.variant, folder);

            settings.preparedControllers = prepared;
            settings.renamedParameters = new List<MeshProtectRoot.RenamedParameter>();
            settings.renamedObjects = new List<MeshProtectRoot.RenamedParameter>();
            settings.preparedWithParameterNames = settings.obfuscateParameterNames;
            settings.preparedWithObjectNames = settings.obfuscateObjectNames;
            settings.preparedWithExpressionParameters = settings.obfuscateExpressionParameters;
            settings.preparedWithSeparateBlendTrees = settings.copySeparateBlendTrees;
            settings.preparedSurfaceHash = SurfaceHash(descriptor);

            if (settings.obfuscateParameterNames)
                renamed += RenameParameters(settings, descriptor, copies, warnings);

            if (settings.obfuscateObjectNames)
                renamed += RenameObjects(settings, descriptor, copies, folder, warnings);

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            // After the save, because what is being recorded is the file, and until SaveAssets runs
            // the file is not what the copies are. This is the record that Undo cannot rewrite.
            foreach (var entry in prepared) entry.copyHash = HashOf(entry.copyPath);
            EditorUtility.SetDirty(settings);

            return renamed;
        }

        /// <summary>
        /// Work out which object names may be replaced, rewrite the copies' animation paths for
        /// them, and record the map for the build to rename the objects with.
        ///
        /// NOTHING IS RENAMED HERE. The objects belong to the avatar in the scene, and this tool
        /// does not touch the scene; they are renamed on the copy the SDK builds from. What happens
        /// here is the other half: the clips inside these controller copies address objects by path,
        /// so their paths are rewritten now to the names the build will hand out. The two halves
        /// only meet because both use this map.
        /// </summary>
        private static int RenameObjects(MeshProtectRoot settings, VRCAvatarDescriptor descriptor,
                                         List<AnimatorController> copies, string folder,
                                         List<string> warnings)
        {
            var options = ObjectSurveyOptions(settings, descriptor);
            var findings = MeshProtectObjectNames.Survey(descriptor, options);

            var map = MeshProtectObjectNames.BuildMap(findings, settings.variant,
                                                      findings.Select(f => f.name));
            if (map.Count == 0)
            {
                warnings?.Add("No object name could be replaced on this avatar without risking it.");
                return 0;
            }

            int rewrites = copies.Sum(c => MeshProtectObjectNames.RewriteController(c, map, folder));

            settings.renamedObjects = map
                .OrderBy(pair => pair.Key, System.StringComparer.Ordinal)
                .Select(pair => new MeshProtectRoot.RenamedParameter
                {
                    original = pair.Key,
                    obfuscated = pair.Value
                })
                .ToList();

            return rewrites;
        }

        /// <summary>
        /// Which controllers' animations this upload rewrites, for the object name survey.
        ///
        /// FX always, because the build copies it; the avatar's own layers only once copies of them
        /// exist. Unlike the parameter survey this barely changes the answer - on the avatar it was
        /// measured against, 631 names either way against 637 with the copies - because animation
        /// paths live overwhelmingly in FX.
        /// </summary>
        internal static MeshProtectObjectNames.Options ObjectSurveyOptions(
            MeshProtectRoot settings, VRCAvatarDescriptor descriptor)
        {
            bool copiesExist = settings.preparedControllers.Count > 0;
            var options = new MeshProtectObjectNames.Options();

            foreach (var layers in new[] { descriptor.baseAnimationLayers, descriptor.specialAnimationLayers })
            {
                if (layers == null) continue;
                foreach (var layer in layers)
                {
                    if (layer.isDefault) continue;

                    bool fx = layer.type == VRCAvatarDescriptor.AnimLayerType.FX;
                    if (!fx && !(copiesExist && Preparable(layer))) continue;

                    var controller = layer.animatorController as AnimatorController;
                    if (controller != null) options.rewritable.Add(controller);
                }
            }

            return options;
        }

        /// <summary>
        /// Work out which parameters may be renamed, rename them inside the copies, and record the
        /// map for the build to finish the job with.
        ///
        /// The survey reads the avatar in the scene, which still points at the author's own
        /// controllers - that is the point. What has to be proved is that every place naming a
        /// parameter is somewhere the upload rewrites, and the scene is where all of those places
        /// are visible at once.
        /// </summary>
        private static int RenameParameters(MeshProtectRoot settings, VRCAvatarDescriptor descriptor,
                                            List<AnimatorController> copies, List<string> warnings)
        {
            var options = SurveyOptions(settings, descriptor);
            var findings = MeshProtectParameters.Survey(descriptor, options);

            var map = MeshProtectParameters.BuildMap(findings, settings.variant,
                                                     findings.Select(f => f.name)
                                                             .Concat(options.reserved));
            if (map.Count == 0)
            {
                warnings?.Add("No parameter could be renamed on this avatar without risking it. " +
                              "The report on the component says which ones and why.");
                return 0;
            }

            int rewrites = copies.Sum(c => MeshProtectParameters.RewriteController(c, map));

            settings.renamedParameters = map
                .OrderBy(pair => pair.Key, System.StringComparer.Ordinal)
                .Select(pair => new MeshProtectRoot.RenamedParameter
                {
                    original = pair.Key,
                    obfuscated = pair.Value
                })
                .ToList();

            return rewrites;
        }

        /// <summary>
        /// What the upload will and will not rewrite, as the survey needs to hear it.
        ///
        /// FX always counts as ours - the build has always copied it. The avatar's own layers count
        /// only once copies of them exist, which is what makes the report honest: an author who has
        /// not pressed Prepare should be told the smaller number they would actually get, not the
        /// one they could have.
        /// </summary>
        internal static MeshProtectParameters.Options SurveyOptions(MeshProtectRoot settings,
                                                                    VRCAvatarDescriptor descriptor)
        {
            bool copiesExist = settings.preparedControllers.Count > 0;
            var options = new MeshProtectParameters.Options
            {
                expressionParameters = settings.obfuscateExpressionParameters,
                copySeparateBlendTrees = settings.copySeparateBlendTrees,
            };

            foreach (var name in settings.variant.parameterNames.Concat(settings.variant.bitNames))
                if (!string.IsNullOrEmpty(name)) options.reserved.Add(name);

            foreach (var layers in new[] { descriptor.baseAnimationLayers, descriptor.specialAnimationLayers })
            {
                if (layers == null) continue;
                foreach (var layer in layers)
                {
                    if (layer.isDefault) continue;

                    bool fx = layer.type == VRCAvatarDescriptor.AnimLayerType.FX;
                    if (!fx && !(copiesExist && Preparable(layer))) continue;

                    var controller = layer.animatorController as AnimatorController;
                    if (controller != null) options.rewritable.Add(controller);
                }
            }

            return options;
        }

        /// <summary>
        /// The avatar's own controllers this build is going to swap a prepared copy in for.
        ///
        /// The build-time check needs this to know what it may skip: a controller about to be
        /// replaced wholesale says nothing about whether a name is safe, because what ships in its
        /// place was already rewritten.
        /// </summary>
        internal static HashSet<AnimatorController> WouldAdopt(VRCAvatarDescriptor descriptor,
                                                               MeshProtectRoot settings)
        {
            var replaced = new HashSet<AnimatorController>();
            if (descriptor == null || settings == null) return replaced;

            var byGuid = settings.preparedControllers
                .Where(e => !string.IsNullOrEmpty(e.sourceGuid))
                .GroupBy(e => e.sourceGuid)
                .ToDictionary(g => g.Key, g => g.Last());

            foreach (var layers in new[] { descriptor.baseAnimationLayers, descriptor.specialAnimationLayers })
            {
                if (layers == null) continue;
                foreach (var layer in layers)
                {
                    if (layer.isDefault || layer.animatorController == null) continue;

                    var controller = layer.animatorController as AnimatorController;
                    if (controller == null) continue;

                    string path = AssetDatabase.GetAssetPath(controller);
                    if (string.IsNullOrEmpty(path)) continue;

                    if (!byGuid.TryGetValue(AssetDatabase.AssetPathToGUID(path), out var entry)) continue;
                    if (AssetDatabase.LoadAssetAtPath<AnimatorController>(entry.copyPath) == null) continue;
                    if (HashOf(path) != entry.sourceHash) continue;

                    replaced.Add(controller);
                }
            }

            return replaced;
        }

        /// <summary>
        /// Copy the controllers, and hand back layer arrays pointing at the copies for renaming.
        ///
        /// The arrays are built here rather than taken from the descriptor because the descriptor
        /// belongs to the avatar in the user's scene, and this must not write to it.
        /// </summary>
        private static VRCAvatarDescriptor.CustomAnimLayer[] Copy(
            VRCAvatarDescriptor.CustomAnimLayer[] layers, string folder,
            List<MeshProtectRoot.PreparedController> prepared, List<string> warnings)
        {
            if (layers == null) return new VRCAvatarDescriptor.CustomAnimLayer[0];

            var result = new List<VRCAvatarDescriptor.CustomAnimLayer>();
            foreach (var layer in layers)
            {
                if (!Preparable(layer)) continue;

                var source = (AnimatorController)layer.animatorController;
                string sourcePath = AssetDatabase.GetAssetPath(source);

                // A controller another tool built in memory and never saved. Skipping it is the
                // right answer - there is no file to copy - but skipping it quietly is not: with no
                // layer copied at all the author is told this avatar has no custom Base, Gesture or
                // Action layer, which is a different problem with a different fix. Preparing the
                // rest and saying which one was left is better than refusing the lot.
                if (string.IsNullOrEmpty(sourcePath))
                {
                    warnings?.Add($"The {layer.type} layer's controller has never been saved to a " +
                                  "file - another tool built it in memory - so there was nothing to " +
                                  "copy and its layer names stay readable. Everything else was " +
                                  "prepared. Saving that controller as an asset and preparing again " +
                                  "will pick it up.");
                    continue;
                }

                string destination = AssetDatabase.GenerateUniqueAssetPath(
                    $"{folder}/{layer.type}.controller");

                var copy = MeshProtectAnimator.CopyOutOfItsFile(source, destination);
                if (copy == null)
                    throw new InvalidOperationException(
                        $"Could not copy the {layer.type} controller '{source.name}'. Renaming " +
                        "inside it has to happen in a copy so your own controller is never " +
                        "modified. Nothing was changed. If another tool built this controller in " +
                        "memory rather than saving it, save the avatar's setup to disk first.");

                prepared.Add(new MeshProtectRoot.PreparedController
                {
                    layerType = layer.type.ToString(),
                    sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath),
                    sourceHash = HashOf(sourcePath),
                    copyPath = destination,
                });

                var pointed = layer;
                pointed.animatorController = copy;
                result.Add(pointed);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Delete the copies and forget them.
        ///
        /// Driven by where the copies actually are rather than by where this avatar's folder is
        /// now, because the folder is named after the protection id and Replace Protection changes
        /// it. Clearing the current folder only would leave the previous algorithm's copies on disk
        /// and still listed - and the build would go on adopting them, so the upload would carry
        /// names from an algorithm the avatar no longer uses.
        /// </summary>
        public static void Clear(MeshProtectRoot settings)
        {
            if (settings == null) return;

            var folders = settings.preparedControllers
                .Where(e => !string.IsNullOrEmpty(e.copyPath))
                .Select(e => e.copyPath.Substring(0, e.copyPath.LastIndexOf('/')))
                .Append(Folder(settings))
                .Distinct();

            foreach (var folder in folders)
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);

            bool hadRecord = settings.preparedControllers.Count > 0 ||
                             settings.renamedParameters.Count > 0 ||
                             settings.renamedObjects.Count > 0 ||
                             settings.preparedSurfaceHash != null;

            settings.preparedControllers = new List<MeshProtectRoot.PreparedController>();

            // The renamed names live in the copies being deleted here. Leaving the map behind would
            // have the build rewrite the FX controller and the expression list to names nothing else
            // in the avatar uses any more.
            settings.renamedParameters = new List<MeshProtectRoot.RenamedParameter>();
            settings.renamedObjects = new List<MeshProtectRoot.RenamedParameter>();

            // The whole record moves together or it means nothing. Leaving these behind describing
            // copies that were just deleted is not reachable as a failure today - every reader of
            // them is behind a non-empty preparedControllers - but a set of fields that is only
            // consistent by accident is how the Undo hole in this file got in.
            settings.preparedWithParameterNames = false;
            settings.preparedWithObjectNames = false;
            settings.preparedWithExpressionParameters = false;
            settings.preparedWithSeparateBlendTrees = false;
            settings.preparedSurfaceHash = null;

            if (hadRecord) EditorUtility.SetDirty(settings);
        }

        // ------------------------------------------------------------------ during the build

        /// <summary>
        /// Point the build's avatar at the prepared copies. Creates nothing.
        ///
        /// Returns the asset paths adopted, so the rename pass can tell an already-renamed
        /// controller from one it is leaving readable - they look identical from inside it, and
        /// reporting the first as skipped would tell the author their layers ship in the clear when
        /// the opposite is true.
        /// </summary>
        internal static List<string> Adopt(VRCAvatarDescriptor descriptor, MeshProtectRoot settings,
                                           MeshProtectPipeline.Report report)
        {
            var adopted = new List<string>();
            if (descriptor == null || settings == null || settings.preparedControllers.Count == 0)
                return adopted;

            var byGuid = new Dictionary<string, MeshProtectRoot.PreparedController>();
            foreach (var entry in settings.preparedControllers)
                if (!string.IsNullOrEmpty(entry.sourceGuid)) byGuid[entry.sourceGuid] = entry;

            var used = new HashSet<string>();
            Swap(ref descriptor.baseAnimationLayers, byGuid, used, adopted, report);
            Swap(ref descriptor.specialAnimationLayers, byGuid, used, adopted, report);

            // Prepared, but the avatar being built no longer points at what it was made from. The
            // usual cause is another tool replacing the layer during this build, which is its right
            // - so this is a note, not a failure. It matters because those names ship readable and
            // the author would otherwise believe they did not.
            var unused = settings.preparedControllers.Where(e => !used.Contains(e.sourceGuid)).ToList();
            if (unused.Count > 0)
                report?.warnings.Add(
                    $"{unused.Count} prepared controller(s) were not used, because this build's " +
                    $"{string.Join(", ", unused.Select(e => e.layerType))} layer(s) no longer come " +
                    "from the controller they were made from - another tool in the build most " +
                    "likely replaced them. Those layers ship with their original names.");

            return adopted;
        }

        private static void Swap(ref VRCAvatarDescriptor.CustomAnimLayer[] layers,
                                 Dictionary<string, MeshProtectRoot.PreparedController> byGuid,
                                 HashSet<string> used, List<string> adopted,
                                 MeshProtectPipeline.Report report)
        {
            if (layers == null) return;

            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].isDefault || layers[i].animatorController == null) continue;

                string path = AssetDatabase.GetAssetPath(layers[i].animatorController);
                if (string.IsNullOrEmpty(path)) continue;

                string guid = AssetDatabase.AssetPathToGUID(path);
                if (!byGuid.TryGetValue(guid, out var entry)) continue;

                // Both failures below mean the same thing: the copy cannot be trusted to be this
                // controller. Neither stops the upload. Refusing would trade a working avatar for
                // a hidden layer name, which is the wrong way round - an author whose upload is
                // refused removes the tool, an author whose Base layer ships readable loses
                // nothing they had. So the layer falls back to the author's own controller, which
                // is exactly what every release before this feature shipped, and says so.
                var copy = AssetDatabase.LoadAssetAtPath<AnimatorController>(entry.copyPath);
                if (copy == null)
                {
                    report?.warnings.Add(
                        $"The prepared {entry.layerType} controller is missing from " +
                        $"'{entry.copyPath}', so that layer ships with its original names. " +
                        "Press Prepare Controllers to put it back.");
                    continue;
                }

                // The author edited the controller after the copy was taken. Adopting it anyway
                // would ship the older one: every toggle, transition and gesture added since would
                // be absent from the upload while working perfectly in the editor, and nothing
                // about that points back here. Shipping their current controller unrenamed is the
                // only option that is certainly correct.
                if (HashOf(path) != entry.sourceHash)
                {
                    report?.warnings.Add(
                        $"'{Path.GetFileName(path)}' has been edited since its protected copy was " +
                        $"made, so this upload uses your own {entry.layerType} controller and that " +
                        "layer ships with its original names - everything you changed is in the " +
                        "avatar. Press Prepare Controllers to protect it again.");
                    continue;
                }

                layers[i].animatorController = copy;
                used.Add(guid);
                adopted.Add(entry.copyPath);
            }
        }

        // ------------------------------------------------------------------ for the inspector

        public static State Inspect(MeshProtectRoot settings, VRCAvatarDescriptor descriptor,
                                    out string detail)
        {
            detail = null;
            if (settings == null) return State.NothingToPrepare;

            bool anyPreparable = descriptor != null &&
                (descriptor.baseAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
                    .Concat(descriptor.specialAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
                    .Any(Preparable);

            if (settings.preparedControllers.Count == 0)
                return anyPreparable ? State.NotPrepared : State.NothingToPrepare;

            var problems = new List<string>();

            // A changed option is as stale as an edited controller, and less obvious: half the
            // answer is already written into the copies, so the two halves would disagree.
            if (settings.preparedWithExpressionParameters != settings.obfuscateExpressionParameters)
                problems.Add("the expression parameter option changed");
            if (settings.preparedWithSeparateBlendTrees != settings.copySeparateBlendTrees)
                problems.Add("the blend tree option changed");
            // Both directions. Turning renaming ON after preparing leaves copies holding the
            // original names while everything else believes they do not, and an empty
            // renamedParameters cannot be used to spot it: preparing with renaming on and clearing
            // no parameter leaves the same empty list and is perfectly up to date.
            if (settings.preparedWithParameterNames != settings.obfuscateParameterNames)
                problems.Add("the parameter renaming option changed");
            if (settings.preparedWithObjectNames != settings.obfuscateObjectNames)
                problems.Add("the object renaming option changed");
            // Only when there is an avatar to compare against. With the component sitting outside
            // one, this cannot be answered at all, and answering it anyway reported the copies as
            // out of date forever - naming a cause that was not the reason and offering a button
            // that could only say "this object is not part of an avatar".
            if (descriptor != null && settings.obfuscateParameterNames &&
                settings.preparedSurfaceHash != SurfaceHash(descriptor))
                problems.Add("the expression parameters or the menu changed");

            foreach (var entry in settings.preparedControllers)
            {
                if (AssetDatabase.LoadAssetAtPath<AnimatorController>(entry.copyPath) == null)
                {
                    problems.Add($"the prepared {entry.layerType} copy is gone");
                    continue;
                }

                // The copy itself, against what it was when it was made. Undo can put every field
                // on this component back to a state that describes a different set of copies -
                // tick the object option, let them be remade, press Ctrl+Z twice and every flag
                // agrees with every other flag again while the folder holds controllers whose
                // animation paths were rewritten for names nothing is going to rename. A file
                // cannot be undone, so this is the one comparison that cannot be fooled that way.
                // An empty hash is not "nothing to check" - it means the pass that writes it never
                // finished. Prepare sets the option flags before it renames, so a failure in the
                // middle leaves flags that agree with each other, copies already half rewritten,
                // and no map: exactly the state that looks up to date and is not.
                if (entry.copyHash != HashOf(entry.copyPath))
                    problems.Add(string.IsNullOrEmpty(entry.copyHash)
                        ? $"the {entry.layerType} copy was never finished"
                        : $"the prepared {entry.layerType} copy is not the one this component was " +
                          "recorded against");

                string sourcePath = AssetDatabase.GUIDToAssetPath(entry.sourceGuid);
                if (string.IsNullOrEmpty(sourcePath))
                    problems.Add($"the controller the {entry.layerType} copy was made from is gone");
                else if (HashOf(sourcePath) != entry.sourceHash)
                    problems.Add($"{Path.GetFileName(sourcePath)} has been edited since");
                else if (!CopyMatchesTheMap(settings, entry, sourcePath))
                    problems.Add($"the prepared {entry.layerType} copy does not carry the names " +
                                 "this component says it does");
            }

            if (problems.Count == 0)
            {
                detail = string.Join(", ", settings.preparedControllers.Select(e => e.layerType));
                return State.UpToDate;
            }

            detail = string.Join("; ", problems.Distinct());
            return State.Stale;
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>
        /// Does the copy on disk carry exactly the names this component says it carries?
        ///
        /// EVERY OTHER CHECK HERE ASKS THE COMPONENT. This one asks the copy, and the difference
        /// matters because the two live in different worlds: the component's record of what was
        /// renamed is serialised state, which Undo rewrites, while the copies are files, which it
        /// does not. Tick "Include expression parameters", let the copies be remade, then press
        /// Ctrl+Z: every flag goes back to agreeing with every other flag, the source hashes still
        /// match, the copies are all present - and the folder is full of controllers carrying
        /// sixty-odd renamed expression parameters that the restored map has never heard of. The
        /// build would then adopt them and rename only what the map lists, leaving the avatar
        /// calling one parameter two different things.
        ///
        /// Renaming does not add or remove parameters, so the copy's parameter list has to be the
        /// source's put through the map. Anything else means the two have drifted, whatever the
        /// component believes.
        /// </summary>
        /// <summary>Remembered per copy, because Inspect runs from OnInspectorGUI. See HashOf.</summary>
        private static readonly Dictionary<string, string> matched = new Dictionary<string, string>();

        private static bool CopyMatchesTheMap(MeshProtectRoot settings,
                                              MeshProtectRoot.PreparedController entry,
                                              string sourcePath)
        {
            // Everything the answer depends on: the copy's bytes, the source it was made from, and
            // the map it is being checked against. Cheap enough to build on every repaint, unlike
            // the answer itself, which reads two controllers' parameter lists.
            string state = Stamp(entry.copyPath) + "|" + entry.sourceHash + "|" + Fingerprint(settings);
            if (matched.TryGetValue(entry.copyPath, out var remembered) && remembered == state)
                return true;

            var copy = AssetDatabase.LoadAssetAtPath<AnimatorController>(entry.copyPath);
            var source = AssetDatabase.LoadAssetAtPath<AnimatorController>(sourcePath);
            if (copy == null || source == null) return true;   // reported by the caller's own checks

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var renamed in settings.renamedParameters)
                if (!string.IsNullOrEmpty(renamed.original)) map[renamed.original] = renamed.obfuscated;

            var expected = new HashSet<string>(
                source.parameters.Select(p => MeshProtectParameters.Remap(p.name, map)),
                StringComparer.Ordinal);

            var actual = new HashSet<string>(copy.parameters.Select(p => p.name), StringComparer.Ordinal);

            if (!expected.SetEquals(actual)) return false;

            matched[entry.copyPath] = state;
            return true;
        }

        /// <summary>Length and write time, which is all the cache needs to know a file moved on.</summary>
        private static string Stamp(string assetPath)
        {
            string full = Path.Combine(Directory.GetCurrentDirectory(), assetPath);
            if (!File.Exists(full)) return null;

            var file = new FileInfo(full);
            return file.Length + ":" + file.LastWriteTimeUtc.Ticks;
        }

        /// <summary>
        /// The map, cheaply. Counting entries is not enough - Undo can restore a different map of
        /// the same size - so the names go into it as well.
        /// </summary>
        private static string Fingerprint(MeshProtectRoot settings)
        {
            unchecked
            {
                int hash = 17;
                foreach (var renamed in settings.renamedParameters.Concat(settings.renamedObjects))
                {
                    foreach (char c in renamed.original ?? "") hash = hash * 33 + c;
                    foreach (char c in renamed.obfuscated ?? "") hash = hash * 31 + c;
                }
                return settings.renamedParameters.Count + "/" + settings.renamedObjects.Count +
                       ":" + hash;
            }
        }

        /// <summary>
        /// The two assets outside the controllers that decide which parameters may be renamed: the
        /// expression parameter list, and the menu that reaches the rest of the tree.
        ///
        /// Never called with a null descriptor - see the guard in Inspect. Returning something for
        /// one would mean answering "has this changed?" without being able to look, and the answer
        /// it gave (null, which matches no stored hash) reported the copies as permanently out of
        /// date with a reason that had nothing to do with it.
        /// </summary>
        private static string SurfaceHash(VRCAvatarDescriptor descriptor)
        {
            var parts = new[] { descriptor.expressionParameters, (UnityEngine.Object)descriptor.expressionsMenu }
                .Select(o => o == null ? "-" : HashOf(AssetDatabase.GetAssetPath(o)) ?? "?");

            return string.Join("/", parts);
        }

        /// <summary>
        /// What the source file looked like when the copy was taken.
        ///
        /// The file's bytes rather than its timestamp: an author who reverts a change, or restores
        /// from version control, has not invalidated anything, and a timestamp would say they had.
        /// </summary>
        /// <summary>
        /// Remembered per file, because Inspect runs from OnInspectorGUI.
        ///
        /// Unity repaints an inspector as the mouse crosses it, and Inspect hashes every source
        /// controller plus the expression parameters and the root menu - on the avatar this was
        /// measured against, some megabytes per repaint for an answer that only changes when a file
        /// does. Length and write time decide whether the remembered answer still stands; the bytes
        /// are still what the answer is made of.
        /// </summary>
        private static readonly Dictionary<string, (long length, long stamp, string hash)> hashes =
            new Dictionary<string, (long, long, string)>();

        private static string HashOf(string assetPath)
        {
            string full = Path.Combine(Directory.GetCurrentDirectory(), assetPath);
            if (!File.Exists(full)) return null;

            var file = new FileInfo(full);
            long stamp = file.LastWriteTimeUtc.Ticks;

            if (hashes.TryGetValue(full, out var remembered) &&
                remembered.length == file.Length && remembered.stamp == stamp)
                return remembered.hash;

            string hash;
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(full))
                hash = BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "");

            hashes[full] = (file.Length, stamp, hash);
            return hash;
        }

        private static void EnsureFolder(string folder)
        {
            var parts = folder.Split('/');
            string built = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = built + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(built, parts[i]);
                built = next;
            }
        }
    }
}
#endif
