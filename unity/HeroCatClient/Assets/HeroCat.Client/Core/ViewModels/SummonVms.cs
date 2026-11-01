using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Summons;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Summons;

namespace HeroCat.Client.Core
{
    /// <summary>The Runtime summon flow (01 section 8.5): SessionRunner.SummonAsync in the client.</summary>
    public delegate Task<Result<SummonResultPayload, SummonError>> SummonFlow(Banner banner, int count, SummonPayment payment);

    /// <summary>One row of the inline rates panel (07 section 4.10): the published rate of a rarity at this banner level.</summary>
    public readonly struct RateLine
    {
        public RateLine(Rarity rarity, double rate)
        {
            Rarity = rarity;
            Rate = rate;
        }

        public Rarity Rarity { get; }
        public double Rate { get; }
    }

    /// <summary>
    /// SCR_SUMMON (07 section 4.10; issue #117): banner tabs, banner level bar, pity bar with the soft-pity marker, the inline
    /// rates panel, x1 / x10 buttons with cost and shortfall ("Need 760 more"), the ticket button (hidden at 0). Summons go
    /// through Runtime (<see cref="SummonFlow"/>), never a sim command from here; the result is applied by Runtime's
    /// ApplySummonResult and then SCR_SUMMON_RESULT opens. At most one request is in flight.
    /// </summary>
    public sealed class SummonVm
    {
        public const string AnchorPull1 = "summon.x1", AnchorPull10 = "summon.x10", AnchorTicket = "summon.ticket";

        readonly VmContext _c;
        readonly SummonFlow _flow;
        readonly SummonResultVm _result;
        readonly List<Banner> _banners = new List<Banner>();
        readonly List<RateLine> _rates = new List<RateLine>();
        Task<Result<SummonResultPayload, SummonError>> _pending;
        string _pendingAnchor;
        SummonBannerVm _view;

        public SummonVm(VmContext c, SummonFlow flow, SummonResultVm result)
        {
            _c = c;
            _flow = flow;
            _result = result;
            var list = new List<BannerDef>();
            foreach (var kv in c.Db.Banners)
                if (kv.Value.Enabled && kv.Value.Levels.Count > 0) list.Add(kv.Value);
            list.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : a.Id.CompareTo(b.Id));
            foreach (var d in list) _banners.Add(d.Id);
            TabNames = new TextKeyLabel[_banners.Count];
            for (int i = 0; i < TabNames.Length; i++)
            {
                TabNames[i] = c.Label();
                TabNames[i].Set(c.Db.Banners[_banners[i]].NameKey);
            }
            Pull1 = c.Button();
            Pull10 = c.Button();
            Ticket = c.Button();
            PityText = c.Label();
            GuaranteeText = c.Label();
            if (_banners.Count > 0) SelectedBanner.Set(_banners[0]);
        }

        public IReadOnlyList<Banner> Banners => _banners;
        public readonly TextKeyLabel[] TabNames;
        public readonly Bindable<Banner> SelectedBanner = new Bindable<Banner>();

        public readonly IntLabel BannerLevel = new IntLabel();
        public readonly IntLabel BannerExp = new IntLabel();
        public readonly IntLabel BannerExpToNext = new IntLabel();
        public readonly BarFill BannerFill = new BarFill();
        public readonly Flag BannerAtMax = new Flag();

        public readonly IntLabel Pity = new IntLabel();
        public readonly IntLabel PityHard = new IntLabel();
        public readonly BarFill PityFill = new BarFill();

        /// <summary>Marker position of PITY_SOFT_START on the pity bar.</summary>
        public readonly BarFill PitySoftMarker = new BarFill();

        public readonly TextKeyLabel PityText, GuaranteeText;
        public readonly ButtonState Pull1, Pull10, Ticket;
        public readonly IntLabel TicketCount = new IntLabel();

        public readonly Flag RatesOpen = new Flag();
        public IReadOnlyList<RateLine> Rates => _rates;
        public int RatesVersion { get; private set; }

        public bool InFlight => _pending != null;
        public readonly Flag InFlightFlag = new Flag();

