using System;

namespace HeroCat.Client.Core
{
    /// <summary>Insets from each panel edge, in panel (reference) units.</summary>
    public readonly struct PanelInsets : IEquatable<PanelInsets>
    {
        public readonly float Left, Top, Right, Bottom;

        public PanelInsets(float left, float top, float right, float bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
        }

        public bool Equals(PanelInsets o) => Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom;
        public override bool Equals(object obj) => obj is PanelInsets o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
        public override string ToString() => $"(L {Left}, T {Top}, R {Right}, B {Bottom})";
    }

    /// <summary>
    /// Screen.safeArea to panel-space insets (specs/07 section 5), kept engine-free so it is unit-testable.
    /// The safe rect is in screen pixels with a bottom-left origin (Unity's convention); the panel covers the whole
    /// screen, so each axis scales by panelSize / screenSize.
    /// </summary>
    public static class SafeAreaMath
    {
        public static PanelInsets ToPanelInsets(
            float screenW, float screenH,
            float safeX, float safeY, float safeW, float safeH,
            float panelW, float panelH)
        {
            if (!(screenW > 0) || !(screenH > 0) || !(panelW > 0) || !(panelH > 0)) return default;
            float sx = panelW / screenW, sy = panelH / screenH;
            float left = safeX;
            float right = screenW - (safeX + safeW);
            float bottom = safeY;
            float top = screenH - (safeY + safeH);
            return new PanelInsets(Clamp(left) * sx, Clamp(top) * sy, Clamp(right) * sx, Clamp(bottom) * sy);
        }

        static float Clamp(float px) => px > 0 ? px : 0;
    }
}
