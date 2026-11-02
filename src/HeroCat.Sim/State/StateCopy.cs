using System;
using System.Collections.Generic;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;

namespace HeroCat.Sim.State
{
    /// <summary>
    /// Deep copy of a GameState (02 section 3.1: bots, tests, the conflict flow, LocalSummonService's roll-on-a-clone).
    /// Every field is copied, the transient Combat.Fight included, so a clone mid-fight continues identically (03 AC15).
    /// Records (DotInstance, CatchupResult, Pending*, DungeonRunRecord) are immutable and shared.
    /// </summary>
    public static class StateCopy
    {
        public static GameState DeepClone(GameState s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            var c = new GameState
            {
                SchemaVersion = s.SchemaVersion,
                ContentVersion = s.ContentVersion,
                ContentHash = s.ContentHash,
                PlayerId = s.PlayerId,
                DeviceId = s.DeviceId,
                RngRoot = s.RngRoot,
                SaveSeq = s.SaveSeq,
                Time = new TimeState
                {
                    Tick = s.Time.Tick, LastSavedServerTime = s.Time.LastSavedServerTime, LastTickServerTime = s.Time.LastTickServerTime,
                    AnchorServerTime = s.Time.AnchorServerTime, AnchorTick = s.Time.AnchorTick, CreatedServerTime = s.Time.CreatedServerTime,
                    DayKey = s.Time.DayKey, WeekKey = s.Time.WeekKey, LoginDayKey = s.Time.LoginDayKey,
                },
                Hero = new HeroState { Level = s.Hero.Level, Exp = s.Hero.Exp, AwakenRank = s.Hero.AwakenRank },
                Training = new TrainingState { Level = Copy(s.Training.Level) },
                Wallet = new Wallet
                {
                    Gold = s.Wallet.Gold, Diamond = s.Wallet.Diamond, FishCoin = s.Wallet.FishCoin, RockCandy = s.Wallet.RockCandy,
                    DungeonTicket = Copy(s.Wallet.DungeonTicket), SummonTicket = Copy(s.Wallet.SummonTicket), GuildCoin = s.Wallet.GuildCoin,
                },
                Weapons = Copy(s.Weapons),
                Skills = Copy(s.Skills),
                Companions = Copy(s.Companions),
                Runes = Copy(s.Runes),
                Loadout = new LoadoutState
                {
                    Weapon = s.Loadout.Weapon, Skills = Copy(s.Loadout.Skills), Companions = Copy(s.Loadout.Companions),
                    Runes = Copy(s.Loadout.Runes),
                },
                Fish = new FishBoardState
                {
                    Cells = Copy(s.Fish.Cells), TankCount = s.Fish.TankCount, TankRefillTick = s.Fish.TankRefillTick,
                    TankReleaseLevel = s.Fish.TankReleaseLevel, DiscoveredLevels = new HashSet<int>(s.Fish.DiscoveredLevels),
                    HighestLevel = s.Fish.HighestLevel, TotalMerges = s.Fish.TotalMerges, AutoMergeEnabled = s.Fish.AutoMergeEnabled,
                },
                Banners = new Dictionary<Banner, BannerState>(),
                Combat = Copy(s.Combat),
                Progress = new ProgressState
                {
                    HighestCleared = s.Progress.HighestCleared, HighestFirstClearBoss = s.Progress.HighestFirstClearBoss,
                    StageCapReached = s.Progress.StageCapReached,
                },
                Quests = new QuestState { Daily = Copy(s.Quests.Daily), Weekly = Copy(s.Quests.Weekly), Achievement = Copy(s.Quests.Achievement) },
                Dungeons = new DungeonState
                {
                    BestFloor = Copy(s.Dungeons.BestFloor), EntriesToday = Copy(s.Dungeons.EntriesToday),
                    SweepsToday = Copy(s.Dungeons.SweepsToday), RecentRuns = new List<DungeonRunRecord>(s.Dungeons.RecentRuns),
                },
                Shop = new ShopState
                {
                    DailyGiftClaimed = s.Shop.DailyGiftClaimed,
                    ExchangeCounts = new Dictionary<string, int>(s.Shop.ExchangeCounts, StringComparer.Ordinal),
                },
                Research = new ResearchState { Level = new Dictionary<string, int>(s.Research.Level, StringComparer.Ordinal) },
                Inbox = new InboxState
                {
                    ClaimedIds = new HashSet<string>(s.Inbox.ClaimedIds, StringComparer.Ordinal),
                    LastFetchedServerTime = s.Inbox.LastFetchedServerTime,
                },
                Offline = new OfflineState { Pending = s.Offline.Pending, LifetimeOfflineSecCredited = s.Offline.LifetimeOfflineSecCredited },
                Pending = new PendingState
                {
                    Summon = s.Pending.Summon, Dungeon = s.Pending.Dungeon, Receipts = new List<PendingReceipt>(s.Pending.Receipts),
                },
                Tutorial = new TutorialState
                {
                    Step = s.Tutorial.Step, Skipped = s.Tutorial.Skipped, TutorialDiamondsGranted = s.Tutorial.TutorialDiamondsGranted,
                },
                Rng = new Dictionary<string, RngStreamState>(StringComparer.Ordinal),
                Stats = Copy(s.Stats),
                DebugTainted = s.DebugTainted,
            };
            foreach (var kv in s.Banners)
                c.Banners[kv.Key] = kv.Value == null ? null : new BannerState { Pity = kv.Value.Pity, Exp = kv.Value.Exp, TotalPulls = kv.Value.TotalPulls };
            foreach (var kv in s.Rng)
                c.Rng[kv.Key] = kv.Value == null ? null : new RngStreamState { Seed = kv.Value.Seed, Counter = kv.Value.Counter };
            return c;
        }

