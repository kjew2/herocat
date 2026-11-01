using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HeroCat.Runtime.Save
{
    /// <summary>Receives one call per applied step (analytics <c>migration_applied{from,to}</c>).</summary>
    public interface IMigrationLog
    {
        void Applied(int from, int to);
    }

    public sealed class NullMigrationLog : IMigrationLog
    {
        public static readonly NullMigrationLog Instance = new NullMigrationLog();
        public void Applied(int from, int to) { }
    }

    /// <summary>
    /// Forward-only JSON migrations (02 section 7). Steps run on the body JObject between Decode and Materialize, so the
    /// C# types only ever describe the current schema. The chain starts at the new v1 (00 decision 33): old-design dev
    /// saves are not migrated.
    ///
    /// Rules for a step (02 section 7.2):
    /// 1. A pure function JObject -> JObject. No ContentDb, no IClock, no file, no current C# type: any value it needs is
    ///    a literal frozen inside the step.
    /// 2. It tolerates absent optional fields and never throws on a shape it can plausibly meet; it fills defaults.
    /// 3. It does not set SchemaVersion; the driver does.
    /// 4. Forward only. No down-migration; a newer save is refused.
    /// 5. Renames are a rename, never drop-and-add. A field may be dropped only when no current code can use it.
    /// 6. A new RNG stream is inserted with its seed derived inside the step from the body's RngRoot (the SHA-256 formula
    ///    of 02 section 3.4 and the stream name as literals) and Counter 0.
    /// 7. A schema change is adding, removing, renaming or retyping a serialized property, renaming an enum member,
    ///    changing a dictionary key format, changing the stream set, or changing a field's meaning. Content changes and
    ///    new keys in Banners/Rng/Items are not.
    /// </summary>
    public static class SaveMigrations
    {
        /// <summary>Steps[i] migrates version i+1 to i+2. Invariant (M1): Steps.Count == SaveSchema.Current - 1.</summary>
        public static readonly IReadOnlyList<Func<JObject, JObject>> Steps = Array.Empty<Func<JObject, JObject>>();

        public static JObject Migrate(int version, JObject body, IMigrationLog log = null) =>
            Migrate(version, body, Steps, SaveSchema.Current, log);

        /// <summary>The driver, parameterised so tests can run it over their own chain. Steps[i] lifts i+1 to i+2.</summary>
        public static JObject Migrate(int version, JObject body, IReadOnlyList<Func<JObject, JObject>> steps, int current, IMigrationLog log = null)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            log ??= NullMigrationLog.Instance;
            if (version > current) throw new SaveTooNewException(version, current);
            if (version < 1) throw new SaveInvalidException($"schema v{version} is below 1 (02 R2)");
            if (steps.Count != current - 1) throw new InvalidOperationException($"migration chain has {steps.Count} steps, expected {current - 1}");
            for (int v = version; v < current; v++)
            {
                body = steps[v - 1](body) ?? throw new InvalidOperationException($"migration step v{v} returned null");
                body["SchemaVersion"] = v + 1;
                log.Applied(v, v + 1);
            }
            return body;
        }
    }
}
