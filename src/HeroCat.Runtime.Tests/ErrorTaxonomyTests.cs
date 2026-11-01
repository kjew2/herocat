using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HeroCat.Runtime.Errors;
using HeroCat.Sim.Commands;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>The error taxonomy, the (status, code) classifier and the presentation policy (01 sections 5.2 and 7; issue #69).</summary>
    public class ErrorTaxonomyTests
    {
        [Test]
        public void EveryRejectCode_IsAnInlineReject_NeverAModal_KeyedByReason()   // issue #69 acceptance 2
        {
            foreach (RejectCode code in Enum.GetValues(typeof(RejectCode)))
            {
                if (code == RejectCode.None) continue;
                var e = ErrorClassifier.ForReject(ApplyResult.Reject(code));
                Assert.That(e.Class, Is.EqualTo(ErrorClass.Reject), code.ToString());
                Assert.That(e.Presentation, Is.EqualTo(Presentation.Inline), code.ToString());
                Assert.That(e.IsModal, Is.False, code.ToString());
                Assert.That(e.LocalizationKey, Is.EqualTo("reject." + code), code.ToString());
            }
            var withReason = ErrorClassifier.ForReject(ApplyResult.Reject(RejectCode.CannotAfford, "NOT_ENOUGH_GOLD"));
            Assert.That(withReason.LocalizationKey, Is.EqualTo("reject.NOT_ENOUGH_GOLD"), "the owning spec's reason wins (2.1)");
        }

        // (status, code) -> class, retry: the 01 section 5.2 rows (issue #30's mapping).
        static readonly object[] Rows =
        {
            new object[] { 401, "TOKEN_EXPIRED", ErrorClass.AuthExpired, RetryPolicy.RefreshOnce },
            new object[] { 402, "INSUFFICIENT_FUNDS", ErrorClass.Rejected, RetryPolicy.None },
            new object[] { 403, "ACCOUNT_BANNED", ErrorClass.Fatal, RetryPolicy.None },
            new object[] { 409, "IDEMPOTENCY_IN_FLIGHT", ErrorClass.Retryable, RetryPolicy.Backoff },
            new object[] { 409, "SAVE_CONFLICT", ErrorClass.Conflict, RetryPolicy.None },
            new object[] { 409, "INBOX_ALREADY_CLAIMED", ErrorClass.Ok, RetryPolicy.None },
            new object[] { 409, "BANNER_CLOSED", ErrorClass.Rejected, RetryPolicy.None },
            new object[] { 426, "CLIENT_TOO_OLD", ErrorClass.Rejected, RetryPolicy.None },
            new object[] { 429, "RATE_LIMITED", ErrorClass.Retryable, RetryPolicy.Backoff },
            new object[] { 503, "MAINTENANCE", ErrorClass.Maintenance, RetryPolicy.PollConfig },
            new object[] { 503, "STORE_UNAVAILABLE", ErrorClass.Retryable, RetryPolicy.ReceiptRetry },
            new object[] { 400, "RECEIPT_INVALID", ErrorClass.Rejected, RetryPolicy.None },
            new object[] { 500, null, ErrorClass.ServerError, RetryPolicy.Once },
            new object[] { 503, "SOMETHING_NEW", ErrorClass.Retryable, RetryPolicy.Backoff },
            new object[] { 502, null, ErrorClass.Retryable, RetryPolicy.Backoff },
            new object[] { 404, "SOMETHING_NEW", ErrorClass.Rejected, RetryPolicy.None },
        };

        [TestCaseSource(nameof(Rows))]
        public void StatusAndCode_MapToTheAgreedClass_AndRetry(int status, string code, ErrorClass cls, RetryPolicy retry)
        {
            var r = ErrorClassifier.Classify(status, code);
            Assert.That(r.Class, Is.EqualTo(cls));
            Assert.That(r.Retry, Is.EqualTo(retry));
        }

        [Test]
        public void AnUnknownCode_IsNeverShownRaw()   // 01 section 7 rule
        {
            foreach (int status in new[] { 400, 404, 409, 418, 500, 503 })
            {
                var r = ErrorClassifier.Classify(status, "BRAND_NEW_CODE");
                if (!r.ShowsMessage) continue;
                Assert.That(ErrorClassifier.IsKnown(r.Code), Is.True, status + ": a shown error uses a table key, got " + r.Code);
            }
        }

        [Test]
        public void TransportFailures_AreOfflineAndTimeout()
        {
            Assert.That(ErrorClassifier.ClassifyTransport(timedOut: false).Class, Is.EqualTo(ErrorClass.Offline));
            Assert.That(ErrorClassifier.ClassifyTransport(timedOut: true).Class, Is.EqualTo(ErrorClass.Timeout));
            Assert.That(ErrorClassifier.ClassifyTransport(false).Presentation, Is.EqualTo(Presentation.Card), "a card, not a modal");
        }

        [Test]
        public void TableCodes_AreUnique_UpperSnake_AndKeyedErrDotCode()
        {
            var codes = ErrorClassifier.Table.Select(r => r.Code).ToList();
            Assert.That(codes, Is.Unique);
            foreach (var r in ErrorClassifier.Table)
            {
                Assert.That(r.Code, Does.Match("^[A-Z][A-Z0-9_]*$"));
                Assert.That(r.LocalizationKey, Is.EqualTo("err." + r.Code));
            }
            foreach (var fatal in new[] { ErrorClassifier.SAVE_TOO_NEW, ErrorClassifier.CONTENT_CORRUPT, ErrorClassifier.SAVE_CORRUPT_ALL })
                Assert.That(ErrorClassifier.Find(fatal).Class, Is.EqualTo(ErrorClass.Fatal), fatal);
        }

        [Test]
        [Ignore("Blocked on a spec decision: 01/00/07 spell keys err.<CODE> and reject.<UPPER_SNAKE>, but 09's key regex " +
                "(E071) is lowercase-only, and 07 section 19 wants err.<code>.title/.body. See the runtime track report.")]
        public void EveryShownCode_HasAnErrKey_InTheEnglishLocalization()   // 01 AC 14
        {
            var path = Path.Combine(TestContent.Dir, "localization.en.json");
            var keys = new HashSet<string>(((JArray)JObject.Parse(File.ReadAllText(path))["rows"])
                                           .Select(r => (string)r["key"]), StringComparer.Ordinal);
            var missing = ErrorClassifier.Table.Where(r => r.ShowsMessage && !keys.Contains(r.LocalizationKey))
                                         .Select(r => r.LocalizationKey).ToList();
            Assert.That(missing, Is.Empty, "add the rows to content_src/localization.csv");
        }

        [Test]
        public void SupportId_IsSessionPrefixPlusMinute_AndStable()   // 01 section 7
        {
            var session = new Guid("0123abcd-0000-4000-8000-000000000001");
            long t = TestContent.T0 + 59;
            var id = new SupportId(session, t).Format();
            Assert.That(id, Is.EqualTo(new SupportId(session, TestContent.T0).Format()), "same minute, same id");
            Assert.That(id, Does.StartWith("0123abcd"));
            Assert.That(new SupportId(session, TestContent.T0 + 60).Format(), Is.Not.EqualTo(id));
            var modal = ErrorClassifier.ForCode("RECEIPT_INVALID", new SupportId(session, t));
            Assert.That(modal.IsModal, Is.True);
            Assert.That(modal.SupportId, Is.EqualTo(id), "every modal-class error carries the support id");
        }

        [Test]
        public void Policy_NeverShowsTwoModals_AndARejectNeverOpensOne()   // 01 section 7 rules
        {
            var p = new ErrorPolicy();
            var inline = new List<UserError>();
            var shown = new List<UserError>();
            p.Inline += inline.Add;
            p.ModalShown += shown.Add;
            var sid = new SupportId(Guid.NewGuid(), TestContent.T0);

            Assert.That(p.Report(ErrorClassifier.ForReject(ApplyResult.Reject(RejectCode.CannotAfford))), Is.True);
            Assert.That(p.CurrentModal, Is.Null);
            Assert.That(inline, Has.Count.EqualTo(1));

            Assert.That(p.Report(ErrorClassifier.ForCode("RECEIPT_INVALID", sid)), Is.True);
            Assert.That(p.Report(ErrorClassifier.ForCode("BANNER_CLOSED", sid)), Is.False, "waits behind the first");
            Assert.That(p.Report(ErrorClassifier.ForCode("BANNER_CLOSED", sid)), Is.False, "not queued twice");
            Assert.That(p.CurrentModal.Code, Is.EqualTo("RECEIPT_INVALID"));
            Assert.That(p.WaitingCount, Is.EqualTo(1));
            p.Dismiss();
            Assert.That(p.CurrentModal.Code, Is.EqualTo("BANNER_CLOSED"));
            Assert.That(p.Report(ErrorClassifier.ForCode(ErrorClassifier.CONTENT_CORRUPT, sid)), Is.True, "fatal replaces");
            Assert.That(p.CurrentModal.Code, Is.EqualTo(ErrorClassifier.CONTENT_CORRUPT));
            Assert.That(shown.Select(e => e.Code), Is.EqualTo(new[] { "RECEIPT_INVALID", "BANNER_CLOSED", ErrorClassifier.CONTENT_CORRUPT }));
        }
    }
}
