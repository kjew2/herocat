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
