using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using Newtonsoft.Json;

namespace HeroCat.Runtime.Save
{
    /// <summary>One <c>PUT /save</c> (02 section 9.2; 08 section 3.1 owns the wire).</summary>
    public sealed record SaveUpload(byte[] Bytes, SaveMeta Meta, string IfMatch, SaveTrigger Trigger);

    /// <summary>A <c>PUT /save</c> answer. <see cref="Sent"/> false: nothing reached the server (uploads suspended).</summary>
    public sealed record SaveUploadResult(bool Sent, string Etag, long SaveSeq, long ServerTime, IReadOnlyList<string> Flags)
    {
        public static readonly SaveUploadResult Suspended = new SaveUploadResult(false, null, 0, 0, Array.Empty<string>());
    }

    /// <summary>
    /// The three cloud-save calls the conflict rule needs (02 sections 9.2-9.3). The CloudSaveClient (issue #152)
    /// implements it over the ApiClient; tests use a fake. Failures throw <see cref="ServiceException"/>; a <c>PUT</c> that
    /// 409s throws with code <c>SAVE_CONFLICT</c> or <c>SAVE_STALE</c>.
    /// </summary>
    public interface ICloudSaveApi
    {
        /// <summary><c>GET /save/meta</c>: the server version's metadata with its <see cref="SaveMeta.Etag"/>; null when it has none.</summary>
        Task<SaveMeta> GetMetaAsync(CancellationToken ct);

        /// <summary><c>GET /save</c>: the raw file (header + body) of the server version.</summary>
        Task<byte[]> GetBlobAsync(CancellationToken ct);

        /// <summary><c>PUT /save</c>.</summary>
        Task<SaveUploadResult> PutAsync(SaveUpload upload, CancellationToken ct);
    }

    /// <summary>Whether uploads may run. 02 section 9.4: suspended while a conflict is unresolved.</summary>
    public interface ICloudUploadGate
    {
        bool UploadsSuspended { get; }
    }

    /// <summary>
    /// The upload path every save trigger takes (02 section 9.4): while the gate is shut, <see cref="PutAsync"/> sends
    /// nothing and answers <see cref="SaveUploadResult.Suspended"/>, whatever the trigger, so a periodic or pause save cannot
    /// overwrite the conflict's loser. The resolver's own upload goes to the inner API. Reads pass through.
    /// </summary>
    public sealed class GatedCloudSaveApi : ICloudSaveApi
    {
        readonly ICloudSaveApi _inner;
        readonly ICloudUploadGate _gate;

        public GatedCloudSaveApi(ICloudSaveApi inner, ICloudUploadGate gate)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        }

        public Task<SaveMeta> GetMetaAsync(CancellationToken ct) => _inner.GetMetaAsync(ct);
        public Task<byte[]> GetBlobAsync(CancellationToken ct) => _inner.GetBlobAsync(ct);

