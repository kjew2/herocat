using System;
using System.Collections.Generic;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// Every analytics event name of the 02 section 14.4 index (snake_case), so emitters cannot misspell one. Each owner
    /// section defines its event's fields (00 decision 96); a new event is added there, to the index and here together.
    /// </summary>
    public static class AnalyticsNames
    {
        // 03 section 11 (combat and progression)
        public const string STAGE_CLEARED = "stage_cleared";
        public const string BOSS_FAILED = "boss_failed";
        public const string WALL_BROKEN = "wall_broken";
        public const string TRAINING = "training";
        public const string LEVEL_UP = "level_up";
        public const string AWAKENED = "awakened";
        public const string RESEARCH_BOUGHT = "research_bought";

        // 04 section 13 (summons and collection)
        public const string SUMMON = "summon";
        public const string SUMMON_RECONCILED = "summon_reconciled";
        public const string SUMMON_PENDING = "summon_pending";
        public const string SUMMON_RESOLVED = "summon_resolved";
        public const string ITEM_ACQUIRED = "item_acquired";
        public const string ITEM_EQUIPPED = "item_equipped";
        public const string LIMIT_BREAK = "limit_break";

        // 05 section 13 (fish)
        public const string FISH_RELEASE = "fish_release";
        public const string FISH_MERGE = "fish_merge";
        public const string FISH_DISCOVERED = "fish_discovered";
        public const string FISH_DISCARD = "fish_discard";
        public const string FISH_BOARD_SNAPSHOT = "fish_board_snapshot";

        // 06 section 13 (economy, quests, dungeons, offline, shop, IAP)
        public const string CURRENCY_CHANGED = "currency_changed";
        public const string QUEST_COMPLETED = "quest_completed";
        public const string QUEST_CLAIMED = "quest_claimed";
        public const string ACHIEVEMENT_UNLOCKED = "achievement_unlocked";
        public const string DUNGEON_RUN = "dungeon_run";
        public const string OFFLINE_CATCHUP = "offline_catchup";
        public const string OFFLINE_CLAIMED = "offline_claimed";
        public const string DAY_ROLLOVER = "day_rollover";
        public const string DAILY_GIFT_CLAIMED = "daily_gift_claimed";
        public const string EXCHANGE = "exchange";
        public const string SHOP_VIEW = "shop_view";
        public const string IAP_INITIATED = "iap_initiated";
        public const string IAP_COMPLETED = "iap_completed";
        public const string IAP_FAILED = "iap_failed";
        public const string IAP_STATUS = "iap_status";
        public const string INBOX_CLAIMED = "inbox_claimed";

        // 08 section 7.4
        public const string PLAUSIBILITY_FLAG = "plausibility_flag";

        // 02 section 14.3 (session, save health, cloud, clock, client performance)
        public const string SESSION_START = "session_start";
        public const string SESSION_END = "session_end";
        public const string CLOUD_SAVE_UPLOADED = "cloud_save_uploaded";
        public const string CLOUD_CONFLICT = "cloud_conflict";
        public const string CLOUD_AUTORESOLVED = "cloud_autoresolved";
        public const string SAVE_FALLBACK = "save_fallback";
        public const string SAVE_CORRUPT = "save_corrupt";
        public const string SAVE_CORRUPT_ALL = "save_corrupt_all";
        public const string SAVE_REPAIRED = "save_repaired";
        public const string SAVE_REJECT = "save_reject";
        public const string SAVE_WRITE_FAILED = "save_write_failed";
        public const string MIGRATION_APPLIED = "migration_applied";
        public const string CLOCK_ANOMALY = "clock_anomaly";
        public const string PLAYER_DELETED = "player_deleted";
        public const string PROFILE_CREATED = "profile_created";
        public const string BOOT_MS = "boot_ms";
        public const string RESUME_MS = "resume_ms";
        public const string PERF_TICK = "perf_tick";

        /// <summary>Every name above, in index order.</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            STAGE_CLEARED, BOSS_FAILED, WALL_BROKEN, TRAINING, LEVEL_UP, AWAKENED, RESEARCH_BOUGHT,
            SUMMON, SUMMON_RECONCILED, SUMMON_PENDING, SUMMON_RESOLVED, ITEM_ACQUIRED, ITEM_EQUIPPED, LIMIT_BREAK,
            FISH_RELEASE, FISH_MERGE, FISH_DISCOVERED, FISH_DISCARD, FISH_BOARD_SNAPSHOT,
            CURRENCY_CHANGED, QUEST_COMPLETED, QUEST_CLAIMED, ACHIEVEMENT_UNLOCKED, DUNGEON_RUN, OFFLINE_CATCHUP, OFFLINE_CLAIMED,
            DAY_ROLLOVER, DAILY_GIFT_CLAIMED, EXCHANGE, SHOP_VIEW, IAP_INITIATED, IAP_COMPLETED, IAP_FAILED, IAP_STATUS, INBOX_CLAIMED,
            PLAUSIBILITY_FLAG,
            SESSION_START, SESSION_END, CLOUD_SAVE_UPLOADED, CLOUD_CONFLICT, CLOUD_AUTORESOLVED, SAVE_FALLBACK, SAVE_CORRUPT,
            SAVE_CORRUPT_ALL, SAVE_REPAIRED, SAVE_REJECT, SAVE_WRITE_FAILED, MIGRATION_APPLIED, CLOCK_ANOMALY, PLAYER_DELETED,
            PROFILE_CREATED, BOOT_MS, RESUME_MS, PERF_TICK,
        };

        /// <summary>
        /// 02 section 14.1: economy events (a currency, purchase, summon, dungeon or inbox payload) also go to the backend
        /// <c>events</c> table. The remote uploader routes on this; Firebase gets every event.
        /// </summary>
        public static IReadOnlyCollection<string> Economy => _economy;

        static readonly HashSet<string> _economy = new HashSet<string>(StringComparer.Ordinal)
        {
            CURRENCY_CHANGED, QUEST_CLAIMED, DUNGEON_RUN, OFFLINE_CLAIMED, DAILY_GIFT_CLAIMED, EXCHANGE,
            IAP_INITIATED, IAP_COMPLETED, IAP_FAILED, IAP_STATUS, INBOX_CLAIMED,
            SUMMON, SUMMON_RECONCILED, ITEM_ACQUIRED, LIMIT_BREAK, AWAKENED,
        };

        static readonly HashSet<string> _known = new HashSet<string>(All, StringComparer.Ordinal);

        public static bool IsKnown(string name) => name != null && _known.Contains(name);

        public static bool IsEconomy(string name) => name != null && _economy.Contains(name);

        /// <summary>Event names: lowercase ASCII words joined by single underscores (02 section 14.1).</summary>
        public static bool IsSnakeCase(string name)
        {
            if (string.IsNullOrEmpty(name) || name[0] < 'a' || name[0] > 'z' || name[name.Length - 1] == '_') return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || (c == '_' && name[i - 1] != '_');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>Property names: camelCase ASCII, letters and digits only, lowercase first (02 section 14.1).</summary>
        public static bool IsCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || name[0] < 'a' || name[0] > 'z') return false;
            foreach (char c in name)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;
            return true;
        }
    }

    /// <summary>
    /// A typed analytics event: the name and the owner section's camelCase fields, so an emitter cannot misspell either.
    /// Track it with <see cref="AnalyticsExtensions.Track(IAnalytics, AnalyticsEvent)"/>; the sink adds the envelope.
    /// </summary>
    public abstract record AnalyticsEvent
    {
        public abstract string Name { get; }

        /// <summary>The owner section's fields. Never an envelope field (02 section 14.1: owners do not repeat them).</summary>
        public abstract Dictionary<string, object> Props();
    }

    public static class AnalyticsExtensions
    {
        public static void Track(this IAnalytics analytics, AnalyticsEvent e)
        {
            if (analytics == null || e == null) return;
            analytics.Track(e.Name, e.Props());
        }
    }

    /// <summary>
    /// The event types this layer owns (issue #155): 06 section 13's <c>currency_changed</c> and 02 section 14.3's session,
    /// save-health, cloud and clock events. Feature emitters (#163, #168, #177) add their owners' events next to these.
    /// </summary>
    public static partial class AnalyticsEvents
    {
        /// <summary>06 section 13. RefId: the item, quest, offer or banner id when the apply has one, else omitted.</summary>
        public sealed record CurrencyChanged(Currency Currency, double Delta, double BalanceAfter, CurrencyReason Reason,
                                             string RefId = null) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.CURRENCY_CHANGED;

            public override Dictionary<string, object> Props()
            {
                var p = new Dictionary<string, object>
                {
                    ["currency"] = Currency.ToString(), ["delta"] = Delta, ["balanceAfter"] = BalanceAfter, ["reason"] = Reason.ToString(),
                };
                if (RefId != null) p["refId"] = RefId;
                return p;
            }
        }

        /// <summary>02 section 14.3: Runtime at SCR_MAIN shown.</summary>
        public sealed record SessionStart(long OfflineElapsedSec, long OfflineCredited, bool ColdBoot, double BootMs) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.SESSION_START;

            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["offlineElapsedSec"] = OfflineElapsedSec, ["offlineCredited"] = OfflineCredited, ["coldBoot"] = ColdBoot, ["bootMs"] = BootMs,
            };
        }

        /// <summary>02 section 14.3: SessionRunner on pause, quit or an in-app gap (issue #274). Trigger: the constants below.</summary>
        public sealed record SessionEnd(long DurationSec, int StageStart, int StageEnd, long Kills, long Ticks, string Trigger) : AnalyticsEvent
        {
            public const string PAUSE = "pause";
            public const string QUIT = "quit";
            public const string GAP = "gap";

            public override string Name => AnalyticsNames.SESSION_END;

            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["durationSec"] = DurationSec, ["stageStart"] = StageStart, ["stageEnd"] = StageEnd, ["kills"] = Kills,
                ["ticks"] = Ticks, ["trigger"] = Trigger,
            };
        }

        /// <summary>02 section 14.3: boot created a new profile (the funnel step; the RngRoot seed is never sent, issue #261).</summary>
        public sealed record ProfileCreated : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.PROFILE_CREATED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>();
        }

        /// <summary>02 section 14.3 / 10 section 5: one per cold boot, the total and each 01 section 8.1 step's milliseconds.</summary>
        public sealed record BootMs(double TotalMs, double ContentMs, double ConfigMs, double AuthMs, double SaveMs, double CatchUpMs,
                                    double HandoffMs) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.BOOT_MS;

            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["totalMs"] = TotalMs, ["contentMs"] = ContentMs, ["configMs"] = ConfigMs, ["authMs"] = AuthMs, ["saveMs"] = SaveMs,
                ["catchUpMs"] = CatchUpMs, ["handoffMs"] = HandoffMs,
            };
        }

        /// <summary>02 section 14.3 / 10 section 5: warm resume to first frame. TODO(#218): emitted by the resume path.</summary>
        public sealed record ResumeMs(double Ms) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.RESUME_MS;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["ms"] = Ms };
        }

        /// <summary>02 section 14.3 / 10 section 5: tick cost sampled over a session, not per tick. TODO(#218): emitted by the runner.</summary>
        public sealed record PerfTick(double P50Ms, double P95Ms, double MaxMs, long Ticks) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.PERF_TICK;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["p50Ms"] = P50Ms, ["p95Ms"] = P95Ms, ["maxMs"] = MaxMs, ["ticks"] = Ticks,
            };
        }

        /// <summary>02 section 14.3.</summary>
        public sealed record CloudSaveUploaded(long SaveSeq, long SizeBytes, string Trigger, double LatencyMs, IReadOnlyList<string> Flags)
            : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.CLOUD_SAVE_UPLOADED;

            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["saveSeq"] = SaveSeq, ["sizeBytes"] = SizeBytes, ["trigger"] = Trigger, ["latencyMs"] = LatencyMs,
                ["flags"] = Flags ?? Array.Empty<string>(),
            };
        }

        /// <summary>02 section 9.4: the conflict modal's outcome.</summary>
        public sealed record CloudConflict(string WinnerSide, string ChosenSide, double ScoreL, double ScoreR, long SecondsApart) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.CLOUD_CONFLICT;

            public override Dictionary<string, object> Props() => new Dictionary<string, object>
            {
                ["winnerSide"] = WinnerSide, ["chosenSide"] = ChosenSide, ["scoreL"] = ScoreL, ["scoreR"] = ScoreR,
                ["secondsApart"] = SecondsApart,
            };
        }

        /// <summary>02 section 9.3: a conflict resolved without asking.</summary>
        public sealed record CloudAutoresolved(string WinnerSide) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.CLOUD_AUTORESOLVED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["winnerSide"] = WinnerSide };
        }

        /// <summary>02 section 8.3: the load fell back to <c>slot0.prev.sav</c>.</summary>
        public sealed record SaveFallback : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.SAVE_FALLBACK;
            public override Dictionary<string, object> Props() => new Dictionary<string, object>();
        }

        /// <summary>02 section 8.3: an existing save copy was unreadable and quarantined. Problems: the reasons, joined.</summary>
        public sealed record SaveCorrupt(string Problems) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.SAVE_CORRUPT;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["problems"] = Problems ?? "" };
        }

        /// <summary>02 section 8.3: a new profile replaced an unreadable save (issue #248).</summary>
        public sealed record SaveCorruptAll(string SupportId) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.SAVE_CORRUPT_ALL;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["supportId"] = SupportId };
        }

        /// <summary>02 section 6.4: one per repair rule id that fired.</summary>
        public sealed record SaveRepaired(string Rule) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.SAVE_REPAIRED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["rule"] = Rule };
        }

        /// <summary>02 section 8.3: a copy failed to decode or validate.</summary>
        public sealed record SaveReject(string File, string Reason) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.SAVE_REJECT;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["file"] = File, ["reason"] = Reason };
        }

        /// <summary>02 section 8.2: a write failed; Consecutive counts this run of failures.</summary>
        public sealed record SaveWriteFailed(int Consecutive) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.SAVE_WRITE_FAILED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["consecutive"] = Consecutive };
        }

        /// <summary>02 section 7: one per migration step.</summary>
        public sealed record MigrationApplied(int From, int To) : AnalyticsEvent
        {
            public override string Name => AnalyticsNames.MIGRATION_APPLIED;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["from"] = From, ["to"] = To };
        }

        /// <summary>02 section 11.3. Kind: backwards, gap or resync (constants below); Seconds is signed.</summary>
        public sealed record ClockAnomaly(string Kind, long Seconds) : AnalyticsEvent
        {
            public const string BACKWARDS = "backwards";
            public const string GAP = "gap";
            public const string RESYNC = "resync";

            public override string Name => AnalyticsNames.CLOCK_ANOMALY;
            public override Dictionary<string, object> Props() => new Dictionary<string, object> { ["kind"] = Kind, ["seconds"] = Seconds };
        }
    }
}
