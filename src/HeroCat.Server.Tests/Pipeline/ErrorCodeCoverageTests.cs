using System.Reflection;
using HeroCat.Runtime.Errors;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Pipeline;

/// <summary>
/// specs/08 acceptance 23 as amended by 00 decision 115 (issue #264): every global or endpoint error code the server
/// defines is either a row of the client's <see cref="ErrorClassifier.Table"/> (whose shown rows carry their
/// <c>err.*</c> keys, 01 AC 14 in the Runtime tests) or deliberately left to 01 section 5.2's status fallback, whose
/// generic keys always exist. A new code must be placed in one of the two; it never reaches the player raw.
/// </summary>
[TestFixture]
public sealed class ErrorCodeCoverageTests
{
    /// <summary>
    /// Codes the client never shows with their own text: a client bug or a server-side state the client handles before
    /// classifying, an admin route, or a <c>details.reason</c> under <c>FORBIDDEN</c>. Each falls back to the status rule.
    /// </summary>
    static readonly Dictionary<string, string> StatusFallback = new(StringComparer.Ordinal)
    {
        ["FORBIDDEN"] = "403 generic modal; DEVICE_MISMATCH and KEY_ROTATION_NOT_ALLOWED ride in details.reason",
        ["NOT_FOUND"] = "404 of an unknown route or id: a client bug",
        ["VALIDATION"] = "400: a client bug, the generic modal",
        ["INTERNAL"] = "500: SRV_5XX",
        ["CONTENT_HASH_MISMATCH"] = "admin config activation only",
        ["NO_SAVE"] = "GET /save 404: the download flow (01 section 8.10) reads it as 'no cloud save yet', never shown",
        ["SAVE_MALFORMED"] = "400: a client bug, the generic modal",
        ["DEVICE_ALREADY_BOUND"] = "409 on /auth/guest: AuthClient answers SignInRequired and the client opens SCR_TITLE",
        ["DEVICE_MISMATCH"] = "details.reason of 403 FORBIDDEN, or the 403 of /auth/refresh: generic modal",
        ["KEY_ROTATION_NOT_ALLOWED"] = "details.reason of 403 FORBIDDEN on /devices",
    };

    static IEnumerable<string> ServerCodes()
    {
        var types = typeof(HeroCat.Server.Pipeline.ErrorCodes).Assembly.GetTypes()
            .Where(t => t.IsAbstract && t.IsSealed && (t.Name == "ErrorCodes" || t.Name.EndsWith("Errors", StringComparison.Ordinal)));
        foreach (var t in types)
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.IsLiteral && f.FieldType == typeof(string) && (string)f.GetRawConstantValue()! == f.Name)
                    yield return f.Name;
    }

    [Test]
    public void The_scan_finds_the_known_error_classes()
    {
        var codes = ServerCodes().ToHashSet();
        foreach (string c in new[] { "UNAUTHENTICATED", "INSUFFICIENT_FUNDS", "SAVE_TOO_LARGE", "DUNGEONS_DISABLED", "INBOX_EXPIRED", "TOKEN_REUSED", "CONTENT_HASH_MISMATCH" })
            Assert.That(codes, Does.Contain(c));
    }

    [Test]
    public void Every_server_code_is_a_client_table_row_or_left_to_the_status_fallback()
    {
        var unplaced = ServerCodes().Distinct().Where(c => !ErrorClassifier.IsKnown(c) && !StatusFallback.ContainsKey(c)).OrderBy(c => c).ToList();
        Assert.That(unplaced, Is.Empty,
                    "add a row to ErrorClassifier.Table (and 01 section 5.2, with err.* keys if it shows a message) or to StatusFallback here");
        var both = StatusFallback.Keys.Where(ErrorClassifier.IsKnown).ToList();
        Assert.That(both, Is.Empty, "a Table row is not a fallback");
    }

    [Test]
    public void The_status_fallbacks_show_only_generic_keyed_codes()
    {
        foreach (int status in new[] { 400, 403, 404, 409, 500 })
        {
            var r = ErrorClassifier.Classify(status, "SOMETHING_NEW");
            Assert.That(ErrorClassifier.IsKnown(r.Code), Is.True, $"{status} falls back to a Table code with keys ({r.Code})");
        }
    }
}
