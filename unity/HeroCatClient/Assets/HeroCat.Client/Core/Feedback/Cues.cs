using System.Collections.Generic;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// One-shot presentation cues the view plays: VFX, pulses, shakes, haptics and sounds (07 sections 4.3, 5, 6 and 9).
    /// The vm pushes them from events and interactions; the Unity layer drains them once per frame. They never change a
    /// game number.
    /// </summary>
    public enum CueKind
    {
        None,
        /// <summary>Button shake under a reject toast; Id = the button's anchor id.</summary>
        Shake,
        GoldPulse, DiamondPulse, FishCoinPulse,
        EnemySpawned, EnemyKilled, GoldBurst, StagePulse, BossFlash, BossFailed, LevelUpFloat, AwakenCard, StageCapReached,
        SkillCast,
        /// <summary>Cell = the cell a released fish landed in (pop-in arc from the tank).</summary>
        FishPop,
        /// <summary>A = source, Cell = target, Level = new level; plays the slide, pop and missile launch.</summary>
        FishMerge,
        FishMove,
        FishDiscard,
        /// <summary>Cell shakes 3 x 6 rp over 180 ms with a bad flash.</summary>
        FishInvalid,
        /// <summary>A dragged fish snaps back to Cell in 120 ms.</summary>
        FishSnapBack,
        /// <summary>The discovery card, 1.5 s; Level and Value (coins).</summary>
        FishDiscovered,
        /// <summary>"Max level: raise the cap in Research" over Cell.</summary>
        FishAtCap,
        /// <summary>Merge-all button pulses once (Release on a full board).</summary>
        MergeAllPulse,
        CardFlip, RarityFlash,
        Haptic, Sound,
    }

    /// <summary>07 section 9's haptic patterns (iOS name; Android falls back to a vibration of similar weight).</summary>
    public enum Haptic { None, Selection, Light, Medium, Heavy, Success, Failure }

    public readonly struct UiCue
    {
        public UiCue(CueKind kind, int cell = -1, int a = 0, int level = 0, double value = 0, string id = null, Haptic haptic = Haptic.None)
        {
            Kind = kind;
            Cell = cell;
            A = a;
            Level = level;
            Value = value;
            Id = id;
            Haptic = haptic;
        }

        public CueKind Kind { get; }
        public int Cell { get; }
        public int A { get; }
        public int Level { get; }
        public double Value { get; }

        /// <summary>An anchor id (Shake), a sound event (Sound) or an item id.</summary>
        public string Id { get; }

        public Haptic Haptic { get; }

        public override string ToString() => Kind + (Cell >= 0 ? " cell " + Cell : "") + (Id != null ? " " + Id : "");
    }

    /// <summary>A bounded list of cues; the view drains it each frame. Allocation-free once warmed up.</summary>
    public sealed class CueQueue
    {
        public const int Capacity = 128;
        readonly List<UiCue> _cues = new List<UiCue>(Capacity);

        public int Count => _cues.Count;

        public UiCue this[int i] => _cues[i];

        public void Push(UiCue c)
        {
            if (_cues.Count >= Capacity) _cues.RemoveAt(0);
            _cues.Add(c);
        }

        public void Push(CueKind kind, int cell = -1, int a = 0, int level = 0, double value = 0, string id = null, Haptic haptic = Haptic.None) =>
            Push(new UiCue(kind, cell, a, level, value, id, haptic));

        /// <summary>A haptic and a sound together (07 section 9 rows).</summary>
        public void Feel(Haptic h, string sound)
        {
            if (h != Haptic.None) Push(CueKind.Haptic, haptic: h);
            if (sound != null) Push(CueKind.Sound, id: sound);
        }

        public bool Contains(CueKind kind)
        {
            for (int i = 0; i < _cues.Count; i++) if (_cues[i].Kind == kind) return true;
            return false;
        }

        public int CountOf(CueKind kind)
        {
            int n = 0;
            for (int i = 0; i < _cues.Count; i++) if (_cues[i].Kind == kind) n++;
            return n;
        }

        public void Clear() => _cues.Clear();
    }

    /// <summary>07 section 9's sound event names.</summary>
    public static class Sounds
    {
        public const string UiTap = "ui_tap", UiDeny = "ui_deny", FishPick = "fish_pick", FishDrop = "fish_drop", FishDiscover = "fish_discover",
                            HitKill = "hit_kill", HitCrit = "hit_crit", BossEnter = "boss_enter", BossClear = "boss_clear", BossFail = "boss_fail",
                            StageUp = "stage_up", LevelUp = "level_up", Awaken = "awaken", CardFlip = "card_flip", CardEpic = "card_epic",
                            CardLegend = "card_legend", CardMythic = "card_mythic", Coins = "coins", Upgrade = "upgrade";

        static readonly string[] Merge = BuildMerge();

        static string[] BuildMerge()
        {
            var r = new string[UiConstants.FishSpriteLevels + 1];
            for (int i = 1; i < r.Length; i++) r[i] = "fish_merge_" + i.ToString("00");
            return r;
        }

        /// <summary><c>fish_merge_01..12</c> by resulting level, clamped at 12 (issue #48).</summary>
        public static string FishMerge(int level) => Merge[level < 1 ? 1 : level > UiConstants.FishSpriteLevels ? UiConstants.FishSpriteLevels : level];
    }
}