        public void SelectBanner(Banner b)
        {
            if (_banners.Contains(b)) SelectedBanner.Set(b);
        }

        public void ToggleRates() => RatesOpen.Set(!RatesOpen.Value);

        public void Refresh(Simulation sim)
        {
            if (_banners.Count == 0) return;
            var b = SelectedBanner.Value;
            var db = _c.Db;
            var def = db.Banners[b];
            _view = SummonQuery.BannerView(db, sim.State, b);
            var v = _view;
            BannerLevel.Set(v.BannerLevel);
            bool atMax = v.BannerLevel >= v.MaxLevel;
            BannerAtMax.Set(atMax);
            long prevReq = def.Levels[Math.Max(0, v.BannerLevel - 1)].ExpRequired;
            long nextReq = atMax ? prevReq : def.Levels[v.BannerLevel].ExpRequired;
            BannerExp.Set(v.Exp);
            BannerExpToNext.Set(nextReq);
            BannerFill.Set(atMax ? 1 : (double)(v.Exp - prevReq), atMax ? 1 : (double)(nextReq - prevReq));

            Pity.Set(v.Pity);
            PityHard.Set(v.PityHard);
            PityFill.Set(v.Pity, v.PityHard);
            PitySoftMarker.Set(def.PitySoftStart, v.PityHard);
            PityText.Set(UiKeys.SummonSoftPity, def.PitySoftStart + 1);
            GuaranteeText.Set(UiKeys.SummonGuarantee, def.GuaranteeBatchSize, UiKeys.RarityName(def.GuaranteeMinRarity));

            int batch = def.GuaranteeBatchSize;
            long cost1 = v.PullCost, cost10 = (long)v.PullCost * batch;
            SetPull(Pull1, 1, cost1, v.Diamonds, v.CanAffordDiamonds1);
            SetPull(Pull10, batch, cost10, v.Diamonds, v.CanAffordDiamonds10);
            TicketCount.Set(v.Tickets);
            Ticket.Visible.Set(v.Tickets > 0);
            Ticket.Enabled.Set(v.Tickets > 0 && !InFlight);
            Ticket.Label.Set(UiKeys.SummonTicket, v.Tickets);
            Ticket.Reason.Set(null);
            InFlightFlag.Set(InFlight);

            if (RatesOpen.Value && _rates.Count == 0 || RatesStale(v))
            {
                _rates.Clear();
                if (v.Rates != null)
                    foreach (Rarity r in RarityOrder)
                        if (v.Rates.Rates.TryGetValue(r, out var rate)) _rates.Add(new RateLine(r, rate));
                _ratesFor = (b, v.BannerLevel);
                RatesVersion++;
            }
        }

        (Banner, int) _ratesFor = ((Banner)(-1), -1);

        bool RatesStale(SummonBannerVm v) => _ratesFor != (v.Banner, v.BannerLevel);

        static readonly Rarity[] RarityOrder = { Rarity.MYTHIC, Rarity.LEGENDARY, Rarity.EPIC, Rarity.RARE, Rarity.COMMON };

        void SetPull(ButtonState btn, int count, long cost, long diamonds, bool afford)
        {
            btn.Visible.Set(true);
            btn.Label.Set(UiKeys.SummonPull, count, LabelArg.Int(cost));
            btn.Enabled.Set(afford && !InFlight);
            if (afford) btn.Reason.Set(null);
            else btn.Reason.Set(UiKeys.NeedMore, LabelArg.Int(cost - diamonds));
        }

        public void OnPull1() => Start(1, SummonPayment.Diamonds, AnchorPull1);

        public void OnPull10()
        {
            if (_c.Db.Banners.TryGetValue(SelectedBanner.Value, out var d)) Start(d.GuaranteeBatchSize, SummonPayment.Diamonds, AnchorPull10);
        }

        public void OnTicket() => Start(1, SummonPayment.Tickets, AnchorTicket);

        /// <summary>SCR_SUMMON_RESULT's "Summon again": same banner, same count, diamonds, a new request id.</summary>
        public void Again(Banner banner, int count)
        {
            SelectedBanner.Set(banner);
            Start(count, SummonPayment.Diamonds, SummonResultVm.AnchorAgain);
        }

