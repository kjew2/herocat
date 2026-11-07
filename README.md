# Hundred Floor Summoner (working title) - design specs

> **Rewrite in progress (epic #353):** this repo is being rewritten in place from HeroCat into a new game (`specs/00` decisions 121-122). `specs/00` and `specs/11` describe the new game; specs 01-10 and the code still describe HeroCat until each is replaced under its own issue (`specs/00` section 10).

Idle gacha RPG for iOS and Android. The player is a newly licensed summoner who calls heroes from former ordinary lives to climb a hundred-floor tower. Heroes have star grades from 1 to 7, former lives, moods and stress, and they stay dead until a revive item brings them back. The player builds parties of five, assigns them to floors and lets the fights run, then levels, promotes, equips and synthesizes heroes in the lobby. Mechanics come from the in-story game of the *Pick Me Up!* webtoon (`docs/research/`); the world, names, characters, story and art are our own. Deterministic pure-C# simulation shared by the Unity client, the ASP.NET Core server and a headless bot harness; every number is content data checked by bots before a person sees it.

Start with `specs/00-overview.md`. It fixes the pitch and pillars, scope tiers, tech stack, authority split, canonical names, constants, contracts and the decisions log. Where another spec disagrees with it, 00 wins.

| Spec | Covers | Status |
|---|---|---|
| `specs/00-overview.md` | Pitch and pillars, scope tiers, tech stack, architecture, vocabulary, constants, contracts, milestones, decisions | New game |
| `specs/01-architecture-and-data-flow.md` | Layers, commands/events, every end-to-end flow, threading, network | HeroCat; rewrite #388 |
| `specs/02-domain-model-and-persistence.md` | Entities, save schema v1, migrations, local + cloud save, server DB | HeroCat; rewrite #384 |
| `specs/03-heroes-and-growth.md` | Hero sheet, stats, classes and roles, former lives and traits, statuses, inner life, the stat pipeline; levels, synthesis, promotion, skills and class change to come | New game (hero model); growth pending #344 |
| `specs/04-summons-and-collections.md` | Becomes `04-summons-and-gear.md`: hero and gear banners, odds, pity, gear, crafting | HeroCat; rewrite #343, #345 |
| `specs/05-fish-merge-minigame.md` | Becomes `05-tower-and-battle.md`: floors, auto-battle, missions, death and revival, side content | HeroCat (retired system); rewrite #346 |
| `specs/06-economy-and-daily-loop.md` | Becomes `06-lobby-and-meta.md`: lobby, facilities, manager, research, parties, account level, quests, shop, idle time | HeroCat; rewrite #347 |
| `specs/07-screens-and-ux.md` | Navigation map, every screen, states, UX and design rules, Unity client setup and device builds | HeroCat; rewrite #385 |
| `specs/08-backend-and-live-ops.md` | Services, API contracts, auth, IAP, anti-cheat, config, compliance | HeroCat; update #386 |
| `specs/09-content-data-pipeline.md` | Table schemas, exporter, validation, `ContentDb`, localization | HeroCat; update #387 |
| `specs/10-testing-release-and-ops.md` | Test pyramid, bots, CI/CD, store release, KPIs, runbooks, roadmap | HeroCat; update #388 |
| `specs/11-source-divergences.md` | Every rule where the new game deliberately differs from the source mechanics in `docs/research/` | New game |
| `specs/archive/00-overview-herocat.md` | HeroCat's overview, kept so existing citations resolve; not a spec | Archived |
| `specs/archive/03-combat-and-progression-herocat.md` | HeroCat's combat and progression spec, kept so "03 section N" citations in code and tools resolve; not a spec | Archived |

Tools (Python 3.9+, stdlib only unless noted):

| Tool | Status | Does |
|---|---|---|
| `tools/power_curve_model.py` | in repo (archived spec 03 section 8.6, issue #87); `--check` runs in full `check.sh`, report-only until the `S` bands pass | Greedy F2P bot across all power layers; prints the day table, layer shares and 06's economy tables (issue #178); validates the `S` constants 03 and 06 list |
| `tools/gacha_model.py` | in repo (spec 04 section 14); `--check` and `--cadence` run by `check.sh` (hard gates; two `--cadence` checks report-only, issue #164) | Exact pity distribution and consolidated rates per banner level, collection fill curve, F2P pull cadence per banner and the 04 section 14 `S`-constant checks; the oracle for the server summon test |
| `tools/fish_model.py` | in repo (spec 05 section 9); run by `check.sh` (hard gate) | Tank and merge throughput, Fish Coin and DPS income per active minute for attentive and lazy policies; `--dungeon-curves` reports the Fish Coin Dungeon share per candidate reward curve (issue #165) |
| `tools/export_content.py` | `build` / `check` / `gen-dtos` / `diff` / `pull`, table rules, versioning, localization checks and the translator verbs `translator-csv` / `import-translations` / `pseudo` landed (spec 09, issues #121-#123, #128, #132, #213) | `content_src/*.csv` (edited directly, #278; optional one-off Sheet `pull`) -> validated `content/*.json` + `manifest.json` + the Sim's generated DTOs; translator CSV round trip; pseudo-localization |
| `tools/content.py` | in repo (spec 09 section 11, issue #130); `power_curve_model.py` reads through it | Loader the three models use so they read the same JSON the game ships |

Milestones (spec 00 section 11):

- **M0 vertical slice** (#352): local hero summon, one party of five, tower floors 1-10 with the floor-5 step and a floor-10 boss, levels and promotion, death and revival, offline catch-up, local save.
- **MVP**: everything tagged MVP, backend (auth, cloud save, server summons, IAP, inbox, config), analytics, tutorial, EN shipped (with `ja` translated in the pipeline); closed beta of 50 with D7 measured and zero save-loss reports.
- **Soft launch**: store checklist complete, staged rollout in two small markets; D1 >= 40%, D7 >= 15%, crash-free >= 99.5%, no P0 economy exploit.
- **P1**: crafting, adventure dungeon, formation board, gifts, memorials, rankings, events, rewarded ads, coupons; scheduled by soft-launch data.
- **P2**: the multi-master layer, costumes; not scheduled.

Sibling project: `idle-rng-game` (a separate repo) is the earlier, narrower prototype whose validated constants and patterns (RNG streams, save migrations, bot harness, content pipeline) this project reuses under new names.
