---
name: feedback-livingrust-default-to-running
description: "LivingRust bots should default to running/sprinting for all movement, not walking - walking is the rare exception, not the default"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 3f841f35-40bd-45b9-adb0-04739bdc72d9
  modified: 2026-08-09T08:56:24.178Z
---

LivingRust survivor bots should sprint by default for essentially all movement, across every task (looting, and later mining/building/combat/anything else) - walking should be the rare, deliberate exception, not the default pace. Lucas explicitly restated this is NOT "run 100% of the time with zero gradation" - the intended shape is "run to the task at hand, then ease off/slow down when actually approaching it," not a flat sprint with no approach behavior. Don't let future work flatten this into an unconditional sprint with no slowdown.

**Why:** Lucas's explicit correction: real Rust players run ~95% of the time, because there's a genuine competitive advantage to moving fast (someone else might get there first). A bot that walks everywhere by default reads as unrealistic/passive compared to how real players actually behave. Walking only makes sense in specific contexts - inside your own base, on a risky ledge, deliberately sneaking/hiding - none of which exist as implemented concepts yet (base-building, ledge-danger-awareness, and stealth are all still open roadmap items, see [[project-livingrust-roadmap]]).

**How to apply:** any new movement-driving code (walk/task/behavior systems) should default to running speed unless there's a specific, real contextual reason not to (an actual implemented "be careful here" condition), not the other way around. `StartWalking` (`LivingRust.Commands.cs`) was refactored 2026-08-09 to this exact model: sprints unconditionally unless an optional `shouldWalkCarefully` predicate says otherwise (currently always null/unused, since no such contexts are built yet), and automatically eases to a walk only within `ApproachSlowdownDistance` of wherever it's actually arriving, so the final few steps still read as a real approach rather than sprinting flat into the destination. Apply the same "run by default, walk only for a specific reason" pattern to any future task/behavior work (mining, building, combat) rather than defaulting new movement to a walk pace and requiring an opt-in to run.
