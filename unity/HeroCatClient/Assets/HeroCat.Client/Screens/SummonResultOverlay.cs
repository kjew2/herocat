using System.Collections.Generic;
using HeroCat.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// SCR_SUMMON_RESULT (07 sections 4.11 and 6; issue #117): the reveal over the already-applied result. Tap anywhere
    /// speeds up (a second tap skips), Skip, Summon again, Close; a face-up card opens SCR_ITEM_DETAIL.
    /// </summary>
    public sealed class SummonResultOverlay : ScreenView, ICueListener
    {
        readonly List<Image> _cards = new List<Image>();
        readonly List<TextMeshProUGUI> _cardText = new List<TextMeshProUGUI>();
        readonly List<CardState> _shown = new List<CardState>();
        readonly List<Image> _cardArt = new List<Image>();
        RectTransform _grid;
        int _cardsVersion = -1;

        // Splash (07 section 4.11, 00 decision 108): an L+ companion flipping on its own fills the overlay for SplashSec.
        const double SplashSec = 1.5;
        Image _splash;
        GameObject _splashRoot;
        double _splashUntil;

        protected override void Build()
        {
            var r = Vm.SummonResult;
            var bg = UiKit.Panel("Bg", Root, Palette.Background, 0.92f, raycast: true);
            UiKit.Fill(bg.rectTransform);
            bg.gameObject.AddComponent<Button>().onClick.AddListener(() => Vm.SummonResult.OnTap(Vm.Now));
            var skip = UiKit.Button("Skip", Root, r.OnSkip, Palette.PanelRaised, UiKit.Body);
            var srt = skip.GetComponent<RectTransform>();
            srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(1, 1);
            srt.anchoredPosition = new Vector2(-24, -24);
            srt.sizeDelta = new Vector2(220, 112);
            UiKit.LabelOf(skip).text = Vm.Localizer.Text(UiKeys.SummonSkip);   // visible from the first frame (rule 4)

            _grid = UiKit.Rect("Cards", Root);
            UiKit.Place(_grid, 40, 200, 1000, 800);

            var summary = UiKit.Text("Summary", Root, UiKit.Title, Palette.Text, TextAlignmentOptions.Center);
            UiKit.Place(summary.rectTransform, 40, 1040, 1000, 72);
            r.Summary.Bind(summary.Sink());
            r.SummaryVisible.Bind(new ActiveSink(summary.gameObject));
            var pity = UiKit.Text("Pity", Root, UiKit.Caption, Palette.Muted, TextAlignmentOptions.Center);
            UiKit.Place(pity.rectTransform, 40, 1112, 1000, 48);
            r.PityAfter.Bind(new ActionTextSink(t => pity.text = Vm.Localizer.Format(UiKeys.SummonPity, t, "")));

            var again = UiKit.Button("Again", Root, r.OnAgain, 0x6A1B9A, UiKit.Body);
            UiKit.Place(again.GetComponent<RectTransform>(), 80, 1200, 560, 136);
            var reason = UiKit.Text("Reason", Root, UiKit.Micro, Palette.Muted, TextAlignmentOptions.Center);
            UiKit.Place(reason.rectTransform, 80, 1340, 560, 40);
            ButtonBinding.Bind(r.Again, again, reason);
            Anchor(SummonResultVm.AnchorAgain, again.GetComponent<RectTransform>());
            var close = UiKit.Button("Close", Root, r.OnClose, Palette.PanelRaised, UiKit.Body);
            UiKit.Place(close.GetComponent<RectTransform>(), 680, 1200, 320, 136);
            UiKit.LabelOf(close).text = Vm.Localizer.Text(UiKeys.Close);

            var splashBg = UiKit.Panel("Splash", Root, Palette.Background, 0.96f, raycast: true);
            UiKit.Fill(splashBg.rectTransform);
            splashBg.gameObject.AddComponent<Button>().onClick.AddListener(HideSplash);
            var splashRt = UiKit.Rect("Art", splashBg.transform);
            UiKit.Fill(splashRt);
            _splash = splashRt.gameObject.AddComponent<Image>();
            _splash.preserveAspect = true;
            _splash.raycastTarget = false;
            _splashRoot = splashBg.gameObject;
            _splashRoot.SetActive(false);
        }

        void HideSplash()
        {
            if (_splashRoot != null) _splashRoot.SetActive(false);
        }

        void ShowSplash(Sprite s, double now)
        {
            _splash.sprite = s;
            _splashRoot.SetActive(true);
            _splashRoot.transform.SetAsLastSibling();
            _splashUntil = now + SplashSec;
        }

        public override void Tick(double now)
        {
            var r = Vm.SummonResult;
            if (r.CardsVersion != _cardsVersion)
            {
                _cardsVersion = r.CardsVersion;
                Rebuild();
            }
            int flipped = 0;
            Sprite splash = null;
            for (int i = 0; i < _cards.Count && i < r.Cards.Count; i++)
            {
                var c = r.Cards[i];
                if (_shown[i] == c.State) continue;
                _shown[i] = c.State;
                Draw(i, c);
                if (c.State == CardState.FaceUp)
                {
                    flipped++;
                    splash = SpriteLibrary.Get(ArtAddresses.SplashFor(r.Banner, c.ItemId, c.Rarity)) ?? splash;
                }
            }
            if (flipped == 1 && splash != null) ShowSplash(splash, now);   // several at once = Skip: no splash
            if (_splashRoot != null && _splashRoot.activeSelf && now >= _splashUntil) HideSplash();
        }

        void Rebuild()
        {
            foreach (var c in _cards) Destroy(c.gameObject);
            _cards.Clear();
            _cardArt.Clear();
            HideSplash();
            _cardText.Clear();
            _shown.Clear();
            var cards = Vm.SummonResult.Cards;
            bool single = cards.Count == 1;
            for (int i = 0; i < cards.Count; i++)
            {
                int idx = i;
                var b = UiKit.Button("Card" + i, _grid, () => Vm.SummonResult.OnCard(idx, Vm.ItemDetail), Palette.Panel, UiKit.Caption);
                var rt = b.GetComponent<RectTransform>();
                if (single) UiKit.Place(rt, 380, 200, 240, 336);
                else UiKit.Place(rt, i % 5 * 200, i / 5 * 380, 184, 360);   // 2 x 5 grid
                _cards.Add(b.GetComponent<Image>());
                _cardText.Add(UiKit.LabelOf(b));
                _cardArt.Add(null);
                _shown.Add((CardState)(-1));
            }
        }

        void Draw(int i, CardVm c)
        {
            var img = _cards[i];
            var t = _cardText[i];
            if (_cardArt[i] != null) { Destroy(_cardArt[i].gameObject); _cardArt[i] = null; }
            switch (c.State)
            {
                case CardState.FaceDown:
                    img.color = UiKit.C(Palette.Panel);
                    t.text = "?";
                    break;
                case CardState.Holding:
                    img.color = UiKit.Rarity(c.Rarity) * 0.6f;   // the L/M border glow before the flip
                    t.text = "?";
                    break;
                case CardState.Flipping:
                    img.color = UiKit.C(Palette.PanelRaised);
                    t.text = "";
                    break;
                default:
                    img.color = UiKit.Rarity(c.Rarity);
                    t.color = UiKit.C(Palette.DarkTextOn(c.Rarity) ? 0x121212u : 0xFFFFFFu);
                    string chip = c.ChipKey == UiKeys.CardNew ? Vm.Localizer.Text(UiKeys.CardNew)
                                : c.ChipKey == UiKeys.CardLevelUp ? Vm.Localizer.Format(UiKeys.CardLevelUp, c.LevelBefore.ToString(), c.LevelAfter.ToString())
                                : Vm.Localizer.Format(UiKeys.CardCopy, TextFmt.IntText(c.CopiesAfter), TextFmt.IntText(c.CopiesForNext));
                    t.text = Palette.ShapeGlyph(c.Rarity) + "\n" + Vm.Localizer.Text(c.NameKey) + "\n" + chip;
                    // Icon art above the text when the build has it (issue #291).
                    var art = SpriteLibrary.Get(c.IconId);
                    if (art != null)
                    {
                        _cardArt[i] = SpriteLibrary.Overlay("Art", img.rectTransform, art, 12);
                        var ar = _cardArt[i].rectTransform;
                        ar.anchorMin = new Vector2(0, 0.45f);
                        ar.anchorMax = Vector2.one;
                        ar.offsetMin = new Vector2(12, 0);
                        ar.offsetMax = new Vector2(-12, -12);
                        t.alignment = TextAlignmentOptions.Bottom;
                    }
                    break;
            }
        }

        public void OnCue(UiCue c, double now)
        {
            if (c.Kind == CueKind.RarityFlash && c.Cell >= 0 && c.Cell < _cards.Count)
                Ui.Tweens.Pulse(_cards[c.Cell].rectTransform, now, 0.15f, c.Level >= (int)HeroCat.Sim.Content.Rarity.MYTHIC ? 0.6 : 0.4);
        }
    }
}
