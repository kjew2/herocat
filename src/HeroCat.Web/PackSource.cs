using System;
using System.Collections.Generic;
using System.Text.Json;
using HeroCat.Sim.Content;

namespace HeroCat.Web
{
    /// <summary>
    /// Content/*.json bundled into one JSON object { "manifest.json": "<raw file text>", "heroes.json": "...", ... }
    /// (web/wasm/pack-content.js). The raw text is kept byte-for-byte so the manifest sha256 checks pass in memory.
    /// </summary>
    internal sealed class PackSource : IContentSource
    {
        private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.Ordinal);

        public static bool LooksLikePack(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("manifest.json", out var m) && m.ValueKind == JsonValueKind.String;
            }
            catch (JsonException) { return false; }
        }

        public PackSource(string packJson)
        {
            using var doc = JsonDocument.Parse(packJson);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) _files[p.Name] = p.Value.GetString();
        }

        public bool Exists(string fileName) => _files.ContainsKey(fileName);

        public string ReadText(string fileName) => _files.TryGetValue(fileName, out var t) ? t : throw new System.IO.FileNotFoundException(fileName);
    }
}
