# Spike: Pick Me Up! Infinite Gacha (issue #330)

Status: **first pass**, 2026-11-07. Research notes, not a spec. Nothing here changes a spec or a constant; every adoption below needs an owner decision first (CLAUDE.md rule 3).

We borrow mechanics and ideas only. No names, characters, story, art or text from the work go into HeroCat.

## 1. What the work is

- *Pick Me Up!* (픽 미 업!), Korean web novel by Hermod (헤르모드), finished in 2018 at about 327 chapters. Webtoon adaptation on KakaoPage / Kakao Webtoon and Tapas (REDICE Studio). Kakao Entertainment named it one of its top titles for 2026.
- Premise: Han Seojin, the world's #5 player ("Loki") of a gacha game called *Pick Me Up*, wakes up inside it as a level 1, 1-star hero. To get home he has to clear the game's 100-floor dungeon while coaching a novice master. As a player he used 1-star heroes as disposable scouts for boss patterns.

**No official game adaptation exists** as of November 2026: no announcement, pre-registration or store listing in Korean or English games press or the app stores. The only game is *Infinite Gacha* by Moebius Order, an unlicensed fan project on Patreon (its own pages say it is not an official adaptation). It has no build, no published rates and no reviews, so it gives us nothing to study yet.

The mechanics below are therefore the **in-story game's** rules, as fan wikis and reader spoiler threads describe them. That is fiction: nobody has tuned or monetised them.

## 2. Mechanics catalog (in-story game)

| # | Mechanic | What the sources say | Confidence |
|---|---|---|---|
| M1 | Two summon tiers | Free (gold) summons give 1-3 star heroes; paid (gem) summons give 3-5 star. No rates or pity are stated anywhere. | Fan wiki + novel summary |
| M2 | Account-dependent rates | Summon odds differ per account; the story uses it as a source of paranoia about rigged rates. | Fan wiki |
| M3 | Seven star grades, level cap per grade | 1-7 stars. Caps 1★ 10, 2★ 20, 3★ 40, 4★ 60, 5★ 80, 6★ 99, 7★ 100+ (sources disagree about the top). A hero at cap stops levelling until promoted. | Fan wiki; novel thread disagrees on the top |
| M4 | Promotion with materials, not dupes | In a Promotion Chamber the master spends attribute stones and rare materials (dropped by rare monsters on expeditions) to raise a hero one star. Grade-specific items exist for the top steps. Any hero, even a 1★, can in principle reach 7★. | Fan wiki + reader threads |
| M5 | Hidden talents | A 1★ hero's talents are hidden; training reveals them, and the same training gives different results for different heroes. | Fan wiki |
| M6 | Traits | Heroes have mentality, skill and physical traits; a bad trait (prone to panic) makes a hero a poor fit for some roles. | Fan wiki |
| M7 | Skills learned in play | Heroes learn skills by fighting, from training, from shop items, and as unique drops from specific bosses. | Fan wiki |
| M8 | Summon-only classes | Some classes only come from summons, never from raising 1★ heroes. | Fan wiki |
| M9 | 100-floor tower as the spine | Clearing the 100-floor dungeon is the game's goal. | Official premise |
| M10 | Five-floor cadence | Difficulty jumps every 5 floors, with boss missions on 5/10/15/20; event floors break the pattern. | Fan wiki |
| M11 | Puzzle and event floors | Some floors need items found in order or objects seized before the fight. | Fan wiki |
| M12 | Permadeath | A dead hero is deleted and cannot be revived. Reviewers call this the main source of tension. | Fan wiki + Korean reviews |
| M13 | The underdog | The core fantasy is a 1★ outgrowing high-star heroes through growth and smart play. | Premise |

Gaps not found in any reachable source: dupe handling, equipment, master rank formula, PvP, guilds, bonds/affinity, formations and synergy rules.

## 3. Mapping onto HeroCat

Checked against `specs/00` section 1 (pillars), `specs/03` sections 4 and 5.3, `specs/04` sections 3, 5 and 8, and `specs/06` section 6.6.

