# Whisker Hero (working title) - design specs

Mobile idle RPG in the *Cat Hero: Idle RPG* mould. A cat hero auto-fights through stages on the top half of a portrait screen while the player merges fish on the bottom half; kills, fish, dungeons and summons feed a dozen parallel upgrade systems, and the game is deciding where to spend next. Deterministic pure-C# simulation shared by the Unity client, the ASP.NET Core server and a headless bot harness; every number is content data tuned from a spreadsheet and verified by bots before a human sees it.

Start with `specs/00-overview.md`. It fixes the tech stack, the authority split, the canonical names, every constant with its inherited/seed tag, the power formula, the cross-spec contracts and the decisions log. Where another spec disagrees with it, 00 wins.

| Spec | Covers |
|---|---|
| `specs/00-overview.md` | Scope tiers (M0/MVP/P1/P2), pitch and pillars, tech stack, architecture, vocabulary, constants, power formula, contracts, milestones, decisions |
| `specs/01-architecture-and-data-flow.md` | Layers, commands/events, every end-to-end flow, threading, network |
| `specs/02-domain-model-and-persistence.md` | Entities, save schema v1, migrations, local + cloud save, server DB |
| `specs/03-combat-and-progression.md` | Combat loop, stats, training, hero level, awakening, research, power curve |
| `specs/04-summons-and-collections.md` | Banners, pity, banner level, weapons, skills, companions, runes |
| `specs/05-fish-merge-minigame.md` | Board, tank, merge rules, discovery, missiles, auto-merge |
| `specs/06-economy-and-daily-loop.md` | Currencies, daily sources/sinks, quests, dungeons, offline, shop, inbox |
| `specs/07-screens-and-ux.md` | Navigation map, every screen, states, UX and design rules, Unity client setup and device builds |
| `specs/08-backend-and-live-ops.md` | Services, API contracts, auth, IAP, anti-cheat, config, compliance |
| `specs/09-content-data-pipeline.md` | Table schemas, exporter, validation, `ContentDb`, localization |
| `specs/10-testing-release-and-ops.md` | Test pyramid, bots, CI/CD, store release, KPIs, runbooks, roadmap |

Tools (Python 3.9+, stdlib only unless noted):

| Tool | Status | Does |
|---|---|---|
| `tools/power_curve_model.py` | in repo (spec 03 section 8.6, issue #87); `--check` runs in full `check.sh`, report-only until the `S` bands pass | Greedy F2P bot across all power layers; prints the day table, layer shares and 06's economy tables (issue #178); validates the `S` constants 03 and 06 list |
| `tools/gacha_model.py` | in repo (spec 04 section 14); `--check` and `--cadence` run by `check.sh` (hard gates; two `--cadence` checks report-only, issue #164) | Exact pity distribution and consolidated rates per banner level, collection fill curve, F2P pull cadence per banner and the 04 section 14 `S`-constant checks; the oracle for the server summon test |
| `tools/fish_model.py` | in repo (spec 05 section 9); run by `check.sh` (hard gate) | Tank and merge throughput, Fish Coin and DPS income per active minute for attentive and lazy policies; `--dungeon-curves` reports the Fish Coin Dungeon share per candidate reward curve (issue #165) |
| `tools/export_content.py` | `build` / `check` / `gen-dtos` / `diff` / `pull`, table rules, versioning, localization checks and the translator verbs `translator-csv` / `import-translations` / `pseudo` landed (spec 09, issues #121-#123, #128, #132, #213) | Sheet -> `content_src/*.csv` -> validated `content/*.json` + `manifest.json` + the Sim's generated DTOs; translator CSV round trip; pseudo-localization |
| `tools/content.py` | in repo (spec 09 section 11, issue #130); `power_curve_model.py` reads through it | Loader the three models use so they read the same JSON the game ships |

Milestones (spec 00 section 11):

- **M0 vertical slice**: sim core, local weapon/skill summons, fish board, offline catch-up, local save, seven placeholder-art screens; bots print the day table and two simulated weeks land within tolerance; one person plays 20 minutes on device unprompted.
- **MVP**: everything tagged MVP, backend (auth, cloud save, server summons, IAP, inbox, config), analytics, tutorial, EN shipped (with `ja` translated in the pipeline); closed beta of 50 with D7 measured and zero save-loss reports.
- **Soft launch**: store checklist complete, staged rollout in two small markets; D1 >= 40%, D7 >= 15%, crash-free >= 99.5%, no P0 economy exploit.
- **P1**: runes, research expansion, tower, guild, rewarded ads, events, leaderboards, coupons, auto merge; scheduled by soft-launch data.
- **P2**: siege, raid, costumes; not scheduled.

Sibling project: `idle-rng-game` (a separate repo) is the earlier, narrower prototype whose validated constants and patterns (RNG streams, save migrations, bot harness, content pipeline) this project reuses under new names.
