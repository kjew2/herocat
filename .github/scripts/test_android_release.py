#!/usr/bin/env python3
"""Unit tests for android_release.py. Run: python3 .github/scripts/test_android_release.py"""
import io
import json
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import android_release as ar  # noqa: E402

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


class Tmp(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)

    def file(self, name, size=0, text=None):
        path = os.path.join(self.dir.name, name)
        with open(path, "wb") as f:
            f.write(text.encode() if text is not None else b"\0" * size)
        return path

    def run_main(self, argv):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = ar.main(argv)
        return code, out.getvalue(), err.getvalue()


class VersionTests(Tmp):
    def test_committed_version_json_is_valid(self):
        ar.read_version(os.path.join(REPO, "unity", "HeroCatClient", "version.json"))

    def test_outputs_and_tag(self):
        path = self.file("v.json", text=json.dumps({"version": "1.2.0"}))
        code, out, _ = self.run_main(["version", "--file", path, "--build", "345"])
        self.assertEqual(code, 0)
        self.assertEqual(out.splitlines(), ["version=1.2.0", "build=345", "full=1.2.0+345", "tag=client-v1.2.0+345"])

    def test_rejects_non_semver(self):
        for bad in ["1.2", "1.2.0-rc1", "01.2.0", "v1.2.0", "", None, 120]:
            path = self.file("v.json", text=json.dumps({"version": bad}))
            with self.subTest(bad=bad), self.assertRaises(ar.ReleaseError):
                ar.read_version(path)

    def test_rejects_bad_json_and_missing_file(self):
        with self.assertRaises(ar.ReleaseError):
            ar.read_version(self.file("v.json", text="{nope"))
        with self.assertRaises(ar.ReleaseError):
            ar.read_version(os.path.join(self.dir.name, "missing.json"))
        with self.assertRaises(ar.ReleaseError):
            ar.read_version(self.file("v.json", text="[1]"))

    def test_rejects_bad_build_number(self):
        for bad in ["0", "-1", "abc", "", "1.5", "012"]:
            with self.subTest(bad=bad), self.assertRaises(ar.ReleaseError):
                ar.version_outputs("1.0.0", bad)


class SizeTests(Tmp):
    def test_at_budget_passes_one_byte_over_fails(self):
        at = self.file("at.aab", size=2 * ar.BYTES_PER_MB)
        over = self.file("over.aab", size=2 * ar.BYTES_PER_MB + 1)
        self.assertTrue(ar.check_sizes([at], 2)[0])
        self.assertFalse(ar.check_sizes([over], 2)[0])
        self.assertFalse(ar.check_sizes([at, over], 2)[0])

    def test_fractional_budget(self):
        f = self.file("a.aab", size=600_000)
        self.assertTrue(ar.check_sizes([f], 0.6)[0])
        self.assertFalse(ar.check_sizes([f], 0.5)[0])

    def test_missing_or_no_artifacts_fail(self):
        self.assertFalse(ar.check_sizes([], 150)[0])
        self.assertFalse(ar.check_sizes([os.path.join(self.dir.name, "nope.aab")], 150)[0])

    def test_nonpositive_budget_is_an_error(self):
        with self.assertRaises(ar.ReleaseError):
            ar.check_sizes([self.file("a.aab", size=1)], 0)

    def test_cli_exit_codes(self):
        f = self.file("a.aab", size=10)
        self.assertEqual(self.run_main(["size", "--max-mb", "150", f])[0], 0)
        code, out, err = self.run_main(["size", "--max-mb", "0.000001", f])
        self.assertEqual(code, 1)
        self.assertIn("FAIL", out)
        self.assertIn("size gate failed", err)
        self.assertEqual(self.run_main(["size", "--max-mb", "-1", f])[0], 2)

    def test_default_budget_is_spec_constant(self):
        self.assertEqual(ar.SIZE_AAB_MB_MAX, 150)


if __name__ == "__main__":
    unittest.main(verbosity=1)