        static T[] Copy<T>(T[] a) => a == null ? null : (T[])a.Clone();

        static CollectionState Copy(CollectionState c)
        {
            var r = new CollectionState();
            foreach (var kv in c.Items)
                r.Items[kv.Key] = kv.Value == null ? null : new OwnedItem
                {
                    Copies = kv.Value.Copies, LimitBreaks = kv.Value.LimitBreaks, FirstAcquiredServerTime = kv.Value.FirstAcquiredServerTime,
                };
            return r;
        }

        static Dictionary<string, QuestProgress> Copy(Dictionary<string, QuestProgress> q)
        {
            var r = new Dictionary<string, QuestProgress>(StringComparer.Ordinal);
            foreach (var kv in q) r[kv.Key] = kv.Value == null ? null : new QuestProgress { Progress = kv.Value.Progress, State = kv.Value.State };
            return r;
        }

        static CombatState Copy(CombatState s)
        {
            var c = new CombatState
            {
                Mode = s.Mode, Stage = s.Stage, KillsInStage = s.KillsInStage, Boss = s.Boss, BossFailsOnStage = s.BossFailsOnStage,
                Selected = s.Selected, Mp = s.Mp, SkillCooldowns = Copy(s.SkillCooldowns), CompanionCooldowns = Copy(s.CompanionCooldowns),
                MissileQueue = new List<double>(s.MissileQueue),
            };
            if (s.DungeonRun != null)
            {
                var d = s.DungeonRun;
                c.DungeonRun = new DungeonRunState
                {
                    Kind = d.Kind, Floor = d.Floor, Seed = d.Seed, StreamCounter = d.StreamCounter, GrantId = d.GrantId,
                    EnemyIndex = d.EnemyIndex, TicksLeft = d.TicksLeft, Fight = Copy(d.Fight),
                };
            }
            c.Fight = Copy(s.Fight);
            return c;
        }

        static FightState Copy(FightState f)
        {
            if (f == null) return null;
            return new FightState
            {
                Phase = f.Phase, SpawnTimer = f.SpawnTimer, EnemyHp = f.EnemyHp, EnemyMaxHp = f.EnemyMaxHp, EnemyIsBoss = f.EnemyIsBoss,
                BossTicksLeft = f.BossTicksLeft, AttackProgress = f.AttackProgress, ActiveDots = new List<DotInstance>(f.ActiveDots),
                AtkBuffPct = f.AtkBuffPct, AtkBuffTicksLeft = f.AtkBuffTicksLeft, PendingTaps = f.PendingTaps,
                TapsThisSecond = f.TapsThisSecond, TapWindowTick = f.TapWindowTick,
            };
        }

        static LifetimeStats Copy(LifetimeStats s)
        {
            var c = new LifetimeStats
            {
                Kills = s.Kills, BossKills = s.BossKills, BossFails = s.BossFails, HeroHits = s.HeroHits, Taps = s.Taps, Pulls = s.Pulls,
                Merges = s.Merges, FishReleased = s.FishReleased, FishDiscards = s.FishDiscards, DungeonClears = s.DungeonClears,
                TrainingLevelsBought = s.TrainingLevelsBought, FirstWallStage = s.FirstWallStage, ActiveSeconds = s.ActiveSeconds,
                GoldEarned = s.GoldEarned, GoldSpent = s.GoldSpent, FishCoinEarned = s.FishCoinEarned,
                DiamondsEarned = s.DiamondsEarned, DiamondsSpent = s.DiamondsSpent, DiamondsPurchased = s.DiamondsPurchased,
                ClientDiamondsEarned = new Dictionary<CurrencyReason, long>(s.ClientDiamondsEarned),
                ClientDiamondsSpent = new Dictionary<CurrencyReason, long>(s.ClientDiamondsSpent),
            };
            foreach (var kv in s.ClientTicketsEarned) c.ClientTicketsEarned[kv.Key] = new Dictionary<CurrencyReason, long>(kv.Value);
            return c;
        }
    }
}
