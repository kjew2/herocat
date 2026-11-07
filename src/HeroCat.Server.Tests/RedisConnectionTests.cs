using System.Diagnostics;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Redis;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using StackExchange.Redis;

namespace HeroCat.Server.Tests;

/// <summary>A first connect while Redis is down must not poison the connection for good (issue #334, specs/08 section 9.1).</summary>
[TestFixture]
public sealed class RedisConnectionTests
{
    const string Unreachable = "127.0.0.1:1,connectTimeout=200,abortConnect=true";

    [Test]
    public void The_options_never_abort_on_connect_fail_and_fail_fast()
    {
        var options = RedisConnection.Options(Unreachable);
        Assert.That(options.AbortOnConnectFail, Is.False, "overrides abortConnect=true from configuration");
        Assert.That(options.BacklogPolicy, Is.SameAs(BacklogPolicy.FailFast));
        Assert.That(options.ConnectTimeout, Is.EqualTo(200), "the rest of the configuration is kept");
    }

    [Test]
    public async Task A_failed_first_connect_does_not_cache_a_faulted_task()
    {
        await using var redis = new RedisConnection(Options.Create(new ServerOptions { Redis = { Configuration = Unreachable } }));
        var watch = Stopwatch.StartNew();
        IDatabase first = await redis.DatabaseAsync();
        IDatabase second = await redis.DatabaseAsync();
        Assert.That(second.Multiplexer, Is.SameAs(first.Multiplexer), "one multiplexer, reconnecting in the background");
        Assert.That(first.Multiplexer.IsConnected, Is.False);
        Assert.ThrowsAsync<RedisConnectionException>(() => second.PingAsync(), "a command fails at once while Redis is down");
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "neither the connect nor the command waits out a long timeout");
    }
}