| # | Where it would live | Fit | Notes |
|---|---|---|---|
| M1 | 04 banners | **Reject** paid-only rarity | A rarity only paid pulls can reach breaks pillar 4 (free players reach every system). Our banners already have one published rate table for both diamonds and tickets. |
| M2 | 04 section 3 | **Reject** | The opposite of design goal 1 (published odds are the odds) and pillar 4; regulated in KR, JP and CN. Useful only as a reminder of what players fear. |
| M3 + M4 | 04 section 5 (new companion **Promotion**), 03 | **Adapt, needs decision** | Our nearest thing is limit breaks (Rock Candy, +1 effective level, max 5). Promotion would be a material-paid step that raises a companion's rarity-driven `dps_pct` tier, so a Common favourite can stay in the party. Fits M13 and pillar 2. Large balance impact: it competes with the companion banner for the same job, adds a material and a sink, and must go through `power_curve_model.py`. P1. |
| M5 | 04 section 8 (companion rows) | **Adapt, P1/P2** | Each companion gets a second, smaller effect that is shown as locked and revealed at a set level or limit-break rank. Must be fixed per companion and shown in the detail screen after reveal; no hidden RNG roll on the player's copy, so odds stay honest and the sim stays deterministic. |
| M6 | 04 section 8.1 role tags | **Defer, P2** | Role tags are presentation only by rule; giving them effects is a new decision. Possible later as party-composition bonuses (for example, two Clerics). |
| M7 | 06 Tower rewards, 04 skills | **Adapt, P1** | Tower milestone floors could grant a copy of a specific skill or companion as a first-clear reward. It must also be in its banner pool, so nothing becomes strictly required, and `OWN_*` targets stay below pool size (pillar 4, issue #44). |
| M8 | 04 | Already true | Every companion comes from the summon or the tutorial grant. |
| M9 + M10 | 06 section 6.6 (P1 Tower) | **Adopt the cadence, P1** | We already plan a 100-floor Tower. Its draft rewards come every 5th and 10th floor, but its difficulty follows the stage curve smoothly (floor `f` is the stage-`10f` boss). A step every 5 floors with a stronger boss on the step floor gives the ladder visible milestones. Pure content tuning. |
| M11 | 06 Tower / Dungeons | **Adapt lightly, P1** | Idle-safe variants only: rule floors such as "only `BUFF_ATK` abilities fire" or "double MP cost". No floor that needs input mid-fight (pillar 1). |
| M12 | 03 section 4 | **Reject** | The party cannot die, by decision (00 decisions 10, 109); deleting a summoned companion would also destroy paid value. The stakes can come from the P1 Tower survival HP pool already in 03 section 4, where losing is a retreat. |
| M13 | 00 section 1, 07 onboarding, art | **Adopt as framing** | The starter party is five Commons (04 section 8.3) and the squire is the player's avatar (decision 104). The squire as a coach raising underdogs is a free narrative hook for onboarding and store copy. Works best with M3/M4. |

## 4. Recommendations, ranked

1. **Tower five-floor cadence (M9, M10).** Low cost and low risk, content only, P1. Fold into the Tower design when it is detailed.
2. **Underdog framing (M13).** No sim cost. Onboarding and marketing copy: "raise your Commons".
3. **Companion Promotion (M3, M4).** The most valuable idea and the biggest balance call. Needs an owner decision on whether low rarities should reach endgame, then a model run.
4. **Revealed second trait (M5).** Adds a collection goal and a reason to level Commons. P1/P2, after Promotion is decided.
5. **Tower first-clear unit rewards (M7)** and **rule floors (M11).** P1 Tower content.

Rejected: paid-only rarity bands (M1), account-dependent rates (M2), permadeath (M12).

## 5. Next steps for the spike

- **Second pass on sources.** This pass used search-result snippets only: the session's network blocked namu.wiki, the two Fandom wikis and Novel Updates. A session with those hosts allowed should read the en.namu.wiki 설정 (Settings) page and the fan wiki Heroes and Tower pages in full, and fill the gaps in section 2.
- **Owner decisions.** Promotion (item 3) and its knock-on to the trait reveal (item 4) are design and balance calls for the owner. Follow-up issues get filed once the owner says which items to pursue.

## Sources

Reliability notes in brackets.

- Kakao Webtoon, 픽 미 업!: https://webtoon.kakao.com/content/픽-미-업/3205 [official; premise only]
- MyAnimeList, Pick Me Up: https://myanimelist.net/manga/154587/Pick_Me_Up [premise and credits]
- Namuwiki, 픽 미 업!: https://namu.wiki/w/%ED%94%BD%20%EB%AF%B8%20%EC%97%85! [best fan source; snippets only]
- Pick Me Up fan wiki: https://pick-me-up.fandom.com/wiki/Pick_Me_Up [fan-written; snippets only]
- Infinite Gacha fan wiki, Pick Me Up! pages (Heroes, Tower): https://infinite-gacha.fandom.com/wiki/Pick_Me_Up! [fan-written, uneven; snippets only]
- Digital Daily, Kakao Entertainment 2026 line-up (Jan 2026): https://www.ddaily.co.kr/page/view/2026010910484703336 [news]
- Webtoon reviews: https://v.daum.net/v/20260228060306596, https://www.fnnews.com/news/202510101533107176 [news; no game mentioned]
- Moebius Order, Infinite Gacha (fan game): https://patreon.com/moebiusorder, https://infinitegacha.netlify.app/about [the project's own pages; unlicensed]
