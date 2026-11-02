using System;
using System.Collections.Generic;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;

// Save schema v1 (specs/02 section 3.1, canonical for names and shapes; issue #7). Pure data: no serializer
// attributes, no IO, no clock. Each system issue (03-06) fills in the behaviour that reads and writes its section.
// C# 9 / netstandard2.1: `init` needs the IsExternalInit polyfill (Properties/AssemblyInfo.cs); nullable reference
// annotations (`string?`) are dropped, so a null string slot is just a null `string`.
// Changing a serialized property, an enum member or a dictionary key format is a schema change (02 section 7.2 rule 7).

namespace HeroCat.Sim.State
{
    public sealed class GameState
    {
        /// <summary>SAVE_SCHEMA_VERSION. Runtime's SaveSchema.Current is this constant.</summary>
        public const int CurrentSchemaVersion = 1;

        public int    SchemaVersion   { get; set; } = CurrentSchemaVersion;   // the only version field in the body
        public string ContentVersion  { get; set; } = "";                     // ContentDb.Version active at last write (09)
        public string ContentHash     { get; set; } = "";                     // ContentDb.ContentHash the save was last reconciled against (issue #56)
        public Guid   PlayerId        { get; init; }                          // server playerId; Guid.Empty for an unlinked local guest
        public Guid   DeviceId        { get; set; }                           // install id of the writing device; cloud-save lineage only, never a seed input
        public Guid   RngRoot         { get; init; }                          // set once by ProfileFactory; the only seed input (3.4, issue #9)
        public long   SaveSeq         { get; set; }                           // +1 per write; same-device tiebreak

        public TimeState        Time        { get; init; } = new TimeState();
        public HeroState        Hero        { get; init; } = new HeroState();
        public TrainingState    Training    { get; init; } = new TrainingState();
        public Wallet           Wallet      { get; init; } = new Wallet();
        public CollectionState  Weapons     { get; init; } = new CollectionState();
        public CollectionState  Skills      { get; init; } = new CollectionState();
        public CollectionState  Companions  { get; init; } = new CollectionState();
        public CollectionState  Runes       { get; init; } = new CollectionState();   // P1; empty in MVP, present so no schema bump
        public LoadoutState     Loadout     { get; init; } = new LoadoutState();
        public FishBoardState   Fish        { get; init; } = new FishBoardState();
        public Dictionary<Banner, BannerState> Banners { get; init; } = new Dictionary<Banner, BannerState>();   // one entry per banner ever seen
        public CombatState      Combat      { get; init; } = new CombatState();
        public ProgressState    Progress    { get; init; } = new ProgressState();
        public QuestState       Quests      { get; init; } = new QuestState();
        public DungeonState     Dungeons    { get; init; } = new DungeonState();
        public ShopState        Shop        { get; init; } = new ShopState();
        public ResearchState    Research    { get; init; } = new ResearchState();
        public InboxState       Inbox       { get; init; } = new InboxState();
        public OfflineState     Offline     { get; init; } = new OfflineState();
        public PendingState     Pending     { get; init; } = new PendingState();
        public TutorialState    Tutorial    { get; init; } = new TutorialState();
        public Dictionary<string, RngStreamState> Rng { get; init; } = new Dictionary<string, RngStreamState>(StringComparer.Ordinal);   // key: stream name (3.4)
        public LifetimeStats    Stats       { get; init; } = new LifetimeStats();
        public bool             DebugTainted { get; set; }                    // any debug command sets it; excluded from tuning data
    }

    public sealed class TimeState
    {
        public long Tick                 { get; set; }        // sim ticks since profile creation; monotone
        public long LastSavedServerTime  { get; set; }        // unix s; stamped by SaveStamp on every write
        public long LastTickServerTime   { get; set; }        // unix s; catch-up anchor (01 section 8.7)
        public long AnchorServerTime     { get; set; }        // unix s; set by each ApplyServerTime. Virtual now = AnchorServerTime + (Tick - AnchorTick) / TICK_HZ
        public long AnchorTick           { get; set; }        // Tick at that ApplyServerTime
        public long CreatedServerTime    { get; init; }       // unix s
        public int  DayKey               { get; set; } = -1;  // floor(serverSeconds / DAY_SECONDS) at last rollover; -1 on a new profile (06 section 4.2 rule 4)
        public int  WeekKey              { get; set; } = -1;  // floor((DayKey - 4) / 7) at last weekly rollover; -1 on a new profile
        public int  LoginDayKey          { get; set; } = -1;  // DayKey on which LOGIN_DIAMONDS was last granted (issue #20)
    }

