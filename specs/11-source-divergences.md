# 11 - Divergences from the source mechanics

The pivot builds on the mechanics of *Pick Me Up!*'s in-story game, catalogued in `docs/research/pick-me-up-infinite-gacha.md` (decision 121). This register lists every rule where our design **knowingly differs** from what that catalog says the source does, so a later change doesn't "fix" a deliberate choice back to the source's version (decision 129, issue #382).

## Rules

- **Kinds:** *Cut* (a source mechanic we won't build), *Changed* (the same mechanic with a different rule or numbers), *Reframed* (the same mechanic with different fiction), *Added* (a system the source doesn't have).
- **Not logged one by one:** our own names, characters, story and art. Decision 121 requires them as a class.
- **Same change:** when a spec departs from the research doc, add or update the row here in the same change. Specs cite rows by id (`DIV-01`).
- **Who approves:** cuts, reframes and anything that changes tone, rating or monetization need the owner (`CLAUDE.md` rule 3). Tuning-only changes can be an agent's call, stated in the PR (rule 1).
- Ids are never reused. A row that no longer applies is marked *Withdrawn*, not deleted.

## Register

| Id | Source rule (research doc section) | Our rule | Kind | Why | Decided in |
|---|---|---|---|---|---|
| DIV-01 | Summon odds and floor difficulty differ per account; an account's world can run out of high grades (§4, §11) | One published rate table per banner, the same for every account; floor variants are seeded and fair | Changed | Honest odds; regulated in KR, JP and CN | #340, decision 126 |
| DIV-02 | No pity or guarantee; only vague "conditions" for pulls (§4, §12) | Hard pity on hero banners | Added | Honest odds | #340, decision 126 |
| DIV-03 | Gold summons give low grades and gems give high grades; free players stay short of strong heroes (§4, §9.2) | No grade is paid-only; free players can reach every grade, and paying gets there sooner | Changed | Fairness | #340, decision 126 |
| DIV-04 | Death is absolute, with no revive at all (§7, §11) | A dead hero can come back with a revive item, earned in play or bought for gems at a listed price | Added | Owner's core loop | #339, #340, decisions 123 and 126 |
| DIV-05 | Gems pay for research hours, annexes, mid-battle skill books and replay recording (§4, §8) | These cost gold or materials; gems only skip waiting | Changed | Fairness | #340, decision 126 |
| DIV-06 | The master gives basic orders in battle and gets a live input on floor 20 (§8, §9.6) | No live input in fights: the player assigns heroes and fights run on their own | Changed | Idle core loop | #339, decision 123 |
| DIV-07 | An account is deleted, and its lobby dies, after 6 months without a login (§8) | No account deletion for inactivity | Cut | Plot-only rule; players don't lose paid value | #381, decision 128 |
| DIV-08 | The heroes are real people pulled from their lives (§2, §13) | Heroes are called from former ordinary lives by a licensed summoner; there is no "trapped inside a game" frame | Reframed | Our premise, "The Summoner's Tower" | #354, decision 124 |
| DIV-09 | Stat points redistribute themselves, so a stat can fall on level-up (§3) | Level-ups only add points, placed automatically by a focus preset. A stat falls only when the player redistributes for gold (MVP) | Changed | A level-up should never feel like a loss; redistribution becomes a lobby decision | #357, decision 138 |
| DIV-10 | Some 1★ heroes hide a high growth rate ("potential") with no stated reveal (§3) | Growth rate is fixed per hero definition and shown after the hero's first level-up; extra hidden growth is a talent with a shown reveal condition | Changed | Honest odds: no hidden per-copy value | #357, decision 137 |
| DIV-11 | Low grades' talents are hidden until the master uses the hero; how they reveal is unexplained (§3) | Talents are fixed per hero definition, not rolled per copy. The sheet shows their count and each reveal condition with progress; the reward stays hidden, and a hidden talent has no effect | Changed | Honest odds: no hidden roll on an owned copy (DIV-01) | #360, decision 137 |
| DIV-12 | Seal slots hold graded traits from the heroes' home world, such as a bloodline (§9.1, §11) | Seal traits come from the hero's own former ordinary life (lineage, upbringing, an old trade). There is no other world | Reframed | Our premise (DIV-08) | #360, owner on #342 (follows decision 124) |
| DIV-13 | A former job carries over loosely: a hunter is good with a bow, artisans craft (§3, §11) | Every former life gives the same starting stat budget, spread differently; aptitudes are training-rate, trade and lobby-work bonuses | Changed | Fairness: former life is flavour and fit, not a ranking | #360 (tuning) |
| DIV-14 | Promotion restores memories that change behaviour (§5, §9.3) | Each promotion step can have one authored memory, applied once per hero per grade; it never lowers a stat. A hero pulled at a high grade arrives with the lower grades' memories applied | Changed | Determinism; no paid step makes a hero weaker | #360, decision 143 |
| DIV-15 | Status triggers are not explained; the master learns of them only by alerts (§3, §12) | Fear, Panic and Despair are bands of a visible pressure value; each hero's opening status is forecast, as a band, before assignment | Added | No hidden rules in an idle game the player can't steer mid-fight | #358, decision 141 |
| DIV-16 | Fear, Panic, Despair and Exhaustion lower "all stats" (§3, §9.1) | They lower every combat stat but never the HP pool; summed status percentages clamp at -95% / +1000% | Changed | A -80% max-HP cut would kill outright; stacked penalties need a floor | #358 (tuning) |
| DIV-17 | Frenzy between a hostile pair wipes the party; no numbers (§3) | A frenzied hero attacks only its hostile partner, for a fixed time; the wipe risk comes from the deaths and shock that follow | Changed | Bounded, readable friendly fire | #358, decision 140 |
| DIV-18 | Status alerts reach the master and the party leader even when parties are split (§3) | Alerts show in the battle view, on the lobby's party chips and in the offline and mission reports, never as push notifications; the leader's AI hook is P1 | Changed | Idle game: the player can't act on an alert mid-fight | #358 (tuning) |
| DIV-19 | Stress has no visible meter (§3) | Morale, stress, loyalty and bonds are shown as numbers and bands, and a breakdown shows its countdown | Changed | Fairness: the player can see the risk coming | Owner, #359 comment |
| DIV-20 | Extreme stress kills a hero outright, with only a death notice (§3, §13); refusals come without warning (§8) | Stress death only after two seen warnings and a 24 h countdown that one rest order always stops; refusals follow fixed rules shown before assignment | Changed | Decision 128 keeps the dark rule, not the ambush | #359, decisions 136 and 139 |
| DIV-21 | Gifting only one hero makes the others jealous (§8) | Jealousy only among party-mates of a gifted hero | Changed | One readable rule instead of a lobby-wide chore | #359 (tuning) |
| DIV-22 | Loyalty and favourability appear as separate ideas (§3, §8) | One value, loyalty, raised by gifts and clears | Changed | Fewer meters for an idle game | #359 (tuning) |
