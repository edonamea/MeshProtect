#if UNITY_EDITOR && LILMP_VRCSDK3_AVATARS
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace MeshProtect
{
    /// <summary>
    /// Builds the unlock chain: a digit menu, the parameters behind it, and the FX layers that
    /// turn a typed password into the material properties the shader reads.
    ///
    /// The parameter encoding is the interesting part. A password is 18 bits, but one synced Int
    /// per digit costs 48 of VRChat's 256 - and a real dressed-up avatar tested during development
    /// already spent 218, so 48 did not fit and the build was refused. Unsynced parameters were
    /// measured to cost nothing, so the split is:
    ///
    ///   menu -> six UNSYNCED Ints (0 bits)  -> parameter driver -> 24 SYNCED bools (24 bits)
    ///           -> FX layers -> material properties -> shader
    ///
    /// 24 bits, four per digit. Three would be the floor and is what this used to spend, but it
    /// left no pattern meaning "not entered" - which is both the state a fresh avatar is in and,
    /// for a password shorter than six digits, the correct answer for the positions the author left
    /// out. The bools are what remote clients receive, so the material has to be driven from them
    /// and not from the local Ints - a remote player never sees those.
    ///
    /// There is deliberately no "write the answer into the local save file" feature. That only
    /// works on the machine running Unity, so it does nothing for someone you hand an avatar to;
    /// the menu is the mechanism that actually reaches them, and Saved=true means they only use it
    /// once.
    /// </summary>
    public static class MeshProtectAnimator
    {
        private const int MenuControlLimit = 8;   // VRCExpressionsMenu.MAX_CONTROLS

        /// <summary>
        /// The one label the owner actually reads, so it says what it does rather than naming the
        /// tool. Everything else generated into the avatar is named off the per-avatar family.
        ///
        /// No emoji, for two reasons that arrived together. A lock glyph came back from in game as
        /// a tofu box: VRChat's menu font draws nothing above the basic multilingual plane, so
        /// U+1F512 has no glyph, while a U+21BA a menu control carried at the time rendered fine. And it was the
        /// one string every avatar this tool produces had in common, sitting in plain sight in the
        /// menu, while every asset name around it is generated per avatar precisely so that nothing
        /// can be recognised by name.
        /// </summary>
        private const string RootControlName = "Unlock";

        /// <summary>
        /// The label on the overflow page made when the root menu is already full. Deliberately the
        /// same word every other menu tool uses for this, because a wearer who has seen one knows
        /// what it is.
        /// </summary>
        private const string OverflowControlName = "More";

        /// <summary>
        /// Could the unlock chain be built on this avatar at all, and if not, why?
        ///
        /// Asked BEFORE a single vertex is displaced. Generate runs after the meshes are baked, so
        /// everything it refuses on refuses too late: the avatar is already scrambled by then and
        /// stopping the upload is the only safe answer left. That turned a full expression
        /// parameter budget - an ordinary state for a heavily modded avatar - into "you cannot
        /// upload this avatar at all", with the fix being to go and free up bits somewhere.
        ///
        /// Answered here, the same avatar uploads exactly as it is, unprotected, with the reason in
        /// the Console. The author decides whether to free up the budget; they are not held
        /// hostage to it.
        ///
        /// It does not cover everything Generate can refuse on - a controller another tool built in
        /// memory cannot be predicted without trying to copy it - and it is not supposed to. It
        /// covers the states an avatar can simply BE in.
        /// </summary>
        public static bool CanGenerate(GameObject avatar, MeshProtectRoot settings, out string why)
        {
            why = null;

            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                why = "it has no VRChat avatar descriptor";
                return false;
            }

            var existing = FindFXLayer(descriptor);
            if (existing != null && !(existing is AnimatorController) &&
                UnderlyingController(existing) == null)
            {
                why = $"its FX playable layer holds a {existing.GetType().Name} rather than an " +
                      "Animator Controller, and the unlock layers can only go into a controller";
                return false;
            }

            // The names this build is about to claim. A clash means something else in the project
            // already owns one of them with a different type, and Generate refuses over it - after
            // the meshes are baked, which is too late to do anything but stop the upload. Asked
            // here it costs the author their obfuscation on this upload and nothing else, and the
            // fix it names (roll a new password, which redraws every generated name) is one click.
            var fxController = existing as AnimatorController;
            if (fxController != null || descriptor.expressionParameters != null)
            {
                var clash = FirstNameClash(descriptor.expressionParameters, fxController,
                                           settings.variant.parameterNames,
                                           settings.variant.bitNames);
                if (clash != null)
                {
                    why = clash + ". These names are generated for this avatar, so pressing " +
                          "'New Protection' on the component draws a fresh set";
                    return false;
                }
            }

            // The transport costs four synced bits per digit. Asked against the avatar's own list,
            // because that is what the budget is spent on.
            //
            // UNLESS VRCFURY IS ON THIS AVATAR, and that exception is not a courtesy - reading the
            // budget here is simply reading it too early. VRCFury's parameter compressor is its own
            // SDK preprocessor at int.MaxValue - 100, which is after everything including this, and
            // its AllowTooManyParametersHook patches out the SDK's own "too many parameters" error
            // for exactly that reason: on a VRCFury avatar, being over the limit in the middle of a
            // build is the normal state, and the compressor makes it fit at the end.
            //
            // So refusing here would give up the protection on precisely the avatars that were
            // about to have room made for them. It cannot be predicted how much the compressor will
            // free - that depends on how many of the author's own parameters are menu-driven - so
            // the honest thing is to stop guessing and let the pipeline finish.
            //
            // Our own 24 bits are NOT what gets compressed, and it is worth writing down why so
            // nobody expects it: ParameterCompressorSolverService only accepts a parameter that a
            // MENU CONTROL drives ("if (!usedInMenu.Contains(param.name)) continue;"). The menu
            // here drives six unsynced Ints; the synced bools behind them are written by parameter
            // drivers, so they are never candidates. Read from VRCFury's source, not measured.
            int needed = MeshProtectRoot.PasswordLength * MeshProtectRoot.BitsPerDigit;
            int spent = descriptor.expressionParameters == null
                ? 0 : descriptor.expressionParameters.CalcTotalCost();

            // Two different questions, depending on whether anything runs after this build.
            //
            // With a compressor: what will still be spent once it has run, which is the cost of the
            // synced parameters it CANNOT take - the ones no menu control drives. Ours are always in
            // that group, because the menu drives six UNSYNCED Ints and parameter drivers write the
            // synced bools.
            //
            // Without one: what is spent now, plus ours. Nothing is going to change it.
            int floor = CompressorWillRun()
                ? SmallestCostVrcFuryCanReach(descriptor, needed)
                : spent + needed;

            // This figure is SAID. It is never obeyed, whichever branch produced it.
            //
            // The rule is the author's, and it is this: an avatar that finishes a build must have
            // the protection on it. This tool does not get to quietly decide the password is not
            // worth 24 bits and hand back an avatar that looks finished and is not. That path
            // produced the worst outcome this tool has ever had - the author frees some
            // parameters, VRCFury is satisfied and says nothing, the SDK is satisfied, the upload
            // succeeds, and the avatar has no protection on it, because we gave up before any of
            // that ran and nothing afterwards had a reason to mention it. Protection that opts
            // itself out silently is worse than none, because the author stops checking.
            //
            // With VRCFury, the decision belongs to its compressor: it re-reads the descriptor
            // after us (ParamsService.ClearCache), so it sees these bits, fits itself around them,
            // and refuses the upload with two real numbers if even its tightest attempt is over.
            //
            // Without one, nothing downstream checks at all - the SDK's only budget test is
            // OnGUIAvatarCheck in the control panel, which runs on the SCENE avatar before the
            // build, so it never sees a parameter a preprocessor added. That was the argument for
            // refusing here, and it was overruled deliberately: refusing costs the protection every
            // time, while going ahead costs it only if VRChat turns out to drop parameters over the
            // cap - which nothing here can test. If an over-budget upload does come back broken,
            // this is the comment to come back to.
            if (floor > VRCExpressionParameters.MAX_PARAMETER_COST)
            {
                int over = floor - VRCExpressionParameters.MAX_PARAMETER_COST;
                Debug.LogWarning(
                    "[MeshProtect] EXPRESSION PARAMETERS ARE OVER BUDGET and the protection is " +
                    $"being applied anyway. This avatar looks like it lands at about {floor} of " +
                    $"{VRCExpressionParameters.MAX_PARAMETER_COST} bits with the password's " +
                    $"{needed} included - about {over} over. " +
                    (CompressorWillRun()
                        ? "That figure is this tool running VRCFury's own cost formula; VRCFury's " +
                          "compressor runs after this build, it can see these bits, and it is the " +
                          "one that decides. If it stops the upload, its numbers are the real ones."
                        : "Nothing in this project compresses parameters, so this figure is final " +
                          "and no later check will catch it - the SDK tests the budget in the " +
                          "control panel, against the avatar in the scene, before the build. If " +
                          "the uploaded avatar will not unlock, this is the first thing to suspect.") +
                    $" To make room, free about {over} bits of synced parameters that NO menu " +
                    "control drives - one synced Int or Float is 8 bits, one synced Bool is 1. " +
                    "Deleting menu-driven ones changes little: with a compressor those were " +
                    "already being taken care of.");
            }

            return true;
        }

        /// <summary>
        /// The smallest total VRCFury's compressor could bring this avatar to, with our bits added.
        ///
        /// Not an allowance and not a guess - it is VRCFury's own arithmetic, from
        /// OptimizationDecision.GetFinalCost:
        ///
        ///     finalCost = originalCost + indexBits + numberSlots*8 + boolSlots - compressed
        ///
        /// evaluated at ONE slot of each type, which is the tightest it ever gets.
        ///
        /// And in the only case this function is ever consulted, that is not a lower bound - it is
        /// the answer. Optimize() spends leftover headroom on extra slots to cut the sync latency,
        /// but it only expands while the result still fits; an avatar that is over the limit has no
        /// headroom to spend, so the compressor stays at one slot per type. So on exactly the
        /// avatars where this decision matters, this arithmetic is what VRCFury will land on.
        ///
        /// Only a menu-driven synced parameter can be compressed at all
        /// ("if (!usedInMenu.Contains(param.name)) continue;"), so everything else - ours included,
        /// since the menu drives unsynced Ints and parameter drivers write the synced bools - stays
        /// exactly where it is.
        ///
        /// This replaced a flat sixteen-bit allowance. The allowance was invented, and the whole
        /// reason it was wrong to invent it is that being over the limit at THIS point in the build
        /// is the normal state of a VRCFury avatar: the only meaningful question is what the number
        /// will be at the end, and that number has a formula.
        /// </summary>
        private static int SmallestCostVrcFuryCanReach(VRCAvatarDescriptor descriptor, int needed)
        {
            var parameters = descriptor.expressionParameters;
            int spent = parameters == null ? 0 : parameters.CalcTotalCost();
            if (parameters?.parameters == null) return spent + needed;

            var menuDriven = new HashSet<string>();
            CollectMenuParameters(descriptor.expressionsMenu, new HashSet<VRCExpressionsMenu>(),
                                  menuDriven);

            int compressedAway = 0, numbers = 0, bools = 0;
            foreach (var parameter in parameters.parameters)
            {
                if (parameter == null || !parameter.networkSynced) continue;
                if (!menuDriven.Contains(parameter.name)) continue;

                if (parameter.valueType == VRCExpressionParameters.ValueType.Bool)
                {
                    compressedAway += 1;
                    bools++;
                }
                else
                {
                    compressedAway += 8;
                    numbers++;
                }
            }

            if (numbers == 0 && bools == 0) return spent + needed;

            // One slot of each type present, so the batch count is however many have to queue
            // through it, and the index has to be able to name every batch plus "nothing".
            int batches = Mathf.Max(numbers, bools);
            int indexBits = 1;
            while ((1 << indexBits) < batches + 1) indexBits++;

            int channel = (numbers > 0 ? 8 : 0) + (bools > 0 ? 1 : 0) + indexBits;

            return spent + needed - compressedAway + channel;
        }

        private static void CollectMenuParameters(VRCExpressionsMenu menu,
                                                  HashSet<VRCExpressionsMenu> seen,
                                                  HashSet<string> into)
        {
            if (menu == null || !seen.Add(menu)) return;

            foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>())
            {
                if (control == null) continue;
                if (!string.IsNullOrEmpty(control.parameter?.name)) into.Add(control.parameter.name);
                if (control.subParameters != null)
                    foreach (var sub in control.subParameters)
                        if (!string.IsNullOrEmpty(sub?.name)) into.Add(sub.name);

                CollectMenuParameters(control.subMenu, seen, into);
            }
        }

        /// <summary>
        /// Will a parameter compressor run AFTER this build and bring the budget back down?
        ///
        /// It is the only question worth asking, and it turns on whether VRCFury is installed -
        /// NOT on whether this avatar uses it. That is from VRCFury's own source, and it is the
        /// opposite of what this function used to assume:
        ///
        ///   - ParameterCompressorHook extends VrcfAvatarPreprocessor, whose OnPreprocessAvatar has
        ///     no VRCFury-component gate of any kind. It runs on EVERY avatar the SDK preprocesses.
        ///   - Its solver opens with "if (originalCost &lt;= maxCost) return" - so on an avatar that
        ///     is already under the limit it does nothing at all, and on one that is over it
        ///     compresses whether or not a single VRCFury component was ever added.
        ///
        /// Requiring a VF component here therefore had a false negative built into it: an ordinary
        /// avatar in a project that has VRCFury installed would be judged as if nothing would
        /// compress afterwards, and refused for a budget that was about to be brought back down.
        /// That is the direction that costs somebody their protection, so it is gone.
        ///
        /// Asked by type name so nothing in this file references VRCFury.
        /// </summary>
        private static bool CompressorWillRun() => CompressorIsInstalled();

        private static bool CompressorIsInstalled()
        {
            if (compressorInstalled.HasValue) return compressorInstalled.Value;

            compressorInstalled = false;
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }

                foreach (var type in types)
                {
                    if (type == null || type.Name != "ParameterCompressorHook") continue;
                    if ((type.Namespace ?? "").StartsWith("VF", System.StringComparison.Ordinal))
                    {
                        compressorInstalled = true;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Answered once - the assembly list does not change inside a build.</summary>
        private static bool? compressorInstalled;

        public static void Generate(GameObject avatar, MeshProtectRoot settings,
                                    IList<Renderer> renderers, string folder)
        {
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
                throw new System.InvalidOperationException("No VRCAvatarDescriptor on the avatar root.");

            descriptor.customExpressions = true;

            // Asset file names become the object names that travel inside the uploaded avatar,
            // so they are derived from this avatar's generated family rather than from the tool.
            var variant = settings.variant;

            // One build, one empty clip; the field would otherwise hold a destroyed asset from the
            // previous build's temporary folder.
            emptyClip = null;

            var parameters = CloneOrCreateParameters(descriptor, variant, folder);
            var rootMenu = CloneOrCreateMenu(descriptor, variant, folder);
            // Read before the copy, because the copy is what we will rebuild it on top of.
            var overrideChain = ReadOverrideChain(FindFXLayer(descriptor));
            var fx = CloneOrCreateFXController(descriptor, variant, folder);

            // Read now, before a single layer of ours exists. Reserving the MMD slots below adds
            // padding layers, and counting those would be this tool asking itself what the avatar
            // does.
            bool writeDefaults = DetectWriteDefaults(fx);

            // Said out loud because the last time this was wrong the symptom was an avatar whose
            // face would not move in MMD worlds, and there was nothing in the console to connect
            // that to an animator setting. One line here turns the next such report into a
            // five-second answer.
            Debug.Log($"[MeshProtect] This avatar's FX controller runs Write Defaults " +
                      $"{(writeDefaults ? "ON" : "OFF")}, so the added layers use the same. Mixing " +
                      "the two is what stops a face moving in MMD worlds.");

            // Parameter and material property names are generated per avatar, so two protected
            // avatars never share a name and a tool cannot find the password chain by grepping for
            // a known one.
            string[] paramNames = variant.parameterNames;

            ReserveMmdLayers(fx, variant, folder, writeDefaults);
            RejectTypeClashes(parameters, fx, paramNames, variant.bitNames);
            AddExpressionParameters(parameters, variant, paramNames, variant.bitNames);
            var keyMenu = BuildKeyMenu(variant, paramNames, folder);
            rootMenu = AttachSubMenu(rootMenu, keyMenu, variant, folder, settings);
            AddPackLayers(fx, variant, paramNames, folder, writeDefaults);
            AddDecodeLayers(fx, variant, avatar, renderers, folder, writeDefaults);

            descriptor.expressionParameters = parameters;
            descriptor.expressionsMenu = rootMenu;
            // Preserve the override chain, including distinct clips that happen to share a name.
            // External clips keep their identities; copied sub-assets are matched by local file ID.
            var fxTarget = RebuildOverrideChain(overrideChain, fx, variant, folder);
            SetFXLayer(descriptor, fxTarget);

            EditorUtility.SetDirty(descriptor);
            EditorUtility.SetDirty(parameters);
            EditorUtility.SetDirty(rootMenu);
            EditorUtility.SetDirty(fx);
        }

        /// <summary>
        /// Keep FX layer indices 1 and 2 clear before adding anything.
        ///
        /// MMD worlds switch those two layers off so their dance animation can drive the avatar's
        /// face. Any layer that lands there stops running, and if that is a password layer the
        /// digits stop reaching the material - which, now that a locked avatar collapses to a
        /// point, means the wearer simply vanishes on entering an MMD world.
        ///
        /// Layers are appended, so this only bites on an avatar whose FX controller has fewer than
        /// three layers of its own. Padding with empty ones is enough: they do nothing, and MMD
        /// worlds are welcome to disable them.
        /// </summary>
        private static void ReserveMmdLayers(AnimatorController fx, MeshProtectVariant variant,
                                             string folder, bool writeDefaults)
        {
            const int firstFreeSlot = 3;

            while (fx.layers.Length < firstFreeSlot)
            {
                var stateMachine = new AnimatorStateMachine
                {
                    name = variant.padLayerNames[fx.layers.Length % variant.padLayerNames.Length],
                    hideFlags = HideFlags.HideInHierarchy
                };
                AssetDatabase.AddObjectToAsset(stateMachine, fx);

                // Even a padding layer needs a state with a clip in it: a layer whose state has no
                // motion at all is not something Unity is obliged to treat as a layer that does
                // nothing, and it costs one empty clip to stop wondering.
                //
                // This used to claim that such a state, at weight 1 with Write Defaults on, writes
                // the default value of everything the CONTROLLER animates. That was too strong, and
                // believing it is what talked this tool into hardcoding Write Defaults off and
                // shipping a release whose avatars could not move their faces in MMD worlds. Write
                // Defaults fills in the properties bound in the same LAYER; these layers bind
                // nothing, so there is nothing to fill in. Measured on a real avatar afterwards:
                // these layers run Write Defaults on with an empty clip and the avatar's toggles,
                // transforms and physics are untouched.
                var idle = stateMachine.AddState("Idle");
                idle.writeDefaultValues = writeDefaults;
                idle.motion = EmptyClip(folder, variant);
                stateMachine.defaultState = idle;

                fx.AddLayer(new AnimatorControllerLayer
                {
                    name = stateMachine.name,
                    defaultWeight = 1f,
                    stateMachine = stateMachine
                });
            }
        }

        // ------------------------------------------------------------------ parameters

        /// <summary>
        /// Refuse to touch a name the avatar already uses for something else. Overwriting the
        /// expression parameter while leaving an existing Int/Float of the same name in the FX
        /// controller produces transitions that silently never fire.
        /// </summary>
        /// <summary>
        /// The first name already taken with the wrong type, or null. Shared with the pre-flight so
        /// the two cannot answer differently - a check that says yes before the bake and no after
        /// it is worse than either answer on its own.
        /// </summary>
        private static string FirstNameClash(VRCExpressionParameters parameters, AnimatorController fx,
                                             string[] intNames, string[] boolNames)
        {
            string found = null;

            Look(intNames, VRCExpressionParameters.ValueType.Int, AnimatorControllerParameterType.Int);
            Look(boolNames, VRCExpressionParameters.ValueType.Bool, AnimatorControllerParameterType.Bool);

            void Look(string[] names, VRCExpressionParameters.ValueType expected,
                      AnimatorControllerParameterType expectedAnim)
            {
                if (names == null) return;
                foreach (var name in names)
                {
                    if (found != null) return;

                    var existingParam = parameters?.parameters?
                        .FirstOrDefault(p => p != null && p.name == name);
                    if (existingParam != null && existingParam.valueType != expected)
                    {
                        found = $"expression parameter '{name}' already exists as {existingParam.valueType}";
                        return;
                    }

                    var existingAnim = fx?.parameters.FirstOrDefault(p => p.name == name);
                    if (existingAnim != null && existingAnim.type != expectedAnim)
                        found = $"FX parameter '{name}' already exists as {existingAnim.type}";
                }
            }

            return found;
        }

        /// <summary>
        /// The backstop. CanGenerate asks the same question before anything is baked, so reaching
        /// this means the avatar changed between the two - and by then the meshes are displaced,
        /// which leaves stopping as the only safe answer.
        /// </summary>
        private static void RejectTypeClashes(VRCExpressionParameters parameters,
                                              AnimatorController fx, string[] intNames, string[] boolNames)
        {
            string clash = FirstNameClash(parameters, fx, intNames, boolNames);
            if (clash == null) return;

            throw new System.InvalidOperationException(
                "Parameter name clash, refusing to overwrite: " + clash +
                "\n\nThese names are generated for this avatar. A clash means something else in the " +
                "project already claimed them; rename or remove it and bake again, or press " +
                "'New Protection' to draw a fresh set.");
        }

        private static void AddExpressionParameters(VRCExpressionParameters parameters,
                                                    MeshProtectVariant variant,
                                                    string[] intNames, string[] boolNames)
        {
            var list = parameters.parameters != null
                ? new List<VRCExpressionParameters.Parameter>(parameters.parameters)
                : new List<VRCExpressionParameters.Parameter>();

            var added = new List<VRCExpressionParameters.Parameter>();

            // Menu input. Unsynced, so it costs nothing and remote players never see it: only the
            // owner ever taps a digit, and what others need is the result, not the keystrokes.
            foreach (var name in intNames)
            {
                list.RemoveAll(p => p != null && p.name == name);
                added.Add(new VRCExpressionParameters.Parameter
                {
                    name = name,
                    valueType = VRCExpressionParameters.ValueType.Int,
                    saved = false,            // a Button releases to zero, so there is nothing to keep
                    networkSynced = false,    // 0 bits
                    defaultValue = 0f
                });
            }

            // The transport. Saved, so the password is entered once; synced, because every remote
            // client has to decode the mesh too.
            foreach (var name in boolNames)
            {
                list.RemoveAll(p => p != null && p.name == name);
                added.Add(new VRCExpressionParameters.Parameter
                {
                    name = name,
                    valueType = VRCExpressionParameters.ValueType.Bool,
                    saved = true,
                    networkSynced = true,
                    defaultValue = 0f         // all-false is the locked state
                });
            }

            // Scatter them through the avatar's own parameters instead of appending a block of
            // thirty at the end. With arbitrary names, a contiguous run was the last thing left
            // that let someone open the parameter list and circle ours at a glance.
            //
            // Seeded from the variant, so a re-bake produces the same list rather than reshuffling
            // it every upload.
            var scatter = new System.Random(variant.macSalt);
            foreach (var parameter in added)
                list.Insert(scatter.Next(list.Count + 1), parameter);

            parameters.parameters = list.ToArray();

            int cost = parameters.CalcTotalCost();

            // Not a refusal, by the same rule as CanGenerate: nothing about the parameter budget
            // is allowed to cancel the protection, because an avatar that finishes a build has to
            // have it. CanGenerate already said this out loud with the number that accounts for
            // whatever runs afterwards; repeating it here with the PRE-compression total would say
            // it twice and contradict itself the second time. That is not hypothetical - it shipped:
            // the VRCFury-aware check agreed the avatar would fit, and twenty lines later this one
            // threw "368 / 256" and stopped the upload, on an avatar whose compressor had well over
            // a hundred spare bits.
            //
            // Logged, not silent, because if an upload does come back broken this is the number
            // that says why.
            if (cost > VRCExpressionParameters.MAX_PARAMETER_COST)
            {
                Debug.Log($"[MeshProtect] Expression parameters are at {cost} / " +
                          $"{VRCExpressionParameters.MAX_PARAMETER_COST} with the password's " +
                          $"{boolNames.Length} bits added. On a VRCFury avatar that is expected at " +
                          "this point - its compressor has not run yet, and the number it reports " +
                          "is the one that counts.");
            }
        }

        // ------------------------------------------------------------------ menu

        /// <summary>
        /// Asset names are derived from the generated family, not from the tool's own name.
        /// VRCExpressionsMenu assets ship inside the uploaded avatar, so a literal "MeshProtect"
        /// in one of them would let a scanner recognise every avatar this tool ever produced -
        /// exactly the signature the per-avatar variant exists to avoid.
        /// </summary>
        private static VRCExpressionsMenu BuildKeyMenu(MeshProtectVariant variant, string[] paramNames,
                                                       string folder)
        {
            var passwordMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            passwordMenu.name = variant.menuAssetName;
            passwordMenu.controls = new List<VRCExpressionsMenu.Control>();
            AssetDatabase.CreateAsset(passwordMenu, $"{folder}/{passwordMenu.name}.asset");

            for (int position = 0; position < paramNames.Length; position++)
            {
                var first = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                first.name = variant.digitMenuAssetNames[position];
                first.controls = new List<VRCExpressionsMenu.Control>();
                AssetDatabase.CreateAsset(first, $"{folder}/{first.name}.asset");

                // Digits are 1..8, which is exactly one VRChat menu page. The previous 1..9 range
                // overflowed and needed an "8-9" sub-menu, so two of the nine digits took an extra
                // click and the manual needed a paragraph explaining it. 8^6 is the same order of
                // magnitude as 9^6, so nothing meaningful was traded for that.
                for (int digit = MeshProtectRoot.MinDigit; digit <= MeshProtectRoot.MaxDigit; digit++)
                    AddDigitToggle(first, paramNames[position], digit);

                passwordMenu.controls.Add(new VRCExpressionsMenu.Control
                {
                    // A digit, not a word. This label is read in game by whoever ends up wearing
                    // the avatar, who may not share a language with whoever protected it - and it
                    // was Chinese while the rest of the tool is English, which is nobody's choice.
                    name = (position + 1).ToString(),
                    type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                    subMenu = first,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = "" }
                });

                EditorUtility.SetDirty(first);
            }

            // There is deliberately NO reset control, and it used to be here.
            //
            // What it solved: the transport bools are saved, the menu cannot send "not entered",
            // so a wearer who turns a dial a short password does not use latches that position
            // forever. The button cleared all six.
            //
            // Why it went anyway, on the word of someone actually wearing the thing: a six digit
            // password - the default - cannot over-type, so for most avatars the button was a
            // permanent extra control that could only ever CLEAR a saved unlock by accident. A
            // wrong digit inside the password's own length needs no reset either - dials
            // overwrite. The one real brick, short password plus a touched spare dial, has a
            // VRChat-native escape: Reset Avatar Data clears every saved parameter, our bits
            // included. Rarer case, native fix, one fewer control.

            EditorUtility.SetDirty(passwordMenu);
            return passwordMenu;
        }

        /// <summary>
        /// A tap, not a toggle.
        ///
        /// A Toggle keeps the parameter set until it is pressed again, which turns it back off -
        /// so tapping the digit you already chose silently unsets it, and the menu carries a
        /// sticky selection that means nothing. A Button holds the value while pressed and
        /// releases to zero, which is all the pack layer needs: it reads the value on the frame
        /// the press lands and stores it in the transport bits, which persist on their own.
        /// </summary>
        private static void AddDigitToggle(VRCExpressionsMenu menu, string parameter, int digit)
        {
            menu.controls.Add(new VRCExpressionsMenu.Control
            {
                name = digit.ToString(),
                type = VRCExpressionsMenu.Control.ControlType.Button,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter },
                value = digit
            });
        }

        /// <summary>
        /// Resolve the parent against the final menu tree and copy the branch we edit. Automatic
        /// placement keeps the original overflow behaviour; an explicit slot stays on that page.
        /// </summary>
        private static VRCExpressionsMenu AttachSubMenu(VRCExpressionsMenu rootMenu,
                                                        VRCExpressionsMenu keyMenu,
                                                        MeshProtectVariant variant, string folder,
                                                        MeshProtectRoot settings)
        {
            var targetMenu = rootMenu;
            var segments = MeshProtectMenuPath.Parse(settings.unlockMenuPath);
            for (int depth = 0; depth < segments.Length; depth++)
            {
                var submenus = (targetMenu.controls ?? new List<VRCExpressionsMenu.Control>())
                    .Where(c => c != null &&
                                c.type == VRCExpressionsMenu.Control.ControlType.SubMenu &&
                                c.subMenu != null).ToArray();
                var matches = MeshProtectMenuPath.MatchIndices(
                    submenus.Select(c => c.name).ToArray(), segments[depth]);
                if (matches.Length != 1)
                    throw new System.InvalidOperationException(
                        $"Unlock menu path '{settings.unlockMenuPath}' cannot be resolved: " +
                        $"'{segments[depth]}' matches {matches.Length} submenus. Use a unique " +
                        "exact submenu name, including rich-text tags if needed. Escape a literal " +
                        "'/' in a name as '\\/'.");

                var selected = submenus[matches[0]];
                var child = CopyMenu(selected.subMenu,
                    variant.rootMenuAssetName + "_p" + depth, folder);
                selected.subMenu = child;
                EditorUtility.SetDirty(targetMenu);
                targetMenu = child;
            }
            if (targetMenu.controls == null) targetMenu.controls = new List<VRCExpressionsMenu.Control>();

            var unlock = new VRCExpressionsMenu.Control
            {
                name = string.IsNullOrWhiteSpace(settings.unlockMenuName)
                    ? RootControlName : settings.unlockMenuName.Trim(),
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                subMenu = keyMenu,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "" }
            };

            // Display labels belong to the author; an existing "Unlock" is not our control.
            int position = Mathf.Clamp(settings.unlockMenuPosition, 0, MenuControlLimit);
            if (targetMenu.controls.Count < MenuControlLimit)
            {
                int index = position == 0 ? targetMenu.controls.Count
                    : Mathf.Min(position - 1, targetMenu.controls.Count);
                targetMenu.controls.Insert(index, unlock);
                EditorUtility.SetDirty(targetMenu);
                return rootMenu;
            }

            // An explicit slot reserves room for Unlock and More. Automatic placement moves
            // only the last original entry down and puts Unlock beside it.
            int keep = MenuControlLimit - (position == 0 ? 1 : 2);
            var displaced = targetMenu.controls.GetRange(keep, targetMenu.controls.Count - keep);
            targetMenu.controls.RemoveRange(keep, targetMenu.controls.Count - keep);

            var overflow = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            overflow.name = variant.wrapperMenuAssetName;
            overflow.controls = displaced;
            if (position == 0) overflow.controls.Add(unlock);
            AssetDatabase.CreateAsset(overflow, $"{folder}/{overflow.name}.asset");

            targetMenu.controls.Add(new VRCExpressionsMenu.Control
            {
                name = OverflowControlName,
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                subMenu = overflow,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "" }
            });
            if (position != 0) targetMenu.controls.Insert(position - 1, unlock);
            EditorUtility.SetDirty(targetMenu);

            Debug.LogWarning(
                $"[MeshProtect] The selected expression menu was full. Its last " +
                $"{MenuControlLimit - keep} original control(s) moved into '{OverflowControlName}'. " +
                (position == 0 ? $"'{unlock.name}' is in that page too. "
                               : $"'{unlock.name}' occupies position {position}. ") +
                "Only this build's menu copies were changed.");
            return rootMenu;
        }

        // ------------------------------------------------------------------ FX layers

        /// <summary>
        /// Menu Int -> four synced bools, one layer per digit.
        ///
        /// The default state is deliberately empty and has no driver. A saved password restores the
        /// bools before anything runs, and the local Int starts at 0 on a machine that has never
        /// seen this avatar; a driver on the default state would fire in exactly that situation and
        /// wipe the restored password.
        /// </summary>
        private static void AddPackLayers(AnimatorController fx, MeshProtectVariant variant,
                                          string[] paramNames, string folder, bool writeDefaults)
        {
            // Match the controller, like the decode layers do. Mixing Write Defaults on and off
            // inside one controller is the classic way for an added tool to break an avatar's
            // existing animations, and these states used to be hardcoded off while the decode
            // layers followed the avatar.

            for (int i = 0; i < paramNames.Length; i++)
            {
                string param = paramNames[i];
                string layerName = variant.packLayerNames[i];

                RemoveExistingLayer(fx, layerName);
                if (!fx.parameters.Any(p => p.name == param))
                    fx.AddParameter(param, AnimatorControllerParameterType.Int);
                foreach (var bit in BitsOf(variant, i))
                    if (!fx.parameters.Any(p => p.name == bit))
                        fx.AddParameter(bit, AnimatorControllerParameterType.Bool);

                var stateMachine = new AnimatorStateMachine
                {
                    name = layerName,
                    hideFlags = HideFlags.HideInHierarchy
                };
                AssetDatabase.AddObjectToAsset(stateMachine, fx);

                var empty = EmptyClip(folder, variant);

                var idle = stateMachine.AddState("Idle");
                idle.writeDefaultValues = writeDefaults;
                idle.motion = empty;
                stateMachine.defaultState = idle;

                for (int digit = MeshProtectRoot.MinDigit; digit <= MeshProtectRoot.MaxDigit; digit++)
                {
                    var state = stateMachine.AddState(digit.ToString());
                    state.writeDefaultValues = writeDefaults;
                    state.motion = empty;

                    var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
                    // Only the owner types a password; letting remote clients run this would have
                    // them fighting the synced value they just received.
                    driver.localOnly = true;
                    // The digit itself, not digit-1. Pattern 0 means "nobody entered this", which
                    // is not the same as "wrong": a password shorter than six digits has it as the
                    // correct answer for the positions the author left out, and the key carries it
                    // like any other value. Only the reset control writes it.
                    int pattern = digit;
                    var bits = BitsOf(variant, i);
                    for (int b = 0; b < bits.Length; b++)
                    {
                        driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter
                        {
                            type = VRC_AvatarParameterDriver.ChangeType.Set,
                            name = bits[b],
                            value = (pattern & (1 << b)) != 0 ? 1f : 0f
                        });
                    }

                    var transition = stateMachine.AddAnyStateTransition(state);
                    transition.hasExitTime = false;
                    transition.duration = 0f;
                    transition.canTransitionToSelf = false;
                    transition.AddCondition(AnimatorConditionMode.Equals, digit, param);
                }

                fx.AddLayer(new AnimatorControllerLayer
                {
                    name = layerName,
                    defaultWeight = 1f,
                    stateMachine = stateMachine
                });
            }
        }

        /// <summary>
        /// Synced bools -> material property, one layer per digit.
        ///
        /// This is the half that has to work on other people's clients, which is why it reads the
        /// bools rather than the menu Ints: a remote player receives the former and never sees the
        /// latter. Nine of the sixteen patterns are used - zero for "not entered" and 1..8 for the
        /// digits - and every one of them has a state, so the layer is never in an undefined one.
        /// Zero is not only the starting state: for a password shorter than six digits it is what
        /// the unused positions are supposed to hold, so it is part of a correct answer.
        /// </summary>
        private static void AddDecodeLayers(AnimatorController fx, MeshProtectVariant variant,
                                            GameObject avatar, IList<Renderer> renderers, string folder, bool writeDefaults)
        {

            for (int i = 0; i < MeshProtectRoot.PasswordLength; i++)
            {
                string layerName = variant.decodeLayerNames[i];
                var bits = BitsOf(variant, i);

                RemoveExistingLayer(fx, layerName);
                foreach (var bit in bits)
                    if (!fx.parameters.Any(p => p.name == bit))
                        fx.AddParameter(bit, AnimatorControllerParameterType.Bool);

                var stateMachine = new AnimatorStateMachine
                {
                    name = layerName,
                    hideFlags = HideFlags.HideInHierarchy
                };
                AssetDatabase.AddObjectToAsset(stateMachine, fx);

                // Patterns 0..8: zero is the locked state, 1..8 are the digits. The remaining
                // four-bit patterns are unreachable because only the pack driver writes these bits.
                AnimatorState first = null;
                for (int pattern = 0; pattern <= MeshProtectRoot.MaxDigit; pattern++)
                {
                    int digit = pattern;
                    var clip = BuildDigitClip($"{folder}/{layerName}{digit}.anim", avatar,
                                              renderers, variant.digitProperties[i], digit);

                    var state = stateMachine.AddState(digit == 0 ? "Locked" : digit.ToString());
                    state.motion = clip;
                    state.writeDefaultValues = writeDefaults;
                    if (first == null) first = state;

                    var transition = stateMachine.AddAnyStateTransition(state);
                    transition.hasExitTime = false;
                    transition.duration = 0f;
                    transition.canTransitionToSelf = false;
                    for (int b = 0; b < bits.Length; b++)
                    {
                        transition.AddCondition(
                            (pattern & (1 << b)) != 0 ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot,
                            0f, bits[b]);
                    }
                }

                stateMachine.defaultState = first;

                fx.AddLayer(new AnimatorControllerLayer
                {
                    name = layerName,
                    defaultWeight = 1f,
                    stateMachine = stateMachine
                });
            }
        }

        /// <summary>
        /// Unity's animated-material binding for a shader property.
        ///
        /// The separator is a dot, and the property name already carries its leading underscore:
        /// "material" + "._zabc1234". Getting this wrong produces a binding that names a property
        /// nothing has, so the curve is written, saved and shipped, and simply never reaches the
        /// material - the avatar stays scrambled with no error anywhere. Route every binding
        /// through here rather than concatenating at the call site.
        /// </summary>
        private static string MaterialBinding(string shaderProperty) => "material." + shaderProperty;

        private static string[] BitsOf(MeshProtectVariant variant, int digitIndex)
        {
            var bits = new string[MeshProtectRoot.BitsPerDigit];
            for (int b = 0; b < bits.Length; b++)
                bits[b] = variant.bitNames[digitIndex * MeshProtectRoot.BitsPerDigit + b];
            return bits;
        }

        private static AnimationClip BuildDigitClip(string path, GameObject avatar,
                                                    IList<Renderer> renderers, string digitProperty,
                                                    int digitValue)
        {
            var clip = new AnimationClip { name = System.IO.Path.GetFileNameWithoutExtension(path) };

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                // Mecanim material curves target the Renderer and write a renderer-wide material
                // property block. This is the same binding format Unity's Animation window emits
                // for VRChat avatars, and it reaches every material slot on the renderer.
                //
                // One curve per digit, carrying the digit itself. The previous version spread each
                // digit over four 0/1 bit properties, which meant 24 curves per clip and 24
                // material properties for the shader to reassemble.
                var curve = new AnimationCurve(new Keyframe(0f, digitValue),
                                               new Keyframe(1f / 60f, digitValue));
                var binding = new EditorCurveBinding
                {
                    path = AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform),
                    type = renderer.GetType(),
                    propertyName = MaterialBinding(digitProperty)
                };
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }

            AssetDatabase.CreateAsset(clip, path);
            return clip;
        }

        /// <summary>
        /// Match whatever the avatar already does. Mixing Write Defaults on and off inside one
        /// controller is the single most common source of "my avatar broke after adding a tool".
        /// </summary>
        /// <summary>
        /// One empty clip, shared by every state of ours that has nothing to animate.
        ///
        /// A clip that animates nothing is not the same thing as no clip. A state with no motion at
        /// all is not something Unity is obliged to treat as a state that does nothing, and this is
        /// the standard shape for generated layers - it is what Modular Avatar and the other tools
        /// that append to an avatar's controller do. One shared empty clip is a cheap way to stop
        /// wondering about it.
        ///
        /// This used to justify itself with something stronger and false: that such a state with
        /// Write Defaults ON writes the default value of every property the CONTROLLER touches, and
        /// that it had been seen resetting an avatar's transforms and PhysBones every frame. Write
        /// Defaults fills in the properties bound in the same LAYER, and these layers bind one
        /// material property or nothing at all. The avatar in that story posed wrongly because of a
        /// VRChat saved parameter sitting at 0.24 with this tool uninstalled.
        ///
        /// It is worth being exact about, because believing the strong version is what hardcoded
        /// Write Defaults off, and that shipped a release whose avatars could not move their faces
        /// in MMD worlds.
        /// </summary>
        private static AnimationClip EmptyClip(string folder, MeshProtectVariant variant)
        {
            if (emptyClip != null) return emptyClip;

            emptyClip = new AnimationClip { name = variant.padLayerNames[0] };
            AssetDatabase.CreateAsset(emptyClip, $"{folder}/{emptyClip.name}.anim");
            return emptyClip;
        }

        private static AnimationClip emptyClip;

        /// <summary>
        /// Our states use whatever the avatar already uses. Read this before changing it back.
        ///
        /// This has been both ways round, and the reason it is here rather than a constant is that
        /// a constant cost a shipped release its MMD support.
        ///
        /// MMD worlds drive the wearer's face by switching off FX layers 1 and 2 - the ones holding
        /// the avatar's expressions - so the world's dance can write those blend shapes instead.
        /// That release only happens when the FX controller runs Write Defaults ON; with it off the
        /// blend shapes keep their last value and the face simply stops moving. Every guide in this
        /// ecosystem says the same thing, and Modular Avatar and VRCFury both carry code for it.
        ///
        /// It was hardcoded off for one release. Measured afterwards on the avatar that reported
        /// the bug: 79 states across its seven FX layers, every one of them Write Defaults ON, and
        /// twelve layers of ours appended with it off. The face did not move in MMD worlds and did
        /// move with the tool removed.
        ///
        /// The commit that hardcoded it off was answering a different symptom - an avatar loading
        /// into a pose it was not in - and its own message records what that turned out to be: a
        /// VRChat saved parameter, paryi_floating_M, sitting at 0.24 on that avatar and doing it
        /// with the tool uninstalled. The Write Defaults change was a fix for something that was
        /// never this tool's doing, and it broke MMD to make it.
        ///
        /// The hazard it worried about is real but does not reach here: Write Defaults writes the
        /// defaults of properties animated in the same LAYER that the current state leaves alone.
        /// Our layers animate one material property each and every state in them writes it, so
        /// there is nothing for a default to overwrite - which is exactly the condition the "never
        /// mix Write Defaults" advice is about.
        /// </summary>
        private static bool DetectWriteDefaults(AnimatorController fx)
        {
            int on = 0, off = 0;
            foreach (var layer in fx.layers) Count(layer.stateMachine, ref on, ref off,
                                                   new HashSet<AnimatorStateMachine>());

            // A controller with no states at all is not evidence of anything, and VRChat's own
            // default is on. Ties go the same way for the same reason.
            return off == 0 || on >= off;
        }

        /// <summary>
        /// Recursive, because an avatar that keeps its states in sub-state machines would otherwise
        /// look like a controller with no states and be read as whatever the fallback happens to be.
        /// </summary>
        private static void Count(AnimatorStateMachine machine, ref int on, ref int off,
                                  HashSet<AnimatorStateMachine> seen)
        {
            if (machine == null || !seen.Add(machine)) return;

            foreach (var child in machine.states)
            {
                if (child.state == null) continue;
                if (child.state.writeDefaultValues) on++; else off++;
            }
            foreach (var sub in machine.stateMachines) Count(sub.stateMachine, ref on, ref off, seen);
        }

        /// <summary>
        /// Drop any layer already carrying one of our names, so a re-bake replaces rather than
        /// accumulates.
        ///
        /// At this point the FX controller is a fresh copy of the avatar's and none of our layers
        /// have been added yet, so anything matching belongs to the avatar. The names are eight-letter
        /// nonsense drawn per avatar, which makes a collision vanishingly unlikely - but "vanishingly
        /// unlikely" and "silent" together is how an avatar ships with one of its layers missing and
        /// nobody knows why. It still goes, because leaving two layers with one name is worse; it
        /// just does not go quietly.
        /// </summary>
        private static void RemoveExistingLayer(AnimatorController fx, string name)
        {
            for (int i = fx.layers.Length - 1; i >= 0; i--)
            {
                if (fx.layers[i].name != name) continue;

                Debug.LogWarning(
                    $"[MeshProtect] The FX controller already had a layer called '{name}', which is " +
                    "the name this build generated for one of its own. It has been removed from the " +
                    "upload to make room. Your project is untouched, but if that layer was yours, " +
                    "press 'New Protection' to draw a different set of names and upload again.");
                fx.RemoveLayer(i);
            }
        }

        // ------------------------------------------------------------------ asset plumbing

        private static VRCExpressionParameters CloneOrCreateParameters(VRCAvatarDescriptor descriptor,
                                                                       MeshProtectVariant variant, string folder)
        {
            string path = $"{folder}/{variant.parametersAssetName}.asset";

            var created = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            created.name = variant.parametersAssetName;

            // Each entry is copied, not just the array. ToArray() duplicates the list and leaves
            // every Parameter in it the same object the author's asset holds, so anything that
            // later writes to one - renaming it, say - edits their project from inside a build.
            // Measured: with the shallow copy, renaming parameters in a build left the author's own
            // expression parameters asset holding the new names. Nothing reported a problem,
            // because the change sits in memory until something calls SaveAssets, which this build
            // does.
            var source = descriptor.expressionParameters?.parameters;
            created.parameters = source == null
                ? new VRCExpressionParameters.Parameter[0]
                : source.Select(Copy).ToArray();

            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        private static VRCExpressionParameters.Parameter Copy(VRCExpressionParameters.Parameter source)
        {
            if (source == null) return null;
            return new VRCExpressionParameters.Parameter
            {
                name = source.name,
                valueType = source.valueType,
                saved = source.saved,
                networkSynced = source.networkSynced,
                defaultValue = source.defaultValue,
            };
        }

        /// <summary>
        /// A private copy of the avatar's root menu, built from the object rather than from its
        /// file.
        ///
        /// This used to be AssetDatabase.CopyAsset followed by LoadAssetAtPath, which copies a FILE
        /// and then returns that file's MAIN asset. Those are the same object only when the root
        /// menu happens to own its file. By the time this runs, Modular Avatar has rebuilt the
        /// menu, and it puts many menus in one asset - so the copy came back as whichever menu
        /// happened to be first, and the avatar shipped with a submenu as its root. Measured on a
        /// real avatar: 455 controls in the tree became 68, and the wearer's menu was replaced by
        /// the "tail" submenu.
        ///
        /// Copying the control list sidesteps the file entirely. The controls themselves are shared
        /// with the original, which is safe because only this list is ever added to.
        /// </summary>
        private static VRCExpressionsMenu CloneOrCreateMenu(VRCAvatarDescriptor descriptor,
                                                            MeshProtectVariant variant, string folder)
        {
            return CopyMenu(descriptor.expressionsMenu, variant.rootMenuAssetName, folder);
        }

        private static VRCExpressionsMenu CopyMenu(VRCExpressionsMenu source, string name, string folder)
        {
            // Instantiate copies the serializable controls too. Replacing a submenu reference
            // must never edit the author's control object, including menus stored as sub-assets.
            var copy = source != null ? Object.Instantiate(source)
                : ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            copy.name = name;
            copy.hideFlags = HideFlags.None;
            if (copy.controls == null) copy.controls = new List<VRCExpressionsMenu.Control>();
            AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.asset"));
            return copy;
        }

        private static AnimatorController CloneOrCreateFXController(VRCAvatarDescriptor descriptor,
                                                                    MeshProtectVariant variant, string folder)
        {
            string path = $"{folder}/{variant.controllerAssetName}.controller";

            var existing = FindFXLayer(descriptor);

            // No FX layer at all: there is nothing to preserve, so a fresh controller is right.
            if (existing == null) return AnimatorController.CreateAnimatorControllerAtPath(path);

            // From here a fresh controller would be a silent catastrophe rather than a fallback.
            // It has no layers, so the avatar uploads with every toggle, outfit and expression
            // gone - and the build succeeds, because an empty FX controller is a valid one.
            var source = existing as AnimatorController ?? UnderlyingController(existing);
            if (source == null)
                throw new System.InvalidOperationException(
                    $"The FX playable layer holds a {existing.GetType().Name}. The unlock layers can " +
                    "only be added to an Animator Controller, and shipping a new one in its place " +
                    "would drop every expression this avatar has. Assign an Animator Controller to " +
                    "the FX layer, or untick Mesh Protect Root to upload without protection.");

            var copy = CopyOutOfItsFile(source, path);
            if (copy == null)
                throw new System.InvalidOperationException(
                    $"Could not take a private copy of the FX controller '{source.name}'. The unlock " +
                    "layers have to go into a copy so your project's controller is never modified, " +
                    "and shipping a fresh one in its place would drop every expression this avatar " +
                    "has. The upload was stopped rather than do that. If an upstream tool built this " +
                    "controller in memory, saving the avatar's setup to disk before uploading gives " +
                    "this something to copy.");

            return copy;
        }

        /// <summary>
        /// The AnimatorController at the bottom of a chain of AnimatorOverrideControllers, or null
        /// if there is not one.
        ///
        /// An override controller has no layers of its own - it delegates to a base controller and
        /// only substitutes clips - so it is not something the unlock layers can be added to. It IS
        /// something the FX playable layer can hold: the SDK draws that field as
        /// ObjectField(typeof(RuntimeAnimatorController)), which accepts any subclass.
        ///
        /// Chains are followed because an override's base can itself be an override, and the loop
        /// is guarded because nothing stops somebody wiring one into a cycle.
        /// </summary>
        private static AnimatorController UnderlyingController(RuntimeAnimatorController controller)
        {
            var seen = new HashSet<RuntimeAnimatorController>();
            while (controller is AnimatorOverrideController over)
            {
                if (!seen.Add(over)) return null;              // cycle
                controller = over.runtimeAnimatorController;
                if (controller == null) return null;           // override with no base
            }
            return controller as AnimatorController;
        }

        /// <summary>
        /// Every override in a chain, outermost first, as (controller, its own overrides).
        /// </summary>
        private static List<KeyValuePair<AnimatorOverrideController,
                                         List<KeyValuePair<AnimationClip, AnimationClip>>>>
            ReadOverrideChain(RuntimeAnimatorController controller)
        {
            var chain = new List<KeyValuePair<AnimatorOverrideController,
                                              List<KeyValuePair<AnimationClip, AnimationClip>>>>();
            var seen = new HashSet<RuntimeAnimatorController>();
            while (controller is AnimatorOverrideController over && seen.Add(over))
            {
                var map = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                over.GetOverrides(map);
                chain.Add(new KeyValuePair<AnimatorOverrideController,
                                           List<KeyValuePair<AnimationClip, AnimationClip>>>(over, map));
                controller = over.runtimeAnimatorController;
            }
            return chain;
        }

        /// <summary>
        /// Rebuild an override chain on top of our copied controller, and hand back the thing the
        /// descriptor should point at.
        ///
        /// External clip references survive the controller file copy. Clips in that same file get
        /// new object identities, but retain their local file IDs. Names are not identities: two
        /// different clips named Idle can have different overrides, or only one can be overridden.
        /// </summary>
        private static RuntimeAnimatorController RebuildOverrideChain(
            List<KeyValuePair<AnimatorOverrideController,
                              List<KeyValuePair<AnimationClip, AnimationClip>>>> chain,
            AnimatorController baseCopy, MeshProtectVariant variant, string folder)
        {
            if (chain.Count == 0) return baseCopy;

            string sourcePath = AssetDatabase.GetAssetPath(
                UnderlyingController(chain[chain.Count - 1].Key));
            string copyPath = AssetDatabase.GetAssetPath(baseCopy);
            var copiedClips = new Dictionary<long, AnimationClip>();
            foreach (var clip in baseCopy.animationClips)
            {
                if (clip == null || AssetDatabase.GetAssetPath(clip) != copyPath) continue;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out _, out long id))
                    copiedClips[id] = clip;
            }

            RuntimeAnimatorController below = baseCopy;

            // Innermost first, so each copy can point at the one already rebuilt beneath it.
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var wanted = chain[i].Value;
                var replacements = new Dictionary<AnimationClip, AnimationClip>();
                foreach (var original in wanted)
                {
                    if (original.Key == null || original.Value == null) continue;
                    // Keep the original identity too: external clips and values inherited from
                    // an inner override have not been copied into the controller file.
                    replacements[original.Key] = original.Value;
                    if (!string.IsNullOrEmpty(sourcePath) &&
                        AssetDatabase.GetAssetPath(original.Key) == sourcePath &&
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(original.Key, out _, out long id) &&
                        copiedClips.TryGetValue(id, out var copiedKey))
                        replacements[copiedKey] = original.Value;
                }

                var copy = new AnimatorOverrideController
                {
                    name = $"{variant.controllerAssetName}_ovr{i}",
                    runtimeAnimatorController = below
                };
                AssetDatabase.CreateAsset(copy, $"{folder}/{copy.name}.overrideController");

                // The keys come from whatever is beneath us NOW, which may be a different set of
                // clip objects than the ones the original override was keyed by.
                var slots = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                copy.GetOverrides(slots);

                for (int s = 0; s < slots.Count; s++)
                {
                    var key = slots[s].Key;
                    if (key != null && replacements.TryGetValue(key, out var replacement))
                        slots[s] = new KeyValuePair<AnimationClip, AnimationClip>(key, replacement);
                }

                copy.ApplyOverrides(slots);
                below = copy;
            }

            return below;
        }

        /// <summary>
        /// Copy the file <paramref name="source"/> lives in, and return the object in the copy that
        /// corresponds to <paramref name="source"/> - which is not necessarily the copy's main
        /// asset.
        ///
        /// LoadAssetAtPath returns a file's main asset, so the obvious version of this silently
        /// returns the wrong object whenever what is being copied is a sub-asset. Tools that run
        /// earlier in the build routinely put several objects in one file.
        ///
        /// The object is found in the copy by its LOCAL FILE ID, which a byte copy preserves.
        /// It used to be found by its position among objects of its type, and that position does
        /// NOT survive the trip: the order LoadAllAssetsAtPath returns carries no contract, and
        /// measured on a real avatar's build it differs between the session that created the file,
        /// a copy imported in the same session, and a fresh session reading a prior session's
        /// file. NDMF and Modular Avatar put several controllers in one generated container, their
        /// file IDs are re-rolled every build, and the position method picked a DIFFERENT
        /// controller as a per-build lottery - up to every slot in every round. A name check
        /// caught most of those and turned them into refusals, which on the FX path is a stopped
        /// upload: an intermittent, dice-rolled build failure on any avatar whose FX shares a
        /// container. File ID association measured zero misses in every scenario the position
        /// method failed in, so the name check has nothing left to catch and is gone with it.
        /// </summary>
        internal static T CopyOutOfItsFile<T>(T source, string destination) where T : Object
        {
            string sourcePath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(sourcePath)) return null;

            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out _, out long wantedId))
                return null;

            ExplainBrokenReferences(sourcePath, destination);

            AssetDatabase.DeleteAsset(destination);
            if (!AssetDatabase.CopyAsset(sourcePath, destination)) return null;

            foreach (var candidate in AssetDatabase.LoadAllAssetsAtPath(destination))
                if (candidate is T match &&
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(match, out _, out long id) &&
                    id == wantedId)
                    return match;

            // A byte copy preserves local file IDs, so this is not expected to be reachable.
            // Callers decide what a failed copy costs; this only states the fact.
            Debug.LogWarning(
                $"[MeshProtect] Copying '{sourcePath}' produced no object with the local file ID " +
                $"of '{source.name}', so the copy cannot be used.");
            return null;
        }

        /// <summary>
        /// Say whose broken references those red errors are, before Unity prints them.
        ///
        /// Reading a controller whose file references objects it no longer contains makes Unity
        /// print "Broken text PPtr in file(...). Local file identifier (N) doesn't exist!" - in red,
        /// once per reference, every time it reads the file. Copying a controller reads it twice
        /// and writes a copy that is read again, so one avatar with seven of them produced
        /// twenty-one red lines with this tool's own call stack underneath.
        ///
        /// The references are in the artist's file and this tool never writes to it. Deleting a
        /// layer or a state in the animator window is enough to leave one behind. But somebody who
        /// downloaded a mesh protection tool, pressed its button and got a console full of red has
        /// no way to know any of that, and the reasonable thing for them to do is uninstall it. So
        /// this counts them first and says so in one line.
        ///
        /// It only measures. If the file is not text - a project serialising assets as binary - no
        /// anchors are found and it says nothing.
        /// </summary>
        private static void ExplainBrokenReferences(string assetPath, string destination)
        {
            string full = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), assetPath);
            if (!System.IO.File.Exists(full)) return;

            string text;
            try { text = System.IO.File.ReadAllText(full); }
            catch { return; }

            var anchors = new HashSet<string>();
            foreach (Match match in Regex.Matches(text, @"^--- !u!\d+ &(-?\d+)", RegexOptions.Multiline))
                anchors.Add(match.Groups[1].Value);
            if (anchors.Count == 0) return;

            // A reference written without a guid names an object in THIS file. One that no anchor
            // in the file defines is a pointer to something that is gone.
            var missing = new HashSet<string>();
            int broken = 0;
            foreach (Match match in Regex.Matches(text, @"\{fileID: (-?\d+)\}"))
            {
                string id = match.Groups[1].Value;
                if (id == "0" || anchors.Contains(id)) continue;
                missing.Add(id);
                broken++;
            }

            if (broken == 0) return;

            // Both file names, because the red lines name two of them and only one is the artist's.
            // A copy is byte for byte, so it inherits the loose ends and Unity complains about the
            // copy as well - under a file name the author has never seen. The first version of this
            // message named only the source, and the author it was written for asked twice whether
            // the batch that followed was normal.
            string copyName = string.IsNullOrEmpty(destination)
                ? null
                : System.IO.Path.GetFileName(destination);

            Debug.LogWarning(
                "[MeshProtect] Unity is about to print a batch of red \"Broken text PPtr ... Local " +
                "file identifier ... doesn't exist!\" lines with this tool's name underneath them. " +
                "They are harmless. Your avatar is fine and there is nothing to fix.\n\n" +
                $"Animator controllers collect loose ends as they are edited - '{System.IO.Path.GetFileName(assetPath)}' " +
                $"has {broken} of them - and Unity mentions each one, in red, every time it reads " +
                "the file. Copying a controller reads it more than once, which is the only reason " +
                "you are seeing them today rather than any other day.\n\n" +
                (copyName == null
                    ? ""
                    : $"Some of those lines will name '{copyName}' instead. That is the protected " +
                      "copy this tool just made; a copy is byte for byte, so it carries the same " +
                      "loose ends. ") +
                "Nothing was changed in your controller: this tool only ever reads it.");
        }

        private static RuntimeAnimatorController FindFXLayer(VRCAvatarDescriptor descriptor)
        {
            if (descriptor.baseAnimationLayers == null) return null;
            foreach (var layer in descriptor.baseAnimationLayers)
                if (layer.type == VRCAvatarDescriptor.AnimLayerType.FX && !layer.isDefault)
                    return layer.animatorController;
            return null;
        }

        /// <summary>
        /// Point the descriptor's FX playable layer at the controller carrying the unlock layers.
        ///
        /// Two failure modes here are worth spelling out, because both ship a broken avatar rather
        /// than raising an error at build time.
        ///
        /// First, customizeAnimationLayers. If it is false - which it is on any avatar whose owner
        /// never ticked "Customize Playable Layers" - VRChat ignores baseAnimationLayers entirely
        /// and loads its stock layers instead. The FX controller would be assigned, saved, and
        /// never used, and the password could never be entered. Setting the array without setting
        /// this flag looks correct in the inspector and fails in game.
        ///
        /// Second, an empty or absent layer array. A descriptor added from a script, or one that
        /// has never been opened in the inspector, arrives with nothing in it. Indexing that threw
        /// a NullReferenceException, which reached the user as "Object reference not set to an
        /// instance of an object" with no indication of what to do.
        /// </summary>
        private static void SetFXLayer(VRCAvatarDescriptor descriptor, RuntimeAnimatorController fx)
        {
            descriptor.customizeAnimationLayers = true;

            var layers = descriptor.baseAnimationLayers;

            if (layers != null)
            {
                for (int i = 0; i < layers.Length; i++)
                {
                    if (layers[i].type != VRCAvatarDescriptor.AnimLayerType.FX) continue;

                    // isDefault must change - it is what makes VRChat use this controller instead
                    // of its own. isEnabled is deliberately left alone: nothing in the SDK reads
                    // it, and an ordinary build ships it false on a working avatar, so setting it
                    // was a change with no purpose. Anything altered without a reason is one more
                    // line to explain away the next time this avatar is compared against a plain
                    // build.
                    layers[i].isDefault = false;
                    layers[i].animatorController = fx;
                    descriptor.baseAnimationLayers = layers;
                    return;
                }
            }

            // No FX slot: rebuild the standard set, leaving everything except FX on its default.
            var standard = new[]
            {
                VRCAvatarDescriptor.AnimLayerType.Base,
                VRCAvatarDescriptor.AnimLayerType.Additive,
                VRCAvatarDescriptor.AnimLayerType.Gesture,
                VRCAvatarDescriptor.AnimLayerType.Action,
                VRCAvatarDescriptor.AnimLayerType.FX
            };

            var rebuilt = new List<VRCAvatarDescriptor.CustomAnimLayer>();
            foreach (var type in standard)
            {
                var existing = layers?.FirstOrDefault(l => l.type == type);
                bool isFX = type == VRCAvatarDescriptor.AnimLayerType.FX;

                rebuilt.Add(new VRCAvatarDescriptor.CustomAnimLayer
                {
                    type = type,
                    isDefault = isFX ? false : (existing?.isDefault ?? true),
                    isEnabled = existing?.isEnabled ?? false,
                    animatorController = isFX ? fx : existing?.animatorController,
                    mask = existing?.mask
                });
            }

            descriptor.baseAnimationLayers = rebuilt.ToArray();
        }

    }
}
#endif