    public sealed class HeroState
    {
        public int    Level        { get; set; } = 1;         // >= 1; 03 HERO_LVL_EXP curve
        public double Exp          { get; set; }              // HERO_EXP toward next level; reset on level up
        public int    AwakenRank   { get; set; }              // 0..AWAKEN_STAGES.Length; 03
    }

    public sealed class TrainingState
    {
        // Index by Stat ordinal: ATK, HP, DEF, CRIT_RATE, CRIT_DMG, ATK_SPD, GOLD_GAIN, EXP_GAIN. Length always 8.
        public int[] Level { get; set; } = new int[8];        // each 0..TRAIN_CAP[stat] (03)   // literal-ok: 02 default size
    }

    /// <summary>
    /// All balances (02 section 3.1, 06 section 1). Every change goes through <see cref="Ledger.Apply"/>, the single
    /// path: it rejects a debit below 0 and NaN/inf, and emits CurrencyChanged (00 contract 6). The setters exist for
    /// the serializer; sim code never assigns a balance directly (a test greps for it).
    /// </summary>
    public sealed class Wallet
    {
        public double Gold          { get; set; }             // GOLD; double because it passes 2^53 by stage ~700
        public long   Diamond       { get; set; }             // DIAMOND; server-reconciled in MVP
        public double FishCoin      { get; set; }             // FISH_COIN
        public long   RockCandy     { get; set; }             // ROCK_CANDY
        public double HeroExp       => 0;                     // HERO_EXP is not a balance; it is Hero.Exp. Present in CurrencyDelta only.
        public int[]  DungeonTicket { get; set; } = new int[3];      // by Dungeon ordinal: FISH_COIN, ROCK_CANDY, EXP   // literal-ok: 02 default size
        public int[]  SummonTicket  { get; set; } = new int[4];      // by Banner ordinal: WEAPON, SKILL, COMPANION, RUNE (issue #26); Remote mode mirrors the server ledger   // literal-ok: 02 default size
        public long   GuildCoin     { get; set; }             // P1

        public bool ShouldSerializeHeroExp() => false;        // Json.NET convention, no attribute: HeroExp is never written

        /// <summary>The balance of <paramref name="c"/>. HERO_EXP is not a balance and reads 0 (see Hero.Exp).</summary>
        public double Get(Currency c)
        {
            switch (c)
            {
                case Currency.GOLD: return Gold;
                case Currency.DIAMOND: return Diamond;
                case Currency.FISH_COIN: return FishCoin;
                case Currency.ROCK_CANDY: return RockCandy;
                case Currency.HERO_EXP: return 0;
                case Currency.GUILD_COIN: return GuildCoin;
                default:
                    int d = DungeonTicketIndex(c);
                    if (d >= 0) return DungeonTicket[d];
                    int s = SummonTicketIndex(c);
                    if (s >= 0) return SummonTicket[s];
                    return 0;
            }
        }

        public bool CanAfford(Currency c, double amount) => Get(c) >= amount;

        /// <summary>Only <see cref="Ledger.Apply"/> calls this.</summary>
        internal void Set(Currency c, double v)
        {
            switch (c)
            {
                case Currency.GOLD: Gold = v; return;
                case Currency.DIAMOND: Diamond = (long)v; return;
                case Currency.FISH_COIN: FishCoin = v; return;
                case Currency.ROCK_CANDY: RockCandy = (long)v; return;
                case Currency.GUILD_COIN: GuildCoin = (long)v; return;
                default:
                    int d = DungeonTicketIndex(c);
                    if (d >= 0) { DungeonTicket[d] = (int)v; return; }
                    int s = SummonTicketIndex(c);
                    if (s >= 0) { SummonTicket[s] = (int)v; return; }
                    throw new ArgumentOutOfRangeException(nameof(c));
            }
        }

        /// <summary>Counted (long/int) currencies take whole amounts only (00 contract 9).</summary>
        public static bool IsCounted(Currency c) =>
            !(c == Currency.GOLD || c == Currency.FISH_COIN || c == Currency.HERO_EXP);

        /// <summary>Dungeon ordinal for a dungeon ticket currency, else -1.</summary>
        public static int DungeonTicketIndex(Currency c)
        {
            switch (c)
            {
                case Currency.TICKET_FISH_COIN: return (int)Content.Dungeon.FISH_COIN;
                case Currency.TICKET_ROCK_CANDY: return (int)Content.Dungeon.ROCK_CANDY;
                case Currency.TICKET_EXP: return (int)Content.Dungeon.EXP;
                default: return -1;
            }
        }

