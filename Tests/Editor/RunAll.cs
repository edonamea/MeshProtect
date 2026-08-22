#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace MPTest
{
    /// <summary>
    /// Every suite in ONE Unity launch.
    ///
    /// Each launch costs an editor start, an asset import and a script compile before a line of
    /// test code runs, and that dominates the wall clock - the suites themselves are fast. Running
    /// them four times over was most of the cost of checking a one-line change.
    ///
    /// Every suite runs even when an earlier one fails, because "the first failure" is rarely the
    /// whole story and a second launch to find out costs what this class exists to avoid.
    /// </summary>
    public static class RunAll
    {
        public static void Run()
        {
            int failed = 0;

            failed += Report("LocalizationTest",     LocalizationTest.RunCore());
            failed += Report("EndToEndTest",         EndToEndTest.RunCore());
            failed += Report("MeshProtectBatchTest", MeshProtectBatchTest.RunAllCore());
            failed += Report("UnlockChainTest",      UnlockChainTest.RunCore());
            failed += Report("BundleRoundTripTest",  BundleRoundTripTest.RunCore());

            Debug.Log(failed == 0
                ? "[RUNALL] === ALL SUITES PASSED ==="
                : $"[RUNALL] === {failed} SUITE(S) FAILED ===");

            EditorApplication.Exit(failed == 0 ? 0 : 1);
        }

        private static int Report(string name, int code)
        {
            Debug.Log($"[RUNALL] {name} -> exit {code}");
            return code == 0 ? 0 : 1;
        }
    }
}
#endif
