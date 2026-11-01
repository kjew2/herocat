using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// 02 section 14.1: the fields the sink adds to every event. Built on the main thread at Track time from the clock and
    /// the live state. Owners never send these fields (the sink drops a prop that collides with one).
    /// </summary>
    public sealed class AnalyticsEnvelope
    {
        public const string TS = "ts";
        public const string SESSION_ID = "sessionId";
        public const string PLAYER_ID_HASH = "playerIdHash";
        public const string DEVICE_ID = "deviceId";
        public const string CLIENT_VERSION = "clientVersion";
        public const string CONTENT_VERSION = "contentVersion";
        public const string SCHEMA_VERSION = "schemaVersion";
        public const string DEBUG_TAINTED = "debugTainted";
        public const string CLOCK_AUTHORITATIVE = "clockAuthoritative";
        public const string DAY_INDEX = "dayIndex";
        public const string STAGE = "stage";
        public const string AGE_GATE = "ageGate";

        /// <summary>Every envelope field, in the order of 02 section 14.1.</summary>
        public static readonly IReadOnlyList<string> Fields = new[]
        {
            TS, SESSION_ID, PLAYER_ID_HASH, DEVICE_ID, CLIENT_VERSION, CONTENT_VERSION, SCHEMA_VERSION, DEBUG_TAINTED,
            CLOCK_AUTHORITATIVE, DAY_INDEX, STAGE, AGE_GATE,
        };

        readonly IClock _clock;
        readonly Func<GameState> _state;
        readonly string _salt;
        readonly long _daySeconds;
        Guid _hashedFor;
        string _hash;

        /// <param name="clock">Server seconds for <c>ts</c> and <c>clockAuthoritative</c>.</param>
        /// <param name="state">The live state; null (before the save loads) leaves dayIndex and stage null.</param>
        /// <param name="salt">The per-environment salt of <c>playerIdHash</c> (from the build's environment config).</param>
        /// <param name="daySeconds">DAY_SECONDS (content constant) for <c>dayIndex</c>.</param>
        public AnalyticsEnvelope(IClock clock, Func<GameState> state, string salt, long daySeconds, Guid sessionId, Guid deviceId,
                                 string clientVersion, string contentVersion)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _state = state ?? (() => null);
            if (string.IsNullOrEmpty(salt)) throw new ArgumentException("a per-environment salt is required (02 section 14.1)", nameof(salt));
            if (daySeconds <= 0) throw new ArgumentOutOfRangeException(nameof(daySeconds));
            _salt = salt;
            _daySeconds = daySeconds;
            SessionId = sessionId;
            DeviceId = deviceId;
            ClientVersion = clientVersion ?? "";
            ContentVersion = contentVersion ?? "";
        }

        /// <summary>GUID per process (02 section 14.1); the client passes BootOptions.SessionId.</summary>
        public Guid SessionId { get; }

        /// <summary>This install's id (GameServices.InstallId).</summary>
        public Guid DeviceId { get; }

        public string ClientVersion { get; }
        public string ContentVersion { get; }

        /// <summary>08 section 10.1: 1 for an under-age player. Set once the age screen has an answer.</summary>
        public int AgeGate { get; set; }

        /// <summary>
        /// Lowercase hex SHA-256 of UTF-8 <c>salt + ":" + playerId</c> (playerId as 36-char "D" GUID). Never the raw id. Null for
        /// <see cref="Guid.Empty"/> (an unlinked local guest has no playerId; deviceId identifies it).
        /// </summary>
        public static string HashPlayerId(Guid playerId, string salt)
        {
            if (playerId == Guid.Empty) return null;
            var bytes = Encoding.UTF8.GetBytes(salt + ":" + playerId.ToString("D"));
            byte[] h;
            using (var sha = SHA256.Create()) h = sha.ComputeHash(bytes);
            var sb = new StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>The envelope for an event tracked now. Main thread (reads the live state).</summary>
        public void WriteTo(IDictionary<string, object> target)
        {
            var s = _state();
            long ts = _clock.NowServerSeconds;
            target[TS] = ts;
            target[SESSION_ID] = SessionId.ToString("N");
            target[PLAYER_ID_HASH] = PlayerIdHash(s);
            target[DEVICE_ID] = DeviceId.ToString("N");
            target[CLIENT_VERSION] = ClientVersion;
            target[CONTENT_VERSION] = ContentVersion;
            target[SCHEMA_VERSION] = HeroCat.Runtime.Save.SaveSchema.Current;
            target[DEBUG_TAINTED] = s?.DebugTainted ?? false;
            target[CLOCK_AUTHORITATIVE] = _clock.IsAuthoritative;
            target[DAY_INDEX] = s == null ? (object)null : FloorDiv(ts - s.Time.CreatedServerTime, _daySeconds);
            target[STAGE] = s == null ? (object)null : s.Combat.Stage;
            target[AGE_GATE] = AgeGate;
        }

        string PlayerIdHash(GameState s)
        {
            var id = s?.PlayerId ?? Guid.Empty;
            if (id != _hashedFor || (_hash == null && id != Guid.Empty))
            {
                _hashedFor = id;
                _hash = HashPlayerId(id, _salt);
            }
            return _hash;
        }

        static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