        /// <summary>Banner ordinal for a summon ticket currency, else -1.</summary>
        public static int SummonTicketIndex(Currency c)
        {
            switch (c)
            {
                case Currency.TICKET_SUMMON_WEAPON: return (int)Banner.WEAPON;
                case Currency.TICKET_SUMMON_SKILL: return (int)Banner.SKILL;
                case Currency.TICKET_SUMMON_COMPANION: return (int)Banner.COMPANION;
                case Currency.TICKET_SUMMON_RUNE: return (int)Banner.RUNE;
                default: return -1;
            }
        }
    }

    public sealed class CollectionState
    {
        // key: content id (e.g. "weapon_c_01"). Absent key == not owned. NEW badges live in Runtime's ClientPrefs (issue #47).
        public Dictionary<string, OwnedItem> Items { get; init; } = new Dictionary<string, OwnedItem>(StringComparer.Ordinal);
    }

    public sealed class OwnedItem
    {
        public int  Copies      { get; set; }   // 1..DUPE_LVL_CUM[10] (160); level derived from Copies at read time (04)
        public int  LimitBreaks { get; set; }   // 0..LIMIT_BREAK_RANKS; 04 section 5.2
        public long FirstAcquiredServerTime { get; init; }
    }

    public sealed class LoadoutState
    {
        public string   Weapon     { get; set; }                         // equipped weapon id; TUTORIAL_WEAPON_ID from profile creation (issue #46)
        public string[] Skills     { get; set; } = new string[6];        // SKILL_SLOTS; null = empty slot; order = auto-cast priority (04)   // literal-ok: 02 default size
        public string[] Companions { get; set; } = new string[4];        // COMPANION_SLOTS   // literal-ok: 02 default size
        public string[] Runes      { get; set; } = new string[6];        // RUNE_SLOTS, P1   // literal-ok: 02 default size
    }

    public sealed class FishBoardState
    {
        public int[] Cells            { get; set; } = new int[20];   // FISH_BOARD_W * FISH_BOARD_H, row-major; 0 = empty, n >= 1 = fish level   // literal-ok: 02 default size
        public int   TankCount        { get; set; }                  // fish in the Magic Tank, 0..TankCap
        public int   TankRefillTick   { get; set; }                  // ticks toward the next tank fish; 0..refillTicks-1
        public int   TankReleaseLevel { get; set; } = 1;             // P1 upgrade level bought (05, 09 tank.release_level is the base)
        public HashSet<int> DiscoveredLevels { get; init; } = new HashSet<int>();   // levels ever created; drives FISH_DISCOVERY_COINS
        public int   HighestLevel     { get; set; }                  // max of DiscoveredLevels, cached for the HUD
        public long  TotalMerges      { get; set; }
        public bool  AutoMergeEnabled { get; set; }                  // P1
    }

    public sealed class BannerState
    {
        public int  Pity       { get; set; }      // pulls since last L+ before the next pull (n = Pity + 1, issue #24), 0..PITY_HARD-1
        public long Exp        { get; set; }      // cumulative banner EXP; banner level derived via BANNER_LVL_EXP, never stored (04)
        public long TotalPulls { get; set; }
    }

    public enum CombatMode { Stage, Dungeon }
    public enum BossPhase  { None, Pending, Walled }           // Pending: next spawn is the boss; Walled: boss failed, farming mobs (03 section 2.7)
    public enum FightPhase { Spawning, Fighting }

    public sealed class CombatState
    {
        public CombatMode Mode          { get; set; } = CombatMode.Stage;
        public int    Stage             { get; set; } = 1;     // frontier stage, >= 1, <= STAGE_CAP
        public int    KillsInStage      { get; set; }          // 0..KILLS_PER_STAGE-1
        public BossPhase Boss           { get; set; }          // None/Pending/Walled, so a wall survives a reload (issue #7)
        public int    BossFailsOnStage  { get; set; }          // reset on advance (03)
        public int?   Selected          { get; set; }          // MVP side-stage farm target <= HighestCleared; null = frontier (issue #35)
        public double Mp                { get; set; }          // shared MP pool (04)
        public int[]  SkillCooldowns    { get; set; } = new int[6];   // ticks left per skill slot   // literal-ok: 02 default size
        public int[]  CompanionCooldowns { get; set; } = new int[4];  // ticks left per companion slot (03 section 2.4)   // literal-ok: 02 default size
        public List<double> MissileQueue { get; init; } = new List<double>();   // queued fish-missile hit fractions, FIFO (issue #7)
        public DungeonRunState DungeonRun { get; set; }        // non-null only while Mode == Dungeon; Fight stays the frozen frontier fight
        public FightState Fight         { get; set; }          // transient fight state; never saved (3.3)

