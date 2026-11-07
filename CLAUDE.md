# Instructions for agents

## Keep the specs current

The specs in `specs/` are the source of truth for this project. Start with `specs/00-overview.md`, which holds the canonical constants and cross-spec contracts.

When something is discussed and agreed with the user that diverges from what a spec says, update the spec in the same change:

- Edit the affected spec(s) so they describe the agreed design, not the original plan. Fix every spec the change touches, including constants and contracts in `specs/00-overview.md`.
- Update anything that mirrors the spec in the same change: test vectors, `golden/` files, `src/HeroCat.Bots/Acceptance/acceptance-targets.json`, and `tools/` models where relevant.
- If the README's spec table no longer matches, update it too.
- Do not leave code and specs disagreeing. An existing mismatch with no agreed decision behind it is a gap: handle it with the rules below.

## When you find a gap

A gap is anything the specs, code or task leave undecided: a missing rule, an unhandled edge case, a contradiction, tech debt. Do not guess silently. Pick one:

1. **Obvious, low-stakes and easy to undo** (naming, a conventional default, a missing guard): decide, proceed, and state the decision in the PR description so the user can veto it in review.
2. **Does not block the current task:** open a GitHub issue and keep going. Search open issues first and comment on a match instead of duplicating it. Label it `agent-found`. The issue must stand alone: the gap, where you found it, options with a recommendation, and what it blocks. If you work around it, leave `TODO(#N)` at that spot.
3. **Blocks the task and the call is the user's** (game design, balance targets, monetization, anything hard to reverse, specs contradicting each other): stop and ask. Do not file an issue and guess anyway.

When unsure between 1 and 3, treat it as 3. Create issues with `gh api repos/kjew2/herocat/issues` (REST; `gh issue create` fails here because GraphQL is blocked).

## Run every check before calling a change done

`./check.sh` runs everything CI runs: content build drift, .NET build and tests, the tick benchmark, the 20-seed acceptance sweep and the golden-file check.

- `./check.sh --fast` for the inner loop (skips golden tests, the bench and the acceptance sweep). Run the full `./check.sh` before you finish.
- `./check.sh --no-dotnet` on a machine without the .NET SDK. It is a partial check: say so when you report results.
- A change is not done while any check fails. Do not weaken a test, threshold or golden file to make it pass unless the user agreed to that change (and then the spec rule above applies).
- If you add a new kind of check, wire it into `check.sh`.

## Pull requests auto-merge when checks pass

Agreed with the user: a PR an agent opens merges itself once its required checks pass (`pr / checks`, `content / content`, `specs/10` section 6.1).

- Right after opening the PR, enable auto-merge with the merge-commit method (`enable_pr_auto_merge`). Do not wait for a review.
- If auto-merge can't be enabled (the repo setting is off, or the PR is already mergeable), merge it yourself once every check is green.
- Never merge with a failing or pending check, and never get a check to pass by skipping or weakening it. If a check fails, fix it on the branch and auto-merge goes ahead when it turns green.
- A change that needs a user decision (rule 3 above) does not get a PR until the user has answered.

## Learn from mistakes

This file is how lessons reach the next agent. When the user corrects you on something that is likely to come up again (a convention, a command, a gotcha, a wrong assumption), add a short rule for it under "Lessons" below in the same change. Keep each rule to one or two lines, say what to do rather than what went wrong, and remove a rule once it no longer applies.

## Lessons

- The project is being rewritten from HeroCat into *Hundred Floor Summoner*, an original-world gacha game on *Pick Me Up!* mechanics (epic #353, `specs/00` decisions 121-127). `specs/00` and `specs/11` describe the new game; specs 01-10 and the code still describe HeroCat until each is rewritten (`specs/00` section 10). Never put that work's names, characters, story, art or text in the repo, and don't start HeroCat-only feature work.
- HeroCat citations like "00 section 6.1" now resolve in `specs/archive/00-overview-herocat.md`, HeroCat citations of spec 03 ("03 section 8.6") in `specs/archive/03-combat-and-progression-herocat.md`, and HeroCat citations of spec 06 ("06 section 7.1") in `specs/archive/06-economy-and-daily-loop-herocat.md`. Decision numbers are never renumbered; `specs/00` section 12 gives each one's status (kept, changed, retired, review).
- When a spec departs on purpose from `docs/research/pick-me-up-infinite-gacha.md`, add or update its row in `specs/11-source-divergences.md` in the same change, and cite the `DIV-` id in the spec.
- The product is a Unity 6 app for iOS and Android and the only client; there is no web build (`specs/00` decisions 31-32). Keep the sim Unity-safe (netstandard2.1, C# 9).
- Code names stay `HeroCat.*` until the rename (#351); the working title is *Hundred Floor Summoner* (`specs/00` decisions 30, 127).
- Unity cannot run in cloud sessions. Only the client's engine-free `unity/HeroCatClient/Assets/HeroCat.Client/Core/` is compiled and tested (inside `HeroCat.Runtime.Tests`); `Boot/`, `Screens/` and `Editor/ContentPreBuild.cs` are only type-checked against Unity API stubs by `tools/unity_typecheck`, which proves nothing about behaviour in Unity. Say so when you change them.
- Cloud sessions install the .NET 8 SDK through `.claude/hooks/session-start.sh`. If `dotnet` is missing, run that script with `CLAUDE_CODE_REMOTE=true`, then the full `./check.sh`.
- The acceptance sweep is report-only until the pivot's balance model #348 replaces it (`REWRITE_REPORT_ONLY=1`, agreed with the user; #249 is closed, `specs/00` decision 125); golden checks are hard again. Report the sweep's result honestly; never regenerate targets or thresholds to hide a regression.
- When a task is blocked on a user decision, ask (or file the decision issue) and keep going with other unblocked work; do not sit idle waiting for the answer.
- Generated test data is large and not for reading. Never open, grep through or hand-edit `golden/` files: regenerate them through the bot CLI, never edit by hand.
- When you share art exploration images, add them to the design lookbook (https://claude.ai/artifact/LQzxNoeTbZjFCY7NSbLceR, linked on #321). Republish that page by its URL; don't create a new one.
- Before generating art, read "Owner preferences" in `art/concepts/splash-prompts.md`. When the owner picks between takes, record the pick and update the preferences there.
