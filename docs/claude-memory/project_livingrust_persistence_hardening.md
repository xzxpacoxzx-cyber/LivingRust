---
name: project-livingrust-persistence-hardening
description: "Pointer: LivingRust 2026-08-08 persistence-hardening bug chain (userID collision, sleep, stuck death-state, stale Spawned, missed native saves) lives in G:\Claudes Conversation history\, not here"
metadata:
  type: reference
---

Full write-up of the 2026-08-08 persistence-hardening session (6-bug chain found after the initial capture/restore feature: userID collision with Rust's own NPCs, bots staying asleep after restart, the sleep fix not showing visually, killed bots never respawning, some bots invisible to restore entirely, and the roster going stale under the user's actual hard-kill-after-manual-save workflow) lives in `G:\Claudes Conversation history\2026-08-08-livingrust-bot-lifecycle-persistence-session.md` ("Part 4"), per [[reference-livingrust-session-history]]'s standing rule - not duplicated here.

**Why this matters:** confirmed working end-to-end by the user testing their real restart workflow. See [[project-livingrust-overview]] for the current state summary and [[reference-livingrust-session-history]] for where the detailed narrative lives.

**How to apply:** if a related persistence bug resurfaces, read the dated history file above first for the full chain of reasoning and evidence (decompiled-assembly findings, log/JSON forensics) before re-deriving it.
