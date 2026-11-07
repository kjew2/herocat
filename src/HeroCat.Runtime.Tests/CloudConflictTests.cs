using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Boot;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>02 sections 9.3-9.4 (issue #153): the conflict rule (acceptance 12), suspended uploads (13), the resolution.</summary>
    public class CloudConflictTests
    {
        static readonly Guid DevL = TestContent.Device;
        static readonly Guid DevR = new Guid("de71ce00-0000-4000-8000-0000000000b2");
        static readonly Guid DevR2 = new Guid("de71ce00-0000-4000-8000-0000000000c3");
        static long Now => TestContent.T0 + 10_000;
        const long Cost = 300;

        // ------------------------------------------------------------------------------------------------ acceptance 12

        static SaveMeta M(Guid device, long seq, long t, bool auth = true, string sha = null, double score = 1000, long pulls = 10,
                          long diamonds = 100) => new SaveMeta
        {
            DeviceId = device, SaveSeq = seq, LastSavedServerTime = t, ClockAuthoritative = auth,
            Sha256 = sha ?? $"{device:N}-{seq}-{t}", ProgressScore = score,
            Summary = new SaveSummary { TotalPulls = pulls, Diamonds = diamonds },
        };

        static IEnumerable<TestCaseData> Table()
        {
            const long T = 1_800_000_000;
            TestCaseData C(string name, SaveMeta l, SaveMeta r, ConflictBranch b, SaveSide w, LoadStatus st,
                           ConflictReason why = ConflictReason.None) => new TestCaseData(l, r, b, w, st, why).SetName("Resolve_" + name);

            // 1. only one exists
            yield return C("OnlyLocal", M(DevL, 5, T), null, ConflictBranch.OnlyLocal, SaveSide.Local, LoadStatus.Loaded);
            yield return C("OnlyRemote", null, M(DevR, 5, T), ConflictBranch.OnlyRemote, SaveSide.Remote, LoadStatus.Loaded);
            // 2. equal sha256 -> L, whatever else differs
            yield return C("SameSha", M(DevL, 5, T, sha: "ab"), M(DevR, 9, T + 99, sha: "AB", score: 9e9),
                           ConflictBranch.SameContent, SaveSide.Local, LoadStatus.Loaded);
            // 3. same device: higher saveSeq, silently, even with far more progress on the loser
            yield return C("SameDevice_RemoteSeqHigher", M(DevL, 5, T + 50), M(DevL, 6, T), ConflictBranch.SameDevice, SaveSide.Remote, LoadStatus.Loaded);
            yield return C("SameDevice_LocalSeqHigher", M(DevL, 7, T), M(DevL, 6, T + 50, score: 9e9, pulls: 999),
                           ConflictBranch.SameDevice, SaveSide.Local, LoadStatus.Loaded);
            yield return C("SameDevice_EqualSeq_KeepsLocal", M(DevL, 6, T), M(DevL, 6, T + 1), ConflictBranch.SameDevice, SaveSide.Local, LoadStatus.Loaded);
            // 4. different devices: higher stamp
            yield return C("Devices_RemoteLater", M(DevL, 50, T), M(DevR, 5, T + 1), ConflictBranch.DifferentDevices, SaveSide.Remote, LoadStatus.Loaded);
            yield return C("Devices_LocalLater", M(DevL, 5, T + 1), M(DevR, 50, T), ConflictBranch.DifferentDevices, SaveSide.Local, LoadStatus.Loaded);
            // a non-authoritative stamp never beats an authoritative one (acceptance 12: local 1 h ahead loses)
            yield return C("Devices_LocalNonAuthHourAhead_LosesToAuthRemote", M(DevL, 5, T + 3600, auth: false), M(DevR, 5, T),
                           ConflictBranch.DifferentDevices, SaveSide.Remote, LoadStatus.Loaded);
            yield return C("Devices_RemoteNonAuthAhead_LosesToAuthLocal", M(DevL, 5, T), M(DevR, 5, T + 3600, auth: false),
                           ConflictBranch.DifferentDevices, SaveSide.Local, LoadStatus.Loaded);
            yield return C("Devices_BothNonAuth_HigherStamp", M(DevL, 5, T + 10, auth: false), M(DevR, 5, T, auth: false),
                           ConflictBranch.DifferentDevices, SaveSide.Local, LoadStatus.Loaded);
            // tie: higher saveSeq, then R
            yield return C("Devices_Tie_HigherSeq", M(DevL, 8, T), M(DevR, 7, T), ConflictBranch.DifferentDevices, SaveSide.Local, LoadStatus.Loaded);
            yield return C("Devices_Tie_EqualSeq_Remote", M(DevL, 7, T), M(DevR, 7, T), ConflictBranch.DifferentDevices, SaveSide.Remote, LoadStatus.Loaded);
            // the loser raises the modal
            yield return C("Devices_LoserProgressAboveMargin_Conflict", M(DevL, 5, T, score: 1101), M(DevR, 5, T + 1, score: 1000),
                           ConflictBranch.DifferentDevices, SaveSide.Remote, LoadStatus.Conflict, ConflictReason.ProgressScore);
            yield return C("Devices_LoserProgressAtMargin_Silent", M(DevL, 5, T, score: 1100), M(DevR, 5, T + 1, score: 1000),
                           ConflictBranch.DifferentDevices, SaveSide.Remote, LoadStatus.Loaded);
            yield return C("Devices_LoserMorePulls_Conflict", M(DevL, 5, T + 1, pulls: 10), M(DevR, 5, T, pulls: 11),
                           ConflictBranch.DifferentDevices, SaveSide.Local, LoadStatus.Conflict, ConflictReason.TotalPulls);
            yield return C("Devices_LoserEqualPulls_Silent", M(DevL, 5, T + 1, pulls: 10), M(DevR, 5, T, pulls: 10),
                           ConflictBranch.DifferentDevices, SaveSide.Local, LoadStatus.Loaded);
            yield return C("Devices_LoserDiamondsOnePullAhead_Conflict", M(DevL, 5, T, diamonds: 100 + Cost), M(DevR, 5, T + 1, diamonds: 100),
                           ConflictBranch.DifferentDevices, SaveSide.Remote, LoadStatus.Conflict, ConflictReason.Diamonds);
            yield return C("Devices_LoserDiamondsJustUnderAPull_Silent", M(DevL, 5, T, diamonds: 99 + Cost), M(DevR, 5, T + 1, diamonds: 100),
                           ConflictBranch.DifferentDevices, SaveSide.Remote, LoadStatus.Loaded);
            // the modal preselects the authority winner even when the loser's stamp is later
            yield return C("Devices_NonAuthLoserWithMoreProgress_Conflict", M(DevL, 5, T + 3600, auth: false, score: 5000), M(DevR, 5, T),
                           ConflictBranch.DifferentDevices, SaveSide.Remote, LoadStatus.Conflict, ConflictReason.ProgressScore);
        }

        [TestCaseSource(nameof(Table))]
        public void Acceptance12(SaveMeta l, SaveMeta r, ConflictBranch branch, SaveSide winner, LoadStatus status, ConflictReason why)
        {
            var d = ConflictRule.Resolve(l, r, Cost);
            Assert.That(d.Branch, Is.EqualTo(branch), d.ToString());
            Assert.That(d.Winner, Is.EqualTo(winner), d.ToString());
            Assert.That(d.Status, Is.EqualTo(status), d.ToString());
            Assert.That(d.Reason, Is.EqualTo(why), d.ToString());
            Assert.That(d.AutoResolved, Is.EqualTo(branch == ConflictBranch.DifferentDevices && status == LoadStatus.Loaded));
        }

        [Test]
        public void Resolve_NeedsACandidate_AndPullCostComesFromContent()
        {
            Assert.Throws<ArgumentException>(() => ConflictRule.Resolve(null, null, Cost));
            long cost = ConflictRule.WeaponPullCost(TestContent.Db);
            Assert.That(cost, Is.EqualTo(TestContent.Db.Banners[Banner.WEAPON].PullCostDiamonds).And.GreaterThan(0));
            var d = ConflictRule.Resolve(M(DevL, 5, 0, diamonds: cost), M(DevR, 5, 1, diamonds: 0), TestContent.Db);
            Assert.That(d.Reason, Is.EqualTo(ConflictReason.Diamonds));
        }

        // ------------------------------------------------------------------------------------------------ harness

        /// <summary>A fake <c>/save</c>: one stored version, PUTs recorded, If-Match checked like 08 section 3.1 step 3.</summary>
        sealed class FakeCloud : ICloudSaveApi
        {
            public SaveMeta Meta;
            public byte[] Blob;
            public bool Offline;
            public int MetaCalls, BlobCalls;
            public readonly List<SaveUpload> Puts = new List<SaveUpload>();

            /// <summary>Runs before a PUT is checked: a test hook to let a "third device" write first.</summary>
            public Action<SaveUpload> BeforePut;
            int _n;

            public void Store(GameState s, string etag, string model = null)
            {
                Blob = SaveSerializer.Encode(s);
                Meta = MetaOf(s, Blob);
                Meta.Etag = etag;
                Meta.DeviceModel = model;
            }

            public Task<SaveMeta> GetMetaAsync(CancellationToken ct)
            {
                MetaCalls++;
                if (Offline) throw new ServiceException(ErrorClass.Offline, "NET_OFFLINE");
                return Task.FromResult(Meta);
            }

            public Task<byte[]> GetBlobAsync(CancellationToken ct)
            {
                BlobCalls++;
                if (Offline) throw new ServiceException(ErrorClass.Offline, "NET_OFFLINE");
                return Task.FromResult(Blob);
            }

            public Task<SaveUploadResult> PutAsync(SaveUpload u, CancellationToken ct)
            {
                if (Offline) throw new ServiceException(ErrorClass.Offline, "NET_OFFLINE");
                BeforePut?.Invoke(u);
                BeforePut = null;
                Puts.Add(u);
                if (Meta != null ? u.IfMatch != Meta.Etag : u.IfMatch != "*")
                    throw new ServiceException(ErrorClass.Conflict, "SAVE_CONFLICT", status: 409);
                Blob = u.Bytes;
                Meta = u.Meta;
                Meta.Etag = $"W/\"{u.Meta.SaveSeq}-{++_n}\"";
                return Task.FromResult(new SaveUploadResult(true, Meta.Etag, u.Meta.SaveSeq, Now, Array.Empty<string>()));
            }
        }

        /// <summary>The test MetaFactory: progressScore = Party.Level * 10, summary from the state, authoritative stamps.</summary>
        static SaveMeta MetaOf(GameState s, byte[] bytes)
        {
            var m = SaveMeta.Of(s, SaveSerializer.Decode(bytes), bytes.Length);
            m.ClockAuthoritative = true;
            m.ProgressScore = s.Party.Level * 10;
            m.Summary = new SaveSummary
            {
                Stage = s.Progress.HighestCleared, PartyLevel = s.Party.Level, Diamonds = s.Wallet.Diamond,
                TotalPulls = s.Banners.Values.Sum(b => b?.TotalPulls ?? 0), HighestFish = s.Fish.HighestLevel,
            };
            return m;
        }

        static GameState RemoteState(Guid device, long seq, long t, int level)
        {
            var s = TestContent.NewState();
            s.DeviceId = device;
            s.SaveSeq = seq;
            s.Time.LastSavedServerTime = t;
            s.Party.Level = level;
            return s;
        }

        sealed class Rig
        {
            public FakeClock Clock = new FakeClock(Now);
            public MemorySaveStore Slots = new MemorySaveStore();
            public MemoryAnalytics Analytics = new MemoryAnalytics();
            public FakeCloud Cloud = new FakeCloud();
            public LocalSaveStore Local;
            public CloudConflictResolver Resolver;

            public Rig(int localLevel = 0, long localSavedAt = 0)
            {
                if (localLevel > 0)   // a local save on disk, stamped at localSavedAt
                {
                    var pre = TestContent.Store(Slots, new FakeClock(localSavedAt));
                    pre.Load(localSavedAt, TestContent.Db);
                    var s = TestContent.NewState();
                    s.Party.Level = localLevel;
                    pre.Save(s, SaveTrigger.Periodic);
                }
                Local = TestContent.Store(Slots, Clock, Analytics);
                Resolver = new CloudConflictResolver(Local, Cloud, Clock, Analytics) { MetaFactory = MetaOf };
            }

            public LoadResult Boot() =>
                Resolver.ResolveAtBootAsync(Local.Load(Clock.NowServerSeconds, TestContent.Db), CancellationToken.None).GetAwaiter().GetResult();

            public List<MemoryAnalytics.Entry> Events(string name) => Analytics.Events.Where(e => e.Name == name).ToList();
        }

        static GameState Decode(byte[] b) => SaveSystem.DecodeFull(b, TestContent.Db).state;

        // ------------------------------------------------------------------------------------------------ boot

        [Test]
        public void Boot_RemoteOnly_IsWrittenLocally_Loaded()
        {
            var rig = new Rig();
            rig.Cloud.Store(RemoteState(DevR, 40, Now - 100, 12), "e1");
            var r = rig.Boot();
            Assert.That(r.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(r.State.Party.Level, Is.EqualTo(12));
            Assert.That(Decode(rig.Slots.Primary).Party.Level, Is.EqualTo(12), "R only: write locally");
            Assert.That(rig.Resolver.UploadsSuspended, Is.False);
            Assert.That(rig.Resolver.ServerEtag, Is.EqualTo("e1"));
            Assert.That(rig.Events(AnalyticsNames.CLOUD_AUTORESOLVED), Is.Empty, "step 1 is not an autoresolve");
        }

        [Test]
        public void Boot_NoServerSave_Or_Offline_RunsOnLocal()
        {
            var rig = new Rig(localLevel: 3, localSavedAt: Now - 50);
            var r = rig.Boot();
            Assert.That(r.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(r.State.Party.Level, Is.EqualTo(3));

            var off = new Rig(localLevel: 3, localSavedAt: Now - 50);
            off.Cloud.Store(RemoteState(DevR, 40, Now, 30), "e1");
            off.Cloud.Offline = true;
            var o = off.Boot();
            Assert.That(o.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(o.State.Party.Level, Is.EqualTo(3));
            Assert.That(off.Resolver.UploadsSuspended, Is.False);
        }

        [Test]
        public void Boot_RemoteLaterWithoutMoreProgress_Autoresolves_ToRemote_AboveBothSeqs()
        {
            var rig = new Rig(localLevel: 5, localSavedAt: Now - 500);
            rig.Cloud.Store(RemoteState(DevR, 1, Now - 100, 5), "e1");   // later stamp, same progress, lower seq
            var r = rig.Boot();
            Assert.That(r.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(r.State.DeviceId, Is.EqualTo(DevR));
            Assert.That(r.State.SaveSeq, Is.EqualTo(1), "raised to max(L, R) = max(1, 1)");
            var ev = rig.Events(AnalyticsNames.CLOUD_AUTORESOLVED);
            Assert.That(ev, Has.Count.EqualTo(1));
            Assert.That(ev[0].Props["winnerSide"], Is.EqualTo("remote"));
            Assert.That(Decode(rig.Slots.Backup).Party.Level, Is.EqualTo(5), "L rotated to prev, never lost outright");
        }

        [Test]
        public void Boot_LoserWithMoreProgress_IsAConflict_RunsOnLocal_UploadsSuspended()
        {
            var rig = new Rig(localLevel: 2, localSavedAt: Now - 100);
            rig.Cloud.Store(RemoteState(DevR, 900, Now - 3600, 40), "e1", "Pixel 9");   // older stamp, far more progress
            PendingConflict raised = null;
            rig.Resolver.ConflictRaised += p => raised = p;
            var r = rig.Boot();

            Assert.That(r.Status, Is.EqualTo(LoadStatus.Conflict));
            Assert.That(r.State.Party.Level, Is.EqualTo(2), "the game runs on L until the player picks");
            Assert.That(r.Other.Party.Level, Is.EqualTo(40));
            Assert.That(rig.Resolver.UploadsSuspended, Is.True);
            Assert.That(raised, Is.SameAs(rig.Resolver.Pending));
            Assert.That(raised.Decision.Winner, Is.EqualTo(SaveSide.Local));
            Assert.That(raised.Decision.Reason, Is.EqualTo(ConflictReason.ProgressScore));
            Assert.That(rig.Events(AnalyticsNames.CLOUD_AUTORESOLVED), Is.Empty);
            Assert.That(Decode(rig.Slots.Primary).Party.Level, Is.EqualTo(2), "nothing written before the choice");
        }

        // ------------------------------------------------------------------------------------------------ resolution

        static Rig ConflictRig()
        {
            var rig = new Rig(localLevel: 2, localSavedAt: Now - 100);
            rig.Cloud.Store(RemoteState(DevR, 900, Now - 3600, 40), "e1", "Pixel 9");
            Assert.That(rig.Boot().Status, Is.EqualTo(LoadStatus.Conflict));
            return rig;
        }

        [Test]
        public void ChooseOtherDevice_WritesIt_ArchivesThisOne_UploadsWithIfMatchR_ReplacesTheRunningState()
        {
            var rig = ConflictRig();
            rig.Clock.Set(Now + 60);
            var o = rig.Resolver.ResolveConflictAsync(SaveSide.Remote, CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(o.Kind, Is.EqualTo(ConflictOutcomeKind.Resolved));
            Assert.That(o.ReplacesRunningState, Is.True);
            Assert.That(o.Uploaded, Is.True);
            Assert.That(o.State.Party.Level, Is.EqualTo(40));
            var written = Decode(rig.Slots.Primary);
            Assert.That(written.Party.Level, Is.EqualTo(40));
            Assert.That(written.DeviceId, Is.EqualTo(DevL), "this device's stamp");
            Assert.That(written.SaveSeq, Is.EqualTo(901), "above both lineages");
            var archived = rig.Slots.Archive.Single(a => a.Kind == "conflict");
            Assert.That(archived.ServerTime, Is.EqualTo(Now + 60), "slot0.conflict-<now>.sav");
            Assert.That(Decode(archived.Bytes).Party.Level, Is.EqualTo(2));

            Assert.That(rig.Cloud.Puts, Has.Count.EqualTo(1));
            Assert.That(rig.Cloud.Puts[0].IfMatch, Is.EqualTo("e1"), "If-Match: R.etag");
            Assert.That(rig.Cloud.Puts[0].Trigger, Is.EqualTo(SaveTrigger.Conflict));
            Assert.That(rig.Resolver.UploadsSuspended, Is.False);
            Assert.That(rig.Resolver.ServerEtag, Is.EqualTo(rig.Cloud.Meta.Etag));

            var ev = rig.Events(AnalyticsNames.CLOUD_CONFLICT).Single().Props;
            Assert.That(ev["winnerSide"], Is.EqualTo("local"));
            Assert.That(ev["chosenSide"], Is.EqualTo("remote"));
            Assert.That(ev["scoreL"], Is.EqualTo(20.0));
            Assert.That(ev["scoreR"], Is.EqualTo(400.0));
            Assert.That(ev["secondsApart"], Is.EqualTo(3500L));
        }

        [Test]
        public void KeepThisDevice_ArchivesTheOther_KeepsTheRunningState()
        {
            var rig = ConflictRig();
            var p = rig.Resolver.Pending;
            var o = rig.Resolver.ResolveConflictAsync(p.Local, p.Remote, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(o.Chosen, Is.EqualTo(SaveSide.Local));
            Assert.That(o.ReplacesRunningState, Is.False);
            Assert.That(Decode(rig.Slots.Primary).Party.Level, Is.EqualTo(2));
            Assert.That(Decode(rig.Slots.Conflicts.Single()).Party.Level, Is.EqualTo(40));
            Assert.That(rig.Cloud.Meta.Summary.PartyLevel, Is.EqualTo(2), "the chosen save is the server's now");
            Assert.That(rig.Events(AnalyticsNames.CLOUD_CONFLICT).Single().Props["chosenSide"], Is.EqualTo("local"));
        }

        [Test]
        public void SecondConflict409_ResolvesAgain_NeverOverwrites()
        {
            var rig = ConflictRig();
            // A third device uploads (a lot of progress, newest stamp) just before our resolution PUT lands.
            rig.Cloud.BeforePut = _ => rig.Cloud.Store(RemoteState(DevR2, 7, Now + 30, 60), "e2", "iPad");
            var o = rig.Resolver.ResolveConflictAsync(SaveSide.Local, CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(rig.Cloud.Meta.Etag, Is.EqualTo("e2"), "the third device's save was not overwritten");
            Assert.That(rig.Cloud.Puts, Has.Count.EqualTo(1));
            // R2 is later with far more progress than L (level 2): R2 wins silently (no override for the loser L).
            Assert.That(o.Kind, Is.EqualTo(ConflictOutcomeKind.Resolved));
            Assert.That(o.ReplacesRunningState, Is.True);
            Assert.That(o.State.Party.Level, Is.EqualTo(60));
            Assert.That(Decode(rig.Slots.Primary).Party.Level, Is.EqualTo(60));
        }

        [Test]
        public void SecondConflict409_WithAConflictingThirdDevice_RaisesTheModalAgain()
        {
            var rig = ConflictRig();
            // The third device's save has the later stamp but less progress than the chosen one (level 40 vs 3).
            rig.Cloud.BeforePut = _ => rig.Cloud.Store(RemoteState(DevR2, 7, Now + 30, 3), "e2", "iPad");
            var o = rig.Resolver.ResolveConflictAsync(SaveSide.Remote, CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(o.Kind, Is.EqualTo(ConflictOutcomeKind.ConflictAgain));
            Assert.That(rig.Resolver.UploadsSuspended, Is.True);
            Assert.That(rig.Resolver.Pending.Remote.Party.Level, Is.EqualTo(3));
            Assert.That(rig.Resolver.Pending.Local.Party.Level, Is.EqualTo(40), "the first choice is now L");
            Assert.That(rig.Resolver.Pending.RemoteMeta.Etag, Is.EqualTo("e2"));
            Assert.That(rig.Cloud.Meta.Etag, Is.EqualTo("e2"), "no silent overwrite");
            Assert.That(rig.Cloud.Puts, Has.Count.EqualTo(1));
        }

        [Test]
        public void SecondConflict409_ChosenStillWinsSilently_UploadsOverTheNewVersion()
        {
            var rig = ConflictRig();
            // The third device's save is older with the same progress as the chosen one: L wins silently, PUT again on e2.
            rig.Cloud.BeforePut = _ => rig.Cloud.Store(RemoteState(DevR2, 7, Now - 7200, 2), "e2");
            var o = rig.Resolver.ResolveConflictAsync(SaveSide.Local, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(o.Kind, Is.EqualTo(ConflictOutcomeKind.Resolved));
            Assert.That(o.Uploaded, Is.True);
            Assert.That(rig.Cloud.Puts.Select(p => p.IfMatch), Is.EqualTo(new[] { "e1", "e2" }));
            Assert.That(rig.Cloud.Meta.Summary.PartyLevel, Is.EqualTo(2));
        }

        [Test]
        public void Resolve_Offline_KeepsTheChoiceLocally_AndReopensUploads()
        {
            var rig = ConflictRig();
            rig.Cloud.Offline = true;
            var o = rig.Resolver.ResolveConflictAsync(SaveSide.Remote, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(o.Kind, Is.EqualTo(ConflictOutcomeKind.Resolved));
            Assert.That(o.Uploaded, Is.False);
            Assert.That(rig.Resolver.UploadsSuspended, Is.False);
            Assert.That(rig.Resolver.ServerEtag, Is.EqualTo("e1"), "the queue uploads with If-Match R.etag");
            Assert.That(Decode(rig.Slots.Primary).Party.Level, Is.EqualTo(40));
        }

        // ------------------------------------------------------------------------------------------------ resume

        [Test]
        public void Resume_SameEtag_FetchesMetaOnly()
        {
            var rig = new Rig(localLevel: 2, localSavedAt: Now - 100);
            rig.Cloud.Store(RemoteState(DevL, 1, Now - 200, 2), "e1");
            var running = rig.Boot().State;
            int blobs = rig.Cloud.BlobCalls;
            var c = rig.Resolver.CheckAsync(running, force: false, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(c.Kind, Is.EqualTo(ConflictCheckKind.KeepLocal));
            Assert.That(rig.Cloud.BlobCalls, Is.EqualTo(blobs), "blob on demand only");
        }

        [Test]
        public void Resume_NewServerVersion_LocalWins_NoBlob_ConflictingVersion_RaisesIt()
        {
            var rig = new Rig(localLevel: 5, localSavedAt: Now - 100);
            var running = rig.Boot().State;
            rig.Cloud.Store(RemoteState(DevR, 3, Now - 5000, 5), "e1");   // older, no more progress: L wins, meta only
            var c = rig.Resolver.CheckAsync(running, false, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(c.Kind, Is.EqualTo(ConflictCheckKind.KeepLocal));
            Assert.That(rig.Cloud.BlobCalls, Is.EqualTo(0));
            Assert.That(rig.Resolver.ServerEtag, Is.EqualTo("e1"), "the next upload overwrites R");

            rig.Cloud.Store(RemoteState(DevR, 4, Now - 4000, 90), "e2");  // older but far more progress: the modal
            c = rig.Resolver.CheckAsync(running, false, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(c.Kind, Is.EqualTo(ConflictCheckKind.Conflict));
            Assert.That(rig.Resolver.Pending.Local, Is.SameAs(running));
            Assert.That(rig.Cloud.BlobCalls, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------------------------------------ acceptance 13

        /// <summary>A minimal stand-in for the CloudSaveClient (#152): every Save writes locally, then uploads through the gate.</summary>
        sealed class UploadingStore : ISaveStore
        {
            readonly Rig _rig;
            readonly GatedCloudSaveApi _api;
            public int Suspended;

            public UploadingStore(Rig rig)
            {
                _rig = rig;
                _api = new GatedCloudSaveApi(rig.Cloud, rig.Resolver);
            }

            public async Task<LoadResult> LoadAsync(long now, ContentDb db, CancellationToken ct) =>
                await _rig.Resolver.ResolveAtBootAsync(await _rig.Local.LoadAsync(now, db, ct), ct);

            public void Save(GameState state, SaveTrigger trigger)
            {
                _rig.Local.Save(state, trigger);
                var bytes = _rig.Slots.Read(SaveSlot.Primary);
                var r = _api.PutAsync(new SaveUpload(bytes, MetaOf(state, bytes), _rig.Resolver.ServerEtag ?? "*", trigger),
                                      CancellationToken.None).GetAwaiter().GetResult();
                if (!r.Sent) Suspended++;
                else _rig.Resolver.ObserveServerEtag(r.Etag);
            }

            public Task ResolveConflictAsync(GameState chosen, GameState rejected, CancellationToken ct) =>
                _rig.Resolver.ResolveConflictAsync(chosen, rejected, ct);
        }

        [Test]
        public void Acceptance13_NoPutWhileConflicted_AcrossPeriodicAndPauseSaves()
        {
            var rig = new Rig(localLevel: 2, localSavedAt: TestContent.T0 - 100);
            rig.Clock.Set(TestContent.T0);
            rig.Cloud.Store(RemoteState(DevR, 900, TestContent.T0 - 3600, 40), "e1");
            var store = new UploadingStore(rig);
            var mono = new FakeMonoClock();
            var boot = BootFlow.Run(new BootOptions
            {
                Content = new Func<ContentDb>[] { () => TestContent.Db },
                Services = (db, state) => GameServices.Build(BackendMode.Local, new ServicesOptions
                {
                    Clock = rig.Clock, Saves = store, Slots = rig.Slots, Analytics = rig.Analytics, State = state, Content = db,
                    InstallId = TestContent.Device,
                }),
                NewProfileRngRoot = TestContent.Root,
                Runner = new SessionRunnerOptions { Mono = mono, TickBudgetMs = 0, AutosaveIntervalSec = 5 },
                Mono = mono,
            });
            Assert.That(boot.Outcome, Is.EqualTo(BootOutcome.Ready), boot.Error?.Code);
            Assert.That(boot.LoadStatus, Is.EqualTo(LoadStatus.Conflict));
            Assert.That(boot.Sim.State.Party.Level, Is.EqualTo(2), "runs on L");

            int writes = rig.Slots.WriteCount;
            for (int i = 0; i < 30; i++)   // periodic autosaves
            {
                mono.Advance(1);
                rig.Clock.Advance(1);
                boot.Runner.OnFrame();
            }
            boot.Runner.OnPause();
            boot.Runner.OnResume();
            boot.Runner.OnQuit();
            Assert.That(rig.Slots.WriteCount, Is.GreaterThan(writes + 3), "local saves keep happening");
            Assert.That(store.Suspended, Is.GreaterThan(3));
            Assert.That(rig.Cloud.Puts, Is.Empty, "no PUT while the conflict is unresolved");
            Assert.That(rig.Cloud.Meta.Summary.PartyLevel, Is.EqualTo(40), "the loser is intact on the server");

            var o = rig.Resolver.ResolveConflictAsync(SaveSide.Local, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(o.Uploaded, Is.True);
            Assert.That(rig.Cloud.Puts, Has.Count.EqualTo(1));
            Assert.That(rig.Cloud.Puts[0].IfMatch, Is.EqualTo("e1"));
            store.Save(boot.Sim.State, SaveTrigger.Periodic);
            Assert.That(rig.Cloud.Puts, Has.Count.EqualTo(2), "uploads resume after the choice");
        }

        // ------------------------------------------------------------------------------------------------ the modal

        [Test]
        public void Modal_TwoCards_WinnerRecommended_LossLineOnlyForTheOtherDevice()
        {
            var rig = ConflictRig();
            var m = ConflictModalModel.From(rig.Resolver.Pending, "iPhone 15");
            Assert.That(m.Recommended, Is.EqualTo(SaveSide.Local));
            Assert.That(m.ThisDevice.Recommended, Is.True);
            Assert.That(m.OtherDevice.Recommended, Is.False);
            Assert.That(m.ThisDevice.IsThisDevice, Is.True);
            Assert.That(m.ThisDevice.DeviceModel, Is.EqualTo("iPhone 15"));
            Assert.That(m.OtherDevice.DeviceModel, Is.EqualTo("Pixel 9"));
            Assert.That(m.OtherDevice.PartyLevel, Is.EqualTo(40));
            Assert.That(m.ThisDevice.PartyLevel, Is.EqualTo(2));
            Assert.That(m.OtherDevice.LastPlayedServerTime, Is.EqualTo(Now - 3600));
            Assert.That(m.OtherDevice.LastPlayedAgoSec(Now), Is.EqualTo(3600));
            Assert.That(m.ShowsLossLine(SaveSide.Remote), Is.True);
            Assert.That(m.ShowsLossLine(SaveSide.Local), Is.False);
            Assert.That(m.LocalProgressSinceServerTime, Is.EqualTo(Now), "since L was loaded");
        }

        [TestCase(true, false, false, false, false, ExpectedResult = true)]
        [TestCase(false, false, false, false, false, ExpectedResult = false)]
        [TestCase(true, true, false, false, false, ExpectedResult = false)]
        [TestCase(true, false, true, false, false, ExpectedResult = false)]
        [TestCase(true, false, false, true, false, ExpectedResult = false)]
        [TestCase(true, false, false, false, true, ExpectedResult = false)]
        public bool Modal_ShownOnlyOnMainIdle(bool main, bool boss, bool reveal, bool dungeon, bool modal) =>
            ConflictModalRules.CanShow(new ConflictModalContext(main, boss, reveal, dungeon, modal));

        [Test]
        public void Gate_DropsEveryPutWhileSuspended_ReadsPassThrough()
        {
            var rig = ConflictRig();
            var gated = new GatedCloudSaveApi(rig.Cloud, rig.Resolver);
            foreach (var t in new[] { SaveTrigger.Periodic, SaveTrigger.Pause, SaveTrigger.Quit, SaveTrigger.Summon })
                Assert.That(gated.PutAsync(new SaveUpload(new byte[1], new SaveMeta(), "e1", t), CancellationToken.None).Result.Sent, Is.False);
            Assert.That(rig.Cloud.Puts, Is.Empty);
            Assert.That(gated.GetMetaAsync(CancellationToken.None).Result.Etag, Is.EqualTo("e1"));
        }
    }
}
