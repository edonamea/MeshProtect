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
