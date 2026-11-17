#if UNITY_EDITOR && LILMP_VRCSDK3_AVATARS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using MeshProtect;

namespace MPTest
{
    /// <summary>
    /// The lock gate: the layer that keeps everything the decode cannot hide switched off until
    /// the password is in.
    ///
    /// It exists because of what a locked avatar used to show. The lock is the decode collapsing
    /// a mesh, so it only reaches what wears the decode, and an avatar is rarely all of that -
    /// which came back from the people it happened to as "the head and the clothes vanish and
    /// the body stands there naked", with accessories left hanging in the air around it.
    ///
    /// Three things can go wrong with a layer like this and none of them shows in a structural
    /// check, so each is simulated or asked of Unity rather than read off our own strings:
    ///
    ///   - it opens at the wrong moment. The state machine is walked by hand through the
    ///     moments that matter: nothing entered, part-way through, complete, reset.
    ///   - it knows more than it should. An animator that could tell a right password from a
    ///     wrong one would be the password, written into a file that ships. So every complete
    ///     entry must open it, right or wrong - that is the test, not a concession.
    ///   - a curve reaches nothing. The renderers ship switched OFF, so a binding Unity cannot
    ///     resolve is not a part that shows early, it is a part that never shows at all. The
    ///     clips are sampled onto the avatar and the components are read back.
    /// </summary>
    public static class LockGateTest
    {
        private static readonly StringBuilder Log = new StringBuilder();
        private static int failures;

        private const string Scratch = "Assets/_MPGateScratch";

        private static void Say(string line)
        {
            Log.AppendLine(line);
            Debug.Log("[GATE] " + line);
        }

        private static void Check(bool ok, string label, string detail)
        {
            if (!ok) failures++;
            Say($"{(ok ? "PASS" : "FAIL")}  {label}  {detail}");
        }

        public static void Run() => EditorApplication.Exit(RunCore());

