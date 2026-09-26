#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MeshProtect
{
    /// <summary>
    /// Applies the protection to an avatar IN PLACE.
    ///
    /// This runs during the upload, on the temporary clone the VRChat SDK builds from, so the
    /// avatar in the scene is never touched. That is the whole reason the previous "_Protected
    /// copy" workflow is gone: it produced a second avatar in the scene, which meant the manual
    /// had to keep repeating "edit the source, upload the copy, re-bake after every change, do not
    /// upload the wrong one". Every one of those instructions was a consequence of the copy
    /// existing, and every one of them was a way for a user to ship an unprotected avatar.
    ///
    /// Nothing here writes to the user's project except the temporary asset folder, which the
    /// build processor deletes afterwards. Untick the component and the next upload is plain again.
    /// </summary>
    public static class MeshProtectPipeline
    {
        public class Report
        {
            public int meshCount;
            public int materialCount;
            public int rendererCount;
            public readonly List<string> warnings = new List<string>();
            public MeshProtectRoot.DisplacementMode mode;

            /// <summary>
            /// Did anything actually get protected? False means the avatar was left exactly as it
            /// came in - no mesh displaced, no material swapped, no unlock chain - and the caller
            /// has to know, because every check that runs afterwards is a check on protection that
            /// is not there. Reporting "expected six menu parameters, found none" to somebody whose
            /// avatar simply has no lilToon on it is the tool blaming them for its own decision.
            /// </summary>
            public bool appliedProtection;

            /// <summary>
            /// Renderers this build left exactly as they were. Counted so the summary line can say
            /// so: the reasons are one warning each, and a console with forty lines in it is one an
            /// author scrolls past to the last line - which said "Protected 12 mesh(es)" and
            /// nothing else, so they read it as "done".
            /// </summary>
            public int skippedRenderers;

            /// <summary>
            /// Material slots on KEPT renderers whose conversion was refused - a tessellating
            /// host variant, a graft that is not prepared, a family that came up short. Each one
            /// is a sub-mesh shipping readable and intact next to protected neighbours, which is
            /// exactly the case the summary line used to hide: it said "Protected 12 mesh(es)"
            /// with no qualifier, because skippedRenderers only counts renderers dropped whole.
            /// The author's own ignore list is not counted - that is a choice, not a refusal.
            /// </summary>
            public int unprotectedSubMeshes;

            /// <summary>How many renderers those refused slots sit on.</summary>
            public int unprotectedSubMeshRenderers;

            /// <summary>
            /// Was not protecting the RIGHT answer, rather than a failure to reach one?
            ///
            /// Only the Quest build sets this. Everything else that ends with nothing protected -
            /// no password, no room for the unlock menu, a shader family that did not build, no
            /// lilToon material anywhere - is an author who asked for protection and did not get
            /// it, and the caller stops the upload over it. Letting those through means they find
            /// out in game, having waited out a full build first.
            ///
            /// Quest is different in kind: the avatar CANNOT carry the decode there, the plain
            /// upload is the correct Quest bundle, and refusing it would put back the "untick for
            /// Quest, tick again for PC" dance whose second half people forget - which ships an
            /// unprotected PC avatar.
            /// </summary>
            public bool skipWasCorrect;

            /// <summary>
            /// One line per renderer this build protected: path, the mesh it ships, and what every
            /// material slot ended up as.
            ///
            /// Added for an avatar with several overlapping body renderers, where three rounds of
            /// diagnosis stalled on the same question: WHICH renderer is this screenshot showing,
            /// and what did the build actually do to it? The counts said "70 protected" and the
            /// warnings named the skipped, but the protected majority was invisible - so every
            /// in-game observation had to be matched against guesswork. The roster makes the
            /// report answer that question by itself.
            /// </summary>
            public readonly List<string> roster = new List<string>();
            public double worstRestoreError;
            public double worstBlendShapeError;
            public int blendShapeFramesChecked;
        }

        /// <summary>
        /// Protect every eligible renderer under <paramref name="avatar"/>, then strip the
        /// authoring component. Throws on anything that would produce a broken or unprotected
        /// upload - the caller turns that into a blocked build.
        /// </summary>
        /// <summary>
        /// Convenience for callers that do not need the report when this throws. It does throw -
        /// several guards below stop the build - and the warnings collected before that point go
        /// with the Report this creates. Anything that shows the user a failure should use the
        /// overload below and hand in its own.
        /// </summary>
        public static Report Apply(GameObject avatar, MeshProtectRoot settings, string folder)
            => Apply(avatar, settings, folder, new Report());

        /// <summary>
        /// The caller may hand in the Report so that it still holds whatever was collected if this
        /// throws. Several of the guards below stop the build, and the warnings gathered before
        /// them are often what explains the failure - the renderers that were skipped for not
        /// belonging to the avatar, say, which is why an unexpected mesh is being complained about.
        /// </summary>
        public static Report Apply(GameObject avatar, MeshProtectRoot settings, string folder,
                                   Report report)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            // Quest. The decode lives in a generated lilToon shader and VRChat's Android platform
            // only permits its own mobile shaders, so there the decode never runs while the mesh
            // ships displaced - and the generated materials carry VRCFallback Hidden, which on that
            // platform is the ordinary path rather than a corner case. The avatar would arrive
            // wrong, most likely invisible, and nothing else in this build would notice.
            //
            // So the mesh is left alone, and this used to refuse the upload instead. Refusing was
            // the more dangerous of the two: the instruction that came with it was "untick the
            // component for the Quest upload, tick it again for the PC one", and an author who
            // forgets the second half ships an UNPROTECTED PC avatar - the exact outcome this tool
            // exists to prevent, caused by the tool's own workflow. Skipping here reaches the same
            // Quest bundle that unticking would have produced, with nothing to remember and nothing
            // to forget.
            //
            // NOT COVERED BY ANY TEST, and not for want of trying: activeBuildTarget is read-only,
            // and reaching it means SwitchActiveBuildTarget, which reimports the whole project for
            // Android. Every build the suites run is on Standalone.
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android)
            {
                report.warnings.Add(
                    "This is an Android (Quest) build, so nothing was protected and the avatar is " +
                    "being uploaded exactly as it is. Quest does not allow the custom shader that " +
                    "puts the mesh back, so protecting it would upload geometry nothing ever " +
                    "restores - a broken avatar rather than an unprotected one. The PC upload is " +
                    "protected as usual; nothing needs to be ticked or unticked between the two.");
                report.skipWasCorrect = true;
                return report;
            }

            // No password, so there is nothing to key the protection to, and the caller stops the
            // upload over it: of every way this build has of protecting nothing, this is the one
            // most likely to be an accident and by far the cheapest to fix - one button, on the
            // component already on screen. A password is never invented here, either: an avatar
            // keyed to digits its author has never seen is an avatar its author cannot unlock.
            //
            // AFTER the Quest check on purpose. On Android nothing is protected whatever the
            // password says, so stopping an Android build over a missing one would refuse an
            // upload that was never going to be affected by it - and the fix it asks for would
            // change nothing about the bundle.
            if (!settings.HasKey)
            {
                report.warnings.Add(
                    "This avatar has no password yet, so there is nothing to key the protection " +
                    "to and nothing was protected. Select it and press 'Generate Password' on the " +
                    "Mesh Protect Root component, then upload again.");
                return report;
            }

            var variant = settings.variant;
            uint key = MeshProtectCipher.PackDigits(settings.keyDigits, variant);
            uint mac = MeshProtectCipher.Mac(key, variant);

            // The shader family is generated when the password is, not here: importing shaders
            // during a build would force an AssetDatabase refresh in the middle of the upload.
            if (Shader.Find(variant.shaderName + "/lilToon") == null)
            {
                report.warnings.Add(
                    $"This avatar's shader family '{variant.shaderName}' is missing from the " +
                    "project, so nothing could be protected. Press 'Rebuild Shader' on the Mesh " +
                    "Protect Root component, then upload again.");
                return report;
            }

            // The family exists, but is all of it there? A short family does not stop anything - each
            // material that needs a missing shader is simply left unprotected - and that is exactly
            // why it needs saying here. Otherwise the author reads a list of individually
            // unremarkable warnings about materials they never touched, with nothing to say the
            // cause is one and the same and is one button away.
            //
            // Said before the bake, because by the time the per-material warnings appear the reader
            // has already formed a theory about that material.
            if (!MeshProtectShaderGen.FamilyIsComplete(variant, out int haveShaders, out int wantShaders))
            {
                report.warnings.Add(
                    $"THIS AVATAR'S SHADER FAMILY '{variant.shaderName}' IS INCOMPLETE: " +
                    $"{haveShaders} usable shaders where {wantShaders} are expected. Any material " +
                    "needing one of the missing ones cannot be moved onto the family and ships " +
                    "UNPROTECTED - if several materials are named below, this is the reason for " +
                    "all of them. Press 'Rebuild Shader' on the Mesh Protect Root component and " +
                    "watch the Console for a shader compile error, which is what a short family " +
                    "means. It usually follows a lilToon upgrade.");
            }

            // The same question for the shaders that are not lilToon, and asked here for the same
            // reason: an author who added a Poiyomi outfit after generating their password would
            // otherwise get one warning per material, none of them saying the cause is shared and
            // is one button away.
            var missingGrafts = MeshProtectForeignShader.MissingGrafts(settings, variant, avatar);
            if (missingGrafts.Count > 0)
            {
                report.warnings.Add(
                    $"{missingGrafts.Count} shader(s) that are not lilToon have no prepared copy " +
                    "carrying the decode, so every material using them ships UNPROTECTED: " +
                    string.Join(", ", missingGrafts.Take(5)) +
                    (missingGrafts.Count > 5 ? ", ..." : "") +
                    ". Press 'Rebuild Shader' on the Mesh Protect Root component and upload again.");
            }

            var missingHosts = MeshProtectLilHost.MissingMerged(settings, variant, avatar);
            if (missingHosts.Count > 0)
            {
                report.warnings.Add(
                    $"{missingHosts.Count} lilToon custom family(ies) on this avatar have no " +
                    "prepared merged copy carrying the decode, so every material using them ships " +
                    "UNPROTECTED: " + string.Join(", ", missingHosts.Take(5)) +
                    (missingHosts.Count > 5 ? ", ..." : "") +
                    ". Press 'Rebuild Shader' on the Mesh Protect Root component and upload again.");
            }

            WarnAboutInvisibleMaterialsThatAreNot(settings, report);

            // Verify the GENERATED HLSL against the C# cipher before baking anything against it.
            // The shader is emitted per avatar, so this is the only thing standing between an
            // emitter bug and an avatar nobody can restore.
            var gpu = MeshProtectGpuCheck.Run(variant);
            if (gpu.ran && !gpu.Passed)
                throw new InvalidOperationException(
                    $"The generated shader does not match the C# cipher (worst error " +
                    $"{gpu.worstError:E3}, tolerance {gpu.tolerance:E3}). This is a bug in the shader " +
                    "emitter; the upload was stopped rather than ship an avatar that cannot be restored.");
            if (!gpu.ran)
                report.warnings.Add("Could not verify the generated shader on the GPU: " + gpu.skipReason);

            // A password shorter than six digits rests on bits the generator's acceptance test did
            // not always measure: it used to sample digits 1-8 only, so the top bit of every
            // position - the one that says "nobody entered this" - went through the whole gate
            // stuck at zero. Variants made before that was fixed are still perfectly valid and are
            // sitting on people's components; the format was deliberately NOT bumped, because that
            // would have invalidated every one of them to fix a case none of them can hit.
            //
            // So the one combination where the difference can bite - an old variant and a short
            // password - is measured here instead. A warning, not a refusal: the avatar works, and
            // what is at risk is that some other password may also open it.
            //
            // Seeded from the variant so the answer is the same on every build of the same avatar,
            // rather than a warning that comes and goes.
            if (MeshProtectRoot.TypedLength(settings.keyDigits) < MeshProtectRoot.PasswordLength &&
                !MeshProtectVariantGenerator.Measure(variant, new System.Random(variant.macSalt),
                                                     out string weakness))
            {
                report.warnings.Add(
                    "This avatar's algorithm was generated before the acceptance test covered the " +
                    "'not entered' pattern, and it does not pass that test now (" + weakness + "). " +
                    "A password shorter than six digits rests on exactly that pattern, so another " +
                    "password may also open this avatar. Press New Protection on the component to " +
                    "roll a new algorithm - your password is kept - or use a six digit password.");
            }

            var renderers = ResolveRenderers(settings, avatar, report);
            if (renderers.Count == 0)
            {
                report.warnings.Add(
                    "Nothing on this avatar can be protected: no renderer uses a material this " +
                    "tool can carry the decode in, or Target Renderers points at none. That means " +
                    "lilToon, or one of the families it can graft onto (" +
                    string.Join(", ", MeshProtectForeignShader.Recipes.Select(r => r.displayName)) +
                    "). If this avatar is none of those, untick the Mesh Protect Root component - " +
                    "it will upload as it always has, and it will stop asking.");
                return report;
            }

            report.rendererCount = renderers.Count;

            // Before anything is displaced. The unlock chain is built after the meshes are baked,
            // so an avatar it cannot be built on - a full expression parameter budget, an FX layer
            // holding something other than a controller - used to be found out when the mesh was
            // already scrambled, and the answer left was to throw from the middle of the bake.
            // Asked here, the avatar is still untouched when the build stops, so nothing has to be
            // undone and the message can be about the budget rather than about a failed bake.
#if LILMP_VRCSDK3_AVATARS
            if (!MeshProtectAnimator.CanGenerate(avatar, settings, out string chainWhy))
            {
                report.warnings.Add(
                    $"The unlock chain cannot be built on this avatar, so nothing was protected: " +
                    $"{chainWhy}. (Nothing to do with menu space - a full root menu is handled by " +
                    "moving the avatar's own menu one level down, inside the build only.)");
                return report;
            }
#endif

            var materialCache = new Dictionary<Material, Material>();
            var meshCache = new Dictionary<string, Mesh>();

            // Pass 1: convert materials and work out which sub-meshes end up protected.
            //
            // Nothing is assigned to a renderer in this pass. The leak check below can still take a
            // renderer back out, and a renderer carrying the protect shader over a mesh that was
            // never displaced is not "unprotected", it is broken: the decode reads its amplitude
            // out of UV6, and on a mesh this build never wrote, UV6 holds whatever the artist put
            // there. So the materials go on only once it is settled that the mesh will be baked.
            var candidates = new List<(Renderer renderer, Material[] materials, bool[] protectedSubMesh)>();
            var refusedOnKept = new Dictionary<Renderer, int>();

            foreach (var renderer in renderers)
            {
                var sourceMaterials = renderer.sharedMaterials;
                var newMaterials = new Material[sourceMaterials.Length];
                var protectedSubMesh = new bool[sourceMaterials.Length];

                int refusedSlots = 0;
                for (int slot = 0; slot < sourceMaterials.Length; slot++)
                {
                    var source = sourceMaterials[slot];
                    newMaterials[slot] = source;
                    if (source == null) continue;

                    if (settings.ignoredMaterials.Contains(source))
                    {
                        report.warnings.Add($"Skipped material '{source.name}' (in the ignore list).");
                        continue;
                    }

                    if (!materialCache.TryGetValue(source, out var converted))
                    {
                        converted = ConvertMaterial(source, settings, variant, mac, folder, report);
                        materialCache[source] = converted;   // may be null when not convertible
                    }

                    if (converted != null)
                    {
                        newMaterials[slot] = converted;
                        protectedSubMesh[slot] = true;
                    }
                    else
                    {
                        refusedSlots++;
                    }
                }

                if (refusedSlots > 0 && protectedSubMesh.Any(p => p))
                    refusedOnKept[renderer] = refusedSlots;

                if (!protectedSubMesh.Any(p => p))
                {
                    report.warnings.Add(
                        $"Renderer '{renderer.name}' has no protectable material, mesh left " +
                        "untouched. It will be visible while the avatar is locked.");
                    report.skippedRenderers++;
                    continue;
                }

                candidates.Add((renderer, newMaterials, protectedSubMesh));
            }

            DropRenderersThatCannotBeProtected(candidates, variant, report);
#if LILMP_VRCSDK3_AVATARS
            DropRenderersWithUnsafeMaterialSwaps(avatar, settings, candidates,
                                                materialCache, variant, mac, folder, report);
#endif
            DropRenderersWhoseMeshLeaks(avatar, candidates, report);

            // Counted over the SURVIVORS only. A renderer the Drop passes removed whole is
            // skippedRenderers; counting its refused slots too would put one renderer in both
            // summary categories, and the sub-mesh line would claim a refusal "beside protected
            // neighbours" on a renderer where nothing is protected at all.
            foreach (var c in candidates)
                if (refusedOnKept.TryGetValue(c.renderer, out int refused))
                {
                    report.unprotectedSubMeshes += refused;
                    report.unprotectedSubMeshRenderers++;
                }

            var plan = new List<(Renderer renderer, bool[] protectedSubMesh)>();
            foreach (var (renderer, materials, protectedSubMesh) in candidates)
            {
                renderer.sharedMaterials = materials;
                plan.Add((renderer, protectedSubMesh));
            }

            // What was protected, not what was looked at. The upload line reports this number, and
            // "across 12 renderer(s)" while nine of them were skipped is the tool telling somebody
            // their avatar is protected when a warning above says it partly is not.
            report.rendererCount = plan.Count;

            // Nothing at all came out protected. Every individual reason for that is a warning
            // above - somebody else's shader, the ignore list, a family missing a shader - and any
            // one of them on its own is fine. All of them at once is not: the avatar uploads
            // looking exactly like an unprotected one, which is what it would be, while the author
            // believes the opposite. That is the failure this refuses, and it is safe to refuse
            // here because nothing has been displaced - the avatar in the scene is untouched and
            // unticking the component uploads it as it stands.
            if (plan.Count == 0)
            {
                report.warnings.Add(
                    $"None of this avatar's {renderers.Count} renderer(s) could be protected - " +
                    "the lines above say why for each one.");
                return report;
            }

            // The displacement mode is a material property, so it must be identical across the
            // whole avatar. Decide it once, here, from every mesh that will actually be baked - a
            // per-mesh fallback would bake with one formula and decode with the other.
            var effectiveMode = ResolveMode(settings, plan.Select(p => p.renderer), report);
            report.mode = effectiveMode;

            // Pass 2: bake.
            var bakedSources = new HashSet<Mesh>();
            foreach (var (renderer, protectedSubMesh) in plan)
                BakeRendererMesh(renderer, settings, effectiveMode, key, variant,
                                 protectedSubMesh, meshCache, bakedSources, folder, report);

            GuardAgainstClearCopies(avatar, bakedSources);
            ReportDisplacedWithoutDecode(plan, variant, report);

            // After the bake, so mesh names are the shipped ones and every slot holds its final
            // material.
            foreach (var (renderer, protectedSubMesh) in plan)
            {
                if (renderer == null) continue;
                var mesh = GetMesh(renderer);
                var materials = renderer.sharedMaterials;

                var slots = new StringBuilder();
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    bool marked = slot < protectedSubMesh.Length && protectedSubMesh[slot];
                    string name = materials[slot] == null ? "<none>" : materials[slot].name;
                    slots.Append($" [{slot} {(marked ? "protected" : "left alone")}: {name}]");
                }
                if (materials.Length > (mesh == null ? 0 : mesh.subMeshCount))
                    slots.Append("  (more materials than sub-meshes: extras re-draw the last one)");

                report.roster.Add(
                    $"{AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform)}" +
                    $" | mesh '{(mesh == null ? "<none>" : mesh.name)}'" + slots);
            }

            foreach (var material in materialCache.Values)
            {
                if (material == null) continue;
                material.SetFloat(variant.modeProperty,
                    effectiveMode == MeshProtectRoot.DisplacementMode.TangentSpace ? 1f : 0f);
            }

            report.meshCount = meshCache.Count;

            // Counted off what the avatar actually wears, not off the cache. The cache holds a
            // converted copy for every material this build looked at, including ones whose only
            // renderer was dropped afterwards - so reporting its size tells the author more
            // materials were protected than were, on exactly the avatars where a warning above
            // says some were not.
            report.materialCount = plan
                .SelectMany(p => p.renderer.sharedMaterials)
                .Where(m => IsProtectShader(m, variant.shaderName))
                .Distinct()
                .Count();

            var protectedRenderers = renderers
                .Where(r => r.sharedMaterials.Any(m => IsProtectShader(m, variant.shaderName)))
                .ToList();

            if (protectedRenderers.Count == 0)
                throw new InvalidOperationException(
                    "Meshes were displaced but no renderer ended up wearing the decode shader. " +
                    "That combination cannot be uploaded - the avatar would arrive scrambled - and " +
                    "it means this tool has a bug rather than that anything is wrong with the " +
                    "avatar. Please report it.");

            report.appliedProtection = true;

