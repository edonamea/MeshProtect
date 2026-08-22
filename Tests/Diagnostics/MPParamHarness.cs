#if UNITY_EDITOR
// Prepare + build smoke test, run without opening the SDK.
//
// The contract parameter renaming has to keep: a parameter is avatar-global, so after a build that
// renames one, the OLD name must not be mentioned anywhere in the built avatar. One missed
// reference is not a smaller obfuscation, it is a broken avatar - and it is invisible to every
// check the build makes about itself, because from the build's point of view nothing failed.
//
// Case E is the one worth having. It puts a reference to a renamed parameter somewhere the build
// cannot rewrite, which is what another tool adding a component between Prepare and Build & Publish
// would do, and asserts that the build gives up the copies rather than shipping half a rename. A
// safety net nobody has dropped anything onto is not a safety net.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using MeshProtect;

namespace MPDiag
{
    public static class MPParamHarness
    {
        private const string Stamp = "MPParamHarness build 3";
        private const string TempFolder = "Assets/_MPDiagTemp";

        /// <summary>Folders holding the artist's own assets, which must not change.</summary>
        private static readonly string[] ArtistFolders = { "Assets/IKUSIA" };

        private sealed class Case
        {
            public string name;
            public bool parameters = true;
            public bool expressions;
            public bool trees = true;

            /// <summary>Put a reference the build cannot rewrite on the clone before building.</summary>
            public bool sabotage;

            /// <summary>Swap a prepared playable layer for an override controller on the clone.</summary>
            public bool overrideLayer;

            /// <summary>Prepare with renaming off, then switch it on without preparing again.</summary>
            public bool flipOnAfterPrepare;

            /// <summary>Replace object names as well.</summary>
            public bool objects;
        }

        private static readonly Case[] Cases =
        {
            new Case { name = "A  2a + copied trees",  expressions = false, trees = true },
            new Case { name = "B  2b + copied trees",  expressions = true,  trees = true },
            new Case { name = "C  2a, trees left out", expressions = false, trees = false },
            new Case { name = "D  renaming off",       parameters = false },
            new Case { name = "E  sabotaged after Prepare", expressions = false, trees = true,
                       sabotage = true },

            // A playable layer can legally hold an AnimatorOverrideController. The survey used to
            // skip one, which is the worst reading available: a parameter only that layer uses is
            // recorded nowhere, looks unreferenced, passes the proof, and gets renamed everywhere
            // else while that layer goes on driving the old name. Expected now: the copies are
            // dropped, exactly as for any other reference this build cannot rewrite.
            new Case { name = "F  a layer becomes an override controller", expressions = false,
                       trees = true, overrideLayer = true },

            // Copies prepared with renaming off, then renaming switched on without preparing again.
            // The copies hold the original names and nothing records that they do, so the build
            // used to decide a fresh map - while skipping the very controllers it was about to
            // swap in, which hid every parameter they use. One shared with FX was then renamed in
            // FX and left alone in the copy.
            new Case { name = "G  renaming switched on after Prepare", parameters = false,
                       expressions = false, trees = true, flipOnAfterPrepare = true },

            // Object names. The failure that matters here is not visible in the avatar's own
            // content: this build generates the clips that carry the digits to the material and
            // binds them to renderer paths, so renaming an object without bringing those along
            // uploads an avatar nobody can unlock, with nothing reported.
            new Case { name = "H  object names as well", expressions = false, trees = true,
                       objects = true },

            // Everything at once, which is what a component nobody has configured now does. B and H
            // each turn on one of the two expensive halves and neither turns on both, so until this
            // case existed the configuration every author gets by default was the one configuration
            // nothing here ran. The interesting part is that the two maps meet: object names are
            // replaced in clip paths that parameter renaming has also rewritten conditions for, and
            // the joint degrade drops both or neither.
            new Case { name = "I  everything on (the default)", expressions = true, trees = true,
                       objects = true },
        };

