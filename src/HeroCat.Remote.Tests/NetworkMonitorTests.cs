using System.Collections.Generic;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Remote.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Remote.Tests
{
    /// <summary>01 section 5.3 NetworkMonitor: reachability plus the last ApiClient outcome, published from Poll.</summary>
    public class NetworkMonitorTests
    {
        sealed class Reach : IReachability
        {
            public bool IsReachable { get; set; } = true;
        }

        [Test]
        public void ReachabilityDecides_UntilAnOutcomeArrives_AndAChangeForgetsTheOutcome()
        {
            var reach = new Reach();
            var m = new NetworkMonitor(reach);
            var seen = new List<bool>();
            m.ConnectivityChanged += seen.Add;

            m.Poll();
            Assert.That(seen, Is.EqualTo(new[] { true }), "the first poll publishes");
            m.Poll();
            Assert.That(seen, Has.Count.EqualTo(1), "no change, no event");

            m.OnServerReached(false);   // a connection failure while the platform claims reachability
            m.Poll();
            Assert.That(m.Online, Is.False);

            reach.IsReachable = false;
            m.Poll();
            reach.IsReachable = true;   // the network came back: the stale failure is forgotten
            m.Poll();
            Assert.That(m.Online, Is.True);

            reach.IsReachable = false;
            m.Poll();
            m.OnServerReached(true);    // a response arrived anyway: the request is the real test
            m.Poll();
            Assert.That(seen, Is.EqualTo(new[] { true, false, true, false, true }));
        }

        [Test]
        public void TheApiClientsOutcomes_FeedTheMonitor()
        {
            var server = new FakeApiHandler().On("GET", "/inbox", 200, new { });
            server.Then(FakeFault.Drop);
            var client = new ApiClient(new ApiClientOptions
            {
                Handler = server,
                Delay = (d, ct) => Task.CompletedTask,
            });
            var m = new NetworkMonitor(new Reach(), client);
            var seen = new List<bool>();
            m.ConnectivityChanged += seen.Add;

            client.SendAsync<JToken>(ApiRequest.Get("/inbox") with { NoRetry = true }).GetAwaiter().GetResult();
            m.Poll();
            client.SendAsync<JToken>(ApiRequest.Get("/inbox")).GetAwaiter().GetResult();
            m.Poll();
            Assert.That(seen, Is.EqualTo(new[] { false, true }));

            m.Dispose();
            server.Then(FakeFault.Drop);
            client.SendAsync<JToken>(ApiRequest.Get("/inbox") with { NoRetry = true }).GetAwaiter().GetResult();
            m.Poll();
            Assert.That(seen, Has.Count.EqualTo(2), "unsubscribed");
        }
    }
}
