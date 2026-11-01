using System;
using System.Collections.Generic;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using Newtonsoft.Json.Linq;

namespace HeroCat.Runtime.Save
{
    /// <summary>
    /// Stateless steps of the save pipeline (02 sections 6-8) shared by <see cref="LocalSaveStore"/>, the self-check, the
    /// bots and tests: decode one file, migrate, materialize, run the reject rules. Loading with fallback, repairs and
    /// quarantine is LocalSaveStore's.
    /// </summary>
    public static class SaveSystem
    {
        /// <summary>
        /// Decode + migrate + materialize + the reject rules R1-R5 for one file. No repairs (they need <c>now</c> and the
        /// install id: <see cref="SaveValidator.Run"/>). Throws SaveCorrupt / SaveInvalid / SaveTooNew.
        /// </summary>
        public static (GameState state, int fromVersion, bool hmacValid) DecodeFull(byte[] bytes, ContentDb db, IAnalytics analytics = null,
                                                                                   byte[] deviceKey = null)
        {
            var file = SaveSerializer.Decode(bytes, deviceKey);
            if (file.SchemaVersion > SaveSchema.Current) throw new SaveTooNewException(file.SchemaVersion, SaveSchema.Current);
            var state = MigrateAndMaterialize(file, analytics);
            Validate(state, db);
            return (state, file.SchemaVersion, file.HmacValid);
        }

        /// <summary>Migrate a decoded file to the current version and materialize it. Throws SaveInvalid / SaveTooNew.</summary>
        public static GameState MigrateAndMaterialize(SaveFile file, IAnalytics analytics = null)
        {
            JObject body;
            try
            {
                body = SaveMigrations.Migrate(file.SchemaVersion, file.Body, new AnalyticsMigrationLog(analytics ?? NullAnalytics.Instance));
            }
            catch (Exception e) when (!(e is SaveTooNewException) && !(e is SaveInvalidException))
            {
                throw new SaveInvalidException($"migration from v{file.SchemaVersion} failed: {e.Message}", e);
            }
            return SaveSerializer.Materialize(body);
        }

        /// <summary>The structural reject rules R2-R5 (02 section 6.4). Throws <see cref="SaveInvalidException"/>.</summary>
        public static void Validate(GameState s, ContentDb db) => SaveValidator.Reject(s, db);

        /// <summary>SaveStamp (02 section 8.2). Kept for callers outside the store (the bots' save fixture).</summary>
        public static void Stamp(GameState state, long nowServerSec, ContentDb db, Guid installId) =>
            SaveStamp.Apply(state, nowServerSec, db, installId);

        sealed class AnalyticsMigrationLog : IMigrationLog
        {
            readonly IAnalytics _a;
            public AnalyticsMigrationLog(IAnalytics a) { _a = a; }

            public void Applied(int from, int to) => _a.Track(new AnalyticsEvents.MigrationApplied(from, to));
        }
    }
}
