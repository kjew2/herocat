using System;
using System.Collections.Generic;
using HeroCat.Runtime.Profile;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using Newtonsoft.Json.Linq;

namespace HeroCat.Runtime.Save
{
    /// <summary>
    /// Startup guard against IL2CPP managed code stripping (issue #8). Newtonsoft builds GameState by reflection, so a
    /// stripped setter or constructor does not throw: the field silently comes back as its default. This round-trips a
    /// state with every field non-default through the real Encode and DecodeFull path and compares. Any difference means
    /// the build lost serialization metadata (check unity/HeroCatClient/Assets/link.xml). Touches no files.
    /// </summary>
    public static class SaveSelfCheck
    {
        public sealed class Result
        {
            /// <summary>JSON paths that differ (or an exception message). Empty when the round trip is exact.</summary>
            public List<string> Mismatches { get; } = new List<string>();

            public bool Ok => Mismatches.Count == 0;

            public override string ToString() => Ok
                ? "save self-check OK"
                : $"save self-check FAILED ({Mismatches.Count}):\n  " + string.Join("\n  ", Mismatches);
        }

        public static Result Run(ContentDb content, long nowServerSec = SaveSamples.DefaultNow)
        {
            var result = new Result();
            try
            {
                var state = SaveSamples.EveryFieldNonDefault(content, nowServerSec);
                var (back, _, _) = SaveSystem.DecodeFull(SaveSerializer.Encode(state), content);
                state.Combat.Fight = null;   // transient, never saved (02 section 3.3)
                Diff(SaveSerializer.ToJson(state), SaveSerializer.ToJson(back), "$", result.Mismatches);
                if (!StateEquality.Equal(state, back)) result.Mismatches.Add("$: canonical encodings differ after the round trip");
            }
            catch (Exception e)
            {
                result.Mismatches.Add("round trip threw " + e.GetType().Name + ": " + e.Message);
            }
            return result;
        }

        /// <summary>Structural JSON diff; appends one line per differing path.</summary>
        internal static void Diff(JToken expected, JToken actual, string path, List<string> into)
        {
            if (expected is JObject eo && actual is JObject ao)
            {
                foreach (var p in eo.Properties())
                {
                    if (ao.TryGetValue(p.Name, out var av)) Diff(p.Value, av, path + "." + p.Name, into);
                    else into.Add($"{path}.{p.Name}: missing after load");
                }
                foreach (var p in ao.Properties())
                    if (!eo.ContainsKey(p.Name)) into.Add($"{path}.{p.Name}: unexpected after load");
            }
            else if (expected is JArray ea && actual is JArray aa && ea.Count == aa.Count)
            {
                for (int i = 0; i < ea.Count; i++) Diff(ea[i], aa[i], $"{path}[{i}]", into);
            }
            else if (!JToken.DeepEquals(expected, actual))
            {
                into.Add($"{path}: wrote {Short(expected)}, read back {Short(actual)}");
            }
        }

        static string Short(JToken t)
        {
            var s = t?.ToString(Newtonsoft.Json.Formatting.None) ?? "<none>";
            return s.Length > 80 ? s.Substring(0, 77) + "..." : s;
        }
    }

    /// <summary>Sample states for the self-check and the round-trip tests (02 acceptance 1).</summary>
    public static class SaveSamples
    {
        /// <summary>2026-01-01T00:00:00Z.</summary>
        public const long DefaultNow = 1767225600;

        public static readonly Guid SampleId = new Guid("5e1fc8ec-0000-4000-8000-000000000008");

