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

        [MenuItem("Tools/Spatial Windows/Test Content Windows")]
        static void Run() => EditorTestRunner.Run("ContentTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Content Windows", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

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

            // Clicks go through MouseWindowInput, so it must be the active input; restore the user's choice after.
            int previousInput = WindowTestUtil.SelectInput<MouseWindowInput>(manager);
            var body = Body(launcherWindow, manager, input, cam);
            try
            {
                while (body.MoveNext()) yield return body.Current;
            }
            finally
            {
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
            }
        }

        static IEnumerator Body(LauncherWindow launcherWindow, WindowManager manager, MouseWindowInput input, Camera cam)
        {

            // Launcher
            launcherWindow.OpenLauncher();
            yield return Settle;
            var launcherContent = launcherWindow.LauncherContent;
            // Image, Video, Blank, Laptop, then the Void, Cinema and Sky environments (ImmersionTest covers those).
            Check(launcherContent != null && launcherContent.Buttons.Count == 7, "launcher shows 7 icons (4 apps, 3 environments)");
            if (launcherContent == null) yield break;

            // Each icon opens its window type.
            string[] titles = { "Image", "Video", "Blank", "Laptop" };
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
            var laptopWindow = opened[3];

            // Laptop: in WebRTC mode the icon opens the picker, whose entries open stream windows; check one of those
            // (opened as the picker would, without connecting). In Placeholder mode the icon opens the screen itself.
            var picker = laptopWindow != null ? laptopWindow.GetComponentInChildren<LaptopPickerContent>() : null;
            if (picker != null)
            {
                Check(picker.HostField != null && picker.PortField != null && picker.TokenField != null && picker.ListButton != null
                      && picker.TokenField.Masked, "the Laptop icon opens the picker: host, port, masked token fields and a List button");
                Check(picker.EntryButtons.Count == 0 && !picker.MoreButton.gameObject.activeSelf, "the picker lists nothing before List");
                var testTarget = new LaptopTarget { id = "window:1", path = "test-window", title = "Test window", kind = "window", width = 1280, height = 720 };
                laptopWindow = launcherWindow.OpenLaptopStream(testTarget);
                yield return Settle;
                Check(laptopWindow != null && laptopWindow.Title == "Test window", "a picked target opens a stream window titled after it");
            }
            var remote = laptopWindow != null ? laptopWindow.GetComponentInChildren<RemoteStreamContent>() : null;
            Check(remote != null, "Laptop window has RemoteStreamContent");
            if (remote != null)
            {
                Check(remote.StatusText == "Not connected" && remote.Source.Texture == null && !remote.Source.IsConnected && remote.Source.IsRunning,
                    $"Laptop shows the \"Not connected\" placeholder (status \"{remote.StatusText}\")");
                Check(AspectMatches(remote.DisplaySize, remote.Source.Aspect) && Mathf.Abs(remote.Source.Aspect - 16f / 9f) < 0.01f,
                    $"Laptop screen keeps the remote's 16:9 aspect ({Ratio(remote.DisplaySize):F3} shown)");
                Check(remote.Surface.GetComponent<Collider>() != null, "the Laptop surface is interactive (has a collider)");

                // Click a quarter in from the top-right through the mouse input's click path.
                var sink = new RecordingSink();
                var originalSink = remote.Source.Sink;
                remote.Source.Sink = sink;
                var surface = remote.Surface.transform;
                manager.Focus(laptopWindow);
                yield return Settle;
                var clickPoint = surface.TransformPoint(new Vector3(0.25f, 0.25f, 0f));
                Click(input, cam, clickPoint);
                // Texture convention (0.75, 0.75) is screen convention (0.75, 0.25): the sink gets top-left origin.
                var expected = new Vector2(0.75f, 0.25f);
                Check(sink.Downs.Count == 1 && Near(sink.Downs[0], expected),
                    $"a click on the Laptop screen sends one press to the sink at {Format(sink.Downs)} (expected {expected})");
                Check(sink.Ups.Count == 1 && Near(sink.Ups[0], expected), $"and one release at {Format(sink.Ups)}");

                // Hover and scroll, as the mouse and hand inputs deliver them.
                var hoverPoint = surface.TransformPoint(new Vector3(-0.4f, -0.3f, 0f));
                remote.Surface.Hover(hoverPoint);
                remote.Surface.Scroll(hoverPoint, -2f);
                var hoverExpected = new Vector2(0.1f, 0.8f);
                Check(sink.Moves.Count > 0 && Near(sink.Moves[sink.Moves.Count - 1], hoverExpected),
                    $"hover sends a move at {Format(sink.Moves)} (expected last {hoverExpected})");
                Check(sink.Scrolls.Count == 1 && Mathf.Approximately(sink.Scrolls[0].z, -2f) && Near(sink.Scrolls[0], hoverExpected),
                    "scroll sends -2 notches at the hovered point");

                remote.Source.Sink = originalSink;

                // Connected mode (WebRTC): the connect panel, and a failed connection that explains itself and retries.
                if (remote.Video != null)
                {
                    Check(remote.HostField != null && remote.PortField != null && remote.TokenField != null && remote.ConnectButton != null,
                        "the Laptop window has host, port and token fields and a Connect button");
                    Check(remote.TokenField.Masked, "the token field is masked");

                    string savedHost = RemoteConnectionPrefs.Host(null), savedToken = RemoteConnectionPrefs.Token();
                    int savedPort = RemoteConnectionPrefs.VideoPort(null);
                    remote.HostField.SetText("127.0.0.1");
                    remote.PortField.SetText("1"); // nothing listens on port 1
                    remote.TokenField.SetText("test-token");
                    manager.Focus(laptopWindow);
                    yield return Settle;
                    Click(input, cam, remote.ConnectButton.transform.position);
                    double deadline = EditorTestRunner.Now + 8.0;
                    while (remote.Video.CurrentState != RemoteVideoState.Retrying && EditorTestRunner.Now < deadline)
                        yield return 0.1f;
                    Check(remote.IsActive && remote.Video.CurrentState == RemoteVideoState.Retrying &&
                          remote.Video.StatusText.Contains("Can't reach"),
                        $"connecting to a closed port explains itself and retries (\"{remote.Video.StatusText}\")");
                    Check(remote.StatusLine.Contains("Video:") && remote.StatusLine.Contains("Input:"),
                        $"the status line shows video and input state (\"{remote.StatusLine}\")");

                    Click(input, cam, remote.ConnectButton.transform.position);
                    yield return 0.2f;
                    Check(!remote.IsActive && remote.Video.CurrentState == RemoteVideoState.Stopped && remote.StatusText == "Not connected",
                        "Disconnect stops trying and shows \"Not connected\" again");
                    RemoteConnectionPrefs.Save(savedHost, savedPort, savedToken);
                }
            }

            // Display-only content keeps clicks on the window body (selecting it), so its surface has no collider.
            var imageSurface = imageWindow != null ? imageWindow.GetComponentInChildren<ContentSurface>() : null;
            Check(imageSurface != null && imageSurface.GetComponent<Collider>() == null && imageSurface.Source is TextureSource,
                "the Image window shows a TextureSource on a display-only surface");
            var videoSurface = videoWindow != null ? videoWindow.GetComponentInChildren<ContentSurface>() : null;
            Check(videoSurface != null && videoSurface.Source is VideoPlayerSource, "the Video window shows a VideoPlayerSource");

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

                // VideoPlayer.Play/Pause take effect asynchronously (longer on a cold decoder), so poll briefly.
                Click(input, cam, video.PlayPauseButton.transform.position);
                double pauseDeadline = EditorTestRunner.Now + 2.0;
                while (video.IsPlaying && EditorTestRunner.Now < pauseDeadline) yield return 0.05f;
                Check(!video.IsPlaying, "play/pause button pauses");
                double pausedAt = video.Player.time;
                yield return 0.4f;
                Check(Math.Abs(video.Player.time - pausedAt) < 0.05, "time holds while paused");

                Click(input, cam, video.PlayPauseButton.transform.position);
                double resumeDeadline = EditorTestRunner.Now + 2.0;
                while (!video.IsPlaying && EditorTestRunner.Now < resumeDeadline) yield return 0.05f;
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
        static bool Near(Vector2 a, Vector2 b) => Vector2.Distance(a, b) < 0.01f;
        static bool Near(Vector3 scroll, Vector2 b) => Near(new Vector2(scroll.x, scroll.y), b);
        static string Format(System.Collections.Generic.List<Vector2> points) =>
            points.Count == 0 ? "none" : string.Join(", ", points.ConvertAll(p => $"({p.x:F3}, {p.y:F3})"));

        /// <summary>Records what a remote screen would receive.</summary>
        sealed class RecordingSink : IRemoteInputSink
        {
            public readonly System.Collections.Generic.List<Vector2> Moves = new System.Collections.Generic.List<Vector2>();
            public readonly System.Collections.Generic.List<Vector2> Downs = new System.Collections.Generic.List<Vector2>();
            public readonly System.Collections.Generic.List<Vector2> Ups = new System.Collections.Generic.List<Vector2>();
            /// <summary>x, y = position; z = delta.</summary>
            public readonly System.Collections.Generic.List<Vector3> Scrolls = new System.Collections.Generic.List<Vector3>();

            public void PointerMove(Vector2 position) => Moves.Add(position);
            public void PointerDown(Vector2 position) => Downs.Add(position);
            public void PointerUp(Vector2 position) => Ups.Add(position);
            public void Scroll(Vector2 position, float delta) => Scrolls.Add(new Vector3(position.x, position.y, delta));
        }
    }
}
