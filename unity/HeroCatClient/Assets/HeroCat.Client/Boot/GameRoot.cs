using System;
using System.IO;
using HeroCat.Client.Core;
using HeroCat.Client.Screens;
using HeroCat.Runtime.Replay;
using HeroCat.Sim.Content;
using UnityEngine;

namespace HeroCat.Client.Boot
{
    /// <summary>
    /// Composition root (07 sections 16 and 17.3). Self-bootstraps after the first scene loads, so pressing Play in any
    /// scene starts the game: no prefabs (M0 builds every uGUI screen from code). Unity callbacks are forwarded to the
    /// plain-C# ClientSession (BootFlow + SessionRunner, 01 sections 3 and 8.1) and its GameViewModel; the uGUI layer
    /// (<see cref="UiRoot"/>) is a thin view over the vms. Unity-only: not compiled in cloud sessions.
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

        ContentDb _content;
        ClientSession _session;
        JsonlCommandLog _cmdlog;
        UiRoot _ui;
        BootScreen _boot;
        Localizer _loc;
        MemoryClientPrefs _prefs;
        string _prefsPath;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || HEROCAT_CHEATS
        DebugPanel _debug;
#endif

        void Awake()
        {
            Instance = this;
            QualitySettings.vSyncCount = 0;
            ApplySleepTimeout(true);   // SCR_MAIN is the first screen; Update follows the router from then on
            Application.runInBackground = true;
            if (Camera.main == null)
            {
                var cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = UiKit.C(Palette.Background);
                cam.orthographic = true;
                DontDestroyOnLoad(cam.gameObject);
            }
            InputSetup.EnsureEventSystem();
        }

        void Start()
        {
            _prefsPath = Path.Combine(Application.persistentDataPath, "client_prefs.txt");
            _prefs = LoadPrefs(_prefsPath);
            Application.targetFrameRate = 60;   // TODO(#47): ClientPrefs.TargetFps (Battery saver 30) in SCR_SETTINGS (MVP)
            _loc = Localizer.FromJson(SafeLoadLocalization());
            _loc.Missing = key => Debug.LogWarning("HeroCat: missing localization key " + key);
            Debug.Log("HeroCat: backend " + Backend.Mode + ", " + _loc.Count + " localization rows");
            StartSession();
        }

        static string SafeLoadLocalization()
        {
            try { return ContentLocator.LoadLocalizationJson(); }
            catch (Exception e)
            {
                Debug.LogWarning("HeroCat: localization not loaded: " + e.Message);
                return null;
            }
        }

        static MemoryClientPrefs LoadPrefs(string path)
        {
            try { return File.Exists(path) ? MemoryClientPrefs.Deserialize(File.ReadAllText(path)) : new MemoryClientPrefs(); }
            catch (Exception) { return new MemoryClientPrefs(); }   // losing prefs is acceptable (07 section 3)
        }

        void SavePrefs()
        {
            try { if (_prefs != null) File.WriteAllText(_prefsPath, _prefs.Serialize()); }
            catch (Exception e) { Debug.LogWarning("HeroCat: client prefs not saved: " + e.Message); }
        }

        /// <summary>Issue #8: IL2CPP stripping can break Newtonsoft silently; round-trip a sample state at every launch.</summary>
        void RunSaveSelfCheck()
        {
            try
            {
                if (SaveBridgeFactory.SelfCheck(_content, out var report)) Debug.Log("HeroCat: " + report);
                else Debug.LogError("HeroCat: SAVES ARE BROKEN IN THIS BUILD (check Assets/link.xml).\n" + report);
            }
            catch (Exception e)
            {
                Debug.LogError("HeroCat: SAVES ARE BROKEN IN THIS BUILD. The save self-check crashed.");
                Debug.LogException(e);
            }
        }

        /// <summary>The bundled content (01 section 8.1 step 1). TODO(#212): a verified cached newer bundle goes first.</summary>
        ContentDb LoadBundledContent()
        {
            if (_content != null) return _content;
            _content = ContentLocator.Load();
            Debug.Log("HeroCat: content " + _content.Version + " from " + ContentLocator.Describe);
            RunSaveSelfCheck();
            return _content;
        }

