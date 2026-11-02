using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using HeroCat.Sim.Content.Dto;

namespace HeroCat.Runtime.Content
{
    /// <summary>
    /// The storage half of 09 section 10.3 (steps 4, 6 and 8), which issue #212 owns: CDN download into a staging area,
    /// promotion to <c>persistentDataPath/content/&lt;version&gt;/</c>, the cold-boot list. <see cref="ContentUpdater"/>
    /// (issue #144) drives it and does the validation and the hot-or-cold decision in between.
    /// TODO(#212): the CDN implementation (manifest sha256 against <c>config.content.sha256</c>, per-file download, the
    /// <c>&lt;version&gt;.tmp</c> rename, CONTENT_CACHE_KEEP pruning). <see cref="MemoryContentStaging"/> is the test double.
    /// </summary>
    public interface IContentStaging
    {
        /// <summary>
        /// Downloads <paramref name="bundle"/> into a staging area (not yet visible to cold boot) and returns it. Throws on a
        /// network failure or a manifest that does not match <c>bundle.Sha256</c>. Runs on the thread pool.
        /// </summary>
        Task<IContentSource> DownloadAsync(ContentPointer bundle, CancellationToken ct);

        /// <summary>Promotes the staged version to the cache and marks it pending: the next cold boot prefers it (step 8).</summary>
        void MarkPending(string version);

        /// <summary>Deletes the staged version: it failed validation and must never be booted.</summary>
        void Discard(string version);
    }

    public enum ContentUpdateState
    {
        /// <summary>Nothing newer offered (or not checked yet).</summary>
        Idle,

        /// <summary>Downloading and validating on the thread pool.</summary>
        Staging,

        /// <summary>The offered bundle needs a newer client (09 section 10.3 step 3): SCR_SETTINGS says "update the app for new content".</summary>
        NeedsClientUpdate,

        /// <summary>Download or validation failed; the staged copy was discarded and the running content stays.</summary>
        Failed,

        /// <summary>Verified and marked pending: applies at the next cold boot. SCR_SETTINGS shows "Update ready".</summary>
        PendingColdBoot,

        /// <summary>A verified hot-apply bundle waiting for a safe point (<see cref="ContentUpdater.TryApplyHot"/>).</summary>
        HotReady,

        /// <summary>The hot tables were swapped this session (and the bundle is also pending for the next cold boot).</summary>
        HotApplied,
    }

    /// <summary>
    /// The hot-applied tables of 00 contract 3, kept outside the sim's <see cref="ContentDb"/> (01 section 8.12 step 2): the
    /// Localizer re-reads its strings from <see cref="Source"/> (<see cref="Localizer.Replace"/>), and the Shop and Inbox
    /// view-models re-read these rows.
    /// </summary>
    public sealed class HotContentSet
    {
        public HotContentSet(string version, IContentSource source, ContentDtos dtos)
        {
            Version = version;
            Source = source;
            ShopProducts = dtos?.ShopProducts ?? Array.Empty<ShopProductRowDto>();
            ShopPackages = dtos?.ShopPackages ?? Array.Empty<ShopPackageRowDto>();
            InboxTemplates = dtos?.InboxTemplates ?? Array.Empty<InboxTemplateRowDto>();
        }

        public string Version { get; }
        public IContentSource Source { get; }
        public IReadOnlyList<ShopProductRowDto> ShopProducts { get; }
        public IReadOnlyList<ShopPackageRowDto> ShopPackages { get; }
        public IReadOnlyList<InboxTemplateRowDto> InboxTemplates { get; }
    }

    /// <summary>
    /// The client update flow of 01 section 8.12 and 09 section 10.3 (issue #144). On every landed config it compares the
    /// offered <c>content.version</c> with the running content (step 2) and the client gate (step 3), then on the thread
    /// pool downloads through <see cref="IContentStaging"/>, verifies the manifest and every table hash and runs
    /// <see cref="ContentDb.Build"/> (09's validator rules, step 5). A failure discards the staged copy, so the next boot
    /// uses what it used before. A good bundle is marked pending for the next cold boot. The running sim's
    /// <see cref="ContentDb"/> is never replaced (01 AC 11): the new build is thrown away after validation.
    /// <para>Hot apply (00 contract 3): only a bundle whose manifest sets <c>hot_apply</c> AND whose tables differ from the
    /// running set's only in <see cref="HotApplyTables"/> becomes <see cref="ContentUpdateState.HotReady"/>; anything else
    /// is cold-boot only, whatever the manifest says. The swap happens at a safe point the client names
    /// (<see cref="TryApplyHot"/>: no modal open, not in a boss).</para>
    /// </summary>
    public sealed class ContentUpdater
    {
        /// <summary>00 contract 3: the tables a hot apply may change. <c>localization</c> covers every locale file.</summary>
        public static IReadOnlyCollection<string> HotApplyTables => HotTables;