        public static int RunCore()
        {
            int exit = 0;
            failures = 0;
            Log.Length = 0;

            try
            {
                Say("=== Lock gate ===");

                var lilToon = Shader.Find("lilToon");
                var standard = Shader.Find("Standard");
                if (lilToon == null) throw new Exception("lilToon shader not found");
                if (standard == null) throw new Exception("Standard shader not found");

                SixDigits(lilToon, standard);
                ShortPassword(lilToon, standard);
                SwitchedOff(lilToon, standard);

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
                foreach (var f in new[] { Scratch, MeshProtectShaderGen.OutputRoot(null) + "/_MeshProtectBuild" })
                    if (AssetDatabase.IsValidFolder(f)) AssetDatabase.DeleteAsset(f);
                AssetDatabase.Refresh();
            }

            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "gate-result.txt"),
                              Log.ToString());
            return exit;
        }

        // ------------------------------------------------------------------ cases

        private static void SixDigits(Shader lilToon, Shader standard)
        {
            var digits = new[] { 3, 7, 2, 6, 1, 5 };
            var fixture = Build(lilToon, standard, digits, hide: true, seed: 27182);
            try
            {
                var report = fixture.report;
                Check(report.appliedProtection, "gate/build-protects", "the fixture has to be a protected avatar");

                var fx = fixture.Fx();
                string gateName = GateName(fixture.variant);
                var layer = fx == null ? null : fx.layers.FirstOrDefault(l => l.name == gateName);
                Check(layer != null, "gate/layer-exists",
                      layer != null ? $"layer '{gateName}' is in the FX controller"
                                    : "no gate layer was added");
                if (layer == null) return;

                // What a locked avatar would have shown, and what the gate did about each.
                Check(!fixture.hat.enabled && !fixture.sparks.enabled && !fixture.mixed.enabled &&
                      !fixture.twinA.enabled,
                      "gate/unprotected-renderers-ship-switched-off",
                      $"hat {fixture.hat.enabled}, particles {fixture.sparks.enabled}, " +
                      $"part-protected {fixture.mixed.enabled}, first twin {fixture.twinA.enabled} " +
                      "(all must be False: a viewer whose client does not run this controller must " +
                      "not be shown the leftovers either)");

                Check(fixture.body.enabled, "gate/a-fully-protected-renderer-is-left-alone",
                      "the decode already hides it; switching it would only add a way to lose it");

                Check(fixture.toggled.enabled, "gate/an-animated-renderer-is-left-alone",
                      "its enabled flag is already driven by the avatar's own FX layer, and the " +
                      "gate would win that fight from the last layer and kill the toggle");

                Check(fixture.twinB.enabled, "gate/a-renderer-a-path-cannot-single-out-is-left-alone",
                      "the curve would bind to its earlier twin, and this one would never come back on");

                Check(!fixture.alreadyOff.enabled, "gate/an-already-disabled-renderer-stays-off",
                      "nothing to hide, and switching it on is not this tool's to decide");

                Check(report.hiddenWhileLocked == 4 && report.visibleWhileLocked == 3,
                      "gate/report-counts",
                      $"hidden {report.hiddenWhileLocked} (want 4), visible {report.visibleWhileLocked} " +
                      "(want 3: the animated one and the second of each pair of twins)");

                // Two PROTECTABLE siblings with one name. The digits travel by animation path
                // just as the gate does, so the second could never receive one: displaced, it
                // would stay collapsed with the right password, for everybody. It has to be left
                // exactly as it came in - and the first must not pay for it.
                bool firstProtected = fixture.pairA.sharedMaterials.All(
                    m => MeshProtectPipeline.IsProtectShader(m, fixture.variant.shaderName));
                bool secondUntouched = fixture.pairB.sharedMaterials.All(
                    m => !MeshProtectPipeline.IsProtectShader(m, fixture.variant.shaderName)) &&
                    fixture.pairB.sharedMesh == fixture.pairBMesh;
                Check(firstProtected && secondUntouched,
                      "gate/a-protectable-twin-is-left-unprotected-rather-than-unreachable",
                      $"first of the pair protected: {firstProtected}; second left with its own " +
                      $"material and mesh: {secondUntouched}");
                Check(report.warnings.Any(w => w.Contains("has the same name") && w.Contains("could never be")),
                      "gate/the-unreachable-twin-is-named",
                      "the author can fix this by renaming one object, if the report says so");

                Check(report.warnings.Any(w => w.Contains("HIDDEN, NOT PROTECTED")),
                      "gate/report-says-hidden-is-not-protected",
                      "the meshes behind the gate ship readable and the report has to say so");

                // Unity as the oracle for the bindings: sample each clip onto the avatar and read
                // the components back.
                var closed = layer.stateMachine.defaultState.motion as AnimationClip;
                var open = layer.stateMachine.states.Select(s => s.state.motion as AnimationClip)
                                .FirstOrDefault(c => c != null && c != closed);
                Check(closed != null && open != null, "gate/two-clips", "a closed clip and an open one");
                if (closed == null || open == null) return;

                var gated = new Renderer[] { fixture.hat, fixture.sparks, fixture.mixed, fixture.twinA };
                open.SampleAnimation(fixture.root, 0f);
                bool allOn = gated.All(r => r.enabled);
                closed.SampleAnimation(fixture.root, 0f);
                bool allOff = gated.All(r => !r.enabled);
                Check(allOn && allOff, "gate/the-clips-really-drive-the-renderers",
                      $"sampling the open clip switched every gated renderer on: {allOn}; the " +
                      $"closed clip switched them off again: {allOff}. A curve Unity cannot resolve " +
                      "leaves its renderer off for good");

                Check(fixture.toggled.enabled && fixture.twinB.enabled && fixture.body.enabled,
                      "gate/the-clips-touch-nothing-else",
                      "sampling the closed clip must not switch off anything the gate left alone");

                // The moments that matter, walked through the state machine.
                var none = new bool[fixture.variant.bitNames.Length];
                Check(!IsOpen(layer, fixture.variant, none, open), "gate/closed-with-nothing-entered",
                      "this is the state everybody meets a locked avatar in");

                var partial = Bits(fixture.variant, new[] { 3, 7, 2, 6, 1, 0 });
                Check(!IsOpen(layer, fixture.variant, partial, open), "gate/closed-part-way-through",
                      "five of six digits in: opening here is what would show the leftovers for " +
                      "as long as the wearer takes to find the last digit");

                var skipped = Bits(fixture.variant, new[] { 3, 7, 0, 6, 1, 5 });
                Check(!IsOpen(layer, fixture.variant, skipped, open), "gate/closed-with-a-gap",
                      "a position in the middle left out is not the password's shape either");

                var right = Bits(fixture.variant, digits);
                Check(IsOpen(layer, fixture.variant, right, open), "gate/opens-on-the-complete-password",
                      "all six in: the protected half appears on the same digits");

                // It must not know which digits. Every complete entry opens it.
                var rng = new System.Random(99);
                int opened = 0, tried = 200;
                for (int i = 0; i < tried; i++)
                {
                    var guess = new int[MeshProtectRoot.PasswordLength];
                    for (int p = 0; p < guess.Length; p++)
                        guess[p] = rng.Next(MeshProtectRoot.MinDigit, MeshProtectRoot.MaxDigit + 1);
                    if (IsOpen(layer, fixture.variant, Bits(fixture.variant, guess), open)) opened++;
                }
                Check(opened == tried, "gate/does-not-know-the-password",
                      $"{opened}/{tried} random complete entries open it. Anything short of all of " +
                      "them means the controller can tell a right digit from a wrong one - and " +
                      "then the controller, which ships, holds part of the password");

                // And it closes again when the saved parameters are wiped under it.
                var state = Walk(fixture.variant, layer.stateMachine.defaultState, right);
                state = Walk(fixture.variant, state, none);
                Check(state != null && state.motion == closed, "gate/closes-again-on-reset",
                      "Reset Avatar Data clears the transport bits while the gate is open");

                // The validator has to agree the avatar is sound with the gate on it.
                var fatal = MeshProtectValidator.Validate(fixture.root, fixture.variant)
                                                .Where(i => i.fatal).Select(i => i.message).ToList();
                Check(fatal.Count == 0, "gate/validator-accepts-the-avatar",
                      fatal.Count == 0 ? "no fatal issue" : string.Join(" | ", fatal.Take(3)));

                // ...and to refuse one whose gate reaches nothing. The renderer ships switched
                // off, so this is a part of the avatar that would never be shown.
                fixture.hat.gameObject.name = "RenamedBehindTheGatesBack";
                var afterRename = MeshProtectValidator.Validate(fixture.root, fixture.variant)
                                                      .Where(i => i.fatal).ToList();
                Check(afterRename.Any(i => i.message.Contains("lock gate")),
                      "gate/validator-refuses-a-curve-that-reaches-nothing",
                      "a gated renderer whose path no longer resolves must stop the upload");
            }
            finally { fixture.Dispose(); }
        }

        private static void ShortPassword(Shader lilToon, Shader standard)
        {
            // Four digits. Positions five and six are part of the key as "not entered", and a
            // gate that waited for all six would never open on this avatar at all.
            var digits = new[] { 4, 8, 1, 2, 0, 0 };
            var fixture = Build(lilToon, standard, digits, hide: true, seed: 16180);
            try
            {
                var fx = fixture.Fx();
                var layer = fx == null ? null
                    : fx.layers.FirstOrDefault(l => l.name == GateName(fixture.variant));
                Check(layer != null, "gate/short/layer-exists", "a short password gets a gate too");
                if (layer == null) return;

                var closed = layer.stateMachine.defaultState.motion as AnimationClip;
                var open = layer.stateMachine.states.Select(s => s.state.motion as AnimationClip)
                                .FirstOrDefault(c => c != null && c != closed);

                Check(IsOpen(layer, fixture.variant, Bits(fixture.variant, digits), open),
                      "gate/short/opens-with-the-spare-dials-untouched",
                      "four in, two left alone: that is this password, entered");

                Check(!IsOpen(layer, fixture.variant, Bits(fixture.variant, new[] { 4, 8, 1, 0, 0, 0 }), open),
                      "gate/short/closed-one-digit-short", "three of four");

                Check(!IsOpen(layer, fixture.variant, Bits(fixture.variant, new[] { 4, 8, 1, 2, 5, 0 }), open),
                      "gate/short/closed-once-a-spare-dial-is-touched",
                      "a touched spare dial can never be right, so there is nothing to show");

                // The 8 is the one digit whose pattern is the top bit alone - the bit a test
                // written around 1..7 never sets.
                Check(IsOpen(layer, fixture.variant, Bits(fixture.variant, new[] { 8, 8, 8, 8, 0, 0 }), open),
                      "gate/short/an-eight-counts-as-entered",
                      "digit 8 is bit 3 on its own; a gate that only looked at the low bits would " +
                      "read it as nothing entered");
            }
            finally { fixture.Dispose(); }
        }

        private static void SwitchedOff(Shader lilToon, Shader standard)
        {
            var fixture = Build(lilToon, standard, new[] { 1, 2, 3, 4, 5, 6 }, hide: false, seed: 14142);
            try
            {
                var fx = fixture.Fx();
                bool present = fx != null && fx.layers.Any(l => l.name == GateName(fixture.variant));
                Check(!present, "gate/off/no-layer", "the option is off, so nothing is added");

                Check(fixture.hat.enabled && fixture.sparks.enabled && fixture.mixed.enabled,
                      "gate/off/renderers-are-left-as-they-were",
                      "an author who wants something visible on a locked avatar gets exactly that");

                Check(fixture.report.hiddenWhileLocked == 0 && fixture.report.visibleWhileLocked >= 4,
                      "gate/off/report-says-what-will-show",
                      $"hidden {fixture.report.hiddenWhileLocked}, visible {fixture.report.visibleWhileLocked}");
            }
            finally { fixture.Dispose(); }
        }

        // ------------------------------------------------------------------ animator walking

        private static string GateName(MeshProtectVariant variant)
        {
            var method = typeof(MeshProtectAnimator).GetMethod(
                "GateLayerName", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) throw new Exception("MeshProtectAnimator.GateLayerName is gone");
            return (string)method.Invoke(null, new object[] { variant });
        }

        /// <summary>The transport bits a set of digits would leave: the digit itself, bit by bit.</summary>
        private static bool[] Bits(MeshProtectVariant variant, int[] digits)
        {
            var bits = new bool[variant.bitNames.Length];
            for (int position = 0; position < digits.Length; position++)
                for (int b = 0; b < MeshProtectRoot.BitsPerDigit; b++)
                    bits[position * MeshProtectRoot.BitsPerDigit + b] = (digits[position] & (1 << b)) != 0;
            return bits;
        }

        /// <summary>
        /// Follow satisfied transitions from a state until none fires. Null means the machine
        /// never settled, which on an avatar is a renderer flickering every frame.
        /// </summary>
        private static AnimatorState Walk(MeshProtectVariant variant, AnimatorState from,
                                          bool[] bits)
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < variant.bitNames.Length; i++) index[variant.bitNames[i]] = i;

            var state = from;
            for (int step = 0; step < 64; step++)
            {
                AnimatorState next = null;
                foreach (var transition in state.transitions)
                {
                    if (transition.destinationState == null || transition.conditions.Length == 0) continue;

                    bool fires = true;
                    foreach (var condition in transition.conditions)
                    {
                        if (!index.TryGetValue(condition.parameter, out int bit)) { fires = false; break; }
                        bool wants = condition.mode == AnimatorConditionMode.If;
                        if (bits[bit] != wants) { fires = false; break; }
                    }
                    if (fires) { next = transition.destinationState; break; }
                }

                if (next == null) return state;
                state = next;
            }
            return null;
        }

        private static bool IsOpen(AnimatorControllerLayer layer, MeshProtectVariant variant,
                                   bool[] bits, AnimationClip open)
        {
            var settled = Walk(variant, layer.stateMachine.defaultState, bits);
            if (settled == null) throw new Exception("the gate never settled - it would flicker");
            return settled.motion == open;
        }

        // ------------------------------------------------------------------ fixture

        private sealed class Fixture : IDisposable
        {
            public GameObject root;
            public MeshProtectVariant variant;
            public MeshProtectPipeline.Report report;
            public SkinnedMeshRenderer body, mixed, pairA, pairB;
            public Mesh pairBMesh;
            public MeshRenderer hat, toggled, alreadyOff, twinA, twinB;
            public ParticleSystemRenderer sparks;

            public AnimatorController Fx()
            {
                var descriptor = root.GetComponent<VRCAvatarDescriptor>();
                return descriptor.baseAnimationLayers
                    .First(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX)
                    .animatorController as AnimatorController;
            }

            public void Dispose()
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static Fixture Build(Shader lilToon, Shader standard, int[] digits, bool hide, int seed)
        {
            if (AssetDatabase.IsValidFolder(Scratch)) AssetDatabase.DeleteAsset(Scratch);
            AssetDatabase.CreateFolder("Assets", "_MPGateScratch");
            AssetDatabase.CreateFolder(Scratch, "build");

            var fixture = new Fixture { root = new GameObject("GateAvatar") };
            var root = fixture.root;
            root.AddComponent<Animator>();
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();

            var bone = new GameObject("Bone");
            bone.transform.SetParent(root.transform, false);

            Material Lil(string name) => new Material(lilToon) { name = name };
            Material Plain(string name) => new Material(standard) { name = name };

            // Fully protected: the decode hides it.
            fixture.body = Skinned(root, bone, "Body", 1, Lil("BodyMaterial"));

            // One slot protected, one on a shader this tool cannot carry.
            fixture.mixed = Skinned(root, bone, "Mixed", 2, Lil("MixedSkin"), Plain("MixedNails"));

            // Nothing protectable on it at all.
            fixture.hat = Static(root, "Hat", Plain("HatMaterial"));

            // The avatar's own FX layer switches this one on and off.
            fixture.toggled = Static(root, "Toggled", Plain("ToggledMaterial"));

            // Off in the scene, and nothing animates it.
            fixture.alreadyOff = Static(root, "AlreadyOff", Plain("OffMaterial"));
            fixture.alreadyOff.enabled = false;

            // Two siblings with one name: a path reaches only the first.
            fixture.twinA = Static(root, "Twin", Plain("TwinMaterialA"));
            fixture.twinB = Static(root, "Twin", Plain("TwinMaterialB"));

            // The same, but on a shader this tool does protect.
            fixture.pairA = Skinned(root, bone, "Pair", 1, Lil("PairMaterialA"));
            fixture.pairB = Skinned(root, bone, "Pair", 1, Lil("PairMaterialB"));
            fixture.pairBMesh = fixture.pairB.sharedMesh;

            var sparks = new GameObject("Sparks");
            sparks.transform.SetParent(root.transform, false);
            sparks.AddComponent<ParticleSystem>();
            fixture.sparks = sparks.GetComponent<ParticleSystemRenderer>();
            fixture.sparks.sharedMaterial = Plain("SparkMaterial");

            // An FX controller of the avatar's own, with the one toggle in it.
            var toggleClip = new AnimationClip { name = "ToggleOff" };
            AnimationUtility.SetEditorCurve(toggleClip,
                EditorCurveBinding.FloatCurve("Toggled", typeof(MeshRenderer), "m_Enabled"),
                new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f / 60f, 0f)));
            AssetDatabase.CreateAsset(toggleClip, Scratch + "/ToggleOff.anim");

            var fx = AnimatorController.CreateAnimatorControllerAtPath(Scratch + "/AvatarFX.controller");
            var onState = fx.layers[0].stateMachine.AddState("On");
            var offState = fx.layers[0].stateMachine.AddState("Off");
            offState.motion = toggleClip;
            fx.layers[0].stateMachine.defaultState = onState;

            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[]
            {
                Default(VRCAvatarDescriptor.AnimLayerType.Base),
                Default(VRCAvatarDescriptor.AnimLayerType.Additive),
                Default(VRCAvatarDescriptor.AnimLayerType.Gesture),
                Default(VRCAvatarDescriptor.AnimLayerType.Action),
                new VRCAvatarDescriptor.CustomAnimLayer
                {
                    type = VRCAvatarDescriptor.AnimLayerType.FX,
                    isDefault = false,
                    animatorController = fx
                }
            };

            var settings = root.AddComponent<MeshProtectRoot>();
            settings.outputFolder = Scratch;
            settings.hideUnprotectedWhileLocked = hide;
            settings.keyDigits = digits;
            settings.variant = MeshProtectVariantGenerator.Generate(new System.Random(seed));
            fixture.variant = settings.variant;

            if (MeshProtectShaderGen.EnsureGenerated(settings, settings.variant, out string folder))
                MeshProtectShaderGen.ImportGenerated(folder);

            fixture.report = new MeshProtectPipeline.Report();
            MeshProtectPipeline.Apply(root, settings, Scratch + "/build", fixture.report);
            AssetDatabase.SaveAssets();
            return fixture;
        }

        private static VRCAvatarDescriptor.CustomAnimLayer Default(VRCAvatarDescriptor.AnimLayerType type)
            => new VRCAvatarDescriptor.CustomAnimLayer { type = type, isDefault = true };

        private static SkinnedMeshRenderer Skinned(GameObject root, GameObject bone, string name,
                                                   int subMeshes, params Material[] materials)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();

            var mesh = Quad(name + "Mesh", subMeshes);
            mesh.boneWeights = Enumerable.Repeat(
                new BoneWeight { boneIndex0 = 0, weight0 = 1f }, mesh.vertexCount).ToArray();
            mesh.bindposes = new[] { Matrix4x4.identity };

            renderer.sharedMesh = mesh;
            renderer.bones = new[] { bone.transform };
            renderer.rootBone = bone.transform;
            renderer.sharedMaterials = materials;
            return renderer;
        }

        private static MeshRenderer Static(GameObject root, string name, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = Quad(name + "Mesh" + material.name, 1);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return renderer;
        }

        /// <summary>A mesh of its own for every renderer: a shared one would be a leak, and the
        /// build would rightly leave both renderers alone.</summary>
        private static Mesh Quad(string name, int subMeshes)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[]
            {
                Vector3.zero, Vector3.right, Vector3.forward, new Vector3(1, 0, 1),
                new Vector3(2, 0, 0), new Vector3(2, 0, 1)
            };
            mesh.uv = new[]
            {
                new Vector2(0.11f, 0.13f), new Vector2(0.71f, 0.17f), new Vector2(0.19f, 0.83f),
                new Vector2(0.67f, 0.79f), new Vector2(0.93f, 0.23f), new Vector2(0.97f, 0.89f)
            };
            mesh.normals = Enumerable.Repeat(Vector3.up, 6).ToArray();
            mesh.subMeshCount = subMeshes;
            if (subMeshes == 1)
            {
                mesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3, 1, 3, 4, 4, 3, 5 }, 0);
            }
            else
            {
                mesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3 }, 0);
                mesh.SetTriangles(new[] { 1, 3, 4, 4, 3, 5 }, 1);
            }
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
#endif
