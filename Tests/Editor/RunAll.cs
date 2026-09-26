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
    [InitializeOnLoad]
    public static class RunAll
    {
        private const string PendingKey = "MeshProtect.RunAll.Pending";

        static RunAll()
        {
            // SDK setup can change scripting defines and reload the domain after -executeMethod.
            if (SessionState.GetBool(PendingKey, false))
                EditorApplication.delayCall += RunWhenReady;
        }

        public static void Run()
        {
            // -executeMethod runs inside FinishLoadingProject. Defer shader generation until the
            // first editor update, after importer registration and the initial domain reload.
            SessionState.SetBool(PendingKey, true);
            EditorApplication.delayCall += RunWhenReady;
        }

        private static void RunWhenReady()
        {
            if (!SessionState.GetBool(PendingKey, false)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += RunWhenReady;
                return;
            }
            SessionState.SetBool(PendingKey, false);
            RunSuites();
        }

        private static void RunSuites()
        {
            int failed = 0;

            failed += Report("LocalizationTest",     LocalizationTest.RunCore());
            failed += Report("LilHostTest",         LilHostTest.RunCore());
            failed += Report("EndToEndTest",         EndToEndTest.RunCore());
            failed += Report("OwnershipTest",        OwnershipTest.RunCore());
            failed += Report("IntegrationTest",      IntegrationTest.RunCore());
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