        static readonly HashSet<string> HotTables =
            new HashSet<string>(StringComparer.Ordinal) { "localization", "shop_products", "shop_packages", "inbox_templates" };

        readonly IContentStaging _staging;
        readonly ContentDb _running;
        readonly ManifestDto _runningManifest;
        readonly string _clientVersion;
        readonly Action<ContentError> _log;
        readonly object _lock = new object();
        readonly HashSet<string> _failed = new HashSet<string>(StringComparer.Ordinal);
        ContentUpdateState _state;
        string _version;
        HotContentSet _hot;
        Task _task = Task.CompletedTask;
        IReadOnlyList<ContentError> _errors = Array.Empty<ContentError>();

        /// <param name="running">The sim's content: its Version is the floor, its BuildTier the tier a candidate is built for.</param>
        /// <param name="runningManifest">The running set's manifest, for the hot-apply table diff; null makes every bundle cold-boot only.</param>
        /// <param name="clientVersion">This build's semver for E903 and step 3; null skips both (bots, tools).</param>
        /// <param name="log">Each error that ruled a bundle out (the <c>content_update_failed</c> report). Thread pool.</param>
        public ContentUpdater(IContentStaging staging, ContentDb running, ManifestDto runningManifest, string clientVersion,
                              Action<ContentError> log = null)
        {
            _staging = staging ?? throw new ArgumentNullException(nameof(staging));
            _running = running ?? throw new ArgumentNullException(nameof(running));
            _runningManifest = runningManifest;
            _clientVersion = clientVersion;
            _log = log;
        }

        public ContentUpdateState State
        {
            get { lock (_lock) return _state; }
        }

        /// <summary>The version being staged, pending, hot-ready or applied (null when Idle).</summary>
        public string Version
        {
            get { lock (_lock) return _version; }
        }

        /// <summary>SCR_SETTINGS' "Update ready, restarts on next launch" (01 section 8.12 step 2).</summary>
        public bool UpdateReady => State == ContentUpdateState.PendingColdBoot;

        /// <summary>The errors of the last failed bundle.</summary>
        public IReadOnlyList<ContentError> LastErrors
        {
            get { lock (_lock) return _errors; }
        }

        /// <summary>The hot set once applied; null before.</summary>
        public HotContentSet Applied { get; private set; }

        /// <summary>Raised by <see cref="TryApplyHot"/> on the main thread with the swapped tables.</summary>
        public event Action<HotContentSet> HotApplied;

        /// <summary>
        /// A landed config (boot, every refresh). Starts staging on the thread pool when the offered bundle is newer than the
        /// running one, this client may load it, and it is not already staged, pending or failed this session. Returns the
        /// staging task (completed when nothing starts); it never faults.
        /// </summary>
        public Task Check(ClientConfig config, CancellationToken ct = default)
        {
            var p = config?.Content;
            if (p == null || !ContentVersion.IsValid(p.Version)) return Task.CompletedTask;
            if (ContentVersion.IsValid(_running.Version) && ContentVersion.Compare(p.Version, _running.Version) <= 0) return Task.CompletedTask;
            lock (_lock)
            {
                if (_state == ContentUpdateState.Staging) return _task;
                if (_failed.Contains(p.Version)) return Task.CompletedTask;   // a broken bundle is not re-downloaded every refresh
                if (_version != null && ContentVersion.Compare(p.Version, _version) <= 0 && _state != ContentUpdateState.NeedsClientUpdate)
                    return Task.CompletedTask;   // already have it (or newer) pending or hot
                if (_clientVersion != null && !string.IsNullOrEmpty(p.MinClientVersion) &&
                    ContentLoader.CompareSemver(p.MinClientVersion, _clientVersion) > 0)
                {
                    if (_state != ContentUpdateState.PendingColdBoot && _state != ContentUpdateState.HotReady &&
                        _state != ContentUpdateState.HotApplied)
                    {
                        _state = ContentUpdateState.NeedsClientUpdate;
                        _version = p.Version;
                    }
                    return Task.CompletedTask;
                }
                _state = ContentUpdateState.Staging;
                _version = p.Version;
                _task = Task.Run(() => StageAsync(p, ct));
                return _task;
            }
        }

        /// <summary>
        /// Main thread: swaps the hot tables when one is ready and <paramref name="safePoint"/> (no modal open, not in a boss;
        /// 01 section 8.12 step 2). Returns the swapped set, else null. The sim's ContentDb is untouched.
        /// </summary>
        public HotContentSet TryApplyHot(bool safePoint)
        {
            HotContentSet set;
            lock (_lock)
            {
                if (_state != ContentUpdateState.HotReady || !safePoint) return null;
                _state = ContentUpdateState.HotApplied;
                set = _hot;
            }
            Applied = set;
            HotApplied?.Invoke(set);
            return set;
        }

