"""ffmpeg encoder settings shared by the backends."""


def common_args(fps: int, bitrate: int) -> list[str]:
    """Low-latency settings: constant bitrate, a small buffer, no B-frames (WebRTC can't use them) and a keyframe every
    second so a reconnecting headset gets a picture quickly. Keyframes go by time, not frame count: a static window
    delivers only a couple of frames a second."""
    b, buf, gop = str(bitrate), str(bitrate // 2), str(fps)
    return ["-b:v", b, "-maxrate", b, "-bufsize", buf, "-g", gop, "-bf", "0",
            "-force_key_frames", "expr:gte(t,n_forced*1)"]
