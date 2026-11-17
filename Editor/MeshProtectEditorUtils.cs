#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;

namespace MeshProtect
{
    /// <summary>Shared by the menu installer and its inspector preview.</summary>
    internal static class MeshProtectMenuPath
    {
        internal static string[] Parse(string path)
        {
            path = path ?? "";
            var segments = new List<string>();
            var segment = new StringBuilder();
            void FinishSegment()
            {
                string value = segment.ToString().Trim();
                if (value.Length > 0) segments.Add(value);
                segment.Clear();
            }

            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (c == '\\' && i + 1 < path.Length &&
                    (path[i + 1] == '/' || path[i + 1] == '\\'))
                {
                    segment.Append(path[++i]);
                }
                else if (c == '<')
                {
                    // A closing rich-text tag contains '/', but is part of the control's name.
                    int end = path.IndexOf('>', i + 1);
                    if (end < 0) segment.Append(c);
                    else
                    {
                        segment.Append(path, i, end - i + 1);
                        i = end;
                    }
                }
                else if (c == '/') FinishSegment();
                else segment.Append(c);
            }
            FinishSegment();
            return segments.ToArray();
        }

        internal static int[] MatchIndices(IReadOnlyList<string> names, string segment)
        {
            var exact = Enumerable.Range(0, names.Count)
                .Where(i => string.Equals(names[i], segment, StringComparison.Ordinal)).ToArray();
            if (exact.Length > 0) return exact;

            string visible = VisibleName(segment);
            if (visible.Length == 0) return new int[0];
            return Enumerable.Range(0, names.Count)
                .Where(i => string.Equals(VisibleName(names[i]), visible,
                                          StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        private static string VisibleName(string name)
        {
            string plain = Regex.Replace(name ?? "", "<[^>]*>", "").Replace("\\n", " ");
            return Regex.Replace(plain, @"\s+", " ").Trim();
        }
    }

    /// <summary>
    /// Which password the last build of each avatar was keyed to - as its check code, never
    /// the digits.
    ///
    /// The password field on the component is live: a keystroke, the Random Password button or
    /// an undo changes what the NEXT upload uses, while the avatar already in VRChat goes on
    /// opening with the one it was uploaded with. Nothing on screen distinguishes the two, so
    /// an author who changes the field after uploading reads the new digits off the component,
    /// enters them in game, and the avatar does not appear. Remembering what the last build
    /// used lets the inspector say so.
    ///
    /// Per machine and keyed by the protection id: it is a convenience for whoever is sitting
    /// at this editor, not part of the avatar, and it must not travel with the project to
    /// somebody who never built it.
    /// </summary>
    internal static class MeshProtectBuildMemory
    {
        private const string Prefix = "MeshProtect.LastBuildCheck.";

        // The inspector asks on every repaint, and EditorPrefs is a registry read on Windows.
        private static readonly Dictionary<string, string> known = new Dictionary<string, string>();

        internal static void Remember(MeshProtectVariant variant, string checkCode)
        {
            if (variant == null || string.IsNullOrEmpty(variant.shaderName) ||
                string.IsNullOrEmpty(checkCode)) return;

            known[variant.shaderName] = checkCode;

            // Not from a batch run. Nobody is going to open an inspector on the result, and a
            // test suite that builds a few dozen throwaway avatars would leave one registry
            // value behind for each of them.
            if (UnityEngine.Application.isBatchMode) return;

            EditorPrefs.SetString(Prefix + variant.shaderName, checkCode);
        }

        /// <summary>The check code of the last build on this machine, or null if there was none.</summary>
        internal static string Recall(MeshProtectVariant variant)
        {
            if (variant == null || string.IsNullOrEmpty(variant.shaderName)) return null;

            if (!known.TryGetValue(variant.shaderName, out string code))
            {
                code = EditorPrefs.GetString(Prefix + variant.shaderName, "");
                known[variant.shaderName] = code;
            }
            return string.IsNullOrEmpty(code) ? null : code;
        }
    }

    /// <summary>Persist direct inspector writes while the caller manages the Undo action.</summary>
    internal static class MeshProtectEditorSettings
    {
        internal static void Persist(MeshProtectRoot settings)
        {
            if (settings == null) return;
            EditorUtility.SetDirty(settings);
            if (PrefabUtility.IsPartOfPrefabInstance(settings))
                PrefabUtility.RecordPrefabInstancePropertyModifications(settings);
        }
    }
}
#endif
