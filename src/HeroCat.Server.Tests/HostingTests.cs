using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Blobs;
using HeroCat.Server.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace HeroCat.Server.Tests;

[TestFixture]
public sealed class ServerRoleTests
{
    [Test]
    public void No_flag_is_the_api_and_other_arguments_pass_through()
    {
        var (role, rest) = ServerRoles.Parse(new[] { "--HeroCat:Version=1" });
        Assert.That(role, Is.EqualTo(ServerRole.Api));
        Assert.That(rest, Is.EqualTo(new[] { "--HeroCat:Version=1" }));
        Assert.That(ServerRoles.Parse(new[] { "api" }).role, Is.EqualTo(ServerRole.Api));
    }

    [Test]
    public void Worker_and_migrate_flags_select_their_role()
    {
        Assert.That(ServerRoles.Parse(new[] { "--worker" }), Is.EqualTo((ServerRole.Worker, Array.Empty<string>())).Using(new RoleComparer()));
        var (role, rest) = ServerRoles.Parse(new[] { "--environment", "Production", "--migrate" });
        Assert.That(role, Is.EqualTo(ServerRole.Migrate));
        Assert.That(rest, Is.EqualTo(new[] { "--environment", "Production" }));
    }

    [Test]
    public void Two_roles_are_an_error() =>
        Assert.Throws<ArgumentException>(() => ServerRoles.Parse(new[] { "--worker", "--migrate" }));

    [Test]
    public async Task Bad_role_arguments_exit_2_with_a_message()
    {
        var stderr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { Assert.That(await ServerHost.RunAsync(new[] { "--worker", "api" }), Is.EqualTo(2)); }
        finally { Console.SetError(stderr); }
        Assert.That(captured.ToString(), Does.Contain("at most one role"));
    }

    sealed class RoleComparer : IEqualityComparer<(ServerRole, string[])>
    {
        public bool Equals((ServerRole, string[]) a, (ServerRole, string[]) b) => a.Item1 == b.Item1 && a.Item2.SequenceEqual(b.Item2);
        public int GetHashCode((ServerRole, string[]) o) => o.Item1.GetHashCode();
    }
}

[TestFixture]
public sealed class WorkerHostTests
{
    [Test]
    public async Task Worker_role_starts_and_stops_without_http_or_dependencies()
    {
        using var host = ServerHost.BuildWorker(new[] { "--Logging:LogLevel:Default=Warning" });
        Assert.That(host.Services.GetRequiredService<ServerRoleInfo>().Name, Is.EqualTo("worker"));
        Assert.That(host.Services.GetServices<IHostedService>().OfType<WorkerService>().Count(), Is.EqualTo(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(cts.Token);
        await host.StopAsync(cts.Token);
    }
}

[TestFixture]
public sealed class BlobStoreTests
{
    static IBlobStore Resolve(string provider, string serviceUrl = "")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HeroCat:Blobs:Provider"] = provider,
            ["HeroCat:Blobs:ServiceUrl"] = serviceUrl,
        }).Build();
        var services = new ServiceCollection();
        ServerHost.AddCore(services, config, ServerRole.Api);
        return services.BuildServiceProvider().GetRequiredService<IBlobStore>();
    }

    [Test]
    public void Provider_selects_the_implementation()
    {
        Assert.That(Resolve("memory"), Is.InstanceOf<InMemoryBlobStore>());
        Assert.That(Resolve("s3"), Is.InstanceOf<S3BlobStore>());
        Assert.Throws<InvalidOperationException>(() => Resolve("gcs"));
    }

    [Test]
    public void A_service_url_points_the_s3_client_at_minio()
    {
        var c = S3BlobStore.Config(new BlobOptions { ServiceUrl = "http://localhost:9000", ForcePathStyle = true, Region = "us-east-1" });
        Assert.That(c.ServiceURL, Does.StartWith("http://localhost:9000"));
        Assert.That(c.ForcePathStyle, Is.True);
        var aws = S3BlobStore.Config(new BlobOptions { Region = "eu-west-1" });
        Assert.That(aws.RegionEndpoint?.SystemName, Is.EqualTo("eu-west-1"));
    }

    [Test]
    public async Task Memory_store_round_trips_and_copies()
    {
        var s = new InMemoryBlobStore();
        var body = new byte[] { 1, 2, 3 };
        await s.PutAsync("herocat-saves-local", "p/1", body, "application/octet-stream");
        body[0] = 9;
        var got = await s.GetAsync("herocat-saves-local", "p/1");
        Assert.That(got, Is.EqualTo(new byte[] { 1, 2, 3 }));
        got![1] = 9;
        Assert.That(await s.GetAsync("herocat-saves-local", "p/1"), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(await s.GetAsync("herocat-receipts-local", "p/1"), Is.Null);
        await s.DeleteAsync("herocat-saves-local", "p/1");
        Assert.That(await s.GetAsync("herocat-saves-local", "p/1"), Is.Null);
    }
}

[TestFixture]
public sealed class DependencyConfigTests
{
    [Test]
    public void Unconfigured_postgres_and_redis_fail_on_first_use_with_the_config_key()
    {
        var services = new ServiceCollection();
        ServerHost.AddCore(services, new ConfigurationBuilder().Build(), ServerRole.Api);
        using var sp = services.BuildServiceProvider();
        var db = sp.GetRequiredService<HeroCat.Server.Infrastructure.Db.IDbConnectionFactory>();
        var e = Assert.ThrowsAsync<InvalidOperationException>(async () => await db.OpenAsync());
        Assert.That(e!.Message, Does.Contain("HeroCat:Database:ConnectionString"));
        var redis = sp.GetRequiredService<HeroCat.Server.Infrastructure.Redis.IRedisConnection>();
        var r = Assert.ThrowsAsync<InvalidOperationException>(() => redis.DatabaseAsync());
        Assert.That(r!.Message, Does.Contain("HeroCat:Redis:Configuration"));
        Assert.That(sp.GetRequiredService<IOptions<ServerOptions>>().Value.Version, Is.EqualTo("dev"));
    }
}
