using System.IO;
using System.Linq;
using System.Threading;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>02 section 8 LocalSaveStore (issue #77): acceptance 5-8, SaveStamp, retention, conflict archive.</summary>
    public class SaveStoreTests : TempDirTest
    {
        static long Now => TestContent.T0 + 100;
        static long Day => TestContent.Db.Constants.DaySeconds;

        /// <summary>A store that has loaded (Save needs the content from LoadAsync).</summary>
        static LocalSaveStore Opened(ISaveSlotStore slots, FakeClock clock = null, MemoryAnalytics analytics = null)
        {
            var store = TestContent.Store(slots, clock ?? new FakeClock(Now), analytics);
            store.Load((clock ?? new FakeClock(Now)).NowServerSeconds, TestContent.Db);
            return store;
        }

        static GameState Decode(byte[] bytes) => SaveSystem.DecodeFull(bytes, TestContent.Db).state;

        [Test]
        public void Acceptance5_TwoWrites_Rotate_NoTmp_SaveSeqIs2()
        {
            var slots = new FileSaveStore(Dir);
            var store = Opened(slots);
            var s = TestContent.NewState();
            store.Save(s, SaveTrigger.Periodic);
            s.Hero.Level = 9;
            store.Save(s, SaveTrigger.Periodic);

            Assert.That(File.Exists(slots.TempPath), Is.False);
            var sav = Decode(slots.Read(SaveSlot.Primary));
            var prev = Decode(slots.Read(SaveSlot.Backup));
            Assert.That(sav.Hero.Level, Is.EqualTo(9));
            Assert.That(sav.SaveSeq, Is.EqualTo(2));
            Assert.That(prev.Hero.Level, Is.EqualTo(1));
            Assert.That(prev.SaveSeq, Is.EqualTo(1));

            var r = TestContent.Store(new FileSaveStore(Dir)).Load(Now, TestContent.Db);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(r.State.SaveSeq, Is.EqualTo(2));
            Assert.That(r.Repairs, Is.Empty);
            Assert.That(r.LocalMeta.SaveSeq, Is.EqualTo(2));
        }

        [Test]
        public void MemoryStore_HasTheSameRotation()
        {
            var slots = new MemorySaveStore();
            var store = Opened(slots);
            var s = TestContent.NewState();
            store.Save(s, SaveTrigger.Periodic);
            s.Hero.Level = 4;
            store.Save(s, SaveTrigger.Periodic);
            Assert.That(slots.Temp, Is.Null);
            Assert.That(Decode(slots.Primary).SaveSeq, Is.EqualTo(2));
            Assert.That(Decode(slots.Backup).Hero.Level, Is.EqualTo(1));
        }

        [Test]
        public void SaveStamp_StampsTimeSeqContentAndDevice_AndTheTickAnchorWhileTicking()
        {
            var clock = new FakeClock(Now);
            var store = Opened(new MemorySaveStore(), clock);
            var s = TestContent.NewState();
            s.DeviceId = System.Guid.Empty;
            s.ContentHash = "";
            clock.Set(Now + 50);
            store.Save(s, SaveTrigger.Resume);
            Assert.That(s.Time.LastSavedServerTime, Is.EqualTo(Now + 50));
            Assert.That(s.Time.LastTickServerTime, Is.EqualTo(TestContent.T0), "Resume follows a catch-up; the anchor is not moved");
            Assert.That(s.SaveSeq, Is.EqualTo(1));
            Assert.That(s.ContentVersion, Is.EqualTo(TestContent.Db.Version));
            Assert.That(s.ContentHash, Is.EqualTo(TestContent.Db.ContentHash));
            Assert.That(s.DeviceId, Is.EqualTo(TestContent.Device));
            clock.Set(Now + 80);
            store.Save(s, SaveTrigger.Periodic);
            Assert.That(s.Time.LastTickServerTime, Is.EqualTo(Now + 80), "ticks ran until this save");
            clock.Set(Now + 10);
            store.Save(s, SaveTrigger.Pause);
            Assert.That(s.Time.LastTickServerTime, Is.EqualTo(Now + 80), "never moved backwards");
        }

        [Test]
        public void StrayTemp_IsDeleted_OnLoad()
        {
            var slots = new FileSaveStore(Dir);
            File.WriteAllText(slots.TempPath, "half a save");
            TestContent.Store(slots).Load(Now, TestContent.Db);
            Assert.That(File.Exists(slots.TempPath), Is.False);
            var mem = new MemorySaveStore { Temp = new byte[] { 1 } };
            TestContent.Store(mem).Load(Now, TestContent.Db);
            Assert.That(mem.Temp, Is.Null);
        }

        [Test]
        public void Acceptance6_FlippedByte_LoadsPrev_EmitsFallback_QuarantinesSav()
        {
            var slots = new FileSaveStore(Dir);
            var store = Opened(slots);
            var s = TestContent.NewState();
            store.Save(s, SaveTrigger.Periodic);
            s.Hero.Level = 2;
            store.Save(s, SaveTrigger.Periodic);
            var bytes = File.ReadAllBytes(slots.PrimaryPath);
            bytes[bytes.Length - 5] ^= 0x01;
            File.WriteAllBytes(slots.PrimaryPath, bytes);

            var analytics = new MemoryAnalytics();
            var r = TestContent.Store(slots, analytics: analytics).Load(Now, TestContent.Db);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.LoadedFromPrev));
            Assert.That(r.State.Hero.Level, Is.EqualTo(1));
            Assert.That(analytics.Count("save_fallback"), Is.EqualTo(1));
            Assert.That(analytics.Count("save_reject"), Is.EqualTo(1));
            Assert.That(analytics.Events.First(e => e.Name == "save_reject").Props["file"], Is.EqualTo("slot0.sav"));
            Assert.That(File.Exists(slots.PrimaryPath), Is.False);
            Assert.That(slots.QuarantinedFiles().Select(Path.GetFileName), Is.EqualTo(new[] { $"slot0.corrupt-{Now}.sav" }));
        }

        [Test]
        public void Acceptance6_BothCorrupt_NewProfile_TwoQuarantined_PurgedAfterKeepDays()
        {
            var slots = new FileSaveStore(Dir);
            File.WriteAllBytes(slots.PrimaryPath, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(slots.BackupPath, new byte[] { 4, 5, 6 });
            var analytics = new MemoryAnalytics();
            var r = TestContent.Store(slots, analytics: analytics).Load(Now, TestContent.Db);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.NewProfile));
            Assert.That(r.State, Is.Not.Null);
            Assert.That(r.State.RngRoot, Is.EqualTo(TestContent.Root));
            Assert.That(analytics.Count("save_corrupt"), Is.EqualTo(1));
            Assert.That(analytics.Count("save_reject"), Is.EqualTo(2));
            Assert.That(slots.QuarantinedFiles(), Has.Length.EqualTo(2), "same second: the second name gets a -1 suffix");

            // Exactly CORRUPT_KEEP_DAYS old: kept. One second older: deleted on the next load.
            long keep = SaveConfig.CORRUPT_KEEP_DAYS * Day;
            TestContent.Store(slots).Load(Now + keep, TestContent.Db);
            Assert.That(slots.QuarantinedFiles(), Has.Length.EqualTo(2));
            TestContent.Store(slots).Load(Now + keep + 1, TestContent.Db);
            Assert.That(slots.QuarantinedFiles(), Is.Empty);
        }

        [Test]
        public void MemoryStore_PurgesByAge_LikeTheFileStore()
        {
            var mem = new MemorySaveStore { Primary = new byte[] { 1 }, Backup = new byte[] { 2 } };
            Assert.That(TestContent.Store(mem).Load(Now, TestContent.Db).Status, Is.EqualTo(LoadStatus.NewProfile));
            Assert.That(mem.Quarantined, Has.Count.EqualTo(2));
            TestContent.Store(mem).Load(Now + SaveConfig.CORRUPT_KEEP_DAYS * Day + 1, TestContent.Db);
            Assert.That(mem.Quarantined, Is.Empty);
        }

        [Test]
        public void Acceptance7_CrashBetweenTheRenames_LeavesPrevOnly_WhichLoads()
        {
            var slots = new FileSaveStore(Dir);
            var store = Opened(slots);
            var s = TestContent.NewState();
            store.Save(s, SaveTrigger.Periodic);
            slots.AfterRotate = () => throw new IOException("power cut");
            s.Hero.Level = 5;
            Assert.Throws<SaveWriteException>(() => store.Save(s, SaveTrigger.Periodic));
            Assert.That(File.Exists(slots.PrimaryPath), Is.False);
            Assert.That(File.Exists(slots.BackupPath), Is.True);

            var r = TestContent.Store(new FileSaveStore(Dir)).Load(Now, TestContent.Db);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.LoadedFromPrev));
            Assert.That(r.State.Hero.Level, Is.EqualTo(1));
        }

        [Test]
        public void Acceptance7_MemoryStore_CrashBetweenTheRenames()
        {
            var slots = new MemorySaveStore();
            var store = Opened(slots);
            var s = TestContent.NewState();
            store.Save(s, SaveTrigger.Periodic);
            slots.AfterRotate = () => throw new IOException("power cut");
            Assert.Throws<SaveWriteException>(() => store.Save(s, SaveTrigger.Periodic));
            slots.AfterRotate = null;
            Assert.That(slots.Primary, Is.Null);
            Assert.That(TestContent.Store(slots).Load(Now, TestContent.Db).Status, Is.EqualTo(LoadStatus.LoadedFromPrev));
        }

        [Test]
        public void Acceptance8_TooNew_RenamesNothing_DoesNotReadPrev()
        {
            var slots = new FileSaveStore(Dir);
            var body = SaveSerializer.ToJson(TestContent.NewState());
            body["SchemaVersion"] = SaveSchema.Current + 1;
            File.WriteAllBytes(slots.PrimaryPath, SaveSerializer.EncodeBody(body.ToString()));
            File.WriteAllBytes(slots.BackupPath, new byte[] { 9, 9 });   // corrupt: reading it would emit save_reject
            var analytics = new MemoryAnalytics();
            var r = TestContent.Store(slots, analytics: analytics).Load(Now, TestContent.Db);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.TooNew));
            Assert.That(r.State, Is.Null);
            Assert.That(analytics.Count("save_reject"), Is.EqualTo(0), "prev not consulted");
            Assert.That(File.Exists(slots.PrimaryPath) && File.Exists(slots.BackupPath), Is.True);
            Assert.That(slots.QuarantinedFiles(), Is.Empty);
        }

        [Test]
        public void NoSave_IsANewProfile_WithNothingQuarantined()
        {
            var analytics = new MemoryAnalytics();
            var r = TestContent.Store(new FileSaveStore(Dir), analytics: analytics).Load(Now, TestContent.Db);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.NewProfile));
            Assert.That(r.State.Time.CreatedServerTime, Is.EqualTo(Now));
            Assert.That(analytics.Count("save_corrupt"), Is.EqualTo(0));
            Assert.That(TestContent.Store(new MemorySaveStore()).Load(Now, TestContent.Db).Status, Is.EqualTo(LoadStatus.NewProfile));
        }

        [Test]
        public void Repairs_AreReturned_AndEmittedAsSaveRepaired()
        {
            var slots = new MemorySaveStore();
            var s = TestContent.NewState();
            s.Wallet.Gold = -5;
            s.Tutorial.Step = 999;
            slots.Primary = SaveSerializer.Encode(s);
            var analytics = new MemoryAnalytics();
            var r = TestContent.Store(slots, analytics: analytics).Load(Now, TestContent.Db);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(r.Repairs, Is.EqualTo(new[] { "P2", "P21" }));
            Assert.That(analytics.Events.Where(e => e.Name == "save_repaired").Select(e => e.Props["rule"]), Is.EqualTo(new[] { "P2", "P21" }));
            Assert.That(r.State.Wallet.Gold, Is.EqualTo(0));
        }

        [Test]
        public void InvalidSav_FallsBackToPrev()
        {
            var slots = new MemorySaveStore();
            var good = TestContent.NewState();
            var bad = TestContent.NewState();
            bad.Training.Level = new int[3];   // R5
            slots.Backup = SaveSerializer.Encode(good);
            slots.Primary = SaveSerializer.Encode(bad);
            var r = TestContent.Store(slots).Load(Now, TestContent.Db);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.LoadedFromPrev));
            Assert.That(slots.Quarantined, Has.Count.EqualTo(1));
        }

        [Test]
        public void Load_FloorsTheLocalClock_AtLastSavedServerTime()   // 02 section 11.2 rule 4
        {
            var slots = new MemorySaveStore();
            var s = TestContent.NewState();
            s.Time.LastSavedServerTime = Now + 365 * Day;
            slots.Primary = SaveSerializer.Encode(s);
            var device = new SystemClock { DeviceSeconds = () => Now };   // the device clock was set a year back
            var store = new LocalSaveStore(slots, device, TestContent.Device);
            store.Load(device.NowServerSeconds, TestContent.Db);
            Assert.That(device.NowServerSeconds, Is.GreaterThanOrEqualTo(s.Time.LastSavedServerTime));
            Assert.That(device.IsAuthoritative, Is.False);
        }

        [Test]
        public void WriteFailures_AreCountedAndReported_StampHasRun()
        {
            var slots = new MemorySaveStore { FailWrites = true };
            var analytics = new MemoryAnalytics();
            var store = Opened(slots, analytics: analytics);
            var s = TestContent.NewState();
            Assert.Throws<SaveWriteException>(() => store.Save(s, SaveTrigger.Periodic));
            Assert.Throws<SaveWriteException>(() => store.Save(s, SaveTrigger.Periodic));
            Assert.That(store.ConsecutiveWriteFailures, Is.EqualTo(2));
            Assert.That(analytics.Events.Where(e => e.Name == "save_write_failed").Select(e => e.Props["consecutive"]), Is.EqualTo(new object[] { 1, 2 }));
            Assert.That(s.SaveSeq, Is.EqualTo(2), "memory keeps the stamped state");
            slots.FailWrites = false;
            store.Save(s, SaveTrigger.Periodic);
            Assert.That(store.ConsecutiveWriteFailures, Is.EqualTo(0));
        }

        [Test]
        public void ResolveConflict_ArchivesTheRejected_SavesTheChosen_PurgesAfterConflictKeepDays()
        {
            var slots = new FileSaveStore(Dir);
            var store = Opened(slots);
            var chosen = TestContent.NewState();
            chosen.Hero.Level = 7;
            var rejected = TestContent.NewState();
            rejected.Hero.Level = 3;
            store.ResolveConflictAsync(chosen, rejected, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(Decode(slots.Read(SaveSlot.Primary)).Hero.Level, Is.EqualTo(7));
            Assert.That(slots.ConflictFiles(), Has.Length.EqualTo(1));
            Assert.That(Decode(File.ReadAllBytes(slots.ConflictFiles()[0])).Hero.Level, Is.EqualTo(3));

            TestContent.Store(slots).Load(Now + SaveConfig.CONFLICT_KEEP_DAYS * Day, TestContent.Db);
            Assert.That(slots.ConflictFiles(), Has.Length.EqualTo(1));
            TestContent.Store(slots).Load(Now + SaveConfig.CONFLICT_KEEP_DAYS * Day + 1, TestContent.Db);
            Assert.That(slots.ConflictFiles(), Is.Empty);
        }

        [Test]
        public void LoadAsync_CompletesSynchronously()
        {
            var task = TestContent.Store(new MemorySaveStore()).LoadAsync(Now, TestContent.Db, CancellationToken.None);
            Assert.That(task.IsCompleted, Is.True);
            Assert.That(task.Result.Status, Is.EqualTo(LoadStatus.NewProfile));
        }

        [Test]
        public void ArchiveNames_Parse()
        {
            Assert.That(SaveArchiveNames.Name("slot0", "corrupt", 1700000000, 0), Is.EqualTo("slot0.corrupt-1700000000.sav"));
            Assert.That(SaveArchiveNames.Name("slot0", "conflict", 5, 2), Is.EqualTo("slot0.conflict-5-2.sav"));
            Assert.That(SaveArchiveNames.TryParseUnix("slot0.corrupt-1700000000-1.sav", "slot0", "corrupt", out long t), Is.True);
            Assert.That(t, Is.EqualTo(1700000000));
            Assert.That(SaveArchiveNames.TryParseUnix("slot0.corrupt-x.sav", "slot0", "corrupt", out _), Is.False);
        }

        [Test]
        public void SessionSaveFailures_AreCounted_NotThrown_ThenRecover()
        {
            var store = new MemorySaveStore();
            var session = GameSession.Open(TestContent.Db, GameServices.InMemory(new FakeClock(Now), store));
            long seqBefore = session.State.SaveSeq;
            store.FailWrites = true;
            for (int i = 0; i < SaveConfig.SAVE_FAIL_WARN_COUNT; i++) Assert.That(session.SaveNow(), Is.False);
            Assert.That(session.ShouldWarnSaveFailure, Is.True);
            Assert.That(session.State.SaveSeq, Is.EqualTo(seqBefore + SaveConfig.SAVE_FAIL_WARN_COUNT), "stamp runs before the write");
            store.FailWrites = false;
            Assert.That(session.SaveNow(), Is.True);
            Assert.That(TestContent.Store(store).Load(Now, TestContent.Db).State.SaveSeq, Is.EqualTo(session.State.SaveSeq));
        }
    }
}
