---
name: livingrust-2026-09-21-state
description: What shipped 2026-09-19/21 and what is still UNTESTED - read before the fresh-map session
metadata: 
  node_type: memory
  type: project
  originSessionId: ea44b221-e47a-4c9f-a86e-4c4181731680
  modified: 2026-09-21T07:20:52.114Z
---

Shipped and deployed (all built clean, most NOT yet observed live):
- Restore-resume after reload/restart, dead bots respawn on restore, watchdog fairness (oldest-stalled first), in-flight build guard, build-goal kept until real success, flat-ground check + design-aware door/foundation check (tolerances relaxed), failed-site memory, 30m build-site reservations, 50m monument no-build (OBB).
- Instant blueprint unlock on pickup (LivingRust.Blueprints.cs). Crafting-from-blueprint / workbench chain / recipe "memory" NOT built yet (spec given by Lucas: wanted-recipes list, workbench tiers first, protect components from recycling).
- Survival kit upkeep (bow+arrows+3 heal items; firearm bots skip bow), ranged-only animal hunting w/ 22m standoff, wood pickup cap 3500, recycler fixes (no hammers/plans, clear recycler first, sweep dropped items), multi-furnace fill (wood trigger 300), post-build resume, base-return every 20min (defers 5min if in monument/fight/building), corpse/bag awareness 50m, bow spread+drop, scientist flee 50-100m + 5min per-bot avoid zone, animals: no base => always avoid, base+ready ranged => don't avoid.
- Monument destination weights flat/gear-independent (monument-heavy), soft occupancy caps, gear gate only on card puzzle; supermarket/gas station route end grants green keycard + 1 fuse.
- Airdrops (LivingRust.Airdrops.cs): heads-up from CargoPlane spawn, cap 15, own rally points, hold until landed (fall ~400s), first-to-crate loots then leaves, hot-zone PvP-on-sight. NEVER got a clean end-to-end test - my own reloads wiped earlier runs. Expect log lines: inbound, supply drop spawned, setting out, rally point, crate landed, moving in.

**Why:** these were built from Lucas's specs across a long session; verification is the gap.
**How to apply:** first thing next session, test ONE airdrop (supply.drop / supply signal) with no reloads for ~8 min; check base-return, scientist flee, early-kit, deposit/furnace logs with `lr.debug.verbose on` briefly.

Open backlog: recipe/crafting progression, smarter full-inventory drop order (weight by current craft goals/recipes), decay/raid repair, high-value dropped-item scan, ground-probe fix verified? (probe origin fix shipped, stall-rescue failures not re-measured), navmesh "no valid NavMesh" console spam (harmless, untouched), existing bad/half-built bases need cleanup.
