using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using Newtonsoft.Json.Linq;

namespace HeroCat.Runtime.Save
{
    /// <summary>
    /// Startup guard against IL2CPP managed code stripping (issue #8). Newtonsoft builds GameState by reflection, so
    /// a stripped setter or constructor does not throw: the field just silently comes back as its default. This
    /// builds a small played profile in memory, runs it through the real write and load path (Encode, then
    /// DecodeFull) and compares the canonical JSON before and after. Any difference means the build lost
    /// serialization metadata (a native build would need a link.xml or equivalent). Touches no files.
    /// </summary>
    public static class SaveSelfCheck
    {
        public sealed class Result
        {
            /// <summary>JSON paths that differ (or an exception message). Empty when the round trip is exact.</summary>
            public List<string> Mismatches { get; } = new List<string>();

            public bool Ok => Mismatches.Count == 0;

            public override string ToString() => Ok
                ? "save self-check OK"
                : $"save self-check FAILED ({Mismatches.Count}):\n  " + string.Join("\n  ", Mismatches);
        }

        /// <summary>Profile ID of the throwaway state; never written anywhere.</summary>
        static readonly Guid CheckProfileId = new Guid("5e1fc8ec-0000-4000-8000-000000000008");

        public static Result Run(ContentDb content, long nowServerSec = 1767225600 /* 2026-01-01 */)
        {
            var result = new Result();
            try
            {
                var state = SampleState(content, nowServerSec);
                var meta = new SaveMeta { LastSavedAtServerSec = nowServerSec, SaveSequence = 7, DeviceInstallId = CheckProfileId, DebugTainted = true };
                var (back, backMeta, _) = SaveSystem.DecodeFull(SaveSerializer.Encode(state, meta), content);

                Diff(JObject.FromObject(state, SaveSerializer.Serializer), JObject.FromObject(back, SaveSerializer.Serializer),
                     "State", result.Mismatches);
                Diff(JObject.FromObject(meta, SaveSerializer.Serializer), JObject.FromObject(backMeta, SaveSerializer.Serializer),
                     "Meta", result.Mismatches);
                // A stripped getter drops the property from both sides of the diff; catch the ones that matter.
                if (back.Gear?.Slots == null || !back.Gear.Slots.Any(p => p != null))
                    result.Mismatches.Add("State.Gear.Slots: no gear piece survived (GearPiece constructor stripped?)");
                if (back.Rng?.Streams == null || back.Rng.Streams.Count == 0)
                    result.Mismatches.Add("State.Rng.Streams: empty after load");
            }
            catch (Exception e)
            {
                result.Mismatches.Add("round trip threw " + e.GetType().Name + ": " + e.Message);
            }
            return result;
        }

        /// <summary>A fresh profile with tutorial grants, a little play and a gear piece, so every state type has data.</summary>
        static GameState SampleState(ContentDb content, long nowServerSec)
        {
            var sim = new Simulation(content, GameState.NewProfile(content, CheckProfileId, nowServerSec));
            sim.ApplyTutorialGrants();
            sim.SyncWallClock(nowServerSec, applyCatchUp: false);
            while (sim.Query.CanPull && sim.Pull().Ok) { }
            sim.Advance(300);
            var slots = sim.State.Gear.Slots;
            if (slots.Length > 0 && slots[0] == null) slots[0] = new GearPiece(0, Rarity.R, content.Constants.TierWidth, 1);
            return sim.State;
        }

        /// <summary>Structural JSON diff; appends one line per differing path.</summary>
        internal static void Diff(JToken expected, JToken actual, string path, List<string> into)
        {
            if (expected is JObject eo && actual is JObject ao)
            {
                foreach (var p in eo.Properties())
                {
                    if (ao.TryGetValue(p.Name, out var av)) Diff(p.Value, av, path + "." + p.Name, into);
                    else into.Add($"{path}.{p.Name}: missing after load");
                }
                foreach (var p in ao.Properties())
                    if (!eo.ContainsKey(p.Name)) into.Add($"{path}.{p.Name}: unexpected after load");
            }
            else if (expected is JArray ea && actual is JArray aa && ea.Count == aa.Count)
            {
                for (int i = 0; i < ea.Count; i++) Diff(ea[i], aa[i], $"{path}[{i}]", into);
            }
            else if (!JToken.DeepEquals(expected, actual))
            {
                into.Add($"{path}: wrote {Short(expected)}, read back {Short(actual)}");
            }
        }

        static string Short(JToken t)
        {
            var s = t?.ToString(Newtonsoft.Json.Formatting.None) ?? "<none>";
            return s.Length > 80 ? s.Substring(0, 77) + "..." : s;
        }
    }
}
