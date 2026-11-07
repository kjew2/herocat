# Spike: Pick Me Up! Infinite Gacha mechanics catalog (issue #330)

Status: **catalog complete for webtoon chapters 1-93**, 2026-11-07. Research notes, not a spec. Epic: #353 (pivot). Spec issues this feeds: #342 hero model, #343 summoning, #344 growth, #345 equipment, #346 tower, floors, missions and stakes, #347 master meta. Decisions it informs: #339 core loop, #340 monetization, #354 premise.

**Mechanics only** (`specs/00` decision 121, #337 = B). This doc describes rules, systems and numbers. It names no characters, places, story events or named items from the work: those are replaced by generic terms ("the protagonist", "a mage hero", "the 7-star book"). Nothing here may go into the game as text or art.

The first pass of this spike (merged in #333) mapped a few mechanics onto HeroCat. That mapping is superseded by the pivot (decision 122) and is dropped here; see git history for it.

## 1. Sources and method

- **The work:** *Pick Me Up!* (픽 미 업!), Korean web novel by Hermod, finished 2018; webtoon by REDICE Studio on KakaoPage / Kakao Webtoon, English on Tapas. **No official game adaptation exists** (Nov 2026); the only game is an unlicensed fan project with no build or rates.
- **Webtoon chapters 1-93**, read through an OCR pass of every page (an English fan scanlation; about 1,200 page strips) and then read chapter by chapter for every system window, rule and number. OCR noise is marked "partial" where a reading is uncertain. Panels without text (fight scenes) were not read.
- **Fan wikis and reader threads**, from search snippets only (first pass). Used where the webtoon is silent; marked "wiki".
- Confidence: **clear** = a system window or explicit dialogue states it; **partial** = OCR or context is unclear; **inferred** = shown by events, never stated.

The in-story game is fiction. Nobody has tuned or monetised it, its numbers are story beats, and some rules exist only to serve the plot. Section 9 lists the ones that look plot-only.

## 2. The game at a glance

A player ("master") runs a lobby of summoned heroes and sends them up a 100-floor tower in parties of five. The heroes are real people pulled from another world who keep their memories, jobs and personalities; the master thinks they are game AI. Combat is automatic on hero AI: the master's job is summoning, synthesis (feeding heroes to each other), promotion, gear, facilities, party building and keeping the lobby from rebelling. Death is permanent. The best player in the world reached floor 88 in two years; floor 80 has been cleared by five accounts out of about a hundred million.

The loop, as the story shows it:

1. Summon heroes (gold for low grades, gems for high grades) and equipment.
2. Sort them: keep the useful, feed the rest to synthesis.
3. Train, gear and promote; build and level lobby facilities.
4. Send parties through the gate to the current tower floor, a replayed floor, a weekday dungeon or a 48-hour exploration.
5. Collect EXP, materials, gold and drops; manage morale, stress, relationships and refusals; repeat.

## 3. Heroes (#342)

| Mechanic | How it works | Ch | Conf |
|---|---|---|---|
| Star grades 1-7 | Every hero has a grade from 1★ to 7★. 1★ heroes are treated as disposable: lowest stats, no class, panic in battle. 3★ and up fight from day one. A top player never pulled above 5★ in thousands of paid pulls; 6★ and 7★ come from promotion. | 1-4, 15-16 | clear |
| Real people with former lives | Heroes are people taken from ordinary lives (carpenter, farmer, street vendor, mercenary, hunter, forest keeper, merchant, guard). Job and personality carry over: a hunter is good with a bow, artisans can craft, a guard is dutiful, liars and slackers drag a lobby down. | 1-3, 8, 15, 19-20, 44, 47 | clear |
| Stat window | Name with stars, level, EXP current/max, class, stats as current/max (Strength, Intelligence, HP or Stamina, Agility or Dexterity) and skills with a level each. Example sheet: Lv15, EXP 38/120, seven skills at Lv4-7. | 1-3, 13, 15, 20-21, 38, 44, 46 | clear |
| Sample stats | 1★ start: STR 14, INT 10, HP 12. 4★ knight: STR 19, INT 10, HP 18, AGI 22. 3★ mage: STR 7, INT 36. 3★ thief: STR 13, INT 10. | 1-2, 15, 21 | clear |
| Class | Low grades start as "Novice". Higher grades arrive with a class (Knight 4★, Thief 3★, Magician 3★). Warrior and thief classes can be grown from 1★; magician can only be pulled, at a very low rate. | 1-3, 15, 20 | clear |
| Growth rate | Each hero gains a fixed number of stat points per level (about 4 for an ordinary 1★, 5 for a normal 4★). Points can be redistributed, so one stat can fall on level-up. Some 1★ heroes hide a high growth rate ("potential"). | 3-4, 13, 18 | clear |
| Hidden talents | High grades arrive with their skills. A low grade's talents are hidden until the master uses the hero. An in-game tip says to try a 1★ before synthesizing it. | 3, 16 | clear |
| Heroes can't see the system | By default heroes cannot see their own stat windows or the mission objective (a party died because it did not know the objective). Research ("Hero Reaction") lets them see part of their own window. | 43 | clear |
| Status: Fear | Unprepared heroes start combat afraid: all stats -30%. Can wear off mid-fight; whole parties can be afraid at once. | 1-3, 7, 10, 27 | clear |
| Status: Panic, Despair | Panic: all stats -50%. Despair: all stats -80%. | 33, 39-40 | clear |
| Status: Frenzy | A hostile pair in one party can fall into Frenzy mid-mission; the party wiped. Numbers not shown. | 39-40 | clear / effect inferred |
| Status: Poison | HP falls over time. Potions slow it but do not cure it (one every ~10 minutes); an antidote is needed. About 10 minutes of useful movement left. | 49-50 | clear |
| Status: Bleeding, brink of death | Bleeding drains HP over time. "On the brink of death" is a warning shown before death. | 10-12 | clear |
| Berserk vs Composure | Berserk raises combat power but removes rational control, lowers skill efficiency, and leaves fatigue and a headache. Composure keeps a hero rational; the two normally cannot coexist. | 12-13, 17, 22, 44 | clear |
| Status alerts | Status messages reach the master and the party leader even when parties are split. | 41, 52 | clear |
| Stress | Extreme stress can kill a hero outright (a death notice gives "stress" as the cause). Synthesis and fighting human-shaped enemies raise it; food variety and rest lower it. No visible meter. | 1, 17-18, 39, 46 | partial |
| Morale | Synthesis lowers morale; gifts and facility upgrades raise it. A lobby run on fear loses hope. | 36-37, 39 | clear, no numbers |
| Loyalty | Heroes can swear real loyalty. How a hero sees the master matters more than skill. | 36-37, 39 | clear, no numbers |
| Intimacy bonus | When one hero feels goodwill toward another, the pair gets stat bonuses in the same party. | 3 | clear |
| Pair synergy and anti-synergy | Some pairs gain stats together, others lower each other's. Hostility can form the moment a hero is summoned. | 39 | clear |
| Hostility | Hostility forms between a hero and a group; the game warns the master to watch relationships. Nasty personalities lower lobby efficiency. | 16 | clear |
| Mage casting | Long casts (about a minute) leave a mage helpless, so the party guards them. Training cuts cast time (40 s to 10 s). Mana can "overflow" past a limit. Spell power comes in circles. Some mages can cast on the move. | 21-23, 27, 31, 42 | partial |

## 4. Summoning and monetization (#343, #340)

| Mechanic | How it works | Ch | Conf |
|---|---|---|---|
| Two hero summons | Normal summon costs gold and gives low grades (wiki: 1-3★). High-Rank (advanced) summon costs gems and gives better heroes (wiki: 3-5★); the game is built to push players to it. Both come as 1x or 10x consecutive pulls; a 3x was also used. | 2-3, 7, 16, 39, 58 | clear |
| High-Rank price | 500 gems a pull; 3 pulls for 1,500 gems. | 19-20, 39 | clear |
| Equipment summon | 10x for 50,000 gold. Example results: E+ sword, E- bow, D+ morningstar. | 1 | clear |
| Free and event pulls | Tutorial: first summon free plus 500 gems. A free 10-pull recurs every few days. A launch event gave 100 gems and a summon card for logging in. Missions give no gems. | 2, 7-8 | partial |
| Affinity pulls | A consecutive summon has a very low chance of pulling heroes who already know each other (a band of five mercenaries). They are stronger in the same party. | 15 | clear |
| Gacha "conditions" | Players say paying for single pulls "without fulfilling the conditions" wastes gems, and that consecutive pulls are better. Hints at hidden pull conditions or a guarantee; never explained. | 38, 40 | partial |
| Per-account rates | Summon probabilities and floor difficulty differ per account. One account's world ran out of high-grade heroes, so it could not pull a 5★. The "infinite" hero pool turns out to be about a million real people. | 6, 36 | clear (lore) |
| Paid packages | Starter package 50,000 won; another 65,000 won; 5,000 gems + 100,000 gold for 90,000 won; 5,000 gems + 3 gift cards for 90,000 won. Billed to phone or app store, delivered to the mailbox. | 7, 15, 19-20, 40 | clear |
| Free vs paid heroes separated | After a lodging rework, free-summon heroes live on lodging floor 1 and paid ones on floor 2. | 58 | clear |
| Gem sinks besides summons | Annex buildings (300 gems for a forge), mid-battle skill books (500 gems), replay recording (300 gems), research hours (10 gems/hour). | 5, 30, 42, 46 | clear |

## 5. Growth (#344)

| Mechanic | How it works | Ch | Conf |
|---|---|---|---|
| Levels and EXP | Stage clears give EXP. EXP to next level: 10 at Lv1, 20 at Lv2, 50 at Lv5-6, 70 at Lv9, 120 at Lv15. Heroes who did not fight get none. | 2-4, 13, 20, 46 | clear |
| Level cap per grade | 1★ caps at Lv10; at the cap the game says the hero must be promoted. Wiki: 2★ 20, 3★ 40, 4★ 60, 5★ 80, 6★ 99, 7★ 100+; the story also says "99, except for 7-stars". | 13, 17, 25, 32 | clear (1★) / wiki |
| Slower levelling for high starters | Heroes that start at a high grade level more slowly. | 46 | clear |
| Promotion | Raises a hero one star. Needs a Promotion Stone, or a hero of the same grade at a set level as material. Goes up to 7★; the last step needs a rare book that drops in dungeons. | 2, 13, 17 | clear |
| Promotion Chamber | A building attached to the Synthesis Chamber. A black box: the hero goes in with the materials and comes out promoted later. | 34 | clear |
| Promotion Stone recipe | Basic Promotion Stone = fire + water + wind elemental stones, automatic synthesis, 87% success; ingredients are lost on failure. | 34, 44 | clear |
| Promotion restores memories | Promotion (seen at 1★ to 2★) gives back forgotten memories of the hero's former life, shown as a dream; the hero's behaviour changes. | 33, 44 | clear |
| Synthesis | Drag heroes onto a target in the Synthesis Chamber. The sacrifices die ("became light") and the target gains levels, stats and sometimes a skill. Many at once (six in one go). Typical masters synthesize 3-4 times a day and use it to cull and to rule by fear. Heroes can refuse dungeons but not synthesis. | 2, 4-5, 9, 16, 18-20, 36, 39, 57-58 | clear |
| Training | Training raises no stats or levels. It gives skill EXP and unlocks new skills. A training hall at Lv2 trains faster. | 3-4, 7 | clear |
| Skill levels and plateau | Skills level from training and combat. Past skill Lv6, more training needs an equal-level sparring partner or self-imposed handicaps. Heroes can be told to train until they learn a named skill. Sparring with real weapons teaches counter-skills. | 3-4, 39, 43-44 | clear |
| Skill tiers | Skills come in tiers: low-rank/basic, intermediate and up; "divine" skills exist at the top. | 3-4, 11, 25 | clear |
| Skill merging and compatibility | Two compatible skills fuse into a new one that starts above level 1 (basic sword + basic shield = a sword-and-shield skill at Lv3). Incompatible skills weaken each other; more skills is not more strength. | 4, 11, 17, 44 | clear |
| Skill awakening | Being pushed to the limit can awaken a skill (pain tolerance, composure, berserk), sometimes two levels at once. Hunting awakened a hunter skill. | 4, 11-12, 14 | clear |
| Resistance training | Resistances (fire, pain) are trained on purpose and give tolerance, not immunity. | 24, 28 | clear |
| Rare passives | A rare passive turns a status like rage into strength. | 58 | clear |
| Third ability | A strange stone is said to grant a special power, a hero's "third ability". | 59 | partial |
| Mid-battle skill books | A hero can ask for a skill mid-battle; the master buys a book in the Battle Item Shop and the hero learns it at once (basic riding, 500 gems). Only basic skills. | 30 | clear |

## 6. Equipment, items and crafting (#345)

| Mechanic | How it works | Ch | Conf |
|---|---|---|---|
| Grades | F < E- < E < E+ < D+ < C- < C < B and up, with +/- sub-grades. Materials are graded too (steel C, iron ore B or C). | 1, 3, 6-7, 10, 23, 25 | clear |
| Default weapon and storage | A hero with no weapon carries an F-grade old iron sword. Weapons placed in storage are auto-equipped; the master can choose who holds what. A dead hero's gear returns to storage. | 1, 3 | clear |
| Slots | Main weapon, armor, a backup weapon (daggers), potions and carried items. A leather pouch raises carry capacity. | 10, 25, 40, 46 | partial |
| Breakage and repair | Carried items can break ("potion broke!"). A sword needed repair mid-fight. | 25, 40, 46 | partial |
| Armor upgrades | Leather armor (E) was reinforced with iron plating to D. | 10 | partial |
| Magic gear | Custom clothing can boost a magic element. | 24 | clear |
| Crafting minigame | The forge shows a success rate, with penalties for a missing facility, expert or blueprint. The player picks a puzzle difficulty up to "Extreme Hell"; harder means a better result, 3 minutes a puzzle. Outcomes include "Complete Success". Potions use the same kind of minigame. | 6-7, 24 | clear |
| Hero artisans | Heroes with trades (smith, tanner, carpenter, cook) craft gear (mostly E grade) and food. Each facility takes 2 support heroes. | 8-9, 19 | clear |
| Equipment Workshop | Crafting facility that later gains an assistant; D-grade bows were made there. | 42, 46, 60 | clear |
| Potions | Health potions heal injuries but the pain stays; mana potions restore mana. Potions are equipped per hero. | 24, 28, 30, 40 | clear |
| Gathering and trash | Gathering windows during missions. F-grade items are trash to throw away. 100 branches make lumber, which needs a carpentry shop. Items go to a dimensional inventory. | 13 | clear |

## 7. Tower, floors, missions and stakes (#346)

| Mechanic | How it works | Ch | Conf |
|---|---|---|---|
| 100-floor main dungeon | The spine of the game: clear floor 100. The gate opens after a 10-second countdown. Higher floors are harder. | 1-2, 21, 42, 44, 46 | clear |
| Five-floor spike | Difficulty jumps every 5 floors, like a boss floor (wiki: boss missions on 5/10/15/20). Floor 12 was much harder than 11 while 13-14 were easier: difficulty is not monotone. | 8, 12, 37-40 | clear |
| Replays | Cleared floors can be replayed for gold (1,000, 11,000, 20,000). A regenerative stone lets a team replay a shut-down boss stage, with no EXP. | 4, 23, 42, 60 | clear |
| Mission types | Subjugation/Conquest, Survival, Explore, Defend, Guard, Escort, Breakthrough, Assassination, Protection, Siege, Pursuit, Escape, Compound. | 1, 9, 14, 24, 26, 35, 46 | clear |
| Survival | Survive a horde for a fixed time (floor 5: thousands of enemies, 10 minutes, timer starts when enemies arrive). | 9-12 | clear |
| Defend | Protect a statue; if it falls, everyone dies. Killing the boss fast decides it. | 26-33 | clear |
| Changing objectives | Floor 15: "Guard a certain person", who must first be found in a crowd; then a new objective appears, "Escape the city with the target". | 46-52 | clear |
| Hidden timers | Remaining time can show as "???". | 46-48 | partial |
| Compound floors | Floor 80: objective unknown, waves of Lv108-121 enemies in the thousands plus SS-rank crystals at Lv311-322; only 5 accounts ever cleared it. | 35-36 | clear |
| Random stages per account | Every account gets different missions with no repeats; difficulty is sometimes random. A "Roulette of Fate" picks the mission type. | 24, 36, 38 | clear |
| Streams (chain quests) | Linked quests start with an Explore mission; clues planted across floors 5-9 pay off on floor 10. | 15, 19, 24, 26 | clear |
| Multi-party floors | A hint shows how many parties a floor needs (floor 10: at least two). A three-part warning marks the highest difficulty. | 19, 24, 26 | clear |
| Enemy roster | Enemies show name, level and count (e.g. "Lv9 x27"); level can be "???". Off-tier monsters from far higher floors can appear. Vital-point hits matter. | 21, 26, 29, 31-33, 36, 42, 44, 52 | clear |
| Human enemies | From floor 12, always-hostile human enemies; fighting them raises stress faster. | 44-46, 52 | clear |
| Mission NPCs | Allied NPCs and units can join by event (500 cavalry). On early floors NPCs can't see the heroes; later they can talk, help and have skills such as healing. | 25-30, 47, 50, 59 | clear |
| Rewards | Clear screen: level-ups, an MVP, materials (elemental stones, ore, bone dust, leather, planks) and gold, delivered to the mailbox. | 2, 4, 9, 12, 19, 23, 33, 38, 42, 46 | clear |
| Weekday dungeon | Opens after floor 5. Each day of the week opens one area (fire stones on two days, other elements on others, all on Sunday), with a countdown to the day's close. Drops: gathering materials, elemental stones (for crafting and promotion) and a rare monster that drops promotion material. Difficulty tiers start at "lowest rank". | 12-14, 18-19, 24, 32, 39, 41 | clear |
| Exploration | Unlocked at floor 10. A party goes into the game world for 48 hours and comes back on its own. The master can request items and give each hero an allowance (3,000 gold suggested) that they spend in local coin (a D-grade bow costs 2 coins). Loot is random: sometimes nothing, sometimes great. | 32, 59-60 | clear |
| World regions | A new region opens every 10 floors behind an invisible border; the whole world opens at floor 100. | 60 | clear |
| Persistent world | The world remembers earlier missions (a city rebuilt, wanted posters for a rescued NPC). World time runs far faster: a year passes there in under a week of hero time. | 59 | clear |
| In-world dungeons | Ruins in the world are dungeons with entry permits and a cut of the loot; a barrier only some can cross. | 59-60 | clear |
| Adventure Dungeon | Unlocked at floor 10; drops special stones and rare ingredients. Not visited by chapter 60. | 33 | clear (unvisited) |
| Permadeath | Any hero who dies, of any grade, is gone for good; no revive exists. A death notice names the hero and sometimes the cause. | 1-2, 8, 11, 17, 33 | clear |
| Party wipe and mission failure | Losing all heroes shows "YOU LOSE" and a "party has been killed" notice. A mission fails when the party dies or the protected target falls; in the story, failure or retreat means death. Deaths inside a mission do not fail it (a floor was cleared after losing 2 of 5). | 1, 26, 33, 39-41, 46-47 | clear |
| No retreat button | Parties return only on a clear, on death, or when an exploration timer ends. | 36-60 | inferred |

## 8. Master meta, lobby and facilities (#347)

| Mechanic | How it works | Ch | Conf |
|---|---|---|---|
| Auto-battle | Combat runs on hero AI. The master gives basic orders; heroes pick their own skills and react differently to the same command. The master "cannot interfere yet": higher floors are said to change that. It is played as an AFK game. | 2, 7, 32, 37, 46 | clear |
| Party building | Drag and drop five heroes into numbered parties; parties can be renamed and have a leader; temporary parties exist. Heroes can ask to join a party (and fight better when they asked) or suggest parties, which the master accepts or rejects. | 1-2, 8, 13, 15, 19-20 | clear |
| Tactical Posts | A facility that, at max level and with a stage map, opens a pre-battle screen: drag heroes into position and place supporting objects before a ~10 s countdown. | 36 | clear |
| Hero refusal and strikes | Heroes can refuse a dungeon, making their party "inoperable". The tip: meet their wishes or punish the instigator. A mass strike disabled two parties; synthesizing the ringleaders restored them. | 38-39, 41-42, 57-58 | clear |
| Favorites | Favorited heroes are protected from culling. | 5, 7 | clear |
| Gifts | Gift cards buy items; gifts raise a hero's favorability. Each hero has tastes and may ask for a specific gift; gifting only one hero makes others jealous. | 37, 41-42 | clear |
| Duels | Heroes settle conflicts by duel; with both sides' agreement they set stakes (the loser gets synthesized). Ends on surrender. Otherwise heroes may not fight each other. | 16-17 | clear |
| Manager NPC | A fairy who manages the lobby: relays the master's orders, calls heroes together, opens and closes facilities, passes on tips. She may use force (instant death) only in self-defence or against disobeyed orders. She earns "intervention points" while the master is active and spends them to act outside the game; her powers are limited. | 1, 3, 5-6, 13, 41-42, 55 | clear |
| Facilities | Summoning Hall, Synthesis Chamber, Promotion Chamber, Lodging, Training Hall, Weapon Storage, Square, the gate, Restaurant, Research Lab, Tactical Posts, Equipment Workshop. Facilities level up (Lv2-3 seen; end-game facilities at Lv18-22). The base grows as floors are cleared. | 4-5, 7, 18, 34, 36, 40, 42, 55, 57 | clear |
| Facility effects | Lodging levels raise the hero cap and add a bathhouse, rest area and cafeteria. The Restaurant gives food that lowers stress. Training Hall Lv2 trains faster; its upgrade heals fast (a severed arm reattaches) and adds a lockable solo room and a Duel Center where regeneration is off. | 7, 18, 40, 55, 57-58 | clear |
| Annexes and fusion | Annex buildings cost gems (forge: 300). Three annexes fuse into a bigger building: smithy + woodshop + metal shop = Equipment Workshop; research lab + alchemy lab + library = Magic Hall. | 5, 7, 24 | clear |
| Research Lab | A hero works as researcher and earns research points by solving problems: 10 points an hour for 10 gems an hour, hours per day set by the master (3 in the story). Too many hours make the researcher resentful. Trees: Hero Reaction (Lv1 for 30 points: heroes see part of their own windows and the mission), Lobby Expansion (more buildable facilities, customization), Dungeon Depth (longer explorations). | 42-43, 55, 59 | clear |
| Tiered lodging | Lobby customization lets the master give some heroes better quarters: 13 of 35 heroes on a privileged floor with all facilities, 22 on a restricted floor. Works as an incentive ladder in place of culling. | 55-58 | clear |
| Floor-10 unlocks | Clearing floor 10 lets the master name the lobby (unique per master), unlocks the Adventure Dungeon and Exploration, and opens new lobby space. | 33, 38, 40, 59 | clear |
| Lobby life | Food and supplies refill daily; imagined items appear in drawers; guns do not work; heroes heal at once in the lobby. | 3, 8, 18, 24 | clear |
| Time ratio | Lobby time runs about 3x real time (10 lobby days = 3 real days); battles run in real time. The master logs in about three times a day. | 4, 9, 13, 40-41 | clear |
| Absence | An account is deleted after 6 months without a login; the lobby falls apart without the master. | 40-41 | clear |
| Replays | A 300-gem "Record" feature saves runs to rewatch. | 46 | clear |
| Tips, mailbox, filter | Tip pop-ups explain systems. Rewards arrive by mailbox. Hero speech reaches the master through a filter. | 1-3, 8, 16 | clear |
| Account and tutorial | Account name of 2-6 characters. Tutorial quests give fixed rewards. Summon and Synthesis tabs. | 2 | clear |
| Rankings and community | Masters are ranked by floor reached (world #5 at floor 88; everyone below #5 at floor 79 or lower). Official forum with guides, a fan cafe, a lounge per master. | 5-7, 25, 32, 35 | clear |

<!-- CH61-93 -->

## 10. Numbers seen

<!-- NUMBERS -->

## 11. Evidence for the open decisions

<!-- EVIDENCE -->

## 12. Gaps

<!-- GAPS -->