        public bool ShouldSerializeFight() => false;           // Json.NET naming convention, no attribute: the serializer skips Fight
    }

    /// <summary>In memory only; null after load or profile creation, and the next tick creates it as Spawning (03).</summary>
    public sealed class FightState
    {
        public FightPhase Phase        { get; set; }
        public int    SpawnTimer       { get; set; }          // ticks until Spawn()
        public double EnemyHp          { get; set; }
        public double EnemyMaxHp       { get; set; }
        public bool   EnemyIsBoss      { get; set; }
        public int    BossTicksLeft    { get; set; }          // 0..BOSS_TIME_TICKS
        public int    AttackProgress   { get; set; }          // fixed point, ATTACK_PROGRESS_UNIT (03 section 2.1)
        public List<DotInstance> ActiveDots { get; init; } = new List<DotInstance>();   // max 6 (03 section 2.3)
        public double AtkBuffPct       { get; set; }          // strongest active BUFF, percentage points
        public int    AtkBuffTicksLeft { get; set; }
        public double GoldBuffPct      { get; set; }          // strongest active BUFF_GOLD companion, percentage points (04 section 8.1)
        public int    GoldBuffTicksLeft { get; set; }
        public int    PendingTaps      { get; set; }          // taps accepted since the last tick
        public int    TapsThisSecond   { get; set; }          // TAP_ZAP_MAX_PER_SEC guard
        public long   TapWindowTick    { get; set; }          // Time.Tick at which the current tap window opened
    }

    /// <summary>
    /// One active DOT (03 section 2.3). A value, not a record class, so ticking it down replaces a list element without
    /// allocating (03 criterion 19); C# 9 has no record struct, so equality is written out.
    /// </summary>
    public readonly struct DotInstance : IEquatable<DotInstance>
    {
        public DotInstance(int ticksLeft, double damagePerTick)
        {
            TicksLeft = ticksLeft;
            DamagePerTick = damagePerTick;
        }

        public int TicksLeft { get; }
        public double DamagePerTick { get; }

        public bool Equals(DotInstance o) => TicksLeft == o.TicksLeft && DamagePerTick.Equals(o.DamagePerTick);
        public override bool Equals(object obj) => obj is DotInstance o && Equals(o);
        public override int GetHashCode() => (TicksLeft * 397) ^ DamagePerTick.GetHashCode();   // literal-ok: hash mixing, not balance
        public static bool operator ==(DotInstance a, DotInstance b) => a.Equals(b);
        public static bool operator !=(DotInstance a, DotInstance b) => !a.Equals(b);
    }

    /// <summary>A ticked dungeon run in the main loop (06 section 6.2; issue #42).</summary>
    public sealed class DungeonRunState
    {
        public Dungeon Kind          { get; init; }
        public int     Floor         { get; init; }
        public ulong   Seed          { get; init; }           // seeds the temporary `dungeon_run` stream (3.4)
        public long    StreamCounter { get; set; }            // draws consumed from `dungeon_run`
        public string  GrantId       { get; init; } = "";     // server grant id (MVP); "" in M0
        public int     EnemyIndex    { get; set; }            // always 0 in MVP (one boss per floor)
        public int     TicksLeft     { get; set; }
        public FightState Fight      { get; set; }            // transient: the floor's boss fight; never saved (a reload abandons the run, P16)

        public bool ShouldSerializeFight() => false;          // Json.NET naming convention, no attribute: the serializer skips Fight
    }

    public sealed class ProgressState
    {
        public int HighestCleared        { get; set; }        // highest stage completed at the frontier (issue #35)
        public int HighestFirstClearBoss { get; set; }        // highest boss stage that paid first-clear diamonds (03)
        public int StageCapReached       { get; set; }        // 0 or the tick at which STAGE_CAP was hit
    }

    /// <summary>Reset off Time.DayKey / Time.WeekKey (06 EnsureDay); no keys of its own.</summary>
    public sealed class QuestState
    {
        public Dictionary<string, QuestProgress> Daily       { get; init; } = new Dictionary<string, QuestProgress>(StringComparer.Ordinal);
        public Dictionary<string, QuestProgress> Weekly      { get; init; } = new Dictionary<string, QuestProgress>(StringComparer.Ordinal);
        public Dictionary<string, QuestProgress> Achievement { get; init; } = new Dictionary<string, QuestProgress>(StringComparer.Ordinal);
    }

    public enum QuestStatus { IN_PROGRESS, COMPLETED, CLAIMED }   // 00 decision 17

