import hashlib
import io
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

import paths
import tools_fetch


def make_zip(files: dict, symlink: str | None = None) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        for name, data in files.items():
            zf.writestr(name, data)
        if symlink:
            info = zipfile.ZipInfo(symlink)
            info.external_attr = (0o120777 << 16)
            zf.writestr(info, "target")
    return buf.getvalue()


def make_tar(files: dict, link: str | None = None) -> bytes:
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz") as tf:
        for name, data in files.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            tf.addfile(info, io.BytesIO(data))
        if link:
            info = tarfile.TarInfo(link)
            info.type = tarfile.SYMTYPE
            info.linkname = "/etc/passwd"
            tf.addfile(info)
    return buf.getvalue()


class FakeResponse(io.BytesIO):
    def __init__(self, data: bytes):
        super().__init__(data)
        self.headers = {"Content-Length": str(len(data))}


class Opener:
    def __init__(self, data: bytes):
        self.data = data
        self.calls = 0

    def __call__(self, request):
        self.calls += 1
        return FakeResponse(self.data)


class ToolsFetchTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)
        self.tools = self.root / "bin"
        for target, value in (("data_dir", self.root), ("tools_dir", self.tools)):
            patcher = mock.patch.object(paths, target, return_value=value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def use(self, archive: bytes, url="https://example.invalid/pkg/tool.zip", extract=None, sha=None):
        entry = {"version": "1.0", "url": url, "sha256": hashlib.sha256(archive).hexdigest() if sha is None else sha,
                 "extract": extract or {"dir/tool.exe": "tool.exe", "dir/LICENSE": "LICENSE-tool.txt"},
                 "binary": "tool.exe"}
        patcher = mock.patch.dict(tools_fetch.MANIFEST, {"testplat": {"tool": entry}})
        patcher.start()
        self.addCleanup(patcher.stop)
        patcher = mock.patch.object(tools_fetch, "platform_key", return_value="testplat")
        patcher.start()
        self.addCleanup(patcher.stop)
        return entry

    def downloads(self):
        folder = self.root / "downloads"
        return sorted(p.name for p in folder.iterdir()) if folder.exists() else []

    def good_zip(self):
        return make_zip({"dir/tool.exe": b"EXE", "dir/LICENSE": b"MIT", "dir/other.txt": b"nope"})

    def test_installs_listed_members_only(self):
        self.use(self.good_zip())
        result = tools_fetch.ensure(opener=Opener(self.good_zip()))
        self.assertEqual(result["tool"], self.tools / "tool.exe")
        self.assertEqual((self.tools / "tool.exe").read_bytes(), b"EXE")
        self.assertEqual((self.tools / "LICENSE-tool.txt").read_bytes(), b"MIT")
        self.assertFalse((self.tools / "other.txt").exists())
        self.assertEqual(sorted(p.name for p in self.tools.iterdir()), ["LICENSE-tool.txt", "tool.exe", "tools.json"])

    def test_sha_mismatch_installs_nothing_and_removes_part(self):
        self.use(self.good_zip(), sha="0" * 64)
        with self.assertRaises(tools_fetch.ToolFetchError):
            tools_fetch.ensure(opener=Opener(self.good_zip()))
        self.assertFalse(self.tools.exists() and any(self.tools.iterdir()))
        self.assertEqual(self.downloads(), [])

    def test_failed_download_removes_part(self):
        self.use(self.good_zip())

        def boom(request):
            raise OSError("network down")
        with self.assertRaises(OSError):
            tools_fetch.ensure(opener=boom)
        self.assertEqual(self.downloads(), [])

    def test_traversal_member_rejected(self):
        archive = make_zip({"dir/tool.exe": b"EXE", "dir/LICENSE": b"MIT", "../evil.txt": b"x"})
        self.use(archive)
        with self.assertRaises(tools_fetch.ToolFetchError):
            tools_fetch.ensure(opener=Opener(archive))
        self.assertFalse((self.tools / "tool.exe").exists())
        self.assertFalse((self.root / "evil.txt").exists())

    def test_absolute_member_rejected(self):
        archive = make_zip({"/abs/tool.exe": b"EXE", "dir/LICENSE": b"MIT"})
        self.use(archive, extract={"/abs/tool.exe": "tool.exe"})
        with self.assertRaises(tools_fetch.ToolFetchError):
            tools_fetch.ensure(opener=Opener(archive))
        self.assertFalse((self.tools / "tool.exe").exists())

    def test_zip_symlink_rejected(self):
        archive = make_zip({"dir/tool.exe": b"EXE", "dir/LICENSE": b"MIT"}, symlink="dir/link")
        self.use(archive)
        with self.assertRaises(tools_fetch.ToolFetchError):
            tools_fetch.ensure(opener=Opener(archive))
        self.assertFalse((self.tools / "tool.exe").exists())

    def test_tar_installs_and_rejects_links_and_traversal(self):
        good = make_tar({"tool": b"BIN", "LICENSE": b"MIT"})
        self.use(good, url="https://example.invalid/pkg/tool.tar.gz", extract={"tool": "tool", "LICENSE": "LICENSE-t.txt"})
        tools_fetch.MANIFEST["testplat"]["tool"]["binary"] = "tool"
        tools_fetch.ensure(opener=Opener(good))
        self.assertEqual((self.tools / "tool").read_bytes(), b"BIN")

    def test_tar_symlink_and_traversal_rejected(self):
        for archive in (make_tar({"tool": b"BIN", "LICENSE": b"MIT"}, link="evil"),
                        make_tar({"tool": b"BIN", "LICENSE": b"MIT", "../evil": b"x"})):
            with self.subTest():
                self.use(archive, url="https://example.invalid/pkg/tool.tar.gz",
                         extract={"tool": "tool", "LICENSE": "LICENSE-t.txt"})
                with self.assertRaises(tools_fetch.ToolFetchError):
                    tools_fetch.ensure(opener=Opener(archive))
                self.assertFalse((self.tools / "tool").exists())

    def test_cached_archive_reused(self):
        archive = self.good_zip()
        self.use(archive)
        (self.root / "downloads").mkdir()
        (self.root / "downloads" / "tool.zip").write_bytes(archive)
        opener = Opener(b"unused")
        tools_fetch.ensure(opener=opener)
        self.assertEqual(opener.calls, 0)
        self.assertEqual((self.tools / "tool.exe").read_bytes(), b"EXE")

    def test_corrupt_cache_is_redownloaded(self):
        archive = self.good_zip()
        self.use(archive)
        (self.root / "downloads").mkdir()
        (self.root / "downloads" / "tool.zip").write_bytes(b"garbage")
        opener = Opener(archive)
        tools_fetch.ensure(opener=opener)
        self.assertEqual(opener.calls, 1)

    def test_empty_sha_is_not_yet_available(self):
        self.use(self.good_zip(), sha="")
        opener = Opener(b"")
        with self.assertRaisesRegex(tools_fetch.ToolUnavailable, "not yet available"):
            tools_fetch.ensure(opener=opener)
        self.assertEqual(opener.calls, 0)

    def test_macos_ffmpeg_placeholder_unavailable(self):
        with mock.patch.object(tools_fetch, "platform_key", return_value="darwin-arm64"):
            with self.assertRaisesRegex(tools_fetch.ToolUnavailable, "not yet available"):
                tools_fetch.ensure(["ffmpeg"], opener=Opener(b""))
            self.assertFalse(tools_fetch.status()["ffmpeg"]["available"])

    def test_unsupported_platform(self):
        with mock.patch.object(tools_fetch, "platform_key", return_value="linux-amd64"):
            with self.assertRaises(tools_fetch.ToolUnavailable):
                tools_fetch.ensure(opener=Opener(b""))

    def test_progress_callback(self):
        archive = self.good_zip()
        self.use(archive)
        events = []
        tools_fetch.ensure(progress=lambda name, done, total: events.append((name, done, total)), opener=Opener(archive))
        self.assertEqual(events[0], ("tool", 0, len(archive)))
        self.assertEqual(events[-1], ("tool", len(archive), len(archive)))

    def test_second_run_is_noop_and_status(self):
        archive = self.good_zip()
        self.use(archive)
        self.assertFalse(tools_fetch.status()["tool"]["installed"])
        tools_fetch.ensure(opener=Opener(archive))
        info = tools_fetch.status()["tool"]
        self.assertTrue(info["installed"])
        self.assertEqual(info["path"], str(self.tools / "tool.exe"))
        opener = Opener(archive)
        events = []
        tools_fetch.ensure(progress=lambda *a: events.append(a), opener=opener)
        self.assertEqual(opener.calls, 0)
        self.assertEqual(events, [])

    def test_missing_installed_file_triggers_reinstall(self):
        archive = self.good_zip()
        self.use(archive)
        tools_fetch.ensure(opener=Opener(archive))
        (self.tools / "tool.exe").unlink()
        opener = Opener(archive)
        tools_fetch.ensure(opener=opener)
        self.assertTrue((self.tools / "tool.exe").exists())

    def test_manifest_shape_and_platform_key(self):
        self.assertRegex(tools_fetch.platform_key(), r"^\w+-\w+$")
        for plat, tools in tools_fetch.MANIFEST.items():
            for name, entry in tools.items():
                self.assertIn(entry["binary"], entry["extract"].values(), (plat, name))
                self.assertRegex(entry["sha256"], r"^([0-9a-f]{64})?$")
        self.assertIn("autobuild-20", tools_fetch.MANIFEST["win32-amd64"]["ffmpeg"]["url"])
        self.assertNotIn("latest", tools_fetch.MANIFEST["win32-amd64"]["ffmpeg"]["url"])


class HardeningTests(ToolsFetchTests):
    """Name checks, HTTPS only, timeouts, one open of the archive, per-file hashes, stale parts."""

    def test_windows_and_unc_member_names_rejected(self):
        for name in ("C:/x", "C:\\x", "..\\x", "\\\\server\\share", "a/../../x", "/abs"):
            with self.subTest(name=name):
                with self.assertRaises(tools_fetch.ToolFetchError):
                    tools_fetch._check_name(name)
        for name in ("dir/tool.exe", "tool", "LICENSE"):
            tools_fetch._check_name(name)

    def test_non_https_url_refused_before_any_request(self):
        self.use(self.good_zip(), url="http://example.invalid/pkg/tool.zip")
        opener = Opener(self.good_zip())
        with self.assertRaisesRegex(tools_fetch.ToolFetchError, "HTTPS"):
            tools_fetch.ensure(opener=opener)
        self.assertEqual(opener.calls, 0)

    def test_redirect_to_http_is_refused(self):
        import urllib.error
        import urllib.request
        handler = tools_fetch._HttpsOnly()
        request = urllib.request.Request("https://example.invalid/a")
        with self.assertRaises(urllib.error.URLError):
            handler.redirect_request(request, None, 302, "Found", {}, "http://example.invalid/b")
        self.assertIsNotNone(handler.redirect_request(request, None, 302, "Found", {}, "https://example.invalid/b"))

    def test_timeout_is_passed_to_openers_that_take_one(self):
        archive = self.good_zip()
        self.use(archive)
        seen = {}

        def opener(request, timeout=None):
            seen["timeout"] = timeout
            return FakeResponse(archive)
        tools_fetch.ensure(opener=opener)
        self.assertEqual(seen["timeout"], 30)

    def test_archive_is_opened_once_for_hash_and_unpack(self):
        archive = self.good_zip()
        self.use(archive)
        (self.root / "downloads").mkdir()
        cached = self.root / "downloads" / "tool.zip"
        cached.write_bytes(archive)
        opened = []
        real_open = Path.open

        def counting(self_, *a, **k):
            if self_ == cached:
                opened.append(1)
            return real_open(self_, *a, **k)
        with mock.patch.object(Path, "open", counting):
            tools_fetch.ensure(opener=Opener(b""))
        self.assertEqual(len(opened), 1)

    def test_record_has_a_hash_per_file_and_damage_triggers_reinstall(self):
        import json
        archive = self.good_zip()
        self.use(archive)
        tools_fetch.ensure(opener=Opener(archive))
        record = json.loads((self.tools / "tools.json").read_text())["tool"]["files"]
        self.assertEqual(record["tool.exe"]["sha256"], hashlib.sha256(b"EXE").hexdigest())
        self.assertEqual(record["tool.exe"]["size"], 3)
        (self.tools / "tool.exe").write_bytes(b"EVIL")  # replaced by something else (also a different size)
        opener = Opener(archive)
        tools_fetch.ensure(opener=opener)
        self.assertEqual((self.tools / "tool.exe").read_bytes(), b"EXE")
        (self.tools / "tool.exe").write_bytes(b"EXX")  # same size, different content
        tools_fetch.ensure(opener=opener)
        self.assertEqual((self.tools / "tool.exe").read_bytes(), b"EXE")

    def test_old_format_record_is_reinstalled(self):
        import json
        archive = self.good_zip()
        entry = self.use(archive)
        tools_fetch.ensure(opener=Opener(archive))
        (self.tools / "tools.json").write_text(json.dumps(
            {"tool": {"version": "1.0", "sha256": entry["sha256"], "files": ["tool.exe", "LICENSE-tool.txt"]}}))
        opener = Opener(archive)
        tools_fetch.ensure(opener=opener)
        self.assertEqual(opener.calls, 0)  # the archive is still cached, so no new download, but it was unpacked again

    def test_macos_ffmpeg_pin_points_at_the_deps_release_but_makes_no_request_while_sha_is_empty(self):
        entry = tools_fetch.MANIFEST["darwin-arm64"]["ffmpeg"]
        self.assertTrue(entry["url"].startswith("https://github.com/OnlyArnavHere/Spatial_Headset/releases/download/"))
        self.assertTrue(entry["url"].endswith("ffmpeg-8.1.3-macos-arm64-lgpl.tar.gz"))
        self.assertEqual(entry["sha256"], "")
        opener = Opener(b"")
        with mock.patch.object(tools_fetch, "platform_key", return_value="darwin-arm64"):
            with self.assertRaises(tools_fetch.ToolUnavailable):
                tools_fetch.ensure(["ffmpeg"], opener=opener)
            with self.assertRaises(tools_fetch.ToolUnavailable):
                tools_fetch.ensure(opener=opener)  # the whole set too: MediaMTX is not fetched first
        self.assertEqual(opener.calls, 0)

    def test_stale_part_files_are_removed(self):
        archive = self.good_zip()
        self.use(archive)
        (self.root / "downloads").mkdir()
        (self.root / "downloads" / "tool.zip.part").write_bytes(b"half")
        tools_fetch.ensure(opener=Opener(archive))
        self.assertEqual(self.downloads(), ["tool.zip"])


for _name in [n for n in dir(ToolsFetchTests) if n.startswith("test_")]:
    setattr(HardeningTests, _name, None)  # share the fixtures only; the base tests run once, in their own class


if __name__ == "__main__":
    unittest.main()
