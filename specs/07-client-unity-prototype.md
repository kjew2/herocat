# 07 - Unity Client Prototype (ugly UI)

> **Unity is not used in HeroCat** (`specs/00` section 7, decision 10). The Unity project this spec describes was not carried over. The screens, view-models, layout, palette, event wiring and frame-loop rules still apply: the web client in `web/` follows them. Read Unity-specific parts (project layout, MonoBehaviours, asmdefs, iOS player settings) as history.

Scope: the P0 Unity client that exposes everything in `specs/00` section 1 on a phone. It is a thin shell over the pure C# `HeroCat.Sim` assembly (`specs/03`): the client ticks the sim, sends it commands, reads its state and query API for numbers, and drains its event outbox for feedback. No game rule lives in the client. Constants are referred to by their `specs/00` section 2 names and are read from `ContentDb` (`specs/05`); the client never holds a balance literal. Where this document disagrees with `specs/00`, `specs/00` wins; where it disagrees with `specs/03` on the sim API, `specs/03` wins and section 17 lists the known gaps.

"Ugly UI" means: default font, flat colored rectangles, no art, no sound, no animation beyond fades and the gacha card flip. The client exists to playtest the loop and to catch integration bugs the bot cannot (lifecycle, clock, save, touch).

## 1. Goals and non-goals

Goals:

1. Every P0 action in `specs/00` section 1 is reachable with one or two taps on a 5.5 inch phone in portrait.
2. The sim never waits on the UI and the UI never computes a game number. Numbers come from `Simulation.State` and `Simulation.Query`; feedback comes from `SimEvent`s.
3. Backgrounding, killing and relaunching the app, and moving the device clock are all handled through the `specs/03` seams (`SyncWallClock`, `ISaveStore`, `IClock`) and are covered by the QA checklist in section 15.
4. Runs at 60 fps on a 2019 mid-range Android phone with zero steady-state managed allocation from the sim bridge (section 12).
5. A new Unity developer can build and install it from a clean checkout in under an hour following sections 3 and 13.

Non-goals (P1/P2, listed in section 16): art, audio, tutorial flow, ads, IAP, cloud save, localisation beyond the `ContentDb.Text` fallback, landscape and tablet layouts.

## 2. Unity version, template and render pipeline

**Unity 6.3 LTS** (`6000.3.x`), newest patch at project creation, pinned in `ProjectSettings/ProjectVersion.txt`. Do not change the editor version during the prototype.

Why 6.3 LTS and not 2022.3 or 6.0:

- 6.0 LTS support ends October 2026 and 2022.3 is already out of support; 6.3 LTS is supported until December 2027 ([Unity 6 support page](https://unity.com/releases/unity-6/support)). A prototype that becomes the product should not start on a stream that is ending.
- Google Play's current requirements (target API level 35 or newer for new apps and updates, 16 KB page size support for apps targeting Android 15 and newer) are met by Unity 6 out of the box; on 2022.3 they depend on late patch releases.
- UI Toolkit runtime (section 7) is stable on Unity 6; it is not on 2022.3.
- Build Profiles (section 13) replace the old per-platform define juggling for the Dev versus Release split.
- C# 9 is the compiler default, matching `HeroCat.Sim`'s `LangVersion 9.0` (`specs/03` section 2).

Template: **Universal 2D** (URP with the 2D Renderer). The client draws only UI, so the pipeline choice costs almost nothing either way; URP is chosen because it is Unity's default and forward path, the built-in pipeline is on the deprecation track, and switching later is more work than configuring URP down now. Configure the URP asset for the low-end target:

| URP setting | Value | Why |
|---|---|---|
| Renderer | 2D Renderer, no lights | UI only |
| HDR | off | memory and bandwidth on 2019 GPUs |
| MSAA | off | rectangles do not need it |
| Render Scale | 1.0 | |
| Post-processing | off | |
| Depth Texture, Opaque Texture | off | |
| SRP Batcher | on (default) | |

If the team has a strong preference for the built-in pipeline the rest of this document does not change; the only coupling is the camera clear color and the `PanelSettings` asset, neither of which is pipeline-specific.

Other project settings fixed here: Color Space Linear (URP default; fine on GLES3), Active Input Handling **Input System Package (New)**, Scripting Backend IL2CPP, API Compatibility Level **.NET Standard 2.1** (so the sim's `netstandard2.1` surface is the same in both hosts), Default Orientation **Portrait** with auto-rotation off (section 5).

## 3. Repository and project layout

The sim source is compiled by two hosts (dotnet for bots and tests, Unity for the client) from one folder. It enters Unity as a local package, not a copied DLL, so breakpoints and edits work in both.

```
herocat/
  Content/                              content_build.py output (specs/05 section 6)
  src/
    Directory.Build.props               redirects obj/ and bin/ out of the source folders (below)
    HeroCat.Sim/
      package.json                      Unity package manifest: name com.herocat.sim
      HeroCat.Sim.asmdef
      HeroCat.Sim.csproj                netstandard2.1, specs/03 section 2
      *.cs, *.cs.meta                   meta files are committed
    HeroCat.Sim.Tests/
      package.json                      com.herocat.sim.tests
      HeroCat.Sim.Tests.asmdef
      HeroCat.Sim.Tests.csproj          net8.0 + NUnit
      *.cs
    HeroCat.Bots/, HeroCat.Bots.Tests/  specs/09; never referenced by Unity
  unity/
    HeroCatClient/
      Packages/manifest.json            "com.herocat.sim": "file:../../../src/HeroCat.Sim",
                                        "com.herocat.sim.tests": "file:../../../src/HeroCat.Sim.Tests",
                                        "com.unity.nuget.newtonsoft-json": "3.2.1",
                                        "testables": ["com.herocat.sim.tests"]
      ProjectSettings/
      Assets/
        Resources/Content/*.json        git-ignored copy of ../Content, made by ContentPreBuild before each build (specs/05 section 10.1)
        Scenes/Boot.unity               the only scene
        HeroCat.Client/
          HeroCat.Client.asmdef
          Boot/        GameRoot.cs, ContentBootstrap.cs, ResourcesContentSource.cs, SimRunner.cs, SafeAreaPadding.cs
          ViewModels/  GameViewModel.cs, HudVm.cs, CombatVm.cs, StatsVm.cs, GearVm.cs, HeroesVm.cs, GachaVm.cs, ClassVm.cs,
                       SettingsVm.cs, RateTable.cs, Feedback/ToastQueue.cs, Feedback/RevealSequencer.cs, Feedback/EventFeed.cs
          Screens/     ScreenBase.cs, TabBar.cs, HudBar.cs, CombatStrip.cs, CombatScreen.cs, StatsScreen.cs, GearScreen.cs,
                       HeroesScreen.cs, GachaScreen.cs, RateScreen.cs, ClassScreen.cs, SettingsScreen.cs, Modal.cs
          Format/      NumberLabel.cs, PctText.cs, TimeText.cs, Palette.cs, HeroGlyph.cs
          Save/        SaveStoreFactory.cs            (adapter to the specs/06 store)
          Debug/       DebugPanel.cs, PerfOverlay.cs  (whole files inside #if HEROCAT_CHEATS, section 6.8)
          UI/          Root.uxml, *.uxml per screen, Theme.uss, Palette.uss, PanelSettings.asset, RuntimeTheme.tss
        HeroCat.Client.Editor/
          HeroCat.Client.Editor.asmdef  editor only
          ContentMenu.cs                HeroCat > Content > Rebuild JSON (specs/05 section 10.2)
          ContentPreBuild.cs            IPreprocessBuildWithReport: copy and hash-check Content/ (specs/05 section 10.1)
        HeroCat.Client.Tests/
          HeroCat.Client.Tests.asmdef   PlayMode tests, section 14
```

### 3.1 Assembly definitions

| asmdef | Location | References | Flags |
|---|---|---|---|
| `HeroCat.Sim` | `src/HeroCat.Sim/` | none | `noEngineReferences: true`, `autoReferenced: true`, `allowUnsafeCode: false` |
| `HeroCat.Runtime` | `src/HeroCat.Runtime/` | `HeroCat.Sim`; Newtonsoft is picked up as an auto-referenced precompiled DLL from `com.unity.nuget.newtonsoft-json` | `noEngineReferences: true`. Holds save serializer, file store, `LocalClock`, `SessionRunner` (`specs/06`; 00 section 7 decision 4) |
| `HeroCat.Sim.Tests` | `src/HeroCat.Sim.Tests/` | `HeroCat.Sim`, `optionalUnityReferences: ["TestAssemblies"]` | `defineConstraints: ["UNITY_INCLUDE_TESTS"]`, `noEngineReferences: false` (the test runner needs engine assemblies; the test code itself uses none) |
| `HeroCat.Client` | `Assets/HeroCat.Client/` | `HeroCat.Sim`, `HeroCat.Runtime`, `Unity.InputSystem` | engine references on; `HEROCAT_CHEATS` is a project define in the Dev build profile only (section 13) |
| `HeroCat.Client.Editor` | `Assets/HeroCat.Client.Editor/` | `HeroCat.Client` | `includePlatforms: ["Editor"]` |
| `HeroCat.Client.Tests` | `Assets/HeroCat.Client.Tests/` | `HeroCat.Client`, `HeroCat.Sim`, TestAssemblies | `defineConstraints: ["UNITY_INCLUDE_TESTS"]` |

`InternalsVisibleTo("HeroCat.Sim.Tests")` in the sim works unchanged because the asmdef name equals the assembly name.

Rules the layout depends on:

1. **dotnet build output must not land inside `src/HeroCat.Sim/`.** Unity imports every file in a package folder, so an `obj/` with generated `AssemblyInfo.cs` would be compiled twice. `src/Directory.Build.props` sets `BaseIntermediateOutputPath` and `BaseOutputPath` to `../build/obj/$(MSBuildProjectName)/` and `../build/bin/$(MSBuildProjectName)/`. `build/` is in `.gitignore`.
2. **Commit the `.meta` files** Unity writes into `src/HeroCat.Sim/` and `src/HeroCat.Sim.Tests/`. The `.csproj.meta` and `package.json.meta` are harmless.
3. **Tests stay within NUnit 3.5 API** (what Unity's Test Framework bundles): classic `Assert.*`, `Assert.That` with `Is.*` constraints, `[TestCase]`, `[TestCaseSource]`. No `Assert.Multiple`, no FluentAssertions. The dotnet test project pins NUnit 3.14 and `NUnit3TestAdapter`; the same source compiles in both.
4. **Newtonsoft version**: the csproj references `Newtonsoft.Json 13.0.x`, matching the 13.0.x inside `com.unity.nuget.newtonsoft-json 3.2.1`. The sim `package.json` declares that package as a dependency.
5. **C# level**: `LangVersion 9.0` in the csproj; Unity 6 compiles C# 9 by default. Do not use `file`-scoped types, `required` members or raw string literals in the sim.

### 3.2 `HeroCat.Client` internal conventions

- `ViewModels/` and `Format/` contain no `using UnityEngine` and no `UnityEngine.UIElements`. They are plain C# so that EditMode tests exercise them without a scene. This is a code-review rule, not a compiler rule; splitting them into their own asmdef is an open decision (section 17).
- `Screens/` is the only folder that touches `UnityEngine.UIElements`.
- `Boot/` is the only folder with `MonoBehaviour`s besides `Debug/`.
- Nothing outside `Boot/GameRoot.cs` and `ViewModels/GameViewModel.cs` holds a reference to `Simulation`.

## 4. Boot sequence and composition

One scene, `Boot.unity`, containing: `Main Camera` (solid clear color, culling nothing), `EventSystem` with `InputSystemUIInputModule`, `UI` (a `UIDocument` with `PanelSettings` and `Root.uxml`), and `GameRoot` (a GameObject with `ContentBootstrap`, `GameRoot`, `SimRunner`, `SafeAreaPadding`).

`GameRoot.Awake` runs the following, in order; any failure before step 7 shows a blocking modal with the error text and a "Copy" button and does not start the sim:

1. `Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0; Screen.sleepTimeout = SleepTimeout.NeverSleep;` (the phone-on-the-desk mode in `specs/02` section 7 depends on the last one; a toggle is P1).
2. `content = ContentDb.Load(new ResourcesContentSource())` (`specs/05` section 10). A `ContentLoadException` lists every `ContentError` in the modal.
3. `clock = new SystemClock()` in Release; in Dev builds wrapped in `OffsetClock` (section 6.8) so the cheat panel can move time. `specs/08` later substitutes a server-synced `IClock` here and nowhere else.
4. `store = SaveStoreFactory.Create(Application.persistentDataPath)` (`specs/06` owns the store; section 6.6 lists what the client requires from it).
5. `state = store.Load()`; if null, `state = GameState.NewProfile(content, Guid.NewGuid(), seed, clock.UtcNow)` with the profile seed derived per `specs/03` section 5.6, then the economy layer's profile-created grants (`specs/04` section 8: `TUTORIAL_GEMS`, `TUTORIAL_FREE_PULLS`) are applied through whatever entry point `specs/04`'s implementation exposes. The client never grants currency itself.
6. `outbox = new EventOutbox(EventKinds.Default | EventKinds.EnemySpawned)`; `HitLanded` stays masked out (section 8.1 explains). `sim = new Simulation(content, state, outbox, new LocalGachaService())`.
7. `sim.Validate()` (`specs/03` section 15, "call it on load"). An `InvariantViolationException` shows a modal with the message, "Copy save to clipboard" and "Reset profile" (two-step confirm); the broken save is kept by the store under a quarantine name (section 6.6).
8. `sim.SyncWallClock(clock.UtcNow)` so offline gold is credited before the first frame renders.
9. `vm = new GameViewModel(sim, content)`; `vm.Refresh()`.
10. `SimRunner.Attach(sim, outbox, clock, store, vm)`; screens bind to `vm` (section 7); the Combat tab is selected; any `OfflineGoldCredited` event already in the outbox is drained on the first `Update` and shows the welcome-back card.

## 5. Portrait phone layout

Portrait only: `Default Orientation = Portrait`, all `Allowed Orientations for Auto Rotation` off except Portrait. Reference resolution **1080 x 1920** (9:16). `PanelSettings`: Scale Mode `Scale With Screen Size`, Screen Match Mode `Match Width Or Height`, Match `0` (width). Taller phones (19.5:9, 20:9) get extra vertical room that the content panel absorbs; nothing is anchored to the bottom except the tab bar. A 16:10 tablet shows the same layout with letterbox-free stretching, which is acceptable for P0 (tablet layout is P2).

Safe area: `GameRoot` reads `Screen.safeArea` on `Start`, whenever `rootVisualElement` receives a `GeometryChangedEvent`, and on any frame where the safe area or screen size changed, converts it to panel units (`Core/SafeAreaMath`, engine-free and unit-tested) and sets `left/top/right/bottom` on the absolutely positioned `#safe` element (insets rather than padding, so absolutely positioned overlays stay inside it too). Everything else is a child of `#safe`; the camera clear color fills the notch and home-indicator bands.

Vertical bands, in reference pixels, top to bottom:

```
+------------------------------------+
| HUD bar                      120px |  gold, gems, free pulls, settings button
+------------------------------------+
| Combat strip                 560px |  always visible: stage, enemy, HP, boss timer, DPS, Boss button
+------------------------------------+
|                                    |
| Content panel          1040px+     |  the selected tab's screen (flex-grow: 1)
|                                    |
+------------------------------------+
| Tab bar                      160px |  Combat | Stats | Gear | Heroes | Gacha | Class
+------------------------------------+
```

The combat strip is always visible so that the player sees kills, drops and the boss timer while shopping; an idle game that hides its loop behind a tab feels dead. The Combat tab's content panel therefore holds the event feed and the wall card, not a second copy of the fight.

Touch targets: every button is at least 120 x 120 reference pixels (about 9 mm on a 5.5 inch 1080p phone), with 16 px gaps. Text: body 36 px, numbers in the HUD and strip 44 px, headings 48 px, captions 28 px. Default UI Toolkit runtime font; no TextMesh Pro assets.

## 6. Game loop bridge: `SimRunner`

`SimRunner` is the only `Update()` in the client that touches the sim. It owns the fixed-step accumulator, the wall-clock sync timer, the autosave timer and the outbox drain. It never calls a command; commands come from the view-model on UI callbacks, which run on the main thread between frames and therefore never interleave with `Advance` (`specs/03` section 13 and 16).

### 6.1 Constants (client-side, not content)

These are engineering limits of the bridge, not balance, so they are `const` in `SimRunner`:

| Name | Value | Meaning |
|---|---|---|
| `MAX_TICKS_PER_FRAME` | 100 | 10 s of sim time per rendered frame (same constant as `specs/06` `SaveConfig.MAX_TICKS_PER_FRAME`); the accumulator is clamped to this and anything beyond is dropped (section 6.3 explains why that is safe) |
| `IGNORE_FRAME_ABOVE_S` | 1.0 | A single frame longer than this contributes nothing to the accumulator (resume glitches, debugger pauses) |
| `WALL_CLOCK_SYNC_S` | 60 | Period of `SyncWallClock` while the app is open (`specs/03` section 16); also drives `DayRolledOver` while open |
| `AUTOSAVE_S` | 30 | Period of background autosave |
| `REFRESH_HZ` | `TICK_HZ` | Screens refresh at most once per sim tick; see 6.4 |

`TICK_HZ` is read from `content.Constants.TickHz` once at `Attach`.

### 6.2 Update

```csharp
public sealed class SimRunner : MonoBehaviour
{
    const int MaxTicksPerFrame = 50;
    const float IgnoreFrameAboveS = 1f, WallClockSyncS = 60f, AutosaveS = 30f;

    Simulation _sim; EventOutbox _outbox; IClock _clock; ISaveStore _store; GameViewModel _vm;
    readonly List<SimEvent> _events = new List<SimEvent>(1024);   // allocated once
    int _tickHz; double _acc; float _sinceSync, _sinceSave; bool _paused;

    public void Attach(Simulation sim, EventOutbox outbox, IClock clock, ISaveStore store, GameViewModel vm) { ...; _tickHz = sim.Content.Constants.TickHz; }

    void Update()
    {
        if (_paused || _sim == null) return;
        float dt = Time.unscaledDeltaTime;
        if (dt <= IgnoreFrameAboveS) _acc += dt * _vm.TimeScale;          // TimeScale is 1 outside the cheat panel
        double cap = MaxTicksPerFrame / (double)_tickHz;
        if (_acc > cap) _acc = cap;
        int ticks = (int)(_acc * _tickHz);
        if (ticks > 0) { _acc -= ticks / (double)_tickHz; _sim.Advance(ticks); }

        _sinceSync += dt;
        if (_sinceSync >= WallClockSyncS) { _sinceSync = 0f; _sim.SyncWallClock(_clock.UtcNow); }

        int n = _outbox.Drain(_events);
        for (int i = 0; i < n; i++) _vm.OnSimEvent(_events[i]);
        _events.Clear();

        if (ticks > 0 || n > 0 || _vm.ConsumeDirty()) _vm.Refresh();   // 6.4

        _sinceSave += dt;
        if (_sinceSave >= AutosaveS || _vm.ConsumeSaveRequest()) { _sinceSave = 0f; Save(); }
    }

    void OnApplicationPause(bool paused)
    {
        if (paused) { _paused = true; Save(); return; }
        _acc = 0; _sinceSync = 0f;
        _sim.SyncWallClock(_clock.UtcNow);          // catch-up; events are drained on the next Update
        _paused = false;
    }

    void OnApplicationQuit() => Save();

    void Save()
    {
#if HEROCAT_CHEATS
        _sim.Validate();                            // specs/03 section 15: on save, cheap enough in dev
#endif
        _store.Save(_sim.State);
    }
}
```

`OnApplicationFocus` is deliberately unused: on Android both focus and pause fire when the app goes to the background, and acting on both would double the work; on split-screen focus loss the game keeps running, which is the behaviour an idle game wants. `Time.timeScale` is never changed; the sim is driven from unscaled time only.

### 6.3 Why dropping backlog is safe

`SyncWallClock` (`specs/03` section 11) computes the gap as `now - (LastSyncUtcMs + ticksSinceSync / TICK_HZ)`. Ticks the bridge did run are accounted for; ticks it dropped appear as gap. A gap below `OFFLINE_MIN_GAP_S` is simply lost (a 3 s hitch loses 3 s of farming, same as the phone not running); a gap at or above it is credited as offline gold at `OFFLINE_RATE`. There is no path where time is counted twice, so the clamp in 6.2 cannot inflate income, and a device that slept without firing `OnApplicationPause` still gets its catch-up from the 60 s sync.

### 6.4 Refresh cadence

`GameViewModel.Refresh()` re-reads `State` and `Query` and updates view-model fields; screens then update only the elements whose value changed (section 7.3). `Refresh` runs on frames where at least one tick ran, at least one event arrived, or a command marked the view-model dirty. At 60 fps and `TICK_HZ = 10` that is roughly every sixth frame. The only per-frame visuals are fades and the card flip, which are USS transitions and cost no managed code.

### 6.5 Lifecycle table

| Event | Bridge action | Player sees |
|---|---|---|
| Cold start | Section 4 steps 1-10 | Combat strip within one frame of the splash; welcome-back card if `OfflineGoldCredited` fired |
| Background (`OnApplicationPause(true)`) | Save; stop ticking | nothing |
| Resume (`OnApplicationPause(false)`) | Reset accumulator; `SyncWallClock`; resume ticking | Welcome-back card if the gap was at least `OFFLINE_MIN_GAP_S`, with elapsed time, credited time (capped at `OFFLINE_CAP_H`) and gold |
| Process killed by OS | Nothing (last autosave is at most `AUTOSAVE_S` old, plus the pause save) | Same as cold start |
| Device clock moved back | Sim publishes `ClockWentBackwards`; no state change | Dev builds: a toast "Clock went back 2h 05m"; Release: nothing (`specs/08` decides policy) |
| Day boundary while open | 60 s sync publishes `DayRolledOver` | Gacha tab badge: free pulls available |
| Low memory warning | `Application.lowMemory`: save | nothing |

### 6.6 What the client needs from the save store (`specs/06`)

The client uses the store through this surface; `specs/06` owns names, file format, migrations and atomic writes. If `specs/06` names differ, `Save/SaveStoreFactory.cs` adapts.

```csharp
public interface ISaveStore
{
    GameState? Load();                 // null when no save exists; throws SaveCorruptException after quarantining the file
    void Save(GameState state);        // atomic (temp file + rename), synchronous, under 5 ms for a P0 save
    void Quarantine(string reason);      // renames the current file aside for bug reports; next Load returns null
    string ExportJson();                 // dev builds: whole save as text for the clipboard
    void ImportJson(string json);        // dev builds: replaces the save; caller restarts the sim
    void Delete();                       // "Reset profile"
}
```

Save triggers: pause, quit, low memory, every `AUTOSAVE_S`, and immediately after `Pull`, `Pull10` and a `ClassAdvanced` event (a `SaveRequested` flag the view-model sets). `ulong` seeds and counters must round-trip exactly (`specs/03` section 18 item 4).

### 6.7 Clock

`IClock` is `SystemClock` in Release. The client never passes `DateTime.Now` anywhere; it reads `_clock.UtcNow` only at the two call sites in 6.2 and at profile creation. Dev builds use `OffsetClock : IClock { IClock Inner; TimeSpan Offset; }` so the cheat panel can add hours; the offset is not persisted, so a relaunch after a `+8h` cheat produces one `ClockWentBackwards` event and nothing else, which is the correct behaviour to test.

### 6.8 Debug surface and compile-out

The sim's `DebugCommands` are always compiled (`specs/03` section 13). The client exposes them, plus save and clock cheats, only under `#if UNITY_EDITOR || DEVELOPMENT_BUILD || HEROCAT_CHEATS` (`Shell.DebugEnabled`). `HEROCAT_CHEATS` is defined by the Dev build profiles, which are also Development Builds, and never by Release profiles (section 13); the Editor always has the Debug surface through `UNITY_EDITOR`. The P0 client shows it as a Debug tab: `Screens/DebugScreen.cs` is wrapped whole in that `#if`, as are the `GameViewModel.Debug*` methods, the Shell line that adds the tab and the fps sampler in `GameRoot`. `GameViewModel.TimeScale` exists in all builds but is only writable from the panel; Release always runs at 1. A Release build therefore contains no cheat UI and no code path that sets `TimeScale` or moves `OffsetClock`.

## 7. View-model layer and UI framework

### 7.1 UI Toolkit, not uGUI

The client uses **UI Toolkit** (`UIDocument`, UXML, USS) for every screen. Reasons, in order of weight for this project:

1. The whole UI is labels, buttons, bars and grids. UXML plus USS builds such screens faster than nested prefabs with layout groups, and a layout change is a text diff, not a prefab merge.
2. Rendering cost: one `UIDocument` is one panel and renders in a handful of draw calls regardless of element count; uGUI canvases rebuild their whole mesh when any child text changes, which with ten numbers changing ten times a second needs sub-canvas surgery to stay cheap.
3. Styling by class: rarity colors, disabled states and the "can afford" badge are USS classes toggled on an element (`AddToClassList("rarity-L")`), not per-prefab color fields.
4. Transitions: the fades and the card flip are USS `transition` rules; no tween library, no coroutines.
5. It is Unity's forward path and is stable at runtime on Unity 6.

What UI Toolkit costs and how it is handled: it has no world-space UI (not needed), limited custom rendering (not needed; icons are styled elements, section 10), and fewer developers know it. The whole API surface this client uses fits in one list: `UIDocument.rootVisualElement`, `Q<T>(name)`, `RegisterCallback<ClickEvent>`, `AddToClassList/RemoveFromClassList/EnableInClassList`, `style.width = Length.Percent(x)`, `style.display`, `schedule.Execute(...)`, `ListView` (not even needed; 54 items are instantiated directly), `VisualTreeAsset.Instantiate()`. If after the first week the team is slower in UI Toolkit than they would be in uGUI, section 17 records the fallback: the view-model layer does not change, only `Screens/`.

Unity 6's runtime data-binding system (`dataSource`, `[CreateProperty]`) is **not** used in P0. It polls bound properties every frame and hides when strings are rebuilt; the explicit `Refresh` in 7.3 is predictable, allocation-controlled and trivially profiled. Revisit in P1 if screen code grows repetitive.

### 7.2 Shape: sim -> `GameViewModel` -> screens

```
Simulation.State / Simulation.Query  --Refresh()-->  GameViewModel { HudVm, CombatVm, StatsVm, GearVm, HeroesVm, GachaVm, ClassVm, SettingsVm }
EventOutbox  --Drain-->  GameViewModel.OnSimEvent(e)  -->  ToastQueue, EventFeed, RevealSequencer, Cards, badges
Screens  --bind once-->  GameViewModel ; --tap-->  GameViewModel.Commands.* --> Simulation.<command>
```

- **One `GameViewModel`**, constructed in `GameRoot`, holding one sub-view-model per screen plus the feedback queues. Screens receive it in `Bind(GameViewModel vm)` and never see `Simulation`.
- **Pull for numbers, push for feedback.** `Refresh()` copies everything a screen displays into plain fields (`double`, `int`, `bool`, `string` only where the text is static). Events are converted to feedback items at the moment they arrive and never used to derive a displayed number (`specs/03` section 12: "the client reads state for numbers").
- **Commands** are methods on `GameViewModel.Commands`: `BuyStat(int count)` (1, 10) and `BuyStatMax()`, `Enhance(int slot)`, `Pull()`, `Pull10()`, `EquipHero(int slot, HeroId id)`, `UnequipHero(int slot)`, `AutoEquipBest()`, `AttemptBoss()`. Each calls the sim, stores the result in the relevant sub-view-model (`LastReject`), sets `Dirty`, and for pulls sets `SaveRequested`. A `Reject` other than `None` becomes a 1.5 s toast with the reason text from a fixed table (`NotEnoughGold` -> "Not enough gold", and so on).
- **`BuyStat(10)`** against the `specs/03` API: the view-model sums `Query.StatCost(n..n+9)`; if `Currencies.Gold` covers it, it calls `BuyStatLevel()` ten times (no tick can run in between, so all ten succeed); otherwise the button is disabled and the command is a no-op. This is the `specs/04` section 4.2 all-or-nothing rule implemented client-side; if the sim gains `BuyStatLevels(m)` the loop is replaced (section 17).
- **Reentrancy rule**: `OnSimEvent` never calls a command. Anything a feedback item wants to do to the sim (there is nothing in P0) would be queued to the next `Update`.

Sub-view-model fields (the contract the screens bind to):

| Vm | Fields (all plain values, refreshed at `REFRESH_HZ`) |
|---|---|
| `HudVm` | `Gold`, `Gems`, `Shards`, `FreePullsRemaining` |
| `CombatVm` | `Stage`, `KillsInStage`, `IsBossStage`, `BossPhase`, `Phase`, `EnemyIsBoss`, `EnemyHp`, `EnemyMaxHp`, `BossTimerS`, `Dps`, `HitDamage`, `HitsToKill`, `KillTimeS`, `IsWalled`, `BossClearable`, `NeedDpsRatio`, `NeedDps`, `BossFailsOnStage`, `ClassIndex`, `EnemyHue` |
| `StatsVm` | `Level`, `StatMult`, `Gold`, per button: `Count`, `Cost`, `Affordable`, `DeltaPct`, `ResultMult`; `MaxCount` |
| `GearVm` | per slot: `Name`, `IsEmpty`, `Tier`, `Rarity`, `Enhance`, `BasePct`, `EffPct`, `SlotMult`; `SelectedSlot`, `EnhanceCost`, `EnhanceSuccess`, `EnhanceAffordable`, `EnhanceMaxed`, `GearMult` |
| `HeroesVm` | `Equipped[6]` (HeroRow or null), `All[54]` HeroRow { `Id`, `Tier`, `Glyph`, `Name`, `Owned`, `Copies`, `Level`, `NextLevelCopies`, `Pct`, `EquippedSlot` }, `HeroMult`, `SelectedHero`, `AutoEquipWouldChange` |
| `GachaVm` | `PityCounter`, `PitySoftStart`, `PityHard`, `PullCostText`, `Pull10CostText`, `CanPull`, `CanPull10`, `FreePullsRemaining`, `TotalPulls`, `RateTable` (section 11.6), `Reveal` (section 8.3) |
| `ClassVm` | `Advances`, `ClassName`, `ClassMult`, `NextStage` (0 if none), `Progress01`, `Milestones[]` { `Stage`, `Name`, `Reached` } |
| `SettingsVm` | `AppVersion`, `ContentVersion`, `ContentHash`, `ProfileId`, `SchemaVersion`, `UnityVersion` |

`NeedDps = Query.BossHp(Stage) * TICK_HZ / BOSS_TIME_TICKS`; `NeedDpsRatio = NeedDps / Query.Dps`. `BossClearable` is taken from `Query.BossClearable` (discrete-hit exact), the ratio is the player-facing estimate (section 8.4). `EnemyHue = (GEAR_TIER(Stage) / 10) * 23 mod 360` degrees: a cheap color change every tier so progress is visible without art.

### 7.3 Change-only updates

Every screen keeps the last displayed value per element and writes to the element only when it differs. `Format/NumberLabel.cs`:

```csharp
public sealed class NumberLabel
{
    readonly Label _label; double _last = double.NaN;
    public NumberLabel(Label l) { _label = l; }
    public void Set(double v) { if (v.Equals(_last)) return; _last = v; _label.text = BigNumber.Short(v); }
}
```

Same pattern for `IntLabel`, `PctLabel`, `TimeLabel` and for `BarFill` (sets `style.width = Length.Percent(p)` only when `p` moved by at least 0.5 points). Because `Refresh` runs at 10 Hz and most values change far less often, steady-state string allocation is a few strings per second (section 12).

## 8. Feedback design

The game's fun is in its random events, so the client spends its small budget on making those legible. Every item below is driven by one `SimEvent` and reads nothing but the event and the current `Query`.

### 8.1 Event routing

| Event | Feedback | Where |
|---|---|---|
| `EnemySpawned` | New enemy rectangle (size and border per `IsBoss`), HP bar full | Combat strip |
| `HitLanded` | masked out in P0 (10 Hz hits would only flicker; the HP bar moving on refresh is enough) | |
| `EnemyKilled` | feed line "Killed stage 37 mob, +12.3K gold" (boss: "BOSS down, +37.0K gold"); gold label pulse | Feed, HUD |
| `StageAdvanced` | stage number pulse (scale 1.0 -> 1.15 -> 1.0 over 0.2 s); feed line | Strip, feed |
| `BossFailed` | wall card (8.4), strip badge "WALL", Boss button appears | Strip, Combat tab |
| `BossCleared` | green flash on the strip (0.3 s fade), feed line "Boss 40 cleared" | Strip, feed |
| `GearDropped` | drop toast (8.2) | Toast |
| `StatLeveled` | "+5%" float above the ATK level (fades 0.5 s); only when 1-10 levels bought; Max shows one toast "Bought 37 levels" | Stats |
| `EnhanceAttempted` | success: slot flashes green, "+4!" label; fail: slot flashes red, "Failed (gold spent)" | Gear |
| `HeroPulled` | queued into `RevealSequencer` (8.3) | Gacha |
| `HeroEquipped` | slot highlight 0.3 s | Heroes |
| `ClassAdvanced` | full-width card (8.5) | Over the content panel |
| `OfflineGoldCredited` | welcome-back card: "Away 5h 12m, credited 5h 12m (cap 8h): +1.84M gold" with an OK button | Modal, non-blocking for the sim |
| `DayRolledOver` | toast "New day: 3 free pulls"; Gacha tab badge | Toast, tab bar |
| `ClockWentBackwards` | Dev builds only: toast | Toast |
| `StageCapReached` | toast "Stage cap reached" | Toast |

The feed is a ring buffer of the last 8 lines, newest at the bottom, on the Combat tab. The toast queue shows one toast at a time, bottom of the content panel, 2 s each, fade in 0.1 s and out 0.3 s; it collapses identical consecutive toasts into one with a "x3" suffix so a Max buy or a turbo run does not flood the screen.

### 8.2 Gear drop toast

Rarity color is the whole message; the text is secondary. The toast background is the rarity color (section 10.1), text black on C/R/L, white on E/M.

```
Equipped: [L] Ring T300  +1636% base      (was [R] T290 +3: +1130% effective)
Sold:     [C] Boots T300 +818% not better than equipped [E] T290 +5
```

Content from `GearDropped`: `Slot`, `Piece`, `Equipped`, `Replaced`; percentages from `Query.GearBasePct` and the effective formula `base * (1 + ENH_PER_LVL * enh)`. The "was" clause makes a replacement that lowers effective DPS visible (`specs/04` section 5.7). An L or M drop additionally flashes the whole strip border in the rarity color for 0.5 s; C and R do not interrupt anything. Toast durations: C/R 1.5 s, E 2.5 s, L/M 4 s.

### 8.3 Pull reveal cadence

`Feedback/RevealSequencer.cs` is plain C#: it is fed `PullResult`s and a `float now` and emits card states; `GachaScreen` renders them. The sim has already applied the pull when the first card is face down (gems are gone from the HUD, copies are in the Heroes tab); the reveal is purely visual and can be skipped at any time.

Single pull: one card, face down ("?" on a grey rectangle). Flip = scale X 1 -> 0 over 0.3 s, swap face (tier color, glyph, name, "NEW" or "Lv 2 -> 3", "+5 shards" if any), scale X 0 -> 1 over 0.3 s. **Total 0.6 s.** Tapping anywhere during the flip completes it instantly.

10-pull: 2 x 5 grid of face-down cards. Timeline: 0.3 s pause, then card `i` starts its flip at `0.3 + 0.15 * i` s, each flip 0.3 s shrink + 0.3 s grow. L and M cards hold a 0.4 s border glow before flipping (an extra beat players read as "something good"), which shifts the cards after them by 0.4 s. Worst case all ten are L+: about 7 s; typical: about 2.3 s. A **Skip** button is visible from the first frame: it sets every card face-up immediately (transitions disabled by a class) and shows the summary. After the last card: summary line ("1 Epic, 3 Rare, 6 Common; pity 23/80") and two buttons, "Pull 10 again" (disabled if `!CanPull10`) and "Close".

Rules: the player can leave the Gacha tab mid-reveal; the sequencer finishes silently. A second pull is not accepted while a reveal is running (buttons disabled) so results never overlap.

### 8.4 Boss fail and the wall

The wall is the central tuning relationship (`specs/02` section 3), so it must be legible at a glance:

```
+------------------------------------+
| BOSS FAILED (attempt 2)            |
| Boss HP 1.7e5 = 170K               |
| Your DPS 3.72K   Need 8.50K        |
|           Need ~2.3x DPS            |
| Farming stage 100 mobs meanwhile.  |
|            [ Retry boss ]          |
+------------------------------------+
```

`Need` is `NeedDps` from 7.2 formatted with `BigNumber.Short`; the ratio is shown with one decimal and the `~` because kills are discrete hits. While walled, the strip shows a "WALL" badge and "9/10" kill progress in red, and the `[Boss]` button is visible on the strip. The button label switches to "Boss: clearable" with a green border the moment `Query.BossClearable` becomes true (a stat buy or drop crossed the threshold), which is the only hint the player needs. `AttemptBoss` rejections (`BossAlreadyPending`, `NoBossToAttempt`) are toasts.

### 8.5 Level-up ticks and class advance

- Stat buys: `+5%` float (the delta is `STAT_PER_LVL` as a percent of base, taken from content for display) and the DPS label pulses once.
- Class advance: full-width card over the content panel, combat keeps running (`specs/04` section 6): class name from `ContentDb.Text(classDef.NameKey)`, "ATK x1.5" (`CLASS_MULT`), "DPS 2.1K -> 3.2K" (before is captured in the view-model at the previous refresh), the player's rectangle changes to the class color (section 10.1), OK button. Card auto-dismisses after 6 s if not tapped.
- Stage advance: number pulse only. Every 10th stage (a cleared boss stage) also writes "+10 gems first clear" when the sim emits that event (`specs/04` section 7.3; see 17).

### 8.6 Gold and gem counters

Instant update with the change-only rule; no count-up tween in P0 (listed P1). Gems pulse (scale 1.0 -> 1.1 -> 1.0, 0.15 s) on any increase because gem income is rare and should be noticed.

## 9. Big-number formatter

### 9.1 Contract

`HeroCat.Sim.Format.BigNumber.Short(double v)` lives in the sim assembly (`specs/03` section 4) so the client, tests and any bot column agree on one implementation. The client never formats a magnitude any other way. Contract, restated so the client and the test file can be written from this page:

1. Input is finite and non-negative in this game. Negative input formats the absolute value with a leading `-`. `NaN` and infinities format as `"?"` and are a bug upstream.
2. `v < 1000`: the value rounded half-away-from-zero to an integer, no suffix. `0 -> "0"`, `2.1 -> "2"`, `999.4 -> "999"`.
3. `v >= 1000`: let `k = floor(log1000(v))` computed by repeated division (not `Math.Log`), `m = v / 1000^k`. Format `m` to three significant digits: `1.23`, `12.3`, `123`. If rounding carries `m` to `1000` (`999.5K`), use `k + 1` and `m = 1.00`.
4. Suffix by `k`: `1 -> K`, `2 -> M`, `3 -> B`, `4 -> T`, `k >= 5 -> two lowercase letters`: `first = 'a' + (k - 5) / 26`, `second = 'a' + (k - 5) % 26`. So `aa = 1e15`, `ab = 1e18`, `az = 1e90`, `ba = 1e93`, `zz = 1e2040`, which `double` never reaches; `double.MaxValue` formats as `180dt`.
5. Culture-invariant: `.` decimal separator, no grouping.
6. No allocation other than the returned string; implemented with a small `char[]` buffer and `string.Create` or `new string(buffer, 0, len)`, no `string.Format`, no LINQ.

Examples (also the test vectors in `HeroCat.Sim.Tests/FormatTests.cs`):

| Input | Output |
|---|---|
| `0` | `0` |
| `2.1` | `2` |
| `999` | `999` |
| `999.5` | `1.00K` |
| `1234` | `1.23K` |
| `12345` | `12.3K` |
| `123456` | `123K` |
| `999_950` | `1.00M` |
| `4.56e7` | `45.6M` |
| `1.7e5` | `170K` |
| `2.9e18` | `2.90ab` |
| `1.23e17` | `123aa` |
| `1.8e308` | `180dt` |

### 9.2 Client-side text helpers (`HeroCat.Client/Format/`)

These are presentation and stay in the client:

- `PctText.Signed(double fraction)`: `+818%`, `+1.64K%` (magnitude through `BigNumber.Short` once it is at least 1000 percent), one decimal below 10 percent (`+5.0%`).
- `PctText.Chance(double p)`: `85%`, `30%`, `100%` (integer percent, floor so a 99.6 percent chance never reads as certain).
- `TimeText.Seconds(double s)`: `17.3 s` for the boss timer.
- `TimeText.Duration(double s)`: `5h 12m`, `45m 10s`, `12s`.
- `Mult.Text(double m)`: `x2.25`, `x1.5e3` above 1000.
- `IntText(long n)`: integer with no grouping (gems, copies, pity).

### 9.3 Where the formatter is tested

`HeroCat.Sim.Tests/FormatTests.cs` covers the table above, the carry case, every suffix boundary from `K` to `ab`, and a monotonicity sweep (`Short(v)` for `v` in 1, 1.001, ... never produces a shorter mantissa with a larger suffix). The client has no formatter tests of its own beyond the `NumberLabel` change-only test (section 14).

## 10. Placeholder art plan

No artist, no texture files, no sprite atlas. Everything visible is a `VisualElement` with a background color, a border and a `Label`.

### 10.1 Palette (`UI/Palette.uss`, mirrored in `Format/Palette.cs`)

| Use | Color | USS class |
|---|---|---|
| Background | `#121212` | `root` |
| Panel | `#1E1E1E` | `panel` |
| Text | `#EEEEEE` | default |
| Muted text | `#9E9E9E` | `muted` |
| Gold | `#FFD54F` | `gold` |
| Gems | `#4DD0E1` | `gems` |
| Rarity C | `#9E9E9E` | `rarity-C` |
| Rarity R | `#42A5F5` | `rarity-R` |
| Rarity E | `#AB47BC` | `rarity-E` |
| Rarity L | `#FFA726` | `rarity-L` |
| Rarity M | `#EF5350` | `rarity-M` |
| Success / affordable | `#66BB6A` | `ok` |
| Fail / wall | `#EF5350` | `bad` |
| Disabled | 40 percent opacity | `:disabled` |
| Class 0..4 | `#BDBDBD`, `#81C784`, `#64B5F6`, `#BA68C8`, `#FFB74D` | `class-N` |

Rarity classes set `background-color`; the toast and card code picks black or white text per the table in 8.2. These are the only colors in the project.

### 10.2 Hero glyphs: 54 icons without files

A hero icon is a 96 x 96 element with `border-radius: 48px`, background = tier color, containing a 40 px bold `Label` with the hero's glyph. The glyph is the tier letter plus the index within the pool, 1-based (`C01` .. `C20`, `R01` .. `R15`, `E01` .. `E10`, `L01` .. `L06`, `M01` .. `M03`), derived once at boot from `content.Heroes.Pool` order. `Format/HeroGlyph.cs` returns the string; `HeroRow.Glyph` carries it. Unowned heroes use the same element with the `unowned` class (30 percent opacity, glyph replaced by `?`). Level is a small badge in the corner (`Lv3`). Names come from `ContentDb.Text(nameKey)` and in P0 usually render as the key (`hero.c_01.name`), which is acceptable and greppable (`specs/05` section 11).

### 10.3 Combat rectangles

- Player: 160 x 160 square, class color (10.1), label "YOU".
- Normal enemy: 160 x 160 square, HSL color with hue `EnemyHue` (7.2), saturation 60 percent, lightness 45 percent; label "s37".
- Boss: 240 x 240 square, same hue, 8 px `rarity-L` border, label "BOSS 40".
- HP bar: 100 percent width, 32 px tall, fill `#EF5350` on `#424242`, text overlay "1.2M / 1.7M".
- Boss timer bar: 16 px tall under the HP bar, fill `gold`, text "17.3 s".

No sprites, no particles, no shaders. Fades and the two pulses are USS transitions on `opacity` and `scale`.

## 11. P0 screens

Wireframes are at reference width; `[ ]` is a button, `( )` a toggle, `|====    |` a bar. All numbers are examples.

### 11.1 HUD bar and combat strip (always visible)

```
+--------------------------------------------------+
| Gold 3.72M        Gems 1,240     Free 2    [cog] |   HUD, 120px
+--------------------------------------------------+
| Stage 100  BOSS           kills 9/10   WALL      |
|                                                  |
|  (E03)(R07)(R11)                                 |   party portraits, cosmetic
|  (C02)(C15)( -- )  [YOU]       [ BOSS 100 ]      |
|                                                  |
| HP |===========================         | 1.2M   |
| Boss time |==================           | 17.3 s |
| DPS 3.72K   hit 3.72K   3 hits / 3.5 s           |
|                           [ Boss: need ~2.3x ]   |   button only while walled
+--------------------------------------------------+
```

- Line 1: stage number; `BOSS` tag on boss stages; kill progress `KillsInStage/KILLS_PER_STAGE`; `WALL` badge in `bad` while `IsWalled`.
- The boss timer row is hidden unless `EnemyIsBoss`. While `BossPhase == Pending` and spawning, the enemy square reads "Boss incoming".
- Party: the six party slots as small hero glyphs (10.2) behind `[YOU]`, empty slots as `( -- )`. Each flashes for 0.15 s on a player hit. Purely cosmetic; hero damage is already in `Query.Dps` (`specs/01` section 7.1). Tapping a portrait opens the Heroes tab.
- DPS row: `Query.Dps`, `Query.HitDamage`, `HitsToKill(EnemyMaxHp)` and `KillTimeSeconds(Stage)`.
- `[Boss]` button: visible only while walled; label "Boss: need ~2.3x" (muted) or "Boss: clearable" (`ok` border) per 8.4; calls `AttemptBoss`.

### 11.2 Combat tab

```
+--------------------------------------------------+
| +----------------------------------------------+ |
| | BOSS FAILED (attempt 2)   Need ~2.3x DPS     | |   wall card, only while walled (8.4)
| | DPS 3.72K  Need 8.50K        [ Retry boss ]  | |
| +----------------------------------------------+ |
| Recent                                           |
|  Killed stage 100 mob  +263 gold                 |
|  Dropped [R] Gloves T100 (sold, not better)      |
|  Killed stage 100 mob  +263 gold                 |
|  BOSS FAILED on 100 (attempt 2)                  |
|  ... 8 lines ...                                 |
|                                                  |
| Lifetime: kills 12,345  bosses 9  fails 2        |
| drops 61  first wall: stage 100                  |
+--------------------------------------------------+
| [Combat] [Stats] [Gear] [Heroes] [Gacha] [Class] |   tab bar, 160px
+--------------------------------------------------+
```

Lifetime line reads `State.Lifetime`. Nothing on this tab is interactive except the retry button.

### 11.3 Stats tab

```
+--------------------------------------------------+
| ATK level 64        ATK x4.20                    |
| Gold 3.72M                                       |
|                                                  |
| [ x1  ]  cost 3.80K   x4.20 -> x4.25   +1.2% DPS |
| [ x10 ]  cost 52.5K   x4.20 -> x4.70   +11.9%    |
| [ Max ]  37 levels  cost 3.71M  -> x6.05  +44%   |
|                                                  |
| Next level costs STAT_COST0 * STAT_COST_G^n      |   muted caption, literal text
+--------------------------------------------------+
```

- `x1`: `Query.StatCost(Level)`; `x10`: sum of ten costs (7.2); `Max`: count found by walking `StatCost` while affordable (same loop as `BuyStatLevelsMax`, computed in the view-model for display only, bounded to 10,000 iterations). Delta is `(StatMult(n+m) / StatMult(n) - 1)` as a percent.
- Buttons disable when unaffordable; `Max` disables at 0. A `+5%` float (8.5) per single buy; `Max` shows one toast.
- A third bulk step (x100, `specs/04` `STAT_BULK_STEPS`) is added only if the content table exposes it; the screen builds one row per step from content with x1 and x10 as the fallback.

### 11.4 Gear tab

```
+--------------------------------------------------+
| Gear x1.24K   (6 slots)                          |
| +--------+ +--------+ +--------+                 |
| | Weapon | | Helm   | | Chest  |                 |
| | [L]T300| | [R]T290| | empty  |                 |
| | +7     | | +3     | |        |                 |
| +--------+ +--------+ +--------+                 |
| +--------+ +--------+ +--------+                 |
| | Gloves | | Boots  | | Ring   |                 |
| | [C]T300| | [E]T280| | [M]T300|                 |
| | +0     | | +12    | | +15 MAX|                 |
| +--------+ +--------+ +--------+                 |
| Selected: Helm  [R] T290 +3                      |
|  base +1130%   effective +1300%   slot x14.0     |
| [ Enhance +3 -> +4   85%   cost 5.26K gold ]     |
+--------------------------------------------------+
```

- Slot tile: slot name (content `gear_slots` order), rarity tag with `rarity-*` background, `T{Tier}`, `+{Enhance}` (`MAX` at `ENH_MAX`), `empty` with muted style.
- Tapping a tile selects it. The detail block shows `GearBasePct`, effective pct and `GearSlotMult(slot)`.
- Enhance button: `Query.EnhanceCost` (`ENH_COST` at the current stage), `Query.EnhanceSuccess(piece.Enhance)` via `PctText.Chance`. Disabled when empty, maxed or unaffordable, with the reason as the label ("Empty slot", "Max level", "Need 5.26K gold"). Result feedback per 8.1. Tier 0 pieces show a muted "T0: enhancing adds nothing yet".

### 11.5 Heroes tab

```
+--------------------------------------------------+
| Heroes x3.40   equipped +240%     [ Auto-equip ] |
| Equipped                                         |
|  (E03)  (R07)  (R11)  (C02)  (C15)  ( -- )       |   6 slots, tap to unequip or to place the selected hero
|  Lv2    Lv3    Lv1    Lv4    Lv2    empty        |
| Inventory  (54)                                  |
|  (C01)(C02)(C03)(C04)(C05)(C06)                  |   6 per row, 9 rows
|  Lv1  Lv4  ?    Lv1  ?    Lv2                    |
|  ...                                             |
|  (M01)(M02)(M03)                                 |
|  ?    ?    ?                                     |
| Selected: R07  hero.r_07.name   Rare             |
|  Lv3 (7/10 copies)  +28.0% DPS  equipped slot 2  |
|  [ Equip to empty slot ]  [ Unequip ]            |
+--------------------------------------------------+
```

- Inventory is all 54 heroes in pool order (C, R, E, L, M), owned ones at full opacity with `Lv`, unowned at 30 percent with `?`. Tapping any owned hero selects it; tapping an equipped slot while a hero is selected calls `EquipHero(slot, id)` (replacing), tapping an equipped slot with nothing selected calls `UnequipHero`.
- `[Equip to empty slot]` picks the first null slot; disabled if none. `[Auto-equip]` calls `AutoEquipBestHeroes` and toasts "Changed N slots"; the tab shows a badge dot while `AutoEquipWouldChange` (recomputed on `HeroPulled` and `HeroEquipped` only, by sorting a preallocated array of 54).
- Copies display `Copies/NextLevelCopies` from `HERO_LVL_CUM`; at level 10 it reads `Lv10 MAX (shards +5 per copy)`.
- The inventory grid is 54 instantiated elements, never virtualised; it fits in the content panel with scrolling (`ScrollView`, vertical only, touch scrolling is UI Toolkit's built-in behaviour and is the one non-tap input allowed, section 11.9).

### 11.6 Gacha tab, rate screen and reveal

```
+--------------------------------------------------+
| Hero Banner                     [ Rates ]        |
| Pity 23 / 80        soft pity from pull 51       |
| |=====================                         | |   pity bar: PityCounter / PITY_HARD, marker at PITY_SOFT_START
| Total pulls 61     Free pulls today 2 / 3        |
|                                                  |
|   [ Pull x1          Free (2 left) ]             |
|   [ Pull x10         1,000 gems ]                |
|                                                  |
|  Last result:  (E03) hero.e_03.name  NEW         |
+--------------------------------------------------+
```

- Pull x1 label: `Free (N left)` while `Query.FreePullsRemaining > 0`, else `100 gems` (`PULL_COST_GEMS`). Disabled when neither is available.
- Pull x10 label: `10 * PULL_COST_GEMS` gems. `specs/03` section 9 says free pulls never pay a 10-pull in P0; `specs/04` section 7.5 lets banked tutorial pulls pay one. The client shows whatever a price query on the sim reports and falls back to gems until one exists (section 17).
- Tapping either opens the reveal overlay (8.3) over the content panel; the HUD and strip remain visible and live.

Rate screen (modal over the content panel, `[Rates]`):

```
+--------------------------------------------------+
| Rates                                     [ X ]  |
| Tier      Base      With pity (consolidated)     |
| Common    60.00%    -                            |
| Rare      28.00%    -                            |
| Epic       9.00%    -                            |
| Legendary  2.50%    3.047%                       |
| Mythic     0.50%    0.609%                       |
| Legendary or better: base 3.00%, with pity 3.657%|
| Expected pulls per L+: 27.4                      |
|                                                  |
| Pity: L+ chance rises from pull 51 by 3.25 points|
| per pull and is 100% at pull 80. Counter resets  |
| on any L or M.                                   |
|  pull 50: 3.0%   55: 19.3%   60: 35.5%           |
|  65: 51.8%   70: 68.0%   75: 84.3%   80: 100%    |
| 10-pull: if slots 1-9 are below Epic, slot 10 is |
| Epic or better.                                  |
+--------------------------------------------------+
```

`ViewModels/RateTable.cs` computes the consolidated numbers at boot from content, never from literals, by the derivation in `specs/01` section 3.3:

```
q(n) = Query.PityRate(n)  for n = 1..PITY_HARD
S = 1; E = 0
for n in 1..PITY_HARD: f = S * q(n); E += n * f; S *= (1 - q(n))
consolidatedLplus = 1 / E
consolidatedM     = consolidatedLplus * M_SHARE_OF_LPLUS
consolidatedL     = consolidatedLplus - consolidatedM
```

With the canonical table this yields 3.6567 percent and 0.6094 percent and `E = 27.35`, which the EditMode test in section 14 asserts against `specs/01` section 3.2 to two decimals. Base rates come from `content.HeroBanner.BaseRates`; the ramp rows print `Query.PityRate(n)` for `n` in {50, 55, 60, 65, 70, 75, 80}. Text is composed once; the screen allocates nothing after boot.

### 11.7 Class tab

```
+--------------------------------------------------+
| Class: class.2.name          ATK x2.25           |
| Advances 2 / 4                                   |
| Next: stage 300   |=============        | 240/300|
|                                                  |
|  [x] Stage  50   class.1.name   x1.5             |
|  [x] Stage 150   class.2.name   x2.25            |
|  [ ] Stage 300   class.3.name   x3.38            |
|  [ ] Stage 500   class.4.name   x5.06            |
|                                                  |
| Class advances are automatic on reaching the     |
| stage. Each multiplies ATK by CLASS_MULT.        |
+--------------------------------------------------+
```

Milestones from `content.Class.ClassStages` and `Query.ClassMult(k)`; names from `ContentDb.Text`. Progress bar is `(Stage - prevMilestone) / (next - prevMilestone)`. No interaction.

### 11.8 Settings and Debug

```
+--------------------------------------------------+
| Settings                                  [ X ]  |
| App 0.1.0 (build 17)   Unity 6000.3.x            |
| Content 1.0  hash 3f9a...                        |
| Profile 8d1c... [ Copy ]   Save schema v1        |
| [ Reset profile ]  (tap twice within 3 s)        |
|                                                  |
| ---- Debug (Dev builds only) -------------------  |
| Gold  [ +1x cost ] [ +100x cost ] [ +1e6 ]       |
| Gems  [ +1,000 ]  [ +10,000 ]                    |
| Hero [ dropdown: C01..M03 ] copies [ 1 ] [ Add ] |
| Stage [ 49 ] [ Set ]   (resets fight)            |
| Clock [ +1 h ] [ +8 h ] [ +1 day ]  offset 0h    |
| Sim   [ Run 1 h ] [ Run 8 h ]   speed (x1)(x5)(x20)
| Save  [ Save now ] [ Export ] [ Import ] [ Validate ]
| Overlay ( ) fps / ms / GC alloc per frame        |
+--------------------------------------------------+
```

Settings proper is one modal reachable from the HUD cog. The Debug section exists only under `HEROCAT_CHEATS` (6.8) and maps to:

| Control | Call |
|---|---|
| Gold +1x / +100x cost | `Debug.GrantGold(Query.StatCost(Level) * k)` |
| Gold +1e6 | `Debug.GrantGold(1e6)` |
| Gems | `Debug.GrantGems(n)` |
| Hero copies | `Debug.GrantHeroCopies(id, n)` |
| Set stage | `Debug.SetStage(s)`; the view-model marks dirty and the feed logs it |
| Clock | `OffsetClock.Offset += span`, then `SyncWallClock(clock.UtcNow)` immediately so the offline card shows |
| Run N h | `Advance(N * 3600 * TICK_HZ)` synchronously with the view-model in "summary mode": events are counted, not toasted, and one toast prints "1h: 2,340 kills, 11 drops, stages 100 -> 118, 1 wall" |
| Speed | `GameViewModel.TimeScale = 1, 5, 20` (sim time per real second; determinism is unaffected because ticks are still ticks) |
| Save now / Export / Import / Validate | `ISaveStore.Save/ExportJson/ImportJson` (6.6), `Simulation.Validate()` with the result toasted |
| Overlay | `PerfOverlay` draws fps, frame ms, `GC.GetAllocatedBytesForCurrentThread()` delta per frame, outbox `Dropped` count, top-left, 24 px text |

"Run 8 h" on the 2019 target takes about 1.5 s of main-thread time at the `specs/03` budget and is acceptable for a cheat.

### 11.9 Input and navigation

- Tap only: `ClickEvent` on buttons and tiles. No long-press, no drag, no pinch, no swipe between tabs. The only non-tap gesture is UI Toolkit's built-in touch scroll on the Heroes inventory `ScrollView` and the rate screen.
- Tabs are exclusive; the active tab's screen is `display: flex`, the others `display: none` (not destroyed, so bound labels keep their cached values).
- Modals (rate screen, settings, welcome-back, class card, reveal) stack in a `#modal` layer over the content panel; the HUD and strip are never covered. Android back button (`Keyboard.current.escapeKey` through the Input System, which is how Unity maps Back) closes the top modal or, with none open, selects the Combat tab; it never quits.
- Disabled buttons stay visible with their reason as label text; nothing is hidden for being unaffordable except the strip's Boss button, which is hidden when there is no boss to attempt.
- Every command gives a visible result within one refresh (100 ms): either a changed number, a feedback item, or a reject toast.

## 12. Performance budget

Target device: a 2019 mid-range Android phone (Snapdragon 665/675 or Helio G80 class, 3-4 GB RAM, 1080p). Everything below is measured on that class of device in a Dev build with the overlay, then confirmed in a Release build with the Unity Profiler attached over USB.

| Metric | Budget | How it is met |
|---|---|---|
| Frame rate | 60 fps sustained on every tab, including during a 10-pull reveal and with toasts | UI Toolkit single panel; URP settings in section 2; no per-frame managed work except transitions |
| Frame time | under 12 ms total, under 2 ms in `SimRunner.Update` on a tick frame | 10 ticks per second at under 5 µs each (`specs/03` section 17); refresh at `REFRESH_HZ` with change-only writes |
| Managed allocation, steady state | 0 B per frame from `SimRunner` and `GameViewModel.Refresh` when no event arrived; under 1 KB per second overall while farming | preallocated `List<SimEvent>`; no LINQ, closures, `string.Format` or boxing in `Update`/`Refresh`; strings only on value change (7.3); sim events are the only unavoidable allocation and arrive a few times per second |
| GC | no GC spike above 2 ms during a 10 minute farm session | incremental GC on (Player Settings), allocation budget above |
| Memory | under 150 MB total PSS after 10 minutes | no textures; content JSON under 1 MB; `EventOutbox` capacity 65,536 references worst case (about 0.5 MB) |
| Cold start | under 3 s from icon tap to first frame of the strip | one scene, content load under 100 ms, no Addressables in P0 |
| Battery | the phone-on-the-desk mode runs 30 minutes without thermal throttling warnings | 60 fps cap, no post-processing, no audio |
| Build size | under 40 MB APK | no art, Managed Stripping `Low` in P0 (safe with Newtonsoft), raised later if size matters |

Rules enforced in code review for `Boot/`, `ViewModels/` and `Screens/`:

1. No `string` concatenation or interpolation outside a change-only setter.
2. No `foreach` over `Dictionary` or `IEnumerable` in `Refresh`; index loops over arrays and `List<T>`.
3. No `GetComponent`, `Find` or `Q<T>` after `Bind`; queries are resolved once and held in fields.
4. `OnSimEvent` switches on the event type with pattern matching (`switch (e) { case GearDropped g: ... }`); no reflection, no dictionary of handlers.
5. The reveal and toast systems use preallocated element pools (10 cards, 3 toasts).

The PlayMode test in section 14 asserts the zero-allocation claim for the bridge; the overlay is the manual check for the rest.

## 13. Build settings

Four Build Profiles (`Assets/Settings/BuildProfiles/`): `Android-Dev`, `Android-Release`, `iOS-Dev`, `iOS-Release`. Dev profiles add the scripting define `HEROCAT_CHEATS` and enable Development Build, Autoconnect Profiler and Script Debugging; Release profiles have none of these. A Release build must not contain the Debug section (QA item 32 checks this).

Build Profile assets are generated by Unity, so the repo does not hand-write them. `Assets/HeroCat.Client/Editor/BuildProfiles.cs` adds **Tools > HeroCat > Build Profiles > Create Dev + Release From Selected**: it copies a platform profile made in File > Build Profiles into `<Platform>-Dev` and `<Platform>-Release`, sets `HEROCAT_CHEATS` on `*-Dev` only, and removes it from the iOS and Android Player Settings defines. Development Build, Autoconnect Profiler and Script Debugging are ticked by hand on the Dev profiles. A pre-build check fails any `*-Release` build marked Development and any non-development build that still defines `HEROCAT_CHEATS`.

### 13.1 Common Player Settings

| Setting | Value |
|---|---|
| Company / Product | `<studio>` / `HeroCat` (placeholder names) |
| Package name | `com.<studio>.herocat` |
| Version / bundle code | `0.1.0` / increment per build |
| Default orientation | Portrait, auto-rotate off |
| Scripting backend | IL2CPP |
| API compatibility | .NET Standard 2.1 |
| Managed stripping | Low (P0) |
| Incremental GC | on |
| Active Input Handling | Input System Package (New) |
| Color space | Linear |
| Splash screen | Unity default (free tier shows it; irrelevant for P0) |
| Status bar | hidden |

### 13.2 Android

| Setting | Value | Why |
|---|---|---|
| Minimum API level | 24 (Android 7.0) | covers every 2019 mid-range device; Unity 6 minimum is 23 |
| Target API level | highest installed (35 or newer) | Google Play requirement for new apps and updates |
| Target architectures | ARM64 only | Play requires 64-bit; ARMv7 doubles build time and no 2019 mid-range test device needs it. Add ARMv7 only if a specific test phone is 32-bit |
| Graphics APIs | OpenGLES3 only in P0; Vulkan evaluated in P1 | 2019 Vulkan drivers are uneven on low-end GPUs; GLES3 is the known-good path for a UI-only app |
| Build type | APK for Dev, AAB for Release | sideload for playtests, Play for later |
| Keystore | debug keystore for Dev; a project keystore kept outside the repo for Release | |
| 16 KB page sizes | on (Unity 6 default) | Play requirement for apps targeting Android 15 and newer |
| Install location | automatic | |
| Internet permission | not required in P0 | no network code; `specs/08` adds it |
| Optimized frame pacing | on | smoother 60 fps on GLES3 |

Build command for CI (optional in P0): `Unity -batchmode -quit -projectPath unity/HeroCatClient -executeMethod HeroCat.Client.Editor.Builds.AndroidDev -logFile -`, where `Builds.cs` wraps `BuildPipeline.BuildPlayer` with the Dev profile.

### 13.3 iOS

| Setting | Value | Why |
|---|---|---|
| Target minimum iOS | 15.0 | Unity 6 floor; covers every device from 2017 on |
| Architecture | ARM64 | |
| Graphics API | Metal | only option |
| Requires full screen | on | portrait-only, no multitasking layouts |
| Signing | automatic with the team ID; Dev profile uses a development provisioning profile | |
| Target SDK | device SDK | |
| Camera / microphone usage descriptions | empty | no permissions used |
| Strip engine code | on | |

Xcode: the newest release that supports the installed iOS SDK; built from the Unity-generated project with no manual edits. TestFlight distribution is P1; P0 playtests use direct installs on registered devices.

## 14. Tests in the client project

The sim's correctness is tested in `HeroCat.Sim.Tests` (visible in the Unity Test Runner through `testables`). The client adds a small set that only the Unity host can run:

EditMode (`HeroCat.Client.Tests`, no scene):

1. `RateTableTests`: with the canonical content, consolidated L+ equals 3.657 percent and M equals 0.609 percent within 0.001 percentage points, and the expected pulls per L+ is 27.35 within 0.01 (`specs/01` section 3.2).
2. `RevealSequencerTests`: for 10 results with slots 3 and 8 at L, card start times are `0.3 + 0.15 i` plus 0.4 s for every L+ card before `i`; `Skip()` sets every card face-up at the same `now`.
3. `NumberLabelTests`: a `NumberLabel` set to the same value twice assigns `Label.text` once (observed via a counting fake `Label` subclass).
4. `GameViewModelTests`: `BuyStat(10)` with gold for exactly nine levels buys nothing; with gold for ten buys ten; `Refresh` after `Debug.SetStage(100)` yields `NeedDpsRatio` equal to `BossHp(100) * TICK_HZ / BOSS_TIME_TICKS / Dps` within 1e-9.
5. `EventRoutingTests`: every `SimEvent` type in `specs/03` section 12 routed through `OnSimEvent` produces exactly the feedback item 8.1 lists, and `HitLanded` produces none.

PlayMode (Boot scene):

6. `BootSmokeTest`: load `Boot.unity`, wait 30 frames, assert the Combat tab is visible and the stage label reads `Stage 1`.
7. `BridgeAllocationTest`: after 120 warm-up frames, record `GC.GetAllocatedBytesForCurrentThread()` around 600 frames with the `EventOutbox` mask set to `None`; assert the delta is 0 for the `SimRunner.Update` region (measured by wrapping the body in `Profiler.BeginSample` and comparing the allocation counter before and after each call).
8. `PauseResumeTest`: tick 100 frames, simulate `OnApplicationPause(true)` then `(false)` with the `FakeClock` advanced 2 h, assert exactly one `OfflineGoldCredited` card appears and `Currencies.Gold` grew by `min(7200, OFFLINE_CAP_H * 3600) * OFFLINE_RATE * Gold(Stage) / KillTimeSeconds(Stage)` within 1e-6 relative.

Tests run from the Unity Test Runner in the editor; CI runs them with `-runTests -testPlatform EditMode` and `PlayMode` when a licence is available.

## 15. Manual QA checklist for the first on-device playtest

Run on the target Android device with an `Android-Dev` build, then repeat items 1, 2, 5, 8, 14 and 32 with `Android-Release`. Record pass/fail and the build number.

Install and launch

1. Fresh install, cold start: first frame of the combat strip within 3 s.
2. Rotate the device: layout stays portrait.
3. Notch and gesture bar do not cover gold, gems or the tab bar.
4. First enemy spawns within 1 s; first kill within 2 s; gold increases per kill.
5. Tutorial grant is visible: gems equal `TUTORIAL_GEMS`, free pulls show the banked amount.

Combat and stages

6. Stage 1 to 2 happens after 10 kills; the stage number pulses and the feed logs it.
7. On stage 10 the 10th enemy is a boss: bigger square, `BOSS` tag, timer counting down from `BOSS_TIME_TICKS / TICK_HZ` seconds.
8. Boss cleared: green flash, feed line, stage 11, kill progress `0/10`.
9. Debug: Set stage 100. Boss fails within 20 s; the wall card shows `Need ~N.Nx DPS` and the strip shows `WALL` and `9/10` in red; the Boss button appears.
10. Buy stats until the Boss button turns `clearable`; tap it; the boss spawns within `SPAWN_DELAY_TICKS / TICK_HZ` seconds and is cleared.
11. `AttemptBoss` on a normal stage is impossible (no button); while the boss is pending the button is gone.

Stats and gear

12. `x1` buys one level, cost and `ATK x` update, `+5%` float appears; `x10` disabled when unaffordable, buys exactly 10 when affordable; `Max` buys the shown count and toasts once.
13. Debug: Grant gold `+1e6` and run `Run 1 h`: at least one gear drop toast appears with the correct rarity color; a replaced piece shows the "was" clause.
14. Enhance: success turns the tile green and increments `+N`; failure turns it red and gold is still deducted; at `+15` the button reads `Max level`; the shown chance equals `ENH_SUCCESS` for that level.

Heroes and gacha

15. Single pull: card flips in about 0.6 s; tapping mid-flip completes it; gems or free pulls decrease before the flip starts.
16. 10-pull: cards flip staggered; Skip reveals all instantly; the summary lists tier counts and the pity counter.
17. Pity counter increments per pull and resets to 0 on an L or M (use Debug gems and pull until one appears, at most 80 pulls).
18. Rate screen numbers match `specs/01` section 3.2: 3.657 percent, 0.609 percent, ramp values at 55, 60, 70, 80.
19. Pulled heroes appear in the inventory with `Lv`; a duplicate increments copies and the level changes at `HERO_LVL_CUM` boundaries.
20. `Auto-equip` fills six slots by `HeroPct`; the badge dot disappears; `Heroes xN` on the tab equals `Query.HeroMult`.
21. Manual equip and unequip work; equipping an already-equipped hero in a second slot is rejected with a toast.

Class

22. Debug: Set stage 49, clear it: class card appears, `ATK x1.5`, player square changes color, Class tab shows 1/4 reached.

Lifecycle, clock and save

23. Background the app for 2 minutes, resume: welcome-back card with elapsed about 2 m and gold equal to `120 * OFFLINE_RATE * Gold(stage) / killTime` within rounding; the strip continues from the pre-pause fight state.
24. Background for 10 s, resume: no card (below `OFFLINE_MIN_GAP_S`), no gold change.
25. Swipe-kill the app mid-boss-fight, relaunch: stage, gold, gear, heroes, pity and the fight (boss timer remaining) are restored.
26. Debug: Clock `+8 h` then `+1 day`: the first shows a capped `OFFLINE_CAP_H` credit; the second also toasts the day rollover and free pulls reset to `FREE_PULLS_PER_DAY`.
27. Set the device clock back one hour (system settings), return: no crash, no gold change, Dev toast "Clock went back".
28. `Export` then `Reset profile` then `Import`: the profile is back with the same profile id and pity counter.

Performance

29. Overlay on, Combat tab, farming: fps at or above 58, GC alloc per frame 0 B on frames with no events.
30. Overlay on during a 10-pull reveal with two toasts: fps at or above 55.
31. 30 minutes on the desk, screen stays on, device is warm but not throttling (fps still at or above 58), PSS under 150 MB in Android developer options.

Release build

32. The Settings modal has no Debug section; `TimeScale` cannot be changed; the overlay cannot be enabled.
33. Items 1, 2, 5, 8 and 14 pass identically.

## 16. P1 and P2 features and the room P0 leaves for them

| Feature | Tag | Where P0 already leaves room |
|---|---|---|
| Rewarded ads (2x offline gold, free pull) | P1 | welcome-back card has a second button slot; `OfflineGoldCredited` carries the base amount |
| IAP (gem packs) | P1 | HUD gems label is a button that opens a modal; empty in P0 |
| Cloud save UI (sign-in, conflict dialog) | P1 | `ISaveStore` is the seam; Settings modal has a reserved section |
| Animations beyond fades, pulses and the card flip (HP bar lerp, gold count-up, hit flashes, particles) | P1 | `HitLanded` is mask-in only; `BarFill` is the one place to add a lerp |
| Sound and haptics | P1 | every feedback row in 8.1 is a single dispatch point |
| Tutorial flow (guided first 10-pull) | P1 | the grants exist; the overlay layer can host step cards |
| Daily quest card and idle chest claim button | P1 | Combat tab has the vertical room under the wall card; the sim events in `specs/04` map to toasts today |
| Localisation | P1 | all text goes through `ContentDb.Text` or the fixed reject table |
| Auto boss retry toggle | P1 | sim content constant per `specs/03` section 7.5; a Settings toggle |
| Gear bag, manual equip, substats, reroll, lock | P1 | `GearDropped.Equipped == false` and `Replaced` already arrive; the Gear tab gains a list below the slots |
| Shard shop | P1 | `Currencies.Shards` is already in `HudVm` |
| Rate-up and pet banners | P1 | `GachaVm` is per banner; the tab gains a banner selector |
| 30 fps battery mode, keep-awake toggle | P1 | Settings |
| Font scaling / accessibility | P1 | all sizes are USS variables |
| Landscape and tablet layouts | P2 | |
| Analytics and replay log | P1 | a second `IEventSink` alongside the outbox |
| Push notifications (chest ready) | P2 | |
| Account sign-in, certified-progress status ("synced N min ago", "progress since last sync not counted"), leaderboard tab | P1 | HUD cog opens Settings (reserved section); a seventh tab slot is free; `specs/10` section 4.7 |
| Async PvP screens (opponent list, battle playback from server result, rating) | P1 | `BattleSim` is pure and shared, so playback is a view-model over its events; `specs/10` section 9 |
| Prestige, guilds, real-time PvP | P2 | |

## 17. Open decisions

1. **Stats bulk buy API.** `specs/03` exposes `BuyStatLevel()` and `BuyStatLevelsMax()`; `specs/04` describes `BuyStatLevels(m)` with `STAT_BULK_STEPS`. The client implements x10 as a pre-checked loop (7.2). If the sim adopts `BuyStatLevels(m)`, delete the loop and build the button rows from the content steps.
2. **10-pull price query.** `specs/03` section 9 and `specs/04` section 7.5 disagree on whether banked free pulls can pay a 10-pull. The client needs a `Query.PullPrice(count)` (or equivalent) to label the buttons correctly; until it exists the 10-pull button shows gems.
3. **Economy events not in `specs/03`.** `QuestCompleted`, `FirstClear`, `IdleChestClaimed`, `GearSold` (`specs/04` section 10) are rendered as generic `+N gems: reason` or feed-line toasts if and when they land in the sim's event list; the Daily card is P1 regardless.
4. **Save store surface.** Section 6.6 is the client's requirement list; `specs/06` owns the names. Confirm `Quarantine`, `ExportJson` and `ImportJson` or decide the cheat panel does without them.
5. **Separate `HeroCat.Client.Core` asmdef** for `ViewModels/` and `Format/` so the no-UnityEngine rule is compiler-enforced. Costs one more asmdef and a reference edit; recommended if the view-model layer grows past about ten files.
6. **UI Toolkit fallback to uGUI.** If after one week of implementation screen work is clearly slower in UI Toolkit, switch `Screens/` and `UI/` to uGUI with TextMesh Pro, keeping `ViewModels/` unchanged. Decide at the end of week one; do not run both.
7. **Consolidated rate computation location.** `RateTable` lives in the client (11.6). It is a pure function of `BannerDef` and would be better placed next to `PityRate` in `HeroCat.Sim` so the server (`specs/08`) and tools share it. Propose moving it when `specs/03` is next revised.
8. **Vulkan on Android.** P0 ships GLES3 only. Evaluate Vulkan on the test device set in P1 for battery.
9. **Reveal versus state visibility.** In P0 the HUD and Heroes tab update before the reveal finishes. If playtesters report it as spoiling, add a 'pending results' mask in `HeroesVm` that hides copies until the sequencer releases them; no sim change needed.
10. **`OFFLINE_MIN_GAP_S` and `OffsetClock` in Release.** The client assumes `OFFLINE_MIN_GAP_S` is content (proposed 60 s in `specs/03` section 11). `specs/08` decides what Release does on `ClockWentBackwards`; the client currently does nothing.
11. **Unity 6.3 patch pin and upgrade policy.** The pinned patch is chosen at project creation; decide who may bump it and when (recommendation: only between playtest rounds).