        async Task StageAsync(ContentPointer p, CancellationToken ct)
        {
            IContentSource staged = null;
            try
            {
                staged = await _staging.DownloadAsync(p, ct).ConfigureAwait(false);
                if (staged == null) throw new InvalidOperationException("staging returned no content");
                var dtos = ContentLoader.LoadDtos(staged, _clientVersion);   // manifest, schema_version, E903, per-table sha256
                if (dtos.Manifest.ContentVersion != p.Version)
                    throw new ContentBuildException(new[]
                    {
                        new ContentError("E900", "manifest.json", $"manifest content_version {dtos.Manifest.ContentVersion} is not the offered {p.Version}"),
                    });
                ContentDb.Build(dtos, _running.BuildTier);   // 09's rules; the result is discarded: the sim keeps its ContentDb
                bool hot = IsHotApplicable(_runningManifest, dtos.Manifest);
                _staging.MarkPending(p.Version);
                lock (_lock)
                {
                    _hot = hot ? new HotContentSet(p.Version, staged, dtos) : null;
                    _state = hot ? ContentUpdateState.HotReady : ContentUpdateState.PendingColdBoot;
                }
            }
            catch (ContentBuildException e)
            {
                Fail(p.Version, e.Errors);
            }
            catch (Exception e)
            {
                Fail(p.Version, new[] { new ContentError("E900", p.Url ?? p.Version, e.GetType().Name + ": " + e.Message) });
            }
        }

        void Fail(string version, IReadOnlyList<ContentError> errors)
        {
            try { _staging.Discard(version); }
            catch (Exception) { /* a failed delete leaves an unmarked staging dir; cold boot never reads it */ }
            lock (_lock)
            {
                _failed.Add(version);
                _errors = errors;
                _state = ContentUpdateState.Failed;
                _hot = null;
            }
            if (_log != null) foreach (var e in errors) _log(e);
        }

        /// <summary>
        /// 00 contract 3 against what is actually running: the manifest's <c>hot_apply</c> bool must be set (the exporter's
        /// decision against its own <c>--previous</c>) AND every table whose bytes differ from the running set's, or that was
        /// added or removed, must be on the allowlist; the schema must not change. Localization files are on it whole.
        /// </summary>
        public static bool IsHotApplicable(ManifestDto running, ManifestDto next)
        {
            if (running == null || next == null || !next.HotApply || running.SchemaVersion != next.SchemaVersion) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (running.Tables != null) names.UnionWith(running.Tables.Keys);
            if (next.Tables != null) names.UnionWith(next.Tables.Keys);
            foreach (var t in names)
            {
                ManifestEntryDto a = null, b = null;
                running.Tables?.TryGetValue(t, out a);
                next.Tables?.TryGetValue(t, out b);
                bool same = a != null && b != null && string.Equals(a.Sha256, b.Sha256, StringComparison.Ordinal);
                if (!same && !HotTables.Contains(t)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// In-memory <see cref="IContentStaging"/> (tests, bots): <see cref="Cdn"/> is what a download returns, by version;
    /// <see cref="Cached"/> is what the next cold boot would choose from (<see cref="ContentSelection.LoadPreferred"/>).
    /// </summary>
    public sealed class MemoryContentStaging : IContentStaging
    {
        readonly object _lock = new object();
        readonly Dictionary<string, IContentSource> _staged = new Dictionary<string, IContentSource>(StringComparer.Ordinal);
        readonly Dictionary<string, IContentSource> _cached = new Dictionary<string, IContentSource>(StringComparer.Ordinal);

        public Dictionary<string, IContentSource> Cdn { get; } = new Dictionary<string, IContentSource>(StringComparer.Ordinal);

        public int Downloads { get; private set; }
        public List<string> Discarded { get; } = new List<string>();

        public IReadOnlyList<IContentSource> Cached
        {
            get { lock (_lock) return new List<IContentSource>(_cached.Values); }
        }

        public Task<IContentSource> DownloadAsync(ContentPointer bundle, CancellationToken ct)
        {
            lock (_lock)
            {
                Downloads++;
                if (!Cdn.TryGetValue(bundle.Version, out var src))
                    return Task.FromException<IContentSource>(new System.IO.IOException("fake CDN: no bundle " + bundle.Version));
                _staged[bundle.Version] = src;
                return Task.FromResult(src);
            }
        }

        public void MarkPending(string version)
        {
            lock (_lock)
            {
                if (_staged.TryGetValue(version, out var src)) _cached[version] = src;
                _staged.Remove(version);
            }
        }

        public void Discard(string version)
        {
            lock (_lock)
            {
                _staged.Remove(version);
                Discarded.Add(version);
            }
        }
    }
}
