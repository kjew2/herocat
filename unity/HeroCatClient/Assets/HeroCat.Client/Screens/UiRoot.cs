using System.Collections.Generic;
using HeroCat.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// The uGUI root and <see cref="IScreenHost"/> for the <see cref="ScreenRouter"/> (07 sections 2, 13, 16, 17.2):
    /// one ScreenSpace-Overlay canvas scaled from 1080 x 1920 (match width), a safe-area rect fitted with Core's
    /// SafeAreaMath, and one nested canvas per layer in 07's z-order (combat and board under the HUD and tab bar, then Full
    /// screens, modal scrim + modal, overlays, toasts, a blocking SCR_ERROR). Views are built from code on first show and
    /// released by the router's LRU. It also plays the vm's cues and shows the toast. Unity-only: not compiled in cloud
    /// sessions.
    /// </summary>
    public sealed class UiRoot : MonoBehaviour, IScreenHost
    {
        public readonly Dictionary<string, RectTransform> Anchors = new Dictionary<string, RectTransform>();
        public readonly Tweens Tweens = new Tweens();

        GameViewModel _vm;
        RectTransform _safe, _fullLayer, _modalLayer, _overlayLayer, _toastLayer, _errorLayer;
        Image _scrim;
        MainScreen _main;
        readonly Dictionary<string, ScreenView> _views = new Dictionary<string, ScreenView>();
        readonly List<ScreenView> _visible = new List<ScreenView>();
        Rect _lastSafe;
        Vector2Int _lastScreen;
        TextMeshProUGUI _toastText;
        Image _toastBack;
        int _toastSerial = -1;

        public GameViewModel Vm => _vm;
        public RectTransform Safe => _safe;

        /// <summary>The Full-screen content area: between the HUD and the tab bar (07 section 2).</summary>
        public RectTransform FullLayer => _fullLayer;

        public static UiRoot Create(Transform parent, GameViewModel vm)
        {
            var go = new GameObject("UiRoot", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var root = go.AddComponent<UiRoot>();
            root.Build(vm);
            return root;
        }

        /// <summary>A top-level canvas with the reference scaler, shared with the boot screen.</summary>
        public static RectTransform MakeCanvas(GameObject go, int sortingOrder)
        {
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(UiConstants.RefWidth, UiConstants.RefHeight);
            scaler.matchWidthOrHeight = 0f;   // match width (07 section 2)
            go.AddComponent<GraphicRaycaster>();
            return (RectTransform)go.transform;
        }

        void Build(GameViewModel vm)
        {
            _vm = vm;
            Tweens.ReducedMotion = vm.Prefs.ReducedMotion;
            var root = MakeCanvas(gameObject, 0);
            UiKit.Panel("Background", root, Palette.Background);   // the only thing drawn into the notch bands
            UiKit.Fill(root.GetChild(0) as RectTransform);
            _safe = UiKit.Fill(UiKit.Rect("Safe", root));
            ApplySafeArea();

            _main = UiKit.Rect("SCR_MAIN", _safe).gameObject.AddComponent<MainScreen>();
            UiKit.Fill(_main.Root);
            _main.Init(vm, this);

            float top = UiConstants.BandHud, bottom = UiConstants.BandTabs;
            _fullLayer = Layer("FullLayer", 40);
            UiKit.Fill(_fullLayer, 0, top, 0, bottom);
            _modalLayer = Layer("ModalLayer", 50);
            UiKit.Fill(_modalLayer);
            _scrim = UiKit.Panel("Scrim", _modalLayer, Palette.Scrim, Palette.ScrimAlpha, raycast: true);
            UiKit.Fill(_scrim.rectTransform);
            _scrim.gameObject.AddComponent<Button>().onClick.AddListener(() => _vm.Router.ScrimTap());
            _modalLayer.gameObject.SetActive(false);
            _overlayLayer = Layer("OverlayLayer", 60);
            UiKit.Fill(_overlayLayer, 0, top, 0, 0);
            _toastLayer = Layer("ToastLayer", 70);
            UiKit.Fill(_toastLayer);
            _errorLayer = Layer("ErrorLayer", 80);
            UiKit.Fill(_errorLayer);

            _toastBack = UiKit.Panel("Toast", _toastLayer, 0xE0E0E0, 0.95f);
            UiKit.BottomBand(_toastBack.rectTransform, bottom + 24, 96);
            _toastBack.rectTransform.offsetMin = new Vector2(48, _toastBack.rectTransform.offsetMin.y);
            _toastBack.rectTransform.offsetMax = new Vector2(-48, _toastBack.rectTransform.offsetMax.y);
            _toastText = UiKit.Text("Text", _toastBack.transform, UiKit.Body, 0x121212, TextAlignmentOptions.Center);
            UiKit.Fill(_toastText.rectTransform, 16, 8, 16, 8);
            _toastBack.gameObject.SetActive(false);

            vm.Router.SetHost(this);
        }

        RectTransform Layer(string name, int order)
        {
            var rt = UiKit.Rect(name, _safe);
            var c = rt.gameObject.AddComponent<Canvas>();
            c.overrideSorting = true;
            c.sortingOrder = order;
            rt.gameObject.AddComponent<GraphicRaycaster>();
            return rt;
        }

        // ------------------------------------------------------------------------------------------ IScreenHost

        public void Show(ScreenEntry e)
        {
            if (e.Id == ScreenIds.Main) { SyncMain(); return; }
            var v = ViewFor(e);
            if (v == null)
            {
                // A screen this build does not have: never leave the player on an empty layer.
                _vm.Context.LockedToast(null);
                if (e.Type == ScreenType.Modal) _vm.Router.CloseModal(e.Id);
                else if (e.Type == ScreenType.Overlay) _vm.Router.CloseOverlay(e.Id);
                else _vm.Router.PopFull();
                return;
            }
            v.gameObject.SetActive(true);
            v.transform.SetAsLastSibling();
            if (e.Type == ScreenType.Modal && e.Id != ScreenIds.Error) PlaceModal(v, e.Placement);
            if (!_visible.Contains(v)) _visible.Add(v);
            v.OnShown(e);
            SyncMain();
        }

        public void Hide(ScreenEntry e)
        {
            if (e.Id == ScreenIds.Main) return;
            if (_views.TryGetValue(e.Id, out var v) && v != null)
            {
                v.OnHidden();
                v.gameObject.SetActive(false);
                _visible.Remove(v);
            }
            SyncMain();
        }

        public void Evict(string screenId)
        {
            if (!_views.TryGetValue(screenId, out var v)) return;
            _views.Remove(screenId);
            _visible.Remove(v);
            if (v != null) Destroy(v.gameObject);
        }

        void SyncMain()
        {
            var r = _vm.Router;
            _main.SetBandsVisible(r.CombatVisible, r.BoardVisible);
            _modalLayer.gameObject.SetActive(r.Modal != null);
            _scrim.transform.SetAsFirstSibling();
            // A modal opened from an overlay (a summon card's SCR_ITEM_DETAIL) draws above that overlay.
            var mc = _modalLayer.GetComponent<Canvas>();
            if (mc != null) mc.sortingOrder = r.Modal != null && r.Overlays.Count > 0 ? 65 : 50;
        }

        void PlaceModal(ScreenView v, ModalPlacement p)
        {
            var rt = v.Root;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, p == ModalPlacement.BoardRegion ? 0f : 0.5f);
            rt.pivot = new Vector2(0.5f, p == ModalPlacement.BoardRegion ? 0f : 0.5f);
            // Board region: over the Fish Board band, never over the combat viewport (07 section 10 rule 4, AC 4).
            rt.anchoredPosition = p == ModalPlacement.BoardRegion ? new Vector2(0, UiConstants.BandTabs) : Vector2.zero;
            if (p == ModalPlacement.BoardRegion)
                rt.sizeDelta = new Vector2(UiConstants.ModalWidth, Mathf.Min(rt.sizeDelta.y, UiConstants.BandBoard + UiConstants.BandAction));
        }

        ScreenView ViewFor(ScreenEntry e)
        {
            if (_views.TryGetValue(e.Id, out var v) && v != null) return v;
            RectTransform parent;
            switch (e.Type)
            {
                case ScreenType.Modal: parent = e.Id == ScreenIds.Error ? _errorLayer : _modalLayer; break;
                case ScreenType.Overlay: parent = _overlayLayer; break;
                default: parent = _fullLayer; break;
            }
            var rt = UiKit.Rect(e.Id, parent);
            switch (e.Id)
            {
                case ScreenIds.Party: v = rt.gameObject.AddComponent<PartyScreen>(); break;
                case ScreenIds.Weapons: v = rt.gameObject.AddComponent<WeaponsScreen>(); break;
                case ScreenIds.Skills: v = rt.gameObject.AddComponent<SkillsScreen>(); break;
                case ScreenIds.Companions: v = rt.gameObject.AddComponent<CompanionsScreen>(); break;
                case ScreenIds.Research: v = rt.gameObject.AddComponent<ResearchScreen>(); break;
                case ScreenIds.Summon: v = rt.gameObject.AddComponent<SummonScreen>(); break;
                case ScreenIds.SummonResult: v = rt.gameObject.AddComponent<SummonResultOverlay>(); break;
                case ScreenIds.ItemDetail: v = rt.gameObject.AddComponent<ItemDetailModal>(); break;
                case ScreenIds.Confirm: v = rt.gameObject.AddComponent<ConfirmModal>(); break;
                case ScreenIds.OfflineReward: v = rt.gameObject.AddComponent<OfflineRewardModal>(); break;
                case ScreenIds.Error: v = rt.gameObject.AddComponent<ErrorModal>(); break;
                case ScreenIds.Quests: v = rt.gameObject.AddComponent<QuestsScreen>(); break;
                case ScreenIds.StageSelect: v = rt.gameObject.AddComponent<StageSelectModal>(); break;
                default:
                    Destroy(rt.gameObject);
                    return null;
            }
            if (e.Type == ScreenType.Full || e.Type == ScreenType.Overlay) UiKit.Fill(rt);
            else
            {
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(UiConstants.ModalWidth, 900);
            }
            v.Init(_vm, this);
            _views[e.Id] = v;
            return v;
        }

        // ------------------------------------------------------------------------------------------ frame

        /// <summary>Every frame after the session: safe area, back button, per-frame view work, cues, toast.</summary>
        public void Tick()
        {
            ApplySafeArea();
            double now = _vm.Now;
            if (BackPressed())
            {
                if (_vm.Back() == BackResult.Quit) Application.Quit();
            }
            _main.Tick(now);
            for (int i = 0; i < _visible.Count; i++) if (_visible[i] != null) _visible[i].Tick(now);
            PlayCues(now);
            Tweens.Update(now);
            ShowToast(now);
        }

        static bool BackPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var k = UnityEngine.InputSystem.Keyboard.current;
            return k != null && k.escapeKey.wasPressedThisFrame;   // Android back maps to Escape
#else
            return Input.GetKeyDown(KeyCode.Escape);
#endif
        }

        void PlayCues(double now)
        {
            var cues = _vm.Cues;
            for (int i = 0; i < cues.Count; i++)
            {
                var c = cues[i];
                switch (c.Kind)
                {
                    case CueKind.Shake:
                        if (c.Id != null && Anchors.TryGetValue(c.Id, out var rt)) Tweens.Shake(rt, now);
                        break;
                    case CueKind.Haptic:
                        Haptics.Play(c.Haptic, _vm.Prefs);
                        break;
                    default:
                        _main.OnCue(c, now);
                        for (int v = 0; v < _visible.Count; v++)
                            if (_visible[v] is ICueListener l) l.OnCue(c, now);
                        break;
                }
            }
            cues.Clear();
        }

        void ShowToast(double now)
        {
            if (!_vm.Toasts.TryCurrent(now, out var t))
            {
                if (_toastBack.gameObject.activeSelf) _toastBack.gameObject.SetActive(false);
                _toastSerial = -1;
                return;
            }
            if (t.Serial == _toastSerial) return;
            _toastSerial = t.Serial;
            _toastBack.gameObject.SetActive(true);
            _toastText.text = t.Display;
            _toastBack.color = UiKit.C(t.Style == ToastStyle.Reject || t.Style == ToastStyle.Error ? 0xFFCDD2u : 0xE0E0E0u, 0.95f);
            var rt = _toastBack.rectTransform;
            if (t.AnchorId != null && Anchors.TryGetValue(t.AnchorId, out var anchor) && anchor != null && anchor.gameObject.activeInHierarchy)
            {
                // Rejects sit under the pressed button (07 section 10 rule 3).
                var corners = new Vector3[4];
                anchor.GetWorldCorners(corners);
                var local = _toastLayer.InverseTransformPoint(corners[0]);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(Mathf.Min(900, _toastLayer.rect.width - 32), 88);
                float x = Mathf.Clamp(local.x + anchor.rect.width / 2, -_toastLayer.rect.width / 2 + rt.sizeDelta.x / 2,
                                      _toastLayer.rect.width / 2 - rt.sizeDelta.x / 2);
                rt.anchoredPosition = new Vector2(x, local.y - 52);
            }
            else
            {
                UiKit.BottomBand(rt, UiConstants.BandTabs + 24, 96);
                rt.offsetMin = new Vector2(48, rt.offsetMin.y);
                rt.offsetMax = new Vector2(-48, rt.offsetMax.y);
            }
        }

        /// <summary>Screen.safeArea to anchors via Core's SafeAreaMath (07 section 17.2); no-op when unchanged.</summary>
        void ApplySafeArea()
        {
            Rect safe = Screen.safeArea;
            var screen = new Vector2Int(Screen.width, Screen.height);
            if (safe == _lastSafe && screen == _lastScreen) return;
            _lastSafe = safe;
            _lastScreen = screen;
            var i = SafeAreaMath.ToPanelInsets(screen.x, screen.y, safe.x, safe.y, safe.width, safe.height, screen.x, screen.y);
            if (screen.x <= 0 || screen.y <= 0) return;
            _safe.anchorMin = new Vector2(i.Left / screen.x, i.Bottom / screen.y);
            _safe.anchorMax = new Vector2(1f - i.Right / screen.x, 1f - i.Top / screen.y);
            _safe.offsetMin = _safe.offsetMax = Vector2.zero;
        }

        public void Dispose()
        {
            _vm.Router.SetHost(null);
            Destroy(gameObject);
        }
    }

    /// <summary>A view that plays some of the vm's cues.</summary>
    public interface ICueListener
    {
        void OnCue(UiCue c, double now);
    }

    /// <summary>07 section 9 haptics on device (a vibration of similar weight); the Settings toggle is MVP.</summary>
    public static class Haptics
    {
        public static void Play(Haptic h, IClientPrefs prefs)
        {
#if UNITY_ANDROID || UNITY_IOS
            if (h == Haptic.Medium || h == Haptic.Heavy || h == Haptic.Success || h == Haptic.Failure) Handheld.Vibrate();
#endif
        }
    }
}
