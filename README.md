# HeroCat - prototype specs

A cat hero idle game. It is mobile first: the product is a Unity app for iOS and Android, and the web client in `web/` is the development and playtest build. It starts from the Idle RNG prototype (MonStack commit 28aaa59), including its Unity client in `unity/HeroCatClient` (open it in Unity 6.3 on a Mac; see its README); the game content is still the original hero gacha and is to be re-themed.

Mobile idle game in the Sword x Staff / Maple Idle mould: auto-combat pushes stages, stages drop currency, currency feeds parallel RNG upgrade systems, the player's job is deciding where to spend.

Start with `specs/00-overview.md`. It holds the canonical constants and the contracts every other spec follows.

| Spec | Covers |
|---|---|
| `specs/00-overview.md` | Scope (P0/P1/P2), canonical constants, vocabulary, cross-spec contracts |
| `specs/01-gacha-math.md` | Rates, pity, 10-pull guarantee, dupe economy, RNG commitment, test vectors |
| `specs/02-power-curve-math.md` | Enemy/gold curves, the four power layers, wall condition, bot results, tuning levers |
| `specs/03-sim-core-combat.md` | Pure-C# simulation assembly: tick loop, combat, RNG streams, public API |
| `specs/04-economy-sinks.md` | Currencies, stat levels, gear drops/enhancement, class advance, daily sources |
| `specs/05-content-data-schema.md` | Table schemas, sheet-to-JSON pipeline, validation, `ContentDb` API |
| `specs/06-save-and-offline.md` | Save schema v1, migrations, offline catch-up, clock seam, integrity |
| `specs/07-client-unity-prototype.md` | Unity client for iOS and Android: screens, view-models, layout, palette, event wiring, build settings (the web client follows the same screens) |
| `specs/08-backend-seams.md` | `IClock` / `IGachaService` / `ISaveStore` contracts, local impls, future remote |
| `specs/09-test-harness-and-bots.md` | Headless bot runner, acceptance table, determinism tests, CI |
| `specs/10-online-competitive.md` | Trust model for accounts, IAP, leaderboards and async PvP: server-verified progress, genesis seeds, command log, fairness levers |

Checks: `./check.sh` runs every check in the repo (`--fast` for the inner loop, `--no-dotnet` without the .NET SDK). Agent rules live in `CLAUDE.md`.

Tools:

- `tools/gacha_model.py` - exact pity distribution and consolidated rates. `python3 tools/gacha_model.py`
- `tools/power_curve_model.py` - greedy F2P bot, 30 days, prints the day table. `python3 tools/power_curve_model.py`

Both run on Python 3.7+ with no dependencies.

## Running the web prototype

The web prototype is the real C# game core (`src/HeroCat.Sim`, the code the bots and any future server use), compiled to WebAssembly by `src/HeroCat.Web` and played through the browser UI in `web/`. Everything runs on your own computer; no Claude session is needed.

### One-time setup

**1. Check what you already have:**

```
./play.sh --check
```

It lists each dependency as `ok` or `MISSING`, with the install command for your OS next to anything missing. Run it again after installing until it says "All set".

**2. Install anything missing.** You need four things; the fifth is optional:

| Dependency | Why | macOS | Windows (PowerShell) | Ubuntu / Debian |
|---|---|---|---|---|
| Python 3 | serves the page | `brew install python` | `winget install Python.Python.3.12` | `sudo apt install python3` |
| Node.js 18+ | packs the game content | `brew install node` | `winget install OpenJS.NodeJS.LTS` | `sudo apt install nodejs` |
| .NET 8 SDK | builds the C# engine | [installer](https://dotnet.microsoft.com/download/dotnet/8.0) | `winget install Microsoft.DotNet.SDK.8` | `sudo apt install dotnet-sdk-8.0` |
| WebAssembly tools | compile C# for the browser | `sudo dotnet workload install wasm-tools` | `dotnet workload install wasm-tools` (as administrator) | `sudo dotnet workload install wasm-tools` |
| qrencode (optional) | QR code for the phone link | `brew install qrencode` | not needed | `sudo apt install qrencode` |

Install them top to bottom: the WebAssembly tools need the .NET SDK first. On macOS, `brew` is [Homebrew](https://brew.sh); the official installers from python.org and nodejs.org work too. Ubuntu's `nodejs` package must be version 18 or newer (`node --version`); older releases need the installer from nodejs.org. On Windows, run `./play.sh` from Git Bash (comes with [Git for Windows](https://git-scm.com/download/win)) or WSL; open a new terminal after installing so it sees the new tools.

### Play

From the repo root:

```
./play.sh
```

It checks the prerequisites (and tells you the command to fix anything missing), builds the C# engine only when `src/` or `Content/` changed since the last build (the first build takes 1-3 minutes), starts a local server, prints the link for this computer and for your phone, and opens your browser. Stop it with Ctrl+C. Settings in the game (the cog, top right) shows "Engine: C# (wasm)" and the content version.

Options: `--rebuild` forces a build, `--port 8080` picks another port (it moves to the next free one if the port is taken), `--no-open` skips opening a browser, `--local` refuses connections from other devices. If `qrencode` is installed it also prints a QR code for the phone link.

By hand, the same thing is `web/wasm/build.sh` then `cd web && python3 -m http.server 8000`.

It must be served over HTTP: browsers refuse to load WebAssembly from a `file://` page, so opening `index.html` directly shows an error telling you to start the server. On Windows, run `build.sh` from Git Bash or WSL.

If the page says "The C# engine did not load", it names the cause: the bundle is not built (run `./play.sh --rebuild`), the page was opened as a file, or loading failed (rerun the build and read its output; the usual cause is the missing `wasm-tools` workload).

### On a phone

The repo is private, so GitHub cannot serve the page (raw links show source, and GitHub Pages on a private repo needs a paid plan and publishes the site publicly). Serve it from your computer:

1. Run `./play.sh` on the computer. It prints an "On your phone" link such as `http://192.168.1.20:8000`.
2. Open that link on the phone, on the same Wi-Fi. If it does not load, allow Python through the computer's firewall.
3. Optional: Add to Home Screen from the browser's share menu for a full-screen icon.

Progress is saved in the browser, separately for each address. Fonts load from Google Fonts; offline the page falls back to system fonts and still plays.

### Tests for the web build

- `node web/wasm/test/run-wasm.js` (after `web/wasm/build.sh`): plays the page on the C# engine in headless Chromium: ticks, tabs, a summon, save and reload, and the file:// error.
- Game rules are tested in C# (`src/HeroCat.Sim.Tests`); `./check.sh` runs everything, including the web build and browser test in full mode.