        void Start(int count, SummonPayment payment, string anchor)
        {
            if (InFlight || _flow == null) return;
            Task<Result<SummonResultPayload, SummonError>> t;
            try
            {
                t = _flow(SelectedBanner.Value, count, payment);
            }
            catch (Exception)
            {
                Fail(SummonError.Network, anchor);
                return;
            }
            _pending = t;
            _pendingAnchor = anchor;
            InFlightFlag.Set(true);
            Poll();   // the M0 local service completes synchronously
        }

        /// <summary>
        /// A summon intent saved before a kill, re-run by Runtime at boot or resume (01 section 8.2 step 3, SummonFlow's
        /// ResolveAsync): it lands like a fresh request, so the reveal opens. Ignored while a request is in flight.
        /// </summary>
        public void Resume(Task<Result<SummonResultPayload, SummonError>> resolve)
        {
            if (InFlight || resolve == null) return;
            _pending = resolve;
            _pendingAnchor = null;
            InFlightFlag.Set(true);
            Poll();
        }

        /// <summary>Per frame on the main thread: lands a finished request (Remote completes on a later frame).</summary>
        public void Poll()
        {
            var t = _pending;
            if (t == null || !t.IsCompleted) return;
            _pending = null;
            InFlightFlag.Set(false);
            string anchor = _pendingAnchor;
            if (t.IsFaulted || t.IsCanceled)
            {
                Fail(SummonError.Network, anchor);
                return;
            }
            var r = t.Result;
            if (!r.IsOk)
            {
                if (r.Error == SummonError.None) return;   // a resume with nothing left to resolve
                Fail(r.Error, anchor);
                return;
            }
            foreach (var p in r.Value.Pulls)
                if (p.IsNew) _c.Prefs.MarkNew(r.Value.Banner, p.ItemId);
            _result.Show(r.Value);
        }

        void Fail(SummonError e, string anchor)
        {
            string key = e switch
            {
                SummonError.NotEnoughDiamonds => ErrorClassifier.RejectReasonKey(CollectionRejectReasons.NotEnough(Currency.DIAMOND)),
                SummonError.NotEnoughTickets => ErrorClassifier.RejectReasonKey(CollectionRejectReasons.NotEnough(_c.Db.Banners[SelectedBanner.Value].TicketCurrency)),
                SummonError.BannerClosed => ErrorClassifier.RejectReasonKey(CollectionRejectReasons.BANNER_CLOSED),
                SummonError.RateLimited => ErrorClassifier.RejectCodeKey(RejectCode.RateLimited),   // the reject toast; 429 shows nothing until retries are exhausted
                _ => ErrorClassifier.ErrTitleKey("NET_OFFLINE"),
            };
            _c.Toasts?.Push(_c.Loc.Text(key), ToastStyle.Reject, UiConstants.ToastSec, anchor);
            if (anchor != null) _c.Cues?.Push(CueKind.Shake, id: anchor);
            _c.Cues?.Feel(Haptic.Light, Sounds.UiDeny);
            _result.OnRequestFailed();
        }

