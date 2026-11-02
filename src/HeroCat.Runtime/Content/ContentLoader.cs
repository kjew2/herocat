using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HeroCat.Sim.Content;
using HeroCat.Sim.Content.Dto;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace HeroCat.Runtime.Content
{
    /// <summary>Where a content set's files come from: a directory, Unity Resources/StreamingAssets, a test dictionary.</summary>
    public interface IContentSource
    {
        bool Exists(string fileName);

        /// <summary>Raw bytes, so the sha256 is over exactly what the exporter wrote.</summary>
        byte[] ReadBytes(string fileName);

        /// <summary>Every *.json the source holds, or null when it cannot list (Unity Resources); then E902's "present but not listed" half is skipped.</summary>
        IReadOnlyList<string> ListJsonFiles();
    }

    /// <summary>Reads content/*.json from a directory (bots, tests, editor).</summary>
    public sealed class FileContentSource : IContentSource
    {
        readonly string _dir;

        public FileContentSource(string directory) { _dir = directory ?? throw new ArgumentNullException(nameof(directory)); }

        public string Directory => _dir;

        public bool Exists(string fileName) => File.Exists(Path.Combine(_dir, fileName));

        public byte[] ReadBytes(string fileName) => File.ReadAllBytes(Path.Combine(_dir, fileName));

        public IReadOnlyList<string> ListJsonFiles()
        {
            var r = new List<string>();
            foreach (var f in System.IO.Directory.GetFiles(_dir, "*.json")) r.Add(Path.GetFileName(f));
            r.Sort(StringComparer.Ordinal);
            return r;
        }

        /// <summary>Walks up from <paramref name="startDirectory"/> looking for content/manifest.json. Null if none.</summary>
        public static string FindContentDirectory(string startDirectory)
        {
            if (string.IsNullOrEmpty(startDirectory)) return null;
            for (var dir = new DirectoryInfo(startDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "content");
                if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
            }
            return null;
        }
    }

    /// <summary>In-memory files (tests, fixtures).</summary>
    public sealed class MemoryContentSource : IContentSource
    {
        readonly Dictionary<string, byte[]> _files;

        public MemoryContentSource(Dictionary<string, byte[]> files) { _files = files; }

        public static MemoryContentSource CopyOf(IContentSource src)
        {
            var d = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var f in src.ListJsonFiles()) d[f] = src.ReadBytes(f);
            return new MemoryContentSource(d);
        }

        public Dictionary<string, byte[]> Files => _files;
        public bool Exists(string fileName) => _files.ContainsKey(fileName);
        public byte[] ReadBytes(string fileName) => _files[fileName];

        public IReadOnlyList<string> ListJsonFiles()
        {
            var r = new List<string>(_files.Keys);
            r.Sort(StringComparer.Ordinal);
            return r;
        }
    }

    /// <summary>
    /// Bundled-content loader (09 sections 2, 6.2, 8.5; issue #127): verifies the manifest (E900-E903), parses every table
    /// into the Sim's plain DTOs with Newtonsoft and hands them to <see cref="ContentDb.Build"/>. The Sim never sees JSON
    /// (00 contract 1). Localization files are hash-checked here; their text is read by <see cref="Localizer"/>.
    /// TODO(#212): the CDN cache, which keeps the bundled set when a downloaded manifest fails E903.
    /// </summary>
    public static class ContentLoader
    {
        /// <summary>Manifest layouts this build reads (09 section 6.2 schema_version).</summary>
        public const int SupportedSchemaVersion = 1;

        static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
            MissingMemberHandling = MissingMemberHandling.Error,   // a column the DTOs do not know is a stale build
            FloatParseHandling = FloatParseHandling.Double,
            DateParseHandling = DateParseHandling.None,
            Culture = CultureInfo.InvariantCulture,
            Converters = { new ConstantValueConverter() },
        });

        /// <summary>Loads and builds. Throws <see cref="ContentBuildException"/> listing every problem found.</summary>
        /// <param name="clientVersion">This client's semver for E903; null skips the gate (bots, tools).</param>
        public static ContentDb Load(IContentSource source, Tier buildTier, string clientVersion = null) =>
            ContentDb.Build(LoadDtos(source, clientVersion), buildTier);

        /// <summary>From a directory (bots, tests, editor).</summary>
        public static ContentDb LoadFromDirectory(string dir, Tier buildTier, string clientVersion = null) =>
            Load(new FileContentSource(dir), buildTier, clientVersion);

        public static ContentDtos LoadDtos(IContentSource source, string clientVersion = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var errors = new List<ContentError>();
            void Fail() { if (errors.Count > 0) throw new ContentBuildException(errors.AsReadOnly()); }

            if (!source.Exists("manifest.json"))
            {
                errors.Add(new ContentError("E902", "manifest.json", "manifest.json listed in manifest but missing"));
                Fail();
            }
            ManifestDto manifest;
            try { manifest = Parse<ManifestDto>(source.ReadBytes("manifest.json")); }
            catch (JsonException e)
            {
                errors.Add(new ContentError("E901", "manifest.json", "manifest does not parse: " + e.Message));
                Fail();
                throw;
            }
            if (manifest.SchemaVersion != SupportedSchemaVersion)
            {
                errors.Add(new ContentError("E901", "manifest.json",
                    $"manifest schema_version {manifest.SchemaVersion} not supported (supports {SupportedSchemaVersion})"));
                Fail();
            }
            if (clientVersion != null && manifest.MinClientVersion != null && CompareSemver(manifest.MinClientVersion, clientVersion) > 0)
            {
                errors.Add(new ContentError("E903", "manifest.json",
                    $"content requires client >= {manifest.MinClientVersion}, this client is {clientVersion}"));
                Fail();
            }

            var listed = new Dictionary<string, ManifestEntryDto>(StringComparer.Ordinal);
            foreach (var group in new[] { manifest.Tables, manifest.Localization })
                if (group != null)
                    foreach (var kv in group) listed[kv.Value.File] = kv.Value;
            var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var kv in listed)
            {
                if (!source.Exists(kv.Key))
                {
                    errors.Add(new ContentError("E902", kv.Key, $"{kv.Key} listed in manifest but missing"));
                    continue;
                }
                var data = source.ReadBytes(kv.Key);
                string actual = Sha256Hex(data);
                if (!string.Equals(actual, kv.Value.Sha256, StringComparison.Ordinal))
                    errors.Add(new ContentError("E900", kv.Key, $"{kv.Key}: sha256 {actual} does not match manifest {kv.Value.Sha256}"));
                bytes[kv.Key] = data;
            }
            var present = source.ListJsonFiles();
            if (present != null)
                foreach (var f in present)
                    if (f != "manifest.json" && !listed.ContainsKey(f))
                        errors.Add(new ContentError("E902", f, $"{f} present but not listed"));
            Fail();

            var dtos = new ContentDtos { Manifest = manifest };
            if (manifest.Tables != null)
                foreach (var kv in manifest.Tables)
                {
                    var field = DtoField(kv.Key);
                    if (field == null)
                    {
                        errors.Add(new ContentError("E003", kv.Key, $"unknown table '{kv.Key}' (this build has no DTO for it)"));
                        continue;
                    }
                    try
                    {
                        var doc = Parse<JObject>(bytes[kv.Value.File]);
                        var rows = doc["rows"] as JArray ?? throw new JsonSerializationException("no rows array");
                        field.SetValue(dtos, rows.ToObject(field.FieldType, Serializer));
                    }
                    catch (JsonException e)
                    {
                        errors.Add(new ContentError("E010", kv.Value.File, e.Message));
                    }
                }
            Fail();
            return dtos;
        }

        static FieldInfo DtoField(string table)
        {
            var sb = new StringBuilder();
            foreach (var part in table.Split('_'))
                if (part.Length > 0) sb.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
            return typeof(ContentDtos).GetField(sb.ToString());
        }

        static T Parse<T>(byte[] data)
        {
            using (var reader = new JsonTextReader(new StreamReader(new MemoryStream(data), new UTF8Encoding(false))))
            {
                reader.FloatParseHandling = FloatParseHandling.Double;
                reader.DateParseHandling = DateParseHandling.None;
                return Serializer.Deserialize<T>(reader);
            }
        }

        internal static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(data);
                var sb = new StringBuilder(64);
                foreach (var b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Semver compare on MAJOR.MINOR.PATCH; anything unparsable compares as 0.0.0. Build metadata and a pre-release tag
        /// are ignored, so <c>X-Client-Version</c>'s <c>1.4.2+318</c> compares as 1.4.2 (08 section 1.1: build metadata ignored).
        /// </summary>
        public static int CompareSemver(string a, string b)
        {
            int[] pa = Semver(a), pb = Semver(b);
            for (int i = 0; i < 3; i++) if (pa[i] != pb[i]) return pa[i] < pb[i] ? -1 : 1;
            return 0;
        }

        static int[] Semver(string v)
        {
            var r = new int[3];
            v = v ?? "";
            int cut = v.IndexOfAny(new[] { '+', '-' });
            if (cut >= 0) v = v.Substring(0, cut);
            var bits = v.Split('.');
            for (int i = 0; i < 3 && i < bits.Length; i++) int.TryParse(bits[i], NumberStyles.None, CultureInfo.InvariantCulture, out r[i]);
            return r;
        }

        /// <summary>constants.value is typed by its row: hand the Sim a long, double, bool or long[], never a JToken.</summary>
        sealed class ConstantValueConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType) => objectType == typeof(object);
            public override bool CanWrite => false;

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => throw new NotSupportedException();

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                var t = JToken.Load(reader);
                switch (t.Type)
                {
                    case JTokenType.Integer: return t.Value<long>();
                    case JTokenType.Float: return t.Value<double>();
                    case JTokenType.Boolean: return t.Value<bool>();
                    case JTokenType.Array:
                        var a = (JArray)t;
                        var r = new long[a.Count];
                        for (int i = 0; i < a.Count; i++)
                        {
                            if (a[i].Type != JTokenType.Integer) throw new JsonSerializationException("intlist holds a non-integer");
                            r[i] = a[i].Value<long>();
                        }
                        return r;
                    case JTokenType.Null: return null;
                    default: return t.ToString();
                }
            }
        }
    }
}
