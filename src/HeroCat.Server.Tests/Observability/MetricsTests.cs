using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using HeroCat.Server.Features.Summon;
using HeroCat.Server.Infrastructure;
using HeroCat.Server.Infrastructure.Redis;
using HeroCat.Server.Observability;
using HeroCat.Server.Worker;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Observability;

/// <summary>The specs/08 section 9.4 metrics: the catalog is the spec table, every metric is registered, and every one is scraped.</summary>
[TestFixture]
public sealed class MetricsTests
{
    sealed record SpecMetric(string Name, string[] Labels, string Type, bool Derived);

    static List<SpecMetric> SpecTable()
    {
        string spec = File.ReadAllText(RepoPaths.Of("specs/08-backend-and-live-ops.md"));
        int start = spec.IndexOf("### 9.4 Logging and metrics", StringComparison.Ordinal);
        int end = spec.IndexOf("### 9.5", start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThan(0));
        var rows = spec[start..end].Split('\n')
            .Where(l => l.StartsWith("| `", StringComparison.Ordinal))
            .Select(l => l.Split('|', StringSplitOptions.TrimEntries));
        var list = new List<SpecMetric>();
        foreach (var cells in rows)
        {
            var names = Regex.Matches(cells[1], @"`([a-z0-9_]+)(?:\{([^}]*)\})?`");
            string[] types = Regex.Replace(cells[2], @"\(([^)]*)\)", m => m.Groups[1].Value.Contains("derived") ? "derived" : "")
                                  .Split(',', StringSplitOptions.TrimEntries);
            for (int i = 0; i < names.Count; i++)
            {
                string type = types.Length == names.Count ? types[i] : types[0];
                bool derived = type.Contains("derived");
                string[] labels = names[i].Groups[2].Success ? names[i].Groups[2].Value.Split(',', StringSplitOptions.TrimEntries) : Array.Empty<string>();
                list.Add(new SpecMetric(names[i].Groups[1].Value, labels, type.Split(' ')[0], derived));
            }
        }
        return list;
    }

    [Test]
    public void The_catalog_is_the_spec_table()
    {
        var spec = SpecTable();
        Assert.That(spec, Has.Count.GreaterThanOrEqualTo(18), "parsed the 9.4 table");
        Assert.That(MetricCatalog.All.Select(m => m.Name), Is.EquivalentTo(spec.Select(m => m.Name)), "metric names");
        foreach (var s in spec)
        {
            var def = MetricCatalog.All.Single(m => m.Name == s.Name);
            Assert.That(def.Kind.ToString().ToLowerInvariant(), Is.EqualTo(s.Type), s.Name + " type");
            Assert.That(def.Labels, Is.EqualTo(s.Labels), s.Name + " labels");
            Assert.That(def.Source == MetricSource.Derived, Is.EqualTo(s.Derived), s.Name + " derived");
        }
    }

    [Test]
    public void Every_metric_the_server_produces_is_a_registered_instrument_of_its_kind()
    {
        MetricCatalog.EnsureRegistered();
        var found = new Dictionary<string, Instrument>();
        using (var listener = new MeterListener())
        {
            listener.InstrumentPublished = (i, _) =>
            {
                if (i.Meter.Name == ServerMetrics.MeterName) found[i.Name] = i;
            };
            listener.Start();
        }
        foreach (var def in MetricCatalog.All.Where(d => d.Source != MetricSource.Derived))
        {
            Assert.That(found.ContainsKey(def.Name), def.Name + " is not an instrument on " + ServerMetrics.MeterName);
            string type = found[def.Name].GetType().Name;
            string expected = def.Kind switch
            {
                MetricKind.Counter => "Counter`1",
                MetricKind.Histogram => "Histogram`1",
                _ => "ObservableGauge`1",
            };
            Assert.That(type, Is.EqualTo(expected), def.Name);
        }
        Assert.That(MetricCatalog.All.Where(d => d.Source == MetricSource.NoEmitterYet).Select(d => d.Note), Has.All.Not.Empty,
                    "a metric with no emitter says which issue brings one");
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Test]
    public async Task Every_metric_is_exported_on_the_loopback_scrape_endpoint()
    {
        int port = FreePort();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("HeroCat:Telemetry:MetricsPort", port.ToString()));
        using (var api = factory.CreateClient())
        using (var r = await api.GetAsync("/health"))
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // One value through each emitter the request did not reach (the features without a database here, and those
        // with no emitter yet), so every name has a point to export.
        SummonMetrics.RecordPull(Banner.WEAPON, Rarity.LEGENDARY);
        IapMetrics.RecordValidate("apple", "OK");
        IapMetrics.RecordWebhook("google", "REFUND");
        SaveMetrics.RecordUpload(30_000);
        SaveMetrics.RecordFlag("STAGE_VS_TIME", "certain");
        ModerationMetrics.RecordAction("rollback");
        IdempotencyMetrics.RecordReplay("post/summon/pull");
        DbMetrics.RecordQuery("summon.lock_pity", TimeSpan.FromMilliseconds(3));
        LockMetrics.RecordWait("summon", "acquired", TimeSpan.FromMilliseconds(2));
        WorkerJobMetrics.Record("inbox_purge", TimeSpan.FromSeconds(1), failed: true);
        PartitionMaintenanceService.RecordHorizon("events", DateTimeOffset.UtcNow.AddDays(14));
        ConfigMetrics.Observe(42, "2026.11.03");

