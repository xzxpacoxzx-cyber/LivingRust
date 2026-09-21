---
name: feedback-livingrust-verbose-default-on
description: "LivingRust's _verboseLootLogging defaults to true as of 2026-08-15 - Lucas wants it on by default during active debugging sessions"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: ea44b221-e47a-4c9f-a86e-4c4181731680
  modified: 2026-08-15T13:52:17.794Z
---

`_verboseLootLogging` in `LivingRust.Main.cs` defaults to `true` (flipped from an earlier same-day default of `false`).

**Why:** Lucas's own explicit request mid-session: "keep verbose on by default, it should help you out more than it helps me out" - during active navmesh/movement debugging work, the routine per-bot narration (loot summaries, movement steps, stuck/recovery events) is the main diagnostic signal Claude relies on when reading `Carbon.Core.log`, so the noise cost is worth it. This reverses an earlier same-session default (OFF) that was set specifically to avoid flooding the console during large free-roam batches of 100-200 bots.

**How to apply:** Leave `_verboseLootLogging = true` as the default going forward. If a future session shifts to large free-roam batch testing (not active debugging) and the log volume becomes a real problem, it's fine to suggest toggling it off with `/lr.debug.verbose off` for that specific test, but don't flip the *default* back to false without Lucas asking again - this was a deliberate, explicit reversal of the earlier choice.
