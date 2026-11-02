using System;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;

namespace HeroCat.Runtime.Account
{
    public enum LogoutOutcome
    {
        /// <summary>Tokens cleared, saves archived: restart to SCR_TITLE.</summary>
        LoggedOut,

        /// <summary>The upload failed and the player chose "Cancel" on LOGOUT_UNSYNCED: nothing changed, stay.</summary>
        Cancelled,
    }

    /// <summary>The result of <see cref="AccountFlow.LogoutAsync"/>.</summary>
    public sealed record LogoutResult(LogoutOutcome Outcome, bool Uploaded, bool ServerRevoked, bool Archived);

    /// <summary>
    /// Logout, account switch and account deletion (01 section 8.13; issue #148), engine-free. The screens supply the
    /// session's save-and-upload and the <c>LOGOUT_UNSYNCED</c> modal as delegates; this class owns the order:
    /// <list type="number">
    /// <item>Logout: save and upload, awaited up to <c>LOGOUT_UPLOAD_TIMEOUT_S</c>. A failure or timeout asks
    /// <c>LOGOUT_UNSYNCED</c> ("Log out anyway" / "Cancel"); Cancel changes nothing, tokens included (01 AC 13).</item>
    /// <item>Then <c>POST /auth/logout</c> (best effort), the tokens are cleared, and <c>saves/</c> moves to
    /// <c>saves.&lt;playerId&gt;.archived/</c> for <c>ARCHIVE_KEEP_DAYS</c>. The caller restarts to <c>SCR_TITLE</c>.</item>
    /// <item>Delete account: <c>POST /account/delete</c>; on success the local saves (and that player's archive) and the
    /// tokens are wiped and the caller restarts. A failure changes nothing.</item>
    /// </list>
    /// Platform sign-in (<c>/auth/link-login</c>) and the linked-elsewhere choice (<c>PROVIDER_ALREADY_LINKED</c> +
    /// <c>/auth/merge</c>, two SaveSummary cards) are issue #196; they extend <see cref="IAccountService"/> and this flow.
    /// The caller stops the session (<c>SessionRunner.OnQuit</c>) once the upload step is done and before the archive.
    /// </summary>
    public sealed class AccountFlow
    {
        /// <summary>The modal shown when the logout upload fails (01 section 8.13 step 1).</summary>
        public const string LOGOUT_UNSYNCED = "LOGOUT_UNSYNCED";

        readonly IAccountService _account;
        readonly SaveArchive _archive;
        readonly IClock _clock;
        readonly Func<TimeSpan, CancellationToken, Task> _delay;

        /// <param name="archive">Null for a build without a data directory (tests, bots): nothing is archived.</param>
        /// <param name="delay">Waits for the upload timeout; default Task.Delay (tests pass a controllable one).</param>
        public AccountFlow(IAccountService account, SaveArchive archive, IClock clock, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _archive = archive;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _delay = delay ?? Task.Delay;
        }

        public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromSeconds(RuntimeConfig.LOGOUT_UPLOAD_TIMEOUT_S);

        /// <summary>
        /// 01 section 8.13 steps 1-2. <paramref name="saveAndUpload"/> writes the save (Quit trigger) and uploads it, true
        /// when the server accepted it; an exception counts as a failure. <paramref name="confirmUnsynced"/> shows
        /// <see cref="LOGOUT_UNSYNCED"/> and returns true for "Log out anyway".
        /// </summary>
        public async Task<LogoutResult> LogoutAsync(Func<CancellationToken, Task<bool>> saveAndUpload, Func<Task<bool>> confirmUnsynced,
                                                    CancellationToken ct = default)
        {
            if (saveAndUpload == null) throw new ArgumentNullException(nameof(saveAndUpload));
            if (confirmUnsynced == null) throw new ArgumentNullException(nameof(confirmUnsynced));

            bool uploaded = await UploadWithTimeoutAsync(saveAndUpload, ct).ConfigureAwait(false);
            if (!uploaded && !await confirmUnsynced().ConfigureAwait(false))
                return new LogoutResult(LogoutOutcome.Cancelled, false, false, false);

            Guid player = _account.PlayerId;   // read before the tokens (and the cached id with them) are cleared
            bool revoked;
            try { revoked = await _account.LogoutAsync(ct).ConfigureAwait(false); }
            catch (ServiceException) { revoked = false; }
            bool archived = _archive != null && _archive.Archive(player, _clock.NowServerSeconds);
            return new LogoutResult(LogoutOutcome.LoggedOut, uploaded, revoked, archived);
        }

        async Task<bool> UploadWithTimeoutAsync(Func<CancellationToken, Task<bool>> saveAndUpload, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                Task<bool> upload;
                try { upload = saveAndUpload(cts.Token); }
                catch (Exception e) when (!(e is OperationCanceledException) || !ct.IsCancellationRequested)
                {
                    return false;
                }
                var timeout = _delay(UploadTimeout, cts.Token);
                var first = await Task.WhenAny(upload, timeout).ConfigureAwait(false);
                cts.Cancel();   // stops the loser: the upload on timeout, the timer on completion
                ct.ThrowIfCancellationRequested();
                if (first != upload) return false;
                try { return await upload.ConfigureAwait(false); }
                catch (Exception e) when (!(e is OperationCanceledException) || !ct.IsCancellationRequested)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// 01 section 8.13 step 5: starts the server deletion, then wipes the local saves and tokens. Returns
        /// <c>graceUntil</c>. Throws ServiceException (nothing wiped) when the server refuses or cannot be reached.
        /// </summary>
        public async Task<long> DeleteAccountAsync(CancellationToken ct = default)
        {
            Guid player = _account.PlayerId;
            long graceUntil = await _account.DeleteAccountAsync(ct).ConfigureAwait(false);
            _archive?.Wipe(player);
            return graceUntil;
        }
    }
}