        public static void RunBatch()
        {
            foreach (var scene in AssetDatabase.FindAssets("t:Scene")
                         .Select(AssetDatabase.GUIDToAssetPath)
                         .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal))
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    scene, UnityEditor.SceneManagement.OpenSceneMode.Single);
                if (MPParamAnalysis.FindDescriptors().Count > 0) break;
            }

            Run();
        }

        [MenuItem("Tools/MeshProtect Diag/Prepare + Build Smoke Test")]
        public static void Run()
        {
            var text = new StringBuilder();
            text.AppendLine("=== " + Stamp + " ===");
            text.AppendLine("run at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            var descriptor = MPParamAnalysis.FindDescriptors().FirstOrDefault();
            if (descriptor == null) { text.AppendLine("no avatar in the scene"); Write(text); return; }

            var settings = descriptor.GetComponentInChildren<MeshProtectRoot>(true);
            if (settings == null || !settings.HasKey)
            {
                text.AppendLine("no MeshProtectRoot with a password on " + descriptor.name);
                Write(text);
                return;
            }

            text.AppendLine("avatar: " + descriptor.name);

            bool wasParameters = settings.obfuscateParameterNames;
            bool wasExpressions = settings.obfuscateExpressionParameters;
            bool wasTrees = settings.copySeparateBlendTrees;
            bool wasObjects = settings.obfuscateObjectNames;

            var failed = new List<string>();

            // What the avatar in the scene mentions, before anything has run. Every case has to
            // leave this exactly as it found it - not only the files on disk, which stay clean
            // simply by nobody calling SaveAssets, but the objects in memory. A build that renames
            // a name in the artist's own asset looks like it worked and writes their project out
            // the next time anything saves.
            var baseline = MPParamAnalysis.CountReferences(descriptor);
            text.AppendLine("scene mentions " + baseline.Count + " parameter name(s) to begin with");

            text.AppendLine();
            text.AppendLine("################ Remap means exactly what the map says ################");
            var remapProblems = CheckRemap();
            foreach (var problem in remapProblems) text.AppendLine("  FAIL: " + problem);
            if (remapProblems.Count > 0) failed.Add("Remap"); else text.AppendLine("  ok");

            text.AppendLine();
            text.AppendLine("################ the menu tree is copied, not written to ################");
            var menuProblems = CheckMenus();
            foreach (var problem in menuProblems) text.AppendLine("  FAIL: " + problem);
            if (menuProblems.Count > 0) failed.Add("menus"); else text.AppendLine("  ok");

            text.AppendLine();
            text.AppendLine("################ preparing twice gives the same names ################");
            var repeatProblems = CheckDeterminism(settings, descriptor);
            foreach (var problem in repeatProblems) text.AppendLine("  FAIL: " + problem);
            if (repeatProblems.Count > 0) failed.Add("determinism"); else text.AppendLine("  ok");

            text.AppendLine();
            text.AppendLine("################ a controller whose structure lives elsewhere clears nothing ################");
            var structureProblems = CheckStructureElsewhere();
            foreach (var problem in structureProblems) text.AppendLine("  FAIL: " + problem);
            if (structureProblems.Count > 0) failed.Add("structure elsewhere"); else text.AppendLine("  ok");

            text.AppendLine();
            text.AppendLine("################ a name an avatar mask lists clears nothing ################");
            var maskProblems = CheckMaskProtects();
            foreach (var problem in maskProblems) text.AppendLine("  FAIL: " + problem);
            if (maskProblems.Count > 0) failed.Add("avatar mask"); else text.AppendLine("  ok");

            text.AppendLine();
            text.AppendLine("################ a new component protects everything ################");
            var defaultProblems = CheckDefaults();
            foreach (var problem in defaultProblems) text.AppendLine("  FAIL: " + problem);
            if (defaultProblems.Count > 0) failed.Add("defaults"); else text.AppendLine("  ok");

            text.AppendLine();
            text.AppendLine("################ face tracking names kept, ordinary names not ################");
            var ftProblems = CheckFaceTrackingNames();
            foreach (var problem in ftProblems) text.AppendLine("  FAIL: " + problem);
            if (ftProblems.Count > 0) failed.Add("face tracking names"); else text.AppendLine("  ok");

            text.AppendLine();
            text.AppendLine("################ nested PhysBone prefixes leave both families alone ################");
            var nestedProblems = CheckNestedPhysBonePrefixes();
            foreach (var problem in nestedProblems) text.AppendLine("  FAIL: " + problem);
            if (nestedProblems.Count > 0) failed.Add("nested prefixes"); else text.AppendLine("  ok");

            text.AppendLine();
            text.AppendLine("################ an edited copy is not the recorded copy ################");
            var copyProblems = CheckCopyMatchesRecord(settings, descriptor);
            foreach (var problem in copyProblems) text.AppendLine("  FAIL: " + problem);
            if (copyProblems.Count > 0) failed.Add("copy on disk"); else text.AppendLine("  ok");

            text.AppendLine();
            text.AppendLine("################ object names: how many could be replaced ################");
            var objectProblems = ReportObjectNames(descriptor, text);
            foreach (var problem in objectProblems) text.AppendLine("  FAIL: " + problem);
            if (objectProblems.Count > 0) failed.Add("object names");

            text.AppendLine();
            text.AppendLine("################ an undone checkbox does not hide stale copies ################");
            var undoProblems = CheckUndoDesync(settings, descriptor);
            foreach (var problem in undoProblems) text.AppendLine("  FAIL: " + problem);
            if (undoProblems.Count > 0) failed.Add("undo"); else text.AppendLine("  ok");

            try
            {
                foreach (var test in Cases)
                {
                    text.AppendLine();
                    text.AppendLine("################ " + test.name + " ################");
                    var problems = RunCase(test, descriptor, settings, text, baseline);
                    if (problems.Count > 0)
                    {
                        failed.Add(test.name);
                        foreach (var problem in problems) text.AppendLine("  FAIL: " + problem);
                    }
                    else text.AppendLine("  case passed");
                }
            }
            catch (Exception e)
            {
                failed.Add("threw");
                text.AppendLine(e.ToString());
            }
            finally
            {
                settings.obfuscateParameterNames = wasParameters;
                settings.obfuscateExpressionParameters = wasExpressions;
                settings.copySeparateBlendTrees = wasTrees;
                settings.obfuscateObjectNames = wasObjects;

                if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                                "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);

                // Leave the project holding copies made under the settings the author had.
                try { MeshProtectControllers.Prepare(settings, descriptor, new List<string>()); }
                catch (Exception e) { text.AppendLine("could not restore the copies: " + e.Message); }
            }

            text.AppendLine();
            text.AppendLine(failed.Count == 0 ? "==== ALL CASES PASS ===="
                                              : "==== FAILED: " + string.Join(", ", failed) + " ====");
            Write(text);
        }

        private static List<string> RunCase(Case test, VRCAvatarDescriptor descriptor,
                                            MeshProtectRoot settings, StringBuilder text,
                                            Dictionary<string, int> baseline)
        {
            var problems = new List<string>();
            GameObject clone = null;

            settings.obfuscateParameterNames = test.parameters;
            settings.obfuscateExpressionParameters = test.expressions;
            settings.copySeparateBlendTrees = test.trees;
            settings.obfuscateObjectNames = test.objects;

            var artistBefore = HashArtistAssets();

            try
            {
                var warnings = new List<string>();
                int prepared = MeshProtectControllers.Prepare(settings, descriptor, warnings);

                var renames = settings.renamedParameters
                    .Where(r => !string.IsNullOrEmpty(r.original))
                    .ToDictionary(r => r.original, r => r.obfuscated);

                text.AppendLine("  prepare: " + prepared + " object(s), " + renames.Count +
                                " parameter(s) renamed");
                foreach (var warning in warnings.Take(3)) text.AppendLine("    warn: " + warning);

                if (test.flipOnAfterPrepare)
                {
                    settings.obfuscateParameterNames = true;
                    text.AppendLine("    renaming switched on, without preparing again");
                }

                var before = MPParamAnalysis.CountReferences(descriptor);
                var sceneDangling = MPParamAnalysis.UnresolvablePaths(descriptor);
                MPParamAnalysis.DeclaredByPlayableLayer(descriptor, out var sceneFx, out var sceneRest);
                var sceneShared = new HashSet<string>(sceneFx);
                sceneShared.IntersectWith(sceneRest);

                clone = UnityEngine.Object.Instantiate(descriptor.gameObject);
                clone.name = descriptor.name + " (build clone)";

                if (test.overrideLayer)
                {
                    string swapped = OverrideALayer(clone);
                    text.AppendLine("    override: the " + (swapped ?? "(none)") +
                                    " layer now holds an AnimatorOverrideController");
                    if (swapped == null) problems.Add("no layer could be swapped for an override");
                }

                string sabotaged = null;
                if (test.sabotage)
                {
                    sabotaged = renames.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
                    if (sabotaged == null) problems.Add("nothing was renamed, so there was nothing to sabotage");
                    else Sabotage(clone, sabotaged);
                    text.AppendLine("    sabotage: an Animator outside the playable layers now uses '" +
                                    sabotaged + "'");
                }

                bool allowed = InvokeHook(clone);
                text.AppendLine("  build hook returned " + allowed);
                if (!allowed) problems.Add("the build hook refused the avatar");

                var built = clone.GetComponent<VRCAvatarDescriptor>();
                var after = built == null ? new Dictionary<string, int>()
                                          : MPParamAnalysis.CountReferences(built);

                bool adopted = built != null && built.baseAnimationLayers
                    .Concat(built.specialAnimationLayers)
                    .Any(l => !l.isDefault && l.animatorController != null &&
                              AssetDatabase.GetAssetPath(l.animatorController)
                                  .Contains("/_Controllers/"));
                text.AppendLine("  prepared copies adopted: " + adopted);

                if (test.overrideLayer)
                {
                    if (adopted) problems.Add("the copies were adopted even though a playable layer " +
                                              "this build does not rewrite is driving those parameters");

                    foreach (var rename in renames.Take(5))
                        if (!after.ContainsKey(rename.Key))
                            problems.Add("'" + rename.Key + "' was renamed anyway");
                }
                else if (test.sabotage)
                {
                    // The whole point: give up the copies rather than ship half a rename.
                    if (adopted) problems.Add("the copies were adopted even though a renamed parameter " +
                                              "is used somewhere this build cannot rewrite");

                    if (sabotaged != null && (!after.TryGetValue(sabotaged, out int kept) || kept == 0))
                        problems.Add("'" + sabotaged + "' vanished from the build, so something was " +
                                     "renamed after all");
                }
                else
                {
                    foreach (var rename in renames)
                    {
                        if (after.TryGetValue(rename.Key, out int count) && count > 0)
                        {
                            var where = built == null ? new List<string>()
                                                      : MPParamAnalysis.WhereMentioned(built, rename.Key);
                            problems.Add("'" + rename.Key + "' still mentioned " + count + " time(s): " +
                                         string.Join(" | ", where.Take(3)));
                        }
                        else if (before.ContainsKey(rename.Key) &&
                                 (!after.TryGetValue(rename.Value, out int now) || now == 0))
                        {
                            // Gated on the avatar having mentioned the old name at all. A PhysBone
                            // prefix puts all eight suffixed names in the map whether or not the
                            // avatar uses them, and an entry that matches nothing renames nothing.
                            problems.Add("'" + rename.Key + "' -> '" + rename.Value +
                                         "' never appears in the build");
                        }
                    }

                    if (test.parameters && renames.Count == 0)
                        problems.Add("renaming was on and nothing was renamed");
                    if (!test.parameters && renames.Count > 0)
                        problems.Add("renaming was off and something was renamed anyway");
                }

                // The invariant that does not need to know the map: a parameter FX and some other
                // playable layer both declare must still be one name in the build, whichever name
                // that is. Renaming it on one side only takes it out of this intersection.
                if (built != null)
                {
                    MPParamAnalysis.DeclaredByPlayableLayer(built, out var builtFx, out var builtRest);
                    var builtShared = new HashSet<string>(builtFx);
                    builtShared.IntersectWith(builtRest);

                    text.AppendLine("  parameters FX shares with another layer, scene/build: " +
                                    sceneShared.Count + " / " + builtShared.Count);

                    if (builtShared.Count < sceneShared.Count)
                        problems.Add((sceneShared.Count - builtShared.Count) + " parameter(s) that " +
                                     "FX shared with another playable layer are no longer shared - " +
                                     "one side was renamed and the other was not");
                }

                // No animation may come out of this pointing at an object that is not there. The
                // avatar is allowed to arrive with some - a clip animating something long deleted -
                // so what is asserted is that the build did not add any.
                if (built != null)
                {
                    // The avatar arrives with a few paths that lead nowhere - clips animating
                    // something the artist deleted - and object renaming rewrites those too, so
                    // they come out as different strings. Comparing the strings raw counts them as
                    // new. What has to be compared is the scene's own dead paths put through the
                    // same map: anything beyond that set is a path THIS BUILD broke.
                    var objectMap = settings.renamedObjects
                        .Where(r => !string.IsNullOrEmpty(r.original))
                        .ToDictionary(r => r.original, r => r.obfuscated, StringComparer.Ordinal);

                    var expected = new HashSet<string>(
                        sceneDangling.Select(p => RewritePath(p, objectMap)), StringComparer.Ordinal);

                    var builtDangling = MPParamAnalysis.UnresolvablePaths(built);
                    text.AppendLine("  animation paths leading nowhere, scene/build: " +
                                    sceneDangling.Count + " / " + builtDangling.Count);

                    foreach (var path in builtDangling.Except(expected).Take(6))
                        problems.Add("this build left an animation pointing at '" + path +
                                     "', which is not an object in it");
                }

                if (test.objects)
                {
                    var renamedObjects = settings.renamedObjects
                        .Where(r => !string.IsNullOrEmpty(r.original))
                        .ToDictionary(r => r.original, r => r.obfuscated);

                    if (renamedObjects.Count == 0) problems.Add("object renaming was on and nothing happened");

                    if (built != null && adopted)
                    {
                        var live = new HashSet<string>(
                            built.GetComponentsInChildren<Transform>(true).Select(x => x.name),
                            StringComparer.Ordinal);

                        foreach (var rename in renamedObjects.Take(200))
                            if (live.Contains(rename.Key))
                                problems.Add("'" + rename.Key + "' is still an object name in a build " +
                                             "whose adopted copies address it as '" + rename.Value + "'");
                    }
                }

                text.AppendLine("  parameters mentioned before/after: " + before.Count + " / " + after.Count);
            }
            finally
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                                "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
                if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
            }

            var artistAfter = HashArtistAssets();
            var changed = artistBefore
                .Where(pair => !artistAfter.TryGetValue(pair.Key, out string hash) || hash != pair.Value)
                .Select(pair => pair.Key).ToList();
            var appeared = artistAfter.Keys.Except(artistBefore.Keys).ToList();

            foreach (var path in changed.Take(10)) problems.Add("artist file changed: " + path);
            foreach (var path in appeared.Take(10)) problems.Add("file appeared in the artist's folder: " + path);

            if (descriptor.baseAnimationLayers.Concat(descriptor.specialAnimationLayers)
                    .Any(l => !l.isDefault && l.animatorController != null &&
                              AssetDatabase.GetAssetPath(l.animatorController).Contains("/_Controllers/")))
                problems.Add("the scene avatar was repointed at generated controllers");

            // The assertion that matters most, and the one this harness did not have the first time
            // it was run: the avatar in the scene must mention exactly what it mentioned before.
            // Nothing here saves, so a name written into one of the artist's own assets shows up
            // only as a change in memory - which is exactly what a rename through a shared object
            // looks like right up until the next SaveAssets writes it to their disk.
            var mentionedNow = MPParamAnalysis.CountReferences(descriptor);
            foreach (var name in mentionedNow.Keys.Except(baseline.Keys).Take(8))
                problems.Add("the scene avatar now mentions '" + name + "', which it did not before");
            foreach (var name in baseline.Keys.Except(mentionedNow.Keys).Take(8))
                problems.Add("the scene avatar no longer mentions '" + name + "'");

            return problems;
        }

        /// <summary>
        /// The menu tree deep copy, on trees built here rather than on the avatar's.
        ///
        /// This is the one part of the feature that creates assets during the upload and rebuilds
        /// every object it touches, and the shapes that break it - the same submenu reached twice,
        /// a submenu pointing back at an ancestor - are ones no real avatar in this project has.
        /// The last one of these that went unchecked shipped a build that wrote renamed parameters
        /// into the author's own expression parameters asset, because the "copy" shared its entries
        /// with the original.
        /// </summary>
        private static List<string> CheckMenus()
        {
            var problems = new List<string>();

            var type = Type.GetType("MeshProtect.MeshProtectParameters, MeshProtect.Editor");
            var rewrite = type?.GetMethod("RewriteMenuTree", System.Reflection.BindingFlags.Static |
                                                             System.Reflection.BindingFlags.NonPublic |
                                                             System.Reflection.BindingFlags.Public);
            if (rewrite == null) return new List<string> { "RewriteMenuTree not found" };

            if (!AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));

            // A real folder standing in for the build's own, because the copies have to be written
            // somewhere. The menus below live OUTSIDE it, which is what makes them the artist's as
            // far as the rewrite is concerned.
            string buildFolder = TempFolder + "/build";
            if (!AssetDatabase.IsValidFolder(buildFolder))
                AssetDatabase.CreateFolder(TempFolder, "build");

            VRCExpressionsMenu Asset(string name)
            {
                var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                menu.controls = new List<VRCExpressionsMenu.Control>();
                AssetDatabase.CreateAsset(menu, TempFolder + "/" + name + ".asset");
                return menu;
            }

            VRCExpressionsMenu.Control Control(string label, string parameter, VRCExpressionsMenu sub)
                => new VRCExpressionsMenu.Control
                {
                    name = label,
                    value = 3f,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter },
                    subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = "Sub" } },
                    subMenu = sub,
                    type = sub != null ? VRCExpressionsMenu.Control.ControlType.SubMenu
                                       : VRCExpressionsMenu.Control.ControlType.Toggle,
                };

            var root = Asset("root");
            var shared = Asset("shared");
            var loop = Asset("loop");

            shared.controls.Add(Control("leaf", "Renameable", null));
            loop.controls.Add(Control("back to root", "Renameable", root));

            root.controls.Add(Control("first", "Renameable", shared));
            root.controls.Add(Control("second", "Untouched", shared));   // the same submenu twice
            root.controls.Add(Control("cycle", "Sub", loop));            // and one that comes back

            var sharedControlBefore = shared.controls[0];
            var rootControlBefore = root.controls[0];
            var map = new Dictionary<string, string> { { "Renameable", "kodabivu" }, { "Sub", "vasemiro" } };
            var variant = new MeshProtectVariant { menuAssetName = "mtest" };

            // Terminating at all is half the test: a tree that points back at itself used to be a
            // reason to hold the root in the memo before the walk starts.
            //
            // The root here lives outside the folder passed in, so this also covers the case the
            // rewrite used to get wrong: a root that is not this build's must be copied, not
            // written to.
            var arguments = new object[] { root, buildFolder, variant, map, null };
            var returned = rewrite.Invoke(null, arguments) as VRCExpressionsMenu;

            if (returned == null)
            {
                problems.Add("no root came back");
                AssetDatabase.DeleteAsset(TempFolder);
                return problems;
            }

            if (ReferenceEquals(returned, root))
                problems.Add("a root outside this build's folder was written to instead of copied");
            if (!ReferenceEquals(root.controls[0], rootControlBefore))
                problems.Add("the artist's root menu had its control list rebuilt");
            if (root.controls[0].parameter.name != "Renameable")
                problems.Add("the artist's root menu was renamed in place");

            root = returned;

            if (root.controls.Count != 3)
                problems.Add("the root lost controls: " + root.controls.Count + " of 3");

            if (root.controls[0].parameter.name != "kodabivu")
                problems.Add("a mapped control parameter was not renamed");
            if (root.controls[1].parameter.name != "Untouched")
                problems.Add("an unmapped control parameter was renamed");
            if (root.controls[0].subParameters[0].name != "vasemiro")
                problems.Add("a mapped sub parameter was not renamed");
            if (Math.Abs(root.controls[0].value - 3f) > 0.0001f || root.controls[0].name != "first")
                problems.Add("a control lost its value or its label");

            // One submenu reached twice has to become one copy, not two, or the wearer gets two
            // menus that drift apart.
            if (!ReferenceEquals(root.controls[0].subMenu, root.controls[1].subMenu))
                problems.Add("the same submenu was copied twice");
            if (ReferenceEquals(root.controls[0].subMenu, shared))
                problems.Add("the author's submenu was written to instead of copied");

            // The original must come out of it untouched, including the Control objects inside it.
            if (!ReferenceEquals(shared.controls[0], sharedControlBefore))
                problems.Add("the author's menu had its control list rebuilt");
            if (shared.controls[0].parameter.name != "Renameable")
                problems.Add("the author's own menu asset was renamed through a shared object");

            var copiedShared = root.controls[0].subMenu;
            if (copiedShared != null && copiedShared.controls[0].parameter.name != "kodabivu")
                problems.Add("the copied submenu was not renamed");

            // A menu that points back at the root has to come back to THIS root. The memo is keyed
            // by the menu handed in, and keying it by the copy instead builds a second root and
            // sends the wearer's "back" entry into a parallel duplicate of the tree they came from.
            var copiedLoop = root.controls[2].subMenu;
            if (copiedLoop == null)
                problems.Add("the submenu that points back at the root was dropped");
            else if (!ReferenceEquals(copiedLoop.controls[0].subMenu, root))
                problems.Add("a menu pointing back at the root was given a second copy of it");

            AssetDatabase.DeleteAsset(TempFolder);
            return problems;
        }

        /// <summary>
        /// Preparing twice has to produce the same names.
        ///
        /// The copies are renamed before the build and the FX controller during it, and the two
        /// passes only agree because the replacement for a name is derived from the name and the
        /// variant rather than handed out in order. Nothing has ever checked that.
        /// </summary>
        private static List<string> CheckDeterminism(MeshProtectRoot settings,
                                                     VRCAvatarDescriptor descriptor)
        {
            var problems = new List<string>();

            MeshProtectControllers.Prepare(settings, descriptor, new List<string>());
            var first = settings.renamedParameters.ToDictionary(r => r.original, r => r.obfuscated);

            MeshProtectControllers.Prepare(settings, descriptor, new List<string>());
            var second = settings.renamedParameters.ToDictionary(r => r.original, r => r.obfuscated);

            if (first.Count != second.Count)
                problems.Add("preparing twice renamed " + first.Count + " then " + second.Count);

            foreach (var pair in first)
            {
                if (!second.TryGetValue(pair.Key, out var again))
                    problems.Add("'" + pair.Key + "' was renamed the first time and not the second");
                else if (again != pair.Value)
                    problems.Add("'" + pair.Key + "' became '" + pair.Value + "' then '" + again + "'");

                if (problems.Count > 6) break;
            }

            return problems;
        }

        /// <summary>
        /// How many object names could be replaced, before any of it is built.
        ///
        /// Same order as the parameter work: measure first, because the last time a guess was made
        /// about how much a mode was worth ("2a is probably close to zero") the measurement said
        /// otherwise. Read only - nothing is renamed.
        /// </summary>
        private static List<string> ReportObjectNames(VRCAvatarDescriptor descriptor, StringBuilder text)
        {
            var problems = new List<string>();

            var type = Type.GetType("MeshProtect.MeshProtectObjectNames, MeshProtect.Editor");
            if (type == null) return new List<string> { "MeshProtectObjectNames not found" };

            var optionsType = type.GetNestedType("Options", System.Reflection.BindingFlags.NonPublic);
            var survey = type.GetMethod("Survey", System.Reflection.BindingFlags.Static |
                                                  System.Reflection.BindingFlags.NonPublic);
            if (optionsType == null || survey == null)
                return new List<string> { "Survey or Options not found" };

            var playable = new List<AnimatorController>();
            foreach (var layers in new[] { descriptor.baseAnimationLayers, descriptor.specialAnimationLayers })
            {
                if (layers == null) continue;
                foreach (var layer in layers)
                {
                    if (layer.isDefault) continue;
                    var controller = layer.animatorController as AnimatorController;
                    if (controller != null) playable.Add(controller);
                }
            }

            var fx = descriptor.baseAnimationLayers
                .Where(l => !l.isDefault && l.type == VRCAvatarDescriptor.AnimLayerType.FX)
                .Select(l => l.animatorController as AnimatorController)
                .Where(c => c != null)
                .ToList();

            foreach (var mode in new[] { ("FX only", fx), ("every playable layer", playable) })
            {
                var options = Activator.CreateInstance(optionsType);
                var set = optionsType.GetField("rewritable").GetValue(options);
                var add = set.GetType().GetMethod("Add");
                foreach (var controller in mode.Item2) add.Invoke(set, new object[] { controller });

                var findings = (System.Collections.IEnumerable)survey.Invoke(null, new[] { descriptor, options });

                int renameable = 0, total = 0;
                var reasons = new Dictionary<string, int>();
                foreach (var finding in findings)
                {
                    total++;
                    var blocked = (string)finding.GetType().GetField("blockedBy").GetValue(finding);
                    if (blocked == null) { renameable++; continue; }

                    string bucket = blocked.Split('(')[0].Trim();
                    reasons[bucket] = reasons.TryGetValue(bucket, out int n) ? n + 1 : 1;
                }

                text.AppendLine("  " + mode.Item1.PadRight(22) + " renameable " + renameable + " / " + total);

                // What the avatar mask rule actually costs, which is the same number as what a build
                // WITHOUT it would have renamed: a name that is a real object here and whose every
                // blocking reference is a mask. Reading blockedBy would undercount - it only names
                // the first blocking site, so a name blocked by a clip AND a mask never says "mask".
                foreach (var finding in findings)
                {
                    var ft = finding.GetType();
                    var reason = (string)ft.GetField("blockedBy").GetValue(finding);
                    if ((int)ft.GetField("objects").GetValue(finding) == 0) continue;

                    // Only when a reference is what blocks it. A humanoid bone that a mask also
                    // happens to list is protected either way, and counting it here would credit
                    // the mask rule with names it is not carrying.
                    if (reason == null || !reason.StartsWith("used by an animation")) continue;

                    var sites = (System.Collections.IEnumerable)ft.GetField("sites").GetValue(finding);
                    int foreign = 0, masks = 0;
                    foreach (var site in sites)
                    {
                        var st = site.GetType();
                        if (st.GetField("scope").GetValue(site).ToString() != "Foreign") continue;
                        foreign++;
                        if ((st.GetField("where").GetValue(site) as string ?? "").Contains("mask '"))
                            masks++;
                    }

                    if (foreign > 0 && foreign == masks)
                        text.AppendLine("        a mask is the only thing protecting this name: " +
                                        (string)ft.GetField("name").GetValue(finding));
                }
                foreach (var reason in reasons.OrderByDescending(r => r.Value))
                    text.AppendLine("        " + reason.Value.ToString().PadLeft(4) + "  " + reason.Key);
            }

            return problems;
        }

        /// <summary>
        /// The state Ctrl+Z leaves behind, which no flag comparison can see.
        ///
        /// The component's record of what the copies carry is serialised state and Undo rewrites
        /// it; the copies are files and it does not. Undoing a checkbox change therefore restores a
        /// record from before the copies were remade, with every flag agreeing with every other
        /// flag and every hash still matching - while the folder holds controllers renaming names
        /// the restored record has never heard of. Adopting those ships an avatar calling one
        /// parameter two different things.
        ///
        /// Reproduced here by hand rather than by pressing Ctrl+Z, which is the same state.
        /// </summary>
        private static List<string> CheckUndoDesync(MeshProtectRoot settings,
                                                    VRCAvatarDescriptor descriptor)
        {
            var problems = new List<string>();
            bool wasExpressions = settings.obfuscateExpressionParameters;

            try
            {
                settings.obfuscateExpressionParameters = false;
                MeshProtectControllers.Prepare(settings, descriptor, new List<string>());
                var before = settings.renamedParameters
                    .Select(r => new MeshProtectRoot.RenamedParameter
                    {
                        original = r.original,
                        obfuscated = r.obfuscated
                    })
                    .ToList();

                settings.obfuscateExpressionParameters = true;
                MeshProtectControllers.Prepare(settings, descriptor, new List<string>());
                int after = settings.renamedParameters.Count;

                if (after <= before.Count)
                {
                    problems.Add("this avatar renames the same amount either way, so there is " +
                                 "nothing for this check to catch");
                    return problems;
                }

                // Exactly what an undo restores: the earlier record, the later copies.
                settings.renamedParameters = before;
                settings.preparedWithExpressionParameters = false;
                settings.obfuscateExpressionParameters = false;

                var state = MeshProtectControllers.Inspect(settings, descriptor, out string detail);
                if (state != MeshProtectControllers.State.Stale)
                    problems.Add("Inspect reports " + state + " for copies carrying " +
                                 (after - before.Count) + " name(s) the record does not list");
            }
            finally
            {
                settings.obfuscateExpressionParameters = wasExpressions;
            }

            return problems;
        }

        /// <summary>
        /// A name that appears only in a layer's avatar mask must not be cleared for renaming.
        ///
        /// The mask is a list of transform paths, so it addresses objects by name exactly as a curve
        /// does - and it belongs to the artist: the controller copy references it by GUID, so this
        /// tool cannot rewrite it. Built here rather than looked for, because whether the avatar in
        /// this project happens to have a mask pointing at a renameable object is not something the
        /// rule should depend on. Renaming such an object leaves the mask pointing at nothing: an
        /// exclusion mask stops excluding, an inclusion mask turns the layer into a dead toggle.
        /// </summary>
        private static List<string> CheckMaskProtects()
        {
            // Both places a mask can live. The second one is the one that got away: a mask stored
            // inside the controller's own file was called "ours" and cleared, and CopyOutOfItsFile
            // is a byte copy, so an artist's sub-asset mask arrives in the copy looking exactly like
            // one this tool made. Nothing writes a mask either way - that is the whole rule - so a
            // test that only covers a mask in its own file leaves half the rule unwatched.
            var problems = new List<string>();
            foreach (bool inTheController in new[] { false, true })
                foreach (var problem in MaskCase(inTheController))
                    problems.Add((inTheController ? "mask inside the controller: "
                                                  : "mask in its own file: ") + problem);
            return problems;
        }

        private static List<string> MaskCase(bool inTheController)
        {
            var problems = new List<string>();

            var type = Type.GetType("MeshProtect.MeshProtectObjectNames, MeshProtect.Editor");
            var optionsType = type?.GetNestedType("Options", System.Reflection.BindingFlags.NonPublic);
            var survey = type?.GetMethod("Survey", System.Reflection.BindingFlags.Static |
                                                   System.Reflection.BindingFlags.NonPublic);
            if (survey == null || optionsType == null)
                return new List<string> { "MeshProtectObjectNames.Survey not found" };

            if (!AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));

            GameObject avatar = null;
            try
            {
                var controller = AnimatorController.CreateAnimatorControllerAtPath(
                    TempFolder + "/masked.controller");

                var mask = new AvatarMask { transformCount = 1 };
                mask.SetTransformPath(0, "SyntheticMasked");
                mask.SetTransformActive(0, false);

                if (inTheController)
                {
                    AssetDatabase.AddObjectToAsset(mask, controller);
                    AssetDatabase.SaveAssets();
                }
                else
                {
                    AssetDatabase.CreateAsset(mask, TempFolder + "/synthetic.mask");
                }

                var layers = controller.layers;
                layers[0].avatarMask = mask;
                controller.layers = layers;

                avatar = new GameObject("MPDiagMaskAvatar");
                var child = new GameObject("SyntheticMasked");
                child.transform.SetParent(avatar.transform, false);

                var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
                descriptor.baseAnimationLayers = new[]
                {
                    new VRCAvatarDescriptor.CustomAnimLayer
                    {
                        type = VRCAvatarDescriptor.AnimLayerType.FX,
                        isDefault = false,
                        animatorController = controller,
                    }
                };
                descriptor.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];

                // The controller itself IS rewritten by this build. The mask inside it still is not,
                // which is the whole point: being allowed to write the controller says nothing about
                // being allowed to write what the controller points at.
                var options = Activator.CreateInstance(optionsType);
                var set = optionsType.GetField("rewritable").GetValue(options);
                set.GetType().GetMethod("Add").Invoke(set, new object[] { controller });

                var findings = (System.Collections.IEnumerable)survey.Invoke(null, new[] { descriptor, options });

                bool found = false;
                foreach (var finding in findings)
                {
                    if ((string)finding.GetType().GetField("name").GetValue(finding) != "SyntheticMasked")
                        continue;

                    found = true;
                    if (finding.GetType().GetField("blockedBy").GetValue(finding) == null)
                        problems.Add("'SyntheticMasked' was cleared for renaming, but a layer's avatar " +
                                     "mask lists it by path and this build never rewrites a mask");
                }

                if (!found) problems.Add("the survey never saw 'SyntheticMasked'");
            }
            finally
            {
                if (avatar != null) UnityEngine.Object.DestroyImmediate(avatar);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            return problems;
        }

        /// <summary>
        /// Two PhysBones whose prefixes nest must leave both families alone.
        ///
        /// "_Squish" is one of the eight suffixes a PhysBone generates, so a second bone named
        /// "A_Squish" is folded into "A" as though it were a name "A" produced. The fold is not
        /// transitive: "A_Squish_IsGrabbed" is neither "A" plus a suffix nor reachable once
        /// "A_Squish" has been folded away, so it used to be handed a name of its own while the
        /// component it belongs to had its prefix rewritten to match "A" - after which the bone
        /// reports grabs under a name nothing is listening for. Everything about the avatar looks
        /// right, including this tool's own report.
        /// </summary>
        private static List<string> CheckNestedPhysBonePrefixes()
        {
            var type = Type.GetType("MeshProtect.MeshProtectParameters, MeshProtect.Editor");
            var optionsType = type?.GetNestedType("Options", System.Reflection.BindingFlags.NonPublic);
            var survey = type?.GetMethod("Survey", System.Reflection.BindingFlags.Static |
                                                   System.Reflection.BindingFlags.NonPublic);
            if (survey == null || optionsType == null)
                return new List<string> { "MeshProtectParameters.Survey not found" };

            var physBone = FindType("VRCPhysBone");
            var parameterField = physBone?.GetField("parameter");
            if (parameterField == null)
                return new List<string> { "VRCPhysBone (or its 'parameter' field) not found, so " +
                                          "this check cannot build the shape it is about" };

            if (!AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));

            var problems = new List<string>();
            GameObject avatar = null;
            try
            {
                var controller = AnimatorController.CreateAnimatorControllerAtPath(
                    TempFolder + "/nested.controller");

                // What the animator sees. The bones supply the prefixes; the runtime supplies these.
                controller.AddParameter("A_IsGrabbed", AnimatorControllerParameterType.Bool);
                controller.AddParameter("A_Squish_IsGrabbed", AnimatorControllerParameterType.Bool);

                avatar = new GameObject("MPDiagNestedAvatar");
                foreach (var prefix in new[] { "A", "A_Squish" })
                {
                    var bone = new GameObject("bone " + prefix);
                    bone.transform.SetParent(avatar.transform, false);
                    parameterField.SetValue(bone.AddComponent(physBone), prefix);
                }

                var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
                descriptor.baseAnimationLayers = new[]
                {
                    new VRCAvatarDescriptor.CustomAnimLayer
                    {
                        type = VRCAvatarDescriptor.AnimLayerType.FX,
                        isDefault = false,
                        animatorController = controller,
                    }
                };
                descriptor.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];

                var options = Activator.CreateInstance(optionsType);
                var set = optionsType.GetField("rewritable").GetValue(options);
                set.GetType().GetMethod("Add").Invoke(set, new object[] { controller });

                var findings = (System.Collections.IEnumerable)survey.Invoke(null, new[] { descriptor, options });

                bool found = false;
                foreach (var finding in findings)
                {
                    var ft = finding.GetType();
                    if ((string)ft.GetField("name").GetValue(finding) != "A_Squish_IsGrabbed") continue;

                    found = true;
                    if (ft.GetField("blockedBy").GetValue(finding) == null)
                        problems.Add("'A_Squish_IsGrabbed' was cleared for renaming while the bone " +
                                     "that generates it has its prefix folded into 'A', so the two " +
                                     "halves would be renamed apart and the bone would stop " +
                                     "reporting grabs");
                }

                if (!found)
                    problems.Add("the survey never saw 'A_Squish_IsGrabbed' as a name of its own - " +
                                 "if the fold has been made transitive this check needs rewriting " +
                                 "rather than deleting");
            }
            finally
            {
                if (avatar != null) UnityEngine.Object.DestroyImmediate(avatar);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            return problems;
        }

        /// <summary>
        /// The older face tracking parameter names must not be renameable, and ordinary ones must.
        ///
        /// Current VRCFaceTracking namespaces everything, so the slash rule covers it. The version
        /// before it namespaced nothing - "JawOpen", "MouthSmileLeft", "LeftEyeLid" - and those
        /// names do not contain "Tracking" either, so until the list went in, the only thing between
        /// them and the expression option was whether the author's controller happened to be named
        /// after face tracking. Merged into FX by Modular Avatar, it is not.
        ///
        /// The second half of this check matters as much as the first: a rule that blocked these by
        /// blocking everything shaped vaguely like them would pass the first half and quietly cost
        /// the avatar most of its obfuscation.
        /// </summary>
        private static List<string> CheckFaceTrackingNames()
        {
            var type = Type.GetType("MeshProtect.MeshProtectParameters, MeshProtect.Editor");
            var optionsType = type?.GetNestedType("Options", System.Reflection.BindingFlags.NonPublic);
            var survey = type?.GetMethod("Survey", System.Reflection.BindingFlags.Static |
                                                   System.Reflection.BindingFlags.NonPublic);
            if (survey == null || optionsType == null)
                return new List<string> { "MeshProtectParameters.Survey not found" };

            if (!AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));

            // Left: must be kept. Right: must still be renameable, on the same avatar, in the same
            // controller, under the same options.
            var tracked = new[] { "JawOpen", "MouthSmileLeft", "LeftEyeLid", "EyesX", "SmileSad" };
            var ordinary = new[] { "Outfit_Swimsuit", "HairToggle", "MyOwnJaw" };

            var problems = new List<string>();
            GameObject avatar = null;
            try
            {
                // The name matters. Calling this "facetracking.controller" made the controller-name
                // heuristic judge the whole thing foreign, every parameter in it was kept for that
                // reason, and the half of this check that is about the name list passed without
                // testing anything. A neutral name is the shape being tested.
                var controller = AnimatorController.CreateAnimatorControllerAtPath(
                    TempFolder + "/plainfx.controller");
                foreach (var name in tracked.Concat(ordinary))
                    controller.AddParameter(name, AnimatorControllerParameterType.Float);

                avatar = new GameObject("MPDiagFaceTrackingAvatar");
                var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
                descriptor.baseAnimationLayers = new[]
                {
                    new VRCAvatarDescriptor.CustomAnimLayer
                    {
                        // Deliberately not called anything face-tracking-ish, which is the shape the
                        // controller-name heuristic cannot see.
                        type = VRCAvatarDescriptor.AnimLayerType.FX,
                        isDefault = false,
                        animatorController = controller,
                    }
                };
                descriptor.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];

                var options = Activator.CreateInstance(optionsType);
                var set = optionsType.GetField("rewritable").GetValue(options);
                set.GetType().GetMethod("Add").Invoke(set, new object[] { controller });

                var findings = (System.Collections.IEnumerable)survey.Invoke(null, new[] { descriptor, options });

                var verdicts = new Dictionary<string, string>(StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var finding in findings)
                {
                    var ft = finding.GetType();
                    string name = (string)ft.GetField("name").GetValue(finding);
                    seen.Add(name);
                    verdicts[name] = (string)ft.GetField("blockedBy").GetValue(finding);
                }

                foreach (var name in tracked)
                {
                    if (!seen.Contains(name)) { problems.Add("the survey never saw '" + name + "'"); continue; }
                    if (verdicts[name] == null)
                        problems.Add("'" + name + "' was cleared for renaming - VRCFaceTracking " +
                                     "drives it by that exact name and nothing here rewrites what " +
                                     "the OSC application sends");
                }

                foreach (var name in ordinary)
                {
                    if (!seen.Contains(name)) { problems.Add("the survey never saw '" + name + "'"); continue; }
                    if (verdicts[name] != null)
                        problems.Add("'" + name + "' was kept (" + verdicts[name] + "), but it is an " +
                                     "ordinary avatar parameter - a face tracking rule this broad " +
                                     "would cost the avatar most of its obfuscation");
                }
            }
            finally
            {
                if (avatar != null) UnityEngine.Object.DestroyImmediate(avatar);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            return problems;
        }

        /// <summary>
        /// A component nobody has configured protects everything it can.
        ///
        /// THIS IS A PRODUCT DECISION, NOT A SAFETY INVARIANT. Everything else in this file fails
        /// when the tool would build a broken avatar; this one fails when the tool would build a
        /// less obfuscated one. Turning a default back off is a decision somebody is entitled to
        /// make - and if they make it, this check is the thing to edit, not the thing to argue
        /// with. A test that has to be edited before the tool can be made safer is the worst shape
        /// this project has, and calling this an invariant is how a file ends up in that shape.
        ///
        /// It is here because a default is otherwise invisible: it can go back the other way in a
        /// merge and nothing else would fail. One option ships on with a cost that lands outside
        /// the author's project - expression parameters reset saved values for everyone who already
        /// has the avatar, and rename names an OSC application may be sending - and the inspector
        /// and all three shipped documents say so.
        /// </summary>
        private static List<string> CheckDefaults()
        {
            var probe = new GameObject("MPDiagDefaults");
            try
            {
                var fresh = probe.AddComponent<MeshProtectRoot>();
                var problems = new List<string>();

                foreach (var option in new[]
                         {
                             ("obfuscateAnimatorNames", fresh.obfuscateAnimatorNames),
                             ("obfuscateParameterNames", fresh.obfuscateParameterNames),
                             ("obfuscateExpressionParameters", fresh.obfuscateExpressionParameters),
                             ("copySeparateBlendTrees", fresh.copySeparateBlendTrees),
                             ("obfuscateObjectNames", fresh.obfuscateObjectNames),
                         })
                {
                    if (!option.Item2)
                        problems.Add(option.Item1 + " is off on a component nobody has touched, so " +
                                     "an author who only sets a password does not get it");
                }

                if (fresh.HasKey)
                    problems.Add("a new component already has a password - protection would be " +
                                 "keyed to digits the author never chose and cannot know");

                return problems;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }
        }

        /// <summary>A type by name, from whichever assembly has it - the SDK moves them about.</summary>
        private static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }

                foreach (var type in types)
                    if (type != null && type.Name == name) return type;
            }
            return null;
        }

        /// <summary>
        /// A prepared copy that has changed on disk since it was recorded must read as stale.
        ///
        /// Everything else the inspector checks is derived from the component and the avatar, so the
        /// two can agree with each other while the file the build will actually adopt is a different
        /// file - Undo reverts the component but not the assets, and a Prepare that throws halfway
        /// leaves a copy nothing vouches for. The copy is touched here in a way that changes no name
        /// the record lists, so the only thing that can notice is the comparison against the file.
        /// </summary>
        private static List<string> CheckCopyMatchesRecord(MeshProtectRoot settings,
                                                           VRCAvatarDescriptor descriptor)
        {
            var problems = new List<string>();

            MeshProtectControllers.Prepare(settings, descriptor, new List<string>());

            var state = MeshProtectControllers.Inspect(settings, descriptor, out string detail);
            if (state != MeshProtectControllers.State.UpToDate)
                return new List<string> { "copies straight out of Prepare already read " + state +
                                          " (" + detail + ")" };

            var entry = settings.preparedControllers.FirstOrDefault(e => !string.IsNullOrEmpty(e.copyPath));
            if (entry == null)
                return new List<string> { "Prepare recorded no copy to test against" };

            var copy = AssetDatabase.LoadAssetAtPath<AnimatorController>(entry.copyPath);
            if (copy == null)
                return new List<string> { "the recorded copy is not on disk: " + entry.copyPath };

            try
            {
                copy.AddLayer("MPDiagTouched");
                EditorUtility.SetDirty(copy);
                AssetDatabase.SaveAssets();

                state = MeshProtectControllers.Inspect(settings, descriptor, out detail);
                if (state != MeshProtectControllers.State.Stale)
                    problems.Add("Inspect reports " + state + " for a copy edited on disk after it was " +
                                 "recorded, so the build would adopt a file nothing vouches for");
            }
            finally
            {
                // Leave the project holding copies that match their record again.
                try { MeshProtectControllers.Prepare(settings, descriptor, new List<string>()); }
                catch (Exception e) { problems.Add("could not restore the copies: " + e.Message); }
            }

            return problems;
        }

        /// <summary>
        /// A controller whose state machine lives in another file must clear no object name.
        ///
        /// The object survey hands out "this build rewrites it" on the strength of the controller,
        /// while the rewrite refuses any state or state machine stored outside the controller's own
        /// file - and real avatars are built that way. When the two disagree the name is cleared,
        /// the state is skipped, and the animation goes on addressing an object that has just been
        /// renamed. Built here rather than looked for, because the avatar in this project does not
        /// happen to have that shape.
        /// </summary>
        private static List<string> CheckStructureElsewhere()
        {
            var problems = new List<string>();

            var type = Type.GetType("MeshProtect.MeshProtectObjectNames, MeshProtect.Editor");
            var optionsType = type?.GetNestedType("Options", System.Reflection.BindingFlags.NonPublic);
            var survey = type?.GetMethod("Survey", System.Reflection.BindingFlags.Static |
                                                   System.Reflection.BindingFlags.NonPublic);
            if (survey == null || optionsType == null)
                return new List<string> { "MeshProtectObjectNames.Survey not found" };

            if (!AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));

            GameObject avatar = null;
            try
            {
                var controller = AnimatorController.CreateAnimatorControllerAtPath(
                    TempFolder + "/structure.controller");

                // The shape that matters: the state machine is its own asset, so a byte copy of the
                // controller does not bring it along and nothing may write to it.
                var outside = new AnimatorStateMachine { name = "outside" };
                AssetDatabase.CreateAsset(outside, TempFolder + "/outside.asset");

                var clip = new AnimationClip { name = "moves the child" };
                AnimationUtility.SetEditorCurve(
                    clip,
                    new EditorCurveBinding { path = "SyntheticChild", type = typeof(Transform),
                                             propertyName = "m_LocalPosition.x" },
                    AnimationCurve.Linear(0, 0, 1, 1));
                AssetDatabase.CreateAsset(clip, TempFolder + "/moves.anim");

                var state = outside.AddState("plays it");
                state.motion = clip;

                var layers = controller.layers;
                layers[0].stateMachine = outside;
                controller.layers = layers;

                avatar = new GameObject("MPDiagSyntheticAvatar");
                var child = new GameObject("SyntheticChild");
                child.transform.SetParent(avatar.transform, false);

                var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
                descriptor.baseAnimationLayers = new[]
                {
                    new VRCAvatarDescriptor.CustomAnimLayer
                    {
                        type = VRCAvatarDescriptor.AnimLayerType.FX,
                        isDefault = false,
                        animatorController = controller,
                    }
                };
                descriptor.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];

                var options = Activator.CreateInstance(optionsType);
                var set = optionsType.GetField("rewritable").GetValue(options);
                set.GetType().GetMethod("Add").Invoke(set, new object[] { controller });

                var findings = (System.Collections.IEnumerable)survey.Invoke(null, new[] { descriptor, options });

                bool found = false;
                foreach (var finding in findings)
                {
                    if ((string)finding.GetType().GetField("name").GetValue(finding) != "SyntheticChild")
                        continue;

                    found = true;
                    if (finding.GetType().GetField("blockedBy").GetValue(finding) == null)
                        problems.Add("'SyntheticChild' was cleared for renaming, but the state that " +
                                     "animates it lives outside the controller and is never rewritten");
                }

                if (!found) problems.Add("the survey never saw 'SyntheticChild'");
            }
            finally
            {
                if (avatar != null) UnityEngine.Object.DestroyImmediate(avatar);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            return problems;
        }

        /// <summary>The same segment-wise substitution the tool does, for comparing against it.</summary>
        private static string RewritePath(string path, Dictionary<string, string> map)
        {
            if (string.IsNullOrEmpty(path) || map.Count == 0) return path;

            var segments = path.Split('/');
            for (int i = 0; i < segments.Length; i++)
                if (map.TryGetValue(segments[i], out var replacement)) segments[i] = replacement;

            return string.Join("/", segments);
        }

        /// <summary>
        /// Put an AnimatorOverrideController in a playable layer, wrapping what was there.
        ///
        /// Legal on a real avatar, and the survey has to treat it as somebody else's controller
        /// rather than as an empty slot. Returns the layer swapped, or null if there was none.
        /// </summary>
        private static string OverrideALayer(GameObject clone)
        {
            var descriptor = clone.GetComponent<VRCAvatarDescriptor>();
            var layers = descriptor.baseAnimationLayers;

            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].isDefault) continue;
                if (layers[i].type == VRCAvatarDescriptor.AnimLayerType.FX) continue;

                var inner = layers[i].animatorController as AnimatorController;
                if (inner == null) continue;

                layers[i].animatorController = new AnimatorOverrideController(inner);
                descriptor.baseAnimationLayers = layers;
                return layers[i].type.ToString();
            }

            return null;
        }

        /// <summary>
        /// The map must mean exactly what it says.
        ///
        /// Remap used to strip PhysBone suffixes and retry the prefix, so an avatar with a
        /// parameter called "Emote" and a separate one called "Emote_Hit" got the second renamed
        /// on the strength of the first being cleared - in the places this build rewrites and
        /// nowhere else. Nothing reported it, because from the build's point of view it worked.
        /// </summary>
        private static List<string> CheckRemap()
        {
            var problems = new List<string>();

            var type = Type.GetType("MeshProtect.MeshProtectParameters, MeshProtect.Editor");
            if (type == null) return new List<string> { "MeshProtectParameters not found" };

            var remap = type.GetMethod("Remap", System.Reflection.BindingFlags.Static |
                                                System.Reflection.BindingFlags.NonPublic |
                                                System.Reflection.BindingFlags.Public);
            if (remap == null) return new List<string> { "Remap not found" };

            var map = new Dictionary<string, string> { { "Emote", "kodabivu" } };

            string Call(string name) => (string)remap.Invoke(null, new object[] { name, map });

            if (Call("Emote") != "kodabivu")
                problems.Add("a name in the map was not renamed");
            if (Call("Emote_Hit") != "Emote_Hit")
                problems.Add("'Emote_Hit' was renamed because 'Emote' was cleared - they are only " +
                             "the same parameter when a PhysBone says so");
            if (Call("Untouched") != "Untouched")
                problems.Add("a name outside the map was renamed");

            // What a real PhysBone prefix looks like once BuildMap has written it out.
            var withSuffixes = new Dictionary<string, string>
            {
                { "Ear", "zolumeva" }, { "Ear_IsGrabbed", "zolumeva_IsGrabbed" }
            };
            if ((string)remap.Invoke(null, new object[] { "Ear_IsGrabbed", withSuffixes }) !=
                "zolumeva_IsGrabbed")
                problems.Add("a written-out PhysBone suffix was not renamed");

            return problems;
        }

        /// <summary>
        /// Give the clone a reference the build has no way to rewrite: an Animator that is not a
        /// playable layer, declaring a parameter the prepared copies have already renamed.
        /// </summary>
        private static void Sabotage(GameObject clone, string parameter)
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));

            var controller = AnimatorController.CreateAnimatorControllerAtPath(
                TempFolder + "/sabotage.controller");
            controller.AddParameter(parameter, AnimatorControllerParameterType.Float);

            var child = new GameObject("MPDiagSabotage");
            child.transform.SetParent(clone.transform, false);
            child.AddComponent<Animator>().runtimeAnimatorController = controller;
        }

        /// <summary>
        /// The build hook, through reflection: calling it directly would make this assembly depend
        /// on the SDK's build pipeline for nothing.
        /// </summary>
        private static bool InvokeHook(GameObject clone)
        {
            var type = Type.GetType("MeshProtect.MeshProtectBuildHook, MeshProtect.Editor");
            if (type == null) throw new Exception("MeshProtectBuildHook not found");

            var hook = Activator.CreateInstance(type);
            var method = type.GetMethod("OnPreprocessAvatar");
            if (method == null) throw new Exception("OnPreprocessAvatar not found");

            return (bool)method.Invoke(hook, new object[] { clone });
        }

        private static Dictionary<string, string> HashArtistAssets()
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var folder in ArtistFolders)
            {
                string full = Path.Combine(Directory.GetCurrentDirectory(), folder);
                if (!Directory.Exists(full)) continue;

                foreach (var file in Directory.GetFiles(full, "*.*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    hashes[file] = Hash(file);
                }
            }

            return hashes;
        }

        private static string Hash(string file)
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(file))
                return BitConverter.ToString(md5.ComputeHash(stream));
        }

        private static void Write(StringBuilder text)
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), "mp-harness-report.txt");
            File.WriteAllText(path, text.ToString());
            Debug.Log("[" + Stamp + "] wrote " + path);
        }
    }
}
#endif
