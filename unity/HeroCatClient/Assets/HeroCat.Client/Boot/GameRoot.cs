using System;
using HeroCat.Client.Core;
using HeroCat.Client.Screens;
using HeroCat.Sim.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace HeroCat.Client.Boot
{
    /// <summary>
    /// Composition root and the only MonoBehaviour (specs/07 section 4). Self-bootstraps after the first scene
    /// loads, so pressing Play in any scene (even an empty one) starts the game: no scene, prefab or UXML assets.
    /// Unity callbacks are forwarded to the plain-C# SimRunnerCore (10 Hz fixed step on unscaled time).
    /// </summary>
    public sealed class GameRoot : MonoBehaviour
    {
        public static GameRoot Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("HeroCatGame");
            DontDestroyOnLoad(go);
            go.AddComponent<GameRoot>();
        }

        UIDocument _doc;
        VisualElement _safe;   // the safe-area rect inside the document root; all UI lives under it
        Rect _lastSafeArea;
        Vector2Int _lastScreen;
        Vector2 _lastPanel;
        ContentDb _content;
        ISaveBridge _save;
        OffsetClock _clock;
        SimRunnerCore _runner;
        Shell _shell;
        Label _fatal;
        bool _useBuiltinFont;
        float _fpsAcc;
        int _fpsFrames;

        void Awake()
        {
            Instance = this;
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            Application.runInBackground = true;

            if (Camera.main == null)
            {
                var cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Ui.C(Palette.Background);
                cam.orthographic = true;
                DontDestroyOnLoad(cam.gameObject);
            }
            InputSetup.EnsureEventSystem();
            CreateDocument();
        }

        void CreateDocument()
        {
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1080, 1920);
            settings.screenMatchMode = PanelScreenMatchMode.Expand;   // the whole 1080x1920 reference always fits
            var theme = Resources.Load<ThemeStyleSheet>("HeroCatTheme");
            if (theme != null) settings.themeStyleSheet = theme;

            // Add the UIDocument on an inactive object so panelSettings is set before its OnEnable runs.
            var uiGo = new GameObject("UI");
            uiGo.SetActive(false);
            uiGo.transform.SetParent(transform, false);
            _doc = uiGo.AddComponent<UIDocument>();
            _doc.panelSettings = settings;
            uiGo.SetActive(true);

            _useBuiltinFont = theme == null;
        }

        void Start()
        {
            // The document root is created in UIDocument.OnEnable; stretch it over the whole panel explicitly.
            var root = _doc.rootVisualElement;
            root.style.position = Position.Absolute;
            root.style.left = 0; root.style.right = 0; root.style.top = 0; root.style.bottom = 0;

            // Safe area (specs/07 section 5): an absolute child inset by Screen.safeArea, re-applied on change.
            _safe = new VisualElement { name = "safe", pickingMode = PickingMode.Ignore };
            _safe.style.position = Position.Absolute;
            root.Add(_safe);
            root.RegisterCallback<GeometryChangedEvent>(_ => ApplySafeArea());
            ApplySafeArea();

            if (_useBuiltinFont)
            {
                // No theme found: give the tree a font explicitly so labels still render.
                try
                {
                    var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    if (font != null) root.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromFont(font));
                }
                catch (Exception e) { Debug.LogWarning("HeroCat: no built-in font: " + e.Message); }
            }

            try
            {
                _content = ContentLocator.Load();
                Debug.Log("HeroCat: content " + _content.Version + " from " + ContentLocator.Describe);
            }
            catch (Exception e)
            {
                ShowFatal("Content failed to load from " + ContentLocator.Describe + ":\n\n" + e.Message);
                Debug.LogException(e);
                return;
            }
            RunSaveSelfCheck();
            // The build-time BackendMode picks the service implementations (01 section 1.2). TODO(#66).
            var services = Backend.Create(Application.persistentDataPath);
            Debug.Log("HeroCat: backend " + Backend.Mode);
            _save = new RuntimeSaveBridge(services.SaveStore);
            _clock = new OffsetClock(services.Clock);
            StartSession();
        }

        /// <summary>
        /// Issue #8: IL2CPP stripping can break Newtonsoft silently (fields load back as defaults), and only on
        /// device. Round-trip a sample state in memory at every launch; a failure is logged as an error, not hidden.
        /// </summary>
        void RunSaveSelfCheck()
        {
            try
            {
                if (SaveBridgeFactory.SelfCheck(_content, out var report)) Debug.Log("HeroCat: " + report);
                else Debug.LogError("HeroCat: SAVES ARE BROKEN IN THIS BUILD. A save round trip lost data, most likely "
                                    + "IL2CPP code stripping (check Assets/link.xml).\n" + report);
            }
            catch (Exception e)
            {
                Debug.LogError("HeroCat: SAVES ARE BROKEN IN THIS BUILD. The save self-check crashed.");
                Debug.LogException(e);
            }
        }

        void StartSession()
        {
            try
            {
                _runner = SimRunnerCore.Start(_content, _save, _clock);
                _shell = new Shell(_safe, _runner.Vm);
            }
            catch (Exception e)
            {
                _runner = null;
                ShowFatal("Failed to start the session:\n\n" + e);
                Debug.LogException(e);
            }
        }

        void ShowFatal(string message)
        {
            if (_fatal == null)
            {
                _fatal = Ui.Text("", Ui.Body, Palette.Bad);
                _fatal.style.position = Position.Absolute;
                _fatal.style.left = 20; _fatal.style.right = 20; _fatal.style.top = 20;
                _safe.Add(_fatal);
            }
            _fatal.text = message;
        }

        /// <summary>Insets the #safe element by Screen.safeArea, converted to panel units. No-op when nothing changed.</summary>
        void ApplySafeArea()
        {
            if (_safe == null) return;
            Rect safe = Screen.safeArea;
            var screen = new Vector2Int(Screen.width, Screen.height);
            Vector2 panel = _doc.rootVisualElement.layout.size;   // NaN until the first layout pass
            if (safe == _lastSafeArea && screen == _lastScreen && panel == _lastPanel) return;
            _lastSafeArea = safe; _lastScreen = screen; _lastPanel = panel;

            var i = SafeAreaMath.ToPanelInsets(screen.x, screen.y, safe.x, safe.y, safe.width, safe.height, panel.x, panel.y);
            _safe.style.left = i.Left;
            _safe.style.top = i.Top;
            _safe.style.right = i.Right;
            _safe.style.bottom = i.Bottom;
        }

        void Update()
        {
            ApplySafeArea();   // the safe area can change without the panel resizing (e.g. rotation, simulator device switch)
            float dt = Time.unscaledDeltaTime;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || HEROCAT_CHEATS
            _fpsAcc += dt; _fpsFrames++;
            if (_fpsAcc >= 0.5f) { DebugScreen.Fps = _fpsFrames / _fpsAcc; _fpsAcc = 0; _fpsFrames = 0; }
#endif

            if (_runner == null) return;
            _runner.Frame(dt);
            _shell.Refresh(Time.unscaledTime);

            if (_runner.Vm.ResetRequested)
            {
                _save.DeleteProfile();
                _shell.Dispose();
                _clock.OffsetSeconds = 0;
                StartSession();
            }
        }

        void OnApplicationPause(bool paused)
        {
            if (_runner == null) return;
            if (paused) _runner.OnPause();
            else _runner.OnResume();
        }

        void OnApplicationQuit() => _runner?.OnQuit();

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