        public Task<SaveUploadResult> PutAsync(SaveUpload upload, CancellationToken ct) =>
            _gate.UploadsSuspended ? Task.FromResult(SaveUploadResult.Suspended) : _inner.PutAsync(upload, ct);
    }

    /// <summary>An unresolved conflict (02 section 9.4): both candidates, the rule's decision, and when it was found.</summary>
    public sealed class PendingConflict
    {
        public PendingConflict(GameState local, SaveMeta localMeta, GameState remote, SaveMeta remoteMeta, ConflictDecision decision,
                               long detectedAtServerTime)
        {
            Local = local; LocalMeta = localMeta; Remote = remote; RemoteMeta = remoteMeta; Decision = decision;
            DetectedAtServerTime = detectedAtServerTime;
        }

        /// <summary>L: the state the game keeps running on until the player picks.</summary>
        public GameState Local { get; }
        public SaveMeta LocalMeta { get; }
        public GameState Remote { get; }
        public SaveMeta RemoteMeta { get; }
        public ConflictDecision Decision { get; }

        /// <summary>Server seconds when the conflict was found: the modal's "since &lt;time&gt;".</summary>
        public long DetectedAtServerTime { get; }

        public GameState StateOf(SaveSide side) => side == SaveSide.Local ? Local : Remote;
    }

    /// <summary>What a mid-session check (resume, or a 409 from the upload queue) found.</summary>
    public enum ConflictCheckKind
    {
        /// <summary>Nothing to do, or L wins silently (the next upload overwrites R). Also: the server was unreachable.</summary>
        KeepLocal,

        /// <summary>R wins silently: it was written locally and the running state must be replaced by <see cref="ConflictCheck.State"/>.</summary>
        ReplaceWithRemote,

        /// <summary>A conflict is pending (<see cref="CloudConflictResolver.Pending"/>): uploads are suspended until the modal is answered.</summary>
        Conflict,
    }

    public sealed class ConflictCheck
    {
        public ConflictCheck(ConflictCheckKind kind, GameState state = null, ConflictDecision decision = null)
        {
            Kind = kind; State = state; Decision = decision;
        }

        public ConflictCheckKind Kind { get; }

        /// <summary>ReplaceWithRemote: the state to run.</summary>
        public GameState State { get; }

        /// <summary>The rule's decision, when the server had a different version.</summary>
        public ConflictDecision Decision { get; }
    }

    public enum ConflictOutcomeKind
    {
        /// <summary>The chosen save is local and on the server (or queued, <see cref="ConflictOutcome.Uploaded"/> false).</summary>
        Resolved,

        /// <summary>The upload 409'd and the fresh server version conflicts again: a new <see cref="CloudConflictResolver.Pending"/>.</summary>
        ConflictAgain,
    }

    /// <summary>The result of <see cref="CloudConflictResolver.ResolveConflictAsync(SaveSide, CancellationToken)"/>.</summary>
    public sealed class ConflictOutcome
    {
        public ConflictOutcome(ConflictOutcomeKind kind, SaveSide chosen, GameState state, bool replacesRunningState, bool uploaded,
                               string etag)
        {
            Kind = kind; Chosen = chosen; State = state; ReplacesRunningState = replacesRunningState; Uploaded = uploaded; Etag = etag;
        }

        public ConflictOutcomeKind Kind { get; }
        public SaveSide Chosen { get; }

        /// <summary>The state the game runs on from now (the chosen one, or a newer server version a second 409 brought).</summary>
        public GameState State { get; }

        /// <summary>
        /// The running Simulation must be replaced by <see cref="State"/> (01: the runner is rebuilt; open question 3's
        /// default, replace immediately). False when the player kept this device's save.
        /// </summary>
        public bool ReplacesRunningState { get; }

        /// <summary>False when the server was unreachable: the chosen copy is local and the upload queue sends it later.</summary>
        public bool Uploaded { get; }

        /// <summary>The server's etag after the upload (the next <c>If-Match</c>).</summary>
        public string Etag { get; }
    }

    /// <summary>
    /// 02 sections 9.3-9.4 around <see cref="ConflictRule"/>: applied at boot (<see cref="ResolveAtBootAsync"/>), at
    /// resume and on a 409 from <c>PUT /save</c> (<see cref="CheckAsync"/>); holds the unresolved conflict, during which
    /// <see cref="UploadsSuspended"/> is true (route every upload through <see cref="GatedCloudSaveApi"/>); and
    /// <see cref="ResolveConflictAsync(SaveSide, CancellationToken)"/> writes the chosen save, archives the other as
    /// <c>slot0.conflict-&lt;now&gt;.sav</c> (kept CONFLICT_KEEP_DAYS) and uploads the chosen one with <c>If-Match: R.etag</c>,
    /// starting again from the top when that 409s. Analytics: <c>cloud_autoresolved{winnerSide}</c> and
    /// <c>cloud_conflict{winnerSide, chosenSide, scoreL, scoreR, secondsApart}</c>.
    ///
    /// Seams for the CloudSaveClient (issue #152), which owns the upload queue, the boot timeout, the metadata and the
    /// etag bookkeeping of ordinary uploads: <see cref="ICloudSaveApi"/>, <see cref="MetaFactory"/> (progressScore, summary,
    /// clock authority), <see cref="ObserveServerEtag"/> after each accepted upload, and <see cref="CheckAsync"/> on a 409.
    /// Single-threaded, like the session that owns it.
    /// </summary>
    public sealed class CloudConflictResolver : ICloudUploadGate
    {
        readonly LocalSaveStore _local;
        readonly ICloudSaveApi _api;
        readonly IClock _clock;
        readonly IAnalytics _analytics;

        public CloudConflictResolver(LocalSaveStore local, ICloudSaveApi api, IClock clock, IAnalytics analytics = null)
        {
            _local = local ?? throw new ArgumentNullException(nameof(local));
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _analytics = analytics ?? NullAnalytics.Instance;
        }

        /// <summary>
        /// The 9.1 metadata of a state and its encoded file. Default: the file's own fields (<see cref="SaveMeta.Of"/>).
        /// TODO(#152): the CloudSaveClient supplies progressScore, summary and clockAuthoritative.
        /// </summary>
        public Func<GameState, byte[], SaveMeta> MetaFactory { get; set; }

        /// <summary>How often one resolution re-uploads after a 409 whose fresh server version L still beats silently.</summary>
        public int MaxUploadRounds { get; set; } = 3;

        /// <summary>The unresolved conflict, else null.</summary>
        public PendingConflict Pending { get; private set; }

        /// <summary>02 section 9.4: no upload while a conflict is unresolved.</summary>
        public bool UploadsSuspended => Pending != null;

        /// <summary>The last server etag this device observed (the next upload's <c>If-Match</c>); null before any.</summary>
        public string ServerEtag { get; private set; }

        /// <summary>Raised when a conflict becomes pending (the client shows the modal at the next SCR_MAIN idle).</summary>
        public event Action<PendingConflict> ConflictRaised;

        /// <summary>The upload queue reports each accepted <c>PUT</c>'s etag.</summary>
        public void ObserveServerEtag(string etag) { if (!string.IsNullOrEmpty(etag)) ServerEtag = etag; }

        ContentDb Db => _local.Content ?? throw new InvalidOperationException("the local store must load before conflict resolution");

        // ------------------------------------------------------------------------------------------------ boot

        /// <summary>
        /// Boot (01 section 8.1 step 4): the local load against the server version. Returns the LoadResult to run:
        /// unchanged when the server has nothing, is unreachable (the check is deferred) or L wins silently; Loaded on R
        /// (written locally) when R wins silently or is the only copy; Conflict with State = L and Other = R otherwise.
        /// TooNew passes through untouched.
        /// </summary>
        public async Task<LoadResult> ResolveAtBootAsync(LoadResult local, CancellationToken ct)
        {
            if (local == null) throw new ArgumentNullException(nameof(local));
            if (local.Status == LoadStatus.TooNew || local.Status == LoadStatus.Conflict) return local;
            SaveMeta rMeta;
            byte[] rBytes = null;
            try
            {
                rMeta = await _api.GetMetaAsync(ct).ConfigureAwait(false);
                if (rMeta != null) rBytes = await _api.GetBlobAsync(ct).ConfigureAwait(false);
            }
            catch (ServiceException) { return local; }   // offline or timeout: run on local, the check is deferred (01 section 7)
            if (rMeta == null) return local;

            long now = _clock.NowServerSeconds;
            var remote = TryDecodeRemote(rBytes, rMeta, now, out var rRepairs, out string problem);
            if (remote == null)
            {
                local.Problems.Add("cloud copy: " + problem);   // TODO(#320) the rule has no row for it
                return local;
            }
            ServerEtag = rMeta.Etag;

            bool hasLocal = local.Status != LoadStatus.NewProfile;
            var lMeta = hasLocal ? MetaOf(local.State) : null;
            var d = ConflictRule.Resolve(lMeta, rMeta, ConflictRule.WeaponPullCost(Db));
            if (d.AutoResolved) _analytics.Track(new AnalyticsEvents.CloudAutoresolved(ConflictRule.SideName(d.Winner)));

            if (d.UsesRemote)
            {
                AdoptRemote(remote, rBytes, hasLocal ? local.State : null);
                var r = new LoadResult(LoadStatus.Loaded, remote, null, rRepairs, lMeta, rMeta);
                r.Problems.AddRange(local.Problems);
                return r;   // ReplacedUnreadableSave stays false: the cloud copy restored the player (02 section 8.3 step 3)
            }
            if (d.IsConflict)
            {
                Raise(new PendingConflict(local.State, lMeta, remote, rMeta, d, now));
                var c = new LoadResult(LoadStatus.Conflict, local.State, remote, local.Repairs, lMeta, rMeta)
                {
                    FromSchemaVersion = local.FromSchemaVersion,
                };
                c.Problems.AddRange(local.Problems);
                return c;
            }
            var kept = new LoadResult(local.Status, local.State, null, local.Repairs, local.LocalMeta, rMeta)
            {
                FromSchemaVersion = local.FromSchemaVersion, ReplacedUnreadableSave = local.ReplacedUnreadableSave,
            };
            kept.Problems.AddRange(local.Problems);
            return kept;
        }

        // ------------------------------------------------------------------------------------------------ resume and 409

        /// <summary>
        /// Resume (01 section 8.11 step 4; meta first, the blob only when R wins or conflicts) and a 409 from the upload
        /// queue (<paramref name="force"/> true: the server says it changed even if its etag is the one last seen).
        /// <paramref name="running"/> is the live state (L). Never throws for a network failure (KeepLocal).
        /// </summary>
        public async Task<ConflictCheck> CheckAsync(GameState running, bool force, CancellationToken ct)
        {
            if (running == null) throw new ArgumentNullException(nameof(running));
            if (Pending != null) return new ConflictCheck(ConflictCheckKind.Conflict, null, Pending.Decision);
            SaveMeta rMeta;
            try { rMeta = await _api.GetMetaAsync(ct).ConfigureAwait(false); }
            catch (ServiceException) { return new ConflictCheck(ConflictCheckKind.KeepLocal); }
            if (rMeta == null) return new ConflictCheck(ConflictCheckKind.KeepLocal);
            if (!force && !string.IsNullOrEmpty(rMeta.Etag) && rMeta.Etag == ServerEtag) return new ConflictCheck(ConflictCheckKind.KeepLocal);

            var lMeta = MetaOf(running);
            var d = ConflictRule.Resolve(lMeta, rMeta, ConflictRule.WeaponPullCost(Db));
            if (!d.IsConflict && d.Winner == SaveSide.Local)
            {
                if (d.AutoResolved) _analytics.Track(new AnalyticsEvents.CloudAutoresolved(ConflictRule.SideName(d.Winner)));
                ServerEtag = rMeta.Etag;   // L overwrites R on the next upload
                return new ConflictCheck(ConflictCheckKind.KeepLocal, null, d);
            }

            byte[] rBytes;
            try { rBytes = await _api.GetBlobAsync(ct).ConfigureAwait(false); }
            catch (ServiceException) { return new ConflictCheck(ConflictCheckKind.KeepLocal); }   // retried at the next check
            long now = _clock.NowServerSeconds;
            var remote = TryDecodeRemote(rBytes, rMeta, now, out _, out _);
            if (remote == null) return new ConflictCheck(ConflictCheckKind.KeepLocal);   // TODO(#320)
            if (d.AutoResolved) _analytics.Track(new AnalyticsEvents.CloudAutoresolved(ConflictRule.SideName(d.Winner)));
            ServerEtag = rMeta.Etag;
            if (d.UsesRemote)
            {
                AdoptRemote(remote, rBytes, running);
                return new ConflictCheck(ConflictCheckKind.ReplaceWithRemote, remote, d);
            }
            Raise(new PendingConflict(running, lMeta, remote, rMeta, d, now));
            return new ConflictCheck(ConflictCheckKind.Conflict, null, d);
        }

        // ------------------------------------------------------------------------------------------------ the player's choice

        /// <summary>
        /// 8.1's shape: <paramref name="chosen"/> must be one of <see cref="Pending"/>'s two states (by reference); the
        /// other is archived.
        /// </summary>
        public Task<ConflictOutcome> ResolveConflictAsync(GameState chosen, GameState rejected, CancellationToken ct)
        {
            var p = Pending ?? throw new InvalidOperationException("no conflict is pending");
            if (ReferenceEquals(chosen, p.Remote) && ReferenceEquals(rejected, p.Local)) return ResolveConflictAsync(SaveSide.Remote, ct);
            if (ReferenceEquals(chosen, p.Local) && ReferenceEquals(rejected, p.Remote)) return ResolveConflictAsync(SaveSide.Local, ct);
            throw new ArgumentException("chosen and rejected must be the pending conflict's two states");
        }

        /// <summary>
        /// 02 section 9.4: write the chosen save locally, archive the other as <c>slot0.conflict-&lt;now&gt;.sav</c>, upload the
        /// chosen one with <c>If-Match: R.etag</c>; a further 409 resolves again from the top (it may raise a new conflict).
        /// Choosing R first raises its SaveSeq to at least L's, so this device's stamped copy is above both lineages and the
        /// server's same-device <c>saveSeq</c> check (08 section 3.1 step 4) accepts it. Emits <c>cloud_conflict</c>.
        /// </summary>
        public async Task<ConflictOutcome> ResolveConflictAsync(SaveSide chosenSide, CancellationToken ct)
        {
            var p = Pending ?? throw new InvalidOperationException("no conflict is pending");
            var chosen = p.StateOf(chosenSide);
            var rejected = p.StateOf(chosenSide == SaveSide.Local ? SaveSide.Remote : SaveSide.Local);
            if (chosenSide == SaveSide.Remote) chosen.SaveSeq = Math.Max(chosen.SaveSeq, p.Local.SaveSeq);
            await _local.ResolveConflictAsync(chosen, rejected, ct).ConfigureAwait(false);   // archive + stamped write
            Pending = null;   // the local choice is made: uploads may go again
            _analytics.Track(new AnalyticsEvents.CloudConflict(
                ConflictRule.SideName(p.Decision.Winner), ConflictRule.SideName(chosenSide), p.LocalMeta?.ProgressScore ?? 0,
                p.RemoteMeta?.ProgressScore ?? 0,
                Math.Abs((p.LocalMeta?.LastSavedServerTime ?? 0) - (p.RemoteMeta?.LastSavedServerTime ?? 0))));
            ServerEtag = p.RemoteMeta?.Etag;
            bool replaces = chosenSide == SaveSide.Remote;

            for (int round = 0; ; round++)
            {
                var bytes = _local.Slots.Read(SaveSlot.Primary);
                try
                {
                    var put = await _api.PutAsync(new SaveUpload(bytes, MetaOf(chosen, bytes), ServerEtag ?? "*", SaveTrigger.Conflict), ct)
                                        .ConfigureAwait(false);
                    ObserveServerEtag(put?.Etag);
                    return new ConflictOutcome(ConflictOutcomeKind.Resolved, chosenSide, chosen, replaces, put != null && put.Sent, ServerEtag);
                }
                catch (ServiceException e) when (IsSaveConflict(e))
                {
                    // A third device wrote meanwhile: resolve again from the top, with the chosen save as L.
                    if (round + 1 >= MaxUploadRounds)
                        return new ConflictOutcome(ConflictOutcomeKind.Resolved, chosenSide, chosen, replaces, false, ServerEtag);
                    var again = await CheckAsync(chosen, force: true, ct).ConfigureAwait(false);
                    switch (again.Kind)
                    {
                        case ConflictCheckKind.Conflict:
                            return new ConflictOutcome(ConflictOutcomeKind.ConflictAgain, chosenSide, chosen, replaces, false, ServerEtag);
                        case ConflictCheckKind.ReplaceWithRemote:
                            return new ConflictOutcome(ConflictOutcomeKind.Resolved, SaveSide.Remote, again.State, true, false, ServerEtag);
                        default:
                            continue;   // the chosen save still wins silently: upload it over the new server version
                    }
                }
                catch (ServiceException)
                {
                    // Offline or a server error: the choice stands locally; the upload queue sends it with If-Match R.etag.
                    return new ConflictOutcome(ConflictOutcomeKind.Resolved, chosenSide, chosen, replaces, false, ServerEtag);
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ helpers

        public static bool IsSaveConflict(ServiceException e) =>
            e != null && (e.Code == "SAVE_CONFLICT" || e.Code == "SAVE_STALE");

        void Raise(PendingConflict p)
        {
            Pending = p;
            ConflictRaised?.Invoke(p);
        }

        SaveMeta MetaOf(GameState s) => MetaOf(s, SaveSerializer.Encode(s));

        SaveMeta MetaOf(GameState s, byte[] bytes)
        {
            if (MetaFactory != null) return MetaFactory(s, bytes);
            return SaveMeta.Of(s, SaveSerializer.Decode(bytes), bytes.Length);
        }

        /// <summary>Decode, migrate, reject and repair the server copy (Remote mode, the other device's id kept). Null when unusable.</summary>
        GameState TryDecodeRemote(byte[] bytes, SaveMeta meta, long now, out IReadOnlyList<string> repairs, out string problem)
        {
            repairs = Array.Empty<string>();
            problem = null;
            try
            {
                if (bytes == null) throw new SaveCorruptException("no blob");
                var state = SaveSystem.DecodeFull(bytes, Db, _analytics).state;
                repairs = SaveValidator.Run(state, now, Db, new SaveValidatorOptions { RemoteMode = true });
                return state;
            }
            catch (Exception e) when (e is SaveCorruptException || e is SaveInvalidException || e is SaveTooNewException || e is JsonException)
            {
                problem = e.Message;
                return null;
            }
        }

        /// <summary>R is used: its file becomes slot0.sav (rotating L to prev), and its SaveSeq is raised to L's so this
        /// device's next stamped save is above both lineages.</summary>
        void AdoptRemote(GameState remote, byte[] bytes, GameState local)
        {
            _local.Slots.Write(bytes);
            if (local != null) remote.SaveSeq = Math.Max(remote.SaveSeq, local.SaveSeq);
            (_clock as IClockFloor)?.SetFloor(remote.Time.LastSavedServerTime);
        }
    }
}
