using System;
using System.Collections.Generic;
using System.Text;
using HeroCat.Sim.Content;
using Newtonsoft.Json.Linq;

namespace HeroCat.Runtime.Content
{
    /// <summary>Which content set boot picked and why the others were passed over.</summary>
    public sealed class ContentChoice
    {
        public ContentChoice(ContentDb db, IContentSource source, bool bundled, IReadOnlyList<ContentError> rejected)
        {
            Db = db;
            Source = source;
            IsBundled = bundled;
            Rejected = rejected;
        }

        public ContentDb Db { get; }
        public IContentSource Source { get; }
        public bool IsBundled { get; }

        /// <summary>Every error that ruled a newer set out (E900-E903, build errors), for the content_update_failed log.</summary>
        public IReadOnlyList<ContentError> Rejected { get; }
    }

    /// <summary>
    /// The bundled-versus-cached gates of 09 section 10.3 as far as M0 needs them (issue #127): cold boot prefers the newest
    /// verified cached set that is newer than the bundle and whose min_client_version this client meets (step 8), and a
    /// candidate that fails any gate (E900-E903 or ContentDb.Build) is logged and skipped so the running content stays
    /// (acceptance 16). TODO(#212): the CDN download, temp-dir install and the CONTENT_CACHE_KEEP pruning feed this.
    /// </summary>
    public static class ContentSelection
    {
        /// <summary>content_version from a set's manifest, or null when the manifest is missing or unreadable.</summary>
        public static string VersionOf(IContentSource source)
        {
            try
            {
                if (!source.Exists("manifest.json")) return null;
                var v = (string)JObject.Parse(Encoding.UTF8.GetString(source.ReadBytes("manifest.json")))["content_version"];
                return ContentVersion.IsValid(v) ? v : null;
            }
            catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// Cold boot (09 section 10.3 step 8): the newest cached set newer than the bundle that loads cleanly for this client,
        /// else the bundle. The bundle failing is fatal (it is the floor every build ships with) and throws.
        /// </summary>
        public static ContentChoice LoadPreferred(IContentSource bundled, IEnumerable<IContentSource> cached, Tier buildTier,
                                                  string clientVersion, Action<ContentError> log = null)
        {
            if (bundled == null) throw new ArgumentNullException(nameof(bundled));
            string floor = VersionOf(bundled);
            var candidates = new List<KeyValuePair<string, IContentSource>>();
            foreach (var c in cached ?? Array.Empty<IContentSource>())
            {
                string v = VersionOf(c);
                if (v != null && (floor == null || ContentVersion.Compare(v, floor) > 0)) candidates.Add(new KeyValuePair<string, IContentSource>(v, c));
            }
            candidates.Sort((a, b) => ContentVersion.Compare(b.Key, a.Key));
            var rejected = new List<ContentError>();
            foreach (var c in candidates)
            {
                var db = TryLoad(c.Value, buildTier, clientVersion, rejected, log);
                if (db != null) return new ContentChoice(db, c.Value, false, rejected.AsReadOnly());
            }
            return new ContentChoice(ContentLoader.Load(bundled, buildTier, clientVersion), bundled, true, rejected.AsReadOnly());
        }

        /// <summary>
        /// A downloaded or cached candidate against the running content: returns the new db when it is newer and passes every
        /// gate, else <paramref name="current"/> itself (same instance), logging why (09 section 10.3 steps 2-5, acceptance 16).
        /// </summary>
        public static ContentDb TryNewer(IContentSource candidate, ContentDb current, Tier buildTier, string clientVersion,
                                         Action<ContentError> log = null)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            string v = candidate == null ? null : VersionOf(candidate);
            if (v == null || (ContentVersion.IsValid(current.Version) && ContentVersion.Compare(v, current.Version) <= 0)) return current;
            return TryLoad(candidate, buildTier, clientVersion, new List<ContentError>(), log) ?? current;
        }

        static ContentDb TryLoad(IContentSource src, Tier tier, string clientVersion, List<ContentError> rejected, Action<ContentError> log)
        {
            try
            {
                return ContentLoader.Load(src, tier, clientVersion);
            }
            catch (ContentBuildException e)
            {
                foreach (var err in e.Errors)
                {
                    rejected.Add(err);
                    log?.Invoke(err);
                }
                return null;
            }
        }
    }
}
