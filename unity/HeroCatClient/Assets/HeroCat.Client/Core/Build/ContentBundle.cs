using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// The player build's content bundle (09 section 10.1, 07 section 17; issue #133), engine-free so dotnet tests it. The
    /// Unity build step (Editor/ContentPreBuild) verifies content/ (manifest + sha256 through ContentLoader), mirrors its
    /// *.json into <see cref="StreamingAssetsDir"/> (09's bundled floor, read with File on iOS and desktop players) and
    /// <see cref="ResourcesDir"/> (TextAssets: Android's StreamingAssets lives inside the APK and is not a folder, so the
    /// Android player reads Resources; decision recorded in issue #133), then checks the asset addresses.
    /// </summary>
    public static class ContentBundle
    {
        /// <summary>Under Assets/; both copies are git-ignored and rewritten before every build.</summary>
        public const string StreamingAssetsDir = "StreamingAssets/content", ResourcesDir = "Resources/content";

        /// <summary>
        /// Makes <paramref name="dst"/> hold exactly <paramref name="src"/>'s *.json: copies new or changed files, deletes
        /// stale ones (and their Unity .meta). Returns the number of files in the bundle.
        /// </summary>
        public static int Mirror(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            string[] files = Directory.GetFiles(src, "*.json");
            var names = new HashSet<string>(files.Select(Path.GetFileName), StringComparer.Ordinal);
            foreach (string stale in Directory.GetFiles(dst, "*.json"))
            {
                if (names.Contains(Path.GetFileName(stale))) continue;
                File.Delete(stale);
                if (File.Exists(stale + ".meta")) File.Delete(stale + ".meta");
            }
            foreach (string file in files)
            {
                string target = Path.Combine(dst, Path.GetFileName(file));
                byte[] bytes = File.ReadAllBytes(file);
                if (File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(bytes)) continue;
                File.WriteAllBytes(target, bytes);
            }
            return files.Length;
        }
    }

    /// <summary>One <c>asset</c> cell whose address the build cannot resolve.</summary>
    public readonly struct MissingAsset
    {
        public MissingAsset(string table, string row, string column, string address)
        {
            Table = table; Row = row; Column = column; Address = address;
        }

        public string Table { get; }
        public string Row { get; }
        public string Column { get; }
        public string Address { get; }

        public override string ToString() => Table + "[" + Row + "]." + Column + " = " + Address;
    }

    /// <summary>
    /// 09 section 3.2's <c>asset</c> check, which belongs to the Unity build step, not the exporter: every asset address in
    /// the content (icons, sprites, VFX, backgrounds, music) must resolve, else the build lists the missing ones.
    /// <see cref="Columns"/> mirrors the <c>asset</c> columns of tools/export_content.py's SCHEMA; a Runtime test keeps the
    /// two equal.
    /// </summary>
    public static class ContentAssets
    {
        /// <summary>Table -> its asset columns (09 section 3; the exporter's SCHEMA).</summary>
        public static readonly IReadOnlyDictionary<string, string[]> Columns = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["stats"] = new[] { "icon" },
            ["chapters"] = new[] { "background", "music" },
            ["enemies"] = new[] { "sprite", "anim_set" },
            ["bosses"] = new[] { "sprite", "anim_set", "intro_vfx" },
            ["rarities"] = new[] { "shape_icon" },
            ["weapons"] = new[] { "icon", "sprite" },
            ["skills"] = new[] { "icon", "vfx" },
            ["companions"] = new[] { "icon", "sprite", "vfx" },
            ["runes"] = new[] { "icon" },
            ["banners"] = new[] { "icon", "banner_art" },
            ["awakening"] = new[] { "icon" },
            ["research"] = new[] { "icon" },
            ["fish_levels"] = new[] { "sprite" },
            ["quests_daily"] = new[] { "icon" },
            ["quests_weekly"] = new[] { "icon" },
            ["dungeons"] = new[] { "icon", "background" },
            ["shop_products"] = new[] { "icon" },
            ["shop_packages"] = new[] { "icon" },
            ["inbox_templates"] = new[] { "icon" },
        };

        /// <summary>
        /// Every asset address in the tables of <paramref name="contentDir"/> that <paramref name="exists"/> rejects, in
        /// table, row and column order. Null cells (nullable asset columns) and absent optional tables are skipped.
        /// </summary>
        public static List<MissingAsset> Missing(string contentDir, Func<string, bool> exists)
        {
            var missing = new List<MissingAsset>();
            foreach (var kv in Columns.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                string path = Path.Combine(contentDir, kv.Key + ".json");
                if (!File.Exists(path)) continue;
                var rows = (JArray)JObject.Parse(File.ReadAllText(path))["rows"];
                if (rows == null) continue;
                int index = 0;
                foreach (var row in rows.OfType<JObject>())
                {
                    string id = RowId(row, index++);
                    foreach (string col in kv.Value)
                    {
                        var cell = row[col];
                        if (cell == null || cell.Type == JTokenType.Null) continue;
                        string address = (string)cell;
                        if (string.IsNullOrEmpty(address) || !exists(address)) missing.Add(new MissingAsset(kv.Key, id, col, address));
                    }
                }
            }
            return missing;
        }

        static string RowId(JObject row, int index)
        {
            foreach (string k in new[] { "id", "sku", "level", "rank", "key" })
                if (row[k] != null && row[k].Type != JTokenType.Null) return (string)row[k];
            return "#" + index;
        }
    }
}
