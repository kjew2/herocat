using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace HeroCat.Runtime.Save
{
    /// <summary>
    /// Forward-only JSON migrations (specs/06 section 8). Steps operate on the body JObject before it is
    /// materialised, so the C# types only ever describe the current schema.
    ///
    /// Rules for a step: a pure JObject -> JObject function; reads no ContentDb, clock, file or current C# type
    /// (any value it needs is a literal frozen inside the step); tolerates absent optional fields; does not set
    /// SchemaVersion (the driver does). To add v1 -> v2: bump SaveSchema.Current to 2, append the step to
    /// <see cref="Steps"/>, add a fixture test.
    /// </summary>
    public static class SaveMigrations
    {
        /// <summary>Steps[i] migrates version (SaveSchema.MinSupported + i) to the next. Count == Current - MinSupported.</summary>
        public static readonly IReadOnlyList<Func<JObject, JObject>> Steps = new Func<JObject, JObject>[]
        {
            V0ToV1,
        };

        /// <summary>Lift <paramref name="json"/> from <paramref name="version"/> to SaveSchema.Current.</summary>
        /// <param name="onStep">Optional (from, to) callback per applied step, for analytics (migration_applied).</param>
        public static JObject Migrate(int version, JObject json, Action<int, int> onStep = null) =>
            Migrate(version, json, Steps, SaveSchema.MinSupported, SaveSchema.Current, onStep);

        /// <summary>The driver, parameterised so tests can exercise it with their own chain.</summary>
        public static JObject Migrate(int version, JObject json, IReadOnlyList<Func<JObject, JObject>> steps,
                                      int minSupported, int current, Action<int, int> onStep = null)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (version > current) throw new SaveTooNewException(version, current);
            if (version < minSupported) throw new SaveInvalidException($"schema v{version} is older than the oldest supported (v{minSupported})");
            if (steps.Count != current - minSupported)
                throw new InvalidOperationException($"migration chain has {steps.Count} steps, expected {current - minSupported}");
            for (int v = version; v < current; v++)
            {
                json = steps[v - minSupported](json) ?? throw new InvalidOperationException($"migration step v{v} returned null");
                json["SchemaVersion"] = v + 1;
                onStep?.Invoke(v, v + 1);
            }
            return json;
        }

        // ================================================================== steps

        /// <summary>
        /// v0 -> v1. v0 was the pre-envelope dev format: the bare GameState at the root ("SchemaVersion": 0), seeds
        /// as JSON integers, and an optional root "LastSavedAtServerSec". v1 wraps it as { Meta, State } and writes
        /// seeds as 16-char hex strings. Without a saved anchor the sim's last wall-clock sync is the best guess.
        /// </summary>
        static JObject V0ToV1(JObject v0)
        {
            var state = (JObject)v0.DeepClone();
            state.Remove("SchemaVersion");
            long lastSaved;
            var anchor = state["LastSavedAtServerSec"];
            if (anchor != null && anchor.Type == JTokenType.Integer) lastSaved = (long)anchor;
            else if (state["Clock"]?["LastSyncServerMs"] is JValue ms && ms.Type == JTokenType.Integer) lastSaved = (long)ms / 1000;
            else lastSaved = 0;
            state.Remove("LastSavedAtServerSec");

            if (state["Rng"] is JObject rng)
            {
                HexSeed(rng, "ProfileSeed");
                if (rng["Streams"] is JObject streams)
                    foreach (var p in streams.Properties())
                        if (p.Value is JObject stream) HexSeed(stream, "Seed");
            }

            return new JObject
            {
                ["SchemaVersion"] = 0,   // the driver overwrites this
                ["Meta"] = new JObject
                {
                    ["LastSavedAtServerSec"] = lastSaved,
                    ["SaveSequence"] = 0,
                    ["DeviceInstallId"] = Guid.Empty.ToString(),
                    ["DebugTainted"] = false,
                },
                ["State"] = state,
            };
        }

        static void HexSeed(JObject o, string name)
        {
            if (!(o[name] is JValue v) || v.Type != JTokenType.Integer) return;
            ulong seed = v.Value is BigInteger b ? (ulong)b : Convert.ToUInt64(v.Value, CultureInfo.InvariantCulture);
            o[name] = seed.ToString("x16", CultureInfo.InvariantCulture);
        }
    }
}
