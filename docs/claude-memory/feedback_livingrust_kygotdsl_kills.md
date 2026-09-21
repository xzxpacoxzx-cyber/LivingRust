---
name: feedback-livingrust-kygotdsl-kills
description: "Bot deaths from player 'KyGotDSL's' in Carbon.Core.log are deliberate test-ending kills, not real combat events worth investigating."
metadata: 
  node_type: memory
  type: feedback
  originSessionId: ea44b221-e47a-4c9f-a86e-4c4181731680
  modified: 2026-08-19T09:15:54.653Z
---

When a LivingRust survivor bot dies to player 'KyGotDSL's' (Lucas's own steam name) in `Carbon.Core.log`, assume the bot was bugging out / not behaving as intended and Lucas killed it to end that test cycle - do not treat it as an organic combat/threat-response event worth investigating in its own right.

**Why:** Lucas's own explicit instruction, 2026-08-19 - a `damage-diag`/`died (Bullet)` line attributed to KyGotDSL's is functionally a manual test-reset, not gameplay.

**How to apply:** When tracing a log around a bot's death for an unrelated bug (e.g. a stall, a stuck task), if the killer is KyGotDSL's, don't chase "why did combat happen" - the real signal is whatever the bot was doing/failing to do in the moments *before* that kill. See [[project_livingrust_overview]] for general session context.
