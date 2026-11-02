using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HeroCat.Sim.Content;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// 08 section 6.1's <c>GET /config</c> body as the client reads it (issue #144). Shared by the remote ConfigClient
    /// (HeroCat.Remote) and the persisted cache, so a cached answer reads exactly like a fresh one. Lenient on purpose: a
    /// missing or malformed kill switch is "enabled" (the default of 01 section 8.1 step 2), an unknown banner name in
    /// <c>summonEnabled</c> is ignored, and fields the client does not act on (<c>banners</c>, <c>events</c>, <c>limits</c>,
    /// <c>features</c>, <c>minContentVersionByClient</c>, <c>hotApplyTables</c>) are not read (issue #54: the client never
    /// decides hot apply from <c>hotApplyTables</c>).
    /// </summary>
    public static class ConfigBody
    {
        /// <summary>
        /// Parses a body. <paramref name="fallbackContentVersion"/> (the running content) is the ContentVersion when the body
        /// has no <c>content</c> block. Throws <see cref="FormatException"/> when the text is not a JSON object.
        /// </summary>
        public static ClientConfig Parse(string json, string fallbackContentVersion, ConfigSource source, long fetchedAtServerSeconds)
        {
            JObject o;
            try
            {
                o = JToken.Parse(json ?? "") as JObject;
            }
            catch (JsonException e)
            {
                throw new FormatException("config body is not JSON: " + e.Message, e);
            }
            if (o == null) throw new FormatException("config body is not a JSON object");

            ContentPointer content = null;
            if (o["content"] is JObject c && Str(c["version"]) is string cv)
                content = new ContentPointer(cv, Str(c["url"]), Str(c["sha256"]), Long(c["sizeBytes"]), Str(c["minClientVersion"]))
                {
                    RolloutPct = c["rolloutPct"] is JValue rp && rp.Type == JTokenType.Integer ? (int?)(int)(long)rp : null,
                    InBucket = c["inBucket"] is JValue ib && ib.Type == JTokenType.Boolean ? (bool?)(bool)ib : null,
                };

            var maint = MaintenanceInfo.None;
            if (o["maintenance"] is JObject m)
            {
                bool active = Bool(m["active"], false);
                long until = Long(m["until"]);
                string key = Str(m["messageKey"]);
                if (active || until > 0 || key != null) maint = new MaintenanceInfo(active, key, until);
            }

            var flags = KillSwitches.AllOn;
            if (o["killSwitches"] is JObject k)
            {
                var summon = new Dictionary<Banner, bool>();
                if (k["summonEnabled"] is JObject se)
                    foreach (var kv in se)
                        if (Enum.TryParse(kv.Key, false, out Banner b) && Enum.IsDefined(typeof(Banner), b)) summon[b] = Bool(kv.Value, true);
                flags = new KillSwitches(Bool(k["shopEnabled"], true), Bool(k["dungeonsEnabled"], true), Bool(k["cloudSaveEnabled"], true),
                                         Bool(k["adsEnabled"], true), summon);
            }

            Dictionary<string, string> stores = null;
            if (o["storeUrls"] is JObject su)
            {
                stores = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in su)
                    if (Str(kv.Value) is string url) stores[kv.Key] = url;
            }

            return new ClientConfig(content?.Version ?? fallbackContentVersion ?? "", Str(o["minClientVersion"]), maint, flags)
            {
                Content = content,
                LatestClientVersion = Str(o["latestClientVersion"]),
                StoreUrls = stores,
                ConfigVersion = Long(o["configVersion"]),
                Source = source,
                FetchedAtServerSeconds = fetchedAtServerSeconds,
            };
        }

        /// <summary>The body's <c>serverTime</c>, 0 when absent.</summary>
        public static long ServerTimeOf(string json)
        {
            try
            {
                return JToken.Parse(json ?? "") is JObject o ? Long(o["serverTime"]) : 0;
            }
            catch (JsonException)
            {
                return 0;
            }
        }

        /// <summary>
        /// A copy fetched at <paramref name="fetchedAt"/> is usable at <paramref name="now"/> while it is under
        /// CONFIG_CACHE_MAX_AGE_S old (01 section 8.1 step 2). A device clock set back by as much counts as stale too.
        /// </summary>
        public static bool IsFresh(long fetchedAt, long now) =>
            fetchedAt > 0 && Math.Abs(now - fetchedAt) < RuntimeConfig.CONFIG_CACHE_MAX_AGE_S;

        static string Str(JToken t) => t is JValue v && v.Type == JTokenType.String ? (string)v : null;

        static long Long(JToken t) => t is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? (long)v : 0;

        static bool Bool(JToken t, bool fallback) => t is JValue v && v.Type == JTokenType.Boolean ? (bool)v : fallback;
    }

    /// <summary>One persisted <c>GET /config</c> answer: the raw body and the server time it was fetched at.</summary>
    public sealed record CachedConfig(string Body, long FetchedAtServerSeconds);

    /// <summary>Where the last good config answer is kept between launches (01 section 8.1 step 2).</summary>
    public interface IConfigCache
    {
        /// <summary>The last written copy, or null (none, or unreadable).</summary>
        CachedConfig Read();

        void Write(CachedConfig copy);
    }

    public sealed class MemoryConfigCache : IConfigCache
    {
        public CachedConfig Stored { get; set; }
        public int Writes { get; private set; }

        public CachedConfig Read() => Stored;

        public void Write(CachedConfig copy)
        {
            Stored = copy;
            Writes++;
        }
    }

    /// <summary>
    /// <c>{dataDir}/config/config.json</c>: <c>{"fetchedAt": unix seconds, "body": {...}}</c>, written to a temp file and
    /// moved over the old one, so a kill mid-write leaves the previous copy. An unreadable file reads as no cache.
    /// </summary>
    public sealed class FileConfigCache : IConfigCache
    {
        static readonly Encoding Utf8 = new UTF8Encoding(false);
        readonly string _path;

        public FileConfigCache(string dataDir)
        {
            if (dataDir == null) throw new ArgumentNullException(nameof(dataDir));
            _path = Path.Combine(dataDir, "config", "config.json");
        }

        public string FilePath => _path;

        public CachedConfig Read()
        {
            try
            {
                if (!File.Exists(_path)) return null;
                var o = JObject.Parse(File.ReadAllText(_path, Utf8));
                if (!(o["body"] is JObject body) || !(o["fetchedAt"] is JValue at) || at.Type != JTokenType.Integer) return null;
                return new CachedConfig(body.ToString(Formatting.None), (long)at);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
            {
                return null;
            }
        }

        public void Write(CachedConfig copy)
        {
            if (copy == null) throw new ArgumentNullException(nameof(copy));
            var o = new JObject { ["fetchedAt"] = copy.FetchedAtServerSeconds, ["body"] = JToken.Parse(copy.Body) };
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, o.ToString(Formatting.None), Utf8);
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
    }
}
