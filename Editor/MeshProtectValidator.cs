#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if LILMP_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
#endif

namespace MeshProtect
{
    /// <summary>
    /// Pre-upload self check. Everything here guards the same rule: the correct key must not be
    /// reconstructible from anything inside the package.
    /// </summary>
    public static class MeshProtectValidator
    {
        public class Issue
        {
            public string message;
            public bool fatal;
            public Issue(string message, bool fatal) { this.message = message; this.fatal = fatal; }
        }

        /// <summary>
        /// Check a built avatar against the variant it was built with.
        ///
        /// The variant used to be recovered from the avatar itself, by looking for a fixed prefix
        /// on the generated names. That worked, and it forced every uploaded avatar to carry that
        /// prefix - which is precisely the signature a scanner matches on. Passing the variant in
        /// costs one argument and lets the names be arbitrary.
        /// </summary>
        public static List<Issue> Validate(GameObject avatar, MeshProtectVariant variant)
        {
            var issues = new List<Issue>();
            if (avatar == null)
            {
                issues.Add(new Issue("No avatar selected.", true));
                return issues;
            }

            // A warning, and it was fatal, which was this tool refusing an upload over something
            // that is none of its business. An inactive root does not weaken the protection or
            // break the avatar - renderers are collected with includeInactive, so every mesh was
            // found and baked exactly as it would have been. Whether an avatar should be uploaded
            // with its root switched off is between the author and the VRChat SDK.
            if (!avatar.activeSelf)
            {
                issues.Add(new Issue(
                    $"Avatar root '{avatar.name}' is inactive, so it is invisible in the scene. The " +
                    "protection is unaffected - every mesh was found and baked either way - but " +
                    "check this is what you meant before uploading.", false));
            }

            // 1. The authoring component serialises the key. It must not be on an upload.
            foreach (var root in avatar.GetComponentsInChildren<MeshProtectRoot>(true))
            {
                issues.Add(new Issue(
                    $"'{root.name}' still has a MeshProtectRoot component. It stores the key and would " +
                    "be uploaded with the avatar. The build strips it by itself, so reaching this " +
                    "means something put it back after protection ran.", true));
            }

            if (variant == null || !variant.IsValid(MeshProtectRoot.PasswordLength))
            {
                issues.Add(new Issue("The protection variant is missing or malformed.", true));
                return issues;
            }

            var renderers = avatar.GetComponentsInChildren<Renderer>(true);
            var protectedMaterials = renderers
                .SelectMany(r => r.sharedMaterials)
                .Where(m => MeshProtectPipeline.IsProtectShader(m, variant.shaderName))
                .Distinct()
                .ToList();

            if (protectedMaterials.Count == 0)
            {
                issues.Add(new Issue("No MeshProtect materials found on this avatar.", true));
                return issues;
            }

            // 2. The shipped material must sit on a wrong combination, must not be bypassed, and
            //    must carry the per-avatar constants the bake wrote.
            foreach (var material in protectedMaterials)
            {
                var live = new List<int>();
                for (int i = 0; i < variant.digitProperties.Length; i++)
                {
                    string property = variant.digitProperties[i];
                    if (!material.HasProperty(property)) continue;
                    if (Mathf.Abs(material.GetFloat(property)) > 0.0001f) live.Add(i + 1);
                }

                if (live.Count > 0)
                {
                    issues.Add(new Issue(
                        $"Material '{material.name}' ships with digit {string.Join(", ", live)} " +
                        "already set. That is part of the answer. The build clears these by itself, " +
                        "so reaching this means the material was rebuilt after the bake.", true));
                }

                // A bypassed material renders correctly without any key at all. An excluded
                // material never gets moved onto this shader, so bypass here always means a mistake.
                if (material.HasProperty(variant.bypassProperty) &&
                    material.GetFloat(variant.bypassProperty) > 0.5f)
                {
                    issues.Add(new Issue(
                        $"Material '{material.name}' has Bypass enabled, so its mesh renders correctly " +
                        "with no key. It is completely unprotected.", true));
                }

                // There used to be a check here that the material's family name equalled the
                // variant's. It has been removed, and both halves of why are worth keeping.
                //
                // It was already dead: protectedMaterials is filtered by IsProtectShader, which
                // asked exactly that question, so the comparison could never fail. And once a
                // decode could be grafted onto a shader that is not lilToon, it stopped being
                // merely dead and became wrong - a graft ships under its own family name, so
                // every Poiyomi material on the avatar would have failed it, fatally, and no
                // avatar using one could be uploaded at all.
                //
                // Several families on one avatar is now NORMAL - lilToon on the body, a graft on
                // an outfit - and that is safe because they are all this variant's, carrying the
                // same emitted decode. "All the same VARIANT" is the property that matters, and
                // IsProtectShader is where it is enforced.

                // What is worth checking, and is not checked anywhere else: the material can
                // actually receive the password. The digits arrive as animated material
                // properties, so a material missing one of them stays on its shipped zero for
                // that position and the avatar never opens - and every other check here passes,
                // because nothing about it is wrong except a name that is not there.
                //
                // Reachable through the graft: the properties are added by patching the host's
                // Properties block, and a host whose block was formatted in a way the patch read
                // differently would come out short.
                var missing = variant.digitProperties.Where(p => !material.HasProperty(p)).ToList();
                if (missing.Count > 0)
                {
                    issues.Add(new Issue(
                        $"Material '{material.name}' is missing {missing.Count} of the " +
                        $"{variant.digitProperties.Length} digit properties its shader must expose " +
                        $"({string.Join(", ", missing)}). The unlock menu writes those, so no " +
                        "password can open this material. Press 'Rebuild Shader' and re-bake.", true));
                }
            }

            // 2a. The key verifier. The shader compares the entered password's hash against this
            //     vector; if it was never written it sits at the shader's default and NO password
            //     matches - the avatar ships permanently invisible, and every other check here
            //     passes because nothing else is wrong. What can get here: a material that did
            //     not come from ConvertMaterial - copied by another tool after the bake, or built
            //     by hand - and a future rename that lands in EmitProperties but not
            //     EmitCustomHlsl, or the reverse. Both leave a material on a shader that reads a
            //     property nobody wrote. An earlier version of this comment blamed a stale shader
            //     folder left behind by changing outputFolder; that is not a cause. The family in
            //     it came from the same variant, so it declares the same name and the same
            //     properties, and the two copies are identical.
            var verifiers = protectedMaterials
                .Where(m => m.HasProperty(variant.macProperty))
                .Select(m => m.GetVector(variant.macProperty))
                .ToList();

            if (verifiers.Count != protectedMaterials.Count)
            {
                issues.Add(new Issue(
                    $"Only {verifiers.Count} of {protectedMaterials.Count} protected material(s) have " +
                    $"the key verifier property '{variant.macProperty}'. The others are on a shader " +
                    "that does not belong to this protection, so no password can open them. Re-bake " +
                    "the avatar.", true));
            }
            else if (verifiers.Any(v => v.Equals(Vector4.zero)))
            {
                issues.Add(new Issue(
                    "A protected material carries an all-zero key verifier, which no password can " +
                    "match - the avatar would ship invisible to everyone including you. This usually " +
                    "means the material did not come through this tool's conversion - " +
                    "something copied or rebuilt it after the bake. Re-bake the avatar.", true));
            }
            else if (verifiers.Distinct().Count() > 1)
            {
                issues.Add(new Issue(
                    $"Protected materials carry {verifiers.Distinct().Count()} different key " +
                    "verifiers. They were baked against different passwords, so no single password " +
                    "opens the whole avatar. Re-bake it.", true));
            }

            // 2b. The mode is a single value shared by every material. Two different values means
            //     some mesh is decoded with the formula it was not baked with.
            var modes = protectedMaterials.Where(m => m.HasProperty(variant.modeProperty))
                                          .Select(m => m.GetFloat(variant.modeProperty) > 0.5f)
                                          .Distinct().ToList();
            if (modes.Count > 1)
            {
                issues.Add(new Issue(
                    "Protected materials disagree about the displacement mode. Every material must " +
                    "use the same displacement mode; re-bake the avatar.", true));
            }
            bool tangentSpace = modes.Count == 1 && modes[0];

            // 3. Baked meshes should carry displacement factors in both channels, and must be able
            //    to supply whatever basis the mode decodes against.
            foreach (var renderer in renderers)
            {
                if (!renderer.sharedMaterials.Any(
                        m => MeshProtectPipeline.IsProtectShader(m, variant.shaderName))) continue;

                var skinned = renderer as SkinnedMeshRenderer;
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = skinned != null ? skinned.sharedMesh : filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;

                if (!mesh.isReadable)
                {
                    issues.Add(new Issue(
                        $"Mesh '{mesh.name}' on '{renderer.name}' is not readable, so it cannot be " +
                        "checked. Baked meshes are readable; this one probably is not the baked copy.",
                        false));
                    continue;
                }

                var uv6 = new List<Vector2>();
                mesh.GetUVs(6, uv6);

                // The displacement itself is generated from the key and stored nowhere; TEXCOORD6.x
                // only carries the per-vertex amplitude. All-zero therefore means "nothing on this
                // mesh was ever displaced".
                bool noAmplitude = uv6.Count == 0 || uv6.All(v => !(v.x > 0f));

                if (noAmplitude)
                {
                    // A warning, and it was fatal. What this describes is a mesh wearing the decode
                    // shader that nothing displaced - which is unprotected, not broken: the decode
                    // skips a vertex whose amplitude is zero, so the mesh renders correctly once
                    // the password is in, and stays hidden until then like everything else on that
                    // shader. Refusing the upload over it stops somebody shipping an avatar that
                    // works, to save them from a piece of it not being protected, which is the
                    // trade this tool is not supposed to make.
                    //
                    // It is still worth saying loudly. The pipeline should not produce it any more
                    // - a renderer wearing the shader was baked, or it was left alone with its own
                    // materials - so if it appears, either this tool has a bug or joint attenuation
                    // scaled every vertex on a small mesh down to nothing.
                    issues.Add(new Issue(
                        $"Mesh '{mesh.name}' on '{renderer.name}' has no displacement amplitude in " +
                        "TEXCOORD6 at all. It wears the protect shader but nothing displaced it, so " +
                        "it ships unprotected - it will still render correctly, and still be hidden " +
                        "until the password is entered. If this avatar has Attenuate At Joints on, " +
                        "try it off; otherwise please report it.", false));
                }
                else if (uv6.Count != mesh.vertexCount)
                {
                    issues.Add(new Issue(
                        $"Mesh '{mesh.name}' has {uv6.Count} amplitude entries for " +
                        $"{mesh.vertexCount} vertices. The mesh and its channels are out of sync.", true));
                }

                // Vertex identity comes from UV0's raw bits, so a protected mesh without UV0 can
                // never decode.
                var meshUv0 = mesh.uv;
                if (meshUv0 == null || meshUv0.Length != mesh.vertexCount)
                {
                    issues.Add(new Issue(
                        $"Mesh '{mesh.name}' on '{renderer.name}' has no UV0. Vertex identity is " +
                        "derived from it, so this mesh cannot be decoded.", true));
                }

                if (tangentSpace)
                {
                    var tangents = mesh.tangents;
                    if (tangents == null || tangents.Length != mesh.vertexCount)
                    {
                        issues.Add(new Issue(
                            $"Mesh '{mesh.name}' has no tangents, but its material decodes in tangent " +
                            "space. The restore will be wrong. Re-bake with Recalculate Missing " +
                            "Tangents enabled, or use Normal mode.", true));
                    }
                }
            }

#if LILMP_VRCSDK3_AVATARS
            ValidateVRChat(avatar, variant, issues);
#endif
            return issues;
        }

#if LILMP_VRCSDK3_AVATARS
        private static void ValidateVRChat(GameObject avatar, MeshProtectVariant variant,
                                           List<Issue> issues)
        {
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                issues.Add(new Issue("No VRCAvatarDescriptor found.", true));
                return;
            }

