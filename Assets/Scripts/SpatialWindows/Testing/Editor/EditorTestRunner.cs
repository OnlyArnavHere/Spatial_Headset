using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Runs a play-mode test written as an iterator, one step per editor update.
    /// <c>yield return 0.5f</c> waits half a second; <c>yield return null</c> waits one update;
    /// <c>yield return Helper()</c> runs another iterator to completion before continuing, like a nested coroutine.
    /// Results are logged as "[Name] PASS/FAIL: ..." and summarised at the end.
    /// </summary>
    static class EditorTestRunner
    {
        // The running test and any helper iterators it is waiting on (top = innermost).
        static readonly Stack<IEnumerator> routines = new Stack<IEnumerator>();
        static double resumeAt;
        static int passed, failed;
        static string testName;
        static bool needsPlayMode = true;

        public static bool IsRunning => routines.Count > 0;
        public static double Now => EditorApplication.timeSinceStartup;

        /// <summary>Suites of pure logic pass <paramref name="playMode"/> false and run in Edit mode.</summary>
        public static void Run(string name, IEnumerator steps, bool playMode = true)
        {
            if (IsRunning)
            {
                Debug.LogWarning($"[{testName}] A test is already running.");
                return;
            }
            testName = name;
            needsPlayMode = playMode;
            passed = failed = 0;
            routines.Push(steps);
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
            if (needsPlayMode && !Application.isPlaying)
            {
                Finish(" (aborted: left Play mode)");
                return;
            }
            if (Now < resumeAt) return;

            var routine = routines.Peek();
            bool more;
            try
            {
                more = routine.MoveNext();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                failed++;
                Finish("");
                return;
            }

            if (!more)
            {
                // A finished helper hands control back to its caller on the next update.
                (routines.Pop() as IDisposable)?.Dispose();
                if (routines.Count == 0) Finish("");
            }
            else if (routine.Current is IEnumerator nested) routines.Push(nested);
            else if (routine.Current is float seconds) resumeAt = Now + seconds;
        }

        static void Finish(string note)
        {
            EditorApplication.update -= Tick;
            // Runs any finally blocks in the test (used to restore scene state), innermost first.
            while (routines.Count > 0) (routines.Pop() as IDisposable)?.Dispose();
            Debug.Log($"[{testName}] Done{note}: {passed} passed, {failed} failed -> {(failed == 0 ? "PASS" : "FAIL")}");
        }
    }
}
