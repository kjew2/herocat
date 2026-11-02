using System;
using System.Collections.Generic;
using HeroCat.Client.Core;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>
    /// The client side of 01 section 8.12 (issue #299): the maintenance banner, the mid-session CLIENT_TOO_OLD block,
    /// SCR_SETTINGS' update row, the hot-apply hook for the Shop and Inbox view-models, and the Shop and Dungeons kill switches.
    /// </summary>
    public class LiveOpsVmTests
    {
        const string V = "2099.01.01";

        /// <summary>Puts a scripted config cadence on the fixture's runner (Local boot has its own, always all-on).</summary>
        static ScriptedConfig Script(ClientFixture f, ClientConfig cfg, string clientVersion = "1.0.0")
        {
            var svc = new ScriptedConfig(cfg);
            var rf = new ConfigRefresher(svc, f.S.Clock, f.S.Mono, clientVersion, Guid.Empty);
            rf.Start(cfg);
            f.S.Runner.Config = rf;
            return svc;
        }

        static void Land(ClientFixture f, ScriptedConfig svc, ClientConfig next)
        {
            svc.Next = next;
            f.S.Runner.Config.RefreshNow();
            f.Frame();
        }

        static ClientConfig Maint(bool active, long until, string key = null) =>
            ScriptedConfig.Cfg(maintenance: new MaintenanceInfo(active, key, until));

        // ------------------------------------------------------------------------------------- maintenance banner

        [Test]
        public void AScheduledWindow_IsAnnouncedFromMaintAnnounceSBefore_AndCountsDown()   // 08 section 8.4
        {
            var f = ClientFixture.Open();
            long now = f.S.Clock.NowServerSeconds;
            var svc = Script(f, Maint(false, now + RuntimeConfig.MAINT_ANNOUNCE_S + 60));
            f.Frame();
            var b = f.Vm.Main.Maintenance;
            Assert.That(b.Visible.Value, Is.False, "more than MAINT_ANNOUNCE_S ahead: nothing yet");

            now = f.S.Clock.NowServerSeconds;
            Land(f, svc, Maint(false, now + 125, "maint.db_upgrade"));
            Assert.That(b.Visible.Value, Is.True);
            Assert.That(b.Active.Value, Is.False);
            Assert.That(b.Line.Key, Is.EqualTo(UiKeys.MaintScheduled));
            long left = b.Current.SecondsLeft;
            Assert.That(left, Is.InRange(123, 125));
            Assert.That(b.Line.Arg(0).ToString(), Is.EqualTo(TextFmt.Countdown(left)));
            Assert.That(b.MessageVisible.Value, Is.True);
            Assert.That(b.Message.Key, Is.EqualTo("maint.db_upgrade"));

            f.Frame(1.0);
            Assert.That(b.Current.SecondsLeft, Is.EqualTo(left - 1), "one server second later");

            Land(f, svc, ScriptedConfig.Cfg());
            Assert.That(b.Visible.Value, Is.False, "the announcement was withdrawn");
            Assert.That(b.MessageVisible.Value, Is.False);
        }

        [Test]
        public void DuringAWindow_TheBannerCountsDownToUntil_AndPlayContinues()   // 01 section 8.12 step 4
        {
            var f = ClientFixture.Open();
            long now = f.S.Clock.NowServerSeconds;
            Script(f, Maint(true, now + 3 * 3600));
            f.Frame();
            var b = f.Vm.Main.Maintenance;
            Assert.That(b.Visible.Value, Is.True);
            Assert.That(b.Active.Value, Is.True);
            Assert.That(b.Line.Key, Is.EqualTo(UiKeys.MaintActive));
            Assert.That(b.MessageVisible.Value, Is.False, "no messageKey sent");
            Assert.That(f.Vm.Error.Visible.Value, Is.False, "mid-session maintenance is a banner, not a block");
            Assert.That(f.Vm.TicksRunning, Is.True);

            var open = new MaintenanceBannerVm();
            open.Show(ConfigRefresher.BannerFor(new MaintenanceInfo(true, null, 0), now));
            Assert.That(open.Line.Key, Is.EqualTo(UiKeys.MaintActiveOpen), "a window with no end");
        }

        [Test]
        public void WithoutMaintenance_TheBannerFrameWritesNothing()
        {
            var f = ClientFixture.Open();
            Script(f, ScriptedConfig.Cfg());
            var counters = new List<Func<int>>();
            SinkBinder.BindAll(f.Vm.Main.Maintenance, counters);
            f.Frames(30);
            Assert.That(SinkBinder.Total(counters), Is.EqualTo(0));
        }

        // ------------------------------------------------------------------------------------- CLIENT_TOO_OLD block

        [Test]
        public void AMidSessionClientTooOld_WaitsForMainIdle_SavesFirst_AndOffersOnlyUpdate()   // 01 section 8.12 step 3
        {
            var f = ClientFixture.Open();
            var svc = Script(f, ScriptedConfig.Cfg());
            string opened = "none";
            f.Vm.OpenUrl = url => opened = url;

            f.Vm.Main.OnTab(MainTab.Summon);   // not idle: another Full screen on top
            var gated = ScriptedConfig.Cfg(minClient: "2.0.0") with
            {
                StoreUrls = new Dictionary<string, string> { [StoreLink.Android] = "https://play.example/herocat", [StoreLink.Ios] = "https://apps.example/herocat" },
            };
            Land(f, svc, gated);
            Assert.That(f.S.Runner.Config.ClientTooOld, Is.Not.Null);
            f.Frames(5);
            Assert.That(f.Vm.UpdateGate.Blocked, Is.False, "play is never interrupted");
            Assert.That(f.Vm.Error.Visible.Value, Is.False);

            long saves = f.S.Runner.SavesWritten;
            f.Vm.Router.PopTo(ScreenIds.Main);
            f.Frame();
            Assert.That(f.Vm.UpdateGate.Blocked, Is.True);
            Assert.That(f.S.Runner.SavesWritten, Is.EqualTo(saves + 1), "saved before the block");
            Assert.That(f.Vm.Error.Code, Is.EqualTo("CLIENT_TOO_OLD"));
            Assert.That(f.Vm.Error.Blocking, Is.True);
            Assert.That(f.Vm.Error.Actions, Is.EqualTo(new[] { ErrorAction.UpdateApp }));
            Assert.That(f.Vm.Router.BlockingError, Is.Not.Null);
            Assert.That(f.Vm.UpdateGate.StoreUrl, Is.Null, "the fixture names no platform");

            f.Vm.Error.Act(ErrorAction.UpdateApp);
            Assert.That(opened, Is.Null, "no store page for this platform: the Unity layer falls back");

            f.Vm.Error.Show(HeroCat.Runtime.Errors.ErrorClassifier.ForCode("MAINTENANCE", default));   // another error replaces it ...
            f.Frame();
            Assert.That(f.Vm.Error.Code, Is.EqualTo("CLIENT_TOO_OLD"), "... for one frame only");

            Land(f, svc, ScriptedConfig.Cfg());
            Assert.That(f.Vm.UpdateGate.Blocked, Is.False, "a config that lifts the gate takes the block down");
            Assert.That(f.Vm.Error.Visible.Value, Is.False);
        }

        [Test]
        public void TheStoreLink_IsConfigsUrlForThisPlatform()
        {
            var opts = new GameVmOptions { Platform = StoreLink.Ios };
            var f = ClientFixture.Open(options: opts);
            string opened = null;
            f.Vm.OpenUrl = url => opened = url;
            Script(f, ScriptedConfig.Cfg(minClient: "2.0.0") with
            {
                StoreUrls = new Dictionary<string, string> { [StoreLink.Ios] = "https://apps.example/herocat" },
            });
            f.Frame();
            Assert.That(f.Vm.UpdateGate.StoreUrl, Is.EqualTo("https://apps.example/herocat"));
            f.Vm.Error.Act(ErrorAction.UpdateApp);
            Assert.That(opened, Is.EqualTo("https://apps.example/herocat"));
            Assert.That(StoreLink.For(ScriptedConfig.Cfg(), StoreLink.Android), Is.Null);
        }

        // ------------------------------------------------------------------------------------- content updates

        static ContentUpdater Updater(MemoryContentSource bundle, string minClient = null)
        {
            var staging = new MemoryContentStaging();
            if (bundle != null) staging.Cdn[V] = bundle;
            var u = new ContentUpdater(staging, TestContent.Db, Bundles.RunningManifest, "1.0.0");
            Assert.That(u.Check(ScriptedConfig.Cfg(content: Bundles.Pointer(V, minClient))).Wait(10000), Is.True);
            return u;
        }

        [Test]
        public void TheSettingsUpdateRow_FollowsTheContentUpdater()   // 01 section 8.12 step 2
        {
            var none = ClientFixture.Open();
            none.Vm.Router.Push(ScreenIds.Settings);
            none.Frame();
            Assert.That(none.Vm.Settings.UpdateVisible.Value, Is.False, "no content updater (Local)");

            var u = Updater(Bundles.Make(V, touch: "skills"));
            Assert.That(u.UpdateReady, Is.True);
            var f = ClientFixture.Open(options: new GameVmOptions { ContentUpdates = u });
            f.Vm.Router.Push(ScreenIds.Settings);
            f.Frame();
            Assert.That(f.Vm.Settings.UpdateVisible.Value, Is.True);
            Assert.That(f.Vm.Settings.UpdateLine.Key, Is.EqualTo(UiKeys.SettingsUpdateReady));

            var old = ClientFixture.Open(options: new GameVmOptions { ContentUpdates = Updater(null, minClient: "9.0.0") });
            old.Vm.Router.Push(ScreenIds.Settings);
            old.Frame();
            Assert.That(old.Vm.Settings.UpdateLine.Key, Is.EqualTo(UiKeys.SettingsUpdateApp), "the bundle needs a newer client");
        }

        [Test]
        public void AHotBundle_IsSwappedAtASafePoint_AndPublishedToTheShopAndInboxHook()   // 00 contract 3, 07 section 16
        {
            var u = Updater(Bundles.Make(V, hotApply: true, touch: "localization"));
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.HotReady));
            var f = ClientFixture.Open(options: new GameVmOptions { ContentUpdates = u });
            HotContentSet raised = null;
            f.Vm.Context.HotContentApplied += s => raised = s;

            f.Vm.Confirm.Ask(new ConfirmRequest { TitleKey = UiKeys.ExitTitle, BodyKey = UiKeys.ExitBody });
            f.Frames(3);
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.HotReady), "not with a modal open");
            Assert.That(f.Vm.Context.HotContent, Is.Null);

            f.Vm.Confirm.OnCancel();
            f.Frame();
            Assert.That(u.State, Is.EqualTo(ContentUpdateState.HotApplied));
            Assert.That(raised, Is.Not.Null);
            Assert.That(f.Vm.Context.HotContent, Is.SameAs(raised));
            Assert.That(raised.Version, Is.EqualTo(V));
            Assert.That(f.S.Sim.Db, Is.SameAs(TestContent.Db), "the sim's ContentDb is never replaced");
        }

        // ------------------------------------------------------------------------------------- kill switches

        [Test]
        public void TheShopAndDungeonsTabs_FollowTheirKillSwitches()   // 01 section 8.12 step 1
        {
            var f = ClientFixture.Open();
            var flags = KillSwitches.AllOn;
            f.Vm.Context.Flags = () => flags;
            f.Frame();
            Assert.That(f.Vm.Main.TabDisabled[(int)MainTab.Shop].Value, Is.False);
            Assert.That(f.Vm.Main.TabDisabled[(int)MainTab.Dungeons].Value, Is.False);

            flags = new KillSwitches(false, false, true, true, new Dictionary<Banner, bool>());
            f.Frame();
            Assert.That(f.Vm.Main.TabDisabled[(int)MainTab.Shop].Value, Is.True);
            Assert.That(f.Vm.Main.TabDisabled[(int)MainTab.Dungeons].Value, Is.True);

            f.Vm.Toasts.Clear();
            f.Vm.Main.OnTab(MainTab.Shop);
            Assert.That(f.Vm.Toasts.Current(f.Now), Is.EqualTo(UiKeys.FeatureDisabled));

            flags = KillSwitches.AllOn;
            f.Frame();
            Assert.That(f.Vm.Main.TabDisabled[(int)MainTab.Shop].Value, Is.False, "back on within one refresh");
        }
    }
}