    public sealed class QuestProgress
    {
        public long        Progress { get; set; }   // counter; keeps counting past target (06)
        public QuestStatus State    { get; set; }
    }

    /// <summary>Daily counters reset off Time.DayKey (06).</summary>
    public sealed class DungeonState
    {
        public int[] BestFloor    { get; set; } = new int[3];   // per Dungeon ordinal; 0 = never cleared   // literal-ok: 02 default size
        public int[] EntriesToday { get; set; } = new int[3];   // literal-ok: 02 default size
        public int[] SweepsToday  { get; set; } = new int[3];   // literal-ok: 02 default size
        public List<DungeonRunRecord> RecentRuns { get; init; } = new List<DungeonRunRecord>();   // newest last, at most DUNGEON_RECENT_RUNS_MAX (issue #52)
    }

    public sealed record DungeonRunRecord(string GrantId, Dungeon Kind, int Floor, bool Cleared);

    /// <summary>06; both reset by the daily rollover.</summary>
    public sealed class ShopState
    {
        public bool DailyGiftClaimed { get; set; }
        public Dictionary<string, int> ExchangeCounts { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    public sealed class ResearchState
    {
        public Dictionary<string, int> Level { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);   // research id -> level >= 1; absent = 0
    }

    public sealed class InboxState
    {
        public HashSet<string> ClaimedIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);   // prevents a double-apply on replay
        public long LastFetchedServerTime { get; set; }
    }

    public sealed class OfflineState
    {
        public CatchupResult Pending { get; set; }   // non-null between ComputeOfflineCatchup and ClaimOfflineReward (01 section 8.7; issue #21)
        public long LifetimeOfflineSecCredited { get; set; }
    }

    /// <summary>TankFill: fish from 05's CatchUpTank(CreditedSec * TICK_HZ) at compute; applied at claim (issue #21).</summary>
    public sealed record CatchupResult(long ElapsedSec, long CreditedSec, bool Capped, double Gold, double Exp, int TankFill, int Stage,
                                       double KillTimeSec, long ComputedServerTime);

    public sealed class PendingState
    {
        public PendingSummon      Summon   { get; set; }                                   // one in flight (01 section 8.5)
        public PendingDungeonSeed Dungeon  { get; set; }
        public List<PendingReceipt> Receipts { get; init; } = new List<PendingReceipt>();  // zero or more (01 section 8.9)
    }

    public sealed record PendingSummon(string RequestId, Banner Banner, int Count, string Payment, long RequestedServerTime);
    public sealed record PendingDungeonSeed(string RequestId, Dungeon Kind, int Floor, long RequestedServerTime);
    public sealed record PendingReceipt(string RequestId, string Platform, string Sku, string TransactionId, string Payload, long RequestedServerTime);

    /// <summary>Issue #46.</summary>
    public sealed class TutorialState
    {
        public int  Step    { get; set; }        // 0..TUTORIAL_DONE (row count of tutorial_steps); advanced only by SetTutorialStep
        public bool Skipped { get; set; }
        public bool TutorialDiamondsGranted { get; set; }   // set when tut_welcome's grant is applied at profile creation; guards it to once
    }

    public sealed class RngStreamState
    {
        public string Seed    { get; init; } = "";   // exactly 16 lowercase hex chars (a ulong); string so JSON tooling never rounds it
        public long   Counter { get; set; }          // draws consumed so far, >= 0
    }

    public sealed class LifetimeStats
    {
        public long Kills, BossKills, BossFails, HeroHits, Taps, Pulls, Merges, FishReleased, FishDiscards, DungeonClears, TrainingLevelsBought;
        public int  FirstWallStage;                   // stage of the first BossFail ever; 0 = never walled (03)
        public long ActiveSeconds;                    // ticked time; analytics only
        public double GoldEarned, GoldSpent, FishCoinEarned;
        public long DiamondsEarned, DiamondsSpent, DiamondsPurchased;
        // Client-side ledger feed (issue #49): cumulative amounts the sim itself credited or debited, per reason, never reset.
        // Only reasons applied locally; never IAP, MAIL, ADMIN, REFUND (inbox grants) or SUMMON (server debit).
        public Dictionary<CurrencyReason, long> ClientDiamondsEarned { get; init; } = new Dictionary<CurrencyReason, long>();
        public Dictionary<CurrencyReason, long> ClientDiamondsSpent  { get; init; } = new Dictionary<CurrencyReason, long>();
        public Dictionary<Banner, Dictionary<CurrencyReason, long>> ClientTicketsEarned { get; init; } = new Dictionary<Banner, Dictionary<CurrencyReason, long>>();
    }
}
