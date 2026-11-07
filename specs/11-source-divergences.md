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
