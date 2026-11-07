using System;
using System.Collections.Generic;
using System.Globalization;
using HeroCat.Sim.Commands;

namespace HeroCat.Runtime.Errors
{
    /// <summary>One row of 01 section 5.2's table (or a client-side code of section 7). Status 0 = no response.</summary>
    public sealed record ErrorRule(int Status, string Code, ErrorClass Class, RetryPolicy Retry, Presentation Presentation)
    {
        /// <summary>The key stem <c>err.&lt;code_lower&gt;</c>: the wire code lowercased (01 section 7, 00 decision 102).</summary>
        public string LocalizationKey => ErrorClassifier.ErrKey(Code);

        /// <summary><c>err.&lt;code_lower&gt;.title</c> (07 section 4.27).</summary>
        public string TitleKey => ErrorClassifier.ErrTitleKey(Code);

        /// <summary><c>err.&lt;code_lower&gt;.body</c> (07 section 4.27).</summary>
        public string BodyKey => ErrorClassifier.ErrBodyKey(Code);

        /// <summary>True when the row shows text to the player, so 01 AC 14 requires its key.</summary>
        public bool ShowsMessage => Presentation != Presentation.None;
    }

    /// <summary>What a view-model hands to SCR_ERROR or the inline reject feedback (07 renders it).</summary>
    /// <remarks>
    /// <see cref="LocalizationKey"/> is the full toast key <c>reject.&lt;reason_lower&gt;</c> for a sim reject, and the key
    /// stem <c>err.&lt;code_lower&gt;</c> for every other class, whose text is <see cref="TitleKey"/> and <see cref="BodyKey"/>.
    /// </remarks>
    public sealed record UserError(ErrorClass Class, string Code, string LocalizationKey, Presentation Presentation, RetryPolicy Retry,
                                   string SupportId)
    {
        /// <summary>Modal-class errors block input and carry the support id (01 section 7 rules).</summary>
        public bool IsModal => ErrorClassifier.IsModal(Presentation);

        /// <summary>The title (or, for a reject, the single toast) key.</summary>
        public string TitleKey => Class == ErrorClass.Reject ? LocalizationKey : LocalizationKey + ".title";

        /// <summary>The body key; null for a reject (one toast line).</summary>
        public string BodyKey => Class == ErrorClass.Reject ? null : LocalizationKey + ".body";
    }

    /// <summary>
    /// A one-time, acknowledge-only notice (not an error class): today only <see cref="ErrorClassifier.SAVE_CORRUPT_ALL"/>,
    /// shown once on the boot that replaced an unreadable save with a new profile, with the support id (02 section 8.3;
    /// 00 decision 103). Keys: <c>notice.&lt;code_lower&gt;.title</c> / <c>.body</c>; the body takes <c>{supportId}</c>.
    /// </summary>
    public sealed record Notice(string Code, string SupportId)
    {
        public string TitleKey => "notice." + ErrorClassifier.Lower(Code) + ".title";
        public string BodyKey => "notice." + ErrorClassifier.Lower(Code) + ".body";
    }

    /// <summary>
    /// The error taxonomy as code (01 sections 5.2 and 7, 00 decision 62; issue #69). Responses are classified by
    /// (status, code), code first: a known code decides the class, retry and presentation; an unknown code falls back to
    /// its status rule. <see cref="Table"/> is the single table the ApiClient and the localization check (01 AC 14) read.
    /// Codes are never shown raw: server and client codes localize as <c>err.&lt;code_lower&gt;.title</c> / <c>.body</c>,
    /// sim rejects as <c>reject.&lt;reason_lower&gt;</c> (or the snake-cased RejectCode, <c>reject.cannot_afford</c>, when a
    /// reject carries no reason). Wire codes stay UPPER_SNAKE; only the key is lowercased (00 decision 102, issue #248).
    /// </summary>
    public static class ErrorClassifier
    {
        public const string NET_OFFLINE = "NET_OFFLINE";
        public const string NET_TIMEOUT = "NET_TIMEOUT";
        public const string SRV_5XX = "SRV_5XX";

        /// <summary>The generic key for a 4xx whose code this build does not know (01 5.2 "other 4xx": modal).</summary>
        public const string REQUEST_REJECTED = "REQUEST_REJECTED";

        /// <summary>Generic key for a non-500 server failure with an unknown code (3xx, 501, ...).</summary>
        public const string SRV_UNEXPECTED = "SRV_UNEXPECTED";

