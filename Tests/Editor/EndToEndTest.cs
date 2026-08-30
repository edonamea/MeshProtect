using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using MeshProtect;

namespace MPTest
{
    /// <summary>
    /// Drives the real upload path: build a synthetic avatar, run the preprocess hook the VRChat
    /// SDK would run, and check what came out the other side.
    ///
    /// This is the acceptance test for the non-destructive rewrite. Everything else verifies the
    /// cipher; this verifies that pressing Build and Publish actually produces a protected avatar
    /// with a working unlock menu and no authoring component left behind.
    /// </summary>
    public static class EndToEndTest
    {
        private static readonly StringBuilder Log = new StringBuilder();
        private static int failures;

        private static void Say(string line)
        {
            Log.AppendLine(line);
            Debug.Log("[E2E] " + line);
        }

        private static void Check(bool ok, string label, string detail)
        {
            if (!ok) failures++;
            Say($"{(ok ? "PASS" : "FAIL")}  {label}  {detail}");
        }

        /// <summary>
        /// Entry point for a Unity launch of its own: run, then end the process with the result.
        /// The work is in RunCore so that a single launch can run every suite - each Exit here is an
        /// editor start, an asset import and a script compile, and four of those is most of the
        /// time a change costs to verify.
        /// </summary>
        public static void Run() => EditorApplication.Exit(RunCore());

