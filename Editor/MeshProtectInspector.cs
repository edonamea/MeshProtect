#if UNITY_EDITOR
using lilToon;
using UnityEditor;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// Material inspector for the generated MeshProtect shader families.
    /// Derived from the lilToon custom shader template (MIT, lilxyzw/lilToon).
    ///
    /// There is no single shader name any more: every avatar gets its own family, named with a
    /// pronounceable ten-letter word drawn per avatar - deliberately not a fixed prefix, because a
    /// prefix is the one string a scanner needs to recognise every avatar this tool ever produced.
    /// The family this inspector should map onto is therefore read from whichever material is being
    /// inspected or converted, not from a constant.
    ///
    /// The generated properties are deliberately not exposed. Their names are randomised per
    /// avatar, and every one of them is written by the baker - hand-editing any of them
    /// desynchronises the material from its baked mesh, which shows up as a scrambled avatar with
    /// no obvious cause.
    /// </summary>
    public class MeshProtectInspector : lilToonInspector
    {
        private static bool isShowCustomProperties = true;

        /// <summary>
        /// Set by the baker immediately before conversion. lilToon's conversion path gives us no
        /// way to pass the target family in, and the material still carries its original lilToon
        /// shader at that point, so there is nothing to derive it from.
        /// </summary>
        private static string conversionTarget;

        private string family = "";

        /// <summary>
        /// The lilToon custom family a shader belongs to: the first path segment, after an optional
        /// "Hidden/". "someword/lilToon" and "Hidden/someword/Cutout" both give "someword".
        ///
        /// Structural on purpose. This used to match a regex against the family name, which meant
        /// the name had to follow a fixed pattern - and a fixed pattern in the shader name of every
        /// uploaded avatar is exactly the signature a scanner looks for. It says nothing about
        /// whether the family is ours; callers compare the result against the variant they hold.
        /// </summary>
        public static string FamilyOf(Shader shader)
        {
            if (shader == null) return null;
            string name = shader.name;
            if (name.StartsWith("Hidden/", System.StringComparison.Ordinal))
                name = name.Substring("Hidden/".Length);
            int slash = name.IndexOf('/');
            return slash < 0 ? name : name.Substring(0, slash);
        }

        /// <summary>True when the material sits on the family this variant generated.</summary>
        public static bool IsFamily(Material material, string family)
        {
            return material != null && !string.IsNullOrEmpty(family)
                   && FamilyOf(material.shader) == family;
        }

        protected override void LoadCustomProperties(MaterialProperty[] props, Material material)
        {
            isCustomShader = true;
            family = FamilyOf(material.shader) ?? conversionTarget ?? "";
            ReplaceToCustomShaders();
            isShowRenderMode = !material.shader.name.Contains("Optional");
        }

        protected override void DrawCustomProperties(Material material)
        {
            isShowCustomProperties = Foldout("Mesh Protect", "Mesh Protect", isShowCustomProperties);
            if (!isShowCustomProperties) return;

            EditorGUILayout.BeginVertical(boxOuter);
            EditorGUILayout.LabelField("Mesh Protect", customToggleFont);
            EditorGUILayout.BeginVertical(boxInnerHalf);

            EditorGUILayout.LabelField(MeshProtectL10n.Tr("matinspector.family"), family,
                                       EditorStyles.miniLabel);
            EditorGUILayout.HelpBox(MeshProtectL10n.Tr("matinspector.body"), MessageType.Info);

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Exposes lilToon's own material conversion to the baker. Calling a protected member on
        /// another instance of the same derived type is legal C#, and reusing lilToon's mapping is
        /// far more robust than reimplementing the shader name table.
        /// </summary>
        public static bool TryConvert(Material material, MeshProtectVariant variant)
        {
            conversionTarget = variant.shaderName;
            try
            {
                var inspector = new MeshProtectInspector { family = variant.shaderName };
                inspector.ConvertMaterialToCustomShader(material);
                return material.shader != null && FamilyOf(material.shader) == variant.shaderName;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MeshProtect] lilToon conversion failed for '{material.name}': {e.Message}");
                return false;
            }
            finally
            {
                conversionTarget = null;
            }
        }

        protected override void ReplaceToCustomShaders()
        {
            string n = family;
            if (string.IsNullOrEmpty(n)) return;

            lts         = Shader.Find(n + "/lilToon");
            ltsc        = Shader.Find("Hidden/" + n + "/Cutout");
            ltst        = Shader.Find("Hidden/" + n + "/Transparent");
            ltsot       = Shader.Find("Hidden/" + n + "/OnePassTransparent");
            ltstt       = Shader.Find("Hidden/" + n + "/TwoPassTransparent");

            ltso        = Shader.Find("Hidden/" + n + "/OpaqueOutline");
            ltsco       = Shader.Find("Hidden/" + n + "/CutoutOutline");
            ltsto       = Shader.Find("Hidden/" + n + "/TransparentOutline");
            ltsoto      = Shader.Find("Hidden/" + n + "/OnePassTransparentOutline");
            ltstto      = Shader.Find("Hidden/" + n + "/TwoPassTransparentOutline");

            ltsoo       = Shader.Find(n + "/[Optional] OutlineOnly/Opaque");
            ltscoo      = Shader.Find(n + "/[Optional] OutlineOnly/Cutout");
            ltstoo      = Shader.Find(n + "/[Optional] OutlineOnly/Transparent");

            ltstess     = Shader.Find("Hidden/" + n + "/Tessellation/Opaque");
            ltstessc    = Shader.Find("Hidden/" + n + "/Tessellation/Cutout");
            ltstesst    = Shader.Find("Hidden/" + n + "/Tessellation/Transparent");
            ltstessot   = Shader.Find("Hidden/" + n + "/Tessellation/OnePassTransparent");
            ltstesstt   = Shader.Find("Hidden/" + n + "/Tessellation/TwoPassTransparent");

            ltstesso    = Shader.Find("Hidden/" + n + "/Tessellation/OpaqueOutline");
            ltstessco   = Shader.Find("Hidden/" + n + "/Tessellation/CutoutOutline");
            ltstessto   = Shader.Find("Hidden/" + n + "/Tessellation/TransparentOutline");
            ltstessoto  = Shader.Find("Hidden/" + n + "/Tessellation/OnePassTransparentOutline");
            ltstesstto  = Shader.Find("Hidden/" + n + "/Tessellation/TwoPassTransparentOutline");

            ltsl        = Shader.Find(n + "/lilToonLite");
            ltslc       = Shader.Find("Hidden/" + n + "/Lite/Cutout");
            ltslt       = Shader.Find("Hidden/" + n + "/Lite/Transparent");
            ltslot      = Shader.Find("Hidden/" + n + "/Lite/OnePassTransparent");
            ltsltt      = Shader.Find("Hidden/" + n + "/Lite/TwoPassTransparent");

            ltslo       = Shader.Find("Hidden/" + n + "/Lite/OpaqueOutline");
            ltslco      = Shader.Find("Hidden/" + n + "/Lite/CutoutOutline");
            ltslto      = Shader.Find("Hidden/" + n + "/Lite/TransparentOutline");
            ltsloto     = Shader.Find("Hidden/" + n + "/Lite/OnePassTransparentOutline");
            ltsltto     = Shader.Find("Hidden/" + n + "/Lite/TwoPassTransparentOutline");

            ltsref      = Shader.Find("Hidden/" + n + "/Refraction");
            ltsrefb     = Shader.Find("Hidden/" + n + "/RefractionBlur");
            ltsfur      = Shader.Find("Hidden/" + n + "/Fur");
            ltsfurc     = Shader.Find("Hidden/" + n + "/FurCutout");
            ltsfurtwo   = Shader.Find("Hidden/" + n + "/FurTwoPass");
            ltsfuro     = Shader.Find(n + "/[Optional] FurOnly/Transparent");
            ltsfuroc    = Shader.Find(n + "/[Optional] FurOnly/Cutout");
            ltsfurotwo  = Shader.Find(n + "/[Optional] FurOnly/TwoPass");
            ltsgem      = Shader.Find("Hidden/" + n + "/Gem");
            ltsfs       = Shader.Find(n + "/[Optional] FakeShadow");

            ltsover     = Shader.Find(n + "/[Optional] Overlay");
            ltsoover    = Shader.Find(n + "/[Optional] OverlayOnePass");
            ltslover    = Shader.Find(n + "/[Optional] LiteOverlay");
            ltsloover   = Shader.Find(n + "/[Optional] LiteOverlayOnePass");

            ltsm        = Shader.Find(n + "/lilToonMulti");
            ltsmo       = Shader.Find("Hidden/" + n + "/MultiOutline");
            ltsmref     = Shader.Find("Hidden/" + n + "/MultiRefraction");
            ltsmfur     = Shader.Find("Hidden/" + n + "/MultiFur");
            ltsmgem     = Shader.Find("Hidden/" + n + "/MultiGem");
        }
    }
}
#endif