        // Client-side codes produced without a response (01 section 7).
        public const string IAP_PENDING = "IAP_PENDING";
        public const string SAVE_CORRUPT = "SAVE_CORRUPT";
        public const string SAVE_TOO_NEW = "SAVE_TOO_NEW";
        public const string CONTENT_CORRUPT = "CONTENT_CORRUPT";

        /// <summary>Logout's choice modal when the save could not be uploaded first (01 section 8.13 step 1).</summary>
        public const string LOGOUT_UNSYNCED = "LOGOUT_UNSYNCED";

        /// <summary>
        /// Logout's choice modal when an unlinked guest's <c>POST /auth/logout</c> was not confirmed ([Retry] / [Cancel];
        /// 01 section 8.13 step 2, 00 decision 119).
        /// </summary>
        public const string LOGOUT_OFFLINE = "LOGOUT_OFFLINE";

        /// <summary>
        /// Not an error class (00 decision 103): no readable save copy means quarantine and a new profile (02 section 8.3),
        /// then a one-time <see cref="Notice"/> and the <c>save_corrupt_all</c> analytics event. Not in <see cref="Table"/>.
        /// </summary>
        public const string SAVE_CORRUPT_ALL = "SAVE_CORRUPT_ALL";

        static ErrorRule R(int status, string code, ErrorClass c, RetryPolicy r, Presentation p) => new ErrorRule(status, code, c, r, p);

