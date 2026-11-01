using System;
using System.IO;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.State;
using HeroCat.Sim.Time;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    public class SaveStoreTests : TempDirTest
    {
        static long Now => TestContent.T0 + 100;

        static void Write(ISaveStore store, GameState s, SaveMeta meta) =>
            SaveSystem.Write(store, s, meta, Now, TestContent.Db.Version);

        [Test]
        public void FileStore_TwoWrites_Rotate()
        {
            var store = new FileSaveStore(Dir);
            var s = TestContent.NewSim().State;
            var meta = new SaveMeta();
            Write(store, s, meta);
            s.Currencies.Gems = 777;
            Write(store, s, meta);

            Assert.That(File.Exists(store.TempPath), Is.False);
            var r = SaveSystem.Load(store, TestContent.Db, Now);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(r.State.Currencies.Gems, Is.EqualTo(777));
            Assert.That(r.Meta.SaveSequence, Is.EqualTo(2));
            var (prev, prevMeta, _) = SaveSystem.DecodeFull(store.Read(SaveSlot.Backup), TestContent.Db);
            Assert.That(prev.Currencies.Gems, Is.EqualTo(0));
            Assert.That(prevMeta.SaveSequence, Is.EqualTo(1));
        }

        [Test]
        public void FileStore_StrayTempIsDeletedOnStartup()
        {
            File.WriteAllText(Path.Combine(Dir, "slot0.tmp"), "half a save");
            var store = new FileSaveStore(Dir);
            Assert.That(File.Exists(store.TempPath), Is.False);
        }

        [Test]
        public void CorruptPrimary_FallsBackToPrevious_AndQuarantinesIt()
        {
            var store = new FileSaveStore(Dir);
            var s = TestContent.NewSim().State;
            var meta = new SaveMeta();
            s.Currencies.Gems = 1;
            Write(store, s, meta);
            s.Currencies.Gems = 2;
            Write(store, s, meta);
            var bytes = File.ReadAllBytes(store.PrimaryPath);
            bytes[bytes.Length - 5] ^= 0x01;   // flip one body bit
            File.WriteAllBytes(store.PrimaryPath, bytes);

            var analytics = new MemoryAnalytics();
            var r = SaveSystem.Load(store, TestContent.Db, Now, analytics);

            Assert.That(r.Status, Is.EqualTo(LoadStatus.LoadedFromBackup));
            Assert.That(r.State.Currencies.Gems, Is.EqualTo(1));
            Assert.That(r.Problems, Has.Count.EqualTo(1));
            Assert.That(analytics.Count("save_fallback"), Is.EqualTo(1));
            Assert.That(File.Exists(store.PrimaryPath), Is.False);
            Assert.That(store.QuarantinedFiles(), Has.Length.EqualTo(1));
        }

        [Test]
        public void BothCorrupt_QuarantinesBoth_AndSessionStartsNewProfile()
        {
            var store = new FileSaveStore(Dir);
            File.WriteAllText(store.PrimaryPath, "not a save");
            File.WriteAllBytes(store.BackupPath, SaveSerializer.Encode(TestContent.NewSim().State, null).AsSpan(0, 40).ToArray());

            var analytics = new MemoryAnalytics();
            var session = GameSession.Open(TestContent.Db, new GameServices
            {
                Clock = new FakeClock(Now), Gacha = new HeroCat.Sim.Gacha.LocalGachaService(), SaveStore = store, Analytics = analytics,
            });

            Assert.That(session.LoadResult.Status, Is.EqualTo(LoadStatus.Corrupt));
            Assert.That(session.IsNewProfile, Is.True);
            Assert.That(store.QuarantinedFiles(), Has.Length.EqualTo(2));
            Assert.That(analytics.Count("save_corrupt"), Is.EqualTo(1));
            // The new profile was saved and loads cleanly.
            Assert.That(SaveSystem.Load(store, TestContent.Db, Now).Status, Is.EqualTo(LoadStatus.Loaded));
        }

        [Test]
        public void SemanticallyInvalidSave_IsRejected()
        {
            var s = TestContent.NewSim().State;
            s.ProfileId = Guid.Empty;
            var store = new MemorySaveStore { Primary = SaveSerializer.Encode(s, new SaveMeta()) };
            var r = SaveSystem.Load(store, TestContent.Db, Now);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.Corrupt));
            Assert.That(r.State, Is.Null);

            var t = TestContent.NewSim().State;
            t.Lifetime.Kills = 99;   // gear_drop counter no longer matches kills: sim invariant breaks
            Assert.Throws<SaveInvalidException>(() => SaveSystem.Validate(t, TestContent.Db));
        }

        [Test]
        public void CrashBetweenRenames_LeavesPrevious_WhichLoads()
        {
            var store = new FileSaveStore(Dir);
            var s = TestContent.NewSim().State;
            var meta = new SaveMeta();
            Write(store, s, meta);
            store.AfterRotate = () => throw new IOException("power cut");
            s.Currencies.Gems = 5;

            Assert.Throws<SaveWriteException>(() => Write(store, s, meta));
            Assert.That(File.Exists(store.PrimaryPath), Is.False);
            Assert.That(File.Exists(store.BackupPath), Is.True);

            var r = SaveSystem.Load(new FileSaveStore(Dir), TestContent.Db, Now);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.LoadedFromBackup));
            Assert.That(r.State.Currencies.Gems, Is.EqualTo(0));
        }

        [Test]
        public void NoSave_ReportsNoSave()
        {
            Assert.That(SaveSystem.Load(new FileSaveStore(Dir), TestContent.Db, Now).Status, Is.EqualTo(LoadStatus.NoSave));
            Assert.That(SaveSystem.Load(new MemorySaveStore(), TestContent.Db, Now).Status, Is.EqualTo(LoadStatus.NoSave));
        }

        [Test]
        public void SessionSaveFailures_AreCounted_NotThrown_ThenRecover()
        {
            var store = new MemorySaveStore();
            var session = GameSession.Open(TestContent.Db, GameServices.InMemory(new FakeClock(Now), store));
            long seqBefore = session.Meta.SaveSequence;
            store.FailWrites = true;
            for (int i = 0; i < SaveConfig.SAVE_FAIL_WARN_COUNT; i++) Assert.That(session.SaveNow(), Is.False);
            Assert.That(session.ShouldWarnSaveFailure, Is.True);
            Assert.That(session.Meta.SaveSequence, Is.EqualTo(seqBefore + SaveConfig.SAVE_FAIL_WARN_COUNT), "stamp runs before the write");

            store.FailWrites = false;
            Assert.That(session.SaveNow(), Is.True);
            Assert.That(session.ConsecutiveSaveFailures, Is.EqualTo(0));
            Assert.That(SaveSystem.Load(store, TestContent.Db, Now).Meta.SaveSequence, Is.EqualTo(session.Meta.SaveSequence));
        }
    }
}
