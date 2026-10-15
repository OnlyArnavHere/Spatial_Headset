#if SPATIAL_LAUNCHER_WEBRTC
using System;
using System.Collections;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Receives a video stream over WebRTC from a WHEP endpoint (MediaMTX: http://host:8889/path/whep) and exposes it
    /// as the <see cref="Texture"/>. Signaling is WHEP: one HTTP POST of the SDP offer (sent once ICE gathering has
    /// finished, so no trickle ICE), the SDP answer comes back, and DELETE on the session URL ends it.
    /// While running it reconnects automatically after a failure, with a growing delay (1 s up to 10 s), except after
    /// a wrong token, where retrying can't help.
    /// Needs a MonoBehaviour to run coroutines on; call <see cref="Tick"/> every frame.
    /// Compiled only with the SPATIAL_LAUNCHER_WEBRTC scripting define (and the com.unity.webrtc package).
    /// </summary>
    public class WebRtcStreamSource : IRemoteVideoSource
    {

        const float GatheringTimeout = 2f;
        const int RequestTimeoutSeconds = 5;
        const float FirstFrameTimeout = 10f;
        const float DisconnectedGrace = 3f;
        const float MaxRetryDelay = 10f;

        readonly MonoBehaviour host;
        readonly Vector2Int expectedSize;

        public string Url { get; set; }
        public string User { get; set; } = "viewer";
        public string Password { get; set; } = "";

        public Texture Texture { get; private set; }
        public Rect UVRect => new Rect(0f, 0f, 1f, 1f);
        public Vector2Int PixelSize => Texture != null ? new Vector2Int(Texture.width, Texture.height) : Vector2Int.zero;
        public float Aspect => Texture != null && Texture.height > 0
            ? Texture.width / (float)Texture.height
            : expectedSize.x / (float)Mathf.Max(1, expectedSize.y);
        public IContentInput Input => null;
        public bool IsRunning { get; private set; }

        public RemoteVideoState CurrentState { get; private set; } = RemoteVideoState.Stopped;
        public bool IsConnected => CurrentState == RemoteVideoState.Receiving && Texture != null;
        public string StatusText { get; private set; } = "Not connected";
        /// <summary>How many times the stream has been (re)established since <see cref="Start"/>.</summary>
        public int ConnectCount { get; private set; }

        public event Action Changed;

        Coroutine loop;
        RTCPeerConnection peer;
        VideoStreamTrack track;
        string sessionUrl;
        RTCPeerConnectionState connectionState;
        bool dropRequested;
        Vector2Int lastSize;

        public WebRtcStreamSource(MonoBehaviour host, Vector2Int expectedSize)
        {
            this.host = host;
            this.expectedSize = expectedSize.x > 0 && expectedSize.y > 0 ? expectedSize : new Vector2Int(1920, 1080);
        }

        public void Start()
        {
            if (IsRunning) return;
            if (string.IsNullOrEmpty(Url)) throw new InvalidOperationException("Set Url before starting the stream.");
            if (!WebRtcUpdater.IsUsable)
            {
                SetState(RemoteVideoState.Failed, "Scripts were reloaded during Play mode: restart Play mode to stream");
                return;
            }
            IsRunning = true;
            ConnectCount = 0;
            WebRtcUpdater.Acquire();
            loop = host.StartCoroutine(ConnectLoop());
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            if (loop != null && host != null) host.StopCoroutine(loop);
            loop = null;
            Teardown(endSession: true);
            WebRtcUpdater.Release();
            SetState(RemoteVideoState.Stopped, "Not connected");
        }

        /// <summary>Closes the current connection as if the network had dropped; the source reconnects (for tests).</summary>
        public void SimulateDrop() => dropRequested = true;

        /// <summary>Notices resolution changes (the decoder may replace its texture). Call once per frame.</summary>
        public void Tick()
        {
            var current = track != null ? track.Texture : null;
            if (current == null || CurrentState != RemoteVideoState.Receiving) return;
            var size = new Vector2Int(current.width, current.height);
            if (current == Texture && size == lastSize) return;
            Texture = current;
            lastSize = size;
            Changed?.Invoke();
        }

        IEnumerator ConnectLoop()
        {
            float retryDelay = 1f;
            while (IsRunning)
            {
                SetState(RemoteVideoState.Connecting, $"Connecting to {HostOf(Url)}…");
                var attempt = new Attempt();
                yield return Connect(attempt);

                if (attempt.Error == null)
                {
                    float started = Time.realtimeSinceStartup;
                    float disconnectedSince = -1f;
                    while (IsRunning && !dropRequested)
                    {
                        if (CurrentState == RemoteVideoState.WaitingForVideo && Time.realtimeSinceStartup - started > FirstFrameTimeout)
                        {
                            attempt.Error = "No video arrived (is the captured window minimized?)";
                            break;
                        }
                        if (connectionState == RTCPeerConnectionState.Failed || connectionState == RTCPeerConnectionState.Closed)
                        {
                            attempt.Error = "Connection lost";
                            break;
                        }
                        if (connectionState == RTCPeerConnectionState.Disconnected)
                        {
                            if (disconnectedSince < 0f) disconnectedSince = Time.realtimeSinceStartup;
                            else if (Time.realtimeSinceStartup - disconnectedSince > DisconnectedGrace)
                            {
                                attempt.Error = "Connection lost";
                                break;
                            }
                        }
                        else disconnectedSince = -1f;

                        if (CurrentState == RemoteVideoState.Receiving) retryDelay = 1f; // a working connection resets the backoff
                        yield return null;
                    }
                    if (dropRequested)
                    {
                        dropRequested = false;
                        attempt.Error = "Connection dropped";
                    }
                }

                Teardown(endSession: true);
                if (!IsRunning) yield break;
                if (attempt.Fatal)
                {
                    SetState(RemoteVideoState.Failed, attempt.Error);
                    yield break;
                }

                for (float wait = retryDelay; wait > 0f && IsRunning; wait -= 0.25f)
                {
                    SetState(RemoteVideoState.Retrying, $"{attempt.Error}. Retrying in {Mathf.CeilToInt(wait)} s");
                    yield return new WaitForSecondsRealtime(Mathf.Min(0.25f, wait));
                }
                retryDelay = Mathf.Min(MaxRetryDelay, retryDelay * 2f);
            }
        }

        sealed class Attempt
        {
            public string Error;
            public bool Fatal;
        }

        IEnumerator Connect(Attempt attempt)
        {
            var configuration = new RTCConfiguration { iceServers = Array.Empty<RTCIceServer>() }; // same network: no STUN/TURN
            peer = new RTCPeerConnection(ref configuration);
            connectionState = RTCPeerConnectionState.New;
            peer.OnConnectionStateChange = state => connectionState = state;
            peer.OnTrack = e =>
            {
                if (!(e.Track is VideoStreamTrack video)) return;
                track = video;
                video.OnVideoReceived += texture =>
                {
                    Texture = texture;
                    lastSize = new Vector2Int(texture.width, texture.height);
                    ConnectCount++;
                    SetState(RemoteVideoState.Receiving, $"Streaming {texture.width}x{texture.height}");
                };
            };
            peer.AddTransceiver(TrackKind.Video, new RTCRtpTransceiverInit { direction = RTCRtpTransceiverDirection.RecvOnly });

            var offer = peer.CreateOffer();
            yield return offer;
            if (offer.IsError)
            {
                attempt.Error = $"Couldn't create an offer ({offer.Error.message})";
                yield break;
            }
            var description = offer.Desc;
            var setLocal = peer.SetLocalDescription(ref description);
            yield return setLocal;
            if (setLocal.IsError)
            {
                attempt.Error = $"Couldn't apply the offer ({setLocal.Error.message})";
                yield break;
            }

            // WHEP without trickle ICE: wait until the offer carries our candidates.
            float deadline = Time.realtimeSinceStartup + GatheringTimeout;
            while (peer.GatheringState != RTCIceGatheringState.Complete && Time.realtimeSinceStartup < deadline)
                yield return null;

            using var request = new UnityWebRequest(Url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(peer.LocalDescription.sdp)) { contentType = "application/sdp" },
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = RequestTimeoutSeconds,
            };
            request.SetRequestHeader("Authorization", BasicAuth(User, Password));
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                attempt.Error = $"Can't reach {HostOf(Url)} (is the agent running?)";
                yield break;
            }
            if (request.responseCode == 401)
            {
                attempt.Error = "Wrong token (the agent prints a new one every run)";
                attempt.Fatal = true;
                yield break;
            }
            if (request.responseCode == 404)
            {
                attempt.Error = "No stream on the laptop yet";
                yield break;
            }
            if (request.responseCode != 201 && request.responseCode != 200)
            {
                attempt.Error = $"The laptop refused the stream (HTTP {request.responseCode}: {Trim(request.downloadHandler.text)})";
                yield break;
            }

            sessionUrl = ResolveLocation(Url, request.GetResponseHeader("Location"));
            var answer = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = request.downloadHandler.text };
            var setRemote = peer.SetRemoteDescription(ref answer);
            yield return setRemote;
            if (setRemote.IsError)
            {
                // Usually no common codec: Unity decodes H.264 in hardware only (NVIDIA on Windows, VideoToolbox on iOS).
                attempt.Error = $"Couldn't use the laptop's stream ({setRemote.Error.message})";
                attempt.Fatal = true;
                yield break;
            }
            SetState(RemoteVideoState.WaitingForVideo, "Connected, waiting for video…");
        }

        void Teardown(bool endSession)
        {
            if (endSession && !string.IsNullOrEmpty(sessionUrl))
            {
                // Tell MediaMTX the session is over; fire and forget (it also times sessions out on its own).
                var delete = UnityWebRequest.Delete(sessionUrl);
                delete.SetRequestHeader("Authorization", BasicAuth(User, Password));
                delete.timeout = 2;
                delete.SendWebRequest().completed += _ => delete.Dispose();
            }
            sessionUrl = null;

            track?.Dispose();
            track = null;
            if (peer != null)
            {
                peer.OnTrack = null;
                peer.OnConnectionStateChange = null;
                peer.Close();
                peer.Dispose();
                peer = null;
            }
            if (Texture != null)
            {
                Texture = null;
                Changed?.Invoke();
            }
        }

        void SetState(RemoteVideoState state, string text)
        {
            if (state == CurrentState && text == StatusText) return;
            CurrentState = state;
            StatusText = text;
            Changed?.Invoke();
        }

        static string BasicAuth(string user, string password) =>
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

        static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Host}:{uri.Port}" : url;

        static string ResolveLocation(string requestUrl, string location)
        {
            if (string.IsNullOrEmpty(location)) return null;
            return Uri.TryCreate(new Uri(requestUrl), location, out var absolute) ? absolute.ToString() : null;
        }

        static string Trim(string text) => string.IsNullOrEmpty(text) ? "" : text.Length > 80 ? text.Substring(0, 80) + "…" : text.Trim();
    }
}
#endif
