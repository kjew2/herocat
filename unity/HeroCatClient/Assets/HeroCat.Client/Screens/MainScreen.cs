using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// SCR_MAIN (07 section 4.3; issues #111, #113, #114): HUD band, combat viewport, action strip, Fish Board and tab bar,
    /// each on its own nested canvas (07 section 13). Placeholder art per 07 section 15.1. Every value comes from
    /// <see cref="MainVm"/> through change-only sinks bound once in <see cref="Build"/>. Unity-only: not compiled in cloud
    /// sessions.
    /// </summary>
    public sealed class MainScreen : ScreenView
    {
        /// <summary>Reference pixels per chibi sprite pixel; the art bible (issue #287) owns the final value (07 section 15.3).</summary>
        const float ChibiRpPerPixel = 4f;

        RectTransform _hud, _combat, _action, _board, _tabs;
        RectTransform _goldChip, _diamondChip, _fishChip, _stageLabel, _enemy, _hero, _mergeAllBtn, _grid;
        Image _enemyImg, _heroOutline, _bossTimerFill;
        TextMeshProUGUI _enemyLabel, _hpText, _killsText, _levelFloat;
        readonly Image[] _cellFish = new Image[32];
        readonly Image[] _cellPad = new Image[32];
        readonly TextMeshProUGUI[] _cellText = new TextMeshProUGUI[32];
        readonly TextMeshProUGUI[] _damage = new TextMeshProUGUI[UiConstants.DamageLabelPool];
        readonly int[] _damageSerial = new int[UiConstants.DamageLabelPool];
        readonly Image[] _skillIcons = new Image[8];
        readonly Image[] _skillCd = new Image[8];
        readonly Image[] _tabBg = new Image[6];
        Image _ghost;
        TextMeshProUGUI _ghostText;
        RectTransform _info, _discovery;
        TextMeshProUGUI _discoveryText;
        double _discoveryUntil, _levelFloatUntil;
        float _cell = UiConstants.BoardCell, _fish = UiConstants.FishSprite;

        public FishBoardVm BoardVm => Vm.Board;
        public double Now => Vm.Now;

        public void SetBandsVisible(bool combat, bool board)
        {
            _combat.gameObject.SetActive(combat);
            _action.gameObject.SetActive(board);
            _board.gameObject.SetActive(board);
        }

        RectTransform Band(string name, int order)
        {
            var rt = UiKit.Rect(name, Root);
            var c = rt.gameObject.AddComponent<Canvas>();
            c.overrideSorting = true;
            c.sortingOrder = order;
            rt.gameObject.AddComponent<GraphicRaycaster>();
            return rt;
        }

        protected override void Build()
        {
            float tabs = UiConstants.BandTabs, board = UiConstants.BandBoard, action = UiConstants.BandAction;
            _combat = Band("Combat", 10);
            UiKit.Fill(_combat, 0, UiConstants.BandHud, 0, tabs + board + action);
            _board = Band("FishBoard", 20);
            UiKit.BottomBand(_board, tabs, board);
            _action = Band("ActionStrip", 30);
            UiKit.BottomBand(_action, tabs + board, action);
            _hud = Band("Hud", 30);
            UiKit.TopBand(_hud, 0, UiConstants.BandHud);
            _tabs = Band("TabBar", 30);
            UiKit.BottomBand(_tabs, 0, tabs);
            BuildHud();
            BuildCombat();
            BuildAction();
            BuildBoard();
            BuildTabs();
        }

        // ------------------------------------------------------------------------------------------ HUD

        void BuildHud()
        {
            var hud = Vm.Hud;
            UiKit.Fill(UiKit.Panel("Bg", _hud, Palette.Panel).rectTransform);
            _goldChip = Chip("Gold", 16, "G", Palette.Gold, hud.OnGoldChip, out var gold);
            hud.Gold.Bind(gold.Sink());
            _diamondChip = Chip("Diamond", 256, "D", Palette.Diamond, hud.OnDiamondChip, out var dia);
            hud.Diamonds.Bind(dia.Sink());
            _fishChip = Chip("FishCoin", 496, "F", Palette.FishCoin, hud.OnFishCoinChip, out var fish);
            hud.FishCoin.Bind(fish.Sink());
            var rc = Chip("RockCandy", 736, "R", Palette.RockCandy, hud.OnRockCandyChip, out var candy);
            hud.RockCandy.Bind(candy.Sink());
            hud.RockCandyVisible.Bind(new ActiveSink(rc.gameObject));

            var inbox = UiKit.Button("Inbox", _hud, hud.OnInbox, Palette.PanelRaised);
            UiKit.Place(inbox.GetComponent<RectTransform>(), 852, 4, 112, 112);
            UiKit.LabelOf(inbox).text = "✉";
            var dot = UiKit.CirclePanel("Dot", inbox.transform, UiKit.C(Palette.Bad));
            UiKit.Place(dot.rectTransform, 80, 4, 28, 28);
            hud.Inbox.Bind(new ActiveSink(dot.gameObject));
            var gear = UiKit.Button("Settings", _hud, hud.OnSettings, Palette.PanelRaised);
            UiKit.Place(gear.GetComponent<RectTransform>(), 968, 4, 112, 112);
            UiKit.LabelOf(gear).text = "⚙";
        }

        RectTransform Chip(string name, float x, string letter, uint color, UnityEngine.Events.UnityAction tap, out TextMeshProUGUI value)
        {
            var b = UiKit.Button(name, _hud, tap, Palette.PanelRaised);
            var rt = b.GetComponent<RectTransform>();
            UiKit.Place(rt, x, 8, 224, 104);
            var l = UiKit.LabelOf(b);
            UiKit.Place(l.rectTransform, 8, 24, 56, 56);
            l.text = letter;   // currency letter chip (07 section 15.1)
            l.color = UiKit.C(color);
            l.fontStyle = FontStyles.Bold;
            value = UiKit.Text("Value", rt, UiKit.Number, Palette.Text, TextAlignmentOptions.MidlineRight, bold: true);
            UiKit.Place(value.rectTransform, 64, 12, 152, 80);
            return rt;
        }

        // ------------------------------------------------------------------------------------------ combat viewport

        void BuildCombat()
        {
            var c = Vm.Combat;
            var bg = UiKit.Panel("Bg", _combat, Palette.Background, 1f, raycast: true);
            UiKit.Fill(bg.rectTransform);
            bg.gameObject.AddComponent<TapCatcher>().Init(this);   // tap anywhere else = TapZap (07 section 4.3)

            var stage = UiKit.Button("Stage", _combat, c.OnStageLabel, Palette.PanelRaised, UiKit.Body);
            _stageLabel = stage.GetComponent<RectTransform>();
            UiKit.Place(_stageLabel, 16, 16, 520, 112);
            c.StageLabel.Bind(UiKit.LabelOf(stage).Sink());

            _killsText = UiKit.Text("Kills", _combat, UiKit.Number, Palette.Text, TextAlignmentOptions.MidlineRight, bold: true);
            var kr = _killsText.rectTransform;
            kr.anchorMin = kr.anchorMax = kr.pivot = new Vector2(1, 1);
            kr.anchoredPosition = new Vector2(-16, -16);
            kr.sizeDelta = new Vector2(360, 64);
            c.Kills.Bind(new ActionTextSink(_ => WriteKills()));
            c.KillsPerStage.Bind(new ActionTextSink(_ => WriteKills()));
            c.Walled.Bind(new ActionFlagSink(on => { _killsText.color = UiKit.C(on ? Palette.Bad : Palette.Text); WriteKills(); }));

            BuildMaintenanceBanner();

            _hero = UiKit.Panel("Hero", _combat, Palette.Accent).rectTransform;
            _hero.anchorMin = _hero.anchorMax = _hero.pivot = new Vector2(0.25f, 0.5f);
            _hero.sizeDelta = new Vector2(200, 200);
            _heroOutline = _hero.GetComponent<Image>();
            var heroLabel = UiKit.Text("Label", _hero, UiKit.Title, 0x121212, TextAlignmentOptions.Center, bold: true);
            UiKit.Fill(heroLabel.rectTransform);
            heroLabel.text = "PARTY";   // placeholder art label (07 section 15.1), not player copy; the squire never fights (00 decision 109)
            var outline = _hero.gameObject.AddComponent<Outline>();
            outline.effectDistance = new Vector2(6, 6);
            outline.effectColor = UiKit.C(Palette.Gold);
            for (int i = 0; i < c.AwakenRanks.Length; i++)
                c.AwakenRanks[i].Bind(new ActionSink<int>(_ => outline.enabled = System.Array.Exists(c.AwakenRanks, r => r.Value > 0)));

            _enemyImg = UiKit.Panel("Enemy", _combat, Palette.Muted);
            _enemy = _enemyImg.rectTransform;
            _enemy.anchorMin = _enemy.anchorMax = _enemy.pivot = new Vector2(0.72f, 0.55f);
            _enemy.sizeDelta = new Vector2(180, 180);
            _enemyLabel = UiKit.Text("Label", _enemy, UiKit.Caption, 0x121212, TextAlignmentOptions.Center, bold: true);
            UiKit.Fill(_enemyLabel.rectTransform);
            c.EnemyIsBoss.Bind(new ActionFlagSink(boss => { _enemy.sizeDelta = boss ? new Vector2(260, 260) : new Vector2(180, 180); WriteEnemyLabel(); }));
            c.Stage.Bind(new ActionTextSink(_ => WriteEnemyLabel()));
            c.EnemyHue.Bind(new ActionSink<float>(h =>
            {
                _enemyImg.color = UiKit.Hue(h);
                bg.color = Color.Lerp(UiKit.C(Palette.Background), UiKit.Hue(h, 0.4f, 0.25f), 0.5f);   // chapter-tinted band
            }));

            var hp = UiKit.Bar("Hp", _combat, Palette.Bad, out var hpFill);
            hp.anchorMin = hp.anchorMax = hp.pivot = new Vector2(0.72f, 0.3f);
            hp.sizeDelta = new Vector2(420, 32);
            c.EnemyHpFill.Bind(hpFill.Sink());
            c.EnemyVisible.Bind(new ActionFlagSink(on => { _enemy.gameObject.SetActive(on); hp.gameObject.SetActive(on); }));
            _hpText = UiKit.Text("HpText", hp, UiKit.Caption, Palette.Text, TextAlignmentOptions.Center);
            UiKit.Fill(_hpText.rectTransform);
            c.EnemyHp.Bind(new ActionTextSink(_ => WriteHp()));
            c.EnemyMaxHp.Bind(new ActionTextSink(_ => WriteHp()));

            var timer = UiKit.Bar("BossTimer", _combat, Palette.Gold, out _bossTimerFill);
            timer.anchorMin = timer.anchorMax = timer.pivot = new Vector2(0.72f, 0.24f);
            timer.sizeDelta = new Vector2(420, 24);
            c.BossTimerVisible.Bind(new ActiveSink(timer.gameObject));
            c.BossTimerFill.Bind(_bossTimerFill.Sink());
            c.BossTimerBad.Bind(new ColorFlagSink(_bossTimerFill, UiKit.C(Palette.Bad), UiKit.C(Palette.Gold)));
            var timerText = UiKit.Text("Text", timer, UiKit.Micro, Palette.Text, TextAlignmentOptions.MidlineRight);
            UiKit.Fill(timerText.rectTransform, 0, -4, -96, -4);
            c.BossTimer.Bind(timerText.Sink());

            var dps = UiKit.Text("Dps", _combat, UiKit.Number, Palette.Text, TextAlignmentOptions.MidlineLeft, bold: true);
            var dr = dps.rectTransform;
            dr.anchorMin = dr.anchorMax = dr.pivot = new Vector2(0, 0);
            dr.anchoredPosition = new Vector2(16, 216);
            dr.sizeDelta = new Vector2(400, 56);
            c.Dps.Bind(new ActionTextSink(t => dps.text = Vm.Localizer.Format(UiKeys.MainDps, t)));

            // Skill row x6 and companion row x4, 88 rp icons.
            for (int i = 0; i < c.Skills.Length && i < _skillIcons.Length; i++)
            {
                int slot = i;
                var b = UiKit.Button("Skill" + i, _combat, () => Vm.Combat.OnSkillIcon(slot, Vm.ItemDetail), Palette.PanelRaised, UiKit.Micro);
                var rt = b.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 0);
                rt.anchoredPosition = new Vector2(16 + i * 100, 112);
                rt.sizeDelta = new Vector2(88, 88);
                _skillIcons[i] = b.GetComponent<Image>();
                _skillCd[i] = UiKit.Panel("Cooldown", rt, Palette.Scrim, 0.55f);
                var cd = _skillCd[i].rectTransform;
                cd.anchorMin = Vector2.zero;
                cd.anchorMax = new Vector2(1, 0);
                cd.offsetMin = cd.offsetMax = Vector2.zero;
                var label = UiKit.LabelOf(b);
                c.Skills[i].Bind(new ActionSink<SlotLook>(s =>
                {
                    _skillIcons[slot].color = s.IsEmpty ? UiKit.C(Palette.PanelRaised) : UiKit.Rarity(s.Rarity);
                    label.text = s.IsEmpty ? "--" : Palette.ShapeGlyph(s.Rarity);
                    _skillCd[slot].rectTransform.anchorMax = new Vector2(1, s.Cooldown01);
                }));
            }
            for (int i = 0; i < c.Companions.Length; i++)
            {
                var img = UiKit.Panel("Companion" + i, _combat, Palette.PanelRaised);
                var rt = img.rectTransform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 0);
                rt.anchoredPosition = new Vector2(16 + i * 100, 12);
                rt.sizeDelta = new Vector2(88, 88);
                var ccd = UiKit.Panel("Cooldown", rt, Palette.Scrim, 0.55f);
                var ccdRt = ccd.rectTransform;
                ccdRt.anchorMin = Vector2.zero;
                ccdRt.anchorMax = new Vector2(1, 0);
                ccdRt.offsetMin = ccdRt.offsetMax = Vector2.zero;
                Image art = null;
                string shown = null;
                // The companion's pixel chibi beside the squire (07 section 15.3): native size x ChibiRpPerPixel, an
                // integer multiple, point-filtered by ArtImportSettings. Hidden while the slot is empty or has no art.
                var chibiRt = UiKit.Rect("Chibi" + i, _combat);
                chibiRt.anchorMin = chibiRt.anchorMax = new Vector2(0.06f + 0.085f * i, 0.30f + (i % 2) * 0.06f);
                chibiRt.pivot = new Vector2(0.5f, 0f);
                var chibi = chibiRt.gameObject.AddComponent<Image>();
                chibi.raycastTarget = false;
                chibi.gameObject.SetActive(false);
                c.Companions[i].Bind(new ActionSink<SlotLook>(s =>
                {
                    img.color = s.IsEmpty ? UiKit.C(Palette.PanelRaised) : UiKit.Rarity(s.Rarity);
                    if (s.ItemId != shown)
                    {
                        shown = s.ItemId;
                        if (art != null) Destroy(art.gameObject);
                        art = s.IsEmpty ? null : SpriteLibrary.Overlay("Art", rt, SpriteLibrary.Get(s.IconId), 4);
                        ccd.transform.SetAsLastSibling();
                        var sprite = !s.IsEmpty && Vm.Context.Db.Companions.TryGetValue(s.ItemId, out var def) ? SpriteLibrary.Get(def.Sprite) : null;
                        chibi.sprite = sprite;
                        chibi.gameObject.SetActive(sprite != null);
                        if (sprite != null) chibiRt.sizeDelta = new Vector2(sprite.rect.width * ChibiRpPerPixel, sprite.rect.height * ChibiRpPerPixel);
                    }
                    ccdRt.anchorMax = new Vector2(1, s.Cooldown01);
                }));
            }
            // One MP bar per slot in the two-row formation (07 section 4.3, 00 decision 110): slots 1-3 in front, 4-5 behind.
            for (int i = 0; i < c.SlotMp.Length; i++)
            {
                int row = CombatVm.RowOf(i), col = row == 0 ? i : i - 3;
                float x = row == 0 ? 104 + col * 296 : 252 + col * 296;
                var mp = UiKit.Bar("Mp" + i, _combat, Palette.Diamond, out var mpFill);
                mp.anchorMin = mp.anchorMax = mp.pivot = new Vector2(0, 0);
                mp.anchoredPosition = new Vector2(x, row == 0 ? 24 : 184);
                mp.sizeDelta = new Vector2(160, 16);
                c.SlotMp[i].Bind(mpFill.Sink());
            }

            for (int i = 0; i < _damage.Length; i++)
            {
                _damage[i] = UiKit.Text("Dmg" + i, _combat, UiKit.Number, Palette.Text, TextAlignmentOptions.Center, bold: true);
                _damage[i].rectTransform.sizeDelta = new Vector2(240, 56);
                _damage[i].gameObject.SetActive(false);
            }

            _levelFloat = UiKit.Text("LevelFloat", _hero, UiKit.Title, Palette.Gold, TextAlignmentOptions.Center, bold: true);
            UiKit.Place(_levelFloat.rectTransform, -40, -72, 280, 64);
            _levelFloat.gameObject.SetActive(false);
        }

        void WriteKills()
        {
            var c = Vm.Combat;
            _killsText.text = Vm.Localizer.Format(UiKeys.MainKills, c.Kills.Text, c.KillsPerStage.Text) +
                              (c.Walled.Value ? "  " + Vm.Localizer.Text(UiKeys.MainWall) : "");
        }

        void WriteEnemyLabel()
        {
            var c = Vm.Combat;
            _enemyLabel.text = (c.EnemyIsBoss.Value ? "BOSS " : "s") + c.Stage.Value;   // placeholder art label (15.1)
        }

        void WriteHp() => _hpText.text = Vm.Combat.EnemyHp.Text + " / " + Vm.Combat.EnemyMaxHp.Text;

        /// <summary>From the TapCatcher: the tap VFX always plays; the throttle decides whether it counts.</summary>
        public void OnViewportTap()
        {
            Vm.Combat.Tap(Vm.Now);
            Ui.Tweens.Pulse(_enemy, Vm.Now, 0.06f, 0.12);
        }

        /// <summary>The maintenance banner under the stage label (01 section 8.12 step 4): countdown line, optional message.</summary>
        void BuildMaintenanceBanner()
        {
            var mb = Vm.Main.Maintenance;
            var panel = UiKit.Panel("MaintenanceBanner", _combat, Palette.PanelRaised, 0.92f);
            var rt = panel.rectTransform;
            UiKit.Place(rt, 16, 136, 1048, 96);
            var line = UiKit.Text("Line", rt, UiKit.Body, Palette.Text, TextAlignmentOptions.MidlineLeft, bold: true);
            UiKit.Place(line.rectTransform, 16, 4, 1016, 48);
            mb.Line.Bind(line.Sink());
            var msg = UiKit.Text("Message", rt, UiKit.Caption, Palette.Muted, TextAlignmentOptions.MidlineLeft);
            UiKit.Place(msg.rectTransform, 16, 52, 1016, 40);
            mb.Message.Bind(msg.Sink());
            mb.MessageVisible.Bind(new ActiveSink(msg.gameObject));
            mb.Active.Bind(new ActionFlagSink(on => line.color = UiKit.C(on ? Palette.Bad : Palette.Text)));
            mb.Visible.Bind(new ActiveSink(panel.gameObject));
        }

        // ------------------------------------------------------------------------------------------ action strip

        void BuildAction()
        {
            var c = Vm.Combat;
            var m = Vm.Main;
            var boss = UiKit.Button("Boss", _action, c.OnBoss, 0x37474F, UiKit.Body);
            var br = boss.GetComponent<RectTransform>();
            UiKit.Place(br, 16, -2, 460, 104);
            ButtonBinding.Bind(c.Boss, boss);
            var border = boss.gameObject.AddComponent<Outline>();
            border.effectDistance = new Vector2(4, 4);
            border.effectColor = UiKit.C(Palette.Ok);
            c.BossClearable.Bind(new ActionFlagSink(on => border.enabled = on));
            Anchor(CombatVm.AnchorBoss, br);

            Pill("Quests", 640, m.QuestsPill, () => Vm.Context.LockedToast("QUESTS"));
            Pill("Dungeons", 784, m.DungeonsPill, () => m.OnTab(MainTab.Dungeons));   // lock toast, or the kill switch's toast
            Pill("Inbox", 928, m.InboxPill, Vm.Hud.OnInbox);
        }

        void Pill(string name, float x, BadgeDot badge, UnityEngine.Events.UnityAction tap)
        {
            var b = UiKit.Button(name, _action, tap, Palette.PanelRaised, UiKit.Micro);
            UiKit.Place(b.GetComponent<RectTransform>(), x, -6, 136, 112);
            UiKit.LabelOf(b).text = name.Substring(0, 1);   // placeholder glyph
            var dot = UiKit.CirclePanel("Dot", b.transform, UiKit.C(Palette.Bad));
            UiKit.Place(dot.rectTransform, 104, 4, 28, 28);
            badge.Bind(new ActiveSink(dot.gameObject));
        }

        // ------------------------------------------------------------------------------------------ Fish Board

        void BuildBoard()
        {
            var b = Vm.Board;
            _cell = UiConstants.BoardCellFor(UiConstants.BandBoard, b.Height);   // 176 rp rows do not fit the 640 rp band
            _fish = _cell * UiConstants.FishSprite / UiConstants.BoardCell;
            UiKit.Fill(UiKit.Panel("Bg", _board, Palette.Panel).rectTransform);

            var gauge = UiKit.Bar("Tank", _board, Palette.Diamond, out var tankFill);
            UiKit.Place(gauge, 8, 16, 144, 40);
            b.TankFill.Bind(tankFill.Sink());
            var count = UiKit.Text("TankCount", _board, UiKit.Body, Palette.Text, TextAlignmentOptions.Center, bold: true);
            UiKit.Place(count.rectTransform, 8, 60, 144, 48);
            b.TankCount.Bind(new ActionTextSink(_ => count.text = b.TankCount.Text + "/" + b.TankCap.Text));
            b.TankCap.Bind(new ActionTextSink(_ => count.text = b.TankCount.Text + "/" + b.TankCap.Text));
            var next = UiKit.Text("NextFish", _board, UiKit.Caption, Palette.Muted, TextAlignmentOptions.Center);
            UiKit.Place(next.rectTransform, 8, 108, 144, 40);
            b.NextFishSec.Bind(next.Sink());
            b.NextFishVisible.Bind(new ActiveSink(next.gameObject));

            BoardButton("Release", 156, b.Release, b.OnRelease, FishBoardVm.AnchorRelease);
            BoardButton("ReleaseMax", 296, b.ReleaseMax, b.OnReleaseMax, FishBoardVm.AnchorReleaseMax);
            _mergeAllBtn = BoardButton("MergeAll", 436, b.MergeAll, b.OnMergeAll, FishBoardVm.AnchorMergeAll);

            // 5 x 4 grid, 176 rp cells with 8 rp gaps (912 rp wide).
            var gridImg = UiKit.Panel("Grid", _board, Palette.Panel, 1f, raycast: true);
            _grid = gridImg.rectTransform;
            float step = _cell + UiConstants.BoardGap;
            UiKit.Place(_grid, UiConstants.TankColumn, 8, b.Width * step - UiConstants.BoardGap, b.Height * step - UiConstants.BoardGap);
            _grid.gameObject.AddComponent<BoardInput>().Init(this, _grid);
            // The drag ghost exists before the cells: Bind draws each cell at once, and DrawCell touches the ghost.
            _ghost = UiKit.CirclePanel("DragGhost", _grid, Color.white);
            _ghost.rectTransform.sizeDelta = new Vector2(_fish * 1.15f, _fish * 1.15f);   // lift 1.15
            _ghostText = UiKit.Text("Level", _ghost.rectTransform, UiKit.Title, 0x121212, TextAlignmentOptions.Center, bold: true);
            UiKit.Fill(_ghostText.rectTransform);
            _ghost.gameObject.SetActive(false);
            for (int i = 0; i < b.CellCount && i < _cellFish.Length; i++)
            {
                int col = i % b.Width, row = i / b.Width;
                _cellPad[i] = UiKit.Panel("Cell" + i, _grid, Palette.PanelRaised, 0.6f);
                var pad = _cellPad[i].rectTransform;
                UiKit.Place(pad, col * step, row * step, _cell, _cell);
                Anchor("fish.cell." + i, pad);
                _cellFish[i] = UiKit.CirclePanel("Fish", pad, Color.white);
                var fr = _cellFish[i].rectTransform;
                fr.anchorMin = fr.anchorMax = fr.pivot = new Vector2(0.5f, 0.5f);
                fr.sizeDelta = new Vector2(_fish, _fish);
                _cellText[i] = UiKit.Text("Level", fr, UiKit.Title, 0x121212, TextAlignmentOptions.Center, bold: true);
                UiKit.Fill(_cellText[i].rectTransform);
                int cell = i;
                b.Cells[i].Bind(new ActionSink<CellLook>(look => DrawCell(cell, look)));
            }
            _ghost.transform.SetAsLastSibling();   // drawn above the cells

            // Long-press info popover (07 section 5 Info row); a tap outside closes it.
            var blocker = UiKit.Panel("InfoBlocker", _board, Palette.Scrim, 0.3f, raycast: true);
            UiKit.Fill(blocker.rectTransform);
            blocker.gameObject.AddComponent<Button>().onClick.AddListener(b.CloseInfo);
            _info = blocker.rectTransform;
            var card = UiKit.Panel("Card", _info, Palette.PanelRaised, 1f, raycast: true).rectTransform;
            UiKit.Place(card, 180, 60, 860, 480);
            var title = UiKit.Text("Title", card, UiKit.Title, Palette.Text, TextAlignmentOptions.Center, bold: true);
            UiKit.Place(title.rectTransform, 24, 24, 812, 72);
            b.InfoTitle.Bind(title.Sink());
            var body = UiKit.Text("Body", card, UiKit.Body, Palette.Muted, TextAlignmentOptions.Center);
            UiKit.Place(body.rectTransform, 24, 112, 812, 120);
            b.InfoBody.Bind(body.Sink());
            var discard = UiKit.Button("Discard", card, b.OnDiscard, 0x5D4037, UiKit.Body);
            UiKit.Place(discard.GetComponent<RectTransform>(), 80, 300, 700, 128);
            b.InfoDiscard.Bind(UiKit.LabelOf(discard).Sink());
            Anchor(FishBoardVm.AnchorDiscard, discard.GetComponent<RectTransform>());
            _info.gameObject.SetActive(false);
            b.InfoVisible.Bind(new ActiveSink(_info.gameObject));   // after the hide: Bind pushes the current value

            _discovery = UiKit.Panel("Discovery", _board, Palette.Gold, 0.95f).rectTransform;
            UiKit.Place(_discovery, 240, 200, 720, 200);
            _discoveryText = UiKit.Text("Text", _discovery, UiKit.Title, 0x121212, TextAlignmentOptions.Center, bold: true);
            UiKit.Fill(_discoveryText.rectTransform, 16, 16, 16, 16);
            _discovery.gameObject.SetActive(false);
        }

        RectTransform BoardButton(string name, float y, ButtonState state, UnityEngine.Events.UnityAction tap, string anchor)
        {
            var btn = UiKit.Button(name, _board, tap, 0x37474F, UiKit.Caption);
            var rt = btn.GetComponent<RectTransform>();
            UiKit.Place(rt, 8, y, 144, 112);
            var label = UiKit.LabelOf(btn);
            label.rectTransform.offsetMin = new Vector2(4, 40);
            var reason = UiKit.Text("Reason", rt, UiKit.Micro, Palette.Muted, TextAlignmentOptions.Center);
            UiKit.Fill(reason.rectTransform, 4, 72, 4, 4);
            ButtonBinding.Bind(state, btn, reason);
            Anchor(anchor, rt);
            return rt;
        }

        void DrawCell(int i, CellLook look)
        {
            var fish = _cellFish[i];
            var pad = _cellPad[i];
            bool has = look.Level > 0 && !look.Lifted;
            fish.gameObject.SetActive(has);
            if (look.Level > 0)
            {
                fish.color = UiKit.Hue(Palette.FishHue(look.Level), 0.6f, 0.9f);
                _cellText[i].text = look.Level + (look.Capped ? "♛" : "");   // crown at the level cap
            }
            uint padColor = Palette.PanelRaised;
            if (look.Hint == DropHint.Ok) padColor = Palette.Ok;
            else if (look.Hint == DropHint.Bad) padColor = Palette.Bad;
            else if (look.Selected) padColor = Palette.Accent;
            pad.color = UiKit.C(padColor, look.Hint == DropHint.None && !look.Selected ? 0.6f : 1f);
            if (look.Pulse) Ui.Tweens.Pulse(fish.rectTransform, Vm.Now, 0.1f, 0.3);
            if (look.Lifted)
            {
                _ghost.gameObject.SetActive(true);
                _ghost.color = UiKit.Hue(Palette.FishHue(look.Level), 0.6f, 0.9f);
                _ghostText.text = look.Level.ToString();
            }
            else if (Vm.Board.DragCell < 0) _ghost.gameObject.SetActive(false);
        }

        /// <summary>Grid-local reference px (origin top-left) to a cell index, or -1 between cells / off the grid.</summary>
        public int CellAt(Vector2 local)
        {
            var b = Vm.Board;
            float step = _cell + UiConstants.BoardGap;
            if (local.x < 0 || local.y < 0) return -1;
            int col = (int)(local.x / step), row = (int)(local.y / step);
            if (col >= b.Width || row >= b.Height) return -1;
            return row * b.Width + col;
        }

        public void MoveGhost(Vector2 local)
        {
            var rt = _ghost.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(local.x, -local.y);
        }

        // ------------------------------------------------------------------------------------------ tab bar

        void BuildTabs()
        {
            var m = Vm.Main;
            UiKit.Fill(UiKit.Panel("Bg", _tabs, Palette.Panel).rectTransform);
            for (int i = 0; i < 6; i++)
            {
                var tab = (MainTab)i;
                var b = UiKit.Button("Tab" + tab, _tabs, () => Vm.Main.OnTab(tab), Palette.Panel, UiKit.Caption);
                var rt = b.GetComponent<RectTransform>();
                UiKit.Place(rt, i * 180, 0, 180, UiConstants.BandTabs);
                _tabBg[i] = b.GetComponent<Image>();
                var label = UiKit.LabelOf(b);
                label.text = Vm.Localizer.Text(UiKeys.TabKey(tab));
                label.rectTransform.offsetMin = new Vector2(4, 8);
                label.rectTransform.offsetMax = new Vector2(-4, -88);
                var icon = UiKit.Text("Icon", rt, UiKit.Title, Palette.Text, TextAlignmentOptions.Center);
                UiKit.Place(icon.rectTransform, 58, 16, 64, 64);
                icon.text = tab.ToString().Substring(0, 1);   // placeholder icon (07 section 15.1)
                var lockGlyph = UiKit.Text("Lock", rt, UiKit.Caption, Palette.Muted, TextAlignmentOptions.Center);
                UiKit.Place(lockGlyph.rectTransform, 120, 8, 48, 40);
                lockGlyph.text = "\u2298";   // ⊘ placeholder lock: the M0 font has no emoji (07 section 15.1)
                m.TabLocked[i].Bind(new ActiveSink(lockGlyph.gameObject));
                m.TabDisabled[i].Bind(new ActionFlagSink(off => { icon.color = UiKit.C(off ? Palette.Muted : Palette.Text); label.color = UiKit.C(off ? Palette.Muted : Palette.Text); }));
                var dot = UiKit.CirclePanel("Dot", rt, UiKit.C(Palette.Bad));
                UiKit.Place(dot.rectTransform, 116, 12, 28, 28);
                m.TabBadges[i].Bind(new ActiveSink(dot.gameObject));
                Anchor("tab." + i, rt);
            }
            m.ActiveTab.Bind(new ActionSink<int>(active =>
            {
                for (int i = 0; i < _tabBg.Length; i++) _tabBg[i].color = UiKit.C(i == active ? Palette.Accent : Palette.Panel, i == active ? 0.35f : 1f);
            }));
        }

        // ------------------------------------------------------------------------------------------ per frame

        public override void Tick(double now)
        {
            // Damage numbers: one pooled label per pool slot, restarted when the slot's serial changes.
            var pool = Vm.Combat.DamageNumbers;
            for (int i = 0; i < _damage.Length && i < pool.Capacity; i++)
            {
                ref var d = ref pool[i];
                var label = _damage[i];
                if (!d.Active)
                {
                    if (label.gameObject.activeSelf) label.gameObject.SetActive(false);
                    continue;
                }
                if (_damageSerial[i] != d.Serial)
                {
                    _damageSerial[i] = d.Serial;
                    label.gameObject.SetActive(true);
                    label.color = UiKit.C(d.Crit ? Palette.Gold : d.Source == DamageSource.Fish ? Palette.FishCoin : Palette.Text);
                    var rt = label.rectTransform;
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.72f, 0.6f);
                    rt.anchoredPosition = new Vector2((i % 5 - 2) * 40, (i % 3) * 30);
                    Ui.Tweens.Float(rt, now);
                }
                label.text = BigNumber.Short(d.Amount) + (d.Crit ? "!" : "");
            }
            if (_discovery.gameObject.activeSelf && now >= _discoveryUntil) _discovery.gameObject.SetActive(false);
            if (_levelFloat.gameObject.activeSelf && now >= _levelFloatUntil) _levelFloat.gameObject.SetActive(false);
        }

        public void OnCue(UiCue c, double now)
        {
            switch (c.Kind)
            {
                case CueKind.GoldPulse: Ui.Tweens.Pulse(_goldChip, now); break;
                case CueKind.DiamondPulse: Ui.Tweens.Pulse(_diamondChip, now); break;
                case CueKind.FishCoinPulse: Ui.Tweens.Pulse(_fishChip, now); break;
                case CueKind.StagePulse: Ui.Tweens.Pulse(_stageLabel, now, 0.08f); break;
                case CueKind.EnemyKilled: Ui.Tweens.Pop(_enemy, now); break;
                case CueKind.BossFlash:
                case CueKind.BossFailed: Ui.Tweens.Pulse(_enemy, now, 0.25f, 0.4); break;
                case CueKind.LevelUpFloat:
                    _levelFloat.gameObject.SetActive(true);
                    _levelFloat.text = Vm.Localizer.Format(UiKeys.PartyLevel, c.Level.ToString());
                    _levelFloatUntil = now + 1.0;
                    Ui.Tweens.Float(_levelFloat.rectTransform, now, 60, 1.0);
                    break;
                case CueKind.FishPop:
                case CueKind.FishMove:
                    if (c.Cell >= 0 && c.Cell < _cellFish.Length) Ui.Tweens.Pop(_cellFish[c.Cell].rectTransform, now);
                    break;
                case CueKind.FishMerge:
                    if (c.Cell >= 0 && c.Cell < _cellFish.Length) Ui.Tweens.Pop(_cellFish[c.Cell].rectTransform, now);
                    Ui.Tweens.Pulse(_enemy, now, 0.1f, 0.2);   // the missile's payoff (placeholder)
                    break;
                case CueKind.FishInvalid:
                    if (c.Cell >= 0 && c.Cell < _cellPad.Length) Ui.Tweens.Shake(_cellPad[c.Cell].rectTransform, now);
                    break;
                case CueKind.FishAtCap:
                    Vm.Toasts.Push(Vm.Localizer.Text(UiKeys.FishMaxLevel), ToastStyle.Info);
                    break;
                case CueKind.FishSnapBack:
                    _ghost.gameObject.SetActive(false);
                    break;
                case CueKind.FishDiscovered:
                    _discovery.gameObject.SetActive(true);
                    _discoveryText.text = Vm.Localizer.Format(UiKeys.FishDiscovered, c.Level.ToString(), BigNumber.Short(c.Value));
                    _discoveryUntil = now + UiConstants.DiscoveryCardSec;
                    break;
                case CueKind.MergeAllPulse: Ui.Tweens.Pulse(_mergeAllBtn, now); break;
            }
        }
    }
}
