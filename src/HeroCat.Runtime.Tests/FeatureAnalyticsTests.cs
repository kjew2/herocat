using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>
    /// The feature specs' analytics rows (02 section 14.4's owners): 06 section 13 and 03's <c>awakened</c> (issue #177).
    /// Mapping tests publish sim events on a bare bus; the session tests drive real commands through SessionRunner.
    /// </summary>
    public class FeatureAnalyticsTests
    {
        sealed class Harness
        {
            public readonly EventBus Bus = new EventBus();
            public readonly MemoryAnalytics Analytics = new MemoryAnalytics();
            public readonly GameState State;
            public readonly FeatureAnalytics Feature;

            public Harness(ContentDb db = null)
            {
                db ??= TestContent.Mvp;
                State = ProfileFactory(db);
                Feature = new FeatureAnalytics(Bus, Analytics, db, () => State);
            }

            static GameState ProfileFactory(ContentDb db) =>
                HeroCat.Runtime.Profile.ProfileFactory.Create(db, Guid.Empty, TestContent.T0, TestContent.Device, TestContent.Root);

            public void Publish(params Event[] events) => Bus.Publish(events);

            public void Applied(Command c, params Event[] events)
            {
                Bus.PublishSystem(new CommandApplied(c, events));
                Bus.Publish(events);   // the frame's drain publishes the same events again
            }

            public MemoryAnalytics.Entry Single(string name)
            {
                Assert.That(Analytics.Count(name), Is.EqualTo(1), name);
                return Analytics.Events.Single(e => e.Name == name);
            }
        }

        static void AssertProps(MemoryAnalytics.Entry e, IDictionary<string, object> expected)
        {
            Assert.That(e.Props.Keys, Is.EquivalentTo(expected.Keys), e.Name);
            foreach (var kv in expected) Assert.That(e.Props[kv.Key], Is.EqualTo(kv.Value), e.Name + "." + kv.Key);
        }

        // ------------------------------------------------------------------------------------------- 06 section 13

        [Test]
        public void QuestCompleted_ForAMission_AchievementUnlocked_ForARung()
        {
            var h = new Harness();
            h.Publish(new QuestCompleted("d_merge"), new QuestCompleted("ach_stage_02"));
            AssertProps(h.Single("quest_completed"), new Dictionary<string, object> { ["questId"] = "d_merge", ["kind"] = "DAILY" });
            AssertProps(h.Single("achievement_unlocked"), new Dictionary<string, object> { ["questId"] = "ach_stage_02", ["rung"] = 2 });
        }

        [Test]
        public void QuestClaimed_OncePerQuest_BulkOnlyUnderClaimAllQuests()
        {
            var h = new Harness();
            h.Applied(new ClaimQuest("d_merge"), new QuestClaimed("d_merge", Currency.DIAMOND, 20),
                      new CurrencyChanged(Currency.DIAMOND, 20, 120, CurrencyReason.QUEST));
            AssertProps(h.Single("quest_claimed"), new Dictionary<string, object>
            {
                ["questId"] = "d_merge", ["kind"] = "DAILY", ["rewardCurrency"] = "DIAMOND", ["rewardAmount"] = 20L, ["bulk"] = false,
            });
            h.Analytics.Events.Clear();
            h.Applied(new ClaimAllQuests(QuestKind.WEEKLY), new QuestClaimed("w_login", Currency.DIAMOND, 50),
                      new QuestClaimed("w_bonus_all_skill", Currency.TICKET_SUMMON_SKILL, 1),
                      new QuestsBulkClaimed(QuestKind.WEEKLY, 2, Array.Empty<CurrencyDelta>()));
            var rows = h.Analytics.Events.Where(e => e.Name == "quest_claimed").ToList();
            Assert.That(rows.Select(r => (string)r.Props["questId"]), Is.EqualTo(new[] { "w_login", "w_bonus_all_skill" }));
            Assert.That(rows.All(r => (bool)r.Props["bulk"] && (string)r.Props["kind"] == "WEEKLY"), Is.True);
            Assert.That(rows[1].Props["rewardCurrency"], Is.EqualTo("TICKET_SUMMON_SKILL"));
        }

        [Test]
        public void DayRollover_OfflineCatchup_OfflineClaimed_DailyGift_Exchange_HaveTheirFields()
        {
            var h = new Harness();
            var pending = new CatchupResult(7200, 7000, false, 1234.5, 99.25, 6, 42, 3.5, TestContent.T0);
            h.Publish(new DayRolledOver(20456, 2, 3 * 86_400L + 17), new OfflineRewardComputed(pending), new OfflineRewardClaimed(pending),
                      new DailyGiftClaimed(Currency.TICKET_SUMMON_WEAPON, 1), new ItemExchanged("ex_ticket_fish", 2, 100));
            AssertProps(h.Single("day_rollover"), new Dictionary<string, object>
            {
                ["dayKey"] = 20456, ["skippedDays"] = 2, ["secondsSinceLastTick"] = 3 * 86_400L + 17,
            });
            AssertProps(h.Single("offline_catchup"), new Dictionary<string, object>
            {
                ["elapsedSec"] = 7200L, ["creditedSec"] = 7000L, ["capped"] = false, ["gold"] = 1234.5, ["exp"] = 99.25, ["tankFill"] = 6,
                ["pendingStage"] = 42,
            });
            AssertProps(h.Single("offline_claimed"), new Dictionary<string, object>
            {
                ["elapsedSec"] = 7200L, ["creditedSec"] = 7000L, ["capped"] = false, ["gold"] = 1234.5, ["exp"] = 99.25, ["tankFill"] = 6,
                ["x2"] = false,
            });
            AssertProps(h.Single("daily_gift_claimed"), new Dictionary<string, object>());
            AssertProps(h.Single("exchange"), new Dictionary<string, object> { ["offerId"] = "ex_ticket_fish", ["qty"] = 2, ["diamonds"] = 100L });
        }

        [Test]
        public void DungeonCleared_Failed_Swept_AreDungeonRunRows()   // 06 section 13 (issues #172, #173)
        {
            var h = new Harness();
            h.Publish(new DungeonCleared(Dungeon.FISH_COIN, 3, Currency.FISH_COIN, 150, 7, 120, "g1"),
                      new DungeonFailed(Dungeon.EXP, 2, 0.25, 8, 300, "g2"),
                      new DungeonSwept(Dungeon.ROCK_CANDY, 4, Currency.ROCK_CANDY, 40));
            var rows = h.Analytics.Events.Where(e => e.Name == "dungeon_run").ToList();
            Assert.That(rows.Select(r => ((string)r.Props["kind"], (int)r.Props["floor"], (string)r.Props["outcome"], (int)r.Props["ticksUsed"], (string)r.Props["grantId"])),
                        Is.EqualTo(new[] { ("FISH_COIN", 3, "cleared", 120, "g1"), ("EXP", 2, "failed", 300, "g2"), ("ROCK_CANDY", 4, "swept", 0, "") }));
            var reward = ((List<Dictionary<string, object>>)rows[0].Props["rewards"]).Single();
            Assert.That((reward["currency"], reward["amount"]), Is.EqualTo(((object)"FISH_COIN", (object)150.0)));
            Assert.That((List<Dictionary<string, object>>)rows[1].Props["rewards"], Is.Empty);
            AssertCatalogue(new AnalyticsEvent[] { new AnalyticsEvents.DungeonRun(Dungeon.EXP, 1, "abandoned", 0, null, 0, "g") });
        }

        [Test]
        public void Awakened_CarriesTheRanksCost_AndTheRockCandyBalanceAfterTheDebit()   // 03 section 11
        {
            var h = new Harness();
            h.Publish(new CurrencyChanged(Currency.ROCK_CANDY, -100, 25, CurrencyReason.AWAKENING), new Awakened(1));
            AssertProps(h.Single("awakened"), new Dictionary<string, object> { ["rank"] = 1, ["rockCandyCost"] = 100L, ["rockCandyAfter"] = 25.0 });
        }

        [Test]
        public void ItemEquipped_OnePerSlotChange_AnEmptiedSlotHasANullItem()   // 04 section 13
        {
            var h = new Harness();
            h.Publish(new ItemEquipped(Banner.SKILL, 2, "sk_a", null, false), new ItemEquipped(Banner.SKILL, 2, null, "sk_a", false));
            var rows = h.Analytics.Events.Where(e => e.Name == "item_equipped").ToList();
            Assert.That(rows, Has.Count.EqualTo(2));
            AssertProps(rows[0], new Dictionary<string, object>
            {
                ["banner"] = "SKILL", ["slot"] = 2, ["itemId"] = "sk_a", ["previousItemId"] = null, ["auto"] = false,
            });
            Assert.That((rows[1].Props["itemId"], rows[1].Props["previousItemId"]), Is.EqualTo(((object)null, "sk_a")));
        }

        [Test]
        public void LimitBreak_ThroughTheRunner_IsOneRowPerRank_WithTheKindRarityRankAndCost()   // 04 section 13, issue #163
        {
            var s = TestSession.Open();
            var db = s.Sim.Db;
            string w = db.TutorialWeaponId;
            var rarity = db.Weapons[w].Rarity;
            long perRank = db.Rarities[rarity].LimitBreakCost;
            s.Sim.State.Wallet.RockCandy = perRank * 3;   // test setup only: ranks 1 and 2 cost 1x and 2x
            int from = s.Analytics.Events.Count;
            Assert.That(s.Runner.Issue(new LimitBreak(w)).Ok, Is.True);
            Assert.That(s.Runner.Issue(new LimitBreak(w)).Ok, Is.True);
            Assert.That(s.Runner.Issue(new LimitBreak(w)).Ok, Is.False, "cannot afford rank 3: no row");
            s.Runner.PublishDrained();
            var rows = s.Analytics.Events.Skip(from).Where(e => e.Name == "limit_break").ToList();
            Assert.That(rows, Has.Count.EqualTo(2));
            AssertProps(rows[0], new Dictionary<string, object>
            {
                ["banner"] = "WEAPON", ["itemId"] = w, ["rarity"] = rarity.ToString(), ["rankAfter"] = 1, ["rockCandySpent"] = perRank,
            });
            Assert.That((rows[1].Props["rankAfter"], rows[1].Props["rockCandySpent"]), Is.EqualTo((2, perRank * 2)));
        }

        [Test]
        public void TypedFeatureEvents_UseKnownNames_AndCamelCaseProps_WithoutEnvelopeFields()
        {
            var all = new AnalyticsEvent[]
            {
                new AnalyticsEvents.QuestCompleted("q", QuestKind.DAILY), new AnalyticsEvents.QuestClaimed("q", QuestKind.DAILY, Currency.DIAMOND, 1, true),
                new AnalyticsEvents.AchievementUnlocked("a", 1), new AnalyticsEvents.OfflineCatchup(1, 1, true, 1, 1, 1, 1),
                new AnalyticsEvents.OfflineClaimed(1, 1, true, 1, 1, 1, false), new AnalyticsEvents.DayRollover(1, 0, 0),
                new AnalyticsEvents.DailyGiftClaimed(), new AnalyticsEvents.Exchange("o", 1, 50), new AnalyticsEvents.ShopView("DIAMONDS"),
                new AnalyticsEvents.Awakened(1, 100, 0),
                new AnalyticsEvents.Summon("r", Banner.WEAPON, 1, "DIAMOND", 100, 0, 0, 1, 1, 1,
                                           new[] { new AnalyticsEvents.SummonResultRow("w", Rarity.COMMON, true, 0, 1, 0, false) }, 0, false, "LOCAL", 1),
                new AnalyticsEvents.ItemAcquired(Banner.WEAPON, "w", Rarity.COMMON, true, 1, 0, 1, "SUMMON", "r"),
                new AnalyticsEvents.ItemEquipped(Banner.SKILL, 1, null, "s", false),
                new AnalyticsEvents.LimitBreak(Banner.WEAPON, "w", Rarity.COMMON, 1, 10),
                new AnalyticsEvents.SummonReconciled("r", Banner.WEAPON, Currency.DIAMOND, 100, 90),
                new AnalyticsEvents.InboxClaimed("i", "MAIL", new[] { new GrantLine("currency", "DIAMOND", 5) }, false, 3),
                new AnalyticsEvents.ContentUpdateFailed("2099.01.01", "E900", "skills.json"),
                new AnalyticsEvents.FishRelease(1, 2, 1, 1, false),
                new AnalyticsEvents.FishMerge(1, new[] { new AnalyticsEvents.FishLevelCount(2, 1) }, 1, new[] { 2 }, true, false),
                new AnalyticsEvents.FishDiscovered(2, 1), new AnalyticsEvents.FishDiscard(1, 0.5, 3),
                new AnalyticsEvents.FishBoardSnapshot(new[] { 1, 0 }, 3.85, 1.0385, 10, 6, 10, false, 1, 0, 1, 1, 60),
            };
            AssertCatalogue(all);
        }

        internal static void AssertCatalogue(IEnumerable<AnalyticsEvent> all)
        {
            foreach (var e in all)
            {
                Assert.That(AnalyticsNames.IsKnown(e.Name), Is.True, e.Name);
                foreach (var k in e.Props().Keys)
                {
                    Assert.That(AnalyticsNames.IsCamelCase(k), Is.True, e.Name + "." + k);
                    Assert.That(AnalyticsEnvelope.Fields, Does.Not.Contain(k), e.Name + "." + k);
                }
            }
        }

        // ------------------------------------------------------------------------------------------- 05 section 13

        static List<MemoryAnalytics.Entry> Rows(TestSession s, int from, string name) =>
            s.Analytics.Events.Skip(from).Where(e => e.Name == name).ToList();

        static int Occupied(TestSession s) => s.Sim.State.Fish.Cells.Count(c => c != 0);

        [Test]
        public void ReleaseFish_IsOneRowPerTap_ReleaseMax_OneRowWithTheCount()
        {
            var s = TestSession.Open();
            int stock = s.Sim.State.Fish.TankCount;
            Assert.That(stock, Is.GreaterThan(2), "the tank starts full");
            int from = s.Analytics.Events.Count;
            s.Runner.Issue(new ReleaseFish());
            AssertProps(Rows(s, from, "fish_release").Single(), new Dictionary<string, object>
            {
                ["count"] = 1, ["stockBefore"] = stock, ["stockAfter"] = stock - 1, ["boardOccupancyAfter"] = 1, ["viaButton"] = false,
            });
            from = s.Analytics.Events.Count;
            s.Runner.Issue(new ReleaseMax());
            AssertProps(Rows(s, from, "fish_release").Single(), new Dictionary<string, object>
            {
                ["count"] = stock - 1, ["stockBefore"] = stock - 1, ["stockAfter"] = 0, ["boardOccupancyAfter"] = Occupied(s), ["viaButton"] = true,
            });
            from = s.Analytics.Events.Count;
            s.Runner.Issue(new ReleaseMax());   // the tank is empty: accepted, nothing released
            Assert.That(Rows(s, from, "fish_release"), Is.Empty);
        }

        [Test]
        public void MergeAll_IsOneRowWithTheHistogram_MergeFish_OneRowPerMerge()   // 05 section 13, issue #168 AC 1
        {
            var s = TestSession.Open();
            s.Sim.State.Fish.TankCount = 20;
            s.Runner.Issue(new ReleaseMax());
            s.Runner.PublishDrained();   // the release's own discovery (level 1) goes out with this drain
            int from = s.Analytics.Events.Count;
            var merged = new List<FishMerged>();
            using (s.Bus.Subscribe<FishMerged>(merged.Add))
            {
                s.Runner.Issue(new MergeAll());
                s.Runner.PublishDrained();
            }
            Assert.That(merged.Count, Is.GreaterThan(1));
            var row = Rows(s, from, "fish_merge").Single().Props;
            Assert.That(row.Keys, Is.EquivalentTo(new[] { "count", "levelsCreated", "coins", "discoveries", "viaButton", "duringBoss" }));
            Assert.That(row["count"], Is.EqualTo(merged.Count));
            var hist = ((List<Dictionary<string, object>>)row["levelsCreated"]).Select(d => ((int)d["level"], (int)d["count"])).ToList();
            Assert.That(hist, Is.EqualTo(merged.GroupBy(m => m.NewLevel).OrderBy(g => g.Key).Select(g => (g.Key, g.Count()))));
            Assert.That(row["coins"], Is.EqualTo(merged.Sum(m => m.Coins)).Within(1e-9));
            Assert.That(row["discoveries"], Is.EqualTo(merged.Where(m => m.Discovered).Select(m => m.NewLevel)));
            Assert.That((row["viaButton"], row["duringBoss"]), Is.EqualTo((true, false)));
            Assert.That(Rows(s, from, "fish_discovered").Select(e => (int)e.Props["level"]),
                        Is.EqualTo(merged.Where(m => m.Discovered).Select(m => m.NewLevel)), "one fish_discovered per FishDiscovered");

            // one manual merge: a pair of equal fish
            var cells = s.Sim.State.Fish.Cells;
            s.Sim.State.Fish.TankCount = 2;
            s.Runner.Issue(new ReleaseMax());
            var pair = Enumerable.Range(0, cells.Length).Where(i => cells[i] != 0).GroupBy(i => cells[i]).First(g => g.Count() >= 2).Take(2).ToArray();
            from = s.Analytics.Events.Count;
            Assert.That(s.Runner.Issue(new MergeFish(pair[0], pair[1])).Ok, Is.True);
            var manual = Rows(s, from, "fish_merge").Single().Props;
            Assert.That((manual["count"], manual["viaButton"]), Is.EqualTo((1, false)));
        }

        [Test]
        public void FishMerge_DuringABossFight_SaysSo()
        {
            var h = new Harness(TestContent.Db);
            h.State.Combat.Fight = new FightState { EnemyIsBoss = true };
            h.Applied(new MergeFish(0, 1), new FishMerged(0, 1, 3, 12.5, 1.0, true), new FishDiscovered(3, 12.5));
            AssertProps(h.Single("fish_merge"), new Dictionary<string, object>
            {
                ["count"] = 1, ["levelsCreated"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["level"] = 3, ["count"] = 1 } },
                ["coins"] = 12.5, ["discoveries"] = new List<int> { 3 }, ["viaButton"] = false, ["duringBoss"] = true,
            });
            AssertProps(h.Single("fish_discovered"), new Dictionary<string, object> { ["level"] = 3, ["coins"] = 12.5 });
        }

        [Test]
        public void DiscardFish_IsOneRow_WithTheOccupancyBefore()
        {
            var s = TestSession.Open();
            s.Runner.Issue(new ReleaseMax());
            int occupied = Occupied(s);
            var cells = s.Sim.State.Fish.Cells;
            int cell = Array.FindIndex(cells, c => c != 0);
            int level = cells[cell];
            double coins = TestContent.Db.Constants.FishDiscardCoinsPct * TestContent.Db.FishLevels[level - 1].DiscoveryCoins;
            int from = s.Analytics.Events.Count;
            s.Runner.Issue(new DiscardFish(cell));
            AssertProps(Rows(s, from, "fish_discard").Single(), new Dictionary<string, object>
            {
                ["level"] = level, ["coins"] = coins, ["boardOccupancyBefore"] = occupied,
            });
        }

        [Test]
        public void TheBoardSnapshot_FiresOnceAtSessionEnd_WithTheBoardAndTheSessionTotals()   // issue #168 AC 2
        {
            var s = TestSession.Open();
            s.Runner.Issue(new ReleaseMax());
            s.Runner.Issue(new MergeAll());
            s.Play(30);
            var st = s.Sim.State;
            long released = st.Stats.FishReleased, merges = st.Stats.Merges;
            Assert.That(s.Analytics.Count("fish_board_snapshot"), Is.Zero);
            s.Runner.OnPause();
            s.Runner.OnQuit();
            Assert.That(s.Analytics.Count("fish_board_snapshot"), Is.EqualTo(1), "a quit after a pause sends no second snapshot");
            var e = s.Analytics.Events.Single(x => x.Name == "fish_board_snapshot");
            Assert.That(e.Props.Keys, Is.EquivalentTo(new[]
            {
                "cells", "fishBoardDpsPct", "fishMult", "tankCap", "tankRefillS", "fishMaxLvl", "autoMerge",
                "releases", "merges", "discoveries", "coinsEarned", "activeSec",
            }));
            var db = TestContent.Db;
            double dps = HeroCat.Sim.Fish.FishBoard.BoardDpsPct(st, db);
            Assert.That(e.Props["cells"], Is.EqualTo(st.Fish.Cells));
            Assert.That((e.Props["fishBoardDpsPct"], e.Props["fishMult"]), Is.EqualTo((dps, 1 + dps / 100)));
            Assert.That((e.Props["tankCap"], e.Props["fishMaxLvl"], e.Props["autoMerge"]),
                        Is.EqualTo((HeroCat.Sim.Fish.FishBoard.TankCap(st, db), HeroCat.Sim.Fish.FishBoard.FishMaxLvl(st, db), false)));
            Assert.That(e.Props["tankRefillS"], Is.EqualTo(HeroCat.Sim.Fish.FishBoard.TankRefillSeconds(st, db)));
            Assert.That((e.Props["releases"], e.Props["merges"]), Is.EqualTo((released, merges)), "a new profile's whole history is this session");
            Assert.That(e.Props["discoveries"], Is.EqualTo((long)st.Fish.DiscoveredLevels.Count));
            Assert.That((double)e.Props["coinsEarned"], Is.EqualTo(st.Wallet.Get(Currency.FISH_COIN)).Within(1e-9), "every Fish Coin came from the board");
            Assert.That(e.Props["activeSec"], Is.EqualTo(st.Stats.ActiveSeconds));

            s.Runner.OnResume();   // a new session counts from zero
            s.Runner.OnPause();
            var second = s.Analytics.Events.Last(x => x.Name == "fish_board_snapshot");
            Assert.That((second.Props["releases"], second.Props["merges"], second.Props["activeSec"]), Is.EqualTo((0L, 0L, 0L)));
        }

        // ------------------------------------------------------------------------------------------- through a session

        [Test]
        public void ABoot_SendsDayRolloverOnce_AndQuestCompletedForDLogin()
        {
            var s = TestSession.Open(content: TestContent.Mvp);
            Assert.That(s.Boot.FeatureAnalytics, Is.Not.Null);
            var roll = s.Analytics.Events.Single(e => e.Name == "day_rollover");
            Assert.That(roll.Props["dayKey"], Is.EqualTo(s.Sim.State.Time.DayKey));
            Assert.That(roll.Props["skippedDays"], Is.EqualTo(0), "a new profile skipped nothing");
            Assert.That(s.Analytics.Events.Count(e => e.Name == "quest_completed" && (string)e.Props["questId"] == "d_login"), Is.EqualTo(1));
        }

        [Test]
        public void AGapOfDays_ReportsTheSkippedDays_AndTheSecondsSinceTheLastTick()
        {
            var s = TestSession.Open(content: TestContent.Mvp);
            long last = s.Sim.State.Time.LastTickServerTime;
            int day = s.Sim.State.Time.DayKey;
            s.Runner.OnPause();
            s.Clock.Advance(3 * 86_400 + 5);
            s.Runner.OnResume();
            var roll = s.Analytics.Events.Last(e => e.Name == "day_rollover");
            Assert.That(roll.Props["dayKey"], Is.EqualTo(day + 3));
            Assert.That(roll.Props["skippedDays"], Is.EqualTo(2));
            Assert.That(roll.Props["secondsSinceLastTick"], Is.EqualTo(s.Clock.NowServerSeconds - last));
            Assert.That(s.Analytics.Count("offline_catchup"), Is.EqualTo(1), "the gap stored a pending reward");
            s.Runner.Issue(new ClaimOfflineReward());
            s.Runner.PublishDrained();
            Assert.That(s.Analytics.Count("offline_claimed"), Is.EqualTo(1));
        }

        [Test]
        public void ClaimAllQuests_ThroughTheRunner_IsOneBulkRowPerQuest()
        {
            var s = TestSession.Open(content: TestContent.Mvp);
            var st = s.Sim.State;
            st.Progress.HighestCleared = TestContent.Mvp.FeatureUnlocks["QUESTS"].Threshold.Value;
            st.Fish.TankCount = 30;
            s.Runner.Issue(new ReleaseMax());                                  // 20: the board is full
            for (int i = 0; i < 10; i++) s.Runner.Issue(new DiscardFish(i));
            s.Runner.Issue(new ReleaseMax());                                  // 10 more: d_release (30) completes
            s.Runner.PublishDrained();
            Assert.That(s.Analytics.Events.Count(e => e.Name == "quest_completed" && (string)e.Props["questId"] == "d_release"), Is.EqualTo(1));
            Assert.That(s.Runner.Issue(new ClaimAllQuests(QuestKind.DAILY)).Ok, Is.True);
            s.Runner.PublishDrained();
            var claimed = s.Analytics.Events.Where(e => e.Name == "quest_claimed").ToList();
            Assert.That(claimed.Select(e => (string)e.Props["questId"]), Does.Contain("d_release"));
            Assert.That(claimed.All(e => (bool)e.Props["bulk"]), Is.True);
        }
    }
}
