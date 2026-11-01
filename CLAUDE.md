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

## Learn from mistakes

This file is how lessons reach the next agent. When the user corrects you on something that is likely to come up again (a convention, a command, a gotcha, a wrong assumption), add a short rule for it under "Lessons" below in the same change. Keep each rule to one or two lines, say what to do rather than what went wrong, and remove a rule once it no longer applies.

## Lessons

- The product is a Unity 6 app for iOS and Android and the only client; there is no web build (`specs/00` decisions 31-32). Keep the sim Unity-safe (netstandard2.1, C# 9).
- Code names are `HeroCat.*`; "Whisker Hero" is only the product's working title (`specs/00` decision 30).
- Unity cannot run in cloud sessions. Only the client's engine-free `unity/HeroCatClient/Assets/HeroCat.Client/Core/` is compiled and tested (inside `HeroCat.Runtime.Tests`); `Boot/` and `Screens/` are only type-checked against Unity API stubs by `tools/unity_typecheck`, which proves nothing about behaviour in Unity. Say so when you change them.
- Cloud sessions install the .NET 8 SDK through `.claude/hooks/session-start.sh`. If `dotnet` is missing, run that script with `CLAUDE_CODE_REMOTE=true`, then the full `./check.sh`.
- The acceptance sweep is report-only until the balance decision #249 lands (`REWRITE_REPORT_ONLY=1`, agreed with the user); golden checks are hard again. Report the sweep's result honestly; never regenerate targets or thresholds to hide a regression.
- When a task is blocked on a user decision, ask (or file the decision issue) and keep going with other unblocked work; do not sit idle waiting for the answer.
- Generated test data is large and not for reading. Never open, grep through or hand-edit `golden/` files: regenerate them through the bot CLI, never edit by hand.