#if LILMP_VRCSDK3_AVATARS
            MeshProtectAnimator.Generate(avatar, settings, protectedRenderers, folder);

            // After Generate, so the unlock layers exist and can be left alone by name.
            if (settings.obfuscateAnimatorNames)
            {
                var descriptor = avatar.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();

                // Decided before anything is swapped in, because it needs to read the avatar with
                // its ORIGINAL parameter names - the prepared copies already carry the new ones.
                ParameterDecision parameters;
                bool objectsAllowed = false;
                bool copiesValid = MeshProtectControllers.ValidatePreparedCopies(settings, out string copiesInvalid);
                bool hasApplicableCopies = copiesValid &&
                    MeshProtectControllers.WouldAdopt(descriptor, settings).Count > 0;
                if (hasApplicableCopies)
                {
                    parameters = ResolveParameters(avatar, descriptor, settings, report);
                    parameters.objectMap = ResolveObjectNames(descriptor, settings, report,
                                                              out objectsAllowed);
                }
                else
                {
                    // Only adopted copies require their recorded maps. Without any, derive both
                    // maps from this build, including parameters and objects added by VRCFury.
                    parameters = new ParameterDecision();
                    if (!copiesValid)
                        report.warnings.Add(
                            "The prepared controller copies were dropped from this upload because " +
                            copiesInvalid + ". Your current controllers are used instead. " +
                            "Press Prepare Controllers to refresh the copies.");
                    else if (settings.preparedControllers.Count > 0)
                        report.warnings.Add(
                            "No prepared controller matches this build's animation layers. " +
                            "The current controllers are used, and safe name maps are recalculated " +
                            "after the other avatar tools have run.");
                }

                // Either decision saying no takes the copies down with it. They carry both kinds of
                // rename - parameters inside them, object names in their animation paths - so half
                // adopting is one half of the avatar disagreeing with the other.
                //
                // But dropping the COPIES no longer means renaming NOTHING. That used to be the
                // rule, and on a VRCFury avatar it fired every time: VRCFury replaces the playable
                // layer controllers during the build, the stored maps were made against the avatar
                // before that, so one mismatched name emptied both maps - including every rename
                // this build's own FX copy could have carried on its own. The friend-shaped report
                // of that was "hierarchy and controller obfuscation just disappeared".
                //
                // So on failure, both maps are REBUILT for the world that will actually ship: no
                // copies adopted, nothing replaced, the only rewritable controller this build's own
                // FX. Both sides rebuild even when only one was broken, because each stored map was
                // validated against a survey that assumed the copies WOULD ship - a stored object
                // map can rename a path some un-adopted controller still animates.
                if (!objectsAllowed || !parameters.adoptAllowed)
                {
                    parameters.adoptAllowed = false;
                    parameters.copyBlendTrees = settings.copySeparateBlendTrees;
                    parameters.expressionParameters = settings.obfuscateExpressionParameters;
                    parameters.map = FallbackParameterMap(descriptor, settings, parameters);
                    parameters.objectMap = FallbackObjectMap(descriptor, settings);

                    if ((settings.preparedControllers.Count > 0 || !copiesValid) &&
                        (parameters.map.Count > 0 || parameters.objectMap.Count > 0))
                        report.warnings.Add(
                            "Safe names were recalculated for this build without prepared copies: " +
                            $"{MeshProtectParameters.DistinctParameters(parameters.map)} " +
                            $"parameter(s) and {parameters.objectMap.Count} object name(s). Names " +
                            "your other layers still use keep their originals.");
                }

                // Point this build at the copies of the avatar's own controllers, if any were
                // prepared. This is a reference assignment and nothing else - the copies were made
                // and renamed before the build, because making them during it is what broke pose
                // switching on a real avatar for reasons that are still not understood.
                var adopted = parameters.adoptAllowed
                    ? MeshProtectControllers.Adopt(descriptor, settings, report)
                    : new List<string>();

                if (adopted.Count > 0)
                    report.warnings.Add($"This upload uses {adopted.Count} prepared controller(s), " +
                                        "renamed before the build. Your own controllers were not " +
                                        "touched.");

                // Before the rename pass so the arrivals are renamed with everything else, and
                // before the parameter rewrite so the parameters inside them can be reached at all.
                // Only the FX controller: the prepared copies had this done when they were made.
                if (parameters.copyBlendTrees)
                {
                    int trees = MeshProtectParameters.CopySeparateBlendTrees(FindFX(descriptor));
                    if (trees > 0)
                        report.warnings.Add($"Copied {trees} blend tree(s) stored outside the FX " +
                                            "controller into this upload's copy of it. Your own " +
                                            "files were not modified.");
                }

                int renamed = MeshProtectObfuscator.Run(descriptor, variant, folder, adopted);

                // After the rename pass, which is what clones the project's clips into the
                // controller this build owns. A clip can drive a parameter, and only a clone of it
                // may be rewritten.
                int rewrites = ApplyParameterNames(avatar, descriptor, folder, variant, parameters);
                int objectRewrites = ApplyObjectNames(avatar, descriptor, folder, parameters.objectMap);

                if (renamed > 0)
                    report.warnings.Add($"Renamed {renamed} animator object(s) in the upload.");

                if (parameters.objectMap.Count > 0)
                    report.warnings.Add($"Renamed {parameters.objectMap.Count} object name(s) in " +
                                        $"the upload across {objectRewrites} place(s). The skeleton, " +
                                        "the object MMD worlds drive the face through, and anything " +
                                        "an animation this upload does not own refers to kept their " +
                                        "names.");

                if (parameters.map.Count > 0)
                    report.warnings.Add(
                        $"Renamed {MeshProtectParameters.DistinctParameters(parameters.map)} " +
                        $"parameter(s) across {rewrites} reference(s). Every other parameter kept " +
                        "its name; the report on the component says which and why.");
            }

            // Last, after every pass that clones or renames clips, so it reads the clips this
            // build actually ships and the paths they actually bind.
            RewriteAnimatedMaterialSwaps(avatar, settings, plan, materialCache, variant, mac,
                                         effectiveMode, folder, report);
#else
            report.warnings.Add(
                "VRChat Avatars SDK not found, so parameters, menu and FX layers were not generated. " +
                "The meshes and materials are baked, but nothing can unlock them.");
#endif

            // The authoring component serialises the password. Strip it last, once everything that
            // needed it has run.
            foreach (var stray in avatar.GetComponentsInChildren<MeshProtectRoot>(true))
                Object.DestroyImmediate(stray);

            return report;
        }

