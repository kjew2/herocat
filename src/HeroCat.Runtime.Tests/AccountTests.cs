using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Account;
using HeroCat.Runtime.Boot;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Content;
using HeroCat.Sim.Replay;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>
    /// Issue #148, engine-free half: the token store and its secure-storage doubles, the logout archive, AccountFlow's
    /// logout / delete order (01 section 8.13, AC 13) and boot step 3 (sign-in, local-guest adoption, archive pruning).
    /// </summary>
    public class AccountTests : TempDirTest
    {
        static readonly Guid Player = new Guid("6b1f2c40-0000-4000-8000-000000000042");

        /// <summary>An account over a real token store; logout and delete clear it, as the AuthClient does.</summary>
        sealed class FakeAccount : IAccountService
        {
            public readonly ITokenStore Tokens;
            public int Logouts, Deletes;
            public bool DeleteFails;
            public SignInResult Next = new SignInResult(SignInStatus.SignedIn, Player);
            public Exception Throw;

            public FakeAccount(ITokenStore tokens) { Tokens = tokens; }

            public Guid PlayerId => Tokens.Load()?.PlayerId ?? Guid.Empty;
            public bool HasSession => Tokens.Load()?.HasSession ?? false;

            public Task<SignInResult> SignInAsync(CancellationToken ct = default)
            {
                if (Throw != null) throw Throw;
                return Task.FromResult(Next);
            }

            public Task<bool> LogoutAsync(CancellationToken ct = default)
            {
                Logouts++;
                Tokens.Clear();
                return Task.FromResult(true);
            }

            public Task<long> DeleteAccountAsync(CancellationToken ct = default)
            {
                Deletes++;
                if (DeleteFails) throw new ServiceException(ErrorClass.Offline, ErrorClassifier.NET_OFFLINE);
                Tokens.Clear();
                return Task.FromResult(1_794_880_000L);
            }

            public event Action<SignInResult> SessionLost { add { } remove { } }
        }

        static SecureTokenStore SignedInTokens()
        {
            var t = new SecureTokenStore(new MemorySecureStore());
            t.Save(new AuthTokens { PlayerId = Player, AccessToken = "a", RefreshToken = "r1.x", AccessExpiresAt = 99 });
            return t;
        }

        string SavesDir => Path.Combine(Dir, "saves");

        void WriteSave(string name = "slot0.sav", string text = "save")
        {
            Directory.CreateDirectory(SavesDir);
            File.WriteAllText(Path.Combine(SavesDir, name), text);
        }

        // ------------------------------------------------------------------------------------------- token store

        [Test]
        public void TokenStore_RoundTrips_ThroughSecureStorage_AndClears()
        {
            foreach (ISecureStore secure in new ISecureStore[] { new MemorySecureStore(), new FileSecureStore(Path.Combine(Dir, "secure")) })
            {
                var store = new SecureTokenStore(secure);
                Assert.That(store.Load(), Is.Null);
                var t = new AuthTokens
                {
                    PlayerId = Player, AccessToken = "eyJ.a.b", RefreshToken = "r1.abc", AccessExpiresAt = 1_793_670_800,
                    PendingGuestRequestId = "0123456789abcdef0123456789abcdef", RegisteredKeyId = "00112233aabbccdd",
                };
                store.Save(t);
                Assert.That(new SecureTokenStore(secure).Load(), Is.EqualTo(t), "another process reads the same record");
                Assert.That(secure.Get(SecureTokenStore.Key), Is.Not.Null, "kept as tokens.json (02 section 8.4)");
                store.Clear();
                Assert.That(store.Load(), Is.Null);
            }
        }

        [Test]
        public void AnUnreadableTokenFile_IsNoAccount()
        {
            var secure = new MemorySecureStore();
            secure.Set(SecureTokenStore.Key, new byte[] { 0x7b, 0x22, 0xff });
            Assert.That(new SecureTokenStore(secure).Load(), Is.Null);
        }

        [Test]
        public void TheDeviceKey_IsCreatedOnce_AndReused()
        {
            var secure = new FileSecureStore(Path.Combine(Dir, "secure"));
            var k = DeviceKey.LoadOrCreate(secure);
            Assert.That(k, Has.Length.EqualTo(32));
            Assert.That(DeviceKey.LoadOrCreate(new FileSecureStore(Path.Combine(Dir, "secure"))), Is.EqualTo(k));
        }

        [Test]
        public void TheDeviceKey_IsHmacKeyBytesOfCsprngOutput_AndAWrongLengthOneIsReplaced()   // 02 section 10.1
        {
            var a = DeviceKey.Create();
            var b = DeviceKey.Create();
            Assert.That(a, Has.Length.EqualTo(DeviceKey.KeyBytes).And.Length.EqualTo(32), "HMAC_KEY_BYTES");
            Assert.That(a, Is.Not.EqualTo(b));
            Assert.That(a.Distinct().Count(), Is.GreaterThan(8), "not a constant fill");

            var secure = new MemorySecureStore();
            secure.Set(DeviceKey.Key, new byte[16]);
            var k = DeviceKey.LoadOrCreate(secure);
            Assert.That(k, Has.Length.EqualTo(32));
            Assert.That(secure.Get(DeviceKey.Key), Is.EqualTo(k));
        }

        [Test]
        public void TheKeyId_IsTheFirst16HexOfSha256_StableAndDistinct()
        {
            var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
            string want;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                want = string.Concat(sha.ComputeHash(key).Take(8).Select(x => x.ToString("x2")));
            Assert.That(DeviceKey.KeyId(key), Is.EqualTo(want));
            Assert.That(DeviceKey.KeyId(key), Does.Match("^[0-9a-f]{16}$"));
            Assert.That(DeviceKey.KeyId(DeviceKey.Create()), Is.Not.EqualTo(want));
        }

        // ------------------------------------------------------------------------------------------- archive

        [Test]
        public void Archive_MovesSavesUnderThePlayerId_AndLeavesAnEmptySavesDir()
        {
            WriteSave();
            WriteSave("cmdlog.jsonl", "{}");
            var a = new SaveArchive(Dir);

            Assert.That(a.Archive(Player, TestContent.T0), Is.True);

            var dir = Path.Combine(Dir, "saves." + Player.ToString("D") + ".archived");
            Assert.That(File.ReadAllText(Path.Combine(dir, "slot0.sav")), Is.EqualTo("save"));
            Assert.That(File.Exists(Path.Combine(dir, "cmdlog.jsonl")), Is.True);
            Assert.That(Directory.Exists(SavesDir) && Directory.GetFileSystemEntries(SavesDir).Length == 0, Is.True);
            var listed = a.List(TestContent.T0).Single();
            Assert.That(listed.PlayerId, Is.EqualTo(Player));
            Assert.That(listed.ArchivedAt, Is.EqualTo(TestContent.T0));
            Assert.That(a.Archive(Player, TestContent.T0), Is.False, "nothing to archive");
        }

        [Test]
        public void Prune_DeletesArchivesOlderThanArchiveKeepDays_ByTheInjectedClock()
        {
            var a = new SaveArchive(Dir);
            var old = Guid.NewGuid();
            WriteSave();
            a.Archive(old, TestContent.T0);
            WriteSave();
            a.Archive(Player, TestContent.T0 + 86400);

            long now = TestContent.T0 + RuntimeConfig.ARCHIVE_KEEP_DAYS * 86400L + 1;
            Assert.That(a.Prune(now), Is.EqualTo(1));
            Assert.That(a.List(now).Select(x => x.PlayerId), Is.EqualTo(new[] { Player }));
        }

        [Test]
        public void Restore_BringsAnArchiveBack_OnlyIntoAnEmptySavesDir()
        {
            var a = new SaveArchive(Dir);
            WriteSave(text: "mine");
            a.Archive(Player, TestContent.T0);
            WriteSave(text: "someone else's");
            Assert.That(a.Restore(Player), Is.False, "never overwrites a save");

            File.Delete(Path.Combine(SavesDir, "slot0.sav"));
            Assert.That(a.Restore(Player), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(SavesDir, "slot0.sav")), Is.EqualTo("mine"));
            Assert.That(File.Exists(Path.Combine(SavesDir, SaveArchive.StampFile)), Is.False);
            Assert.That(a.List(TestContent.T0), Is.Empty);
        }

        // ------------------------------------------------------------------------------------------- AccountFlow (8.13)

        [Test]
        public void Logout_WithAFailedUpload_AsksLogoutUnsynced_AndCancelClearsNothing()   // 01 AC 13
        {
            WriteSave();
            var account = new FakeAccount(SignedInTokens());
            var flow = new AccountFlow(account, new SaveArchive(Dir), new FakeClock(TestContent.T0));
            int asked = 0;

            var r = flow.LogoutAsync(_ => Task.FromResult(false), () => { asked++; return Task.FromResult(false); }).GetAwaiter().GetResult();

            Assert.That(r.Outcome, Is.EqualTo(LogoutOutcome.Cancelled));
            Assert.That(asked, Is.EqualTo(1));
            Assert.That(account.Logouts, Is.Zero);
            Assert.That(account.Tokens.Load().RefreshToken, Is.EqualTo("r1.x"), "tokens are not cleared unless confirmed");
            Assert.That(File.Exists(Path.Combine(SavesDir, "slot0.sav")), Is.True);
        }

        [Test]
        public void Logout_ConfirmedAnyway_ClearsTokens_AndArchivesTheSave()   // 01 AC 13
        {
            WriteSave();
            var account = new FakeAccount(SignedInTokens());
            var flow = new AccountFlow(account, new SaveArchive(Dir), new FakeClock(TestContent.T0));

            var r = flow.LogoutAsync(_ => throw new IOException("disk"), () => Task.FromResult(true)).GetAwaiter().GetResult();

            Assert.That(r, Is.EqualTo(new LogoutResult(LogoutOutcome.LoggedOut, Uploaded: false, ServerRevoked: true, Archived: true)));
            Assert.That(account.Tokens.Load(), Is.Null);
            Assert.That(File.Exists(Path.Combine(Dir, "saves." + Player.ToString("D") + ".archived", "slot0.sav")), Is.True);
            Assert.That(Directory.GetFileSystemEntries(SavesDir), Is.Empty);
        }

        [Test]
        public void Logout_AfterASuccessfulUpload_DoesNotAsk()
        {
            WriteSave();
            var account = new FakeAccount(SignedInTokens());
            var flow = new AccountFlow(account, new SaveArchive(Dir), new FakeClock(TestContent.T0));
            var r = flow.LogoutAsync(_ => Task.FromResult(true), () => throw new AssertionException("not asked")).GetAwaiter().GetResult();
            Assert.That(r.Outcome, Is.EqualTo(LogoutOutcome.LoggedOut));
            Assert.That(r.Uploaded, Is.True);
            Assert.That(account.Logouts, Is.EqualTo(1));
        }

        [Test]
        public void Logout_AnUploadSlowerThanLogoutUploadTimeout_CountsAsFailed_AndIsCancelled()
        {
            var account = new FakeAccount(SignedInTokens());
            TimeSpan waited = TimeSpan.Zero;
            var flow = new AccountFlow(account, null, new FakeClock(TestContent.T0), (d, ct) => { waited = d; return Task.CompletedTask; });
            CancellationToken seen = default;
            var hang = new TaskCompletionSource<bool>();
            bool asked = false;

            var r = flow.LogoutAsync(ct => { seen = ct; return hang.Task; }, () => { asked = true; return Task.FromResult(false); })
                        .GetAwaiter().GetResult();

            Assert.That(waited, Is.EqualTo(TimeSpan.FromSeconds(RuntimeConfig.LOGOUT_UPLOAD_TIMEOUT_S)));
            Assert.That(asked, Is.True);
            Assert.That(seen.IsCancellationRequested, Is.True, "the upload is told to stop");
            Assert.That(r.Outcome, Is.EqualTo(LogoutOutcome.Cancelled));
        }

        [Test]
        public void DeleteAccount_WipesSavesAndTokens_OnlyWhenTheServerAccepts()
        {
            WriteSave();
            var account = new FakeAccount(SignedInTokens()) { DeleteFails = true };
            var archive = new SaveArchive(Dir);
            var flow = new AccountFlow(account, archive, new FakeClock(TestContent.T0));

            Assert.Throws<ServiceException>(() => flow.DeleteAccountAsync().GetAwaiter().GetResult());
            Assert.That(File.Exists(Path.Combine(SavesDir, "slot0.sav")), Is.True);
            Assert.That(account.Tokens.Load(), Is.Not.Null);

            account.DeleteFails = false;
            Assert.That(flow.DeleteAccountAsync().GetAwaiter().GetResult(), Is.EqualTo(1_794_880_000L));
            Assert.That(archive.SavesEmpty, Is.True);
            Assert.That(account.Tokens.Load(), Is.Null);
        }

        // ------------------------------------------------------------------------------------------- boot step 3

        BootOptions Boot(FakeClock clock, MemorySaveStore slots, IAccountService account, SaveArchive archives = null) => new BootOptions
        {
            Content = new Func<ContentDb>[] { () => TestContent.Db },
            Services = (db, state) => GameServices.Build(BackendMode.Local, new ServicesOptions
            {
                Clock = clock, Slots = slots, State = state, Content = db, Analytics = new MemoryAnalytics(), InstallId = TestContent.Device,
            }) with { Account = account, Archives = archives },
            NewProfileRngRoot = TestContent.Root,
            Runner = new SessionRunnerOptions { Mono = new FakeMonoClock(), TickBudgetMs = 0 },
            CommandLog = new ListCommandLog(),
            Mono = new FakeMonoClock(),
        };

        [Test]
        public void LocalBuilds_BootAsALocalGuest()
        {
            var r = BootFlow.Run(Boot(new FakeClock(TestContent.T0), new MemorySaveStore(), new LocalAccountService()));
            Assert.That(r.Outcome, Is.EqualTo(BootOutcome.Ready));
            Assert.That(r.SignIn, Is.EqualTo(SignInResult.LocalGuest));
            Assert.That(r.Sim.State.PlayerId, Is.EqualTo(Guid.Empty));
        }

        [Test]
        public void AnOfflineFirstLaunch_IsAPlayableLocalGuest_AndTheFirstAuthAdoptsItsSave()   // 01 open question 7
        {
            var clock = new FakeClock(TestContent.T0);
            var slots = new MemorySaveStore();
            var account = new FakeAccount(new SecureTokenStore(new MemorySecureStore()))
            {
                Next = new SignInResult(SignInStatus.Offline, Guid.Empty, Code: ErrorClassifier.NET_OFFLINE),
            };
            var first = BootFlow.Run(Boot(clock, slots, account));
            Assert.That(first.Outcome, Is.EqualTo(BootOutcome.Ready));
            Assert.That(first.SignIn.Status, Is.EqualTo(SignInStatus.Offline));
            Assert.That(first.IsNewProfile, Is.True);
            Assert.That(first.Sim.State.PlayerId, Is.EqualTo(Guid.Empty));
            Assert.That(first.AdoptedLocalSave, Is.False);
            var root = first.Sim.State.RngRoot;

            clock.Advance(600);
            account.Next = new SignInResult(SignInStatus.SignedIn, Player, NewAccount: true);
            var second = BootFlow.Run(Boot(clock, slots, account));
            Assert.That(second.IsNewProfile, Is.False);
            Assert.That(second.AdoptedLocalSave, Is.True);
            Assert.That(second.Sim.State.PlayerId, Is.EqualTo(Player));
            Assert.That(second.Sim.State.RngRoot, Is.EqualTo(root), "adopting re-seeds nothing");
            var saved = TestContent.Store(slots, clock).LoadAsync(clock.NowServerSeconds, TestContent.Db, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(saved.State.PlayerId, Is.EqualTo(Player), "boot step 5 saved the adopted save");
        }

        [Test]
        public void ANewProfile_BelongsToTheSignedInAccount()
        {
            var account = new FakeAccount(new SecureTokenStore(new MemorySecureStore()));
            var r = BootFlow.Run(Boot(new FakeClock(TestContent.T0), new MemorySaveStore(), account));
            Assert.That(r.IsNewProfile, Is.True);
            Assert.That(r.Sim.State.PlayerId, Is.EqualTo(Player));
            Assert.That(r.AdoptedLocalSave, Is.False);
        }

        [Test]
        public void ARefusedOrThrowingSignIn_DoesNotStopTheBoot()
        {
            var account = new FakeAccount(new SecureTokenStore(new MemorySecureStore()))
            {
                Next = new SignInResult(SignInStatus.SignInRequired, Guid.Empty, Code: "TOKEN_REUSED"),
            };
            var refused = BootFlow.Run(Boot(new FakeClock(TestContent.T0), new MemorySaveStore(), account));
            Assert.That(refused.Outcome, Is.EqualTo(BootOutcome.Ready));
            Assert.That(refused.SignIn.Status, Is.EqualTo(SignInStatus.SignInRequired));

            account.Throw = new InvalidOperationException("bug");
            var thrown = BootFlow.Run(Boot(new FakeClock(TestContent.T0), new MemorySaveStore(), account));
            Assert.That(thrown.Outcome, Is.EqualTo(BootOutcome.Ready));
            Assert.That(thrown.SignIn.Status, Is.EqualTo(SignInStatus.Offline));
        }

        [Test]
        public void Boot_PrunesLogoutArchivesOlderThanArchiveKeepDays()
        {
            var archive = new SaveArchive(Dir);
            WriteSave();
            archive.Archive(Guid.NewGuid(), TestContent.T0);
            WriteSave();
            archive.Archive(Player, TestContent.T0 + 20 * 86400L);

            var clock = new FakeClock(TestContent.T0 + (RuntimeConfig.ARCHIVE_KEEP_DAYS + 1) * 86400L);
            BootFlow.Run(Boot(clock, new MemorySaveStore(), new LocalAccountService(), archive));

            Assert.That(archive.List(clock.NowServerSeconds).Select(a => a.PlayerId), Is.EqualTo(new[] { Player }));
        }
    }
}