        public void OnEvent(Event e)
        {
            if (e is BannerLeveledUp bl) _result.OnBannerLeveledUp(bl);
        }
    }

    /// <summary>One card of SCR_SUMMON_RESULT (07 section 4.11).</summary>
    public sealed class CardVm
    {
        public Rarity Rarity;
        public string ItemId, NameKey, IconId;
        public bool IsNew;
        public int LevelBefore, LevelAfter, CopiesAfter, CopiesForNext;

        /// <summary>07 section 6 rule 6: NEW, "Lv 2 -> 3", or "+1 copy (4/6)".</summary>
        public string ChipKey;

        public CardState State;
    }

    /// <summary>
    /// SCR_SUMMON_RESULT (07 sections 4.11 and 6; issue #117): the overlay over the already-applied result, driven by the
    /// <see cref="RevealSequencer"/>. Skip (also back), tap to speed, Summon again only after the reveal and when
    /// affordable, Close; a banner level-up during the reveal is toasted after the summary.
    /// </summary>
    public sealed class SummonResultVm
    {
        public const string AnchorAgain = "summon.again";

        readonly VmContext _c;
        readonly List<CardVm> _cards = new List<CardVm>();
        readonly List<BannerLeveledUp> _queuedLevelUps = new List<BannerLeveledUp>();
        bool _awaiting;

        public SummonResultVm(VmContext c)
        {
            _c = c;
            Reveal.CardRevealed = OnCardRevealed;
            Again = c.Button();
            Summary = c.Label();
        }

        public readonly RevealSequencer Reveal = new RevealSequencer();
        public IReadOnlyList<CardVm> Cards => _cards;
        public int CardsVersion { get; private set; }

        public Banner Banner { get; private set; }
        public int Count { get; private set; }

        public readonly int[] Counts = new int[5];
        public readonly IntLabel PityAfter = new IntLabel();
        public readonly TextKeyLabel Summary;
        public readonly Flag SummaryVisible = new Flag();
        public readonly ButtonState Again;

        /// <summary>Set by the GameViewModel: the SummonVm that sends "again".</summary>
        public SummonVm Summon { get; set; }

        public bool Running => Reveal.Running;

        /// <summary>Opens (or re-fills in place, for "again") the overlay for an applied payload.</summary>
        public void Show(SummonResultPayload p)
        {
            _awaiting = false;
            Banner = p.Banner;
            Count = p.Count;
            _cards.Clear();
            Array.Clear(Counts, 0, Counts.Length);
            var db = _c.Db;
            var rarities = new Rarity[p.Pulls.Length];
            for (int i = 0; i < p.Pulls.Length; i++)
            {
                var pull = p.Pulls[i];
                string name = "", icon = "";
                if (p.Banner == Banner.WEAPON && db.Weapons.TryGetValue(pull.ItemId, out var w)) { name = w.NameKey; icon = w.Icon; }
                else if (p.Banner == Banner.SKILL && db.Skills.TryGetValue(pull.ItemId, out var s)) { name = s.NameKey; icon = s.Icon; }
                int lvl = pull.LevelAfter;
                int forNext = lvl >= db.DupeLevels.Count ? 0 : db.DupeLevels[Math.Max(0, lvl)];
                _cards.Add(new CardVm
                {
                    Rarity = pull.Rarity, ItemId = pull.ItemId, NameKey = name, IconId = icon, IsNew = pull.IsNew,
                    LevelBefore = pull.LevelBefore, LevelAfter = pull.LevelAfter, CopiesAfter = pull.CopiesAfter, CopiesForNext = forNext,
                    ChipKey = pull.IsNew ? UiKeys.CardNew : pull.LevelAfter > pull.LevelBefore ? UiKeys.CardLevelUp : UiKeys.CardCopy,
                });
                rarities[i] = pull.Rarity;
                Counts[(int)pull.Rarity]++;
            }
            PityAfter.Set(p.PityAfter);
            Reveal.Start(rarities, _c.Now(), _c.Prefs.ReducedMotion);
            SyncCards();
            CardsVersion++;
            SummaryVisible.Set(false);
            if (_c.Router != null && !_c.Router.IsOpen(ScreenIds.SummonResult)) _c.Router.ShowOverlay(ScreenIds.SummonResult, p.RequestId);
        }

        /// <summary>A failed "again": the cards from the previous result stay face up.</summary>
        public void OnRequestFailed() => _awaiting = false;

        public void OnBannerLeveledUp(BannerLeveledUp e)
        {
            if (Reveal.Running || _awaiting) _queuedLevelUps.Add(e);
            else ToastLevelUp(e);
        }

        void ToastLevelUp(BannerLeveledUp e) =>
            _c.Toasts?.Push(_c.Loc.Format(UiKeys.ToastBannerLevel, TextFmt.IntText(e.Level)), ToastStyle.Reward);

        public void Update(double now, Simulation sim)
        {
            if (Reveal.Running)
            {
                Reveal.Update(now);
                SyncCards();
            }
            bool done = Reveal.Complete;
            SummaryVisible.Set(done && !_awaiting);
            if (done)
            {
                Summary.Set(UiKeys.SummonSummary,
                            LabelArg.Int(Counts[(int)Rarity.MYTHIC] + Counts[(int)Rarity.LEGENDARY] + Counts[(int)Rarity.EPIC]),
                            LabelArg.Int(Counts[(int)Rarity.RARE]), LabelArg.Int(Counts[(int)Rarity.COMMON]));
                if (_queuedLevelUps.Count > 0 && !_awaiting)
                {
                    foreach (var e in _queuedLevelUps) ToastLevelUp(e);
                    _queuedLevelUps.Clear();
                }
            }
            if (sim != null && _c.Db.Banners.TryGetValue(Banner, out var def))
            {
                long cost = (long)def.PullCostDiamonds * Count;
                bool afford = sim.State.Wallet.Diamond >= cost;
                bool inFlight = Summon != null && Summon.InFlight;
                bool can = done && afford && !inFlight && !_awaiting;
                Again.Visible.Set(true);
                Again.Label.Set(UiKeys.SummonAgain, Count, LabelArg.Int(cost));
                Again.Enabled.Set(can);
                if (can || !done || _awaiting) Again.Reason.Set(null);
                else if (!afford) Again.Reason.Set(UiKeys.NeedMore, LabelArg.Int(cost - sim.State.Wallet.Diamond));
                else Again.Reason.Set(null);
            }
        }

        void SyncCards()
        {
            for (int i = 0; i < _cards.Count && i < Reveal.Count; i++) _cards[i].State = Reveal.State(i);
        }

        void OnCardRevealed(int i, Rarity r)
        {
            switch (r)
            {
                case Rarity.EPIC:
                    _c.Cues?.Push(CueKind.RarityFlash, cell: i, level: (int)r);
                    _c.Cues?.Feel(Haptic.Light, Sounds.CardEpic);
                    break;
                case Rarity.LEGENDARY:
                    _c.Cues?.Push(CueKind.RarityFlash, cell: i, level: (int)r);
                    _c.Cues?.Feel(Haptic.Medium, Sounds.CardLegend);
                    break;
                case Rarity.MYTHIC:
                    _c.Cues?.Push(CueKind.RarityFlash, cell: i, level: (int)r);
                    _c.Cues?.Feel(Haptic.Heavy, Sounds.CardMythic);
                    break;
                default:
                    _c.Cues?.Push(CueKind.CardFlip, cell: i);
                    _c.Cues?.Feel(Haptic.None, Sounds.CardFlip);
                    break;
            }
        }

        public void OnSkip()
        {
            Reveal.Skip();
            SyncCards();
        }

        /// <summary>A tap anywhere on the overlay: speed up, a second tap skips (rule 5).</summary>
        public void OnTap(double now)
        {
            if (!Reveal.Running) return;
            Reveal.Tap(now);
            SyncCards();
        }

        /// <summary>Rule 7: keeps the overlay, re-faces the cards down and runs a new request.</summary>
        public void OnAgain()
        {
            if (!Again.Enabled.Value || Summon == null) return;
            _awaiting = true;
            foreach (var c in _cards) c.State = CardState.FaceDown;
            CardsVersion++;
            Summon.Again(Banner, Count);
        }

        /// <summary>Close, or leave mid-reveal: the reveal completes silently (rule 8).</summary>
        public void OnClose()
        {
            if (Reveal.Running) Reveal.Skip();
            SyncCards();
            foreach (var e in _queuedLevelUps) ToastLevelUp(e);
            _queuedLevelUps.Clear();
            _c.Router?.CloseOverlay(ScreenIds.SummonResult);
        }

        /// <summary>Tap a face-up card: SCR_ITEM_DETAIL with its NEW ribbon.</summary>
        public void OnCard(int i, ItemDetailVm detail)
        {
            if (i < 0 || i >= _cards.Count || _cards[i].State != CardState.FaceUp) return;
            detail?.Open(Banner, _cards[i].ItemId);
        }
    }
}