        using var scraper = new HttpClient(new HttpClientHandler { UseProxy = false });
        string text = await scraper.GetStringAsync(TelemetrySetup.MetricsPrefix(port) + "metrics");

        foreach (var def in MetricCatalog.All.Where(d => d.Source != MetricSource.Derived))
        {
            string type = def.Kind.ToString().ToLowerInvariant();
            Assert.That(text, Does.Contain($"# TYPE {def.Name} {type}\n"), $"{def.Name} ({type}) is not in the scrape:\n{text}");
        }
        Assert.That(text, Does.Match(@"http_requests_total\{[^}]*route=""get/health""[^}]*status=""200""[^}]*\} [1-9]"));
        Assert.That(text, Does.Contain("http_request_duration_seconds_bucket{"));
        Assert.That(text, Does.Contain("le=\"0.5\""), "the seconds buckets carry the 500 ms alarm line");
        Assert.That(text, Does.Match(@"save_upload_bytes_bucket\{[^}]*le=""32768""\}"));
        Assert.That(text, Does.Match(@"content_active_version_info\{[^}]*version=""2026.11.03""[^}]*\} 1"));
        Assert.That(text, Does.Match(@"config_active_version(\{[^}]*\})? 42"));
    }

    [Test]
    public void The_metrics_listener_is_off_in_Development_and_on_the_loopback_interface_otherwise()
    {
        string dev = File.ReadAllText(RepoPaths.Of("src/HeroCat.Server/appsettings.Development.json"));
        Assert.That(dev, Does.Match(@"""MetricsPort"":\s*0"));
        Assert.That(TelemetrySetup.MetricsPrefix(9464), Is.EqualTo("http://127.0.0.1:9464/"));
        Assert.That(new TelemetryOptions().MetricsPort, Is.EqualTo(9464));
    }

    [Test]
    public async Task A_worker_job_pass_records_its_duration_and_failures()
    {
        var points = new List<(string name, string job)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == ServerMetrics.MeterName && i.Name.StartsWith("worker_job_", StringComparison.Ordinal)) l.EnableMeasurementEvents(i);
        };
        void OnPoint<T>(Instrument i, T _, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? __)
        {
            foreach (var t in tags)
                if (t.Key == "job" && (string?)t.Value == "test_job") lock (points) points.Add((i.Name, "test_job"));
        }
        listener.SetMeasurementEventCallback<double>(OnPoint);
        listener.SetMeasurementEventCallback<long>(OnPoint);
        listener.Start();

        Assert.That(await WorkerJobMetrics.TimeAsync("test_job", _ => Task.FromResult(7), CancellationToken.None), Is.EqualTo(7));
        Assert.ThrowsAsync<InvalidOperationException>(() => WorkerJobMetrics.TimeAsync<int>("test_job", _ => throw new InvalidOperationException(), CancellationToken.None));
        Assert.ThrowsAsync<OperationCanceledException>(() => WorkerJobMetrics.TimeAsync<int>("test_job", _ => throw new OperationCanceledException(), CancellationToken.None));

        Assert.That(points.Count(p => p.name == WorkerJobMetrics.DurationSeconds), Is.EqualTo(3));
        Assert.That(points.Count(p => p.name == WorkerJobMetrics.FailuresTotal), Is.EqualTo(1), "a cancelled pass is not a failure");
    }

    [Test]
    public void The_pool_gauge_counts_a_connection_until_it_closes()
    {
        long before = DbMetrics.InUseNow;
        var c = new FakeConnection();
        c.Open();
        DbMetrics.Track(c);
        Assert.That(DbMetrics.InUseNow, Is.EqualTo(before + 1));
        c.Close();
        c.Close();
        Assert.That(DbMetrics.InUseNow, Is.EqualTo(before));
    }

    sealed class FakeConnection : System.Data.Common.DbConnection
    {
        System.Data.ConnectionState _state = System.Data.ConnectionState.Closed;
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "16";
        public override System.Data.ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) { }
        public override void Open() => Set(System.Data.ConnectionState.Open);
        public override void Close() => Set(System.Data.ConnectionState.Closed);
        void Set(System.Data.ConnectionState s)
        {
            var old = _state;
            _state = s;
            if (old != s) OnStateChange(new System.Data.StateChangeEventArgs(old, s));
        }
        protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override System.Data.Common.DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
