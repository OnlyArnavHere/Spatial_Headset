using System;
using System.Collections;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for the launcher and content windows. Clicks go through MouseWindowInput's
    /// press/release path (the same code real mouse clicks use), spread over frames so physics and the
    /// focus animation settle. Results are logged with a [ContentTest] prefix.
    /// </summary>
    static class ContentWindowTest
    {
        const float Settle = 0.6f;
        const string SpritePath = "Assets/Samples/Images/SamplePortraitSprite.png";

        static IEnumerator routine;
        static double resumeAt;
        static int passed, failed;

        [MenuItem("Tools/Spatial Windows/Test Content Windows")]
        static void Run()
        {
            if (routine != null)
            {
                Debug.LogWarning("[ContentTest] Already running.");
                return;
            }
            passed = failed = 0;
            routine = Steps();
            resumeAt = 0;
            EditorApplication.update += Tick;
            Debug.Log("[ContentTest] Started.");
        }

        [MenuItem("Tools/Spatial Windows/Test Content Windows", true)]
        static bool CanRun() => Application.isPlaying;

        static void Tick()
        {
            if (!Application.isPlaying)
            {
                Finish(" (aborted: left Play mode)");
                return;
            }
            if (EditorApplication.timeSinceStartup < resumeAt) return;

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
            else if (routine.Current is float seconds) resumeAt = EditorApplication.timeSinceStartup + seconds;
        }

        static void Finish(string note)
        {
            EditorApplication.update -= Tick;
            routine = null;
            Debug.Log($"[ContentTest] Done{note}: {passed} passed, {failed} failed -> {(failed == 0 ? "PASS" : "FAIL")}");
        }

        static void Check(bool condition, string what)
        {
            if (condition) passed++;
            else failed++;
            Debug.Log($"[ContentTest] {(condition ? "PASS" : "FAIL")}: {what}");
        }

        static IEnumerator Steps()
        {
            var launcherWindow = UnityEngine.Object.FindFirstObjectByType<LauncherWindow>();
            var manager = UnityEngine.Object.FindFirstObjectByType<WindowManager>();
            var input = UnityEngine.Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            if (launcherWindow == null || manager == null || input == null || cam == null)
            {
                Check(false, "scene has a LauncherWindow, WindowManager, MouseWindowInput and main camera");
                yield break;
            }

            // Launcher
            launcherWindow.OpenLauncher();
            yield return Settle;
            var launcherContent = launcherWindow.LauncherContent;
            Check(launcherContent != null && launcherContent.Buttons.Count == 3, "launcher shows 3 icons");
            if (launcherContent == null) yield break;

            // Each icon opens its window type.
            string[] titles = { "Image", "Video", "Blank" };
            var opened = new SpatialWindow[titles.Length];
            for (int i = 0; i < titles.Length; i++)
            {
                launcherWindow.OpenLauncher(); // bring the launcher back to the front
                yield return Settle;
                int before = manager.Windows.Count;
                bool clicked = Click(input, cam, launcherContent.Buttons[i].transform.position);
                yield return Settle;
                opened[i] = manager.FocusedWindow;
                Check(clicked && manager.Windows.Count == before + 1 && opened[i] != null && opened[i].Title == titles[i],
                    $"clicking the {titles[i]} icon opens a {titles[i]} window");
            }
            var imageWindow = opened[0];
            var videoWindow = opened[1];
            var blankWindow = opened[2];

            // Image: aspect ratio kept at the initial size and after resizing both ways.
            var image = imageWindow != null ? imageWindow.GetComponentInChildren<ImageContent>() : null;
            Check(image != null && image.Texture != null, "Image window shows a texture");
            if (image != null)
            {
                // Compare against the source file, not the imported texture: importing with power-of-two
                // rescaling (Unity's default) squashes a 1200x800 image to 1024x1024.
                if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(image.Texture)) is TextureImporter importer)
                {
                    importer.GetSourceTextureWidthAndHeight(out int sourceWidth, out int sourceHeight);
                    float sourceAspect = sourceWidth / (float)sourceHeight;
                    Check(Mathf.Abs(image.Aspect - sourceAspect) < sourceAspect * 0.01f,
                        $"image aspect {image.Aspect:F3} matches the source file ({sourceWidth}x{sourceHeight}, {sourceAspect:F3})");
                }

                Check(AspectMatches(image.DisplaySize, image.Aspect), $"image keeps its {image.Aspect:F3} aspect ({Ratio(image.DisplaySize):F3} shown)");

                imageWindow.SetSize(new Vector2(0.9f, 0.3f));
                yield return null;
                Check(AspectMatches(image.DisplaySize, image.Aspect) && Fits(image.DisplaySize, imageWindow.Size, 0f),
                    $"after resizing wide, image keeps aspect and fits ({image.DisplaySize.x:F3} x {image.DisplaySize.y:F3})");

                imageWindow.SetSize(new Vector2(0.3f, 0.8f));
                yield return null;
                Check(AspectMatches(image.DisplaySize, image.Aspect) && Fits(image.DisplaySize, imageWindow.Size, 0f),
                    $"after resizing tall, image keeps aspect and fits ({image.DisplaySize.x:F3} x {image.DisplaySize.y:F3})");
            }

            // Sprite path, using the portrait sample (imported as a Sprite).
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpritePath);
            Check(sprite != null, $"sample sprite loads from {SpritePath}");
            if (sprite != null)
            {
                var spriteWindow = manager.Spawn("Sprite", new Vector2(0.4f, 0.5f));
                var spriteContent = new GameObject().AddComponent<ImageContent>();
                spriteContent.Init(spriteWindow, manager.WindowMaterial, sprite);
                yield return null;
                float spriteAspect = sprite.rect.width / sprite.rect.height;
                Check(Mathf.Approximately(spriteContent.Aspect, spriteAspect) && AspectMatches(spriteContent.DisplaySize, spriteAspect)
                      && Fits(spriteContent.DisplaySize, spriteWindow.Size, 0f),
                    $"sprite image keeps its {spriteAspect:F3} aspect ({Ratio(spriteContent.DisplaySize):F3} shown)");
                manager.Close(spriteWindow);
            }

            // Video: prepares, plays, keeps aspect, and the controls work.
            var video = videoWindow != null ? videoWindow.GetComponentInChildren<VideoContent>() : null;
            Check(video != null, "Video window has VideoContent");
            if (video != null)
            {
                double deadline = EditorApplication.timeSinceStartup + 5.0;
                while (!(video.IsPrepared && video.IsPlaying) && EditorApplication.timeSinceStartup < deadline)
                    yield return 0.1f;
                Check(video.IsPrepared && video.IsPlaying, "video prepares and starts playing");
                Check(AspectMatches(video.DisplaySize, video.Aspect), $"video keeps its {video.Aspect:F3} aspect ({Ratio(video.DisplaySize):F3} shown)");
                Check(Fits(video.DisplaySize, videoWindow.Size, VideoContent.ControlBarHeight), "video fits above the control bar");

                double start = video.Player.time;
                yield return 0.5f;
                Check(video.Player.time > start, $"video time advances ({start:F2}s -> {video.Player.time:F2}s)");

                manager.Focus(videoWindow);
                yield return Settle;

                Click(input, cam, video.PlayPauseButton.transform.position);
                yield return 0.3f;
                Check(!video.IsPlaying, "play/pause button pauses");
                double pausedAt = video.Player.time;
                yield return 0.4f;
                Check(Math.Abs(video.Player.time - pausedAt) < 0.05, "time holds while paused");

                Click(input, cam, video.PlayPauseButton.transform.position);
                yield return 0.3f;
                Check(video.IsPlaying, "play/pause button resumes");

                // Click three quarters of the way along the seek bar (a quad spans -0.5..0.5 locally).
                Click(input, cam, video.SeekBar.transform.TransformPoint(new Vector3(0.25f, 0f, 0f)));
                yield return 0.4f;
                double target = 0.75 * video.Player.length;
                Check(Math.Abs(video.Player.time - target) < 0.75,
                    $"seek bar jumps to 75% ({video.Player.time:F2}s, target {target:F2}s of {video.Player.length:F2}s)");
            }

            // Blank
            Check(blankWindow != null && blankWindow.ContentRoot.childCount == 0, "Blank window has no content");

            // Closing a video window cleans up without errors.
            if (videoWindow != null)
            {
                manager.Close(videoWindow);
                yield return 0.2f;
                Check(videoWindow == null, "video window closes");
            }
        }

        static bool Click(MouseWindowInput input, Camera cam, Vector3 worldPoint)
        {
            Vector3 screen = cam.WorldToScreenPoint(worldPoint);
            if (screen.z <= 0f) return false;
            input.PressAt(screen);
            input.ReleaseAt(screen);
            return true;
        }

        static float Ratio(Vector2 size) => size.y > 0f ? size.x / size.y : 0f;

        static bool AspectMatches(Vector2 size, float aspect) => Mathf.Abs(Ratio(size) - aspect) < aspect * 0.01f;

        // The displayed media must fit the window's content area (minus any extra height such as a control bar).
        static bool Fits(Vector2 display, Vector2 windowSize, float extraHeight)
        {
            const float tolerance = 1e-4f;
            return display.x <= windowSize.x - WindowContent.Padding * 2f + tolerance
                && display.y <= windowSize.y - WindowContent.Padding * 2f - extraHeight + tolerance;
        }
    }
}
