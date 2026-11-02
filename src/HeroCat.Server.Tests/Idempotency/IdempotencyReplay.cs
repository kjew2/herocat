using System.Net;
using HeroCat.Server.Pipeline;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Idempotency;

/// <summary>
/// specs/10 section 1.3 "Idempotency replay", the shared half every idempotent endpoint's tests call (summon, inbox claim,
/// save PUT, dungeon seed, guest; issue #194): the same <c>X-Request-Id</c> twice returns a byte-identical response with
/// <c>X-Idempotent-Replay: true</c> and performs no second write.
/// </summary>
static class IdempotencyReplay
{
    /// <param name="request">Builds the request; called twice with the same <paramref name="requestId"/>.</param>
    /// <param name="writes">Counts the endpoint's writes (rows, calls) so the test sees there was no second one.</param>
    /// <returns>The first response's body, for endpoint-specific assertions.</returns>
    public static async Task<byte[]> AssertReplayAsync(HttpClient client, Func<string, HttpRequestMessage> request, Func<Task<long>> writes,
                                                      string? requestId = null)
    {
        requestId ??= Guid.NewGuid().ToString("N");
        long before = await writes();

        using var first = await client.SendAsync(request(requestId));
        byte[] firstBody = await first.Content.ReadAsByteArrayAsync();
        Assert.That((int)first.StatusCode, Is.LessThan(500), System.Text.Encoding.UTF8.GetString(firstBody));
        Assert.That(first.Headers.Contains(IdempotencyMiddleware.ReplayHeader), Is.False, "the first response is not a replay");
        long afterFirst = await writes();

        using var second = await client.SendAsync(request(requestId));
        byte[] secondBody = await second.Content.ReadAsByteArrayAsync();
        Assert.That(second.StatusCode, Is.EqualTo(first.StatusCode));
        Assert.That(second.Headers.GetValues(IdempotencyMiddleware.ReplayHeader).Single(), Is.EqualTo("true"));
        Assert.That(secondBody, Is.EqualTo(firstBody), "byte-identical replay");
        Assert.That(second.Content.Headers.ContentType?.ToString(), Is.EqualTo(first.Content.Headers.ContentType?.ToString()));
        Assert.That(second.Headers.GetValues(ApiHeaders.RequestId).Single(), Is.EqualTo(requestId));
        Assert.That(await writes(), Is.EqualTo(afterFirst), "no second write");
        Assert.That(afterFirst, Is.GreaterThanOrEqualTo(before));
        return firstBody;
    }

    /// <summary>A request without a 32-hex <c>X-Request-Id</c> is <c>400 VALIDATION</c> naming the header.</summary>
    public static async Task AssertRequestIdRequiredAsync(HttpClient client, Func<string?, HttpRequestMessage> request)
    {
        foreach (string? id in new[] { null, "not-hex", "0123456789abcdef0123456789abcde", Guid.NewGuid().ToString("D") })
        {
            using var r = await client.SendAsync(request(id));
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), $"X-Request-Id {id ?? "(none)"}");
            string body = await r.Content.ReadAsStringAsync();
            Assert.That(body, Does.Contain("\"code\":\"VALIDATION\"").And.Contain("X-Request-Id"));
        }
    }
}
