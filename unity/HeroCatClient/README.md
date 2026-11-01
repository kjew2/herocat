# HeroCatClient (Unity POC)

Ugly-UI Unity client for HeroCat (`specs/07`). It is a thin shell over the pure C# sim in
`src/HeroCat.Sim`. The sim comes in as a local package: `Packages/manifest.json` has `"com.herocat.sim": "file:../../../src/HeroCat.Sim"`.

## Run it

1. Install **Unity 6000.6.7f1** with Unity Hub, the version in `ProjectSettings/ProjectVersion.txt` (`specs/07` section 2).
   Opening the project in another version upgrades it; don't commit that unless the team agreed to move the pin.
2. Unity Hub → **Add project from disk** → choose `unity/HeroCatClient` (this folder).
3. On first open Unity imports packages and generates `ProjectSettings/*` and the `.meta` files, including some in
   `src/HeroCat.Sim/`. Commit them. If Unity asks to enable the new Input System backends, answer **Yes**. Either
   setting works: the code picks the matching UI input module.
4. Open `Assets/Main.unity` (an empty scene, already in the build list) and press **Play**. No scene, prefab or UXML
   setup is needed. `GameRoot` bootstraps itself through `[RuntimeInitializeOnLoadMethod]` and builds the whole UI
   from code.

For a phone-like view, set the Game view to a portrait resolution such as 1080x1920. Landscape also works, with the
layout centred.

Content is read directly from the repo's `Content/` folder: the loader walks up from `Assets/`. Nothing is copied for Play. Edit
`content_src/*.csv`, run `python3 tools/content_build.py`, then press Play again.

**iOS player settings** (bundle ID, version, IL2CPP, .NET Standard 2.1, iOS 15 minimum, portrait only, Input
System, app icon, plain launch screen) and the URP asset (`Assets/Settings/HeroCat_URP.asset`: 2D Renderer; HDR, MSAA,
post-processing, depth and opaque textures off) are applied by **Tools → HeroCat → Apply iOS Settings**. Run it once
after the first open, restart the editor if it asks (Active Input Handling changed), and commit `ProjectSettings/*`
and `Assets/Settings/`. Re-running is safe. **Tools → HeroCat → Configure URP** does only the URP part. The bundle ID
`com.kjew2.herocat` is a placeholder (`Editor/ProjectSetup.cs`), and the signing team ID is left empty for the Mac.

**Device builds** cannot see the repo, so every player build copies the content in for you.
`Editor/ContentPreBuild.cs` (an `IPreprocessBuildWithReport`) checks the repo's `Content/` against its
`manifest.json`, copies the `*.json` files into `Assets/Resources/Content`, and checks the copy. The loader falls back
to `Resources/Content` on device. The build fails if `manifest.json` is missing or a hash doesn't match; fix that with
`python3 tools/content_build.py`. The copy is git-ignored, so don't commit it or edit it. **Tools → HeroCat → Copy
Content into Resources** runs the same step without building.

**Code stripping.** `Assets/link.xml` keeps IL2CPP from stripping the Sim, Runtime and Newtonsoft.Json code that save
serialization reaches only by reflection. At every launch `GameRoot` round-trips a sample save in memory
(`SaveSelfCheck`) and logs `HeroCat: save self-check OK`, or an error starting `SAVES ARE BROKEN IN THIS BUILD`. Check the
device log for it after each new build.

## What's there

The HUD (gold, gems, free pulls, shards) and the combat strip are always visible. The strip shows the stage, kill
progress, a WALL badge, party, player and enemy rectangles, HP and boss-timer bars, DPS, and a Boss retry button that
appears while you are walled.

The tabs are:

| Tab | Contents |
|---|---|
| **Combat** | Wall card with "need ~N.Nx DPS", event feed, idle chest, lifetime stats |
| **Stats** | Buy x1 / x10 / Max ATK levels |
| **Gear** | 6 slots, selected detail, Enhance (and "enhance weakest") |
| **Heroes** | Party of 6 (tap a slot to place the selected hero, or to unequip it), 54-hero inventory, auto-equip |
| **Gacha** | Pity bar, pull x1 / x10, result cards, rates with pity-consolidated L+/M |
| **Class** | Automatic advances at stages 50, 150, 300 and 500, with a progress bar |
| **Debug** | Editor, Development builds or `HEROCAT_CHEATS` only; compiled out of Release builds. Grant gold, gems or heroes; set stage; "Run N h" (real sim ticks); "Clock +N h" (offline earnings popup); x1/x5/x20 speed; save, validate, reset |

Two popups appear in a modal: offline earnings ("Welcome back") and class advance. Toasts cover gear drops, rejects,
quests and the day rollover.

## Layout

```
Assets/HeroCat.Client/
  HeroCat.Client.asmdef        Unity side (UnityEngine, UI Toolkit, uGUI EventSystem, Input System)
  Boot/      GameRoot.cs (bootstrap + MonoBehaviour), ContentLocator.cs, InputSetup.cs
  Editor/    ProjectSetup.cs (Tools > HeroCat menu: player settings, URP asset)
             BuildProfiles.cs (Tools > HeroCat > Build Profiles: Dev/Release defines, Release pre-build check)
             ContentPreBuild.cs (copies Content/ into Resources/Content before each build)
             Builds.cs (batch-mode entry points Builds.AndroidRelease / Builds.AndroidDev for CI)
  Art/       AppIcon.png (placeholder 1024x1024 icon)
  Screens/   Ui.cs (style kit), Shell.cs, HudAndStrip.cs, *Screen(s).cs, DebugScreen.cs
  Core/      HeroCat.Client.Core.asmdef (noEngineReferences: plain C#)
             GameViewModel.cs, ViewModels.cs, Feedback.cs, TextFmt.cs, SimRunnerCore.cs (10 Hz tick driver)
             SaveBridge.cs   <- the ONLY save/load/session code
Assets/Resources/HeroCatTheme.tss   imports Unity's default runtime theme (optional; styles are inline)
```

## Build profiles (Dev and Release)

`specs/07` section 13 wants a **Dev** and a **Release** Build Profile per platform. Dev defines `HEROCAT_CHEATS` and is a
Development Build; Release has neither, so its build contains no Debug tab and no cheat code (everything cheat-related is
under `#if UNITY_EDITOR || DEVELOPMENT_BUILD || HEROCAT_CHEATS`). Profile assets are Unity-generated, so create them once
in the editor and commit them:

1. **File > Build Profiles > Add Build Profile**, pick **iOS** (and/or **Android**). Rename it `iOS` (or `Android`).
2. Select that profile asset in the Project window and run **Tools > HeroCat > Build Profiles > Create Dev + Release From Selected**.
   This creates `Assets/Settings/BuildProfiles/iOS-Dev.asset` and `iOS-Release.asset`, adds `HEROCAT_CHEATS` to the Dev
   profile's Scripting Defines and keeps it out of the Release profile and the iOS/Android Player Settings defines. You can
   then delete the original `iOS` profile.
3. In **iOS-Dev**, tick **Development Build**, **Autoconnect Profiler** and **Script Debugging**. Leave them off in
   **iOS-Release**.
4. Commit `Assets/Settings/BuildProfiles/` and the `.meta` files.

**Tools > HeroCat > Build Profiles > Apply Cheat Defines** re-applies the defines after you rename or add profiles. A pre-build
check fails a `*-Release` build marked Development, and any non-development build that still defines `HEROCAT_CHEATS`.
To confirm a Release build has no Debug tab: activate `iOS-Release`, build, and check the tab bar ends at **Class**. Pressing
Play in the Editor always shows the Debug tab, whichever profile is active.


## Android release build (CI)