        void StartSession()
        {
            TearDownViews();
            try
            {
                string dataDir = Application.persistentDataPath;
                _cmdlog?.Dispose();
                _cmdlog = new JsonlCommandLog(Path.Combine(dataDir, "saves", JsonlCommandLog.FileName));
                // The build-time BackendMode picks the service implementations (01 section 1.2); nothing else branches on it.
                _session = ClientSession.Start(new Func<ContentDb>[] { LoadBundledContent },
                                               (db, state, wrapClock) => Backend.Create(dataDir, db, state, wrapClock), _cmdlog,
                                               clientVersion: Application.version,
                                               options: new ClientOptions { Localizer = _loc, Prefs = _prefs, BuildHash = Application.buildGUID });
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _session = null;
                return;
            }
            if (!_session.IsReady)
            {
                // SCR_BOOT with the blocking SCR_ERROR: the code, Retry / Update / Contact support (07 section 4.1).
                var err = _session.Boot.Error;
                Debug.LogError("HeroCat: boot failed: " + err?.Code + " support id " + err?.SupportId);
                _session.BootError.OnAction = OnBootErrorAction;
                _boot = BootScreen.Create(transform, _session.BootVm, _session.BootError, _loc);
                return;
            }
            _session.Vm.Error.Log = code => Debug.LogWarning("HeroCat: SCR_ERROR replaced " + code);
            _ui = UiRoot.Create(transform, _session.Vm);
            // TODO(#118): the acknowledge-only notice modal; M0 toasts the notice body with the support id (00 decision 103).
            var notice = _session.Boot.SaveResetNotice;
            if (notice != null)
                _session.Vm.Toasts.Push(_loc.Text(notice.BodyKey).Replace("{supportId}", notice.SupportId ?? ""), ToastStyle.Error, 8);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || HEROCAT_CHEATS
            _debug = DebugPanel.Create(_ui.Safe, _session.Vm);
#endif
        }

        void OnBootErrorAction(ErrorAction a)
        {
            switch (a)
            {
                case ErrorAction.Retry:
                    _content = null;
                    StartSession();
                    break;
                case ErrorAction.UpdateApp:
                    Application.OpenURL("market://details?id=" + Application.identifier);   // TODO(#141): per-platform store URL
                    break;
                case ErrorAction.ResetProfile:
                    _session?.DeleteProfile();
                    StartSession();
                    break;
                default:
                    Debug.Log("HeroCat: contact support with id " + _session?.Boot.Error?.SupportId);
                    break;
            }
        }

        void TearDownViews()
        {
            if (_ui != null) { _ui.Dispose(); _ui = null; }
            if (_boot != null) { Destroy(_boot.gameObject); _boot = null; }
        }

        void Update()
        {
            if (_session == null || !_session.IsReady) return;
            _session.Frame();
            _ui.Tick();
            ApplySleepTimeout(_session.Vm.Router.KeepScreenAwake);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || HEROCAT_CHEATS
            _debug?.Tick(Time.unscaledDeltaTime);
#endif
            if (_session.Vm.ResetRequested)
            {
                _session.DeleteProfile();
                StartSession();
            }
        }

        bool? _keepAwake;

        /// <summary>01 section 3.3: NeverSleep on SCR_MAIN (the phone-on-the-desk idle mode), SystemSetting elsewhere.</summary>
        void ApplySleepTimeout(bool keepAwake)
        {
            if (_keepAwake == keepAwake) return;
            _keepAwake = keepAwake;
            Screen.sleepTimeout = keepAwake ? SleepTimeout.NeverSleep : SleepTimeout.SystemSetting;
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) SavePrefs();
            if (_session == null) return;
            if (paused) _session.OnPause();
            else _session.OnResume();
        }

        void OnApplicationQuit()
        {
            SavePrefs();
            _session?.OnQuit();
        }

        void OnLowMemory() => _session?.OnLowMemory();

        void OnEnable() => Application.lowMemory += OnLowMemory;

        void OnDisable() => Application.lowMemory -= OnLowMemory;

        void OnDestroy()
        {
            _cmdlog?.Dispose();
            if (Instance == this) Instance = null;
        }
    }
}
