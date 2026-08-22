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
using VRC.SDKBase;
using MeshProtect;

namespace MPTest
{
    /// <summary>
    /// Walks the generated unlock chain and checks that it is wired correctly, end to end:
    ///
    ///     menu digit -> pack layer -> synced bools -> decode layer -> material property -> shader
    ///
    /// The end-to-end test proves those pieces exist. It cannot prove they are connected the right
    /// way round, and every failure mode here is invisible to a structural check: bits emitted in
    /// the wrong order, a transition condition with inverted polarity, an off-by-one between the
    /// digit and its bit pattern. Each of those produces a perfectly well-formed animator that
    /// decodes to the wrong password, which nobody would notice until the avatar was in game.
    ///
    /// So this simulates the animator instead of inspecting it: for every digit of every position,
    /// resolve which pack state the Int would select, apply its parameter driver, resolve which
    /// decode state those bools would select, and read the material value that state actually
    /// writes. It must come back as the digit that went in.
    /// </summary>
    public static class UnlockChainTest
    {
        private static readonly StringBuilder Log = new StringBuilder();
        private static int failures;

        private static void Say(string line)
        {
            Log.AppendLine(line);
            Debug.Log("[CHAIN] " + line);
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
                Say("=== Unlock chain simulation ===");

                var lilToon = Shader.Find("lilToon");
                if (lilToon == null) throw new Exception("lilToon shader not found");

                avatar = BuildAvatar(lilToon, out var variant, out var renderer);
                Say($"      protection {variant.shaderName}");

                var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
                var fx = descriptor.baseAnimationLayers
                    .First(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX)
                    .animatorController as AnimatorController;
                if (fx == null) throw new Exception("no FX controller was assigned");

                string rendererPath = AnimationUtility.CalculateTransformPath(
                    renderer.transform, avatar.transform);

                int cases = 0;
                var wrong = new List<string>();

                for (int position = 0; position < MeshProtectRoot.PasswordLength; position++)
                {
                    string packLayer = variant.packLayerNames[position];
                    string decodeLayer = variant.decodeLayerNames[position];

                    for (int digit = MeshProtectRoot.MinDigit; digit <= MeshProtectRoot.MaxDigit; digit++)
                    {
                        cases++;

                        // 1. Which pack state does this Int value select, and what does its driver set?
                        var packState = StateForInt(fx, packLayer, variant.parameterNames[position], digit);
                        if (packState == null) { wrong.Add($"pos{position} digit{digit}: no pack state"); continue; }

                        var bits = DriverBits(packState, variant, position);
                        if (bits == null) { wrong.Add($"pos{position} digit{digit}: no driver"); continue; }

                        // 2. Which decode state do those bools select, and what does its clip write?
                        var decodeState = StateForBits(fx, decodeLayer, variant, position, bits);
                        if (decodeState == null)
                        {
                            wrong.Add($"pos{position} digit{digit}: bits {Bits(bits)} match no decode state");
                            continue;
                        }

                        float written = MaterialValue(decodeState, rendererPath,
                                                      variant.digitProperties[position]);
                        // NaN means the clip carries no curve for that property at all. Comparing
                        // it would quietly succeed - Mathf.Abs(NaN - digit) > eps is false - so a
                        // binding Unity had silently refused to store would read as correct.
                        if (float.IsNaN(written))
                            wrong.Add($"pos{position} digit{digit}: nothing drives the property");
                        else if (Mathf.Abs(written - digit) > 0.0001f)
                            wrong.Add($"pos{position} digit{digit}: bits {Bits(bits)} -> wrote {written}");
                    }
                }

                Check(wrong.Count == 0, "chain/digit-round-trip",
                      wrong.Count == 0
                          ? $"all {cases} (position, digit) pairs survive menu -> bools -> material"
                          : $"{wrong.Count}/{cases} wrong: " + string.Join("; ", wrong.Take(5)));

                // The locked state: every bool false is what a fresh avatar carries. It must
                // decode to zero - not to a digit - because zero is what the shader collapses on.
                // Anything else and a fresh avatar renders its scrambled mesh instead of nothing.
                var lockedDigits = new List<int>();
                for (int position = 0; position < MeshProtectRoot.PasswordLength; position++)
                {
                    var state = StateForBits(fx, variant.decodeLayerNames[position],
                                             variant, position, new bool[MeshProtectRoot.BitsPerDigit]);
                    float locked = state == null
                        ? float.NaN
                        : MaterialValue(state, rendererPath, variant.digitProperties[position]);
                    lockedDigits.Add(float.IsNaN(locked) ? -1 : Mathf.RoundToInt(locked));
                }
                Check(lockedDigits.All(d => d == 0), "chain/locked-state",
                      $"all bools false decodes to [{string.Join(",", lockedDigits)}] (0 = locked)");
                // ...and no digit the menu can produce may land on that pattern. If one could, a
                // perfectly correct password would collapse the avatar instead of decoding it.
                // This replaced an assertion comparing two compile-time constants, which passed by
                // construction and could not have caught anything.
                // What the menu can actually send, per parameter. Without this the two halves of
                // "a wearer can clear a position" are checked by two assertions that never meet:
                // one counts a menu control writing MaxDigit + 1, the other finds a state that
                // clears the bits and is triggered by something outside 1..8. Point the control at
                // 10 and leave the transition on 9 and both stay green with the reset dead.
                var menuValues = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
                CollectMenuValues(descriptor.expressionsMenu, new HashSet<VRCExpressionsMenu>(),
                                  menuValues);

                var reachesLocked = new List<string>();
                var cannotClear = new List<string>();
                for (int position = 0; position < MeshProtectRoot.PasswordLength; position++)
                {
                    var layer = fx.layers.FirstOrDefault(
                        l => l.name == variant.packLayerNames[position]);
                    if (layer == null)
                    {
                        reachesLocked.Add($"position {position}: no pack layer");
                        cannotClear.Add($"position {position}: no pack layer");
                        continue;
                    }

                    // Every pack layer, not just this position's. One state clears all six
                    // positions' bits, so the state that puts position 5 back sits on layer 0 -
                    // looking only at a position's own layer reports five positions as unclearable
                    // when they are not.
                    bool clearable = false;
                    foreach (var transition in fx.layers
                                 .Where(l => variant.packLayerNames.Contains(l.name))
                                 .SelectMany(l => l.stateMachine.anyStateTransitions))
                    {
                        var state = transition.destinationState;
                        if (state == null) continue;

                        var driven = DriverBits(state, variant, position);
                        if (driven == null || !driven.All(bit => !bit)) continue;

                        if (transition.conditions.Length == 0) continue;
                        int menuValue = Mathf.RoundToInt(transition.conditions[0].threshold);
                        string conditionParam = transition.conditions[0].parameter;

                        // A digit landing on the not-entered pattern would collapse the avatar on a
                        // correct password.
                        if (menuValue >= MeshProtectRoot.MinDigit &&
                            menuValue <= MeshProtectRoot.MaxDigit)
                        {
                            reachesLocked.Add(
                                $"position {position}: digit {menuValue} ('{state.name}')");
                            continue;
                        }

                        // Reachable only if the menu writes this exact value to this exact
                        // parameter. A state nothing can trigger clears nothing.
                        if (menuValues.TryGetValue(conditionParam, out var sendable) &&
                            sendable.Contains(menuValue))
                            clearable = true;
                    }

                    if (!clearable) cannotClear.Add($"position {position}");
                }
                Check(reachesLocked.Count == 0, "chain/locked-state-unreachable-by-digit",
                      reachesLocked.Count == 0
                          ? "no digit the menu offers drives a position to the not-entered pattern"
                          : string.Join(", ", reachesLocked));

                // The other direction FLIPPED with the reset control's removal: not-entered is
                // now deliberately unreachable from the menu. The way back for a wearer who
                // touched a spare dial is VRChat's own Reset Avatar Data, which clears the saved
                // transport bools wholesale - native, out of this chain's hands, and impossible
                // for this test to reach.
                //
                // What must still hold is the half that keeps the chain SAFE without a reset:
                // no state a menu control can trigger may drive a position to the not-entered
                // pattern (asserted above as locked-state-unreachable-by-digit), because a digit
                // landing there would collapse the avatar on a correct password. Clearability
                // itself is asserted to be absent, so a future "clear" state cannot half-return:
                // either it comes back for every position with the menu control to reach it, or
                // it stays out entirely.
                Check(cannotClear.Count == MeshProtectRoot.PasswordLength,
                      "chain/not-entered-unreachable-by-design",
                      cannotClear.Count == MeshProtectRoot.PasswordLength
                          ? "no menu control clears any position - the way back is VRChat's own " +
                            "Reset Avatar Data, and nothing in the chain can wipe a password"
                          : $"{MeshProtectRoot.PasswordLength - cannotClear.Count} position(s) " +
                            "have a clear path from the menu - the reset was removed deliberately, " +
                            "and a partial return is the worst of both: it can wipe some of a " +
                            "saved password but not restore a short one");

                // The pack layer must not clobber a restored password. Its default state runs
                // before the owner touches anything, on a machine where the saved bools have just
                // been restored and the local Int is still 0.
                var offenders = new List<string>();
                foreach (var layer in fx.layers.Where(l => variant.packLayerNames.Contains(l.name)))
                {
                    var def = layer.stateMachine.defaultState;
                    if (def == null) { offenders.Add(layer.name + " (none)"); continue; }
                    if (def.behaviours.OfType<VRCAvatarParameterDriver>().Any())
                        offenders.Add(layer.name + " (has a driver)");
                }
                Check(offenders.Count == 0, "chain/pack-default-is-inert",
                      offenders.Count == 0
                          ? "no pack layer would overwrite a restored password on load"
                          : string.Join(", ", offenders));

                // Drivers must be local-only. A remote client running them would fight the synced
                // value it just received from the owner.
                var remoteDrivers = fx.layers.Where(l => variant.packLayerNames.Contains(l.name))
                    .SelectMany(l => l.stateMachine.states)
                    .SelectMany(c => c.state.behaviours.OfType<VRCAvatarParameterDriver>())
                    .Count(d => !d.localOnly);
                Check(remoteDrivers == 0, "chain/drivers-local-only",
                      $"{remoteDrivers} driver(s) would also run on remote clients");

                // Unity as the independent oracle. Every check above compares our own emitted
                // string against our own expected string, which cannot catch the two of them being
                // wrong together - and they were: "material" + "_zabc" produced "material_zabc"
                // instead of "material._zabc", a property nothing has. The curves were written,
                // saved and shipped, and never reached a material. Ask Unity what is animatable
                // instead of asking ourselves.
                var animatable = new HashSet<string>(
                    AnimationUtility.GetAnimatableBindings(renderer.gameObject, avatar)
                        .Where(b2 => typeof(Renderer).IsAssignableFrom(b2.type))
                        .Select(b2 => b2.propertyName));

                var emitted = fx.layers.Where(l => variant.decodeLayerNames.Contains(l.name))
                    .SelectMany(l => l.stateMachine.states)
                    .Select(c => c.state.motion as AnimationClip).Where(c => c != null).Distinct()
                    .SelectMany(AnimationUtility.GetCurveBindings)
                    .Select(b2 => b2.propertyName).Distinct().ToList();

                var bogus = emitted.Where(n => !animatable.Contains(n)).ToList();

                // The count matters as much as the membership. Unity DISCARDS a curve whose
                // property does not exist, so a malformed binding leaves the clip empty - and an
                // empty list satisfies "every emitted binding is animatable" perfectly. That is
                // exactly how the missing-dot bug walked past the test written to catch it; a
                // mutation run put the dot back in the bin and this check stayed green.
                Check(emitted.Count == MeshProtectRoot.PasswordLength && bogus.Count == 0,
                      "chain/bindings-are-animatable",
                      bogus.Count > 0
                          ? "Unity does not recognise: " + string.Join(", ", bogus.Take(4))
                          : emitted.Count != MeshProtectRoot.PasswordLength
                              ? $"expected {MeshProtectRoot.PasswordLength} material bindings, found " +
                                $"{emitted.Count} - Unity discards curves for properties that do " +
                                "not exist, so this means the names are malformed"
                              : $"Unity accepts all {emitted.Count} emitted material binding(s)");

                Say("      animatable material bindings Unity offers for this renderer:");
                foreach (var n in animatable.Where(n => n.StartsWith("material"))
                                            .OrderBy(n => n).Take(6))
                    Say("          " + n);

                // Multi-slot: does one binding reach every protected slot, or is one needed each?
                int slots = renderer.sharedMaterials.Length;
                int perSlot = animatable.Count(n => n.Contains(variant.digitProperties[0]));
                Say($"      renderer has {slots} material slot(s); Unity offers {perSlot} " +
                    $"binding(s) for one digit property");

                // MMD worlds disable FX layers 1 and 2. A password layer sitting there stops
                // running, and a locked avatar is invisible, so the wearer would vanish on entering
                // one.
                var ourNames = new HashSet<string>(
                    variant.packLayerNames.Concat(variant.decodeLayerNames));
                var ours = fx.layers.Select((l, idx) => (l.name, idx))
                    .Where(x => ourNames.Contains(x.name)).ToList();
                var inMmdSlots = ours.Where(x => x.idx == 1 || x.idx == 2).Select(x => x.name).ToList();
                Check(inMmdSlots.Count == 0, "chain/mmd-slots-free",
                      inMmdSlots.Count == 0
                          ? $"our {ours.Count} layer(s) start at index " +
                            (ours.Count == 0 ? "n/a - none were found" : ours.Min(x => x.idx).ToString())
                          : "in MMD-disabled slots: " + string.Join(", ", inMmdSlots));

                // Our states have to agree with the avatar's about Write Defaults, and this fixture
                // builds an avatar with no FX controller of its own - so there is nothing to agree
                // with and the build falls back to on, which is VRChat's own default.
                //
                // The comment that used to sit here argued the opposite: that requiring the whole
                // controller to agree was the mistake, and that a fixture with no avatar layers
                // could not show what was wrong with it. It had the failure exactly backwards. The
                // assertion demanded our states never write defaults, stayed green, and the release
                // it certified could not move its face in MMD worlds - because those worlds switch
                // off the FX layers holding the expressions to let their dance drive the blend
                // shapes, and a controller with the two settings mixed does not release them.
                //
                // This fixture still cannot demonstrate the disagreement, for the reason the old
                // comment gave. What can is e2e/our-states-match-the-avatar, which builds an avatar
                // that has FX layers of its own.
                var oursLayers = new HashSet<string>(variant.packLayerNames
                    .Concat(variant.decodeLayerNames).Concat(variant.padLayerNames));

                int avatarOn = 0, avatarOff = 0;
                foreach (var layer in fx.layers)
                {
                    if (oursLayers.Contains(layer.name) || layer.stateMachine == null) continue;
                    foreach (var child in layer.stateMachine.states)
                        if (child.state != null)
                        {
                            if (child.state.writeDefaultValues) avatarOn++; else avatarOff++;
                        }
                }
                bool expected = avatarOff == 0 || avatarOn >= avatarOff;

                var disagreeing = new List<string>();
                foreach (var layer in fx.layers)
                {
                    if (!oursLayers.Contains(layer.name) || layer.stateMachine == null) continue;
                    foreach (var child in layer.stateMachine.states)
                        if (child.state != null && child.state.writeDefaultValues != expected)
                            disagreeing.Add(layer.name + "/" + child.state.name);
                }
                Check(disagreeing.Count == 0, "chain/our-states-match-the-avatar",
                      disagreeing.Count == 0
                          ? $"the avatar's own layers say Write Defaults {(expected ? "ON" : "OFF")} " +
                            $"({avatarOn} on, {avatarOff} off) and every generated state does too"
                          : $"{disagreeing.Count} generated state(s) disagree: " +
                            string.Join(", ", disagreeing.Take(4)));

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
                foreach (var f in new[] { MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild",
                                          "Assets/_MeshProtectBuild", "Assets/_MPTestScratch" })
                    if (AssetDatabase.IsValidFolder(f)) AssetDatabase.DeleteAsset(f);
                AssetDatabase.Refresh();
            }

            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "chain-result.txt"), Log.ToString());
            return exit;
        }

        // ------------------------------------------------------------------ animator walking

        private static AnimatorState StateForInt(AnimatorController fx, string layerName,
                                                 string param, int value)
        {
            var layer = fx.layers.FirstOrDefault(l => l.name == layerName);
            if (layer == null) return null;

            foreach (var t in layer.stateMachine.anyStateTransitions)
            {
                bool match = t.conditions.Length == 1
                             && t.conditions[0].parameter == param
                             && t.conditions[0].mode == AnimatorConditionMode.Equals
                             && Mathf.RoundToInt(t.conditions[0].threshold) == value;
                if (match) return t.destinationState;
            }
            return null;
        }

        /// <summary>Every value the menu tree can write, per parameter.</summary>
        private static void CollectMenuValues(VRCExpressionsMenu menu,
                                              HashSet<VRCExpressionsMenu> seen,
                                              Dictionary<string, HashSet<int>> into)
        {
            if (menu == null || !seen.Add(menu)) return;

            foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>())
            {
                if (control == null) continue;

                string name = control.parameter?.name;
                if (!string.IsNullOrEmpty(name))
                {
                    if (!into.TryGetValue(name, out var values))
                        into[name] = values = new HashSet<int>();
                    values.Add(Mathf.RoundToInt(control.value));
                }

                CollectMenuValues(control.subMenu, seen, into);
            }
        }

        private static bool[] DriverBits(AnimatorState state, MeshProtectVariant variant, int position)
        {
            var driver = state.behaviours.OfType<VRCAvatarParameterDriver>().FirstOrDefault();
            if (driver == null) return null;

            var bits = new bool[MeshProtectRoot.BitsPerDigit];
            for (int b = 0; b < bits.Length; b++)
            {
                string name = variant.bitNames[position * MeshProtectRoot.BitsPerDigit + b];
                var set = driver.parameters.FirstOrDefault(
                    p => p.name == name && p.type == VRC_AvatarParameterDriver.ChangeType.Set);
                if (set == null) return null;
                bits[b] = set.value > 0.5f;
            }
            return bits;
        }

        private static AnimatorState StateForBits(AnimatorController fx, string layerName,
                                                  MeshProtectVariant variant, int position, bool[] bits)
        {
            var layer = fx.layers.FirstOrDefault(l => l.name == layerName);
            if (layer == null) return null;

            foreach (var t in layer.stateMachine.anyStateTransitions)
            {
                bool match = t.conditions.Length == bits.Length;
                for (int b = 0; match && b < bits.Length; b++)
                {
                    string name = variant.bitNames[position * MeshProtectRoot.BitsPerDigit + b];
                    var c = t.conditions.FirstOrDefault(x => x.parameter == name);
                    if (c.parameter != name) { match = false; break; }
                    bool wants = c.mode == AnimatorConditionMode.If;
                    if (wants != bits[b]) match = false;
                }
                if (match) return t.destinationState;
            }
            return null;
        }

        private static float MaterialValue(AnimatorState state, string rendererPath, string property)
        {
            var clip = state.motion as AnimationClip;
            if (clip == null) return float.NaN;

            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.path != rendererPath) continue;
                if (binding.propertyName != "material." + property) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve != null && curve.length > 0) return curve.Evaluate(0f);
            }
            return float.NaN;
        }

        private static string Bits(bool[] bits) => string.Concat(bits.Select(b => b ? '1' : '0'));

        // ------------------------------------------------------------------ fixture

        private static GameObject BuildAvatar(Shader lilToon, out MeshProtectVariant variant,
                                              out SkinnedMeshRenderer renderer)
        {
            var root = new GameObject("ChainAvatar");
            root.AddComponent<Animator>();
            root.AddComponent<VRCAvatarDescriptor>();

            var bone = new GameObject("Bone");
            bone.transform.SetParent(root.transform, false);

            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            renderer = body.AddComponent<SkinnedMeshRenderer>();

            var mesh = new Mesh { name = "ChainQuad" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.forward, new Vector3(1, 0, 1) };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            mesh.normals = Enumerable.Repeat(Vector3.up, 4).ToArray();
            // Two sub-meshes, so the renderer has two material slots. Real avatars are full of
            // multi-slot renderers and Unity's material animation is per-slot, so a fixture with a
            // single material would not exercise the binding that actually matters.
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 2, 1 }, 0);
            mesh.SetTriangles(new[] { 1, 2, 3 }, 1);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            mesh.boneWeights = Enumerable.Repeat(
                new BoneWeight { boneIndex0 = 0, weight0 = 1f }, 4).ToArray();
            mesh.bindposes = new[] { Matrix4x4.identity };

            renderer.sharedMesh = mesh;
            renderer.bones = new[] { bone.transform };
            renderer.rootBone = bone.transform;
            renderer.sharedMaterials = new[]
            {
                new Material(lilToon) { name = "ChainMaterial0" },
                new Material(lilToon) { name = "ChainMaterial1" }
            };

            var settings = root.AddComponent<MeshProtectRoot>();
            settings.outputFolder = "Assets/_MPTestScratch";

            var rng = new System.Random(31415);
            settings.keyDigits = MeshProtectCipher.GeneratePassword(rng);
            settings.variant = MeshProtectVariantGenerator.Generate(rng);
            variant = settings.variant;

            if (MeshProtectShaderGen.EnsureGenerated(settings, variant, out string folder))
                MeshProtectShaderGen.ImportGenerated(folder);

            if (!new MeshProtectBuildHook().OnPreprocessAvatar(root))
                throw new Exception("preprocess blocked while building the fixture");

            return root;
        }
    }
}
