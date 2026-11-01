using System;
using System.Net.Http;
using HeroCat.Remote;
using HeroCat.Remote.Transport;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;
using NUnit.Framework;

namespace HeroCat.Remote.Tests
{
    public class RemoteClientTests
    {
        static ServiceException Fails(Action a) => Assert.Throws<ServiceException>(() => a());

        [Test]
        public void Success_returns_body_and_logs_request()
        {
            var t = new FakeTransport().On("GET", "/ping", 200, "pong");
            var c = new RemoteClient(t);
            Assert.That(c.Send("GET", "/ping"), Is.EqualTo("pong"));
            Assert.That(t.Requests, Has.Count.EqualTo(1));
            Assert.That(t.Requests[0].Path, Is.EqualTo("/ping"));
        }

        [TestCase(TransportFailure.Unreachable, ErrorClass.Offline)]
        [TestCase(TransportFailure.Timeout, ErrorClass.Timeout)]
        public void Transport_failures_map_to_kind(TransportFailure failure, ErrorClass kind)
        {
            var c = new RemoteClient(new FakeTransport { FailWith = failure });
            var e = Fails(() => c.Send("GET", "/ping"));
            Assert.That(e.Kind, Is.EqualTo(kind));
            Assert.That(e.Retryable, Is.True);
        }

        [TestCase(400, ErrorClass.Rejected)]
        [TestCase(403, ErrorClass.Rejected)]
        [TestCase(404, ErrorClass.Rejected)]
        [TestCase(408, ErrorClass.Timeout)]
        [TestCase(409, ErrorClass.Conflict)]
        [TestCase(500, ErrorClass.ServerError)]
        [TestCase(503, ErrorClass.ServerError)]
        [TestCase(302, ErrorClass.ServerError)]
        public void Status_codes_map_to_kind(int status, ErrorClass kind)
        {
            var c = new RemoteClient(new FakeTransport().On("POST", "/x", status));
            Assert.That(Fails(() => c.Send("POST", "/x")).Kind, Is.EqualTo(kind));
        }

        [Test]
        public void Rejected_carries_the_body_code()
        {
            var c = new RemoteClient(new FakeTransport().On("POST", "/pull", 400, "{\"code\":\"CannotAfford\"}"));
            var e = Fails(() => c.Send("POST", "/pull"));
            Assert.That(e.Kind, Is.EqualTo(ErrorClass.Rejected));
            Assert.That(e.Code, Is.EqualTo("CannotAfford"));
            Assert.That(e.Retryable, Is.False);
        }

        [Test]
        public void Non_json_error_body_falls_back_to_http_code()
        {
            var c = new RemoteClient(new FakeTransport().On("GET", "/x", 502, "<html>bad gateway</html>"));
            Assert.That(Fails(() => c.Send("GET", "/x")).Code, Is.EqualTo("Http502"));
        }

        [Test]
        public void Unrouted_call_is_rejected_with_NoRoute()
        {
            var e = Fails(() => new RemoteClient(new FakeTransport()).Send("GET", "/nope"));
            Assert.That(e.Kind, Is.EqualTo(ErrorClass.Rejected));
            Assert.That(e.Code, Is.EqualTo("NoRoute"));
        }

        [Test]
        public void Sdk_exceptions_are_mapped()
        {
            Assert.That(RemoteErrors.FromException(new HttpRequestException("dns")).Kind, Is.EqualTo(ErrorClass.Offline));
            Assert.That(RemoteErrors.FromException(new System.Threading.Tasks.TaskCanceledException()).Kind, Is.EqualTo(ErrorClass.Timeout));
            Assert.That(RemoteErrors.FromException(new InvalidOperationException("sdk bug")).Kind, Is.EqualTo(ErrorClass.ServerError));
            var original = new ServiceException(ErrorClass.Conflict, "Stale");
            Assert.That(RemoteErrors.FromException(original), Is.SameAs(original));
        }

        [Test]
        public void Offline_transport_is_always_offline()
        {
            var e = Fails(() => new RemoteClient(OfflineTransport.Instance).Send("GET", "/time"));
            Assert.That(e.Kind, Is.EqualTo(ErrorClass.Offline));
        }

        [Test]
        public void Request_timeout_is_passed_to_transport()
        {
            TimeSpan seen = default;
            var t = new CapturingTransport(ts => seen = ts);
            new RemoteClient(t, new RemoteOptions { RequestTimeout = TimeSpan.FromSeconds(2) }).Send("GET", "/x");
            Assert.That(seen, Is.EqualTo(TimeSpan.FromSeconds(2)));
        }

        sealed class CapturingTransport : IRemoteTransport
        {
            readonly Action<TimeSpan> _onSend;
            public CapturingTransport(Action<TimeSpan> onSend) { _onSend = onSend; }
            public RemoteResponse Send(RemoteRequest request, TimeSpan timeout) { _onSend(timeout); return new RemoteResponse(204); }
        }
    }
}