        /// <summary>01 section 5.2 (status, code) rows in table order, then section 7's client-side codes.</summary>
        public static readonly IReadOnlyList<ErrorRule> Table = new[]
        {
            R(0, NET_OFFLINE, ErrorClass.Offline, RetryPolicy.IfIdempotent, Presentation.Card),
            R(0, NET_TIMEOUT, ErrorClass.Timeout, RetryPolicy.IfIdempotent, Presentation.ToastThenModal),
            R(401, "TOKEN_EXPIRED", ErrorClass.AuthExpired, RetryPolicy.RefreshOnce, Presentation.None),
            R(401, "UNAUTHENTICATED", ErrorClass.AuthExpired, RetryPolicy.None, Presentation.None),
            R(401, "TOKEN_REVOKED", ErrorClass.AuthExpired, RetryPolicy.None, Presentation.None),
            R(401, "TOKEN_REUSED", ErrorClass.AuthExpired, RetryPolicy.None, Presentation.None),
            R(400, "DISPLAY_NAME_BLOCKED", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(402, "INSUFFICIENT_FUNDS", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(403, "ACCOUNT_BANNED", ErrorClass.Fatal, RetryPolicy.None, Presentation.Fatal),
            R(403, "DUNGEON_LOCKED", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(404, "INBOX_NOT_FOUND", ErrorClass.Rejected, RetryPolicy.None, Presentation.None),
            R(409, "IDEMPOTENCY_IN_FLIGHT", ErrorClass.Retryable, RetryPolicy.Backoff, Presentation.None),
            R(409, "PAYMENT_MISMATCH", ErrorClass.Rejected, RetryPolicy.None, Presentation.None),
            R(409, "SAVE_CONFLICT", ErrorClass.Conflict, RetryPolicy.None, Presentation.ChoiceModal),
            R(409, "SAVE_STALE", ErrorClass.Rejected, RetryPolicy.None, Presentation.None),
            R(409, "SAVE_ROLLED_BACK", ErrorClass.Conflict, RetryPolicy.None, Presentation.ChoiceModal),
            R(409, "CONTENT_TOO_OLD", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(409, "INBOX_ALREADY_CLAIMED", ErrorClass.Ok, RetryPolicy.None, Presentation.None),
            R(409, "INBOX_NOT_AVAILABLE", ErrorClass.Rejected, RetryPolicy.None, Presentation.None),
            R(409, "INBOX_BAD_GRANT", ErrorClass.Rejected, RetryPolicy.None, Presentation.None),
            R(409, "AGE_GATE_LOCKED", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(409, "PROVIDER_ALREADY_LINKED", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(409, "BANNER_CLOSED", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(409, "SKU_NOT_AVAILABLE", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(410, "INBOX_EXPIRED", ErrorClass.Rejected, RetryPolicy.None, Presentation.None),
            R(413, "SAVE_TOO_LARGE", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(426, "CLIENT_TOO_OLD", ErrorClass.Rejected, RetryPolicy.None, Presentation.Block),
            R(429, "RATE_LIMITED", ErrorClass.Retryable, RetryPolicy.Backoff, Presentation.None),
            R(429, "DUNGEON_SEED_CAP", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(429, "EXPORT_RATE_LIMITED", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(503, "MAINTENANCE", ErrorClass.Maintenance, RetryPolicy.PollConfig, Presentation.Block),
            R(503, "SUMMON_DISABLED", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(503, "DUNGEONS_DISABLED", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(503, "STORE_UNAVAILABLE", ErrorClass.Retryable, RetryPolicy.ReceiptRetry, Presentation.None),
            R(400, "RECEIPT_INVALID", ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(500, SRV_5XX, ErrorClass.ServerError, RetryPolicy.Once, Presentation.ToastThenModal),
            // Section 7 client-side codes (no HTTP response).
            R(0, IAP_PENDING, ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(0, SAVE_CORRUPT, ErrorClass.Conflict, RetryPolicy.None, Presentation.ChoiceModal),
            R(0, SAVE_TOO_NEW, ErrorClass.Fatal, RetryPolicy.None, Presentation.Fatal),
            R(0, CONTENT_CORRUPT, ErrorClass.Fatal, RetryPolicy.None, Presentation.Fatal),
            R(0, LOGOUT_UNSYNCED, ErrorClass.Conflict, RetryPolicy.None, Presentation.ChoiceModal),
            R(0, LOGOUT_OFFLINE, ErrorClass.Conflict, RetryPolicy.None, Presentation.ChoiceModal),
            // Status-rule fallbacks with a generic key (an unknown code must still never show raw).
            R(0, REQUEST_REJECTED, ErrorClass.Rejected, RetryPolicy.None, Presentation.Modal),
            R(0, SRV_UNEXPECTED, ErrorClass.ServerError, RetryPolicy.None, Presentation.ToastThenModal),
        };

        static readonly Dictionary<string, ErrorRule> ByCode = BuildIndex();

        static Dictionary<string, ErrorRule> BuildIndex()
        {
            var d = new Dictionary<string, ErrorRule>(StringComparer.Ordinal);
            foreach (var r in Table)
            {
                if (d.ContainsKey(r.Code)) throw new InvalidOperationException("duplicate error code " + r.Code);
                d[r.Code] = r;
            }
            return d;
        }

        /// <summary>The key stem <c>err.&lt;code_lower&gt;</c>; the wire code itself stays UPPER_SNAKE.</summary>
        public static string ErrKey(string code) => "err." + Lower(code);

        /// <summary><c>err.&lt;code_lower&gt;.title</c>, SCR_ERROR's title (07 section 4.27).</summary>
        public static string ErrTitleKey(string code) => ErrKey(code) + ".title";

        /// <summary><c>err.&lt;code_lower&gt;.body</c>, SCR_ERROR's body (07 section 4.27).</summary>
        public static string ErrBodyKey(string code) => ErrKey(code) + ".body";

        /// <summary>
        /// Key for a sim reject: <c>reject.&lt;reason_lower&gt;</c>, else the snake-cased RejectCode
        /// (<c>CannotAfford</c> -> <c>reject.cannot_afford</c>) (01 section 2.1).
        /// </summary>
        public static string RejectKey(ApplyResult r) =>
            string.IsNullOrEmpty(r.Reason) ? RejectCodeKey(r.Code) : RejectReasonKey(r.Reason);

        /// <summary><c>reject.&lt;reason_lower&gt;</c> for a reject reason (e.g. one the client predicts before sending).</summary>
        public static string RejectReasonKey(string reason) => "reject." + Lower(reason);

        /// <summary>The fallback key for a RejectCode with no reason, <c>reject.&lt;snake_case&gt;</c>.</summary>
        public static string RejectCodeKey(RejectCode c) => "reject." + Snake(c.ToString());

        /// <summary>UPPER_SNAKE code to the lowercase key segment (09's key pattern is lowercase only).</summary>
        public static string Lower(string code) => code.ToLowerInvariant();

        static string Snake(string pascal)
        {
            var sb = new System.Text.StringBuilder(pascal.Length + 4);
            for (int i = 0; i < pascal.Length; i++)
            {
                char ch = pascal[i];
                if (char.IsUpper(ch) && i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>True for a code the table knows.</summary>
        public static bool IsKnown(string code) => code != null && ByCode.ContainsKey(code);

        /// <summary>The row for a known code, else null.</summary>
        public static ErrorRule Find(string code) => code != null && ByCode.TryGetValue(code, out var r) ? r : null;

        /// <summary>
        /// Classifies an HTTP response: a known code decides, else the status rule (5.2's last four rows). The returned
        /// rule's Code is the localization code (the body's code when it is known, else the status rule's generic code).
        /// </summary>
        public static ErrorRule Classify(int status, string code)
        {
            var known = Find(code);
            if (known != null) return known;
            if (status >= 200 && status < 300) return new ErrorRule(status, code ?? "OK", ErrorClass.Ok, RetryPolicy.None, Presentation.None);
            switch (status)
            {
                case 408: case 425: case 429: case 502: case 503: case 504:
                    return new ErrorRule(status, code ?? "HTTP_" + status.ToString(CultureInfo.InvariantCulture), ErrorClass.Retryable,
                                         RetryPolicy.Backoff, Presentation.None);
                case 500:
                    return ByCode[SRV_5XX];
            }
            if (status >= 400 && status < 500) return ByCode[REQUEST_REJECTED];
            return ByCode[SRV_UNEXPECTED];
        }

        /// <summary>No response: DNS / connection failure (Offline) or HTTP_TIMEOUT_S elapsed (Timeout).</summary>
        public static ErrorRule ClassifyTransport(bool timedOut) => ByCode[timedOut ? NET_TIMEOUT : NET_OFFLINE];

        /// <summary>A <see cref="ServiceException"/> from a service seam: its code when known, else its class's rule.</summary>
        public static ErrorRule Classify(ServiceException e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            var known = Find(e.Code);
            if (known != null) return known;
            if (e.Status.HasValue) return Classify(e.Status.Value, e.Code);
            switch (e.Kind)
            {
                case ErrorClass.Offline: return ByCode[NET_OFFLINE];
                case ErrorClass.Timeout: return ByCode[NET_TIMEOUT];
                case ErrorClass.ServerError: return ByCode[SRV_5XX];
                default: return new ErrorRule(0, e.Code ?? REQUEST_REJECTED, e.Kind, RetryPolicy.None, PresentationOf(e.Kind));
            }
        }

        /// <summary>The section 7 presentation of a class, for codes outside the table.</summary>
        public static Presentation PresentationOf(ErrorClass c)
        {
            switch (c)
            {
                case ErrorClass.Reject: return Presentation.Inline;
                case ErrorClass.Offline: return Presentation.Card;
                case ErrorClass.Timeout: case ErrorClass.ServerError: return Presentation.ToastThenModal;
                case ErrorClass.Rejected: return Presentation.Modal;
                case ErrorClass.Conflict: return Presentation.ChoiceModal;
                case ErrorClass.Maintenance: return Presentation.Block;
                case ErrorClass.Fatal: return Presentation.Fatal;
                default: return Presentation.None;
            }
        }

        public static bool IsModal(Presentation p) =>
            p == Presentation.Modal || p == Presentation.ChoiceModal || p == Presentation.ToastThenModal ||
            p == Presentation.Block || p == Presentation.Fatal;

        // ------------------------------------------------------------------------------------- UserError

        /// <summary>A sim reject: class Reject, inline presentation, never a modal, no support id.</summary>
        public static UserError ForReject(ApplyResult r)
        {
            if (r.Ok) throw new ArgumentException("an accepted result is not an error", nameof(r));
            return new UserError(ErrorClass.Reject, string.IsNullOrEmpty(r.Reason) ? r.Code.ToString() : r.Reason, RejectKey(r),
                                 Presentation.Inline, RetryPolicy.None, null);
        }

        /// <summary>The user-facing error for a rule; modal-class errors carry the support id.</summary>
        public static UserError ForRule(ErrorRule rule, SupportId support)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            string sid = IsModal(rule.Presentation) ? support.Format() : null;
            return new UserError(rule.Class, rule.Code, rule.LocalizationKey, rule.Presentation, rule.Retry, sid);
        }

        /// <summary>A client-side or known code (for example boot's CONTENT_CORRUPT).</summary>
        public static UserError ForCode(string code, SupportId support) =>
            ForRule(Find(code) ?? throw new ArgumentException("unknown error code " + code, nameof(code)), support);
    }

    /// <summary>
    /// The support id printed in small type on every modal (01 section 7): the session id's first 8 hex chars and the
    /// UTC minute, <c>3f2a9c1e-202611011342</c>. Stable format: <c>[0-9a-f]{8}-yyyyMMddHHmm</c>.
    /// </summary>
    public readonly struct SupportId
    {
        public SupportId(Guid sessionId, long serverSeconds)
        {
            SessionId = sessionId;
            ServerSeconds = serverSeconds;
        }

        public Guid SessionId { get; }
        public long ServerSeconds { get; }

        public string Format()
        {
            string prefix = SessionId.ToString("N").Substring(0, 8);
            var t = DateTimeOffset.FromUnixTimeSeconds(ServerSeconds).UtcDateTime;
            return prefix + "-" + t.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        }

        public override string ToString() => Format();
    }
}
