using System;
using System.Collections;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Runs a play-mode test written as an iterator, one step per editor update.
    /// <c>yield return 0.5f</c> waits half a second; <c>yield return null</c> waits one update.
    /// Results are logged as "[Name] PASS/FAIL: ..." and summarised at the end.
    /// </summary>
    static class EditorTestRunner
    {
        static IEnumerator routine;
        static double resumeAt;
        static int passed, failed;
        static string testName;

        public static bool IsRunning => routine != null;
        public static double Now => EditorApplication.timeSinceStartup;

        public static void Run(string name, IEnumerator steps)
        {
            if (IsRunning)
            {
                Debug.LogWarning($"[{testName}] A test is already running.");
                return;
            }
            testName = name;
            passed = failed = 0;
            routine = steps;
            resumeAt = 0;
            EditorApplication.update += Tick;
            Debug.Log($"[{testName}] Started.");
        }

        public static void Check(bool condition, string what)
        {
            if (condition) passed++;
            else failed++;
            Debug.Log($"[{testName}] {(condition ? "PASS" : "FAIL")}: {what}");
        }

        static void Tick()
        {
            if (!Application.isPlaying)
            {
                Finish(" (aborted: left Play mode)");
                return;
            }
            if (Now < resumeAt) return;

            bool more;
            try
            {
                more = routine.MoveNext();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                failed++;
                more = false;
            }

            if (!more) Finish("");
            else if (routine.Current is float seconds) resumeAt = Now + seconds;
        }

        static void Finish(string note)
        {
            EditorApplication.update -= Tick;
            // Runs any finally blocks in the test (used to restore scene state).
            (routine as IDisposable)?.Dispose();
            routine = null;
            Debug.Log($"[{testName}] Done{note}: {passed} passed, {failed} failed -> {(failed == 0 ? "PASS" : "FAIL")}");
        }
    }
}