#if LILMP_VRCSDK3_AVATARS
        // ------------------------------------------------------------------ animated swaps

        /// <summary>
        /// Check animated material states before changing any mesh. A renderer whose animation
        /// cannot carry the decode stays entirely original, including its material keyframes.
        /// Run before the shared-mesh leak check: a renderer removed here may share its mesh with
        /// a remaining candidate, which must then be left original too.
        /// </summary>
        private static void DropRenderersWithUnsafeMaterialSwaps(
            GameObject avatar, MeshProtectRoot settings,
            List<(Renderer renderer, Material[] materials, bool[] protectedSubMesh)> candidates,
            Dictionary<Material, Material> materialCache, MeshProtectVariant variant, uint mac,
            string folder, Report report)
        {
            var descriptor = avatar.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null || candidates.Count == 0) return;

            var byPath = candidates.GroupBy(c =>
                AnimationUtility.CalculateTransformPath(c.renderer.transform, avatar.transform))
                .ToDictionary(g => g.Key, g => g.ToArray());
            var rejected = new Dictionary<Renderer, string>();
            var layers = new List<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.CustomAnimLayer>();
            if (descriptor.baseAnimationLayers != null) layers.AddRange(descriptor.baseAnimationLayers);
            if (descriptor.specialAnimationLayers != null) layers.AddRange(descriptor.specialAnimationLayers);

            var sources = new List<(string label, Transform root, bool canRewrite, AnimationClip[] clips)>();
            foreach (var layer in layers)
            {
                if (layer.isDefault || layer.animatorController == null) continue;
                bool canRewrite = layer.type ==
                    VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.FX;
                if (canRewrite && layer.animatorController is
                    UnityEditor.Animations.AnimatorController controller)
                {
                    // Synced overrides live on the layer, not in state.motion. The final rewrite
                    // changes only state motions, even when the same clip occurs in both places.
                    // An AnimatorOverrideController is different: its effective clips are all
                    // replaced through GetOverrides/ApplyOverrides by that rewrite.
                    var overrides = SyncedOverrideClips(controller);
                    if (overrides.Length > 0)
                        sources.Add(("synced layer overrides in the FX controller", avatar.transform,
                                     false, overrides));
                }
                sources.Add(($"the {layer.type} layer", avatar.transform, canRewrite,
                             layer.animatorController.animationClips));
            }

            // Child animators use paths relative to their own object, and keep their original
            // clips. Treat their swaps like other animation layers we cannot rewrite safely.
            foreach (var animator in avatar.GetComponentsInChildren<Animator>(true))
            {
                if (animator.transform == avatar.transform || animator.runtimeAnimatorController == null)
                    continue;
                sources.Add(($"Animator '{animator.name}'", animator.transform, false,
                    animator.runtimeAnimatorController.animationClips));
            }
            foreach (var animation in avatar.GetComponentsInChildren<Animation>(true))
            {
                var clips = new List<AnimationClip> { animation.clip };
                foreach (AnimationState state in animation) clips.Add(state.clip);
                sources.Add(($"Animation '{animation.name}'", animation.transform, false, clips.ToArray()));
            }

            foreach (var source in sources)
            {
                string prefix = AnimationUtility.CalculateTransformPath(source.root, avatar.transform);
                foreach (var clip in source.clips.Where(c => c != null).Distinct())
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (binding.type != typeof(SkinnedMeshRenderer) && binding.type != typeof(MeshRenderer))
                        continue;
                    string path = string.IsNullOrEmpty(prefix) ? binding.path :
                        string.IsNullOrEmpty(binding.path) ? prefix : prefix + "/" + binding.path;
                    if (!binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal) ||
                        !byPath.TryGetValue(path, out var affected)) continue;

                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (keys == null) continue;
                    foreach (var candidate in affected)
                    {
                        var renderer = candidate.renderer;
                        if (rejected.ContainsKey(renderer) || !binding.type.IsInstanceOfType(renderer))
                            continue;
                        var mask = candidate.protectedSubMesh;
                        if (mask.Length == 0) continue;
                        int slot = Mathf.Clamp(ParseMaterialSlot(binding.propertyName), 0, mask.Length - 1);
                        var original = renderer.sharedMaterials;
                        bool displaced = mask[slot] || (slot < original.Length && original[slot] != null &&
                            settings.invisibleMaterials.Contains(original[slot]));
                        if (!displaced) continue;

                        foreach (var keyframe in keys)
                        {
                            var material = keyframe.value as Material;
                            if (material == null || settings.invisibleMaterials.Contains(material) ||
                                IsProtectShader(material, variant.shaderName)) continue;

                            string why = null;
                            if (settings.ignoredMaterials.Contains(material))
                                why = "the material is in Ignored Materials";
                            else if (!source.canRewrite)
                                why = "material swaps in this animation source cannot be rewritten";
                            else
                            {
                                if (!materialCache.TryGetValue(material, out var converted))
                                {
                                    converted = ConvertMaterial(material, settings, variant, mac, folder, report);
                                    materialCache[material] = converted;
                                }
                                if (converted == null) why = "the material cannot carry the decode";
                            }
                            if (why == null) continue;
                            rejected[renderer] = $"animation '{clip.name}' in {source.label} " +
                                $"uses '{material.name}' on material slot {slot}: {why}";
                            break;
                        }
                    }
                }
            }

            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                var renderer = candidates[i].renderer;
                if (!rejected.TryGetValue(renderer, out string reason)) continue;
                report.warnings.Add(
                    $"Renderer '{renderer.name}' was left UNPROTECTED and unchanged because {reason}. " +
                    "Its material animations remain usable, and it stays visible while locked. " +
                    "Use supported materials in ordinary FX states to protect this renderer.");
                report.skippedRenderers++;
                candidates.RemoveAt(i);
            }
        }

        private static AnimationClip[] SyncedOverrideClips(
            UnityEditor.Animations.AnimatorController controller)
        {
            var pending = new Stack<Motion>();
            foreach (var layer in controller.layers)
                foreach (var state in MeshProtectParameters.SyncedLayerStates(controller, layer))
                    pending.Push(layer.GetOverrideMotion(state));

            var visited = new HashSet<Motion>();
            var clips = new HashSet<AnimationClip>();
            while (pending.Count > 0)
            {
                var motion = pending.Pop();
                if (motion == null || !visited.Add(motion)) continue;
                if (motion is AnimationClip clip) clips.Add(clip);
                else if (motion is UnityEditor.Animations.BlendTree tree)
                    foreach (var child in tree.children) pending.Push(child.motion);
            }
            return clips.ToArray();
        }

        /// <summary>
        /// Re-point material references inside the FX layer's animation clips at this build's
        /// decode copies.
        ///
        /// This closes the hole a real avatar shipped through. A skin-toggle avatar keeps its
        /// alternate skins as material-swap animations: a clip binds m_Materials.Array.data[N] on
        /// the body renderer and its keyframe references the material ASSET. The build only ever
        /// converted what sat in renderer slots, so the moment that layer played, the animator
        /// wrote the ORIGINAL material back onto a mesh whose vertices this build had displaced.
        /// From there the body renders as noise, the right password changes nothing - the swapped
        /// material has no decode on it - and every number in the upload report says the avatar
        /// was protected, because at build time it was. Nothing in the editor reproduces it,
        /// because nothing in a test scene plays the FX controller.
        ///
        /// So: every keyframe in the shipped FX that would put a material onto a DISPLACED
        /// sub-mesh is re-pointed at the decode copy of that material, converting it first if it
        /// never sat in a slot. Clips are cloned before they are touched unless this build already
        /// owns them - the ones in the project stay exactly as they are. The preflight leaves
        /// renderers with incompatible swaps original. Finding one here means the preflight and
        /// the final controller disagree; stop rather than ship a displaced mesh without decode.
        ///
        /// Only the FX layer is rewritten: it is the layer this build owns a copy of, and it is
        /// where material swaps live. Swaps found anywhere else are warned about instead.
        /// </summary>
        private static void RewriteAnimatedMaterialSwaps(GameObject avatar, MeshProtectRoot settings,
                                                         List<(Renderer renderer, bool[] protectedSubMesh)> plan,
                                                         Dictionary<Material, Material> materialCache,
                                                         MeshProtectVariant variant, uint mac,
                                                         MeshProtectRoot.DisplacementMode effectiveMode,
                                                         string folder, Report report)
        {
            var descriptor = avatar.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null || descriptor.baseAnimationLayers == null) return;

            // Which paths carry displaced sub-meshes. Computed here, after the object-rename pass,
            // so the paths match what the shipped clips bind.
            var displaced = new Dictionary<string, bool[]>();
            foreach (var (renderer, mask) in plan)
            {
                if (renderer == null) continue;

                // Vouched-invisible slots displace too, so a clip swapping a CONVERTIBLE material
                // into one must be re-pointed like any other. Unconverted slots still wear their
                // original materials at this point, so the list matches what the renderer holds.
                var slotMask = mask;
                if (settings.invisibleMaterials.Count > 0)
                {
                    var worn = renderer.sharedMaterials;
                    slotMask = (bool[])mask.Clone();
                    for (int slot = 0; slot < slotMask.Length && slot < worn.Length; slot++)
                        if (!slotMask[slot] && worn[slot] != null &&
                            settings.invisibleMaterials.Contains(worn[slot]))
                            slotMask[slot] = true;
                }

                displaced[AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform)] = slotMask;
            }
            if (displaced.Count == 0) return;

            int rewrote = 0;
            var rewritten = new Dictionary<Motion, Motion>();
            var visiting = new HashSet<Motion>();

            bool BuildOwns(Object asset)
            {
                string path = AssetDatabase.GetAssetPath(asset);
                return !string.IsNullOrEmpty(path) &&
                       path.StartsWith(folder + "/", StringComparison.Ordinal);
            }

            // Material safety cannot depend on the optional name-obfuscation/tree-copy switches.
            // Copy only external trees whose child references change, and share those copies
            // across states. The original trees must never point into this build's temp folder.
            Motion RewriteMotion(Motion motion)
            {
                if (motion == null) return null;
                if (rewritten.TryGetValue(motion, out var cached)) return cached;
                if (!visiting.Add(motion))
                    throw new InvalidOperationException(
                        $"Blend tree '{motion.name}' contains a cycle. Material swaps cannot be " +
                        "rewritten safely; remove the circular motion reference before uploading.");

                try
                {
                    Motion result = motion;
                    if (motion is AnimationClip clip)
                    {
                        var replacement = RewriteClipMaterials(clip, settings, displaced,
                                                               materialCache, variant, mac,
                                                               effectiveMode, folder, report);
                        if (replacement != null) { result = replacement; rewrote++; }
                    }
                    else if (motion is UnityEditor.Animations.BlendTree tree)
                    {
                        var children = tree.children;
                        bool changed = false;
                        for (int i = 0; i < children.Length; i++)
                        {
                            var replacement = RewriteMotion(children[i].motion);
                            if (replacement == children[i].motion) continue;
                            children[i].motion = replacement;
                            changed = true;
                        }

                        if (changed)
                        {
                            var target = tree;
                            if (!BuildOwns(tree))
                            {
                                target = new UnityEditor.Animations.BlendTree();
                                EditorUtility.CopySerialized(tree, target);
                                AssetDatabase.CreateAsset(target, AssetDatabase.GenerateUniqueAssetPath(
                                    $"{folder}/{GeneratedName(tree.name, variant)}.asset"));
                            }
                            target.children = children;
                            EditorUtility.SetDirty(target);
                            result = target;
                        }
                    }
                    rewritten[motion] = result;
                    return result;
                }
                finally { visiting.Remove(motion); }
            }

            foreach (var layer in descriptor.baseAnimationLayers)
            {
                if (layer.animatorController == null || layer.isDefault) continue;

                if (layer.type != VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.FX)
                {
                    WarnAboutForeignLayerSwaps(layer.animatorController, layer.type.ToString(),
                                               displaced, variant, report);
                    continue;
                }

                // The FX slot may hold our override-chain copy; the clip that PLAYS for a slot is
                // the override value when there is one, the base clip otherwise. Expressing every
                // rewrite as an override on our outermost copy leaves the base controller's clip
                // identities alone, which is what keeps the author's own overrides matching.
                if (layer.animatorController is AnimatorOverrideController over)
                {
                    var slots = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                    over.GetOverrides(slots);
                    bool changed = false;
                    for (int i = 0; i < slots.Count; i++)
                    {
                        var playing = slots[i].Value != null ? slots[i].Value : slots[i].Key;
                        var replacement = RewriteMotion(playing) as AnimationClip;
                        if (replacement == playing) continue;
                        slots[i] = new KeyValuePair<AnimationClip, AnimationClip>(slots[i].Key, replacement);
                        changed = true;
                    }
                    if (changed) over.ApplyOverrides(slots);
                    continue;
                }

                var fx = layer.animatorController as UnityEditor.Animations.AnimatorController;
                if (fx == null) continue;

                foreach (var controllerLayer in fx.layers)
                {
                    foreach (var state in AllStates(controllerLayer.stateMachine))
                    {
                        if (state == null) continue;
                        var replacement = RewriteMotion(state.motion);
                        if (replacement == state.motion) continue;
                        if (!BuildOwns(state))
                            throw new InvalidOperationException(
                                $"FX state '{state.name}' lives outside this build's controller copy. " +
                                "Its material swap needs a different motion, but changing the shared " +
                                "state would modify your project. Store the state in its controller " +
                                "before uploading.");
                        state.motion = replacement;
                        EditorUtility.SetDirty(state);
                    }
                }
            }

            if (rewrote > 0)
                report.warnings.Add(
                    $"Re-pointed {rewrote} material swap animation(s) at this build's decode " +
                    "materials, so skin and outfit toggles keep working on the protected mesh. " +
                    "Your own animation files were not modified - the upload carries copies.");
        }

        private static IEnumerable<UnityEditor.Animations.AnimatorState> AllStates(
            UnityEditor.Animations.AnimatorStateMachine machine)
        {
            if (machine == null) yield break;
            foreach (var child in machine.states) yield return child.state;
            foreach (var child in machine.stateMachines)
                foreach (var state in AllStates(child.stateMachine)) yield return state;
        }

        /// <summary>
        /// Returns the edited clip (which may be the input) or null when no keyframe changed.
        /// External clips are cloned; only clips in this build's folder are edited in place.
        /// </summary>
        private static AnimationClip RewriteClipMaterials(AnimationClip clip, MeshProtectRoot settings,
                                                          Dictionary<string, bool[]> displaced,
                                                          Dictionary<Material, Material> materialCache,
                                                          MeshProtectVariant variant, uint mac,
                                                          MeshProtectRoot.DisplacementMode effectiveMode,
                                                          string folder, Report report)
        {
            if (clip == null) return null;

            var edits = new List<(EditorCurveBinding binding, ObjectReferenceKeyframe[] keys)>();

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (binding.type != typeof(SkinnedMeshRenderer) && binding.type != typeof(MeshRenderer))
                    continue;
                if (!binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal))
                    continue;
                if (!displaced.TryGetValue(binding.path, out var mask) || mask.Length == 0)
                    continue;

                // Same clamp as BuildSkipMask: extra material slots past the sub-mesh count render
                // the last sub-mesh again, so they get the last slot's verdict.
                int slot = ParseMaterialSlot(binding.propertyName);
                bool hitsDisplaced = mask[slot < 0 ? 0 : Mathf.Min(slot, mask.Length - 1)];
                if (!hitsDisplaced) continue;

                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keys == null) continue;

                bool changed = false;
                for (int i = 0; i < keys.Length; i++)
                {
                    var material = keys[i].value as Material;
                    if (material == null) continue;
                    if (IsProtectShader(material, variant.shaderName)) continue;

                    // The author vouched this one draws nothing, so a swap that brings it in
                    // changes nothing visible either - locked or unlocked.
                    if (settings.invisibleMaterials.Contains(material)) continue;

                    if (settings.ignoredMaterials.Contains(material))
                    {
                        throw new InvalidOperationException(
                            $"Animation '{clip.name}' swaps ignored material '{material.name}' onto " +
                            $"'{binding.path}' after material preflight. The build was stopped " +
                            "because this displaced mesh would have no decode when the animation plays.");
                    }

                    if (!materialCache.TryGetValue(material, out var converted))
                    {
                        converted = ConvertMaterial(material, settings, variant, mac, folder, report);
                        materialCache[material] = converted;
                        if (converted != null)
                            converted.SetFloat(variant.modeProperty,
                                effectiveMode == MeshProtectRoot.DisplacementMode.TangentSpace ? 1f : 0f);
                    }

                    if (converted == null)
                    {
                        throw new InvalidOperationException(
                            $"Animation '{clip.name}' swaps '{material.name}' onto " +
                            $"'{binding.path}', whose mesh is displaced, and that material cannot " +
                            "carry the decode after material preflight. The build was stopped " +
                            "before this broken material swap could be uploaded.");
                    }

                    keys[i].value = converted;
                    changed = true;
                }

                if (changed) edits.Add((binding, keys));
            }

            if (edits.Count == 0) return null;

            // Clone unless this build already owns the clip - the object-rename pass clones project
            // clips into the temp folder, and those may be edited in place.
            var target = clip;
            string path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path) || !path.StartsWith(folder + "/", StringComparison.Ordinal))
            {
                target = Object.Instantiate(clip);
                target.name = clip.name;
                AssetDatabase.CreateAsset(target, AssetDatabase.GenerateUniqueAssetPath(
                    $"{folder}/{target.name}.anim"));
            }

            foreach (var (binding, keys) in edits)
                AnimationUtility.SetObjectReferenceCurve(target, binding, keys);

            return target;
        }

        private static int ParseMaterialSlot(string propertyName)
        {
            int open = propertyName.IndexOf('[');
            int close = propertyName.IndexOf(']');
            if (open < 0 || close <= open + 1) return -1;
            return int.TryParse(propertyName.Substring(open + 1, close - open - 1), out int slot)
                ? slot : -1;
        }

        /// <summary>
        /// Material swaps outside the FX layer are only reported. Those layers ship as copies made
        /// before the build, so a per-build material cannot be written into them - and a swap
        /// living there is rare enough that a loud, specific warning is the proportionate answer.
        /// </summary>
        private static void WarnAboutForeignLayerSwaps(RuntimeAnimatorController controller,
                                                       string layerName,
                                                       Dictionary<string, bool[]> displaced,
                                                       MeshProtectVariant variant, Report report)
        {
            foreach (var clip in controller.animationClips)
            {
                if (clip == null) continue;
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (binding.type != typeof(SkinnedMeshRenderer) && binding.type != typeof(MeshRenderer))
                        continue;
                    if (!binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal))
                        continue;
                    if (!displaced.ContainsKey(binding.path)) continue;

                    report.warnings.Add(
                        $"Animation '{clip.name}' in the {layerName} layer swaps a material onto " +
                        $"'{binding.path}', whose mesh is displaced. Only FX-layer swaps are " +
                        "re-pointed at decode materials, so when this one plays, that part shows " +
                        "as noise. Moving the swap into the FX layer fixes it.");
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ parameters

        /// <summary>What this upload is allowed to do about parameter names.</summary>
        private struct ParameterDecision
        {
            /// <summary>Original name to replacement. Empty means no parameter is renamed.</summary>
            public Dictionary<string, string> map;

            /// <summary>
            /// The same for object names, and it lives or dies with the one above.
            ///
            /// Not because they are related, but because both are carried by the prepared copies:
            /// those hold clips whose paths already say the new object names. Renaming the objects
            /// without adopting the copies would leave the author's own controllers - which ship in
            /// their place - addressing objects that no longer answer to those names.
            /// </summary>
            public Dictionary<string, string> objectMap;

            /// <summary>False when the prepared copies must not be used, because they carry renamed
            /// parameters this build cannot make the rest of the avatar agree with.</summary>
            public bool adoptAllowed;

            public bool copyBlendTrees;
            public bool expressionParameters;
        }

        /// <summary>
        /// The parameter map for a build that adopts NO prepared copies: the only controller this
        /// build rewrites is its own FX copy, and nothing is replaced. Used when the stored maps
        /// were invalidated - the survey re-runs against the avatar exactly as it will ship, so a
        /// name is renamed only if the FX copy, the expression list, the menus and the components
        /// fully account for it. Anything an un-adopted controller still touches keeps its name,
        /// by the same per-name proof the normal path uses.
        /// </summary>
        private static Dictionary<string, string> FallbackParameterMap(
            VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor, MeshProtectRoot settings,
            ParameterDecision decision)
        {
            if (!settings.obfuscateParameterNames || descriptor == null)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var options = MeshProtectControllers.SurveyOptions(settings, descriptor);
            options.rewritable.Clear();
            options.expressionParameters = decision.expressionParameters;
            options.copySeparateBlendTrees = decision.copyBlendTrees;

            var fx = FindFX(descriptor);
            if (fx != null) options.rewritable.Add(fx);

            var findings = MeshProtectParameters.Survey(descriptor, options);
            return MeshProtectParameters.BuildMap(
                findings, settings.variant,
                findings.Select(f => f.name).Concat(options.reserved));
        }

        /// <summary>Object-name half of the no-adoption fallback, same shape as the parameter one.</summary>
        private static Dictionary<string, string> FallbackObjectMap(
            VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor, MeshProtectRoot settings)
        {
            if (!settings.obfuscateObjectNames || descriptor == null)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var options = MeshProtectControllers.ObjectSurveyOptions(settings, descriptor);
            options.rewritable.Clear();

            var fx = FindFX(descriptor);
            if (fx != null) options.rewritable.Add(fx);

            var findings = MeshProtectObjectNames.Survey(descriptor, options);
            return MeshProtectObjectNames.BuildMap(findings, settings.variant,
                                                   findings.Select(f => f.name));
        }

        /// <summary>
        /// Decide the parameter map, and whether the prepared copies may be used at all.
        ///
        /// THE RE-CHECK IS THE POINT. The copies were renamed before the build, against the avatar
        /// as it stood then. By the time this runs, NDMF, Modular Avatar and VRCFury have all had
        /// the avatar - they merge animators, add menus and can introduce a reference to a parameter
        /// that was safe to rename an hour ago. So every name the copies already carry is checked
        /// against the avatar actually being built, and if any one of them no longer holds, the
        /// copies are dropped entirely and the author's own controllers ship instead.
        ///
        /// All or nothing, because a parameter is avatar-global: half the copies adopted would mean
        /// one layer calling it by the new name and another by the old. Dropping them costs hidden
        /// layer names. Keeping them would cost an avatar that does not work, and an upload refused
        /// is worse than either - an author whose upload fails uninstalls the tool.
        /// </summary>
        private static ParameterDecision ResolveParameters(
            GameObject avatar, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor,
            MeshProtectRoot settings, Report report)
        {
            var decision = new ParameterDecision
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal),
                adoptAllowed = true,
                copyBlendTrees = settings.copySeparateBlendTrees,
                expressionParameters = settings.obfuscateExpressionParameters,
            };

            if (descriptor == null) return decision;

            // Whether the copies about to be swapped in carry renamed parameters - which is not the
            // same question as whether copies exist. Prepared with renaming off, or prepared with it
            // on when no parameter could be cleared, both leave copies holding the original names
            // and this list empty, and both are states the editor reports as up to date.
            bool prepared = settings.renamedParameters.Count > 0;

            // Turning the option off does not put the names in the copies back, so the copies
            // cannot be used either. Preparing again is what actually turns it off.
            if (prepared && !settings.obfuscateParameterNames)
            {
                report.warnings.Add(
                    "Parameter renaming is switched off, but the prepared controller copies were " +
                    "made with it on, so this upload uses your own controllers instead and those " +
                    "layers ship with their original names. Press Prepare Controllers to remake " +
                    "the copies without renamed parameters.");
                decision.adoptAllowed = false;
                return decision;
            }

            if (!settings.obfuscateParameterNames) return decision;

            // The copies were renamed under the options in force when they were made. Those are the
            // ones the rest of this build has to agree with, not whatever the checkboxes say now.
            if (prepared)
            {
                decision.copyBlendTrees = settings.preparedWithSeparateBlendTrees;
                decision.expressionParameters = settings.preparedWithExpressionParameters;
            }

            var options = MeshProtectControllers.SurveyOptions(settings, descriptor);
            options.rewritable.Clear();
            options.expressionParameters = decision.expressionParameters;
            options.copySeparateBlendTrees = decision.copyBlendTrees;

            // At this point the only controller whose contents this build rewrites is its own copy
            // of FX. The avatar's other layers are either replaced by a prepared copy - already
            // rewritten, and skipped below - or left exactly as the author has them.
            var fx = FindFX(descriptor);
            if (fx != null) options.rewritable.Add(fx);

            // A controller about to be replaced is left out of the survey because what ships in its
            // place was already rewritten - and that is only true of copies the stored map renamed.
            // Leaving them out with no stored map hides every parameter they use: one shared with FX
            // would show a single rewritable mention, pass, and be renamed in FX, the expression
            // list and the menus while the copy this build cannot write to went on saying the old
            // name. So without a stored map they stay in the survey, as controllers this build does
            // not rewrite, and anything they touch is off limits.
            if (prepared)
                foreach (var replaced in MeshProtectControllers.WouldAdopt(descriptor, settings))
                    options.replaced.Add(replaced);

            var findings = MeshProtectParameters.Survey(descriptor, options);

            if (!prepared)
            {
                // No name is committed anywhere yet, so the map is decided here against the avatar
                // as it will ship. Only what the FX copy alone can account for qualifies.
                decision.map = MeshProtectParameters.BuildMap(
                    findings, settings.variant,
                    findings.Select(f => f.name).Concat(options.reserved));
                return decision;
            }

            var stored = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in settings.renamedParameters)
                if (!string.IsNullOrEmpty(entry.original)) stored[entry.original] = entry.obfuscated;

            var byName = findings.ToDictionary(f => f.name, StringComparer.Ordinal);
            var broken = new List<string>();

            foreach (var entry in stored)
            {
                if (byName.TryGetValue(entry.Key, out var finding) && finding.blockedBy != null)
                    broken.Add($"'{entry.Key}' is {finding.blockedBy}");

                // A replacement that turned up as a real name in the meantime would silently merge
                // two parameters into one. The names are eight letters drawn from this avatar's own
                // algorithm, so this is not expected - but it is cheap to rule out, and if it ever
                // did happen it would repeat on every upload of that avatar.
                if (byName.ContainsKey(entry.Value))
                    broken.Add($"the replacement name for '{entry.Key}' is already used by " +
                               "something else in this build");
            }

            if (broken.Count > 0)
            {
                report.warnings.Add(
                    "The protected controller copies were dropped from this upload, and your own " +
                    "controllers are being used instead, so those layers ship with their original " +
                    "names. The avatar works; it is less protected. The copies rename parameters, " +
                    "and something in this build now uses one of them in a place this upload cannot " +
                    "rewrite:\n  " + string.Join("\n  ", broken.Take(10)) +
                    (broken.Count > 10 ? "\n  ..." : "") +
                    "\n\nPress Prepare Controllers to work the names out again against the avatar " +
                    "as it is now.");

                decision.adoptAllowed = false;
                return decision;
            }

            decision.map = stored;
            return decision;
        }

        /// <summary>
        /// The same decision for object names, made the same way and reported into the same
        /// all-or-nothing: the copies carry rewritten animation paths, so using them and not
        /// renaming the objects is as broken as the other way round.
        /// </summary>
        private static Dictionary<string, string> ResolveObjectNames(
            VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor, MeshProtectRoot settings,
            Report report, out bool allowed)
        {
            allowed = true;
            var empty = new Dictionary<string, string>(StringComparer.Ordinal);
            if (descriptor == null) return empty;

            bool prepared = settings.renamedObjects.Count > 0;

            if (prepared && !settings.obfuscateObjectNames)
            {
                report.warnings.Add(
                    "Object renaming is switched off, but the prepared controller copies were made " +
                    "with it on - their animations already address objects by the new names. This " +
                    "upload uses your own controllers instead, so those layers ship with their " +
                    "original names. Press Prepare Controllers to remake the copies.");
                allowed = false;
                return empty;
            }

            if (!settings.obfuscateObjectNames) return empty;

            var options = MeshProtectControllers.ObjectSurveyOptions(settings, descriptor);
            options.rewritable.Clear();

            var fx = FindFX(descriptor);
            if (fx != null) options.rewritable.Add(fx);

            if (prepared)
                foreach (var replaced in MeshProtectControllers.WouldAdopt(descriptor, settings))
                    options.replaced.Add(replaced);

            var findings = MeshProtectObjectNames.Survey(descriptor, options);

            if (!prepared)
                return MeshProtectObjectNames.BuildMap(findings, settings.variant,
                                                       findings.Select(f => f.name));

            var stored = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in settings.renamedObjects)
                if (!string.IsNullOrEmpty(entry.original)) stored[entry.original] = entry.obfuscated;

            var byName = findings.ToDictionary(f => f.name, StringComparer.Ordinal);
            var broken = new List<string>();

            foreach (var entry in stored)
            {
                if (byName.TryGetValue(entry.Key, out var finding) && finding.blockedBy != null)
                    broken.Add($"'{entry.Key}' is {finding.blockedBy}");

                // A replacement that has become a real name would give two objects one path, and
                // whichever the hierarchy found first would answer for both. The parameter check
                // rules this out and this one has to as well.
                if (byName.ContainsKey(entry.Value))
                    broken.Add($"the replacement name for '{entry.Key}' is already an object name " +
                               "in this build");
            }

            if (broken.Count > 0)
            {
                report.warnings.Add(
                    "The protected controller copies were dropped from this upload, and your own " +
                    "controllers are being used instead, so those layers ship with their original " +
                    "names. The avatar works; it is less protected. The copies rename objects, and " +
                    "something in this build now uses one of those names where this upload cannot " +
                    "rewrite it:\n  " + string.Join("\n  ", broken.Take(10)) +
                    (broken.Count > 10 ? "\n  ..." : "") +
                    "\n\nPress Prepare Controllers to work the names out again against the avatar " +
                    "as it is now.");

                allowed = false;
                return empty;
            }

            return stored;
        }

        /// <summary>
        /// Rename the objects on the clone, and rewrite every animation path that reaches them.
        ///
        /// THE ORDER OF THESE TWO DOES NOT MATTER, but what is covered does. This build generates
        /// the clips that drive the unlock and writes them into its own folder as assets of their
        /// own - they are not sub-assets of the FX controller - and they bind to RENDERER PATHS.
        /// Rewriting only what lives inside the controller would leave them addressing objects that
        /// have just been renamed: the digits would stop reaching the material, the avatar would
        /// upload, and nobody could ever unlock it. That is why the ownership test here takes the
        /// build folder as well.
        /// </summary>
        private static int ApplyObjectNames(
            GameObject avatar, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor,
            string folder, Dictionary<string, string> map)
        {
            if (map.Count == 0) return 0;

            int rewrites = MeshProtectObjectNames.RewriteController(FindFX(descriptor), map, folder);
            rewrites += MeshProtectObjectNames.RewriteObjects(avatar, map);
            return rewrites;
        }

        /// <summary>
        /// Write the new names everywhere this build owns: its FX copy, the expression parameter
        /// list, the menu tree, and the avatar clone's own components.
        /// </summary>
        private static int ApplyParameterNames(
            GameObject avatar, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor,
            string folder, MeshProtectVariant variant, ParameterDecision decision)
        {
            if (decision.map.Count == 0) return 0;

            int rewrites = MeshProtectParameters.RewriteController(FindFX(descriptor), decision.map);
            rewrites += MeshProtectParameters.RewriteComponents(avatar, decision.map);

            if (decision.expressionParameters)
            {
                rewrites += MeshProtectParameters.RewriteExpressionParameters(
                    descriptor.expressionParameters, decision.map);

                // The root may come back as a different menu: if what the descriptor held turned
                // out to belong to the artist, it is copied rather than written to, and the copy is
                // what this upload has to point at.
                descriptor.expressionsMenu = MeshProtectParameters.RewriteMenuTree(
                    descriptor.expressionsMenu, folder, variant, decision.map, out int menuRewrites);
                rewrites += menuRewrites;
            }

            return rewrites;
        }

        private static UnityEditor.Animations.AnimatorController FindFX(
            VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor)
        {
            if (descriptor == null || descriptor.baseAnimationLayers == null) return null;

            foreach (var layer in descriptor.baseAnimationLayers)
                if (layer.type == VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.FX &&
                    !layer.isDefault)
                    return layer.animatorController as UnityEditor.Animations.AnimatorController;

            return null;
        }
