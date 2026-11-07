using HeroCat.Server.Pipeline;
using HeroCat.Sim.Content;
using Microsoft.AspNetCore.Mvc;

namespace HeroCat.Server.Features.Summon;

/// <summary><c>GET /summon/rates</c> (specs/08 section 4.5, issue #200): the published odds of one banner level.</summary>
[ApiController]
public sealed class SummonRatesController : ControllerBase
{
    public const string CacheControl = "max-age=3600";

    readonly IContentSets _content;

    public SummonRatesController(IContentSets content) => _content = content;

    /// <summary>
    /// The rate table, pity, 10-pull guarantee, <c>effective</c> numbers and pool of <c>banner</c> at <c>level</c>
    /// (default 1) in the newest held content set, or in <c>contentVersion</c> when given (the set a logged pull used).
    /// Errors: <c>400 VALIDATION</c> (unknown banner, level out of range, unparseable content version),
    /// <c>404 NOT_FOUND</c> (a banner with no <c>banner_levels</c> rows, or an <c>event</c> table, P1), and the content
    /// version errors of <c>/summon/pull</c>.
    /// </summary>
    [HttpGet("v1/summon/rates"), Auth(AuthPolicy.None), RateLimit(60)]
    [ProducesResponseType(typeof(SummonRatesResponse), StatusCodes.Status200OK)]
    public SummonRatesResponse Rates([FromQuery] string? banner, [FromQuery] int? level, [FromQuery] string? contentVersion, [FromQuery] string? @event)
    {
        if (!string.IsNullOrEmpty(@event))
            // P1 (08 section 8.1): event rate-up tables are banner_levels rows with an eventId (09), published with their own hash.
            throw ApiException.Of(ErrorCodes.NOT_FOUND, "Event rate tables are not published yet.", new { field = "event" });
        var b = banner != null && Enum.TryParse<Banner>(banner, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) && banner == parsed.ToString()
            ? parsed
            : throw ApiException.Of(ErrorCodes.VALIDATION, "Unknown banner.", new { field = "banner" });
        var db = string.IsNullOrEmpty(contentVersion) ? _content.Active : _content.Resolve(contentVersion);
        if (!db.Banners.TryGetValue(b, out var def) || def.Levels.Count == 0)
            throw ApiException.Of(ErrorCodes.NOT_FOUND, "This banner has no published rates.", new { banner = b.ToString() });
        int lv = level ?? 1;
        if (lv < 1 || lv > def.Levels.Count)
            throw ApiException.Of(ErrorCodes.VALIDATION, $"level must be 1..{def.Levels.Count}.", new { field = "level" });

        Response.Headers.CacheControl = CacheControl;
        return PublishedRates.Build(db, def, lv);
    }
}