            // The names are arbitrary now, so membership is a set lookup rather than a prefix test.
            var menuNames = new HashSet<string>(variant.parameterNames);
            var bitSet = new HashSet<string>(variant.bitNames);

            var parameters = descriptor.expressionParameters;
            if (parameters == null || parameters.parameters == null)
            {
                issues.Add(new Issue("The avatar has no expression parameters, so the key cannot be entered.", true));
            }
            else
            {
                // Two kinds, with opposite requirements. The menu Ints are local input and must
                // stay unsynced or they waste 48 bits of budget for nothing. The bools are the
                // transport and must sync, or every remote player sees a scrambled avatar.
                var menuParams = parameters.parameters
                    .Where(p => p != null && menuNames.Contains(p.name)).ToList();
                var bitParams = parameters.parameters
                    .Where(p => p != null && bitSet.Contains(p.name)).ToList();

                if (menuParams.Count != MeshProtectRoot.PasswordLength)
                {
                    issues.Add(new Issue(
                        $"Expected {MeshProtectRoot.PasswordLength} menu parameters but found " +
                        $"{menuParams.Count}, so the password cannot be entered.", true));
                }

                int expectedBits = MeshProtectRoot.PasswordLength * MeshProtectRoot.BitsPerDigit;
                if (bitParams.Count != expectedBits)
                {
                    issues.Add(new Issue(
                        $"Expected {expectedBits} synced password bits but found {bitParams.Count}. " +
                        "The mesh can never be restored.", true));
                }

                foreach (var p in menuParams)
                {
                    if (p.valueType != VRCExpressionParameters.ValueType.Int)
                        issues.Add(new Issue($"Parameter '{p.name}' is {p.valueType}, not Int. " +
                                             "The digit menu will not drive it.", true));
                    if (Mathf.Abs(p.defaultValue) > 0.0001f)
                        issues.Add(new Issue($"Parameter '{p.name}' has a non-zero default value. " +
                                             "Zero is the required locked value.", true));
                    if (p.networkSynced)
                        issues.Add(new Issue($"Parameter '{p.name}' is synced. It is local menu input; " +
                                             "syncing it spends 8 bits of budget for nothing.", false));
                }

                foreach (var p in bitParams)
                {
                    if (p.valueType != VRCExpressionParameters.ValueType.Bool)
                        issues.Add(new Issue($"Parameter '{p.name}' is {p.valueType}, not Bool. " +
                                             "The decode transitions will not fire.", true));
                    if (Mathf.Abs(p.defaultValue) > 0.0001f)
                        issues.Add(new Issue($"Parameter '{p.name}' defaults to true. That is part of " +
                                             "the answer; every bit must ship false.", true));
                    if (!p.saved)
                        issues.Add(new Issue($"Parameter '{p.name}' is not Saved, so the password would " +
                                             "have to be re-entered every session.", false));
                    if (!p.networkSynced)
                        issues.Add(new Issue($"Parameter '{p.name}' is not synced, so other players would " +
                                             "see the mesh scrambled.", true));
                }
            }

