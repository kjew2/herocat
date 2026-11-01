using HeroCat.Client.Core;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>07 section 2's band budget (issue #111).</summary>
    public class LayoutTests
    {
        [Test]
        public void Bands_Sum1820_AndFitA16By9ScreenWith100rpInsets_WithoutShrinkingHudOrTabs()
        {
            float bands = UiConstants.BandHud + UiConstants.BandCombat + UiConstants.BandAction + UiConstants.BandBoard + UiConstants.BandTabs;
            Assert.That(bands, Is.EqualTo(1820));
            Assert.That(bands + 100, Is.LessThanOrEqualTo(UiConstants.RefHeight));
            // The board grid: 5 x 176 + 4 x 8 = 912 rp next to the 160 rp tank column, inside 1080.
            float grid = 5 * UiConstants.BoardCell + 4 * UiConstants.BoardGap;
            Assert.That(grid, Is.EqualTo(912));
            Assert.That(UiConstants.TankColumn + grid, Is.LessThanOrEqualTo(UiConstants.RefWidth));
            // Four 176 rp rows do not fit the 640 rp band (spec gap): the cell shrinks to fit, never below UI_MIN_TOUCH.
            float cell = UiConstants.BoardCellFor(UiConstants.BandBoard, 4);
            Assert.That(cell, Is.EqualTo(150));
            Assert.That(4 * cell + 5 * UiConstants.BoardGap, Is.LessThanOrEqualTo(UiConstants.BandBoard));
            Assert.That(cell, Is.GreaterThanOrEqualTo(UiConstants.MinTouch));
            Assert.That(UiConstants.BoardCellFor(2000, 4), Is.EqualTo(UiConstants.BoardCell), "never larger than 176 rp");
        }
    }
}
