#!/usr/bin/env python3
"""Unit tests for tools/content.py (issue #130, specs/09 section 11). Run: python3 tools/test_content.py (check.sh does)."""
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import content  # noqa: E402


class ContentLoaderTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.dir = os.path.join(self.tmp, "content")
        shutil.copytree(content.DEFAULT_DIR, self.dir)

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def test_loads_committed_content(self):
        db = content.load()
        self.assertEqual(db.curves["enemy_hp"][1], db.table("curves")[0]["growth"])
        self.assertEqual(db.constants["TICK_HZ"], 10)
        self.assertEqual(len(db.table("stats")), 8)
        self.assertIsNone(db.table("no_such_table", None))
        with self.assertRaises(KeyError):
            db.table("no_such_table")
        self.assertIn("pool_size", content.Content.columns("banners"))
        self.assertNotIn("pool_c", content.Content.columns("banners"))

    def test_tampered_byte_fails_e900(self):
        path = os.path.join(self.dir, "skills.json")
        with open(path, "rb") as f:
            data = bytearray(f.read())
        i = data.index(b"10.0")
        data[i] = ord("2")
        with open(path, "wb") as f:
            f.write(bytes(data))
        with self.assertRaises(content.ContentError) as cm:
            content.load(self.dir)
        msg = cm.exception.errors[0]
        self.assertTrue(msg.startswith("CONTENT E900 skills.json: skills.json: sha256 "), msg)
        self.assertIn("does not match manifest", msg)

    def test_unlisted_and_missing_files_fail_e902(self):
        with open(os.path.join(self.dir, "pets.json"), "w") as f:
            f.write("{}")
        os.remove(os.path.join(self.dir, "tank.json"))
        with self.assertRaises(content.ContentError) as cm:
            content.load(self.dir)
        self.assertEqual(cm.exception.errors, [
            "CONTENT E902 tank.json: tank.json listed in manifest but missing",
            "CONTENT E902 pets.json: pets.json present but not listed",
        ])


if __name__ == "__main__":
    unittest.main()