        /// <summary>Runs the suite and returns the exit code, without ending the process.</summary>
        public static int RunCore()
        {
            int exit = 0;
            GameObject avatar = null;
            try
            {
                Say("=== Mesh Protect end-to-end (upload path) ===");

                var lilToon = Shader.Find("lilToon");
                if (lilToon == null) throw new Exception("lilToon shader not found in this project.");

                avatar = BuildAvatar(lilToon, out var settings, out var renderer, out Mesh original);
                var variant = settings.variant;
                uint key = MeshProtectCipher.PackDigits(settings.keyDigits, variant);
                string family = variant.shaderName;

                Say($"      avatar built; password {settings.KeyAsString()}, protection {family}");

                var menuSet = new HashSet<string>(variant.parameterNames);
                var bitSet = new HashSet<string>(variant.bitNames);
                var originalVertices = original.vertices;

                // The SDK calls this on the clone it builds from. Nothing else in the pipeline is
                // involved, so if this returns true the avatar is ready to upload.
                bool ok = new MeshProtectBuildHook().OnPreprocessAvatar(avatar);
                Check(ok, "e2e/preprocess-succeeds", ok ? "hook returned true" : "hook BLOCKED the build");
                if (!ok) throw new Exception("preprocess blocked; see console");

                // 1. The authoring component holds the password and must not survive.
                Check(avatar.GetComponentsInChildren<MeshProtectRoot>(true).Length == 0,
                      "e2e/component-stripped", "no MeshProtectRoot remains on the built avatar");

                // 2. The mesh must actually be displaced, and must decode back exactly.
                var built = renderer.sharedMesh;
                Check(built != original, "e2e/mesh-replaced", "renderer points at a new mesh");

                double worstMove = 0;
                var builtVertices = built.vertices;
                for (int i = 0; i < builtVertices.Length; i++)
                    worstMove = Math.Max(worstMove, (builtVertices[i] - originalVertices[i]).magnitude);
                Check(worstMove > 0.01, "e2e/mesh-displaced", $"worst vertex moved {worstMove:F4} m");

                var mode = built.tangents != null && built.tangents.Length == built.vertexCount
                    ? MeshProtectRoot.DisplacementMode.TangentSpace
                    : MeshProtectRoot.DisplacementMode.Normal;
                var check = MeshProtectSelfCheck.Verify(original, built, mode, key, variant);
                Check(check.Passed, "e2e/decodes-with-password",
                      $"worst restore error {check.worstVertexError:E3} (tol {check.tolerance:E3})");

                // 3. Materials moved onto this avatar's own shader family, with no digit leaked.
                var material = renderer.sharedMaterials[0];
                Check(MeshProtectInspector.FamilyOf(material.shader) == family,
                      "e2e/material-converted", $"shader is '{material.shader.name}'");

                Check(material.GetTag("VRCFallback", false) == "Hidden", "e2e/fallback-hidden",
                      "viewers with shaders off see nothing rather than the scrambled mesh");

                // Unity draws per-object motion vectors with its own internal shader, which never
                // runs the decode - so with the default Object mode, any motion-blur or TAA world
                // rasterises the ENCRYPTED mesh into the motion-vector buffer and smears it into
                // flicker noise around the avatar. Camera mode skips that pass entirely; measured,
                // it makes the buffer pixel-identical to an unprotected avatar's. This assertion
                // is what keeps the fix from silently falling out of the bake.
                Check(renderer.motionVectorGenerationMode == MotionVectorGenerationMode.Camera,
                      "e2e/motion-vectors-camera-only",
                      $"protected renderer writes no per-object motion vectors (mode {renderer.motionVectorGenerationMode})");

                // The shader compares the entered key's hash against this vector. If it were never
                // written, the property sits at its shader default and no password on earth
                // matches: the avatar ships permanently invisible, and every other check passes.
                var protectedMaterials = avatar.GetComponentsInChildren<Renderer>(true)
                    .SelectMany(r => r.sharedMaterials)
                    .Where(m => m != null && MeshProtectInspector.IsFamily(m, family))
                    .Distinct()
                    .ToList();
                var macs = protectedMaterials
                    .Where(m => m.HasProperty(variant.macProperty))
                    .Select(m => m.GetVector(variant.macProperty))
                    .ToList();
                bool macWritten = macs.Count == protectedMaterials.Count && macs.Count > 0 &&
                                  macs.All(v => v != Vector4.zero) &&
                                  macs.Distinct().Count() == 1;
                Check(macWritten, "e2e/mac-written",
                      macs.Count != protectedMaterials.Count
                          ? $"only {macs.Count} of {protectedMaterials.Count} materials even have " +
                            "the property"
                          : macs.Count == 0 ? "no protected material found"
                          : macs.Any(v => v == Vector4.zero)
                              ? "a material carries an all-zero verifier - no password can match it"
                              : macs.Distinct().Count() != 1
                                  ? $"{macs.Distinct().Count()} different verifiers across materials"
                                  : $"all {macs.Count} material(s) carry the same non-zero verifier");

                var leaked = variant.digitProperties
                    .Where(p => material.HasProperty(p) && Mathf.Abs(material.GetFloat(p)) > 0.0001f)
                    .ToList();
                Check(leaked.Count == 0, "e2e/no-password-in-material",
                      leaked.Count == 0 ? "all digit properties ship at 0" : "LEAKED: " + string.Join(",", leaked));

                // 4. The unlock chain: parameters, menu, FX layers.
                var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
                var parameters = descriptor.expressionParameters;
                var all = parameters?.parameters?
                    .Where(p => p != null && (menuSet.Contains(p.name) || bitSet.Contains(p.name)))
                    .ToList() ?? new List<VRCExpressionParameters.Parameter>();
                var menuInts = all.Where(p => menuSet.Contains(p.name)).ToList();
                var bits = all.Where(p => bitSet.Contains(p.name)).ToList();

                Check(menuInts.Count == MeshProtectRoot.PasswordLength, "e2e/menu-parameters",
                      $"{menuInts.Count} unsynced Int(s) for menu input");
                Check(bits.Count == MeshProtectRoot.PasswordLength * MeshProtectRoot.BitsPerDigit,
                      "e2e/transport-parameters", $"{bits.Count} synced bool(s)");
                Check(menuInts.All(p => !p.networkSynced &&
                                        p.valueType == VRCExpressionParameters.ValueType.Int),
                      "e2e/menu-parameters-unsynced", "menu Ints cost 0 bits of sync budget");

                // Ours must be mixed into the avatar's own parameters, not appended as a block that
                // anybody can circle at a glance.
                var names = parameters.parameters.Select(p => p.name).ToList();
                var indices = all.Select(p => names.IndexOf(p.name)).OrderBy(i => i).ToList();
                bool contiguous = indices.Count > 1 &&
                                  indices.Last() - indices.First() == indices.Count - 1;
                Check(!contiguous, "e2e/parameters-scattered",
                      contiguous
                          ? $"all {indices.Count} sit in one run at {indices.First()}..{indices.Last()}"
                          : $"{indices.Count} spread over slots {indices.First()}..{indices.Last()} " +
                            $"of {names.Count}");
                Check(bits.All(p => p.saved && p.networkSynced &&
                                    p.valueType == VRCExpressionParameters.ValueType.Bool),
                      "e2e/transport-saved-and-synced",
                      "all Bool, Saved (entered once) and Synced (remote viewers decode too)");
                Check(all.All(p => Mathf.Abs(p.defaultValue) < 0.0001f),
                      "e2e/parameter-defaults-locked", "every default is 0, i.e. locked");

                // Our contribution, not the avatar's total - the fixture brings its own parameters.
                int ours = all.Where(p => p.networkSynced)
                              .Sum(p => p.valueType == VRCExpressionParameters.ValueType.Bool ? 1 : 8);
                Check(ours == MeshProtectRoot.PasswordLength * MeshProtectRoot.BitsPerDigit,
                      "e2e/sync-budget",
                      $"the unlock chain adds {ours} bits to this avatar's " +
                      $"{parameters.CalcTotalCost()} total (one synced Int per digit would be 48)");

                int menuDigits = CountMenuDigits(descriptor.expressionsMenu, menuSet,
                                                 new HashSet<VRCExpressionsMenu>());
                Check(menuDigits == MeshProtectRoot.PasswordLength * MeshProtectRoot.MaxDigit,
                      "e2e/menu-complete",
                      $"{menuDigits} digit controls (expected " +
                      $"{MeshProtectRoot.PasswordLength * MeshProtectRoot.MaxDigit}: 6 positions x 8 digits)");

                // And NO way back to nothing entered - the reset control is deliberately gone,
                // on the word of somebody wearing the thing. A six digit password (the default)
                // cannot over-type, so the button's ordinary effect was clearing a
                // correctly-entered password by accident; a wrong digit within the password's
                // length just needs its dial re-selected; and the one real brick - a short
                // password plus a touched spare dial - has a VRChat-native escape in Reset Avatar
                // Data. This assertion pins the REMOVAL, so the button cannot drift back in as a
                // kindness without meeting the reasoning that took it out.
                int resets = CountMenuControls(descriptor.expressionsMenu, menuSet,
                                               new HashSet<VRCExpressionsMenu>(),
                                               MeshProtectRoot.MaxDigit + 1);
                Check(resets == 0, "e2e/menu-has-no-reset",
                      resets == 0
                          ? "no reset control: dials only, nothing to clear a saved password by " +
                            "accident"
                          : $"{resets} control(s) write the old reset sentinel " +
                            $"({MeshProtectRoot.MaxDigit + 1}) - the button was removed " +
                            "deliberately; if it is coming back, this test is where the reasoning " +
                            "lives");

                var fx = descriptor.baseAnimationLayers
                    .FirstOrDefault(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX)
                    .animatorController as AnimatorController;
                int packLayers = fx == null ? 0 : fx.layers.Count(l => variant.packLayerNames.Contains(l.name));
                int decodeLayers = fx == null ? 0 : fx.layers.Count(l => variant.decodeLayerNames.Contains(l.name));
                Check(packLayers == MeshProtectRoot.PasswordLength &&
                      decodeLayers == MeshProtectRoot.PasswordLength, "e2e/fx-layers",
                      $"{packLayers} pack + {decodeLayers} decode layers");

                // Ours are not the only layers that have to arrive. The FX controller ships as a
                // private copy of the avatar's, and every way of failing to make that copy used to
                // fall through to "create an empty one instead" - an avatar with no toggles, no
                // outfits and no expressions, uploaded without a word. Every other check here
                // survives that, this one included until it was written: the names are not readable
                // because there are no names left.
                bool Ours(string layerName) =>
                    variant.packLayerNames.Contains(layerName) ||
                    variant.decodeLayerNames.Contains(layerName) ||
                    variant.padLayerNames.Contains(layerName);

                var theirs = (fx?.layers ?? new AnimatorControllerLayer[0])
                    .Where(l => !Ours(l.name)).ToList();
                int theirStates = theirs.Sum(l => l.stateMachine == null ? 0 : l.stateMachine.states.Length);
                Check(theirs.Count >= 1 && theirStates >= 2, "e2e/fx-content-preserved",
                      theirs.Count >= 1 && theirStates >= 2
                          ? $"the avatar's own {theirs.Count} FX layer(s) and {theirStates} state(s) " +
                            "came through the copy"
                          : $"the avatar's FX content is gone: {theirs.Count} layer(s), " +
                            $"{theirStates} state(s) - this upload has no toggles at all");

                // 5. No literal naming the tool anywhere that ships. A scanner that recognises
                //    every avatar this tool produced would undo the point of per-avatar variants.
                // Nothing that ships may name the tool, describe what a layer does, or carry an
                // index that maps a name to a password position.
                var offenders = new List<string>();
                var shipped = new List<string>();
                if (fx != null) shipped.AddRange(fx.layers.Select(l => "FX layer " + l.name));
                shipped.AddRange(CollectMenuNames(descriptor.expressionsMenu,
                                                  new HashSet<VRCExpressionsMenu>())
                                 .Select(n => "menu asset " + n));
                shipped.AddRange(all.Select(p => "parameter " + p.name));
                shipped.Add("shader " + variant.shaderName);

                foreach (var item in shipped)
                {
                    string value = item.Substring(item.IndexOf(' ') + 1);
                    if (item.Contains("MeshProtect") || value.Contains("pack") ||
                        value.Contains("decode") || value.Contains("Protect"))
                        offenders.Add(item + " (describes the tool)");
                    else if (!item.StartsWith("FX layer") && value.Any(char.IsDigit))
                        offenders.Add(item + " (carries an index)");
                }
                Check(offenders.Count == 0, "e2e/no-tool-signature",
                      offenders.Count == 0
                          ? $"{shipped.Count} shipped names, none naming the tool or indexed"
                          : string.Join("; ", offenders.Take(4)));

                // The fixture's animator was deliberately named after what it does. None of those
                // words may reach the bundle.
                foreach (var l in descriptor.baseAnimationLayers ??
                                  new VRCAvatarDescriptor.CustomAnimLayer[0])
                    Say($"      shipped layer {l.type} isDefault={l.isDefault} " +
                        $"controller={(l.animatorController == null ? "<null>" : l.animatorController.name)}");

                // The FX controller is this build's own copy, so its names are fair game. The
                // Gesture controller belongs to the avatar and is deliberately left alone: renaming
                // it meant copying it first, and copying somebody else's controller is what left a
                // real avatar stuck in a pose it was not in.
                var fxReadable = new[] { "DanceToggle", "DanceMachine", "Kiss_On", "Outfit_Swimsuit" };
                var otherReadable = new[] { "HandSigns", "SignMachine", "Wave_On", "Gesture_Wave" };
                var readable = fxReadable;
                var shippedAnimator = (descriptor.baseAnimationLayers ??
                                       new VRCAvatarDescriptor.CustomAnimLayer[0])
                    .Select(l => l.animatorController as AnimatorController)
                    .Where(c => c != null)
                    .SelectMany(AnimatorNames)
                    .ToList();
                var survived = fxReadable.Where(r => shippedAnimator.Contains(r)).ToList();
                Check(survived.Count == 0, "e2e/fx-names-obfuscated",
                      survived.Count == 0
                          ? $"{shippedAnimator.Count} animator names shipped, none from FX readable"
                          : "still readable in the upload: " + string.Join(", ", survived));

                // The other half of the same guarantee: nothing outside the FX copy was touched.
                // A tool that quietly starts rewriting the avatar's own controllers again would
                // pass the check above and fail here.
                var intact = otherReadable.Where(r => shippedAnimator.Contains(r)).ToList();
                Check(intact.Count == otherReadable.Length, "e2e/other-controllers-untouched",
                      intact.Count == otherReadable.Length
                          ? "the Gesture controller ships exactly as the avatar had it"
                          : "the avatar's own controller was rewritten; missing " +
                            string.Join(", ", otherReadable.Except(intact)));

                // VRChat replaces proxy animations with real locomotion at runtime, matching them
                // by name. Renaming them leaves the avatar playing the placeholder pose instead:
                // crouch does nothing, and standing back up does nothing, on every avatar built
                // with this tool. It is the one class of name that has to survive obfuscation.
                var proxies = shippedAnimator
                    .Where(n => n != null && n.StartsWith("proxy_", StringComparison.OrdinalIgnoreCase))
                    .Distinct().ToList();
                // Both of them, not one of them. "At least one survived" is satisfied by an
                // avatar that lost every proxy but one.
                Check(proxies.Count == 2, "e2e/proxy-animations-survive",
                      proxies.Count == 2
                          ? $"both proxy animations still named for the client to find"
                          : "every proxy animation was renamed - the client has nothing to " +
                            "substitute, so locomotion plays the placeholder pose and the avatar " +
                            "cannot stand up");

                // ...and every state of ours must agree with the avatar about Write Defaults.
                //
                // This assertion used to require the opposite - that none of ours ever write
                // defaults - and it was green for the release whose avatars could not move their
                // faces in MMD worlds. Those worlds switch off the FX layers holding the
                // expressions so their dance can drive the blend shapes, and a controller with the
                // two settings mixed does not release them. Measured on the avatar that reported
                // it: 79 states across its seven FX layers, all Write Defaults ON, and twelve
                // layers of ours appended with it off.
                var ourLayerNames = new HashSet<string>(
                    variant.packLayerNames.Concat(variant.decodeLayerNames)
                        .Concat(variant.padLayerNames));

                int avatarOn = 0, avatarOff = 0;
                foreach (var l in (fx?.layers ?? new AnimatorControllerLayer[0])
                         .Where(l => !ourLayerNames.Contains(l.name)))
                    CountWriteDefaults(l.stateMachine, ref avatarOn, ref avatarOff,
                                       new HashSet<AnimatorStateMachine>());

                // The same majority rule the build uses, restated here rather than borrowed, so a
                // change to it has to be made twice on purpose.
                bool expected = avatarOff == 0 || avatarOn >= avatarOff;

                // WHAT THIS DOES AND DOES NOT PIN. The fixture's avatar runs Write Defaults ON, so
                // this catches the direction that actually shipped broken - ours going off while
                // the avatar is on - and hardcoding it off again turns this red. It cannot catch
                // the mirror image: hardcode it ON and this still passes, because the fixture has
                // no Write Defaults OFF avatar to disagree with. That direction is covered only at
                // the level below, by wd/all-off and wd/mostly-off in the unit suite, which pin
                // what the detector answers but not that the generated states follow it.

                var disagreeing = new List<string>();
                foreach (var l in (fx?.layers ?? new AnimatorControllerLayer[0])
                         .Where(l => ourLayerNames.Contains(l.name)))
                    foreach (var child in l.stateMachine?.states ?? new ChildAnimatorState[0])
                        if (child.state != null && child.state.writeDefaultValues != expected)
                            disagreeing.Add($"{l.name}/{child.state.name}");

                Check(disagreeing.Count == 0, "e2e/our-states-match-the-avatar",
                      disagreeing.Count == 0
                          ? $"the avatar runs Write Defaults {(expected ? "ON" : "OFF")} " +
                            $"({avatarOn} on, {avatarOff} off) and every generated state does too"
                          : $"{disagreeing.Count} state(s) disagree with the avatar's " +
                            $"{(expected ? "ON" : "OFF")}, e.g. " +
                            string.Join(", ", disagreeing.Take(4)));

                // Every state this tool generates must have a motion, even one that animates
                // nothing. A state with no motion at all is not something Unity is obliged to treat
                // as a state that does nothing, and one empty clip is cheaper than finding out.
                //
                // This used to say such a state writes the default value of every property the
                // CONTROLLER animates, and cite an avatar that posed wrongly and would not crouch.
                // Both halves were wrong: that avatar's pose came from a VRChat saved parameter
                // sitting at 0.24 with this tool uninstalled, and Write Defaults fills in the
                // properties bound in the same LAYER, which for these layers is nothing. Believing
                // the strong version is what talked this tool into hardcoding Write Defaults off
                // and shipping a release that could not do MMD.
                var motionless = new List<string>();
                var oursLayers = new HashSet<string>(
                    variant.packLayerNames.Concat(variant.decodeLayerNames)
                        .Concat(variant.padLayerNames));
                foreach (var l in (fx?.layers ?? new AnimatorControllerLayer[0])
                         .Where(l => oursLayers.Contains(l.name)))
                {
                    if (l.stateMachine == null) { motionless.Add(l.name + " (no state machine)"); continue; }
                    if (l.stateMachine.states.Length == 0) motionless.Add(l.name + " (no states)");
                    foreach (var child in l.stateMachine.states)
                        if (child.state != null && child.state.motion == null)
                            motionless.Add($"{l.name}/{child.state.name}");
                }
                Check(motionless.Count == 0, "e2e/our-states-have-motions",
                      motionless.Count == 0
                          ? "every generated state carries a clip"
                          : $"{motionless.Count} state(s) with no motion, e.g. " +
                            string.Join(", ", motionless.Take(4)));

                // The tool's own states used to be called "Locked" and "0".."8", which named the
                // mechanism and mapped each layer to a password position. Reserving our layers by
                // name must not stop the states inside them being renamed.
                var toolWords = new[] { "Locked", "Idle" }
                    .Concat(Enumerable.Range(0, MeshProtectRoot.MaxDigit + 1).Select(d => d.ToString()))
                    .ToList();
                var selfNamed = shippedAnimator.Where(toolWords.Contains).Distinct().ToList();
                Check(selfNamed.Count == 0, "e2e/no-tool-state-names",
                      selfNamed.Count == 0
                          ? "no state in the upload is called Locked or a bare digit"
                          : "the tool names itself through its states: " + string.Join(", ", selfNamed));

                // ...and the project copy must be exactly as the user left it. Renaming the assets
                // in place would obfuscate the upload by vandalising the source project.
                var projectNames = new List<string>();
                var missingAssets = new List<string>();
                foreach (var name in new[] { "OwnFX", "OwnGesture" })
                {
                    var asset = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                        $"Assets/_MPTestScratch/{name}.controller");
                    if (asset == null) missingAssets.Add(name);
                    else projectNames.AddRange(AnimatorNames(asset));
                }
                var lost = readable.Where(r => !projectNames.Contains(r)).ToList();
                Check(missingAssets.Count == 0 && lost.Count == 0,
                      "e2e/project-controller-untouched",
                      missingAssets.Count > 0
                          ? "the project's controller(s) are gone: " + string.Join(", ", missingAssets)
                          : lost.Count == 0
                              ? "both project controllers still have all their original names"
                              : "renaming reached into the project and destroyed: " +
                                string.Join(", ", lost));

                // 6. The validator - the same one that gates the upload - must be happy.
                var issues = MeshProtectValidator.Validate(avatar, variant);
                var fatal = issues.Where(i => i.fatal).Select(i => i.message).ToList();
                foreach (var warn in issues.Where(i => !i.fatal))
                    Say("      (warning) " + warn.message);
                Check(fatal.Count == 0, "e2e/validator-clean",
                      fatal.Count == 0 ? "no fatal issues" : string.Join(" | ", fatal));

                UnityEngine.Object.DestroyImmediate(original);

                TestNothingToProtectStillUploads(lilToon);
                TestFullParameterBudgetStillUploads(lilToon);
                TestNoPasswordStillUploads(lilToon);
                TestAlreadyProtectedMeshIsLeftAlone(lilToon);
                TestSharedMeshLeak(lilToon);
                // In use and pointed at UV6: must never ship. Pointed at UV6 with the feature
                // switched off: must not be mistaken for the first case.
                TestDisabledComponentSkips(lilToon);

                TestIdMaskCollision(lilToon, "in-use", true);
                TestIdMaskCollision(lilToon, "not-in-use", false);

                TestRendererOutsideTheAvatar(lilToon);
                TestOccupiedUv6(lilToon, "occupied", true);
                TestOccupiedUv6(lilToon, "all-zero", false);

                // A root menu with room, and one already at VRChat's limit of 8.
                // Both clip layouts, because only one of them can break the override map - and
                // which one is not something to assume.
                TestExtraMaterialSlot(lilToon);
                TestVouchedInvisibleExtraSlot(lilToon);
                TestObfuscationFallback(lilToon);
                TestOverrideControllerFxLayer(lilToon, "clip-is-own-asset", false);
                TestOverrideControllerFxLayer(lilToon, "clip-inside-controller", true);
                TestMaterialSwapAnimation(lilToon);
                TestFullRootMenu(lilToon, "has-room", 5);
                TestFullRootMenu(lilToon, "is-full", 8);

                Say(failures == 0 ? "=== ALL PASSED ===" : $"=== {failures} FAILURE(S) ===");
                exit = failures == 0 ? 0 : 1;
            }
            catch (Exception e)
            {
                Say("EXCEPTION: " + e);
                exit = 2;
            }
            finally
            {
                if (avatar != null) UnityEngine.Object.DestroyImmediate(avatar);
                foreach (var folder in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild", "Assets/_MPTestScratch" })
                    if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                AssetDatabase.Refresh();
            }

            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "e2e-result.txt"), Log.ToString());
            return exit;
        }

        // ------------------------------------------------------------------ fixture

        private static GameObject BuildAvatar(Shader lilToon, out MeshProtectRoot settings,
                                              out SkinnedMeshRenderer renderer, out Mesh original)
        {
            var root = new GameObject("E2EAvatar");
            root.AddComponent<Animator>();
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.customExpressions = false;

            var bone = new GameObject("Bone");
            bone.transform.SetParent(root.transform, false);

            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            renderer = body.AddComponent<SkinnedMeshRenderer>();

            original = BuildTestMesh(32);
            // One bone with full weight everywhere: enough for a SkinnedMeshRenderer to be valid
            // without dragging a rig into the fixture.
            var weights = new BoneWeight[original.vertexCount];
            for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            original.boneWeights = weights;
            original.bindposes = new[] { Matrix4x4.identity };

            renderer.sharedMesh = original;
            renderer.bones = new[] { bone.transform };
            renderer.rootBone = bone.transform;
            renderer.sharedMaterials = new[] { new Material(lilToon) { name = "E2EMaterial" } };

            // The avatar's own parameters, so "scattered among them" is a claim that can fail.
            var existing = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            existing.parameters = Enumerable.Range(0, 12).Select(i =>
                new VRCExpressionParameters.Parameter
                {
                    name = "OwnToggle" + i,
                    valueType = VRCExpressionParameters.ValueType.Bool,
                    saved = true, networkSynced = true
                }).ToArray();
            if (!AssetDatabase.IsValidFolder("Assets/_MPTestScratch"))
                AssetDatabase.CreateFolder("Assets", "_MPTestScratch");
            AssetDatabase.CreateAsset(existing, "Assets/_MPTestScratch/OwnParams.asset");
            descriptor.customExpressions = true;
            descriptor.expressionParameters = existing;

            // Two controllers whose names give the game away, so "the bundle no longer says what
            // the avatar does" is a claim with something to bite on.
            //
            // FX and Gesture are both needed, and for different reasons. The tool copies the FX
            // controller itself, before any renaming happens, so an FX-only fixture would pass the
            // "project untouched" check no matter what the renamer does - the project asset was
            // never the thing being renamed. Gesture is a layer the tool otherwise leaves alone, so
            // it is the one that proves the renamer copies before it writes.
            var fx = ReadableController("OwnFX", "DanceToggle", "DanceMachine", "Kiss_On",
                                        "Outfit_Swimsuit");
            var gesture = ReadableController("OwnGesture", "HandSigns", "SignMachine", "Wave_On",
                                             "Gesture_Wave");

            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[]
            {
                new VRCAvatarDescriptor.CustomAnimLayer
                {
                    type = VRCAvatarDescriptor.AnimLayerType.Gesture,
                    isDefault = false,
                    animatorController = gesture
                },
                new VRCAvatarDescriptor.CustomAnimLayer
                {
                    type = VRCAvatarDescriptor.AnimLayerType.FX,
                    isDefault = false,
                    animatorController = fx
                }
            };

            settings = root.AddComponent<MeshProtectRoot>();
            settings.outputFolder = "Assets/_MPTestScratch";
            settings.mode = MeshProtectRoot.DisplacementMode.TangentSpace;
            settings.distortRatio = 0.04f;

            var rng = new System.Random(777);
            settings.keyDigits = MeshProtectCipher.GeneratePassword(rng);
            settings.variant = MeshProtectVariantGenerator.Generate(rng);

            if (MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out _))
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            return root;
        }

        /// <summary>
        /// A mesh used by BOTH a lilToon renderer and a renderer the tool will not touch.
        ///
        /// The lilToon renderer gets a displaced copy; the other one keeps the original, and that
        /// original is still reachable from the avatar, so it is serialised into the bundle exactly
        /// as the artist authored it. Anyone who opens the bundle has a perfect copy of a mesh the
        /// avatar claims to be protecting, and no password is involved.
        ///
        /// The property asserted here is the outcome, not the mechanism: either nothing that ships
        /// is a clear copy of a protected mesh, or the build was refused. Both are acceptable; a
        /// build that succeeds while leaking is not.
        /// </summary>
        /// <summary>
        /// A mesh that was protected, still reachable through some OTHER reference on the avatar.
        ///
        /// Protection replaces the mesh on the renderers the tool handles. Anything else pointing
        /// at that same Mesh object keeps the undisplaced original, and everything reachable from
        /// the avatar is serialised into the bundle - so one stray reference hands over a perfect
        /// copy with no password involved, while every other check still reports success.
        ///
        /// Each carrier below is a different way to hold that reference, and each gets its own
        /// avatar carrying ONLY that one. A guard written as a list of component types passes the
        /// first few and fails the rest, which is the point: the last carrier is a MonoBehaviour
        /// this tool has never heard of, and no list can ever include it.
        ///
        /// The asserted property is the outcome, not the mechanism: either nothing that ships is a
        /// clear copy, or the build was refused. A build that succeeds while leaking is not.
        /// </summary>
        private static void TestSharedMeshLeak(Shader lilToon)
        {
            var standard = Shader.Find("Standard");
            if (standard == null)
            {
                Say("      (skipped leak/*: no Standard shader in this project)");
                return;
            }

            var carriers = new (string name, Action<GameObject, Mesh> attach, Func<GameObject, Mesh> read)[]
            {
                ("renderer",
                 (go, mesh) =>
                 {
                     var r = go.AddComponent<SkinnedMeshRenderer>();
                     r.sharedMesh = mesh;
                     r.sharedMaterials = new[] { new Material(standard) { name = "PlainMat" } };
                 },
                 go => go.GetComponent<SkinnedMeshRenderer>()?.sharedMesh),

                ("mesh-filter",
                 (go, mesh) => go.AddComponent<MeshFilter>().sharedMesh = mesh,
                 go => go.GetComponent<MeshFilter>()?.sharedMesh),

                ("mesh-collider",
                 (go, mesh) => go.AddComponent<MeshCollider>().sharedMesh = mesh,
                 go => go.GetComponent<MeshCollider>()?.sharedMesh),

                ("particle-shape",
                 (go, mesh) =>
                 {
                     var ps = go.AddComponent<ParticleSystem>();
                     var shape = ps.shape;
                     shape.enabled = true;
                     shape.shapeType = ParticleSystemShapeType.Mesh;
                     shape.mesh = mesh;
                 },
                 go =>
                 {
                     var ps = go.GetComponent<ParticleSystem>();
                     return ps == null ? null : ps.shape.mesh;
                 }),

                ("unknown-component",
                 (go, mesh) => go.AddComponent<MeshHolderProbe>().held = mesh,
                 go => go.GetComponent<MeshHolderProbe>()?.held),
            };

            // One variant for all of them: each carrier needs its own avatar, but generating a
            // shader family per carrier would cost seconds apiece for nothing.
            var rng = new System.Random(4242);
            var keyDigits = MeshProtectCipher.GeneratePassword(rng);
            var sharedVariant = MeshProtectVariantGenerator.Generate(rng);

            foreach (var carrier in carriers)
                RunLeakCarrier(lilToon, carrier.name, carrier.attach, carrier.read,
                               keyDigits, sharedVariant);
        }

        private static void RunLeakCarrier(Shader lilToon, string label,
                                           Action<GameObject, Mesh> attach,
                                           Func<GameObject, Mesh> read,
                                           int[] keyDigits, MeshProtectVariant variant)
        {
            GameObject root = null;
            Mesh shared = null;
            try
            {
                root = new GameObject("LeakAvatar_" + label);
                root.AddComponent<Animator>();
                root.AddComponent<VRCAvatarDescriptor>();

                var bone = new GameObject("Bone");
                bone.transform.SetParent(root.transform, false);

                shared = BuildTestMesh(16);
                var weights = new BoneWeight[shared.vertexCount];
                for (int i = 0; i < weights.Length; i++)
                    weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                shared.boneWeights = weights;
                shared.bindposes = new[] { Matrix4x4.identity };
                var originalVertices = shared.vertices;

                var protectedGo = new GameObject("Protected");
                protectedGo.transform.SetParent(root.transform, false);
                var smr = protectedGo.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = shared;
                smr.bones = new[] { bone.transform };
                smr.rootBone = bone.transform;
                smr.sharedMaterials = new[] { new Material(lilToon) { name = "ProtectedMat" } };

                var carrierGo = new GameObject("Carrier");
                carrierGo.transform.SetParent(root.transform, false);
                attach(carrierGo, shared);          // the same Mesh object, deliberately

                // A second protected renderer, on a mesh of its own that nothing else points at.
                // Without it this avatar has exactly one protectable renderer, so dropping the
                // leaking one leaves nothing to protect and the build refuses for that reason
                // instead - which reads identically from out here and would hide whether the drop
                // works at all.
                var otherGo = new GameObject("AlsoProtected");
                otherGo.transform.SetParent(root.transform, false);
                var otherSmr = otherGo.AddComponent<SkinnedMeshRenderer>();
                otherSmr.sharedMesh = BuildTestMesh(16);
                var otherWeights = new BoneWeight[otherSmr.sharedMesh.vertexCount];
                for (int i = 0; i < otherWeights.Length; i++)
                    otherWeights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                otherSmr.sharedMesh.boneWeights = otherWeights;
                otherSmr.sharedMesh.bindposes = new[] { Matrix4x4.identity };
                otherSmr.bones = new[] { bone.transform };
                otherSmr.rootBone = bone.transform;
                otherSmr.sharedMaterials = new[] { new Material(lilToon) { name = "OtherMat" } };

                var settings = root.AddComponent<MeshProtectRoot>();
                settings.outputFolder = "Assets/_MPTestScratch";
                settings.mode = MeshProtectRoot.DisplacementMode.TangentSpace;
                settings.distortRatio = 0.04f;
                settings.keyDigits = keyDigits;
                settings.variant = variant;
                if (MeshProtectShaderGen.EnsureGenerated(settings, variant, out _))
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                bool ok;
                try
                {
                    ok = new MeshProtectBuildHook().OnPreprocessAvatar(root);
                }
                catch (Exception e)
                {
                    ok = false;
                    Say($"      leak/{label}: hook threw - " + e.Message);
                }

                var left = read(carrierGo);
                bool clear = left != null && left.vertexCount == originalVertices.Length;
                if (clear)
                {
                    var v = left.vertices;
                    for (int i = 0; clear && i < v.Length; i++)
                        clear = (v[i] - originalVertices[i]).sqrMagnitude < 1e-16f;
                }

                // The property, restated for a build that drops the mesh instead of refusing.
                //
                // What must never happen is a mesh that LOOKS protected while a clear copy ships:
                // the renderer wearing the decode shader, and the original readable next to it. A
                // mesh left alone everywhere is a different thing - it is unprotected, like a
                // Poiyomi one, and the report says so. So the question is no longer "did anything
                // clear ship" but "does anything still claim this mesh is protected".
                bool claimsProtected = false;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    // Not `GetComponent<MeshFilter>()?.sharedMesh`: ?. is a C# null check and
                    // skips the == UnityEngine.Object overloads, so a missing component walks
                    // straight into sharedMesh and throws.
                    Mesh m = null;
                    if (r is SkinnedMeshRenderer sk) m = sk.sharedMesh;
                    else
                    {
                        var f = r.GetComponent<MeshFilter>();
                        if (f != null) m = f.sharedMesh;
                    }
                    if (m == null || m != shared) continue;
                    if (r.sharedMaterials.Any(mat => mat != null && mat.shader != null &&
                                                     mat.shader.name.StartsWith(variant.shaderName,
                                                         StringComparison.Ordinal)))
                        claimsProtected = true;
                }

                Check(!ok || !clear || !claimsProtected, "leak/" + label,
                      !ok ? "build refused, so nothing ships"
                          : !clear ? "the carrier no longer holds the source mesh"
                                   : !claimsProtected
                                       ? "the mesh was left unprotected everywhere, so the copy " +
                                         "that ships is not pretending to be protected"
                                       : "build SUCCEEDED with a renderer wearing the decode " +
                                         "shader while a clear copy of its mesh also ships");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (shared != null) UnityEngine.Object.DestroyImmediate(shared);
                foreach (var folder in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }

        /// <summary>
        /// An unticked component must produce a plain upload, and a ticked one must not.
        ///
        /// Unticking a component means "do nothing" everywhere else in Unity, and here it used to
        /// mean nothing at all - the only way to get one unprotected build, to test whether this
        /// tool was behind some behaviour, was to delete the component and lose the variant and
        /// password with it.
        ///
        /// Both halves are asserted. A checkbox that skips protection is a way to ship an
        /// unprotected avatar by accident, so "ticked still protects" is the more important of the
        /// two and is checked first.
        /// </summary>
        private static void TestDisabledComponentSkips(Shader lilToon)
        {
            foreach (bool enabled in new[] { true, false })
            {
                GameObject root = null;
                try
                {
                    root = new GameObject("EnabledAvatar_" + enabled);
                    root.AddComponent<Animator>();
                    root.AddComponent<VRCAvatarDescriptor>();

                    var bone = new GameObject("Bone");
                    bone.transform.SetParent(root.transform, false);

                    var mesh = BuildTestMesh(16);
                    var weights = new BoneWeight[mesh.vertexCount];
                    for (int i = 0; i < weights.Length; i++)
                        weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                    mesh.boneWeights = weights;
                    mesh.bindposes = new[] { Matrix4x4.identity };
                    var originalVertices = mesh.vertices;

                    var body = new GameObject("Body");
                    body.transform.SetParent(root.transform, false);
                    var smr = body.AddComponent<SkinnedMeshRenderer>();
                    smr.sharedMesh = mesh;
                    smr.bones = new[] { bone.transform };
                    smr.rootBone = bone.transform;
                    smr.sharedMaterials = new[] { new Material(lilToon) { name = "EnabledMat" } };

                    var settings = root.AddComponent<MeshProtectRoot>();
                    settings.outputFolder = "Assets/_MPTestScratch";
                    settings.mode = MeshProtectRoot.DisplacementMode.TangentSpace;
                    settings.distortRatio = 0.04f;
                    var rng = new System.Random(enabled ? 777001 : 777002);
                    settings.keyDigits = MeshProtectCipher.GeneratePassword(rng);
                    settings.variant = MeshProtectVariantGenerator.Generate(rng);
                    if (MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out _))
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                    settings.enabled = enabled;

                    bool ok = new MeshProtectBuildHook().OnPreprocessAvatar(root);

                    var shipped = smr.sharedMesh;
                    bool untouched = shipped == null || shipped.vertexCount != originalVertices.Length;
                    if (!untouched && shipped != null)
                    {
                        var v = shipped.vertices;
                        untouched = true;
                        for (int i = 0; untouched && i < v.Length; i++)
                            untouched = (v[i] - originalVertices[i]).sqrMagnitude < 1e-16f;
                    }

                    bool stripped = root.GetComponentsInChildren<MeshProtectRoot>(true).Length == 0;

                    if (enabled)
                        Check(ok && !untouched && stripped, "enabled/ticked-protects",
                              !ok ? "the build was blocked"
                                  : untouched ? "the mesh was NOT displaced despite being ticked"
                                  : !stripped ? "the authoring component survived the build"
                                  : "the mesh is displaced and the component is gone");
                    else
                        // Stripped in BOTH cases. The first version of this test asserted the
                        // opposite - that an unticked component is left alone - and passed, while
                        // the upload it described was rejected by VRChat for carrying a component
                        // type it does not recognise. Authoring data never belongs in a bundle.
                        Check(ok && untouched && stripped, "enabled/unticked-skips",
                              !ok ? "the build was blocked by a component that is switched off"
                                  : !untouched ? "the mesh was displaced even though the component " +
                                                 "is unticked"
                                  : !stripped ? "the component survived into the upload, which " +
                                                "VRChat rejects"
                                  : "unticked: mesh untouched and the component still stripped");
                }
                finally
                {
                    if (root != null) UnityEngine.Object.DestroyImmediate(root);
                    if (AssetDatabase.IsValidFolder("Assets/_MeshProtectBuild"))
                        AssetDatabase.DeleteAsset("Assets/_MeshProtectBuild");
                }
            }
        }

        /// <summary>
        /// lilToon's ID Mask can be told to read its index from UV6, and UV6.x is exactly where the
        /// bake writes the per-vertex amplitude.
        ///
        /// When both are in play the mask reads an amplitude - a number like 0.04 - instead of the
        /// index the author assigned, so every ID-masked region collapses to index zero. The avatar
        /// uploads, the protection works, and the model just looks wrong, with nothing anywhere
        /// saying why. The README listed the conflict from the start; the code never checked for it.
        ///
        /// There is no repair: the channel is single-occupancy. The property asserted is that a
        /// build which succeeds does not ship the collision.
        /// </summary>
        /// <summary>
        /// A Target Renderer that is not part of the avatar must be left completely alone.
        ///
        /// The SDK builds from an Instantiate, and Unity only remaps references inside the graph it
        /// copied - so an entry pointing outside still points at the object in the open scene. Baking
        /// it assigns a mesh and materials to the author's own scene, and the build folder is deleted
        /// afterwards on every path, leaving that renderer bound to an asset that no longer exists.
        /// Nothing else in this suite would see it: the avatar itself comes out perfectly protected.
        /// </summary>
        private static void TestRendererOutsideTheAvatar(Shader lilToon)
        {
            GameObject root = null, stranger = null;
            try
            {
                root = BuildMinimalAvatar(lilToon, "OutsideAvatar", 4242, out var inside, out var settings);

                // Deliberately not parented to the avatar: this stands in for something sitting in
                // the author's scene that got dragged into the list by accident.
                stranger = new GameObject("SceneObjectNotInTheAvatar");
                var strangerRenderer = stranger.AddComponent<MeshFilter>();
                strangerRenderer.sharedMesh = BuildTestMesh(16);
                var strangerMr = stranger.AddComponent<MeshRenderer>();
                var strangerMat = new Material(lilToon) { name = "SceneMat" };
                strangerMr.sharedMaterials = new[] { strangerMat };

                var meshBefore = strangerRenderer.sharedMesh;
                var matBefore = strangerMr.sharedMaterial;

                settings.targetRenderers = new List<Renderer> { inside, strangerMr, inside };

                bool ok;
                try { ok = new MeshProtectBuildHook().OnPreprocessAvatar(root); }
                catch (Exception e) { ok = false; Say("      outside fixture: hook threw - " + e.Message); }

                bool untouched = strangerRenderer.sharedMesh == meshBefore &&
                                 strangerMr.sharedMaterial == matBefore;
                Check(untouched, "scene/renderer-outside-the-avatar-untouched",
                      untouched
                          ? "a Target Renderer outside the avatar kept its own mesh and material"
                          : "the build wrote into an object that is not part of the avatar - that " +
                            "is the author's scene, and the asset it now points at is deleted when " +
                            "the build folder goes");

                // Listing the same renderer twice used to bake it twice; the second pass reads the
                // materials the first one converted and reports the author's own avatar as already
                // protected.
                Check(ok, "scene/duplicate-target-renderer-is-harmless",
                      ok ? "the same renderer listed twice still builds"
                         : "listing a renderer twice refused the build");
            }
            finally
            {
                if (stranger != null) UnityEngine.Object.DestroyImmediate(stranger);
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
            }
        }

        /// <summary>
        /// UV6 is written for every vertex, so a mesh that already has something in it loses that
        /// something - on every sub-mesh, including any the bake was told to skip.
        ///
        /// Both halves matter. Refusing an occupied channel is the guard; NOT refusing a channel
        /// that exists but is all zero is what keeps it from turning away perfectly good avatars,
        /// which some importers produce for free.
        /// </summary>
        private static void TestOccupiedUv6(Shader lilToon, string label, bool occupied)
        {
            GameObject root = null;
            try
            {
                root = BuildMinimalAvatar(lilToon, "Uv6Avatar_" + label, occupied ? 4243 : 4244,
                                          out var renderer, out _);

                var mesh = ((SkinnedMeshRenderer)renderer).sharedMesh;
                var uv6 = new List<Vector2>();
                for (int i = 0; i < mesh.vertexCount; i++)
                    uv6.Add(occupied && i == 3 ? new Vector2(7f, 0f) : Vector2.zero);
                mesh.SetUVs(6, uv6);

                bool ok;
                try { ok = new MeshProtectBuildHook().OnPreprocessAvatar(root); }
                catch (Exception e) { ok = false; Say("      uv6 fixture: hook threw - " + e.Message); }

                if (occupied)
                {
                    // The property, not the policy. What must never happen is the author's UV6
                    // being overwritten; whether the build refuses or leaves the renderer alone is
                    // this tool's choice to make, and it now leaves it alone. Reading the channel
                    // back is what says which of those actually happened.
                    var afterMesh = ((SkinnedMeshRenderer)renderer).sharedMesh;
                    var after = new List<Vector4>();
                    if (afterMesh != null) afterMesh.GetUVs(6, after);
                    bool kept = after.Count > 3 && Mathf.Approximately(after[3].x, 7f);

                    Check(!ok || kept, "uv6/" + label,
                          !ok ? "build refused, so nothing was overwritten"
                              : kept ? "the mesh was left exactly as it was, UV6 and all"
                                     : "build SUCCEEDED and the author's UV6 was overwritten - it " +
                                       "ships looking wrong with nothing saying why");
                }
                else
                    Check(ok, "uv6/" + label,
                          ok ? "an all-zero UV6 channel is not mistaken for occupied"
                             : "build REFUSED a mesh whose UV6 exists but carries nothing");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
            }
        }

        /// <summary>The smallest avatar the build hook will accept, for scenarios that only need one.</summary>
        /// <summary>
        /// An avatar this tool cannot protect must be STOPPED, and its mesh left exactly as it is.
        ///
        /// Two rules, and they were learnt in that order.
        ///
        /// The mesh half is the older one. Every "cannot protect X" used to be a refusal thrown
        /// from the middle of the bake, so the vertices were already displaced when it fired; a
        /// build that gets past that point and uploads anyway ships a scrambled avatar with nothing
        /// to decode it. "It uploaded" on its own would be true of exactly that build, so the
        /// vertices are compared, not just the exit code.
        ///
        /// The stop half replaced the opposite rule. Carrying on was meant to keep an author whose
        /// avatar cannot be protected from being trapped - but it made "protected" and "not
        /// protected" look identical from outside: same button, same success, same upload. The way
        /// anybody found out was in game, after the build and the upload, when the unlock menu was
        /// not there. Stopping costs the people who genuinely want a plain upload one dialog
        /// button; carrying on cost everybody else an avatar they thought was protected.
        ///
        /// Note that a throw is NOT a pass here. The expected result is a refusal, and an exception
        /// out of the hook produces the same false - so the two are told apart on purpose.
        /// </summary>
        private static void TestNothingToProtectStillUploads(Shader lilToon)
        {
            var standard = Shader.Find("Standard");
            if (standard == null)
            {
                Say("      (skipped plain/*: no Standard shader in this project)");
                return;
            }

            GameObject root = null;
            try
            {
                root = BuildMinimalAvatar(lilToon, "PlainAvatar", 4711, out var renderer, out _);

                // Nothing lilToon anywhere: the shape of a Poiyomi avatar, which is most of them.
                renderer.sharedMaterials = new[] { new Material(standard) { name = "PoiyomiIsh" } };

                var mesh = ((SkinnedMeshRenderer)renderer).sharedMesh;
                var before = mesh.vertices;

                bool ok, threw = false;
                try { ok = new MeshProtectBuildHook().OnPreprocessAvatar(root); }
                catch (Exception e)
                {
                    ok = false;
                    threw = true;
                    Say("      plain fixture: hook threw - " + e.Message);
                }

                var afterMesh = ((SkinnedMeshRenderer)renderer).sharedMesh;
                var after = afterMesh == null ? new Vector3[0] : afterMesh.vertices;
                bool untouched = after.Length == before.Length;
                for (int i = 0; untouched && i < after.Length; i++)
                    untouched = (after[i] - before[i]).sqrMagnitude < 1e-16f;

                Check(!ok && !threw && untouched, "plain/nothing-to-protect-stops-the-upload",
                      threw ? "the hook THREW instead of refusing - the refusal has to be the " +
                              "reported one, or the dialog that explains it never appears"
                          : ok ? "the build carried on and shipped an UNPROTECTED avatar with " +
                                 "nothing at upload time to say so"
                          : untouched
                              ? "stopped before the build, with every vertex exactly where it was"
                              : "stopped, but the mesh had already been displaced - a build that " +
                                "got any further would ship it scrambled");

                // The way out, and the only one that survives being pressed twice: untick the
                // component. It has to upload, and it has to leave nothing behind - VRChat refuses
                // an avatar carrying a component type it does not know, so a build that skips
                // protection but keeps the component turns "unprotected" into "cannot upload".
                var left = root.GetComponentInChildren<MeshProtectRoot>(true);
                if (left != null) left.enabled = false;

                bool plainOk;
                try { plainOk = new MeshProtectBuildHook().OnPreprocessAvatar(root); }
                catch (Exception e) { plainOk = false; Say("      untick fixture: threw - " + e.Message); }

                bool stripped = root.GetComponentInChildren<MeshProtectRoot>(true) == null;
                Check(plainOk && stripped, "plain/unticked-uploads-and-strips",
                      !plainOk
                          ? "unticking the component did NOT let the avatar upload - there is now " +
                            "no way at all to upload one this tool cannot protect"
                          : stripped
                              ? "unticked: uploaded, and the authoring component was taken out"
                              : "the component shipped, and VRChat refuses an avatar carrying a " +
                                "type it does not know");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
            }
        }

        /// <summary>
        /// A renderer with MORE materials than sub-meshes must not ship displaced geometry that
        /// something else draws undecoded.
        ///
        /// Unity draws every material past the end of the sub-mesh list over the LAST sub-mesh
        /// again. Avatars use that constantly - a second outline pass, a black body pass - and it
        /// is the shape no fixture here had, because every other fixture gives a renderer exactly
        /// as many materials as its mesh has sub-meshes.
        ///
        /// What it cost: the skip mask was built by walking SUB-MESHES, so slots past the end were
        /// never consulted. A one-sub-mesh mesh whose only material converted was therefore
        /// displaced in full, while the extra material - on somebody else's shader, with no decode
        /// in it - kept drawing that same displaced geometry. The avatar came back an exploded
        /// cloud, and it stayed exploded when the right password was entered, because only one of
        /// the two passes was ever listening to the password. That report is what this test is.
        ///
        /// The rule: the last sub-mesh may only be displaced when every slot drawing over it is
        /// ours. Here the second material is not, so nothing may move.
        /// </summary>
        /// <summary>
        /// An extra slot the author VOUCHED FOR must not cost the mesh its displacement.
        ///
        /// The invisible-materials list exists because the tool cannot tell an anti-clip shell
        /// from a gem - both are foreign shaders, one draws nothing and one very much does - and
        /// the conservative answer (leave the mesh alone) costs exactly the mesh people most want
        /// scrambled, the body. The author is the only party who knows which kind theirs is, this
        /// list is where they say so, and a wrong entry announces itself as visible noise on the
        /// first locked test rather than failing silently - which is why the switch is safe to
        /// offer at all.
        /// </summary>
        private static void TestVouchedInvisibleExtraSlot(Shader lilToon)
        {
            var standard = Shader.Find("Standard");
            if (standard == null)
            {
                Say("      (skipped extra-slot/vouched: no Standard shader in this project)");
                return;
            }

            GameObject root = null;
            try
            {
                root = BuildMinimalAvatar(lilToon, "VouchedAvatar", 4716, out var renderer,
                                          out var settings);

                var shell = new Material(standard) { name = "InvisibleShell" };
                var materials = renderer.sharedMaterials.ToList();
                materials.Add(shell);
                renderer.sharedMaterials = materials.ToArray();

                settings.invisibleMaterials.Add(shell);

                var mesh = ((SkinnedMeshRenderer)renderer).sharedMesh;
                var before = mesh.vertices;

                bool ok;
                string thrown = null;
                try { ok = new MeshProtectBuildHook().OnPreprocessAvatar(root); }
                catch (Exception e) { ok = false; thrown = e.Message; }

                var afterMesh = ((SkinnedMeshRenderer)renderer).sharedMesh;
                var after = afterMesh == null ? new Vector3[0] : afterMesh.vertices;
                bool moved = after.Length == before.Length && after.Length > 0;
                if (moved)
                {
                    bool any = false;
                    for (int i = 0; !any && i < after.Length; i++)
                        any = (after[i] - before[i]).sqrMagnitude > 1e-12f;
                    moved = any;
                }

                Check(ok && moved, "extra-slot/vouched-invisible-displaces",
                      thrown != null
                          ? "the build threw: " + thrown
                          : !ok
                              ? "the build did not go through"
                          : moved
                              ? "the vouched extra slot no longer blocks displacement - the mesh " +
                                "ships scrambled with the shell drawing nothing over it"
                              : "the mesh still ships undisplaced despite the vouch - the " +
                                "invisible-materials list is not reaching the skip mask");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
            }
        }

        /// <summary>
        /// Dropped prepared copies must degrade to FX-only renaming, not to none.
        ///
        /// The all-or-nothing rule threw away BOTH maps whenever one stored name went stale, and
        /// on a VRCFury avatar one always does: VRCFury replaces the playable-layer controllers
        /// during the build, so the stored maps - made against the avatar before that - never
        /// survive contact. The observable result was a wearer reporting that hierarchy and
        /// controller obfuscation had simply disappeared between versions, when what disappeared
        /// was the fallback nobody had written.
        ///
        /// The shape here is the real one: one parameter safe (nothing but the expression list
        /// uses it), one blocked (a Gesture controller this build does not rewrite declares it),
        /// and a stored map whose one entry renames the blocked one. The stored map must die, the
        /// safe parameter must still be renamed, and the blocked one must keep its name - all
        /// three, because "renamed everything" and "renamed nothing" are both one-line bugs away.
        /// </summary>
        private static void TestObfuscationFallback(Shader lilToon)
        {
            GameObject root = null;
            const string dir = "Assets/_MPTestScratch";
            string gesturePath = dir + "/FallbackGesture.controller";
            try
            {
                root = BuildMinimalAvatar(lilToon, "FallbackAvatar", 4717, out _, out var settings);

                if (!AssetDatabase.IsValidFolder(dir))
                    AssetDatabase.CreateFolder("Assets", "_MPTestScratch");

                var descriptor = root.GetComponent<VRCAvatarDescriptor>();

                var parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                parameters.parameters = new[]
                {
                    new VRCExpressionParameters.Parameter
                    {
                        name = "FallbackSafe", valueType = VRCExpressionParameters.ValueType.Int,
                        saved = true, networkSynced = true
                    },
                    new VRCExpressionParameters.Parameter
                    {
                        name = "FallbackBlocked", valueType = VRCExpressionParameters.ValueType.Int,
                        saved = true, networkSynced = true
                    }
                };
                AssetDatabase.CreateAsset(parameters, dir + "/FallbackParams.asset");
                descriptor.customExpressions = true;
                descriptor.expressionParameters = parameters;

                // The blocker: a playable layer this build does not rewrite declares the parameter.
                var gesture = AnimatorController.CreateAnimatorControllerAtPath(gesturePath);
                gesture.AddParameter("FallbackBlocked", AnimatorControllerParameterType.Int);
                descriptor.customizeAnimationLayers = true;
                descriptor.baseAnimationLayers = new[]
                {
                    new VRCAvatarDescriptor.CustomAnimLayer
                    {
                        type = VRCAvatarDescriptor.AnimLayerType.Gesture,
                        isDefault = false,
                        animatorController = gesture
                    }
                };

                // A stored map from "an hour ago" whose one entry no longer holds.
                settings.renamedParameters.Add(new MeshProtectRoot.RenamedParameter
                {
                    original = "FallbackBlocked",
                    obfuscated = "zzqqzzqq"
                });

                bool ok;
                string thrown = null;
                try { ok = new MeshProtectBuildHook().OnPreprocessAvatar(root); }
                catch (Exception e) { ok = false; thrown = e.Message; }

                var shipped = descriptor.expressionParameters;
                var names = shipped == null || shipped.parameters == null
                    ? new HashSet<string>()
                    : new HashSet<string>(shipped.parameters.Where(x => x != null).Select(x => x.name));

                bool blockedKept = names.Contains("FallbackBlocked");
                bool safeRenamed = !names.Contains("FallbackSafe");

                Check(ok && blockedKept && safeRenamed, "fallback/fx-only-renaming-survives",
                      thrown != null
                          ? "the build threw: " + thrown
                          : !ok
                              ? "the build did not go through"
                          : !blockedKept
                              ? "'FallbackBlocked' was renamed even though a Gesture controller " +
                                "this build does not rewrite still declares it - that controller " +
                                "ships driving a name that no longer exists"
                          : !safeRenamed
                              ? "'FallbackSafe' kept its name: the stored map died and took every " +
                                "rename with it, which is the all-or-nothing this fallback exists " +
                                "to end"
                              : "the stored map was dropped, the safe parameter is renamed, the " +
                                "blocked one keeps its name - renaming degrades instead of dying");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
                AssetDatabase.DeleteAsset(gesturePath);
                AssetDatabase.DeleteAsset(dir + "/FallbackParams.asset");
            }
        }

        private static void TestExtraMaterialSlot(Shader lilToon)
        {
            var standard = Shader.Find("Standard");
            if (standard == null)
            {
                Say("      (skipped extra-slot: no Standard shader in this project)");
                return;
            }

            GameObject root = null;
            try
            {
                root = BuildMinimalAvatar(lilToon, "ExtraSlotAvatar", 4715, out var renderer, out _);

                var mesh = ((SkinnedMeshRenderer)renderer).sharedMesh;
                int subMeshes = mesh.subMeshCount;

                // One more material than the mesh has sub-meshes. The extra one draws the last
                // sub-mesh a second time, and it carries no decode.
                var slots = new Material[subMeshes + 1];
                for (int i = 0; i < subMeshes; i++)
                    slots[i] = new Material(lilToon) { name = "ExtraSlotLil" + i };
                slots[subMeshes] = new Material(standard) { name = "SecondPass" };
                renderer.sharedMaterials = slots;

                var before = mesh.vertices;

                bool ok;
                string thrown = null;
                try { ok = new MeshProtectBuildHook().OnPreprocessAvatar(root); }
                catch (Exception e) { ok = false; thrown = e.Message; }

                var afterMesh = ((SkinnedMeshRenderer)renderer).sharedMesh;
                var after = afterMesh == null ? new Vector3[0] : afterMesh.vertices;

                // Every vertex the second pass can draw must be exactly where it started.
                bool moved = after.Length != before.Length;
                for (int i = 0; !moved && i < after.Length; i++)
                    moved = (after[i] - before[i]).sqrMagnitude > 1e-12f;

                Check(!moved && thrown == null, "extra-slot/last-submesh-not-displaced",
                      thrown != null
                          ? "the build threw: " + thrown
                          : moved
                              ? "the mesh was displaced even though an extra material slot draws " +
                                "the last sub-mesh again with no decode on it - that geometry " +
                                "ships as noise and stays noise with the right password entered"
                              : ok
                                  ? "nothing was displaced under the undecoded second pass"
                                  : "nothing was displaced (and the build stopped, which is at " +
                                    "least not a broken avatar)");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
            }
        }

        /// <summary>
        /// A material-swap animation must end up pointing at the DECODE copy of its material.
        ///
        /// This is the failure a real avatar shipped with, and it is invisible to every other
        /// check in this file. A skin-toggle avatar keeps its alternate skins as animations: a
        /// clip binds m_Materials.Array.data[0] on the body and references the material ASSET.
        /// The build converted only what sat in renderer slots, so in game the animator wrote the
        /// ORIGINAL material back onto the displaced mesh - permanent noise, unmoved by the right
        /// password, on an upload whose report said everything was protected. It reproduced on no
        /// test machine because nothing in a test scene ever plays the FX controller; what a test
        /// CAN do is read the shipped keyframes, which is exactly what this one does.
        ///
        /// The swap material deliberately sits on NO renderer - only the clip knows it - because
        /// that is the real shape: the alternate skin is not the one you spawn wearing.
        ///
        /// The author's own clip asset must also come out untouched. The rewrite works on this
        /// build's clones; editing the project's clip would be the exact kind of destructive
        /// change this whole tool promises never to make.
        /// </summary>
        private static void TestMaterialSwapAnimation(Shader lilToon)
        {
            GameObject root = null;
            const string dir = "Assets/_MPTestScratch";
            string fxPath = dir + "/SwapFx.controller";
            string clipPath = dir + "/SwapClip.anim";
            string matPath = dir + "/SwapSkin.mat";
            try
            {
                root = BuildMinimalAvatar(lilToon, "SwapAvatar", 4715, out var renderer,
                                          out var settings);
                var variant = settings.variant;

                if (!AssetDatabase.IsValidFolder(dir))
                    AssetDatabase.CreateFolder("Assets", "_MPTestScratch");

                // The alternate skin: stock lilToon, referenced only by the clip.
                var swapSkin = new Material(lilToon) { name = "SwapSkin" };
                AssetDatabase.CreateAsset(swapSkin, matPath);

                var clip = new AnimationClip { name = "SwapClip" };
                AnimationUtility.SetObjectReferenceCurve(clip,
                    EditorCurveBinding.PPtrCurve("Body", typeof(SkinnedMeshRenderer),
                                                 "m_Materials.Array.data[0]"),
                    new[] { new ObjectReferenceKeyframe { time = 0f, value = swapSkin } });
                AssetDatabase.CreateAsset(clip, clipPath);

                var fx = AnimatorController.CreateAnimatorControllerAtPath(fxPath);
                fx.AddMotion(clip);
                AssetDatabase.SaveAssets();

                var descriptor = root.GetComponent<VRCAvatarDescriptor>();
                descriptor.customizeAnimationLayers = true;
                descriptor.baseAnimationLayers = new[]
                {
                    new VRCAvatarDescriptor.CustomAnimLayer
                    {
                        type = VRCAvatarDescriptor.AnimLayerType.FX,
                        isDefault = false,
                        animatorController = fx
                    }
                };

                bool ok;
                string thrown = null;
                try { ok = new MeshProtectBuildHook().OnPreprocessAvatar(root); }
                catch (Exception e) { ok = false; thrown = e.Message; }

                // Walk the SHIPPED FX for material keyframes. Located by binding, not by path or
                // clip name - both may have been renamed by the build, and neither matters here.
                var shippedRuntime = descriptor.baseAnimationLayers
                    .Where(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX)
                    .Select(l => l.animatorController).FirstOrDefault();
                while (shippedRuntime is AnimatorOverrideController o)
                    shippedRuntime = o.runtimeAnimatorController;
                var shipped = shippedRuntime as AnimatorController;

                Material landed = null;
                int swapCurves = 0;
                if (shipped != null)
                {
                    foreach (var shippedClip in shipped.animationClips.Where(c => c != null).Distinct())
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(shippedClip))
                    {
                        if (!binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal))
                            continue;
                        foreach (var key in AnimationUtility.GetObjectReferenceCurve(shippedClip, binding))
                        {
                            var material = key.value as Material;
                            if (material == null) continue;
                            swapCurves++;
                            landed = material;
                        }
                    }
                }

                bool decoded = landed != null &&
                               MeshProtectPipeline.IsProtectShader(landed, variant.shaderName);

                // The author's clip, reloaded from disk, must still reference the original.
                var authorsClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                Material authorsValue = null;
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(authorsClip))
                    foreach (var key in AnimationUtility.GetObjectReferenceCurve(authorsClip, binding))
                        authorsValue = key.value as Material;
                bool untouched = authorsValue == swapSkin;

                Check(ok && swapCurves > 0 && decoded && landed != swapSkin && untouched,
                      "swap/animated-material-carries-the-decode",
                      thrown != null
                          ? "the build threw: " + thrown
                          : !ok
                              ? "the build was refused over a material swap animation"
                          : swapCurves == 0
                              ? "the shipped FX has no material swap curve at all - the toggle " +
                                "was dropped rather than protected"
                          : !decoded || landed == swapSkin
                              ? $"the shipped keyframe still references '{(landed == null ? "<null>" : landed.name)}' " +
                                $"on '{(landed == null || landed.shader == null ? "?" : landed.shader.name)}' - in game the " +
                                "animator swaps the original material onto the displaced mesh and " +
                                "that part renders as noise forever, which is the shipped bug " +
                                "this test exists to keep dead"
                          : !untouched
                              ? "the AUTHOR'S clip asset was edited in place - the rewrite must " +
                                "work on this build's clone, never on the project's file"
                              : "the swap now lands on the decode copy, and the author's clip is " +
                                "untouched");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
                foreach (var a in new[] { fxPath, clipPath, matPath })
                    AssetDatabase.DeleteAsset(a);
            }
        }

        /// <summary>
        /// An FX layer holding an AnimatorOverrideController must still be protected.
        ///
        /// Reachable, and not a corner somebody has to go looking for: the SDK draws that field as
        /// ObjectField(typeof(RuntimeAnimatorController)), so an override controller drops straight
        /// into it. An override has no layers of its own - it delegates to a base controller and
        /// substitutes clips - so the unlock layers cannot go into it, and this used to refuse.
        /// Refusing became much more expensive when a build that protects nothing started stopping
        /// the upload: the avatar went from "uploads unprotected" to "cannot be uploaded at all".
        ///
        /// The dangerous half is the override map. It is keyed by the BASE controller's clip
        /// objects, and the base is copied before the unlock layers go in - so if the base kept its
        /// clips as sub-assets, the copy holds different AnimationClip objects and every override
        /// stops matching by identity. The avatar would then animate from the base controller
        /// instead of the author's clips, and the build would report success.
        ///
        /// Both halves are asserted here, and this fixture deliberately uses a SEPARATE clip asset
        /// for the base - the case that should work. The sub-asset case is the one the build refuses
        /// rather than guesses at, and it refuses by measuring, not by inspecting how the clips were
        /// stored.
        /// </summary>
        private static void TestOverrideControllerFxLayer(Shader lilToon, string label,
                                                          bool clipInsideController)
        {
            GameObject root = null;
            const string dir = "Assets/_MPTestScratch";
            string basePath = dir + "/OvrBase_" + label + ".controller";
            string clipPath = dir + "/OvrReplacement_" + label + ".anim";
            string originalPath = dir + "/OvrOriginal_" + label + ".anim";
            string overPath = dir + "/OvrController_" + label + ".overrideController";
            try
            {
                root = BuildMinimalAvatar(lilToon, "OverrideFxAvatar_" + label, 4714,
                                          out var renderer, out var settings);
                var variant = settings.variant;

                if (!AssetDatabase.IsValidFolder(dir))
                    AssetDatabase.CreateFolder("Assets", "_MPTestScratch");

                // A base controller with one layer and one state. Where its motion LIVES is the
                // whole point of running this twice: a clip stored inside the controller's own file
                // is a different object in a byte copy of that file, so an override keyed by object
                // identity would stop matching. Whether it actually does is measured here rather
                // than guarded against.
                var baseController = AnimatorController.CreateAnimatorControllerAtPath(basePath);
                var original = new AnimationClip { name = "OvrOriginal" };
                if (clipInsideController)
                    AssetDatabase.AddObjectToAsset(original, baseController);
                else
                    AssetDatabase.CreateAsset(original, originalPath);
                baseController.AddMotion(original);

                // What the override swaps it for.
                var replacement = new AnimationClip { name = "OvrReplacement" };
                AssetDatabase.CreateAsset(replacement, clipPath);

                // CreateAsset renames the object to its file name, so these are read back AFTER
                // the assets exist rather than assumed - comparing against the literal is what made
                // this fixture fail on the product instead of on itself.
                string originalName = original.name;
                string replacementName = replacement.name;

                var over = new AnimatorOverrideController(baseController) { name = "OvrController" };
                over[original] = replacement;
                AssetDatabase.CreateAsset(over, overPath);
                AssetDatabase.SaveAssets();

                var descriptor = root.GetComponent<VRCAvatarDescriptor>();
                descriptor.customizeAnimationLayers = true;
                descriptor.baseAnimationLayers = new[]
                {
                    new VRCAvatarDescriptor.CustomAnimLayer
                    {
                        type = VRCAvatarDescriptor.AnimLayerType.FX,
                        isDefault = false,
                        animatorController = over
                    }
                };

                bool ok;
                string thrown = null;
                try { ok = new MeshProtectBuildHook().OnPreprocessAvatar(root); }
                catch (Exception e) { ok = false; thrown = e.Message; }

                // What the descriptor points at now, and what it resolves down to.
                var finalRuntime = descriptor.baseAnimationLayers
                    .Where(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX)
                    .Select(l => l.animatorController).FirstOrDefault();

                bool stillOverride = finalRuntime is AnimatorOverrideController;

                var resolved = finalRuntime;
                while (resolved is AnimatorOverrideController o) resolved = o.runtimeAnimatorController;
                var asController = resolved as AnimatorController;

                // The unlock layers went into the controller underneath.
                var ourNames = new HashSet<string>(variant.packLayerNames
                    .Concat(variant.decodeLayerNames));
                int ourLayers = asController == null
                    ? 0
                    : asController.layers.Count(l => ourNames.Contains(l.name));

                // And the author's override still points at the author's clip.
                bool overrideKept = false;
                if (finalRuntime is AnimatorOverrideController finalOver)
                {
                    var map = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                    finalOver.GetOverrides(map);
                    overrideKept = map.Any(m => m.Key != null && m.Key.name == originalName &&
                                                m.Value != null && m.Value.name == replacementName);
                }

                Check(ok && stillOverride && ourLayers > 0 && overrideKept,
                      "override/" + label,
                      thrown != null
                          ? "the build was stopped: " + thrown
                          : !ok
                              ? "the build was REFUSED over an override controller in the FX layer, " +
                                "so this avatar could not be uploaded at all"
                          : !stillOverride
                              ? "the FX layer came back as a plain controller - the override was " +
                                "dropped, and with it every clip the author had swapped"
                          : ourLayers == 0
                              ? "no unlock layer reached the controller under the override, so the " +
                                "avatar ships displaced with nothing to decode it"
                          : !overrideKept
                              ? $"the override of '{originalName}' did not survive the copy - the " +
                                "avatar would animate from the base controller instead"
                              : $"protected through the override: {ourLayers} unlock layer(s) in " +
                                "the base copy, and the author's clip swap still in place");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
                foreach (var a in new[] { basePath, clipPath, originalPath, overPath })
                    AssetDatabase.DeleteAsset(a);
            }
        }

        /// <summary>
        /// An avatar with NO ROOM for the unlock menu must still be protected.
        ///
        /// This is the rule that cost the most to arrive at, and it is the opposite of what this
        /// test asserted twice before.
        ///
        /// The first version refused the upload from the middle of the bake, with the mesh already
        /// displaced. The second skipped protection and let the upload through - which produced the
        /// worst outcome this tool has had, because it is invisible from every side: the author
        /// frees a few parameters, VRCFury is satisfied and says nothing, the SDK is satisfied, the
        /// upload succeeds, and the avatar has no protection on it. Nobody finds out until somebody
        /// rips it.
        ///
        /// So: over budget is not this tool's decision to act on. The bits go in. A compressor
        /// running afterwards sees them and fits itself around them; if even its tightest attempt
        /// is over, IT stops the upload, with real numbers. With no compressor the figure is final
        /// and nothing downstream catches it - the SDK's only budget test is OnGUIAvatarCheck in
        /// the control panel, on the avatar in the SCENE, before the build - and going ahead is
        /// still the author's chosen answer, because refusing costs the protection every time
        /// while going ahead costs it only if VRChat drops parameters over the cap.
        ///
        /// The transport bits are checked, not just the exit code. "It uploaded" is exactly what
        /// the silent-skip build did too; the thing that tells them apart is whether the twenty-four
        /// synced bools are actually in the parameter list.
        /// </summary>
        private static void TestFullParameterBudgetStillUploads(Shader lilToon)
        {
            GameObject root = null;
            VRCExpressionParameters parameters = null;
            try
            {
                root = BuildMinimalAvatar(lilToon, "FullBudgetAvatar", 4712, out var renderer,
                                          out var settings);

                // Read before the hook runs: Apply strips the component that holds it.
                var variant = settings.variant;

                // Spend nearly all of it. Every Int costs eight bits, and the transport needs
                // twenty-four, so this leaves too little on purpose.
                var descriptor = root.GetComponent<VRCAvatarDescriptor>();
                parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                int slots = (VRCExpressionParameters.MAX_PARAMETER_COST -
                             (MeshProtectRoot.PasswordLength * MeshProtectRoot.BitsPerDigit) + 8) / 8;
                parameters.parameters = Enumerable.Range(0, slots)
                    .Select(i => new VRCExpressionParameters.Parameter
                    {
                        name = "Filler" + i,
                        valueType = VRCExpressionParameters.ValueType.Int,
                        networkSynced = true
                    }).ToArray();
                descriptor.customExpressions = true;
                descriptor.expressionParameters = parameters;

                int costBefore = parameters.CalcTotalCost();

                var mesh = ((SkinnedMeshRenderer)renderer).sharedMesh;
                var before = mesh.vertices;

                bool ok;
                string thrown = null;
                try { ok = new MeshProtectBuildHook().OnPreprocessAvatar(root); }
                catch (Exception e) { ok = false; thrown = e.Message; }

                var afterMesh = ((SkinnedMeshRenderer)renderer).sharedMesh;
                var after = afterMesh == null ? new Vector3[0] : afterMesh.vertices;
                bool displaced = after.Length == before.Length && after.Length > 0;
                if (displaced)
                {
                    bool moved = false;
                    for (int i = 0; !moved && i < after.Length; i++)
                        moved = (after[i] - before[i]).sqrMagnitude > 1e-12f;
                    displaced = moved;
                }

                // The one that cannot be faked by a build that merely succeeded.
                var final = descriptor.expressionParameters;
                var names = final?.parameters == null
                    ? new HashSet<string>()
                    : new HashSet<string>(final.parameters.Where(x => x != null).Select(x => x.name));
                int carried = variant.bitNames.Count(n => names.Contains(n));

                Check(ok && displaced && carried == variant.bitNames.Length,
                      "plain/full-budget-still-protects",
                      thrown != null
                          ? "the build THREW over the parameter budget: " + thrown
                          : !ok
                              ? "the build was REFUSED over the parameter budget - that decision " +
                                "belongs to whatever compresses afterwards, not to this tool"
                          : !displaced
                              ? "the build succeeded but the mesh was never displaced - this is " +
                                "the silent skip, the one failure nobody downstream reports"
                          : carried != variant.bitNames.Length
                              ? $"only {carried} of {variant.bitNames.Length} transport bits " +
                                "reached the parameter list, so the avatar cannot be unlocked"
                              : $"protected over budget as intended: {carried} transport bits " +
                                $"added on top of {costBefore} already spent, and the decision " +
                                "left to whatever runs after this build");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (parameters != null) UnityEngine.Object.DestroyImmediate(parameters);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
            }
        }

        /// <summary>
        /// A component with no password must stop the upload, with the mesh exactly as it is.
        ///
        /// There is nothing to key the protection to, so nothing can be protected. Of every reason
        /// this build has for protecting nothing, this is the one most likely to be an accident and
        /// the cheapest to fix - one button, on the component already in front of them - so going
        /// ahead and shipping the avatar unprotected is the least useful thing to do about it.
        ///
        /// Inventing a password instead would be worse than either: the avatar would ship keyed to
        /// six digits its author has never seen and cannot unlock.
        /// </summary>
        private static void TestNoPasswordStillUploads(Shader lilToon)
        {
            GameObject root = null;
            try
            {
                root = BuildMinimalAvatar(lilToon, "NoPasswordAvatar", 4713, out var renderer,
                                          out var settings);
                settings.keyDigits = new int[0];

                var mesh = ((SkinnedMeshRenderer)renderer).sharedMesh;
                var before = mesh.vertices;

                bool ok, threw = false;
                try { ok = new MeshProtectBuildHook().OnPreprocessAvatar(root); }
                catch (Exception e)
                {
                    ok = false;
                    threw = true;
                    Say("      no-password fixture: hook threw - " + e.Message);
                }

                var afterMesh = ((SkinnedMeshRenderer)renderer).sharedMesh;
                var after = afterMesh == null ? new Vector3[0] : afterMesh.vertices;
                bool untouched = after.Length == before.Length;
                for (int i = 0; untouched && i < after.Length; i++)
                    untouched = (after[i] - before[i]).sqrMagnitude < 1e-16f;

                Check(!ok && !threw && untouched, "plain/no-password-stops-the-upload",
                      threw ? "the hook THREW instead of refusing, so what the author gets is a " +
                              "stack trace rather than \"press Generate Password\""
                          : ok ? "the build carried on and shipped unprotected over a button that " +
                                 "had not been pressed yet"
                          : untouched
                              ? "stopped, with every vertex exactly where it was"
                              : "stopped, but the mesh had already been displaced with no password " +
                                "to unlock it");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
            }
        }

        /// <summary>
        /// A mesh that already carries somebody's protection must be left completely alone.
        ///
        /// The dangerous half is not the double displacement - the already-protected sub-mesh goes
        /// into the skip mask, so its vertices are not moved twice. It is UV6. The amplitude is
        /// written across the WHOLE mesh, every sub-mesh including the skipped ones, and a mesh
        /// protected by anything that keeps its amplitude there - this tool on an earlier run,
        /// another avatar's family, another tool entirely - has its own data in that channel.
        /// Baking the rest of the mesh overwrites it and leaves that sub-mesh permanently
        /// undecodable: this tool destroying somebody's work, silently, on their own avatar.
        ///
        /// What holds the property up is the UV6 occupancy check in MeshProtectMesh.CanProtect,
        /// and naming that here matters: a whole-renderer veto on "material is on a protection
        /// family" was written first, this test was written to prove it worked, and it passed
        /// just as well after the veto was deleted. The veto was doing nothing. Anything that
        /// keeps data in UV6 is refused by the channel check; anything that does not keep data
        /// there has nothing to lose. A test that cannot tell those apart is not testing the thing
        /// its name says.
        /// </summary>
        private static void TestAlreadyProtectedMeshIsLeftAlone(Shader lilToon)
        {
            GameObject root = null;
            try
            {
                root = BuildMinimalAvatar(lilToon, "AlreadyProtectedAvatar", 4714, out var renderer,
                                          out var settings);
                if (MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out _))
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                var family = Shader.Find(settings.variant.shaderName + "/lilToon");
                if (family == null)
                {
                    Say("      (skipped already-protected/*: the family did not generate)");
                    return;
                }

                // Two slots: one ordinary lilToon, one already wearing a protection shader. The
                // second is what a re-run over an avatar somebody already protected looks like.
                var smr = (SkinnedMeshRenderer)renderer;
                var mesh = smr.sharedMesh;
                mesh.subMeshCount = 1;
                smr.sharedMaterials = new[]
                {
                    new Material(lilToon) { name = "StockMat" },
                    new Material(family) { name = "AlreadyProtectedMat" }
                };

                // Somebody else's amplitude, which must still be there afterwards.
                var uv6 = Enumerable.Range(0, mesh.vertexCount)
                                    .Select(i => new Vector4(0.25f, 0f, 0f, 0f)).ToList();
                mesh.SetUVs(6, uv6);

                bool ok;
                try { ok = new MeshProtectBuildHook().OnPreprocessAvatar(root); }
                catch (Exception e) { ok = false; Say("      already-protected fixture: hook threw - " + e.Message); }

                var after = new List<Vector4>();
                var afterMesh = smr.sharedMesh;
                if (afterMesh != null) afterMesh.GetUVs(6, after);
                bool kept = after.Count == uv6.Count &&
                            after.All(v => Mathf.Approximately(v.x, 0.25f));

                Check(kept, "already-protected/uv6-survives",
                      kept
                          ? "the mesh was left alone, so the protection already on it still works"
                          : ok
                              ? "the build overwrote UV6 on a mesh that was already protected - " +
                                "that sub-mesh can never be decoded again"
                              : "the build was refused rather than leaving the mesh alone");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
            }
        }

        private static GameObject BuildMinimalAvatar(Shader lilToon, string name, int seed,
                                                     out Renderer renderer, out MeshProtectRoot settings)
        {
            var root = new GameObject(name);
            root.AddComponent<Animator>();
            root.AddComponent<VRCAvatarDescriptor>();

            var bone = new GameObject("Bone");
            bone.transform.SetParent(root.transform, false);

            var mesh = BuildTestMesh(16);
            var weights = new BoneWeight[mesh.vertexCount];
            for (int i = 0; i < weights.Length; i++)
                weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { Matrix4x4.identity };

            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            var smr = body.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = new[] { bone.transform };
            smr.rootBone = bone.transform;
            smr.sharedMaterials = new[] { new Material(lilToon) { name = name + "Mat" } };
            renderer = smr;

            settings = root.AddComponent<MeshProtectRoot>();
            settings.outputFolder = "Assets/_MPTestScratch";
            settings.mode = MeshProtectRoot.DisplacementMode.TangentSpace;
            settings.distortRatio = 0.04f;
            var rng = new System.Random(seed);
            settings.keyDigits = MeshProtectCipher.GeneratePassword(rng);
            settings.variant = MeshProtectVariantGenerator.Generate(rng);
            if (MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out _))
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return root;
        }

        private static void TestIdMaskCollision(Shader lilToon, string label, bool featureInUse)
        {
            GameObject root = null;
            try
            {
                root = new GameObject("IdMaskAvatar_" + label);
                root.AddComponent<Animator>();
                root.AddComponent<VRCAvatarDescriptor>();

                var bone = new GameObject("Bone");
                bone.transform.SetParent(root.transform, false);

                var mesh = BuildTestMesh(16);
                var weights = new BoneWeight[mesh.vertexCount];
                for (int i = 0; i < weights.Length; i++)
                    weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                mesh.boneWeights = weights;
                mesh.bindposes = new[] { Matrix4x4.identity };

                var body = new GameObject("Body");
                body.transform.SetParent(root.transform, false);
                var smr = body.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mesh;
                smr.bones = new[] { bone.transform };
                smr.rootBone = bone.transform;

                var material = new Material(lilToon) { name = "IdMaskMat" };
                if (!material.HasProperty("_IDMaskFrom"))
                {
                    Say("      (skipped idmask/*: this lilToon has no _IDMaskFrom)");
                    return;
                }
                material.SetFloat("_IDMaskFrom", 6f);   // read the index from UV6.x

                // lilToon decides whether to compile the feature at all by looking for a non-zero
                // _IDMask*, so a material with the channel selected but nothing switched on is not
                // using the feature and must not be blocked.
                if (featureInUse && material.HasProperty("_IDMask1"))
                    material.SetFloat("_IDMask1", 1f);

                smr.sharedMaterials = new[] { material };

                var settings = root.AddComponent<MeshProtectRoot>();
                settings.outputFolder = "Assets/_MPTestScratch";
                settings.mode = MeshProtectRoot.DisplacementMode.TangentSpace;
                settings.distortRatio = 0.04f;
                var rng = new System.Random(featureInUse ? 9001 : 9002);
                settings.keyDigits = MeshProtectCipher.GeneratePassword(rng);
                settings.variant = MeshProtectVariantGenerator.Generate(rng);
                if (MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out _))
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                bool ok;
                try
                {
                    ok = new MeshProtectBuildHook().OnPreprocessAvatar(root);
                }
                catch (Exception e)
                {
                    ok = false;
                    Say("      idmask fixture: hook threw - " + e.Message);
                }

                var shipped = root.GetComponentsInChildren<Renderer>(true)
                    .SelectMany(r => r.sharedMaterials)
                    .Where(m => m != null && m.HasProperty("_IDMaskFrom") &&
                                Mathf.Approximately(m.GetFloat("_IDMaskFrom"), 6f))
                    .ToList();

                if (featureInUse)
                {
                    // A material may still read its ID mask from UV6 - that is the avatar's own
                    // setup and not this tool's business. What matters is whether the bake wrote
                    // the displacement amplitude over the channel it reads. An untouched mesh still
                    // has the all-zero UV6 the fixture gave it; a baked one does not.
                    var idMesh = smr.sharedMesh;
                    var idUv6 = new List<Vector4>();
                    if (idMesh != null) idMesh.GetUVs(6, idUv6);
                    bool untouched = idUv6.Count == 0 || idUv6.All(v => v == Vector4.zero);

                    Check(!ok || shipped.Count == 0 || untouched, "idmask/" + label,
                          !ok ? "build refused, so the collision never ships"
                              : shipped.Count == 0
                                  ? "nothing left reading its ID mask from the amplitude channel"
                                  : untouched
                                      ? "the mesh was left alone, so the ID mask still reads what " +
                                        "the avatar put there"
                                      : $"build SUCCEEDED with {shipped.Count} material(s) whose " +
                                        "ID mask reads UV6.x, which the bake overwrote");
                }
                else
                {
                    // The other half of the guard: a rule that blocks on _IDMaskFrom alone would
                    // refuse perfectly good avatars, and nothing else here would notice.
                    Check(ok, "idmask/" + label,
                          ok ? "an unused ID Mask pointed at UV6 is not mistaken for a collision"
                             : "build REFUSED an avatar whose ID Mask feature is switched off");
                }
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
            }
        }

        /// <summary>
        /// A root expression menu that is already at VRChat's limit of eight controls.
        ///
        /// This is not an edge case. Eight is a small number, plenty of avatars ship with a full
        /// root, and anything that installs menu items during the build - Modular Avatar most of
        /// all - can fill the last slot before this tool ever sees the avatar. Refusing to build
        /// leaves the author to go and rearrange a menu they did not design, which for someone
        /// selling the model means telling every buyer to do the same.
        ///
        /// Asserted: the build succeeds, and the unlock entry is actually reachable from wherever
        /// the descriptor now points. Reachable, not "in the root" - making room by nesting is a
        /// legitimate answer and the test should not forbid it.
        ///
        /// The has-room case exists to stop the fix from firing when it is not needed: nesting an
        /// avatar's whole menu one level deeper for no reason would be worse than the problem.
        /// </summary>
        private static void TestFullRootMenu(Shader lilToon, string label, int existingControls)
        {
            GameObject root = null;
            try
            {
                root = new GameObject("MenuAvatar_" + label);
                root.AddComponent<Animator>();
                var descriptor = root.AddComponent<VRCAvatarDescriptor>();

                var bone = new GameObject("Bone");
                bone.transform.SetParent(root.transform, false);

                var mesh = BuildTestMesh(16);
                var weights = new BoneWeight[mesh.vertexCount];
                for (int i = 0; i < weights.Length; i++)
                    weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                mesh.boneWeights = weights;
                mesh.bindposes = new[] { Matrix4x4.identity };

                var body = new GameObject("Body");
                body.transform.SetParent(root.transform, false);
                var smr = body.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mesh;
                smr.bones = new[] { bone.transform };
                smr.rootBone = bone.transform;
                smr.sharedMaterials = new[] { new Material(lilToon) { name = "MenuMat" } };

                if (!AssetDatabase.IsValidFolder("Assets/_MPTestScratch"))
                    AssetDatabase.CreateFolder("Assets", "_MPTestScratch");

                var rootMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                rootMenu.controls = Enumerable.Range(0, existingControls).Select(i =>
                    new VRCExpressionsMenu.Control
                    {
                        name = "Own" + i,
                        type = VRCExpressionsMenu.Control.ControlType.Toggle,
                        parameter = new VRCExpressionsMenu.Control.Parameter { name = "OwnToggle" + i }
                    }).ToList();
                AssetDatabase.CreateAsset(rootMenu,
                    $"Assets/_MPTestScratch/RootMenu_{label}.asset");

                var parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                parameters.parameters = Enumerable.Range(0, existingControls).Select(i =>
                    new VRCExpressionParameters.Parameter
                    {
                        name = "OwnToggle" + i,
                        valueType = VRCExpressionParameters.ValueType.Bool,
                        saved = true, networkSynced = true
                    }).ToArray();
                AssetDatabase.CreateAsset(parameters,
                    $"Assets/_MPTestScratch/RootParams_{label}.asset");

                descriptor.customExpressions = true;
                descriptor.expressionsMenu = rootMenu;
                descriptor.expressionParameters = parameters;

                var settings = root.AddComponent<MeshProtectRoot>();
                settings.outputFolder = "Assets/_MPTestScratch";
                settings.mode = MeshProtectRoot.DisplacementMode.TangentSpace;
                settings.distortRatio = 0.04f;
                var rng = new System.Random(existingControls == 8 ? 5150 : 5151);
                settings.keyDigits = MeshProtectCipher.GeneratePassword(rng);
                settings.variant = MeshProtectVariantGenerator.Generate(rng);
                if (MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out _))
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                bool ok;
                try
                {
                    ok = new MeshProtectBuildHook().OnPreprocessAvatar(root);
                }
                catch (Exception e)
                {
                    ok = false;
                    Say($"      menu/{label}: hook threw - " + e.Message);
                }

                int depth = UnlockDepth(descriptor.expressionsMenu,
                                        new HashSet<VRCExpressionsMenu>(), 0);

                Check(ok && depth >= 0, "menu/" + label,
                      !ok ? "build REFUSED - the author has to rearrange a menu they did not design"
                          : depth < 0 ? "build succeeded but the unlock entry is not reachable at all"
                          : $"unlock entry reachable {depth} level(s) below the root");

                // Only the full case is allowed to cost an extra level.
                if (ok && depth >= 0)
                {
                    bool acceptable = existingControls >= 8 ? depth <= 1 : depth == 0;
                    Check(acceptable, "menu/" + label + "/depth",
                          acceptable
                              ? (depth == 0 ? "sits directly in the root menu"
                                            : "one extra level, which the full root made necessary")
                              : $"unlock is {depth} level(s) deep with only {existingControls} " +
                                "controls in the way - the avatar's menu was nested for no reason");
                }

                // Depth of the UNLOCK entry cannot tell "added to a root that had room" apart from
                // "wrapped the whole menu and put unlock in the new root" - both leave it at depth
                // 0. Telling those apart is the entire point, so the AUTHOR's controls are what
                // gets measured.
                //
                // The rule is seven plus one: with a full root, at most ONE of the author's entries
                // may move, and it goes into the overflow page beside the unlock control. The
                // version that nested the whole menu passed a check on the unlock entry alone and
                // still made the avatar worse to wear - every toggle used all day moved a level
                // down to make room for one used once.
                int firstDepth = ControlDepth(descriptor.expressionsMenu,
                                              new HashSet<VRCExpressionsMenu>(), 0, "Own0");
                Check(firstDepth == 0, "menu/" + label + "/own-menu-not-nested",
                      firstDepth == 0
                          ? "the avatar's first control is still in the root menu"
                          : firstDepth < 0
                              ? "the avatar's own controls are not in the shipped menu at all"
                              : $"the avatar's own controls were pushed {firstDepth} level(s) down " +
                                "- the whole menu was nested to make room, which is the thing that " +
                                "came back as too hard to use");

                if (existingControls >= 8)
                {
                    // Exactly one displaced, and it is the last one, and it is still reachable.
                    int lastDepth = ControlDepth(descriptor.expressionsMenu,
                                                 new HashSet<VRCExpressionsMenu>(), 0,
                                                 "Own" + (existingControls - 1));
                    int stayed = Enumerable.Range(0, existingControls)
                        .Count(i => ControlDepth(descriptor.expressionsMenu,
                                                 new HashSet<VRCExpressionsMenu>(), 0, "Own" + i) == 0);

                    bool sevenPlusOne = lastDepth == 1 && stayed == existingControls - 1;
                    Check(sevenPlusOne, "menu/" + label + "/seven-plus-one",
                          lastDepth < 0
                              ? "the displaced control vanished from the menu entirely"
                          : sevenPlusOne
                              ? $"{stayed} of the author's controls stayed in the root and one " +
                                "moved into the overflow page with the unlock entry"
                              : $"{stayed} of {existingControls} stayed in the root and the last " +
                                $"is {lastDepth} level(s) deep - the overflow should cost exactly " +
                                "one control one level");
                }

                // Whatever happened, VRChat's limit still applies to every menu that ships.
                var over = AllMenus(descriptor.expressionsMenu, new HashSet<VRCExpressionsMenu>())
                    .Where(m => m.controls != null && m.controls.Count > 8)
                    .Select(m => $"{m.name} has {m.controls.Count}")
                    .ToList();
                Check(over.Count == 0, "menu/" + label + "/within-limit",
                      over.Count == 0 ? "no menu exceeds VRChat's eight controls"
                                      : string.Join(", ", over));
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var mpbuild in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                               "Assets/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(mpbuild)) AssetDatabase.DeleteAsset(mpbuild);
            }
        }

        /// <summary>Levels below the given menu at which a named control sits, or -1.</summary>
        private static int ControlDepth(VRCExpressionsMenu menu, HashSet<VRCExpressionsMenu> seen,
                                        int depth, string name)
        {
            if (menu == null || menu.controls == null || !seen.Add(menu)) return -1;
            foreach (var control in menu.controls)
            {
                if (control == null) continue;
                if (control.name == name) return depth;
                int found = ControlDepth(control.subMenu, seen, depth + 1, name);
                if (found >= 0) return found;
            }
            return -1;
        }

        /// <summary>Levels below the given menu at which the unlock control sits, or -1.</summary>
        private static int UnlockDepth(VRCExpressionsMenu menu, HashSet<VRCExpressionsMenu> seen,
                                       int depth)
        {
            if (menu == null || !seen.Add(menu)) return -1;
            foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>())
            {
                if (control == null) continue;
                if (control.name != null && control.name.Contains("Unlock")) return depth;
                int found = UnlockDepth(control.subMenu, seen, depth + 1);
                if (found >= 0) return found;
            }
            return -1;
        }

        private static IEnumerable<VRCExpressionsMenu> AllMenus(VRCExpressionsMenu menu,
                                                                HashSet<VRCExpressionsMenu> seen)
        {
            if (menu == null || !seen.Add(menu)) yield break;
            yield return menu;
            foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>())
            {
                if (control?.subMenu == null) continue;
                foreach (var m in AllMenus(control.subMenu, seen)) yield return m;
            }
        }

        /// <summary>
        /// A controller in the project whose every name says what it does. Created on disk, like a
        /// real one, so renaming it in place would be a visible act of vandalism on the project.
        /// </summary>
        private static AnimatorController ReadableController(string assetName, string layerName,
                                                             string machineName, string stateName,
                                                             string clipName)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(
                $"Assets/_MPTestScratch/{assetName}.controller");

            var machine = new AnimatorStateMachine
            {
                name = machineName,
                hideFlags = HideFlags.HideInHierarchy
            };
            AssetDatabase.AddObjectToAsset(machine, controller);

            var clip = new AnimationClip { name = clipName };
            AssetDatabase.CreateAsset(clip, $"Assets/_MPTestScratch/{clipName}.anim");
            machine.AddState(stateName).motion = clip;

            // A VRChat proxy animation, which the client swaps for real locomotion at runtime. It
            // is matched by name, so it is the one clip on the avatar that must come out of the
            // build with the name it went in with.
            // One name per controller. They used to share one, and Distinct() then reduced two
            // proxies to one fact: an FX controller that lost its proxy altogether still passed.
            var proxy = new AnimationClip { name = "proxy_" + assetName.ToLowerInvariant() };
            AssetDatabase.CreateAsset(proxy, $"Assets/_MPTestScratch/proxy_{assetName}.anim");
            machine.AddState("Locomotion").motion = proxy;

            controller.AddLayer(new AnimatorControllerLayer
            {
                name = layerName,
                defaultWeight = 1f,
                stateMachine = machine
            });
            return controller;
        }

        /// <summary>Every name a reader of the bundle would see in one controller.</summary>
        private static IEnumerable<string> AnimatorNames(AnimatorController controller)
        {
            foreach (var layer in controller.layers)
            {
                yield return layer.name;
                foreach (var n in MachineNames(layer.stateMachine, new HashSet<AnimatorStateMachine>()))
                    yield return n;
            }
        }

        private static IEnumerable<string> MachineNames(AnimatorStateMachine machine,
                                                        HashSet<AnimatorStateMachine> seen)
        {
            if (machine == null || !seen.Add(machine)) yield break;
            yield return machine.name;

            foreach (var child in machine.states)
            {
                if (child.state == null) continue;
                yield return child.state.name;
                foreach (var n in MotionNames(child.state.motion)) yield return n;
            }

            foreach (var child in machine.stateMachines)
                foreach (var n in MachineNames(child.stateMachine, seen)) yield return n;
        }

        /// <summary>
        /// Count a layer's states by Write Defaults setting, recursively.
        ///
        /// Recursively, because an avatar that keeps its states one level down in a sub-state
        /// machine would otherwise be counted as having none - which is how the build's own
        /// detector used to get the answer wrong without saying anything.
        /// </summary>
        private static void CountWriteDefaults(AnimatorStateMachine machine, ref int on, ref int off,
                                               HashSet<AnimatorStateMachine> seen)
        {
            if (machine == null || !seen.Add(machine)) return;

            foreach (var child in machine.states)
            {
                if (child.state == null) continue;
                if (child.state.writeDefaultValues) on++; else off++;
            }
            foreach (var sub in machine.stateMachines)
                CountWriteDefaults(sub.stateMachine, ref on, ref off, seen);
        }

        private static IEnumerable<string> MotionNames(Motion motion)
        {
            if (motion == null) yield break;
            yield return motion.name;
            if (motion is BlendTree tree)
                foreach (var child in tree.children)
                    foreach (var n in MotionNames(child.motion)) yield return n;
        }

        private static int CountMenuDigits(VRCExpressionsMenu menu, HashSet<string> menuParams,
                                           HashSet<VRCExpressionsMenu> seen)
        {
            if (menu == null || !seen.Add(menu)) return 0;
            int total = 0;
            foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>())
            {
                if (control == null) continue;
                // Digits only. The reset control writes the same Int as position one, with a value
                // no digit uses, so counting every control on these parameters counts it too - and
                // then "48 digit controls" quietly becomes "49 controls, one of which we have not
                // checked is a digit".
                if (!string.IsNullOrEmpty(control.parameter?.name) &&
                    menuParams.Contains(control.parameter.name) &&
                    control.value >= MeshProtectRoot.MinDigit &&
                    control.value <= MeshProtectRoot.MaxDigit) total++;
                total += CountMenuDigits(control.subMenu, menuParams, seen);
            }
            return total;
        }

        /// <summary>Controls on the unlock parameters carrying one particular value.</summary>
        private static int CountMenuControls(VRCExpressionsMenu menu, HashSet<string> menuParams,
                                             HashSet<VRCExpressionsMenu> seen, int value)
        {
            if (menu == null || !seen.Add(menu)) return 0;
            int total = 0;
            foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>())
            {
                if (control == null) continue;
                if (!string.IsNullOrEmpty(control.parameter?.name) &&
                    menuParams.Contains(control.parameter.name) &&
                    Mathf.RoundToInt(control.value) == value) total++;
                total += CountMenuControls(control.subMenu, menuParams, seen, value);
            }
            return total;
        }

        private static IEnumerable<string> CollectMenuNames(VRCExpressionsMenu menu,
                                                            HashSet<VRCExpressionsMenu> seen)
        {
            if (menu == null || !seen.Add(menu)) yield break;
            yield return menu.name;
            foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>())
            {
                if (control?.subMenu == null) continue;
                foreach (var n in CollectMenuNames(control.subMenu, seen)) yield return n;
            }
        }

        private static Mesh BuildTestMesh(int side)
        {
            var mesh = new Mesh { name = "E2EGrid" };
            int n = side * side;
            var vertices = new Vector3[n];
            var uv = new Vector2[n];
            var normals = new Vector3[n];

            for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int i = y * side + x;
                float fx = x / (float)(side - 1);
                float fy = y / (float)(side - 1);
                vertices[i] = new Vector3(fx * 2f - 1f,
                                          0.25f * Mathf.Sin(fx * 3.1f) * Mathf.Cos(fy * 2.7f),
                                          fy * 2f - 1f);
                uv[i] = new Vector2(fx, fy);
                normals[i] = new Vector3(fx * 0.2f - 0.1f, 1f, fy * 0.2f - 0.1f).normalized;
            }

            var tris = new List<int>();
            for (int y = 0; y < side - 1; y++)
            for (int x = 0; x < side - 1; x++)
            {
                int i = y * side + x;
                tris.Add(i); tris.Add(i + side); tris.Add(i + 1);
                tris.Add(i + 1); tris.Add(i + side); tris.Add(i + side + 1);
            }

            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.normals = normals;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
