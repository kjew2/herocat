using System;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// The store page for the CLIENT_TOO_OLD block's [Update] (01 section 8.1 step 2 and 8.12 step 3): config's
    /// <c>storeUrls</c> by platform key (<see cref="Ios"/>, <see cref="Android"/>). Null when config names none; the Unity
    /// layer then falls back to its own store link.
    /// </summary>
    public static class StoreLink
    {
        public const string Ios = "ios", Android = "android";

        public static string For(ClientConfig config, string platform)
        {
            var urls = config?.StoreUrls;
            if (urls == null || string.IsNullOrEmpty(platform)) return null;
            return urls.TryGetValue(platform, out var url) && !string.IsNullOrEmpty(url) ? url : null;
        }
    }

    /// <summary>
    /// SCR_MAIN's maintenance banner (07 section 4.3, 01 section 8.12 step 4, 08 section 8.4; issue #299), fed once per
    /// server second from <see cref="ConfigRefresher.Banner"/>. During a window it reads "Maintenance: back in 12:34"
    /// (or "Maintenance in progress" when the window has no end); from MAINT_ANNOUNCE_S before an announced window
    /// (<c>active: false</c> with a future <c>until</c>) it reads "Maintenance starts in 42:10". The play is not blocked:
    /// players keep playing offline through a window (08 section 8.4). The server's <c>messageKey</c> is a second line,
    /// shown only when config sends one.
    /// </summary>
    public sealed class MaintenanceBannerVm
    {
        public MaintenanceBannerVm(ILocalizer loc = null)
        {
            Line = new TextKeyLabel(loc);
            Message = new TextKeyLabel(loc);
        }

        public readonly Flag Visible = new Flag();

        /// <summary>True during the window, false for the announcement.</summary>
        public readonly Flag Active = new Flag();

        /// <summary>The countdown line (<see cref="UiKeys.MaintActive"/>, <see cref="UiKeys.MaintActiveOpen"/> or <see cref="UiKeys.MaintScheduled"/>).</summary>
        public readonly TextKeyLabel Line;

        /// <summary>The server's <c>maintenance.messageKey</c> (08 section 8.4, e.g. <c>maint.db_upgrade</c>).</summary>
        public readonly TextKeyLabel Message;

        public readonly Flag MessageVisible = new Flag();

        /// <summary>What is shown; null while hidden.</summary>
        public MaintenanceBanner Current { get; private set; }

        /// <summary>Change-only: a second's tick rewrites only the countdown argument.</summary>
        public void Show(MaintenanceBanner b)
        {
            Current = b;
            Visible.Set(b != null);
            if (b == null)
            {
                Active.Set(false);
                MessageVisible.Set(false);
                return;
            }
            Active.Set(b.Active);
            var left = LabelArg.Num(b.SecondsLeft, NumberStyle.Countdown);
            if (!b.Active) Line.Set(UiKeys.MaintScheduled, left);
            else if (b.SecondsLeft > 0) Line.Set(UiKeys.MaintActive, left);
            else Line.Set(UiKeys.MaintActiveOpen);   // no end, or `until` passed and the re-fetch has not landed yet
            bool msg = !string.IsNullOrEmpty(b.MessageKey);
            MessageVisible.Set(msg);
            if (msg) Message.Set(b.MessageKey);
        }
    }

    /// <summary>
    /// The mid-session CLIENT_TOO_OLD block (01 section 8.12 step 3, 07 sections 4.1 and 4.27; issue #299). Polled every
    /// frame: once a landed config gates this client out, it waits until SCR_MAIN is idle, saves through
    /// <see cref="ConfigRefresher.TryBlock"/> and shows the blocking SCR_ERROR whose only action is [Update], which opens
    /// <see cref="StoreUrl"/>. Another error never replaces it for good: the next poll shows it again. A later config that
    /// lifts the gate takes the block down.
    /// </summary>
    public sealed class ClientUpdateGateVm
    {
        readonly ErrorVm _error;

        public ClientUpdateGateVm(ErrorVm error)
        {
            _error = error ?? throw new ArgumentNullException(nameof(error));
        }

        /// <summary>The block on screen; null while play continues.</summary>
        public UserError Shown { get; private set; }

        public bool Blocked => Shown != null;

        /// <summary>The store page from config's <c>storeUrls</c> for this platform; null when config names none.</summary>
        public string StoreUrl { get; private set; }

        /// <summary>
        /// One frame. <paramref name="mainIdle"/> is asked only while a block is pending (SCR_MAIN on top, no modal, no flow
        /// in flight); <paramref name="save"/> runs right before the block shows. Returns true on the frame the block shows.
        /// </summary>
        public bool Poll(ConfigRefresher refresher, Func<bool> mainIdle, Func<bool> save, string platform)
        {
            if (refresher == null) return false;
            if (Shown != null)
            {
                if (refresher.ClientTooOld == null)
                {
                    Shown = null;   // the gate was lifted (minClientVersion rolled back)
                    if (_error.Code == "CLIENT_TOO_OLD") _error.Hide();
                    return false;
                }
                if (!(_error.Visible.Value && _error.Code == Shown.Code)) _error.Show(Shown);
                return false;
            }
            if (refresher.ClientTooOld == null) return false;
            var e = refresher.TryBlock(mainIdle != null && mainIdle(), save);
            if (e == null) return false;
            Shown = e;
            StoreUrl = StoreLink.For(refresher.Current, platform);
            _error.Show(e);
            return true;
        }
    }

    /// <summary>
    /// SCR_SETTINGS (07 section 4.20; issue #299). Only the content update row is built so far (01 section 8.12 step 2):
    /// "Update ready, restarts on next launch" while <see cref="ContentUpdater.UpdateReady"/>, and "Update the app for new
    /// content" when the offered bundle needs a newer client (09 section 10.3 step 3). Hidden otherwise, and always without
    /// a ContentUpdater (Local mode, tests). The other rows are issue #185.
    /// </summary>
    public sealed class SettingsVm
    {
        readonly ContentUpdater _updates;

        public SettingsVm(VmContext c, ContentUpdater updates)
        {
            _updates = updates;
            UpdateLine = c.Label();
        }

        public readonly Flag UpdateVisible = new Flag();
        public readonly TextKeyLabel UpdateLine;

        public void Refresh()
        {
            if (_updates == null)
            {
                UpdateVisible.Set(false);
                return;
            }
            if (_updates.UpdateReady)
            {
                UpdateVisible.Set(true);
                UpdateLine.Set(UiKeys.SettingsUpdateReady);
            }
            else if (_updates.State == ContentUpdateState.NeedsClientUpdate)
            {
                UpdateVisible.Set(true);
                UpdateLine.Set(UiKeys.SettingsUpdateApp);
            }
            else UpdateVisible.Set(false);
        }
    }
}