        /// <summary>
        /// A state with every field non-default: null slots next to filled ones, non-empty collections, non-null
        /// Pending.Summon, Offline.Pending and Combat.DungeonRun, and a live Combat.Fight (which the save drops).
        /// Fields are set directly: this is fixture data, not play.
        /// </summary>
        public static GameState EveryFieldNonDefault(ContentDb db, long now = DefaultNow)
        {
            var s = ProfileFactory.Create(db, SampleId, now, new Guid("0d0d0d0d-0000-4000-8000-000000000001"), SampleId);
            var sim = new HeroCat.Sim.Simulation(s, db);
            sim.Apply(new ApplyServerTime(now));
            sim.Advance(37);
            s.SaveSeq = 7;
            s.Time.LastSavedServerTime = now + 3;
            s.Time.LastTickServerTime = now + 2;
            s.Party.Level = 3; s.Party.Exp = 12.5; s.Party.Slots[2].AwakenRank = 1;
            for (int i = 0; i < s.Training.Level.Length; i++) s.Training.Level[i] = i + 1;
            s.Wallet.Gold = 1.8423371e12; s.Wallet.FishCoin = double.MaxValue / 2; s.Wallet.RockCandy = 41; s.Wallet.GuildCoin = 2;
            s.Wallet.DungeonTicket = new[] { 1, 0, 2 }; s.Wallet.SummonTicket = new[] { 3, 0, 1, 0 };
            s.Skills.Items["skill_c_01"] = new OwnedItem { Copies = 4, LimitBreaks = 1, FirstAcquiredServerTime = now + 1 };
            s.Companions.Items["comp_c_01"] = new OwnedItem { Copies = 2, FirstAcquiredServerTime = now + 2 };
            s.Runes.Items["rune_c_01"] = new OwnedItem { Copies = 1, FirstAcquiredServerTime = now + 3 };
            s.Party.Slots[0].Skill = "skill_c_01"; s.Party.Slots[1].Weapon = null; s.Party.Slots[4].Companion = null; s.Loadout.Runes[2] = "rune_c_01";
            s.Fish.Cells[0] = 3; s.Fish.Cells[7] = 1; s.Fish.TankCount = 4; s.Fish.TankRefillTick = 23; s.Fish.TankReleaseLevel = 2;
            s.Fish.DiscoveredLevels.Add(3); s.Fish.DiscoveredLevels.Add(1); s.Fish.DiscoveredLevels.Add(2);
            s.Fish.HighestLevel = 3; s.Fish.TotalMerges = 5; s.Fish.AutoMergeEnabled = true;
            s.Banners[Banner.WEAPON] = new BannerState { Pity = 23, Exp = 41, TotalPulls = 41 };
            s.Banners[Banner.COMPANION] = new BannerState { Pity = 2, Exp = 3, TotalPulls = 3 };
            var c = s.Combat;
            c.Mode = CombatMode.Dungeon; c.Stage = 20; c.KillsInStage = 6; c.Boss = BossPhase.Walled; c.BossFailsOnStage = 2;
            c.Selected = 11; c.Mp[0] = 84.0; c.Mp[3] = 2.5; c.SkillCooldowns[1] = 31; c.CompanionCooldowns[0] = 12;
            c.MissileQueue.Add(0.25); c.MissileQueue.Add(1.5);
            c.DungeonRun = new DungeonRunState { Kind = Dungeon.ROCK_CANDY, Floor = 3, Seed = 0xDEADBEEFCAFEF00DUL, StreamCounter = 9, GrantId = "dgr_1", EnemyIndex = 0, TicksLeft = 120 };
            c.Fight = new FightState { Phase = FightPhase.Fighting, SpawnTimer = 2, EnemyHp = 10.5, EnemyMaxHp = 20, EnemyIsBoss = true, BossTicksLeft = 99 };
            c.Fight.ActiveDots.Add(new DotInstance(2, 4, 1.25));
            s.Progress.HighestCleared = 19; s.Progress.HighestFirstClearBoss = 10; s.Progress.StageCapReached = 0;
            s.Quests.Daily["d_kill"] = new QuestProgress { Progress = 731, State = QuestStatus.CLAIMED };
            s.Quests.Weekly["w_kill"] = new QuestProgress { Progress = 3, State = QuestStatus.IN_PROGRESS };
            s.Quests.Achievement["ach_stage_01"] = new QuestProgress { Progress = 20, State = QuestStatus.COMPLETED };
            s.Dungeons.BestFloor = new[] { 9, 6, 8 }; s.Dungeons.EntriesToday = new[] { 1, 1, 0 }; s.Dungeons.SweepsToday = new[] { 0, 1, 0 };
            s.Dungeons.RecentRuns.Add(new DungeonRunRecord("dgr_0", Dungeon.FISH_COIN, 9, true));
            s.Shop.DailyGiftClaimed = true; s.Shop.ExchangeCounts["ex_b"] = 2; s.Shop.ExchangeCounts["ex_a"] = 1;
            s.Research.Level["res_mp_pool"] = 3;
            s.Inbox.ClaimedIds.Add("ibx_b"); s.Inbox.ClaimedIds.Add("ibx_a"); s.Inbox.LastFetchedServerTime = now - 100;
            s.Offline.Pending = new CatchupResult(7200, 7200, false, 1234.5, 99.25, 3, 20, 2.5, now + 1);
            s.Offline.LifetimeOfflineSecCredited = 3600;
            s.Pending.Summon = new PendingSummon("0123456789abcdef0123456789abcdef", Banner.SKILL, 10, "Diamonds", now);
            s.Pending.Dungeon = new PendingDungeonSeed("fedcba9876543210fedcba9876543210", Dungeon.EXP, 2, now);
            s.Pending.Receipts.Add(new PendingReceipt("r1", "ios", "dia_1", "t1", "payload", now));
            s.Tutorial.Step = 4; s.Tutorial.Skipped = true;
            s.Rng["crit"].Counter = 17;
            var st = s.Stats;
            st.Kills = 1; st.BossKills = 2; st.BossFails = 3; st.PartyHits = 17; st.Taps = 5; st.Pulls = 6; st.Merges = 5; st.FishReleased = 8;
            st.FishDiscards = 9; st.DungeonClears = 10; st.TrainingLevelsBought = 11; st.FirstWallStage = 10; st.ActiveSeconds = 3;
            st.GoldSpent = 2.5; st.FishCoinEarned = 4.25; st.DiamondsSpent = 100; st.DiamondsPurchased = 300;
            st.ClientDiamondsSpent[CurrencyReason.SHOP_EXCHANGE] = 200;
            st.ClientTicketsEarned[Banner.WEAPON] = new Dictionary<CurrencyReason, long> { [CurrencyReason.DAILY_GIFT] = 3, [CurrencyReason.QUEST] = 1 };
            s.DebugTainted = true;
            return s;
        }
    }
}
