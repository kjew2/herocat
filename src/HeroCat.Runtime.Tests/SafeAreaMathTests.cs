using HeroCat.Client.Core;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>Unity client's engine-free safe-area math (compiled in via a linked source file).</summary>
    public class SafeAreaMathTests
    {
        [Test]
        public void FullScreenSafeArea_NoInsets()
        {
            var i = SafeAreaMath.ToPanelInsets(1080, 1920, 0, 0, 1080, 1920, 1080, 1920);
            Assert.AreEqual(new PanelInsets(0, 0, 0, 0), i);
        }

        [Test]
        public void IPhone15Portrait_NotchOnTop_HomeBarAtBottom()
        {
            // 1179x2556 px; 59pt status/Dynamic Island band and 34pt home indicator at 3x. Panel is 1080 wide (Expand).
            float panelH = 2556f * 1080f / 1179f;
            var i = SafeAreaMath.ToPanelInsets(1179, 2556, 0, 102, 1179, 2277, 1080, panelH);
            Assert.AreEqual(0, i.Left, 1e-3);
            Assert.AreEqual(0, i.Right, 1e-3);
            Assert.AreEqual(177f * 1080f / 1179f, i.Top, 1e-3);    // ~162 reference px
            Assert.AreEqual(102f * 1080f / 1179f, i.Bottom, 1e-3); // ~93 reference px
        }

        [Test]
        public void LandscapeNotchLeft_ScalesEachAxis()
        {
            // 2000x1000 screen shown as a 1000x500 panel: 80 px notch on the left, 40 px home bar at the bottom.
            var i = SafeAreaMath.ToPanelInsets(2000, 1000, 80, 40, 1840, 960, 1000, 500);
            Assert.AreEqual(new PanelInsets(40, 0, 40, 20), i);
        }

        [Test]
        public void ZeroSizesOrOversizedSafeArea_AreSafe()
        {
            Assert.AreEqual(default(PanelInsets), SafeAreaMath.ToPanelInsets(0, 0, 0, 0, 0, 0, 1080, 1920));
            Assert.AreEqual(default(PanelInsets), SafeAreaMath.ToPanelInsets(1080, 1920, 0, 0, 1080, 1920, float.NaN, 1920));
            var i = SafeAreaMath.ToPanelInsets(100, 100, -5, -5, 120, 120, 100, 100);
            Assert.AreEqual(new PanelInsets(0, 0, 0, 0), i);
        }
    }
}
