using System;

namespace HeroCat.Runtime.Errors
{
    /// <summary>
    /// The error classes of 01 sections 5.1 and 7 (issue #69). The class picks the presentation; the code picks the
    /// localization keys <c>err.&lt;code_lower&gt;.title</c> / <c>.body</c> (server and client codes) or <c>reject.&lt;reason_lower&gt;</c>
    /// (sim rejects); wire codes stay UPPER_SNAKE (00 decision 102).
    /// <see cref="Reject"/> is a sim reject (01 section 7); <see cref="Rejected"/> is a server rejection.
    /// </summary>
    public enum ErrorClass
    {
        Ok, Reject, Offline, Timeout, ServerError, Retryable, AuthExpired, Rejected, Conflict, Maintenance, Fatal,
    }

    /// <summary>How a failed request is retried (01 section 5.2 "Retry" column).</summary>
    public enum RetryPolicy
    {
        /// <summary>Never retried; the player changes something or acknowledges.</summary>
        None,

        /// <summary>Offline and timeout: retried only when the request is idempotent (same requestId).</summary>
        IfIdempotent,

        /// <summary>401 TOKEN_EXPIRED: refresh the token once, replay the request once.</summary>
        RefreshOnce,

        /// <summary>After retryAfterSec / Retry-After if present, else the backoff schedule, same requestId.</summary>
        Backoff,

        /// <summary>500: retried once.</summary>
        Once,

        /// <summary>503 MAINTENANCE: poll /config every MAINT_POLL_S.</summary>
        PollConfig,

        /// <summary>503 STORE_UNAVAILABLE: keep the pending receipt, retry every RECEIPT_RETRY_S.</summary>
        ReceiptRetry,
    }

    /// <summary>How a <see cref="UserError"/> is shown (01 section 7 "Presentation" column, 07 places them).</summary>
    public enum Presentation
    {
        /// <summary>Nothing visible (success-equivalent, silent rejections, retries still running).</summary>
        None,

        /// <summary>Sim reject: button shake plus a short toast next to the button. Never a modal.</summary>
        Inline,

        /// <summary>Offline: non-blocking card in the action-strip position, auto-hides on reconnect.</summary>
        Card,

        /// <summary>Timeout / ServerError: toast while retrying, then a Retry / Later modal when retries are exhausted.</summary>
        ToastThenModal,

        /// <summary>Server rejection: modal with a specific message.</summary>
        Modal,

        /// <summary>Conflict: a dedicated choice modal (save conflict, corrupt save, rollback notice).</summary>
        ChoiceModal,

        /// <summary>Maintenance and update-only blocks: full screen on SCR_BOOT / SCR_TITLE.</summary>
        Block,

        /// <summary>Fatal: full screen with the support id; the app cannot continue.</summary>
        Fatal,
    }

    /// <summary>
    /// Raised by service implementations (remote ones, and the local inbox for an unknown id). The sim and the client
    /// never see transport exceptions, only this. <see cref="Code"/> is an 08 wire code or a client code from 01 5.2/7.
    /// </summary>
    public sealed class ServiceException : Exception
    {
        public ServiceException(ErrorClass kind, string code, string message = null, int? status = null)
            : base(message ?? $"{kind}: {code}")
        {
            Kind = kind;
            Code = code;
            Status = status;
        }

        public ErrorClass Kind { get; }

        /// <summary>Machine-readable UPPER_SNAKE code, e.g. "INBOX_NOT_FOUND", "NET_OFFLINE".</summary>
        public string Code { get; }

        /// <summary>The HTTP status, when the failure came from a response.</summary>
        public int? Status { get; }

        public bool Retryable =>
            Kind == ErrorClass.Offline || Kind == ErrorClass.Timeout || Kind == ErrorClass.ServerError || Kind == ErrorClass.Retryable;
    }
}
