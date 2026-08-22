#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace MPTest
{
    /// <summary>
    /// The localization tables and the code that reads them, checked against each other.
    ///
    /// Everything here is file reading - no scenes, no bakes - because the failures it hunts are
    /// all editing accidents: a key renamed in code but not in the tables, a translation added to
    /// three files out of four, a {0} lost in translation so string.Format throws in a dialog
    /// nobody rehearses. Each of those survives compilation and appears at the worst time, in a
    /// language the author of the change may not read.
    /// </summary>
    public static class LocalizationTest
    {
        private static readonly string[] Languages = { "en", "zh-Hans", "ja", "ko" };

        public static void Run() => EditorApplication.Exit(RunCore());

        public static int RunCore()
        {
            int failed = 0;
            var log = new System.Text.StringBuilder();
            void Check(bool ok, string what)
            {
                string line = (ok ? "PASS " : "FAIL ") + what;
                log.AppendLine(line);
                Debug.Log("[L10N] " + line);
                if (!ok) failed++;
            }
            int Finish()
            {
                Debug.Log(failed == 0 ? "[L10N] === PASS ===" : "[L10N] === FAIL (" + failed + ") ===");
                File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "l10n-result.txt"),
                                  log.ToString());
                return failed == 0 ? 0 : 1;
            }

            try
            {
                string editorFolder = EditorFolder();
                Check(editorFolder != null, "plugin Editor folder found");
                if (editorFolder == null) return Finish();

                // ---- every language file parses, and their key sets are identical ----
                var tables = new Dictionary<string, Dictionary<string, string>>();
                foreach (string code in Languages)
                {
                    string path = editorFolder + "/Localization/" + code + ".txt";
                    Check(File.Exists(path), code + ".txt exists");
                    tables[code] = File.Exists(path) ? Parse(path) : new Dictionary<string, string>();
                    Check(tables[code].Count > 0, code + ".txt has entries (" + tables[code].Count + ")");
                }

                var english = tables["en"];
                foreach (string code in Languages.Skip(1))
                {
                    var missing = english.Keys.Except(tables[code].Keys).ToList();
                    var extra = tables[code].Keys.Except(english.Keys).ToList();
                    Check(missing.Count == 0,
                          code + " carries every English key" +
                          (missing.Count == 0 ? "" : " - missing: " + string.Join(", ", missing)));
                    Check(extra.Count == 0,
                          code + " has no keys English lacks" +
                          (extra.Count == 0 ? "" : " - extra: " + string.Join(", ", extra)));
                }

                // ---- per key: format slots and the leading-space convention survive translation ----
                // A translation that drops {0} silently loses the value; one that invents {1} makes
                // string.Format throw inside a dialog. Toggle labels lead with a space (they sit
                // against the checkbox), and that is part of the string, so it must survive too.
                foreach (string key in english.Keys)
                {
                    var slots = FormatSlots(english[key]);
                    bool lead = english[key].StartsWith(" ", StringComparison.Ordinal);
                    foreach (string code in Languages.Skip(1))
                    {
                        if (!tables[code].TryGetValue(key, out string value)) continue;
                        if (!FormatSlots(value).SetEquals(slots))
                            Check(false, code + ":" + key + " keeps format slots {" +
                                         string.Join(",", slots) + "}");
                        if (value.StartsWith(" ", StringComparison.Ordinal) != lead)
                            Check(false, code + ":" + key + " keeps the leading-space convention");
                    }
                }
                Check(true, "format slots and leading spaces match across languages");

                // string.Format throws on any brace that is not a {n} slot, and the slot-set
                // comparison above cannot see a lone one. English included: its values feed the
                // same Format call through the fallback path.
                foreach (string code in Languages)
                    foreach (var kv in tables[code])
                        if (Regex.Replace(kv.Value, "\\{\\d+\\}", "").IndexOfAny(Braces) >= 0)
                            Check(false, code + ":" + kv.Key +
                                         " has a stray brace that would crash string.Format");
                Check(true, "no stray braces outside {n} slots in any language");

                // ---- every key the code asks for exists; every key in the table is asked for ----
                string sources = string.Join("\n",
                    Directory.GetFiles(editorFolder, "*.cs").Select(File.ReadAllText));

                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in Regex.Matches(sources,
                             "MeshProtectL10n\\s*\\.\\s*Tr[C]?\\s*\\(\\s*\"([^\"]+)\""))
                    used.Add(m.Groups[1].Value);
                foreach (Match m in Regex.Matches(sources,
                             "MeshProtectL10n\\s*\\.\\s*TrC\\s*\\(\\s*\"[^\"]+\"\\s*,\\s*\"([^\"]+)\""))
                    used.Add(m.Groups[1].Value);

                // The inspector's field list reaches TrC through a local helper, so those keys are
                // literals in a Field(name, label, tip) call rather than in a Tr(...) call.
                foreach (Match m in Regex.Matches(sources,
                             "Field\\s*\\(\\s*\"[^\"]+\"\\s*,\\s*\"([^\"]+)\"\\s*,\\s*\"([^\"]+)\"\\s*\\)"))
                {
                    used.Add(m.Groups[1].Value);
                    used.Add(m.Groups[2].Value);
                }

                Check(used.Count > 0, "found Tr()/TrC() calls with literal keys (" + used.Count + ")");

                var dangling = used.Where(k => !english.ContainsKey(k)).ToList();
                Check(dangling.Count == 0,
                      "every key the code asks for is in en.txt" +
                      (dangling.Count == 0 ? "" : " - dangling: " + string.Join(", ", dangling)));

                // The reverse direction is a plain quoted-substring search, because some keys reach
                // Tr() through a variable (the field list in the inspector). A key nobody quotes
                // anywhere is dead weight - or, worse, the surviving spelling of a typo in code.
                var dead = english.Keys.Where(k => !sources.Contains("\"" + k + "\"")).ToList();
                Check(dead.Count == 0,
                      "every key in en.txt is quoted somewhere in Editor sources" +
                      (dead.Count == 0 ? "" : " - unreferenced: " + string.Join(", ", dead)));

                return Finish();
            }
            catch (Exception e)
            {
                // Same convention as the sibling suites: an unhandled throw must not abort
                // RunAll before the other suites get their turn.
                Check(false, "EXCEPTION: " + e);
                return Finish();
            }
        }

        private static readonly char[] Braces = { '{', '}' };

        private static string EditorFolder()
        {
            foreach (var guid in AssetDatabase.FindAssets("MeshProtectL10n t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/MeshProtectL10n.cs", StringComparison.Ordinal)) continue;
                return Path.GetDirectoryName(path)?.Replace('\\', '/');
            }
            return null;
        }

        /// <summary>Same rules as MeshProtectL10n.Parse, restated here so a regression in the
        /// real parser and in the tables cannot cancel each other out.</summary>
        private static Dictionary<string, string> Parse(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                int split = line.IndexOf('=');
                if (split <= 0) continue;
                result[line.Substring(0, split).Trim()] =
                    line.Substring(split + 1).Replace("\\n", "\n");
            }
            return result;
        }

        private static HashSet<int> FormatSlots(string value)
        {
            var slots = new HashSet<int>();
            foreach (Match m in Regex.Matches(value, "\\{(\\d+)\\}"))
                slots.Add(int.Parse(m.Groups[1].Value));
            return slots;
        }
    }
}
#endif
