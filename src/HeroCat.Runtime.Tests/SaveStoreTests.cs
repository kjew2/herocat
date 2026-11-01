using System.IO;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    public class SaveStoreTests : TempDirTest
    {
        static long Now => TestContent.T0 + 100;

        static void Write(ISaveStore store, GameState s) => SaveSystem.Write(store, s, Now, TestContent.Db, TestContent.Device, ticking: false);

        [Test]
        public void FileStore_TwoWrites_Rotate_AndStampSaveSeq()
        {
            var store = new FileSaveStore(Dir);
            var s = TestContent.NewState();
            Write(store, s);
            s.Hero.Level = 9;
            Write(store, s);

            Assert.That(File.Exists(store.TempPath), Is.False);
            var r = SaveSystem.Load(store, TestContent.Db, Now);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(r.State.Hero.Level, Is.EqualTo(9));
            Assert.That(r.State.SaveSeq, Is.EqualTo(2));
            Assert.That(r.State.Time.LastSavedServerTime, Is.EqualTo(Now));
            Assert.That(r.State.ContentHash, Is.EqualTo(TestContent.Db.ContentHash));
            var (prev, _, _) = SaveSystem.DecodeFull(store.Read(SaveSlot.Backup), TestContent.Db);
            Assert.That(prev.Hero.Level, Is.EqualTo(1));
            Assert.That(prev.SaveSeq, Is.EqualTo(1));
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
            var s = TestContent.NewState();
            s.Hero.Level = 1;
            Write(store, s);
            s.Hero.Level = 2;
            Write(store, s);
            var bytes = File.ReadAllBytes(store.PrimaryPath);
            bytes[bytes.Length - 5] ^= 0x01;
            File.WriteAllBytes(store.PrimaryPath, bytes);

            var analytics = new MemoryAnalytics();
            var r = SaveSystem.Load(store, TestContent.Db, Now, analytics);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.LoadedFromPrev));
            Assert.That(r.State.Hero.Level, Is.EqualTo(1));
            Assert.That(analytics.Count("save_fallback"), Is.EqualTo(1));
            Assert.That(File.Exists(store.PrimaryPath), Is.False);
            Assert.That(store.QuarantinedFiles(), Has.Length.EqualTo(1));
        }

        [Test]
        public void CrashBetweenRenames_LeavesPrevious_WhichLoads()
        {
            var store = new FileSaveStore(Dir);
            var s = TestContent.NewState();
            Write(store, s);
            store.AfterRotate = () => throw new IOException("power cut");
            s.Hero.Level = 5;
            Assert.Throws<SaveWriteException>(() => Write(store, s));
            var r = SaveSystem.Load(new FileSaveStore(Dir), TestContent.Db, Now);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.LoadedFromPrev));
            Assert.That(r.State.Hero.Level, Is.EqualTo(1));
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
            var session = TestSession.Open(new FakeClock(Now), store);
            var state = session.Sim.State;
            long seqBefore = state.SaveSeq;
            store.FailWrites = true;
            for (int i = 0; i < RuntimeConfig.SAVE_FAIL_WARN_COUNT; i++) Assert.That(session.Runner.SaveNow(SaveTrigger.Periodic), Is.False);
            Assert.That(session.Runner.ConsecutiveSaveFailures, Is.EqualTo(RuntimeConfig.SAVE_FAIL_WARN_COUNT));
            Assert.That(state.SaveSeq, Is.EqualTo(seqBefore + RuntimeConfig.SAVE_FAIL_WARN_COUNT), "stamp runs before the write");
            store.FailWrites = false;
            Assert.That(session.Runner.SaveNow(SaveTrigger.Periodic), Is.True);
            Assert.That(SaveSystem.Load(store, TestContent.Db, Now).State.SaveSeq, Is.EqualTo(state.SaveSeq));
        }
    }
}
