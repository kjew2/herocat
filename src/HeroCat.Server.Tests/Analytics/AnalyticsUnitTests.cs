using System.Text.Json;
using HeroCat.Runtime.Services;
using HeroCat.Server.Features.Analytics;
using HeroCat.Server.Features.Summon;
using HeroCat.Server.Hosting;
using FakeClock = HeroCat.Server.Tests.Pipeline.FakeClock;
using HeroCat.Server.Worker;
using Microsoft.Extensions.Options;
using HeroCat.Sim.Economy;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Analytics;

/// <summary>
/// The analytics ingest's rules without a database (specs/08 section 3.5, specs/02 section 14; issue #209): the server's
/// mirrors of the client's economy names and player hash, and the per-event checks.
/// </summary>
[TestFixture]
public sealed class AnalyticsUnitTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_793_670_900);
    static readonly Guid Player = Guid.Parse("8d3c7f0e-2b1a-4c5d-9e8f-0a1b2c3d4e5f");

    static IngestContext Ctx(short ageGate = 0) =>
        new(AnalyticsEventRules.HashPlayerId(Player, "salt"), ageGate, "test", Now, TimeSpan.FromDays(30), TimeSpan.FromSeconds(120), 4096);

    static JsonElement Event(string name = "currency_changed", object? props = null, long? ts = null, string? eventId = null, Dictionary<string, object?>? extra = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["name"] = name, ["eventId"] = eventId ?? Guid.NewGuid().ToString("N"), ["ts"] = ts ?? Now.ToUnixTimeSeconds(),
            ["sessionId"] = Guid.NewGuid().ToString("N"), ["playerIdHash"] = "ffff", ["deviceId"] = Guid.NewGuid().ToString("N"),
            ["clientVersion"] = "1.0.0", ["contentVersion"] = "2026.11.03", ["schemaVersion"] = 1, ["debugTainted"] = false,
            ["clockAuthoritative"] = true, ["dayIndex"] = 3, ["stage"] = 41, ["ageGate"] = 0,
            ["props"] = props ?? new { currency = "DIAMOND", delta = 50, balanceAfter = 640, reason = "QUEST" },
        };
        if (extra != null) foreach (var (k, v) in extra) d[k] = v;
        return JsonSerializer.SerializeToElement(d);
    }

    [Test]
    public void Economy_names_mirror_the_clients()
    {
        Assert.That(AnalyticsEventRules.Economy, Is.EquivalentTo(AnalyticsNames.Economy));
    }

    [Test]
    public void Envelope_fields_mirror_the_clients()
    {
        Assert.That(AnalyticsEventRules.EnvelopeFields, Is.EquivalentTo(AnalyticsEnvelope.Fields));
    }

    [Test]
    public void Player_hash_equals_the_clients_playerIdHash()
    {
        string hex = Convert.ToHexString(AnalyticsEventRules.HashPlayerId(Player, "env-salt")).ToLowerInvariant();
        Assert.That(hex, Is.EqualTo(AnalyticsEnvelope.HashPlayerId(Player, "env-salt")));
    }

    [Test]
    public void Reasons_are_exactly_the_closed_enum()
    {
        Assert.That(AnalyticsEventRules.Reasons, Is.EquivalentTo(Enum.GetNames<CurrencyReason>()));
        Assert.That(AnalyticsEventRules.IsReason("QUEST"), Is.True);
        foreach (string bad in new[] { "quest", "3", "KILL, BOSS", "GIFT", "" })
            Assert.That(AnalyticsEventRules.IsReason(bad), Is.False, bad);
    }

    [Test]
    public void A_valid_event_becomes_a_row_with_server_stamped_identity()
    {
        var p = AnalyticsEventParser.Parse(Event(), Ctx());
        Assert.That(p.Code, Is.Null);
        var r = p.Row!;
        Assert.That(r.Name, Is.EqualTo("currency_changed"));
        Assert.That(r.Ts, Is.EqualTo(Now));
        Assert.That(r.PlayerIdHash, Is.EqualTo(AnalyticsEventRules.HashPlayerId(Player, "salt")), "the client's playerIdHash is ignored");
        Assert.That(r.Source, Is.EqualTo("client"));
        Assert.That(r.DayIndex, Is.EqualTo(3));
        Assert.That(r.Stage, Is.EqualTo(41));
        Assert.That(r.SchemaVersion, Is.EqualTo(1));
        Assert.That(r.ClockAuthoritative, Is.True);
        Assert.That(JsonDocument.Parse(r.PropsJson).RootElement.GetProperty("reason").GetString(), Is.EqualTo("QUEST"));
    }

    [Test]
    public void Age_gate_comes_from_the_token_and_advertising_ids_are_dropped()
    {
        var e = Event(props: new { currency = "DIAMOND", delta = 50, balanceAfter = 640, reason = "IAP", advertisingId = "abc", idfa = "x" },
                      extra: new() { ["ageGate"] = 0 });
        var r = AnalyticsEventParser.Parse(e, Ctx(ageGate: 1)).Row!;
        Assert.That(r.AgeGate, Is.EqualTo(1));
        var props = JsonDocument.Parse(r.PropsJson).RootElement.EnumerateObject().Select(x => x.Name);
        Assert.That(props, Is.EquivalentTo(new[] { "currency", "delta", "balanceAfter", "reason" }));
        Assert.That(AnalyticsEventRules.EnvelopeAgeGate(0), Is.EqualTo(0));
        Assert.That(AnalyticsEventRules.EnvelopeAgeGate(1), Is.EqualTo(1));
        Assert.That(AnalyticsEventRules.EnvelopeAgeGate(2), Is.EqualTo(0), "an adult account is ageGate 0 in the envelope");
    }

    [Test]
    public void Refusals_have_their_codes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Code(Event(props: new { currency = "DIAMOND", delta = 5, reason = "FREE_STUFF" })), Is.EqualTo(AnalyticsEventCodes.EVENT_REASON_INVALID));
            Assert.That(Code(Event(props: new { currency = "DIAMOND", delta = 5 })), Is.EqualTo(AnalyticsEventCodes.EVENT_REASON_INVALID), "currency_changed needs a reason");
            Assert.That(Code(Event(props: new { currency = "DIAMOND", delta = 5, reason = 3 })), Is.EqualTo(AnalyticsEventCodes.EVENT_REASON_INVALID));
            Assert.That(Code(Event(name: "summon", props: new { bannerId = "b", reason = "nope" })), Is.EqualTo(AnalyticsEventCodes.EVENT_REASON_INVALID));
            Assert.That(Code(Event(name: "summon", props: new { bannerId = "b" })), Is.Null, "only currency_changed requires a reason");
            Assert.That(Code(Event(name: "session_start")), Is.EqualTo(AnalyticsEventCodes.EVENT_NAME_INVALID), "not an economy event");
            Assert.That(Code(Event(name: "Currency_Changed")), Is.EqualTo(AnalyticsEventCodes.EVENT_NAME_INVALID));
            Assert.That(Code(Event(eventId: "not-hex")), Is.EqualTo(AnalyticsEventCodes.EVENT_MALFORMED));
            Assert.That(Code(Event(props: new { currency = "DIAMOND", reason = "QUEST", stage = 4 })), Is.EqualTo(AnalyticsEventCodes.EVENT_MALFORMED), "an envelope name");
            Assert.That(Code(Event(props: new { currency = "DIAMOND", reason = "QUEST", playerId = "x" })), Is.EqualTo(AnalyticsEventCodes.EVENT_MALFORMED));
            Assert.That(Code(Event(props: new Dictionary<string, object> { ["reason"] = "QUEST", ["Bad_Key"] = 1 })), Is.EqualTo(AnalyticsEventCodes.EVENT_MALFORMED));
            Assert.That(Code(Event(extra: new() { ["ts"] = "soon" })), Is.EqualTo(AnalyticsEventCodes.EVENT_MALFORMED));
            Assert.That(Code(Event(extra: new() { ["stage"] = "41" })), Is.EqualTo(AnalyticsEventCodes.EVENT_MALFORMED));
            Assert.That(Code(Event(ts: Now.AddDays(-31).ToUnixTimeSeconds())), Is.EqualTo(AnalyticsEventCodes.EVENT_TS_OUT_OF_RANGE));
            Assert.That(Code(Event(ts: Now.AddSeconds(121).ToUnixTimeSeconds())), Is.EqualTo(AnalyticsEventCodes.EVENT_TS_OUT_OF_RANGE));
            Assert.That(Code(Event(ts: Now.AddSeconds(120).ToUnixTimeSeconds())), Is.Null);
            Assert.That(Code(Event(props: new { currency = "DIAMOND", reason = "QUEST", refId = new string('x', 5000) })), Is.EqualTo(AnalyticsEventCodes.EVENT_TOO_LARGE));
            Assert.That(AnalyticsEventParser.Parse(JsonSerializer.SerializeToElement(5), Ctx()).Code, Is.EqualTo(AnalyticsEventCodes.EVENT_MALFORMED));
        });
    }

    static string? Code(JsonElement e) => AnalyticsEventParser.Parse(e, Ctx()).Code;

    [Test]
    public void Partition_names_follow_0003()
    {
        Assert.That(AnalyticsEventRules.PartitionOf(DateTimeOffset.Parse("2026-11-02T23:59:59+00:00")), Is.EqualTo("events_p20261102"));
        Assert.That(AnalyticsEventRules.PartitionOf(DateTimeOffset.Parse("2026-11-02T23:30:00-01:00")), Is.EqualTo("events_p20261103"), "UTC days");
        var month = new PartitionedTable("summon_log", "month");
        Assert.That(PartitionMaintenanceService.PartitionEnd(month, "summon_log_p202612"), Is.EqualTo(DateTimeOffset.Parse("2027-01-01T00:00:00+00:00")));
        var day = new PartitionedTable("events", "day");
        Assert.That(PartitionMaintenanceService.PartitionEnd(day, "events_p20261231"), Is.EqualTo(DateTimeOffset.Parse("2027-01-01T00:00:00+00:00")));
        Assert.That(PartitionMaintenanceService.PartitionEnd(day, "events_default"), Is.Null);
    }

    // ---- server-written rows' day_index (issue #305) ----

    static readonly Lazy<IContentSets> Content = new(() => new DirectoryContentSets(Options.Create(new ContentSetOptions())));

    static ServerEconomyEvents ServerEvents(DateTimeOffset now) =>
        new(Options.Create(new ServerOptions { Env = "test", Analytics = { PlayerIdSalt = "salt" } }), new FakeClock(now), Content.Value);

    [Test]
    public void Server_day_index_counts_whole_days_since_the_account_was_created()
    {
        const int day = 86400;
        var created = DateTimeOffset.FromUnixTimeSeconds(1_793_600_000);
        Assert.That(ServerEconomyEvents.DayIndex(created, created, day), Is.Zero);
        Assert.That(ServerEconomyEvents.DayIndex(created.AddSeconds(day - 1), created, day), Is.Zero);
        Assert.That(ServerEconomyEvents.DayIndex(created.AddSeconds(day), created, day), Is.EqualTo(1));
        Assert.That(ServerEconomyEvents.DayIndex(created.AddDays(30).AddHours(5), created, day), Is.EqualTo(30));
        // Floored like the client's envelope: a ledger clock a second behind the account's created_at is day -1, not 0.
        Assert.That(ServerEconomyEvents.DayIndex(created.AddSeconds(-1), created, day), Is.EqualTo(-1));
    }

    [Test]
    public void Server_currency_changed_carries_the_account_day_index_and_age_gate()
    {
        var events = ServerEvents(Now);
        Assert.That(Content.Value.Active.Constants.DaySeconds, Is.EqualTo(86400), "DAY_SECONDS (00 section 4)");

        var row = events.CurrencyChanged(Player, new EconomyAccount(1, Now.AddDays(-7).AddMinutes(-1)), "DIAMOND", 500, 900, "IAP", "sku");
        Assert.That(row.DayIndex, Is.EqualTo(7));
        Assert.That(row.AgeGate, Is.EqualTo(1));
        Assert.That(row.Source, Is.EqualTo(AnalyticsEventRules.SourceServer));
        Assert.That(row.Ts, Is.EqualTo(Now));
        Assert.That(row.SessionId, Is.Null);
        Assert.That(row.Stage, Is.Null);

        var fresh = events.Row("iap_status", Player, new EconomyAccount(2, Now.AddHours(-23)), new Dictionary<string, object?> { ["status"] = "granted" });
        Assert.That(fresh.DayIndex, Is.Zero);

        var orphan = events.CurrencyChanged(Player, EconomyAccount.Unknown, "DIAMOND", 1, 1, "MAIL", null);
        Assert.That(orphan.DayIndex, Is.Null, "no account row, no day");
        Assert.That(orphan.AgeGate, Is.Zero);
    }
}