#endif

        // ------------------------------------------------------------------ meshes

        /// <summary>
        /// TangentSpace only survives if every mesh being baked can supply tangents. Otherwise the
        /// entire avatar drops to Normal, because the mode is one value shared by all materials.
        /// </summary>
        private static MeshProtectRoot.DisplacementMode ResolveMode(
            MeshProtectRoot settings, IEnumerable<Renderer> renderers, Report report)
        {
            if (settings.mode != MeshProtectRoot.DisplacementMode.TangentSpace)
                return MeshProtectRoot.DisplacementMode.Normal;

            var blockers = new List<string>();
            foreach (var renderer in renderers)
            {
                var mesh = GetMesh(renderer);
                if (mesh == null) continue;
                if (!MeshProtectMesh.CanUseTangentSpace(mesh, settings.recalculateMissingTangents))
                    blockers.Add(mesh.name);
            }

            if (blockers.Count == 0) return MeshProtectRoot.DisplacementMode.TangentSpace;

            report.warnings.Add(
                $"Fell back to Normal mode for the whole avatar: {blockers.Count} mesh(es) cannot supply " +
                $"tangents ({string.Join(", ", blockers.Distinct().Take(6))}" +
                $"{(blockers.Distinct().Count() > 6 ? ", ..." : "")}). " +
                (settings.recalculateMissingTangents
                    ? "They have no UV0, so tangents cannot be recalculated either."
                    : "Enable Recalculate Missing Tangents to keep the stronger mode.") +
                " This costs more than it sounds like. Normal mode moves every vertex along its " +
                "own normal, and a normal is exactly the direction a 'this surface should be " +
                "smooth' fit can solve for - measured on one avatar, a short smoothing pass with " +
                "no password recovered up to 89% of the displacement in Normal mode against 32% " +
                "in tangent space.");

            return MeshProtectRoot.DisplacementMode.Normal;
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null) return skinned.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        /// <summary>
        /// Leave a renderer entirely alone when its mesh cannot carry the protection.
        ///
        /// Every one of these used to stop the upload, from deep inside the bake, after the
        /// renderer had already been given the decode shader - so by then there was no way back to
        /// "leave it as it was" and refusing was the only safe answer left. Asked here instead,
        /// while nothing has been touched, the answer is the one the author actually wants: that
        /// renderer ships exactly as it came in, everything else is protected, and the Console says
        /// which and why.
        ///
        /// A mesh with no UV0, one nobody ticked Read/Write on, one that already uses UV6, a
        /// material reading its ID Mask out of UV6 - none of these is a reason that somebody cannot
        /// upload their avatar. They are reasons that one mesh is not protected.
        /// </summary>
        private static void DropRenderersThatCannotBeProtected(
            List<(Renderer renderer, Material[] materials, bool[] protectedSubMesh)> candidates,
            MeshProtectVariant variant, Report report)
        {
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                var renderer = candidates[i].renderer;
                string why = null;

                if (!MeshProtectMesh.CanProtect(GetMesh(renderer), out string meshWhy))
                    why = meshWhy;

                // The ID Mask has to be judged for the whole renderer, not per material. UV6 is
                // written for every vertex of the mesh, so leaving one material out does not save
                // the mask indices of a sub-mesh sharing that mesh - only leaving the mesh alone
                // does. Read off the ORIGINAL materials: the converted copies are not assigned yet.
                if (why == null)
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null || !UsesIdMaskFromUv6(material)) continue;
                        why = $"'{material.name}' takes its lilToon ID Mask index from UV6, which " +
                              "is the channel the displacement amplitude goes into - set the ID " +
                              "Mask's UV to 7 to protect this mesh";
                        break;
                    }
                }

                // A material on a protection family - this avatar's own from an earlier run, or
                // another tool's - is deliberately NOT vetoed here, and that was a change made and
                // then taken back out. The reasoning for vetoing was that UV6 is written across the
                // whole mesh, so baking could overwrite another tool's amplitude. It is already
                // covered: anything that keeps data in UV6 makes CanProtect refuse the mesh above,
                // and anything that does not keep data there has nothing to lose - its own
                // sub-mesh's vertices go into the skip mask and are never displaced twice.
                //
                // Vetoing anyway cost protection on every renderer that merely shares a mesh with
                // such a material, for no safety at all. It was caught by noticing that the test
                // written to prove the veto worked still passed with the veto removed: what was
                // actually holding the property up was the UV6 check.

                if (why == null) continue;

                // "Visible while locked" is the part an author does not work out for themselves.
                // The lock is a vertex shader effect - the decode collapses every vertex to a point
                // when the key is wrong - so it only reaches materials wearing that shader. Anything
                // skipped here renders normally on a locked avatar, which looks like a bug to
                // whoever is standing next to it and is the first thing they will report.
                report.warnings.Add(
                    $"'{renderer.name}' was left exactly as it is, unprotected: {why}. It will " +
                    "also be VISIBLE while the avatar is locked - hiding is done by the protection " +
                    "shader, so it only hides what it protects.");
                report.skippedRenderers++;
                candidates.RemoveAt(i);
            }
        }

        /// <summary>
        /// Leave a mesh alone entirely when something else on the avatar still points at it.
        ///
        /// Protection replaces the mesh on the renderers this tool handles. Every OTHER reference
        /// to that same Mesh - a second renderer whose material is not lilToon, a particle system
        /// emitting from it, a leftover MeshFilter - keeps pointing at the undisplaced original,
        /// and anything reachable from the avatar is serialised into the bundle. One such
        /// reference hands over a perfect copy with no password involved.
        ///
        /// This used to be found after baking and stopped the upload. Stopping it is the wrong
        /// half of the choice: the author is told to go and edit their avatar, from inside an SDK
        /// dialog, over one mesh - and until they do, they cannot upload at all, so the whole
        /// avatar ships with no protection rather than most of it with some. Dropping the mesh
        /// instead is honest in the way blocking was trying to be: it is not protected, rather
        /// than looking protected, and everything else on the avatar still is.
        ///
        /// Done BEFORE the materials are assigned, because a renderer that keeps the protect
        /// shader over a mesh nothing displaced is broken rather than unprotected - the decode
        /// would read its displacement amplitude from a UV6 this build never wrote.
        ///
        /// GuardAgainstClearCopies still runs after the bake. It should now have nothing to find;
        /// if it does, something got past this and blocking is the right answer at that point.
        /// </summary>
        private static void DropRenderersWhoseMeshLeaks(
            GameObject avatar,
            List<(Renderer renderer, Material[] materials, bool[] protectedSubMesh)> candidates,
            Report report)
        {
            if (candidates.Count == 0) return;

            // The components whose mesh reference this build is going to replace. A reference from
            // one of these is not a leak - it is the one being overwritten.
            var ours = new HashSet<Component>();
            foreach (var (renderer, _, __) in candidates)
            {
                if (renderer is SkinnedMeshRenderer skinned) { ours.Add(skinned); continue; }
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null) ours.Add(filter);
            }

            // Every serialised field of every component, rather than a list of the component types
            // known to hold a Mesh. The list approach was written first and it was already wrong:
            // it named ParticleSystemRenderer and missed that a particle system's SHAPE module
            // emits from a mesh of its own, and it could never have covered a Mesh field on some
            // third-party MonoBehaviour - and a real avatar is mostly third-party components.
            var elsewhere = new Dictionary<Mesh, List<string>>();
            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
            {
                // A missing script serialises as null and has no fields to read.
                if (component == null || ours.Contains(component)) continue;

                using (var serialized = new SerializedObject(component))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        if (!(property.objectReferenceValue is Mesh mesh)) continue;

                        if (!elsewhere.TryGetValue(mesh, out var where))
                            elsewhere[mesh] = where = new List<string>();
                        where.Add($"{component.GetType().Name}.{property.propertyPath} on " +
                                  $"'{HierarchyPath(component.transform)}'");
                    }
                }
            }

            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                var mesh = GetMesh(candidates[i].renderer);
                if (mesh == null || !elsewhere.TryGetValue(mesh, out var where)) continue;

                report.warnings.Add(
                    $"'{mesh.name}' on '{candidates[i].renderer.name}' was left unprotected, and " +
                    "will be visible while the avatar is locked: the " +
                    $"avatar still points at it from {string.Join(", ", where.Distinct().Take(3))}" +
                    $"{(where.Distinct().Count() > 3 ? ", ..." : "")}, and that reference would " +
                    "carry the undisplaced mesh into the bundle whatever this build did to the " +
                    "renderer. To protect it, give the other object a lilToon material so it is " +
                    "protected too, or remove the reference.");

                report.skippedRenderers++;
                candidates.RemoveAt(i);
            }
        }

        /// <summary>
        /// Refuse the build if a mesh that was protected is still reachable in its original form.
        ///
        /// Protection replaces the mesh on the renderers this tool handles. Every OTHER reference
        /// to that same Mesh object - a second renderer whose material is not lilToon, a mesh
        /// particle system, a leftover MeshFilter - keeps pointing at the undisplaced original, and
        /// anything reachable from the avatar is serialised into the bundle. One such reference
        /// hands over a perfect copy of the mesh with no password involved, silently voiding the
        /// protection for that mesh while every other check still reports success.
        ///
        /// There is no safe automatic repair: the other renderer cannot decode, so giving it the
        /// displaced copy would leave it permanently scrambled. The choice belongs to the author.
        /// </summary>
        private static void GuardAgainstClearCopies(GameObject avatar, HashSet<Mesh> bakedSources)
        {
            if (bakedSources.Count == 0) return;

            var leaks = new List<string>();

            // Every serialised field of every component, rather than a list of the component types
            // that are known to hold a Mesh. The list approach was written first and it was already
            // wrong: it named ParticleSystemRenderer, and missed that a particle system's SHAPE
            // module emits from a mesh of its own. It could never have covered a Mesh field on some
            // third-party MonoBehaviour, and a real avatar is mostly third-party components.
            //
            // What ships in the bundle is what is reachable by serialisation, so serialisation is
            // what this walks.
            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
            {
                // A missing script serialises as null and has no fields to read.
                if (component == null) continue;

                using (var serialized = new SerializedObject(component))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        if (!(property.objectReferenceValue is Mesh mesh)) continue;
                        if (!bakedSources.Contains(mesh)) continue;

                        leaks.Add($"'{mesh.name}' via {component.GetType().Name}.{property.propertyPath} " +
                                  $"on '{HierarchyPath(component.transform)}'");
                    }
                }
            }

            if (leaks.Count == 0) return;

            throw new InvalidOperationException(
                "These meshes were protected, but the avatar still carries the original, " +
                "undisplaced version, which would ship in the bundle and be readable without the " +
                "password:\n  " + string.Join("\n  ", leaks.Distinct().Take(10)) +
                (leaks.Distinct().Count() > 10 ? "\n  ..." : "") +
                "\n\nGive the listed object a lilToon material so it gets protected too, or " +
                "take the reference away. Ignoring the material is NOT enough on its own: that " +
                "leaves the sub-mesh undisplaced while the mesh is still baked and UV6 still " +
                "written across it. The upload was stopped rather than ship a mesh that only " +
                "looks protected.");
        }

        private static string HierarchyPath(Transform t)
        {
            var parts = new List<string>();
            for (var c = t; c != null; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void BakeRendererMesh(Renderer renderer, MeshProtectRoot settings,
                                             MeshProtectRoot.DisplacementMode effectiveMode,
                                             uint key, MeshProtectVariant variant, bool[] protectedSubMesh,
                                             Dictionary<string, Mesh> cache, HashSet<Mesh> bakedSources,
                                             string folder, Report report)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            var filter = renderer.GetComponent<MeshFilter>();
            Mesh source = skinned != null ? skinned.sharedMesh : filter != null ? filter.sharedMesh : null;

            if (source == null)
            {
                report.warnings.Add($"Renderer '{renderer.name}' has no mesh.");
                return;
            }

            bakedSources.Add(source);

            // The author's word on materials this build cannot judge. A slot on the invisible
            // list counts as protected for the DISPLACEMENT decision only: its material is claimed
            // to change nothing on screen, so it does not matter that it has no decode.
            //
            // The comment here used to say a wrong entry "announces itself as visible noise on the
            // first locked test". That is true only for a material that draws COLOUR. One that
            // writes stencil or depth spoils what other materials draw, and while the avatar is
            // locked those are collapsed and invisible - so the locked test is exactly the test
            // that cannot see it. WarnAboutInvisibleMaterialsThatAreNot catches those up front.
            // protectedSubMesh itself stays untouched - it is the record of what was CONVERTED,
            // which the roster and the swap rewrite report from.
            bool[] effective = protectedSubMesh;
            if (settings.invisibleMaterials.Count > 0)
            {
                var worn = renderer.sharedMaterials;
                effective = (bool[])protectedSubMesh.Clone();
                for (int slot = 0; slot < effective.Length && slot < worn.Length; slot++)
                    if (!effective[slot] && worn[slot] != null &&
                        settings.invisibleMaterials.Contains(worn[slot]))
                        effective[slot] = true;
            }

            bool[] skipVertex = BuildSkipMask(source, effective);

            if (skipVertex != null)
            {
                int untouched = skipVertex.Count(s => s);

                // When the cause is an EXTRA material slot, say so - the generic line below blames
                // "sub-meshes whose material is not protected", and on the avatar that found this
                // the sub-mesh's own material was lilToon and converted fine. Its owner cannot act
                // on a reason that names the wrong thing.
                bool extraSlotIsWhy = effective.Length > source.subMeshCount;
                if (extraSlotIsWhy)
                {
                    extraSlotIsWhy = false;
                    for (int extra = source.subMeshCount; extra < effective.Length; extra++)
                        if (!effective[extra]) { extraSlotIsWhy = true; break; }
                }

                report.warnings.Add(
                    $"'{source.name}': {untouched} of {source.vertexCount} vertices ship undisplaced " +
                    "because they belong to sub-meshes whose material is not protected (or are shared " +
                    "with one). Those parts of the mesh are readable without the password." +
                    (extraSlotIsWhy
                        ? $" On '{renderer.name}' part of the reason is an EXTRA material slot: the " +
                          "renderer has more materials than the mesh has sub-meshes, Unity draws " +
                          "each extra one over the last sub-mesh again, and an extra that cannot " +
                          "be protected would keep drawing the displaced geometry with no decode - " +
                          "so that sub-mesh is left in place instead. To protect it: make it a " +
                          "shader this tool can carry the decode in, or remove the extra slot. " +
                          "Invisible Materials is the last resort and only for a material that " +
                          "changes NOTHING on screen - an anti-clip shell that writes stencil is " +
                          "not one, because its mask moves with the displaced mesh and the damage " +
                          "lands on the body instead of on it."
                        : ""));
            }

            // Two renderers can share a mesh but exclude different sub-meshes, so the mask is part
            // of the cache identity. Sharing the bake is what keeps a shared mesh from being
            // displaced twice.
            string cacheKey = source.GetInstanceID() + "|" + MaskSignature(skipVertex);

            if (!cache.TryGetValue(cacheKey, out var baked))
            {
                var result = MeshProtectMesh.Bake(source, settings, effectiveMode, key, variant, skipVertex);
                if (!string.IsNullOrEmpty(result.warning)) report.warnings.Add(result.warning);

                baked = result.mesh;

                // Verify the bake against the cipher before it goes anywhere. A failure means the
                // baker and MeshProtectCipher have drifted apart, which would otherwise only show
                // up as a broken avatar in game.
                var check = MeshProtectSelfCheck.Verify(source, baked, effectiveMode, key, variant);
                if (!check.Passed)
                {
                    throw new InvalidOperationException(
                        $"Self check failed on '{source.name}': worst vertex error " +
                        $"{check.worstVertexError:E3}, worst blend shape error " +
                        $"{check.worstBlendShapeError:E3} ({check.worstBlendShapeName}), " +
                        $"tolerance {check.tolerance:E3}.");
                }
                report.worstRestoreError = Math.Max(report.worstRestoreError, check.worstVertexError);
                report.worstBlendShapeError = Math.Max(report.worstBlendShapeError, check.worstBlendShapeError);
                report.blendShapeFramesChecked += check.blendShapeFramesChecked;

                baked.name = GeneratedName(source.name, variant);
                string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{baked.name}.asset");
                AssetDatabase.CreateAsset(baked, path);
                cache[cacheKey] = baked;
            }

            if (skinned != null) skinned.sharedMesh = baked;
            else if (filter != null) filter.sharedMesh = baked;

            // Motion vectors would betray the mesh - not to rippers, to motion blur. Unity draws
            // per-object motion vectors with its own internal replacement shader, which never sees
            // this family's vertex code, so it rasterises the RAW ENCRYPTED buffer. In any world
            // that consumes motion vectors (post-process motion blur, TAA), that scattered ±8cm
            // shell smears into visible flicker noise on and around the avatar. Measured: with the
            // default Object mode, 49% of the motion-vector buffer differs from an unprotected
            // avatar's; with Camera mode, 0 pixels differ - the per-object pass is simply never
            // drawn, and the avatar picks up whole-screen camera motion instead. The cost is that
            // the wearer's own limbs no longer contribute per-object blur, which is the correct
            // trade: the alternative (ForceNoMotion) still rasterises the encrypted silhouette.
            // Locked avatars stop writing per-object vectors too - measured identical to an
            // unprotected baseline. This runs on the build clone only, like everything here.
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
        }

        /// <summary>
        /// After the bake: is every sub-mesh that was DISPLACED actually wearing the decode?
        ///
        /// This is the one combination that produces a broken avatar rather than an unprotected
        /// one, and until this existed nothing looked for it. A sub-mesh whose vertices were moved
        /// but whose material is not ours has no decode to put them back: it renders as a cloud of
        /// noise, it stays that way when the right password is entered - because nothing on that
        /// material reads the password at all - and every count in the report says the avatar was
        /// protected, because from the build's point of view it was.
        ///
        /// It is written as a check on the FINISHED avatar rather than on the plan on purpose.
        /// Everything in this file assigns the converted material and the baked mesh together, from
        /// one list, so by reading the plan this could only ever agree with itself. Reading what the
        /// renderers actually hold at the end is the only version of the question that can catch
        /// something outside this file having changed a material afterwards - and something did:
        /// an avatar came back with a displaced body on a stock lilToon shader, which no path
        /// through this file can produce.
        ///
        /// It reports; it does not stop the build. A broken sub-mesh is worth knowing about in the
        /// upload report - which is where the author looks after finding their avatar wrong in game
        /// - and by the time this runs the bake is done, so refusing costs the whole upload to say
        /// something the author can read either way.
        /// </summary>
        private static void ReportDisplacedWithoutDecode(
            List<(Renderer renderer, bool[] protectedSubMesh)> plan,
            MeshProtectVariant variant, Report report)
        {
            foreach (var (renderer, protectedSubMesh) in plan)
            {
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials;

                for (int slot = 0; slot < protectedSubMesh.Length && slot < materials.Length; slot++)
                {
                    if (!protectedSubMesh[slot]) continue;               // never displaced
                    if (IsProtectShader(materials[slot], variant.shaderName)) continue;

                    string on = materials[slot] == null ? "<none>"
                              : materials[slot].shader == null ? "<no shader>"
                              : materials[slot].shader.name;

                    report.warnings.Add(
                        $"BROKEN: sub-mesh {slot} of '{renderer.name}' was displaced, but the " +
                        $"material in that slot ('{(materials[slot] == null ? "<none>" : materials[slot].name)}') " +
                        $"is on '{on}' rather than this avatar's '{variant.shaderName}' family. " +
                        "Nothing will put those vertices back - that part of the avatar renders as " +
                        "noise and stays that way whatever password is entered. This build assigned " +
                        "the decode material and the displaced mesh together, so something after " +
                        "this point changed it back. Please report this along with the upload report.");
                }
            }
        }

        /// <summary>Mark vertices used by any unprotected sub-mesh. Shared vertices stay untouched.</summary>
        private static bool[] BuildSkipMask(Mesh mesh, bool[] protectedSubMesh)
        {
            var skip = new bool[mesh.vertexCount];
            bool anyExcluded = false;

            int subMeshCount = mesh.subMeshCount;
            for (int sub = 0; sub < subMeshCount; sub++)
            {
                // Unity reuses the last material for sub-meshes past the end of the array, so the
                // index has to be clamped rather than treated as unprotected - otherwise those
                // sub-meshes would be decoded without ever having been displaced.
                int slot = protectedSubMesh.Length == 0 ? -1 : Mathf.Min(sub, protectedSubMesh.Length - 1);
                bool isProtected = slot >= 0 && protectedSubMesh[slot];

                // The other direction, which cost somebody an avatar: MORE materials than
                // sub-meshes. Unity draws every extra material over the LAST sub-mesh again, which
                // is how avatars get a second outline or a black body pass - very common, and never
                // true of the fixtures here, where the two counts always match.
                //
                // Those extra slots were never consulted, so a mesh whose only sub-mesh was
                // protected got displaced in full while an unprotected extra material kept drawing
                // that same displaced geometry with no decode on it. The result is a body that is
                // an exploded cloud both locked AND unlocked - our pass collapses and restores
                // underneath, and the undecoded pass on top never changes whatever password is
                // entered. It looked exactly like the mesh being displaced with no decode at all,
                // because for one of the two passes it was.
                //
                // So the last sub-mesh may only be displaced when every slot that draws over it is
                // ours as well.
                if (isProtected && sub == subMeshCount - 1)
                {
                    for (int extra = subMeshCount; extra < protectedSubMesh.Length; extra++)
                    {
                        if (protectedSubMesh[extra]) continue;
                        isProtected = false;
                        break;
                    }
                }

                if (isProtected) continue;

                anyExcluded = true;
                var indices = mesh.GetIndices(sub);
                foreach (int index in indices)
                    if (index >= 0 && index < skip.Length) skip[index] = true;
            }

            return anyExcluded ? skip : null;
        }

        /// <summary>
        /// The exact mask, packed. A hash would risk two different exclusion masks colliding and
        /// one renderer silently reusing the wrong baked mesh.
        /// </summary>
        private static string MaskSignature(bool[] skip)
        {
            if (skip == null) return "all";

            var bytes = new byte[(skip.Length + 7) / 8];
            for (int i = 0; i < skip.Length; i++)
                if (skip[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));

            return skip.Length + ":" + Convert.ToBase64String(bytes);
        }

        // ------------------------------------------------------------------ materials

        /// <summary>
        /// Stock lilToon, as opposed to somebody's custom family built on it.
        ///
        /// The distinction matters because "the shader name contains lilToon" is true of every
        /// custom family too - including other mesh protection tools, which are built the same way
        /// this one is. lilToon ships its own shaders under exactly these four prefixes; a custom
        /// family always puts its own name first.
        /// </summary>
        /// <summary>
        /// Is this material actually using lilToon's ID Mask, and reading it from UV6?
        ///
        /// "Reading it from UV6" alone is not enough: _IDMaskFrom can sit on 6 while the feature is
        /// switched off entirely, and blocking that would be a false alarm. The second half of the
        /// test is lilToon's own - it decides whether to compile LIL_FEATURE_IDMASK for a material
        /// by checking exactly these properties for a non-zero value, so this asks the same
        /// question the shader compiler does rather than inventing a rule.
        /// </summary>
        private static bool UsesIdMaskFromUv6(Material material)
        {
            if (!material.HasProperty("_IDMaskFrom")) return false;
            if (Mathf.RoundToInt(material.GetFloat("_IDMaskFrom")) != 6) return false;

            for (int i = 1; i <= 8; i++)
            {
                if (NonZero(material, "_IDMask" + i)) return true;
                if (NonZero(material, "_IDMaskPrior" + i)) return true;
            }
            return NonZero(material, "_IDMaskIsBitmap") || NonZero(material, "_IDMaskCompile");
        }

        private static bool NonZero(Material material, string property)
        {
            return material.HasProperty(property) && material.GetFloat(property) != 0f;
        }

        private static bool IsStockLilToon(Shader shader)
        {
            if (shader == null) return false;
            string n = shader.name;
            return n == "lilToon"
                || n.StartsWith("lilToon/", StringComparison.Ordinal)
                || n.StartsWith("Hidden/lilToon", StringComparison.Ordinal)
                || n.StartsWith("Hidden/lts", StringComparison.Ordinal)
                || n.StartsWith("_lil/", StringComparison.Ordinal);
        }

        private static Material ConvertMaterial(Material source, MeshProtectRoot settings,
                                                MeshProtectVariant variant,
                                                uint macValue, string folder, Report report)
        {
            if (source.shader == null) return null;

            // Already on this avatar's own protection. Converting a second time would displace an
            // already-displaced mesh, and no password recovers that. (A different tool's family,
            // or another variant's, is caught by the stock-lilToon check further down - we cannot
            // tell those apart by name any more, and should not try.)
            // Handled as a whole-renderer veto before anything is assigned - see
            // DropRenderersThatCannotBeProtected - so this is unreachable. Left as a backstop
            // because what it prevents is a mesh displaced twice, which nothing can undo.
            if (IsProtectShader(source, variant.shaderName))
            {
                report.warnings.Add(
                    $"Material '{source.name}' is already on this avatar's protection shader " +
                    $"('{source.shader.name}') and was left alone.");
                return null;
            }

            // A lilToon custom family this tool knows how to merge with - lilSSAO, lilSSRT.
            // Checked before everything below, because lilSSAO's shader name begins "lilToon/"
            // and would otherwise be taken for stock and converted with its effect dropped.
            if (MeshProtectLilHost.HostFor(source.shader) != null)
                return ConvertHostMaterial(source, settings, variant, macValue, folder, report);

            bool looksLikeLilToon = source.shader.name.Contains("lilToon")
                                    || source.shader.name.Contains("lts")
                                    || source.HasProperty("_LightMinLimit");
            if (!looksLikeLilToon)
            {
                // Not lilToon, but possibly a family this tool knows how to graft the decode onto.
                // The graft is a patched COPY of the author's shader living in this build's own
                // folder; their installed shader is read and never written.
                if (MeshProtectForeignShader.RecipeFor(source.shader) != null)
                    return ConvertForeignMaterial(source, settings, variant, macValue, folder, report);

                // A shader another build-time tool generated a moment ago, most often VRCFury's
                // SPS. Worth naming, because the shader in the message is one the author never
                // chose and cannot find: SPS clones the real shader into Hidden/SPSPatched/ and
                // swaps it in during the build.
                //
                // It gets there first and always will. VRCFury's preupload hook is -10000 and
                // NDMF's pipeline is -11000/-1025, while this runs at -1000 on purpose - after
                // everything that might merge meshes or weld vertices, because that would destroy
                // the vertex identities the restore depends on. So the two never patch the same
                // file; what happens instead is that SPS hands us a shader no recipe claims.
                //
                // The outcome is safe: unrecognised means unprotected, and unprotected means the
                // mesh under it is not displaced either. It is a hole in COVERAGE, not a broken
                // avatar - but a silent one, which is why it is said out loud.
                if (source.shader.name.IndexOf("SPSPatched", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    report.warnings.Add(
                        $"'{source.name}' was rewritten by VRCFury's SPS before this tool ran " +
                        $"(its shader is now '{source.shader.name}'), so this tool no longer " +
                        "recognises it and that sub-mesh ships UNPROTECTED and undisplaced. " +
                        "Nothing is broken and it is not something you can reorder - SPS runs " +
                        "first by design. To protect that part, move SPS to a renderer of its own, " +
                        "or accept that this one is not covered.");
                    return null;
                }

                report.warnings.Add(
                    $"Material '{source.name}' uses '{source.shader.name}', which is not a shader " +
                    "family this tool can carry the decode in. Its sub-mesh was left unprotected.");
                return null;
            }

            // A lilToon-derived family that is not stock and not ours. It could be another mesh
            // protection tool - they are built exactly this way - in which case the mesh is already
            // displaced and doing it again destroys it. Even when it is something harmless, moving
            // the material onto our family would silently drop whatever that shader added. Neither
            // outcome is worth guessing about, so leave it alone and say so.
            if (!IsStockLilToon(source.shader))
            {
                report.warnings.Add(
                    $"Material '{source.name}' uses '{source.shader.name}', a custom lilToon family " +
                    "rather than stock lilToon. It was left unprotected: if that is another mesh " +
                    "protection tool, its mesh is already displaced and touching it again would " +
                    "ruin the model. Switch the material to stock lilToon to protect it here.");
                return null;
            }

            // lilToon's ID Mask can be told to take its index from UV6, and UV6.x is exactly where
            // the bake writes each vertex's amplitude. The mask reads that amplitude as a uint, so
            // a value like 0.04 truncates to zero and every ID-masked region collapses to index 0.
            // The upload succeeds, the protection works, and the model is simply wrong - with
            // nothing anywhere saying why. The channel holds one thing; there is no repair.
            // UsesIdMaskFromUv6 is checked per RENDERER before anything is assigned, not here.
            // Refusing one material is not enough: UV6 is written for the whole mesh, so a sub-mesh
            // sharing it loses its mask indices whatever this material does. See
            // DropRenderersThatCannotBeProtected.


            var copy = new Material(source) { name = GeneratedName(source.name, variant) };
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{copy.name}.mat");
            AssetDatabase.CreateAsset(copy, path);

            if (!MeshProtectInspector.TryConvert(copy, variant) ||
                !IsProtectShader(copy, variant.shaderName))
            {
                // Not the same thing as "this material is Poiyomi" or "this is somebody else's
                // custom family" - both of those returned above. Getting here means an ordinary
                // lilToon material could not be moved onto a family THIS BUILD generated, which
                // means the family is incomplete: one of the shaders it should hold did not
                // compile, or a container was not imported.
                //
                // This used to stop the upload, and stopping the upload was wrong. It is our family
                // that is short, not their material, and there is nothing the author can do about
                // it from inside the SDK dialog it appears in - so they simply cannot upload, over
                // one sub-mesh, on an avatar that is otherwise fine. Returning null puts this
                // material down the same road as a Poiyomi one: the sub-mesh ships unprotected AND
                // its vertices are left where they are, so nothing is broken, only unprotected.
                // That is the rule this project runs on - a check that fails during a build
                // degrades, it does not block.
                //
                // It is not silent either. The warning names the material, and the build refuses
                // only when NOTHING came out protected, which is where "unprotected in part"
                // becomes "not protected at all".
                string stayedOn = copy.shader == null ? "<null>" : copy.shader.name;
                AssetDatabase.DeleteAsset(path);
                report.warnings.Add(
                    $"'{source.name}' is a lilToon material but could not be moved onto this " +
                    $"avatar's '{variant.shaderName}' family - it stayed on '{stayedOn}', so that " +
                    "sub-mesh ships UNPROTECTED. Its vertices are left where they are, but if any " +
                    "other material on the same renderer did convert, the mesh is still rebuilt " +
                    "for those - so this renderer ends up part protected and part not. The family " +
                    "is missing a shader it should have: press 'Rebuild Shader' on the Mesh " +
                    "Protect Root component, then upload again.");
                return null;
            }

            // Old families lack the lock guard that runs before outline/AudioLink/fur effects.
            // Counting compiled variants cannot establish that guard is present. Regeneration
            // stays outside the upload because importing shaders here interrupts the SDK build.
            if (!MeshProtectShaderGen.FamilyIsCurrent(settings, variant))
            {
                AssetDatabase.DeleteAsset(path);
                report.warnings.Add(
                    $"'{source.name}' needs this version's protection shader family " +
                    $"'{variant.shaderName}'. Its generated files are older, incomplete or missing; " +
                    "older families can show outline or AudioLink geometry while locked. " +
                    "This material slot stays UNPROTECTED and intact instead. " +
                    "Press 'Rebuild Shader', under 'Advanced' on the Mesh Protect Root " +
                    "component, before uploading. Your password does not need to change.");
                return null;
            }

            // Viewers who have custom shaders switched off - safety settings, performance rank
            // blocking - get VRChat's fallback shader, which does not run the decode. Without this
            // tag they would see the scrambled mesh: a cloud of noise, for people who explicitly
            // asked not to render this avatar's shader. Hidden shows them nothing instead.
            copy.SetOverrideTag("VRCFallback", "Hidden");

            copy.SetFloat(variant.bypassProperty, 0f);
            // The mode property is written once for every material after the mode is resolved.
            // The hash constants are not properties at all - they are compiled into the generated
            // shader, so no material asset exposes them in plain text.

            // The shipped material must never carry the answer. Zero is the locked state: the
            // four-bit encoding reserves it, so it is not any digit's value and no password ships
            // half-entered. It used to be the lowest digit's value, which is why 111111 was once
            // refused at generation - see MeshProtectCipher.GeneratePassword for why it no longer
            // has to be.
            foreach (var digitProperty in variant.digitProperties)
                copy.SetFloat(digitProperty, 0f);

            // The value the shader checks the entered password against. Set from the password, but
            // the password itself never ships.
            copy.SetVector(variant.macProperty, MeshProtectCipher.MacToVector(macValue));

            return copy;
        }

        /// <summary>
        /// Say so when something on Invisible Materials writes stencil or depth.
        ///
        /// The list means "this changes nothing on screen, so displace the mesh under it even
        /// though it carries no decode". Drawing no colour is not the same thing. A stencil write
        /// decides what OTHER materials draw, and the geometry it writes from is displaced along
        /// with everything else - so the mask lands in the wrong places and the damage appears on
        /// the body or the clothes, never on the material that was listed.
        ///
        /// Anti-clip shells are the ones that matter, and they are exactly the example the tool
        /// used to give for what SHOULD go on the list. That advice is now gone; this catches the
        /// avatars that already took it.
        ///
        /// The reason this cannot be left to "you will see it when you test": while the avatar is
        /// LOCKED everything this tool protects is collapsed and invisible, so a stray mask has
        /// nothing left to spoil and the locked test looks perfect. The failure only appears
        /// unlocked - which is the one state an author checks last, and the one a friend sees first.
        ///
        /// Read off the shader SOURCE because Unity exposes no render state at edit time. A shader
        /// whose source cannot be read is not guessed about.
        /// </summary>
        private static void WarnAboutInvisibleMaterialsThatAreNot(MeshProtectRoot settings,
                                                                  Report report)
        {
            foreach (var material in settings.invisibleMaterials)
            {
                if (material == null || material.shader == null) continue;

                string path = MeshProtectForeignShader.RealPath(
                    AssetDatabase.GetAssetPath(material.shader));
                if (path == null) continue;

                string source;
                try { source = File.ReadAllText(path); } catch { continue; }

                bool stencil = Regex.IsMatch(source, @"(?im)^\s*Stencil\s*\{|^\s*Stencil\s*$");
                bool depth = Regex.IsMatch(source, @"(?im)^\s*ZWrite\s+On\s*$");
                if (!stencil && !depth) continue;

                report.warnings.Add(
                    $"'{material.name}' is on Invisible Materials, but its shader " +
                    $"('{material.shader.name}') writes {(stencil ? "stencil" : "depth")}. That is " +
                    "not invisible - it decides what OTHER materials draw. Being on the list lets " +
                    "the mesh under it be displaced, and it has no decode, so it writes its mask " +
                    "from scrambled geometry: the body shows through clothes, or the clothes get " +
                    "holes. The damage appears on those materials, not on this one, and NOT while " +
                    "the avatar is locked - everything protected is hidden then. Take it off the " +
                    "list unless you are certain, and test UNLOCKED.");
            }
        }

        /// <summary>
        /// Move a material that is not lilToon onto a grafted copy of its own shader.
        ///
        /// Everything after the shader swap is identical to the lilToon path, and deliberately so:
        /// the decode reads the same nine properties out of the material whichever host it is
        /// compiled into, because it is the same emitted text in both.
        ///
        /// What differs is the failure mode, and it is the milder one. lilToon's conversion can
        /// only fail if OUR family came out short. A graft can fail because the author's shader is
        /// a version whose shape this tool has not been taught - which is not a defect anywhere,
        /// just a limit - so it says so plainly and leaves the sub-mesh alone.
        /// </summary>
        /// <summary>
        /// A material on lilSSAO or lilSSRT, moved onto the merged family built for that host.
        /// The merged family is their folder with the decode added, so every property they set
        /// carries across by name and their own effect keeps working.
        /// </summary>
        private static Material ConvertHostMaterial(Material source, MeshProtectRoot settings,
                                                    MeshProtectVariant variant, uint macValue,
                                                    string folder, Report report)
        {
            var host = MeshProtectLilHost.HostFor(source.shader);
            var merged = MeshProtectLilHost.FindMerged(settings, variant, source.shader,
                                                       out string why);
            if (merged == null)
            {
                report.warnings.Add(
                    $"Material '{source.name}' uses '{source.shader.name}' and was left " +
                    $"unprotected: {why}. Its vertices are left where they are, so nothing is " +
                    "broken - that sub-mesh simply ships readable.");
                return null;
            }

            var copy = new Material(source) { name = GeneratedName(source.name, variant) };
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{copy.name}.mat");
            AssetDatabase.CreateAsset(copy, path);

            // Straight across to the same shader in the merged family: it was built from the
            // host's containers, so every name matches but the family segment. Assigning the
            // shader after the copy is what preserves the host's property values.
            var target = MeshProtectLilHost.MergedShaderFor(settings, variant, host, source.shader);
            if (target != null) copy.shader = target;

            if (target == null || !IsProtectShader(copy, variant.shaderName))
            {
                string stayedOn = copy.shader == null ? "<null>" : copy.shader.name;
                AssetDatabase.DeleteAsset(path);

                bool tessellating = source.shader != null
                                    && MeshProtectLilHost.IsTessellating(source.shader.name);

                // Two distinct causes wear the same refusal, and the remedies are different.
                // A merged family built before the tessellation wiring existed is fixed by one
                // press of Rebuild Shader; a container the wiring pass could not verify is not,
                // and there the only road to protection is taking the material off the variant.
                // The advice for that road has to name controls that actually leave it: lilSSRT
                // puts AO materials onto its AOTessellation shaders BY ITSELF whenever AO is on
                // with the shipped defaults (Quality High, Evaluation Auto, Vertex AO
                // Tessellation Auto) - the shader is Hidden/, absent from the dropdown, and the
                // inspector reassigns it on every open; lilToon's Rendering Mode does not escape
                // either, Cutout maps straight back to AOTessellation/Cutout.
                bool staleWiring = tessellating &&
                    MeshProtectLilHost.TessSupportIsStale(settings, variant, host);
                // Chosen by VARIANT, not by host: a lilSSRT install also carries plain
                // lilToon-style Tessellation variants, and pointing those at the AO controls
                // sends the author to a dropdown that does not govern their material.
                bool aoVariant = source.shader != null &&
                    (source.shader.name.Contains("/AOTessellation/") ||
                     source.shader.name.Contains("/ltspass_aotess"));
                string offSwitch = aoVariant
                    ? "open 'lilSSRT Occlusion' on the material and set Evaluation to Pixel " +
                      "(better AO, costs fragment time) or Vertex AO Tessellation to Off " +
                      "(cheaper, coarser AO)"
                    : "switch the material off lilToon's Tessellation";
                report.warnings.Add(tessellating
                    ? (staleWiring
                        ? $"'{source.name}' is on a tessellating variant of '{host.family}', " +
                          "and this avatar's merged family was built by a version that could " +
                          "not yet protect tessellation. That sub-mesh ships UNPROTECTED and " +
                          "intact, and stays VISIBLE while the avatar is locked. Press " +
                          "'Rebuild Shader', under 'Advanced' on the Mesh Protect Root " +
                          "component, then upload again - the rebuilt family protects it."
                        : $"'{source.name}' is on a tessellating variant of '{host.family}' " +
                          "whose container could not be wired for the decode - see the Console " +
                          "line from the rebuild for the reason. That sub-mesh ships " +
                          "UNPROTECTED and intact, and stays VISIBLE while the avatar is " +
                          "locked; if any other material on the same renderer did convert, " +
                          "this renderer ends up part protected and part not. To protect it, " +
                          $"{offSwitch}, then upload again.")
                    : $"'{source.name}' could not be moved onto the merged '{host.family}' family - " +
                      $"it stayed on '{stayedOn}', so that sub-mesh ships UNPROTECTED. Its vertices " +
                      "are left where they are. The merged family is missing a shader it should " +
                      "have, which usually means this project's lilToon and that product are " +
                      "versions that do not match: press 'Rebuild Shader' on the Mesh Protect Root " +
                      "component, watch the Console for a shader compile error, then upload again.");
                return null;
            }

            // Custom shaders off means VRChat's fallback, which does not run the decode: the
            // host's own fallback tag would draw the displaced mesh as noise at exactly the
            // people who asked not to render it.
            copy.SetOverrideTag("VRCFallback", "Hidden");

            copy.SetFloat(variant.bypassProperty, 0f);

            // The shipped material must never carry the answer; zero is the locked state.
            foreach (var digitProperty in variant.digitProperties)
                copy.SetFloat(digitProperty, 0f);

            copy.SetVector(variant.macProperty, MeshProtectCipher.MacToVector(macValue));

            return copy;
        }

        private static Material ConvertForeignMaterial(Material source, MeshProtectRoot settings,
                                                       MeshProtectVariant variant, uint macValue,
                                                       string folder, Report report)
        {
            var grafted = MeshProtectForeignShader.FindGraft(settings, variant, source.shader,
                                                             out string why);
            if (grafted == null)
            {
                report.warnings.Add(
                    $"Material '{source.name}' uses '{source.shader.name}' and was left " +
                    $"unprotected: {why}. Its vertices are left where they are, so nothing is " +
                    "broken - that sub-mesh simply ships readable, and it stays visible while the " +
                    "avatar is locked.");
                return null;
            }

            var copy = new Material(source) { name = GeneratedName(source.name, variant) };
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{copy.name}.mat");
            AssetDatabase.CreateAsset(copy, path);

            // The graft is the author's shader plus our additions, so every property they set
            // carries across by name. Assigning the shader after the copy is what preserves them:
            // Unity keeps values whose names still exist on the new shader.
            copy.shader = grafted;

            if (!IsProtectShader(copy, variant.shaderName))
            {
                string stayedOn = copy.shader == null ? "<null>" : copy.shader.name;
                AssetDatabase.DeleteAsset(path);
                report.warnings.Add(
                    $"'{source.name}' could not be moved onto its grafted shader - it stayed on " +
                    $"'{stayedOn}', so that sub-mesh ships UNPROTECTED. Its vertices are left " +
                    "where they are.");
                return null;
            }

            // Viewers with custom shaders switched off get VRChat's fallback, which does not run
            // the decode. Poiyomi's own tag asks for Standard, which would draw the displaced mesh
            // as a cloud of noise at exactly the people who asked not to render this shader.
            copy.SetOverrideTag("VRCFallback", "Hidden");

            copy.SetFloat(variant.bypassProperty, 0f);

            // The shipped material must never carry the answer; zero is the locked state.
            foreach (var digitProperty in variant.digitProperties)
                copy.SetFloat(digitProperty, 0f);

            copy.SetVector(variant.macProperty, MeshProtectCipher.MacToVector(macValue));

            return copy;
        }

        /// <summary>
        /// Is this material on the given variant's shader family?
        ///
        /// The family has to be passed in. There is no longer any way to recognise "a Mesh Protect
        /// shader" in general, and that is the point: a name shape shared by every avatar the tool
        /// produces is the signature this obfuscation pass exists to remove.
        /// </summary>
        public static bool IsProtectShader(Material material, string family)
        {
            return MeshProtectInspector.IsFamily(material, family)
                || MeshProtectForeignShader.IsGraftedFamily(material, family)
                || MeshProtectLilHost.IsMergedFamily(material, family);
        }

        // ------------------------------------------------------------------ misc

        private static List<Renderer> ResolveRenderers(MeshProtectRoot settings, GameObject avatar,
                                                       Report report)
        {
            if (settings.targetRenderers != null && settings.targetRenderers.Count > 0)
            {
                // Everything else here runs on the temporary clone the SDK builds from, which is
                // what lets this tool promise it does not modify the project. That promise stops at
                // this list. The clone is an Instantiate, and Unity only remaps references that
                // live inside the graph it copied - so an entry pointing at something outside the
                // avatar still points at the object in the open scene, and baking it would assign a
                // mesh and materials to the author's own scene. The build folder is deleted
                // afterwards on every path, including a refused upload, so they would be left with
                // a scene renderer bound to a mesh asset that no longer exists.
                var mine = new List<Renderer>();
                var outside = new List<string>();
                foreach (var r in settings.targetRenderers)
                {
                    if (r == null) continue;
                    if (r.transform.IsChildOf(avatar.transform)) mine.Add(r);
                    else outside.Add(r.name);
                }

                if (outside.Count > 0)
                    report.warnings.Add(
                        $"{outside.Count} Target Renderer(s) are not part of this avatar, so they " +
                        "were left alone: " + string.Join(", ", outside) + ". Only renderers under " +
                        "the avatar can be protected - anything else belongs to your scene.");

                // The same renderer twice is baked twice, and the second pass reads the materials
                // the first one converted. That trips the "already on this avatar's protection
                // shader" guard, which then tells the author to fix a problem they do not have.
                var once = mine.Distinct().ToList();
                if (once.Count != mine.Count)
                    report.warnings.Add(
                        $"{mine.Count - once.Count} Target Renderer(s) were listed more than once; " +
                        "each is protected once.");
                return once;
            }

            return avatar.GetComponentsInChildren<Renderer>(true)
                         .Where(r => r is SkinnedMeshRenderer || r is MeshRenderer)
                         .Where(r => r.sharedMaterials.Any(CanCarryTheDecode))
                         .ToList();
        }

        /// <summary>
        /// Could this material end up carrying the decode - lilToon, a host family this merges
        /// with, or a family the graft claims?
        ///
        /// Only a first pass. Whether it actually can is settled per material in ConvertMaterial,
        /// which has the shader source in front of it; this decides which renderers are worth
        /// looking at when the author has not listed any. Answering "no" here is the expensive
        /// mistake: the renderer is never considered again, and it ships unprotected with no
        /// warning naming it, because nothing ever picked it up to warn about.
        ///
        /// The host clause is not covered by the first two, which is what made that mistake real.
        /// A host family names itself whatever it likes - lilSSAO calls itself "lilToon/lilSSAO"
        /// and answers the name test by accident, but lilSSRT is plain "lilSSRT" - so it leans on
        /// _LightMinLimit, and every lilToon property set declares that except one:
        /// DefaultFakeShadow. lilSSRT's "[Optional] FakeShadow" variants therefore failed all
        /// three clauses and were dropped here, silently, leaving a shadow plane drawn under an
        /// avatar the decode had otherwise hidden.
        /// </summary>
        public static bool CanCarryTheDecode(Material material)
        {
            if (material == null || material.shader == null) return false;

            return material.shader.name.Contains("lilToon")
                || material.HasProperty("_LightMinLimit")
                || MeshProtectLilHost.HostFor(material.shader) != null
                || MeshProtectForeignShader.RecipeFor(material.shader) != null;
        }

        /// <summary>
        /// The name a generated mesh or material ships under.
        ///
        /// These used to keep the artist's name, on the grounds that a "_p" suffix on every mesh is
        /// a mark saying something processed this avatar. That reasoning answers the wrong question.
        /// The bundle already contains a shader family with generated names, two dozen synced bools
        /// with generated names, and displaced geometry: nobody opening it is in any doubt that it
        /// was processed. What the original names buy is nothing, and what they cost is the sentence
        /// "Outfit_Swimsuit" sitting on a material in a file this tool exists to make less useful to
        /// whoever took it.
        ///
        /// Renaming these is free of the usual danger, and the reason is worth being precise about:
        /// this build CREATED both objects, and nothing addresses a mesh or a material by name at
        /// runtime. A clip that swaps materials holds an object reference, not a name.
        ///
        /// Derived from the source name and the variant, so a re-bake of the same avatar produces
        /// the same names rather than a fresh set on every upload. Two sources that collided would
        /// get " 1" appended by GenerateUniqueAssetPath, which is what already happened to two
        /// meshes with the same name.
        /// </summary>
        private static string GeneratedName(string source, MeshProtectVariant variant)
        {
            const string consonants = "bcdfghjklmnprstvwz";
            const string vowels = "aeiou";

            unchecked
            {
                int hash = 17 + variant.macSalt * 31;
                foreach (char c in source ?? "") hash = hash * 33 + c;

                var rng = new System.Random(hash);
                var chars = new char[8];
                for (int i = 0; i < chars.Length; i++)
                    chars[i] = i % 2 == 0
                        ? consonants[rng.Next(consonants.Length)]
                        : vowels[rng.Next(vowels.Length)];

                return new string(chars);
            }
        }

        public static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace('/', '_').Replace('\\', '_');
        }
    }
}
#endif
