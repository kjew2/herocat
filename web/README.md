# Browser prototype

A playable, phone-sized UI for feel and balance testing, running the **real C# game core** (`src/HeroCat.Sim`) compiled to WebAssembly. This web client is the app: the store builds will wrap it (`specs/00` section 7, decision 10).

To play: `./play.sh` from the repo root (details in the root README, "Running the web prototype").

| File | What it is |
|---|---|
| `index.html`, `app.js` | UI shell: ticks the engine, sends commands, reads queries, drains events. No game rules. |
| `engine.js` | Facade over the C# engine (`HeroCat.Web.Engine`): caches state per tick batch, memoises queries, wraps saves. |
| `format.js` | Big-number display (`1.23K`, `45.6M`, `123aa`); same output as the C# `BigNumber.Short`. |
| `sprites.js` | Seeded 12x12 pixel sprites for heroes, player and enemies. Cosmetic only, never touches the game's RNG. |
| `wasm/build.sh` | Builds `src/HeroCat.Web` and lays out `wasm/dist/` (gitignored). |
| `wasm/pack-content.js` | Bundles `Content/*.json` into the content pack the engine loads and hash-checks. |
| `wasm/BRIDGE.md` | The JS <-> C# contract: exports, JSON shapes, events. |
| `wasm/test/run-wasm.js` | Headless-Chromium end-to-end test on the built bundle. |

## Where things come from

- **Rules and numbers**: the C# core and `Content/*.json` (built from `content_src/` by `tools/content_build.py`). Change a number in `content_src/`, rebuild content and the bundle, reload.
- **Labels and display numbers** (hero names, slot names, pity limits, costs shown on buttons): `Engine.UiContent()`, read from the same `ContentDb`. The UI never evaluates a game formula itself.
- **Saves**: the C# save format (`HeroCat.Runtime` `SaveSerializer`) inside a small JSON envelope, in `localStorage['herocat-wasm-save-v1']`. Saves from the old JS engine are rejected with a message.

The cog opens playtest tools: speed x1/x5/x20/x100 (moves the game clock too, so chests, offline time and daily resets come faster), clock skips (+1 h, +8 h, +1 day), gold and gem grants, stage jump, save export/import, and reset.

## Known limits

- **Permanently unranked.** It creates its own random seeds, so it can never be certified for leaderboards or PvP (`specs/10` section 3). That is right for a playtest build; a ranked browser client needs a server genesis.
- **Needs HTTP.** WebAssembly does not load from `file://`, and the claude.ai artifact page cannot host the .NET runtime, so there is no install-free way to play.
- The core has the command log, `StateHash` and `Replay` (`specs/03` section 22, `specs/10` section 6), but the bridge does not expose them yet.