`.github/workflows/build-android.yml` (`specs/10` section 6.2) runs on every push to `release/**` and on manual dispatch.
It checks `Content/`, builds a signed IL2CPP ARM64 AAB with `game-ci/unity-builder` through
`HeroCat.Client.Editor.Builds.AndroidRelease`, fails if the AAB is over 150 MB (`SIZE_AAB_MB_MAX`), uploads it as an
artifact, and on `release/*` tags the commit `client-v<version>+<build>`.

**Version.** `version.json` in this folder holds the client version, semver `MAJOR.MINOR.PATCH` (`specs/10` section 6.3).
Bump it on the release branch: MINOR per store release, PATCH per hotfix, MAJOR for a save-incompatible change. The build
number is the workflow's `github.run_number`, which becomes the Android versionCode, so `1.2.0` in run 345 is `1.2.0+345`.
The build fails if game-ci's `-buildVersion` disagrees with `version.json`.

**Setup the repo owner does once** (Settings > Secrets and variables > Actions):

| Secret | What |
|---|---|
| `UNITY_LICENSE` | contents of the Unity `.ulf` licence file (Personal); or use `UNITY_SERIAL` for Pro/Plus |
| `UNITY_EMAIL`, `UNITY_PASSWORD` | the Unity account that owns the licence |
| `UNITY_SERIAL` | Pro/Plus serial, only when `UNITY_LICENSE` is not set |
| `ANDROID_KEYSTORE_BASE64` | the upload keystore, `base64 -w0 herocat-upload.keystore` |
| `ANDROID_KEYSTORE_PASS` | keystore password |
| `ANDROID_KEYALIAS_NAME`, `ANDROID_KEYALIAS_PASS` | key alias and its password |

Never commit the keystore (`*.keystore` and `*.jks` are git-ignored); keep the original and its passwords outside the
repo, since losing it locks you out of Play updates unless Play App Signing is on. Create one with
`keytool -genkeypair -v -keystore herocat-upload.keystore -alias herocat -keyalg RSA -keysize 2048 -validity 10000`.

**Runner.** A self-hosted Linux runner with Docker and the labels `self-hosted`, `linux`, `unity`. To use other labels,
set the repository variable `ANDROID_RUNNER` to a JSON array, for example `["ubuntu-latest"]` for a GitHub-hosted runner.
game-ci pulls the `unityci/editor` image for the version in `ProjectSettings/ProjectVersion.txt`, Android module included.
The optional variable `SIZE_AAB_MB_MAX` changes the budget; the dispatch input `size_max_mb` overrides it for one run
(set it below the AAB size to see the gate fail).

**Locally.** `Unity -batchmode -quit -projectPath unity/HeroCatClient -executeMethod HeroCat.Client.Editor.Builds.AndroidDev -logFile -`
builds a development APK with the debug keystore into `Builds/Android/`. `Builds.AndroidRelease` also needs
`-androidVersionCode N` and the keystore arguments (`-androidKeystoreName`, `-androidKeystorePass`, `-androidKeyaliasName`,
`-androidKeyaliasPass`, or the `ANDROID_*` environment variables of the same names). Both use the `Android-Dev` /
`Android-Release` Build Profile when it exists (see above), else the Player Settings. The minimum API level is whatever
Player Settings hold (currently 26, Android 8).

The version and size helpers (`.github/scripts/android_release.py`) are unit-tested by `./check.sh`; the Unity steps run
only in the workflow.

`Core/SaveBridge.cs` saves through `HeroCat.Runtime` (`specs/06`) to `<persistentDataPath>/saves/`: a CRC-checked
file with a `.prev` backup. Bad files are quarantined and a new profile is created. Offline gold since the last save
is credited on launch. Debug > Reset profile deletes the save. `EphemeralSaveBridge` (no persistence) is still there
for quick tests: return it from `SaveBridgeFactory.Create`.

## Not done (POC)

These are not built yet:

- Gacha card-flip reveal.
- Safe-area padding.
- Android back button.
- Perf overlay.
- PlayMode tests.