            // 4. The key properties must be animated. An unanimated material property is a
            //    constant as far as any downstream optimiser is concerned.
            var clips = CollectClips(descriptor);
            var animated = new HashSet<int>();
            bool invalidKeyBinding = false;
            foreach (var clip in clips)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (TryParseKeyBinding(binding, variant, out int bit)) animated.Add(bit);
                    else if (binding.propertyName != null &&
                             variant.digitProperties.Any(d => binding.propertyName.EndsWith(d)))
                        invalidKeyBinding = true;
                }
            }

            if (invalidKeyBinding)
            {
                issues.Add(new Issue(
                    "One or more digit curves use an invalid material binding. Material curves " +
                    "must target the Renderer and use the material.<property> form. " +
                    "Bake again with the current Mesh Protect package.", true));
            }

            if (animated.Count == 0)
            {
                issues.Add(new Issue(
                    "No animation clip drives any digit property. The password can never be entered, " +
                    "and shader optimisers are free to fold the properties into constants.", true));
            }
            else
            {
                ValidateBindings(avatar, descriptor, parameters, variant, clips, issues);
            }

            if (!descriptor.customExpressions)
            {
                issues.Add(new Issue(
                    "Customize Expressions is off on the avatar descriptor, so the generated menu and " +
                    "parameters are ignored and the key cannot be entered.", true));
            }

            foreach (var layer in descriptor.baseAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
            {
                if (layer.type != VRCAvatarDescriptor.AnimLayerType.FX) continue;
                if (layer.isDefault || layer.animatorController == null)
                    issues.Add(new Issue(
                        "The FX playable layer is set to default or has no controller, so the key " +
                        "layers are not loaded.", true));
            }

            // The FX controller must agree with the expression parameters on type.
            foreach (var controller in CollectFXControllers(descriptor))
            {
                foreach (var p in controller.parameters.Where(
                             p => menuNames.Contains(p.name) || bitSet.Contains(p.name)))
                {
                    bool isBit = bitSet.Contains(p.name);
                    var expected = isBit ? AnimatorControllerParameterType.Bool
                                         : AnimatorControllerParameterType.Int;
                    if (p.type != expected)
                        issues.Add(new Issue(
                            $"FX parameter '{p.name}' is {p.type}, not {expected}. The digit chain will " +
                            "not reach the material.", true));
                }
            }

            var ourLayers = new HashSet<string>(variant.packLayerNames.Concat(variant.decodeLayerNames));

            var defaultStates = new List<string>();
            foreach (var layer in CollectFXLayers(descriptor))
            {
                if (layer.stateMachine == null || layer.stateMachine.defaultState == null) continue;
                if (!ourLayers.Contains(layer.name)) continue;
                var motion = layer.stateMachine.defaultState.motion as AnimationClip;
                if (motion == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(motion))
                {
                    if (!TryParseKeyBinding(binding, variant, out _)) continue;
                    var curve = AnimationUtility.GetEditorCurve(motion, binding);
                    if (curve == null || curve.length == 0) continue;

                    // A fresh avatar must start locked, and locked is zero: the four-bit digit
                    // encoding reserves pattern 0 for "never entered", which is what the shader
                    // collapses on. A default state writing anything else would both leak a digit
                    // and make the avatar render its scrambled mesh.
                    if (Mathf.Abs(curve.Evaluate(0f)) > 0.0001f)
                        defaultStates.Add(layer.name);
                }
            }

            foreach (var name in defaultStates.Distinct())
            {
                issues.Add(new Issue(
                    $"FX layer '{name}' does not start locked. " +
                    "The default state must always carry the locked value.", true));
            }
        }

        /// <summary>
        /// Counting property names is not enough: a renamed hierarchy leaves every binding pointing
        /// at nothing, and a mismatched set (declare 0 and 1, animate 0 and 9) still counts equal.
        /// Check that every protected renderer has all 24 shader-bit bindings and that every one
        /// of the six Int parameters has menu choices 1 through 8.
        /// </summary>
        private static void ValidateBindings(GameObject avatar, VRCAvatarDescriptor descriptor,
                                             VRCExpressionParameters parameters,
                                             MeshProtectVariant variant,
                                             List<AnimationClip> clips, List<Issue> issues)
        {
            var declaredDigits = new HashSet<int>();
            for (int i = 0; i < variant.parameterNames.Length; i++)
            {
                string want = variant.parameterNames[i];
                if (parameters?.parameters != null &&
                    parameters.parameters.Any(p => p != null && p.name == want))
                    declaredDigits.Add(i);
            }

            var expectedDigits = new HashSet<int>(
                Enumerable.Range(0, MeshProtectRoot.PasswordLength));
            foreach (int digit in expectedDigits.Except(declaredDigits))
                issues.Add(new Issue(
                    $"Password parameter {digit + 1} ('{variant.parameterNames[digit]}') is missing.",
                    true));
            foreach (int digit in declaredDigits.Except(expectedDigits))
                issues.Add(new Issue(
                    $"Unexpected password parameter index {digit}.", true));

            // bit -> set of renderer paths it actually reaches
            var reached = new Dictionary<int, HashSet<string>>();
            foreach (var clip in clips)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (!TryParseKeyBinding(binding, variant, out int index)) continue;

                    var target = string.IsNullOrEmpty(binding.path)
                        ? avatar.transform
                        : avatar.transform.Find(binding.path);

                    if (target == null)
                    {
                        issues.Add(new Issue(
                            $"An animation binding for digit {index + 1} points at '{binding.path}', " +
                            "which does not exist on this avatar. Renaming or moving a renderer " +
                            "breaks the password.", true));
                        continue;
                    }

                    if (target.GetComponent(binding.type) == null)
                    {
                        issues.Add(new Issue(
                            $"An animation binding for digit {index + 1} targets a {binding.type.Name} on " +
                            $"'{binding.path}', which has no such component.", true));
                        continue;
                    }

                    if (!reached.TryGetValue(index, out var set))
                        reached[index] = set = new HashSet<string>();
                    set.Add(binding.path);
                }
            }

            var animatedBits = new HashSet<int>(reached.Keys);
            var expectedBits = new HashSet<int>(Enumerable.Range(0, MeshProtectRoot.PasswordLength));
            foreach (int bit in expectedBits.Except(animatedBits))
                issues.Add(new Issue(
                    $"No digit animation drives digit {bit + 1}. The password cannot be decoded.", true));
            foreach (int bit in animatedBits.Except(expectedBits))
                issues.Add(new Issue(
                    $"Unexpected digit {bit + 1} animation exists outside the six-digit password.", true));

            // Mecanim's Renderer material curve is a renderer-wide property block, so one path
            // deliberately covers every protected material slot on that Renderer.
            var protectedPaths = avatar.GetComponentsInChildren<Renderer>(true)
                .Where(r => r.sharedMaterials.Any(
                    m => MeshProtectPipeline.IsProtectShader(m, variant.shaderName)))
                .Select(r => AnimationUtility.CalculateTransformPath(r.transform, avatar.transform))
                .Distinct()
                .ToList();

            foreach (int bit in expectedBits.Intersect(animatedBits))
            {
                var missing = protectedPaths.Where(p => !reached[bit].Contains(p)).ToList();
                if (missing.Count == 0) continue;
                issues.Add(new Issue(
                    $"Digit {bit + 1} does not reach {missing.Count} protected renderer(s) " +
                    $"(e.g. '{missing[0]}'). Those meshes can never be restored.", true));
            }

            var menuValues = new Dictionary<string, HashSet<int>>();
            CollectMenuValues(descriptor.expressionsMenu, menuValues,
                              new HashSet<VRCExpressionsMenu>());
            foreach (int digitIndex in expectedDigits)
            {
                string parameter = variant.parameterNames[digitIndex];
                menuValues.TryGetValue(parameter, out var values);
                var missing = Enumerable.Range(MeshProtectRoot.MinDigit,
                                               MeshProtectRoot.MaxDigit - MeshProtectRoot.MinDigit + 1)
                                        .Where(v => values == null || !values.Contains(v)).ToList();
                if (missing.Count > 0)
                    issues.Add(new Issue(
                        $"Menu choices for {parameter} are incomplete; missing: " +
                        string.Join(", ", missing) + ".", true));
            }

        }

        private static bool TryParseKeyBinding(EditorCurveBinding binding,
                                               MeshProtectVariant variant, out int keyIndex)
        {
            keyIndex = -1;
            if (binding.type == null || !typeof(Renderer).IsAssignableFrom(binding.type) ||
                string.IsNullOrEmpty(binding.propertyName))
                return false;

            for (int i = 0; i < variant.digitProperties.Length; i++)
            {
                if (binding.propertyName != "material." + variant.digitProperties[i]) continue;
                keyIndex = i;
                return true;
            }
            return false;
        }

        private static void CollectMenuValues(VRCExpressionsMenu menu,
                                              Dictionary<string, HashSet<int>> into,
                                              HashSet<VRCExpressionsMenu> seen)
        {
            if (menu == null || !seen.Add(menu)) return;
            foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>())
            {
                if (control == null) continue;
                if (!string.IsNullOrEmpty(control.parameter?.name))
                {
                    if (!into.TryGetValue(control.parameter.name, out var values))
                        into[control.parameter.name] = values = new HashSet<int>();
                    values.Add(Mathf.RoundToInt(control.value));
                }
                CollectMenuValues(control.subMenu, into, seen);
            }
        }

        private static List<AnimationClip> CollectClips(VRCAvatarDescriptor descriptor)
        {
            var clips = new List<AnimationClip>();
            foreach (var layer in descriptor.baseAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
                if (layer.animatorController != null) clips.AddRange(layer.animatorController.animationClips);
            foreach (var layer in descriptor.specialAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
                if (layer.animatorController != null) clips.AddRange(layer.animatorController.animationClips);
            return clips.Where(c => c != null).Distinct().ToList();
        }

        private static IEnumerable<UnityEditor.Animations.AnimatorController> CollectFXControllers(
            VRCAvatarDescriptor descriptor)
        {
            foreach (var layer in descriptor.baseAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
            {
                var controller = layer.animatorController as UnityEditor.Animations.AnimatorController;
                if (controller != null) yield return controller;
            }
        }

        private static IEnumerable<UnityEditor.Animations.AnimatorControllerLayer> CollectFXLayers(
            VRCAvatarDescriptor descriptor)
        {
            foreach (var layer in descriptor.baseAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
            {
                var controller = layer.animatorController as UnityEditor.Animations.AnimatorController;
                if (controller == null) continue;
                foreach (var l in controller.layers) yield return l;
            }
        }
#endif

        /// <summary>Force every key property on the avatar back to the locked state.</summary>
        public static int ClearKeys(GameObject avatar, MeshProtectVariant variant)
        {
            int cleared = 0;
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (!MeshProtectPipeline.IsProtectShader(material, variant.shaderName)) continue;
                    bool dirty = false;

                    foreach (var property in variant.digitProperties)
                    {
                        if (!material.HasProperty(property)) continue;
                        if (Mathf.Abs(material.GetFloat(property)) <= 0.0001f) continue;
                        material.SetFloat(property, 0f);
                        dirty = true;
                    }
                    if (dirty) { EditorUtility.SetDirty(material); cleared++; }
                }
            }
            if (cleared > 0) AssetDatabase.SaveAssets();
            return cleared;
        }
    }
}
#endif
