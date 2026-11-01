namespace HeroCat.Client.Core
{
    /// <summary>
    /// 07's "Constants introduced" table: presentation constants (layout, timings, pools). None is a balance number, so
    /// none is a content row (09 holds the sim's constants); behaviour values owned by 05 (<see cref="LongPressMs"/>,
    /// <see cref="DragStartPx"/>) are mirrored here as 07's list says (issue #18).
    /// </summary>
    public static class UiConstants
    {
        public const float RefWidth = 1080, RefHeight = 1920;

        public const float BandHud = 120, BandCombat = 800, BandAction = 100, BandBoard = 640, BandTabs = 160;

        /// <summary>The Full-screen title row (07 section 2).</summary>
        public const float TitleRow = 96;

        public const float MinTouch = 112, MinTouchGap = 16, MinListRow = 128;

        public const float BoardCell = 176, BoardGap = 8, FishSprite = 140, TankColumn = 160;

        public const float ModalWidth = 960, ModalMaxHeight = 1400, ModalRadius = 32;

        public const double ToastSec = 2.0, ToastRareSec = 4.0;

        public const double RevealFlipSec = 0.3, RevealStaggerSec = 0.12, RevealLplusHoldSec = 0.4;

        /// <summary>Single pull: pause before the flip, and the whole single reveal (07 section 6 rule 1).</summary>
        public const double RevealSinglePauseSec = 0.2, RevealSingleTotalSec = 0.6;

        /// <summary>First card of a 10-pull starts at 0.3 s (07 section 6 rule 2).</summary>
        public const double RevealFirstCardSec = 0.3;

        public const int LongPressMs = 350;
        public const float DragStartPx = 8;

        public const int ReleaseMaxAnimMs = 60, MergeAllAnimMs = 80;

        public const int DamageLabelPool = 32, MaxParticles = 300;

        /// <summary>How long a damage number floats before its label returns to the pool.</summary>
        public const double DamageNumberLifeSec = 0.8;

        public const int ScreenCache = 3;

        public const int FishSpriteLevels = 12;

        public const double BackExitWindowSec = 2;

        public const int QuestExpiryWarnSec = 3600;

        /// <summary>DPS readout refresh cap (07 section 4.3: at most 2 Hz).</summary>
        public const double DpsRefreshSec = 0.5;

        /// <summary>Boss timer bar turns <c>bad</c> under this many seconds (07 section 4.3).</summary>
        public const double BossTimerWarnSec = 5;

        /// <summary>Fish discovery card on SCR_MAIN (07 section 5).</summary>
        public const double DiscoveryCardSec = 1.5;

        /// <summary>
        /// The board cell that fits: 07 section 2 asks for 176 rp cells, but four rows of 176 rp plus gaps (728 rp) do not fit
        /// the 640 rp board band, so the cell shrinks to fit (never below <see cref="MinTouch"/>), as section 2 already allows
        /// on short screens. With the seed bands: (640 - 2 x 8 - 3 x 8) / 4 = 150 rp.
        /// </summary>
        public static float BoardCellFor(float bandHeight, int rows)
        {
            if (rows < 1) rows = 1;
            float fit = (bandHeight - 2 * BoardGap - (rows - 1) * BoardGap) / rows;
            if (fit > BoardCell) fit = BoardCell;
            return fit < MinTouch ? MinTouch : fit;
        }

        /// <summary>A Loading state shows a Retry link after this long (07 section 10 rule 5).</summary>
        public const double LoadingRetrySec = 2, LoadingErrorSec = 10;
    }
}
