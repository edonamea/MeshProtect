// Inspector localization: Japanese, English, Simplified Chinese, Korean.
//
// What is localized is the surface the author interacts with - the inspector, its tooltips and
// the dialogs. Build warnings, the validator's messages, last-upload.txt and the Console stay
// English on purpose: they are what gets pasted into help threads, and the author answering
// those threads has to be able to read them. One language for evidence, four for controls.
//
// The mechanism follows lilToon's, because every user of this tool already uses lilToon: one
// text file per language next to this script, a language popup in the inspector, the choice
// stored per machine in EditorPrefs, and "Auto" following the OS language - with Japanese,
// not English, as the answer for languages the switch does not know. A key missing from a
// translation falls back to English rather than to a blank - a check in the test suite keeps
// the four files carrying identical key sets, so that fallback is for safety, not routine.
//
// The file format is one entry per line, "key=text", UTF-8, with \n standing for a line break.
// Deliberately not JSON: these files are edited by people translating prose, and a stray quote
// or brace must not be able to take the whole table down. Lines starting with '#' and lines
// without '=' are comments - the explicit marker matters, because a comment ABOUT the format
// is exactly the kind of line that contains an '='.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MeshProtect
{
    internal static class MeshProtectL10n
    {
        private const string PrefKey = "MeshProtect.Language";

        // Japanese leads: the tool is sold on Booth, so the dropdown mirrors the audience.
        // The stored pref is the code string, not the index, so reordering costs nobody
        // their saved choice.
        private static readonly string[] Codes = { "auto", "ja", "en", "zh-Hans", "ko" };
        private static readonly string[] Names = { "Auto", "日本語", "English", "简体中文", "한국어" };

        private static Dictionary<string, string> table;
        private static Dictionary<string, string> english;

        internal static string Language
        {
            get => EditorPrefs.GetString(PrefKey, "auto");
            set
            {
                EditorPrefs.SetString(PrefKey, value);
                table = null;
            }
        }

        /// <summary>The translation for a key, falling back to English, then to the key itself -
        /// a wrong key stays visible in the UI instead of rendering as nothing.</summary>
        internal static string Tr(string key)
        {
            Load();
            if (table != null && table.TryGetValue(key, out string s)) return s;
            if (english != null && english.TryGetValue(key, out string e)) return e;
            return key;
        }

        internal static string Tr(string key, params object[] args) => string.Format(Tr(key), args);

        internal static GUIContent TrC(string labelKey, string tooltipKey = null) =>
            new GUIContent(Tr(labelKey), tooltipKey == null ? null : Tr(tooltipKey));

        /// <summary>The language popup, drawn at the top of the inspector. The label names every
        /// language rather than translating "Language", so it is findable from any of them.</summary>
        internal static void DrawSelector()
        {
            int index = Array.IndexOf(Codes, Language);
            if (index < 0) index = 0;

            // The default label column (120px at narrow inspectors) clips the tail off this
            // label - and the tail is exactly the languages it exists to serve. 170px fits the
            // whole string at every inspector width, and the popup still has ample field room.
            float prev = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 170f;
            int chosen = EditorGUILayout.Popup(
                new GUIContent("言語 ⁄ Language ⁄ 语言 ⁄ 언어"), index, Names);
            EditorGUIUtility.labelWidth = prev;
            if (chosen != index) Language = Codes[chosen];
        }

        private static void Load()
        {
            // The warm path is just this null check. Tr runs dozens of times per repaint, and
            // resolving the language reads EditorPrefs - a registry read on Windows - so that
            // resolution happens only while the table is actually being built. The language
            // setter nulls the table, and the system language cannot change mid-session.
            if (table != null) return;

            // A FindAssets miss mid-refresh must never be cached as the session's answer -
            // leaving the table null costs a retry per Tr only while the miss lasts.
            string folder = LocalizationFolder();
            if (folder == null) return;

            string code = ResolveCode();
            english = Parse(folder + "/en.txt");
            table = code == "en" ? english : Parse(folder + "/" + code + ".txt");
        }

        private static string ResolveCode()
        {
            string pref = Language;
            if (pref != "auto") return pref;

            switch (Application.systemLanguage)
            {
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.ChineseTraditional:
                    return "zh-Hans";
                case SystemLanguage.English: return "en";
                case SystemLanguage.Korean: return "ko";
                // Japanese, and also everything unmatched: the tool ships on Booth, where an
                // OS language this switch does not know is far more often a Japanese user on a
                // differently-set PC than anyone the English table would serve better.
                default: return "ja";
            }
        }

        private static string LocalizationFolder()
        {
            foreach (var guid in AssetDatabase.FindAssets("MeshProtectL10n t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/MeshProtectL10n.cs", StringComparison.Ordinal)) continue;
                return Path.GetDirectoryName(path)?.Replace('\\', '/') + "/Localization";
            }
            return null;
        }

        private static Dictionary<string, string> Parse(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (path == null || !File.Exists(path)) return result;

            foreach (string line in File.ReadAllLines(path))
            {
                if (line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;

                int split = line.IndexOf('=');
                if (split <= 0) continue;

                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Replace("\\n", "\n");
                result[key] = value;
            }
            return result;
        }
    }
}
#endif
