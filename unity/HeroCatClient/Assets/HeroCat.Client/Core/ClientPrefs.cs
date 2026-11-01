using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HeroCat.Sim.Content;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// Client-only flags (00 section 5.1, decision 79, issue #47): NEW badges, coach marks, sort/filter/step choices and
    /// toggles. Never in GameState, never cloud-synced, never a sim command, so they cannot affect a state hash. 07 puts
    /// the store in Runtime (<c>ClientPrefs</c>, a JSON file next to the save); until that lands this seam lives in the
    /// client's Core with an in-memory implementation the Unity layer persists as text (TODO(#47)).
    /// </summary>
    public interface IClientPrefs
    {
        /// <summary>Index into TRAIN_BULK_STEPS plus Max (the last index); 07 section 4.4.</summary>
        int TrainStep { get; set; }

        CollectionSort Sort { get; set; }
        CollectionFilter Filter { get; set; }
        bool ReducedMotion { get; set; }

        bool IsNew(Banner banner, string itemId);
        void MarkNew(Banner banner, string itemId);
        void ClearNew(Banner banner, string itemId);
        int NewCount(Banner banner);

        bool SeenCoachMark(string id);
        void MarkCoachMark(string id);

        /// <summary>Bumped on every change, so a vm knows when to re-read NEW badges.</summary>
        int Version { get; }
    }

    /// <summary>07 section 4.5 sort options.</summary>
    public enum CollectionSort { Rarity, Level, Newest, OwnedFirst }

    /// <summary>07 section 4.5 filter options (All, Owned, or one rarity).</summary>
    public enum CollectionFilter { All, Owned, Common, Rare, Epic, Legendary, Mythic }

    public sealed class MemoryClientPrefs : IClientPrefs
    {
        readonly Dictionary<Banner, HashSet<string>> _new = new Dictionary<Banner, HashSet<string>>();
        readonly HashSet<string> _coach = new HashSet<string>(StringComparer.Ordinal);
        int _trainStep;
        CollectionSort _sort;
        CollectionFilter _filter;
        bool _reduced;

        public int Version { get; private set; }

        public int TrainStep { get => _trainStep; set { if (_trainStep != value) { _trainStep = value; Version++; } } }
        public CollectionSort Sort { get => _sort; set { if (_sort != value) { _sort = value; Version++; } } }
        public CollectionFilter Filter { get => _filter; set { if (_filter != value) { _filter = value; Version++; } } }
        public bool ReducedMotion { get => _reduced; set { if (_reduced != value) { _reduced = value; Version++; } } }

        HashSet<string> Set(Banner b)
        {
            if (!_new.TryGetValue(b, out var s)) _new[b] = s = new HashSet<string>(StringComparer.Ordinal);
            return s;
        }

        public bool IsNew(Banner banner, string itemId) => itemId != null && _new.TryGetValue(banner, out var s) && s.Contains(itemId);

        public void MarkNew(Banner banner, string itemId)
        {
            if (itemId != null && Set(banner).Add(itemId)) Version++;
        }

        public void ClearNew(Banner banner, string itemId)
        {
            if (itemId != null && _new.TryGetValue(banner, out var s) && s.Remove(itemId)) Version++;
        }

        public int NewCount(Banner banner) => _new.TryGetValue(banner, out var s) ? s.Count : 0;

        public bool SeenCoachMark(string id) => _coach.Contains(id);

        public void MarkCoachMark(string id)
        {
            if (_coach.Add(id)) Version++;
        }

        // ------------------------------------------------------------------------------------- text form (Unity persists it)

        /// <summary>A line-based text form: <c>key=value</c>, NEW badges as <c>new.WEAPON=id1,id2</c>.</summary>
        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("trainStep=").Append(_trainStep.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("sort=").Append(_sort).Append('\n');
            sb.Append("filter=").Append(_filter).Append('\n');
            sb.Append("reducedMotion=").Append(_reduced ? "1" : "0").Append('\n');
            foreach (var kv in _new)
            {
                if (kv.Value.Count == 0) continue;
                var ids = new List<string>(kv.Value);
                ids.Sort(StringComparer.Ordinal);
                sb.Append("new.").Append(kv.Key).Append('=').Append(string.Join(",", ids)).Append('\n');
            }
            if (_coach.Count > 0)
            {
                var ids = new List<string>(_coach);
                ids.Sort(StringComparer.Ordinal);
                sb.Append("coach=").Append(string.Join(",", ids)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Reads <see cref="Serialize"/>'s form; unknown or broken lines are skipped (losing prefs is acceptable).</summary>
        public static MemoryClientPrefs Deserialize(string text)
        {
            var p = new MemoryClientPrefs();
            if (string.IsNullOrEmpty(text)) return p;
            foreach (var raw in text.Split('\n'))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "trainStep":
                        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ts)) p._trainStep = ts;
                        break;
                    case "sort":
                        if (Enum.TryParse(v, out CollectionSort so)) p._sort = so;
                        break;
                    case "filter":
                        if (Enum.TryParse(v, out CollectionFilter fi)) p._filter = fi;
                        break;
                    case "reducedMotion":
                        p._reduced = v == "1";
                        break;
                    case "coach":
                        foreach (var id in v.Split(',')) if (id.Length > 0) p._coach.Add(id);
                        break;
                    default:
                        if (k.StartsWith("new.", StringComparison.Ordinal) && Enum.TryParse(k.Substring(4), out Banner b))
                            foreach (var id in v.Split(',')) if (id.Length > 0) p.Set(b).Add(id);
                        break;
                }
            }
            return p;
        }
    }
}
