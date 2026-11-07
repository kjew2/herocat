using System;
using System.Threading;
using System.Threading.Tasks;

namespace HeroCat.Runtime.Account
{
    /// <summary>The outcome of boot step 3 (01 section 8.1) or a later sign-in attempt.</summary>
    public enum SignInStatus
    {
        /// <summary>Local backend: no server, no account; the save is a local guest (<c>PlayerId = Guid.Empty</c>).</summary>
        Local,

        /// <summary>Tokens are fresh: refreshed, or a new guest account was created.</summary>
        SignedIn,

        /// <summary>
        /// The server could not be reached (offline, timeout, 5xx, rate limit). Play continues with the cached
        /// <c>playerId</c> if any, else as a local guest that is linked on the first successful connection (open question 7).
        /// Tokens are kept.
        /// </summary>
        Offline,

        /// <summary>
        /// The server refused the session (<c>TOKEN_REUSED</c>, <c>TOKEN_REVOKED</c>, <c>TOKEN_EXPIRED</c> refresh token,
        /// <c>UNAUTHENTICATED</c>, <c>DEVICE_MISMATCH</c>) or this device is bound to an account it holds no tokens for
        /// (<c>DEVICE_ALREADY_BOUND</c>). The tokens are cleared; the player must sign in from <c>SCR_TITLE</c>.
        /// </summary>
        SignInRequired,
    }

    /// <summary>
    /// Boot step 3's result. <see cref="PlayerId"/> is the account to play as (Guid.Empty: a local guest).
    /// <see cref="NewAccount"/>: this call created the guest account, so a local save with <c>PlayerId = Guid.Empty</c> is
    /// adopted as its first upload (01 open question 7, 08 open question 9). <see cref="Code"/> is the wire or client code
    /// behind an Offline or SignInRequired result.
    /// </summary>
    public sealed record SignInResult(SignInStatus Status, Guid PlayerId, bool NewAccount = false, string Code = null)
    {
        public static SignInResult LocalGuest { get; } = new SignInResult(SignInStatus.Local, Guid.Empty);
    }

    /// <summary>
    /// The account seam (01 sections 8.1 step 3 and 8.13): the AuthClient (HeroCat.Remote, issue #148) in Remote builds,
    /// <see cref="LocalAccountService"/> in Local ones. Methods may run on the thread pool; results are plain records.
    /// Account linking and platform sign-in (<c>/auth/link</c>, <c>/auth/link-login</c>, <c>/auth/merge</c>) are issue
    /// #196 and join this seam then.
    /// </summary>
    public interface IAccountService
    {
        /// <summary>The account this install is signed in to (cached while offline); Guid.Empty for none.</summary>
        Guid PlayerId { get; }

        /// <summary>True while a refresh token is held.</summary>
        bool HasSession { get; }

        /// <summary>
        /// True when the account has a linked platform provider (Apple, Google Play Games), so it can be signed back into
        /// after a logout (01 section 8.13 step 3). False for a guest with no linked provider, whose logout abandons the
        /// account (00 decisions 117, 119), and for the Local backend.
        /// </summary>
        bool IsLinked { get; }

        /// <summary>
        /// 01 section 8.1 step 3: refresh when a refresh token exists, else create a guest for this device. Never throws
        /// for a failed call (only for <paramref name="ct"/>); safe to call again later, e.g. on reconnecting.
        /// </summary>
        Task<SignInResult> SignInAsync(CancellationToken ct = default);

        /// <summary>
        /// <c>POST /auth/logout</c> (best effort: the server revokes this device's refresh family), then clears the tokens
        /// whatever the answer. True when the server confirmed the revoke. The save handling around it is
        /// <see cref="AccountFlow"/>'s.
        /// </summary>
        Task<bool> LogoutAsync(CancellationToken ct = default);

        /// <summary>
        /// The unlinked guest's logout (00 decision 119): <c>POST /auth/logout</c>, and the tokens are cleared only when
        /// the server confirmed it (true). On any failure (offline, timeout, an error answer) it returns false and keeps the
        /// tokens, so the device stays signed in to the account the server still binds it to. Never throws for a failed
        /// call (only for <paramref name="ct"/>). True with nothing sent when no session is held.
        /// </summary>
        Task<bool> TryConfirmedLogoutAsync(CancellationToken ct = default);

        /// <summary>
        /// <c>POST /account/delete</c> (08 section 2.3, 02 section 13.2): starts the GDPR deletion and returns its
        /// <c>graceUntil</c> (unix seconds). On success the tokens are cleared; on failure it throws ServiceException and
        /// nothing changes.
        /// </summary>
        Task<long> DeleteAccountAsync(CancellationToken ct = default);

        /// <summary>Raised (on any thread) when a refresh is refused and the tokens were cleared: route to SCR_TITLE.</summary>
        event Action<SignInResult> SessionLost;
    }

    /// <summary>The Local backend's account: no server, a local guest forever. Logout and delete only touch local data.</summary>
    public sealed class LocalAccountService : IAccountService
    {
        public Guid PlayerId => Guid.Empty;
        public bool HasSession => false;
        public bool IsLinked => false;

        public Task<SignInResult> SignInAsync(CancellationToken ct = default) => Task.FromResult(SignInResult.LocalGuest);

        public Task<bool> LogoutAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> TryConfirmedLogoutAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<long> DeleteAccountAsync(CancellationToken ct = default) => Task.FromResult(0L);

        public event Action<SignInResult> SessionLost
        {
            add { }
            remove { }
        }
    }
}
