#!/usr/bin/env python3
"""Unit tests for tools/fish_model.py's Fish Coin Dungeon curve report (issue #165). Run: python3 tools/test_fish_model.py
(check.sh does)."""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fish_model as fm  # noqa: E402


class DungeonCurveTests(unittest.TestCase):
    def test_parse_curve(self):
        self.assertEqual(fm.parse_curve("50*f"), ("50*f", 50.0, 1.0))
        self.assertEqual(fm.parse_curve("50 * f ** 0.7")[1:], (50.0, 0.7))
        self.assertEqual(fm.parse_curve("120*f^1.2")[1:], (120.0, 1.2))
        for bad in ("50", "f*50", "50*g", "50*f+1"):
            with self.assertRaises(SystemExit):
                fm.parse_curve(bad)

    def test_share_and_band_coefficients(self):
        rows = [{"coins_board": 3000.0}] * 7
        floors = {1: 0, 7: 10}
        curves = [fm.parse_curve("50*f"), fm.parse_curve("50*f^0.7")]
        rep = {r["day"]: r for r in fm.dungeon_report(rows, floors, curves, days=(1, 7))}
        self.assertEqual(rep[1]["curves"]["50*f"]["coins_dungeon"], 0.0)   # dungeon not open: floor 0
        self.assertIsNone(rep[1]["linear_k_for_band"]["low"])
        lin = rep[7]["curves"]["50*f"]
        self.assertAlmostEqual(lin["coins_dungeon"], 2 * 50 * 10)
        self.assertAlmostEqual(lin["share"], 1000 / 4000)
        self.assertAlmostEqual(rep[7]["curves"]["50*f^0.7"]["coins_dungeon"], 2 * 50 * 10 ** 0.7)
        k = rep[7]["linear_k_for_band"]
        self.assertAlmostEqual(k["low"], 37.5)                     # 2 * 37.5 * 10 = 750 = 20% of 3750
        self.assertAlmostEqual(k["high"], 3000 / (2 * 10))         # dungeon == board at 50%


if __name__ == "__main__":
    unittest.main()
