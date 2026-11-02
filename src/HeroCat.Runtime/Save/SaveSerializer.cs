using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HeroCat.Sim.State;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace HeroCat.Runtime.Save
{
    /// <summary>A decoded save file (02 section 6.2): the body at its own schema version, before migration.</summary>
    public sealed class SaveFile
    {
        public SaveFile(int schemaVersion, JObject body, bool hmacPresent, bool hmacValid, string sha256)
        {
            SchemaVersion = schemaVersion; Body = body; HmacPresent = hmacPresent; HmacValid = hmacValid; Sha256 = sha256;
        }

        public int SchemaVersion { get; }
        public JObject Body { get; }
        public bool HmacPresent { get; }

        /// <summary>False on a mismatch; not a local reject (a restored backup from another device fails it), the server decides.</summary>
        public bool HmacValid { get; }

        /// <summary>Lowercase hex SHA-256 of the body bytes.</summary>
        public string Sha256 { get; }
    }

    /// <summary>
    /// The save container and serializer (02 sections 5-6). A file is one ASCII header line and exactly <c>len</c> bytes
    /// of UTF-8 JSON, the GameState tree at the root:
    /// <code>
    /// HEROCAT-SAVE v=1 fmt=json crc32=7f3a19c2 len=38127 hmac=&lt;64 hex or -&gt;\n
    /// { "SchemaVersion": 1, "ContentVersion": "...", ... }
    /// </code>
    /// <c>v</c> is the container version; the body's SchemaVersion is authoritative. Settings follow 02 section 6.1:
    /// PascalCase as declared, nulls and defaults written, unknown members ignored, doubles round-trip, NaN/Infinity as
    /// strings (rejected by the validator), enums by name (ints accepted on read), every dictionary and set written sorted
    /// by key (ordinal on the written key) so equal states encode to equal bytes, no $type, reference loops are errors.
    /// </summary>
    public static class SaveSerializer
    {
        public const string Magic = "HEROCAT-SAVE";
        public const int ContainerVersion = 1;
        const int MaxHeaderBytes = 256;
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter(), new SortedSetConverter(), new SortedDictionaryConverter() },
            NullValueHandling = NullValueHandling.Include,
            DefaultValueHandling = DefaultValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            FloatParseHandling = FloatParseHandling.Double,
            FloatFormatHandling = FloatFormatHandling.String,
            DateParseHandling = DateParseHandling.None,
            TypeNameHandling = TypeNameHandling.None,
            ReferenceLoopHandling = ReferenceLoopHandling.Error,
            Culture = CultureInfo.InvariantCulture,
        };

        public static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        /// <summary>Formatting.None in release, Indented in development builds (02 section 6.1). Default: release.</summary>
        public static Formatting BodyFormatting = Formatting.None;

        // ------------------------------------------------------------------------------------------------ encode

        /// <summary>Header + body; computes crc32 and, with a device key, the HMAC (else <c>hmac=-</c>, Local mode).</summary>
        public static byte[] Encode(GameState state, byte[] deviceKey = null) =>
            EncodeBody(ToJson(state).ToString(BodyFormatting), deviceKey);

        /// <summary>Wrap an arbitrary JSON body in a valid header (fixtures, tests). No schema checks.</summary>
        public static byte[] EncodeBody(string bodyJson, byte[] deviceKey = null)
        {
            var body = Utf8NoBom.GetBytes(bodyJson);
            string hmac = deviceKey == null ? "-" : Hex(Hmac(deviceKey, body));
            var header = Encoding.ASCII.GetBytes(
                $"{Magic} v={ContainerVersion} fmt=json crc32={Crc32.Compute(body):x8} len={body.Length.ToString(CultureInfo.InvariantCulture)} hmac={hmac}\n");
            var file = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, file, 0, header.Length);
            Buffer.BlockCopy(body, 0, file, header.Length, body.Length);
            return file;
        }

        /// <summary>The body document: the GameState tree at the root.</summary>
        public static JObject ToJson(GameState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return JObject.FromObject(state, Serializer);
        }

        /// <summary>Canonical minified JSON of a state (tests, equality diagnostics).</summary>
        public static string StateToJson(GameState state) => ToJson(state).ToString(Formatting.None);

        // ------------------------------------------------------------------------------------------------ decode

        /// <summary>
        /// Validates header, length and CRC and parses the body (rule R1). Throws <see cref="SaveCorruptException"/> on a
        /// malformed header, a byte-count mismatch, a CRC mismatch or a parse failure. An HMAC mismatch does not throw.
        /// </summary>
        public static SaveFile Decode(byte[] file, byte[] deviceKey = null)
        {
            if (file == null || file.Length == 0) throw new SaveCorruptException("empty file");
            int nl = Array.IndexOf(file, (byte)'\n', 0, Math.Min(file.Length, MaxHeaderBytes));
            if (nl < 0) throw new SaveCorruptException("no header line");
            string header = Encoding.ASCII.GetString(file, 0, nl);
            var parts = header.Split(' ');
            if (parts.Length != 6 || parts[0] != Magic) throw new SaveCorruptException($"bad header '{header}'");
            string v = Field(parts[1], "v"), fmt = Field(parts[2], "fmt"), crcHex = Field(parts[3], "crc32"),
                   lenStr = Field(parts[4], "len"), hmacHex = Field(parts[5], "hmac");
            if (v != ContainerVersion.ToString(CultureInfo.InvariantCulture)) throw new SaveCorruptException($"unsupported container v={v}");
            if (fmt != "json") throw new SaveCorruptException($"unsupported fmt '{fmt}'");
            if (crcHex.Length != 8 || !uint.TryParse(crcHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint crc))
                throw new SaveCorruptException("bad crc32 field");
            if (!int.TryParse(lenStr, NumberStyles.None, CultureInfo.InvariantCulture, out int len))
                throw new SaveCorruptException("bad len field");
            bool hmacPresent = hmacHex != "-";
            if (hmacPresent && hmacHex.Length != 64) throw new SaveCorruptException("bad hmac field");

            int bodyStart = nl + 1;
            int remaining = file.Length - bodyStart;
            if (remaining < len) throw new SaveCorruptException($"truncated body ({remaining} < {len})");
            if (remaining > len) throw new SaveCorruptException($"trailing bytes ({remaining} > {len})");
            if (Crc32.Compute(file, bodyStart, len) != crc) throw new SaveCorruptException("crc mismatch");

            var bodyBytes = new byte[len];
            Buffer.BlockCopy(file, bodyStart, bodyBytes, 0, len);
            bool hmacValid = hmacPresent && deviceKey != null &&
                             string.Equals(Hex(Hmac(deviceKey, bodyBytes)), hmacHex, StringComparison.OrdinalIgnoreCase);

            JObject body;
            try { body = ParseBody(Utf8NoBom.GetString(bodyBytes)); }
            catch (JsonException e) { throw new SaveCorruptException("body is not valid JSON", e); }

            var sv = body["SchemaVersion"];
            if (sv == null || sv.Type != JTokenType.Integer) throw new SaveCorruptException("SchemaVersion missing");
            return new SaveFile((int)sv, body, hmacPresent, hmacValid, Hex(Sha256(bodyBytes)));
        }

        /// <summary>Parse a body without a header (fixtures, debug import).</summary>
        public static JObject ParseBody(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json))
                   { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
            {
                var o = JObject.Load(reader);
                while (reader.Read())
                    if (reader.TokenType != JsonToken.Comment) throw new JsonReaderException("trailing content after body");
                return o;
            }
        }

        static string Field(string part, string key)
        {
            string prefix = key + "=";
            if (!part.StartsWith(prefix, StringComparison.Ordinal)) throw new SaveCorruptException($"header field '{key}' missing");
            return part.Substring(prefix.Length);
        }

        // ------------------------------------------------------------------------------------------------ materialize

        /// <summary>The GameState root sections R3 requires in the body (02 section 6.4).</summary>
        static readonly string[] RootSections =
        {
            "Time", "Party", "Training", "Wallet", "Weapons", "Skills", "Companions", "Runes", "Loadout", "Fish", "Banners", "Combat",
            "Progress", "Quests", "Dungeons", "Shop", "Research", "Inbox", "Offline", "Pending", "Tutorial", "Rng", "Stats",
        };

        /// <summary>Body at the CURRENT schema version (run SaveMigrations first) to a GameState. Throws SaveInvalidException.</summary>
        public static GameState Materialize(JObject body)
        {
            if (body == null) throw new SaveInvalidException("no body");
            if ((int?)body["SchemaVersion"] != SaveSchema.Current)
                throw new SaveInvalidException($"Materialize expects schema v{SaveSchema.Current}; migrate first");
            // R3 on the body (02 section 6.4): an absent root section is not silently defaulted, so a pre-party dev save
            // (root Hero, no Party; 00 decision 109) is rejected rather than loaded as an empty party.
            foreach (var name in RootSections)
                if (body[name] == null || body[name].Type == JTokenType.Null)
                    throw new SaveInvalidException($"R3: root section {name} is absent");
            try
            {
                return body.ToObject<GameState>(Serializer) ?? throw new SaveInvalidException("body is null");
            }
            catch (JsonException e) { throw new SaveInvalidException("body does not deserialise: " + e.Message, e); }
            catch (FormatException e) { throw new SaveInvalidException("body does not deserialise: " + e.Message, e); }
            catch (OverflowException e) { throw new SaveInvalidException("body does not deserialise: " + e.Message, e); }
            catch (ArgumentException e) { throw new SaveInvalidException("body does not deserialise: " + e.Message, e); }
        }

        // ------------------------------------------------------------------------------------------------ helpers

        static byte[] Hmac(byte[] key, byte[] body)
        {
            using (var h = new HMACSHA256(key)) return h.ComputeHash(body);
        }

        static byte[] Sha256(byte[] body)
        {
            using (var h = SHA256.Create()) return h.ComputeHash(body);
        }

        static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        static string KeyString(object key)
        {
            switch (key)
            {
                case string s: return s;
                case Enum e: return e.ToString();
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return key.ToString();
            }
        }

        /// <summary>HashSet&lt;int&gt; / HashSet&lt;string&gt; as JSON arrays, written sorted (02 section 6.1). Reading is Json.NET's default.</summary>
        sealed class SortedSetConverter : JsonConverter
        {
            public override bool CanConvert(Type t) => t == typeof(HashSet<int>) || t == typeof(HashSet<string>);
            public override bool CanRead => false;

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) =>
                throw new NotSupportedException();

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                writer.WriteStartArray();
                if (value is HashSet<int> ints)
                {
                    var l = new List<int>(ints);
                    l.Sort();
                    foreach (var v in l) writer.WriteValue(v);
                }
                else
                {
                    var l = new List<string>((HashSet<string>)value);
                    l.Sort(StringComparer.Ordinal);
                    foreach (var v in l) writer.WriteValue(v);
                }
                writer.WriteEndArray();
            }
        }

        /// <summary>Every Dictionary written with its keys sorted ordinally as written (enum names, strings). Reading is Json.NET's default.</summary>
        sealed class SortedDictionaryConverter : JsonConverter
        {
            public override bool CanConvert(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>);
            public override bool CanRead => false;

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) =>
                throw new NotSupportedException();

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var entries = new List<KeyValuePair<string, object>>();
                foreach (DictionaryEntry e in (IDictionary)value) entries.Add(new KeyValuePair<string, object>(KeyString(e.Key), e.Value));
                entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                writer.WriteStartObject();
                foreach (var e in entries)
                {
                    writer.WritePropertyName(e.Key);
                    serializer.Serialize(writer, e.Value);
                }
                writer.WriteEndObject();
            }
        }
    }
}
