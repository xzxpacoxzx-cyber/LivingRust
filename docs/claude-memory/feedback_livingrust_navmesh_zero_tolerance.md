---
name: feedback-livingrust-navmesh-zero-tolerance
description: "Standing expectation for the per-monument navmesh debugging pass - report every anomaly, no matter how small, not just hard failures"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: ea44b221-e47a-4c9f-a86e-4c4181731680
  modified: 2026-08-15T11:29:36.803Z
---

During the monument-by-monument navmesh debugging pass (see [[project-livingrust-roadmap]]'s "next session's starting focus" entry), Lucas's explicit standing instruction: be fully transparent, flag it if a bot "seems to bug out in any way shape or form" - not just crashes/exceptions/stuck-forever cases. Report hesitation, an odd detour, a stutter, a sub-optimal approach angle, a stuck-recovery escalation firing when it arguably shouldn't have needed to, anything - even if the bot ultimately self-recovers and the task technically completes.

**Why:** his own framing - "we need to nail this 10000000000000000% of the way, this is where bots will really excel in progressing themselves throughout the world and becoming a real pain for players." Monument navigation (reaching keycard/crate_elite/military-tier loot behind doorways/walls) is explicitly the mechanism he expects to make bots a genuine threat/challenge to real players, not a nice-to-have polish pass - so silence on a minor-looking glitch here is a real cost, not harmless.

**How to apply:** during any live trace/verbose session on monument navigation (this includes the tier-1-then-2-then-3 pass), proactively surface anything that reads as even slightly wrong in the log/trace the moment it's spotted, rather than waiting to be asked or filtering for "is this bad enough to mention." Default to over-reporting in this specific context - err toward flagging too much rather than too little. This is a context-specific escalation of the general "verify don't guess" working style already documented in [[user_lucas_profile]], not a blanket rule for every LivingRust topic.

**Ranking, added 2026-08-15:** when multiple anomalies turn up in one pass, don't just list them in log order - rank them high to low by actual severity/impact using own judgment ("I trust your instinct," his own words). Weigh: how often it'd realistically bite in live play, how bad the failure mode is (permanently stuck/dead vs. a cosmetic stutter that self-resolves), and how many monuments/situations it likely generalizes to vs. a one-off. Lead the report with the highest-priority finding, not the first one spotted chronologically.
