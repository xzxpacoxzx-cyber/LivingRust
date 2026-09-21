# LivingRust — Ladder Climbing Session Summary

**Date:** 2026-08-07 (consolidates several sessions' worth of ladder-climbing work since the 2026-08-06 movement/pathfinding session paused right before ladders)
**Project:** `G:\Projects\TemplateProject` (Carbon plugin, live test server under `Rust\server\`, identity "LivingRust")
**Scope:** implementing ladder climbing end-to-end (up, down, tied into follow, a standalone "climb the monument" goal), fixing the long tail of monument-geometry edge cases that surfaced along the way, then hitting new regressions on a second monument type (a watchtower) and proposing a architecture change for monument navigation.

This file supersedes the C:\-based memory system for this project going forward — see the note at the bottom.

---

## Who's working on this / how (merged from the old C:\ memory system)

User is building a Rust (game) server plugin called LivingRust using the Carbon modding framework (C#, .NET Framework 4.8, Harmony-based). The plugin adds persistent AI-driven NPC survivors that navigate the game world via Rust's baked NavMesh, have needs (hunger/thirst/fatigue), and personality-driven decision-making.

Development loop: runs a local Carbon test server, manually drives an in-game character to test AI behavior via chat/console commands (spawn/follow/walk/debug), reads the server log and Carbon command history to see what happened, then edits the C# source in Claude Code sessions.

Comfortable working close to game-engine internals (Unity physics layers, raycasting, NavMesh, entity spawning). Takes a rigorous "verify via an in-game debug command, don't guess" approach to reverse-engineering Rust/Unity internals (e.g. physics layer masks confirmed via a custom `/lr.debug.look` raycast tool rather than assumed from docs; `ModelState`/`SendModelState` behavior confirmed via PowerShell reflection over the decompiled `Assembly-CSharp.dll` rather than guessed at).

---

## New commands since the last summary

| Command | What it does |
|---|---|
| `/lr.climb` | Climbs the nearest ladder. Auto-detects direction: compares current Y against the ladder's midpoint Y — below it climbs up, at/above it climbs down. |
| `/lr.climb.monument` | Autonomous "climb as high as possible" goal — picks a destination 100m straight up from wherever the survivor stands when issued, then chains ladders/ramps to get there, giving up cleanly after ~10s of no real progress. |
| `/lr.debug.trace` | Toggle a CSV position/state logger (`elapsed_s,x,y,z,facing_deg,on_ladder,sprinting,ducked`) on the nearest survivor, written to `LivingRust/traces/trace_<alias>_<timestamp>.csv`. Built specifically to catch oscillation/stall bugs that don't show up in the log. |
| `/lr.debug.despawnall` | Kills every survivor this instance tracks (scoped, not global — see bug list). |

`/lr.follow` and `/lr.climb.monument` both now transparently detour through ladders mid-path when the target/goal is on a different level — no separate command needed.

---

## Architecture added this arc

- **`ClimbState` / `AdvanceClimb` / `BuildClimbState`** (`LivingRust.Commands.cs`) — the shared per-tick climb state machine, used by `/lr.climb`, `/lr.follow`'s auto-detour, and `/lr.climb.monument`. Handles the approach-to-mount-point phase, the vertical climb (clamped `ClimbSpeed`, targets `ladder.Top/Bottom.y ± ClimbOvershoot`), and the dismount step.
- **`npc.SendModelState(true)`** — the single most important discovery of this arc. Setting `modelState` fields (onLadder, sprinting, ducked, etc.) on a connectionless bot does *nothing* by itself; a real player's client tick silently calls `SendModelState` for them, but our bots never send a tick. Every `modelState` mutation site now explicitly calls it.
- **`BasePlayer.Die()` vs `.Kill()`** — `Kill()` alone left despawned bots invincible/frozen (wrong teardown path for a `BasePlayer`). Fixed to call `Die()` first, `Kill()` as a fallback.
- **`DespawnAllBots()` scoping** — originally iterated Rust's *global* `BasePlayer.bots` registry (every bot-tier NPC on the whole map), not just ours. Rescoped to `_engine.SurvivorManager.GetAll()`.
- **NavigationManager additions**: `MaxStepDownHeight` (symmetric to step-up, fixes gliding off elevated edges), `MaxWalkableSurfaceAngle` (filters out diagonal structural beams misread as steppable surfaces), `NonSteppableColliderNames` (hard-excludes the Powerline tower's huge compound frame collider `powerline_a (1)`, which spans the entire tower's Y range and poisons probes everywhere near it), `DuckClearance` (fallback headroom check so bots can duck under low beams instead of hard-blocking), `ObstacleLayerMask` gained the `Ragdoll` layer (corpses are solid to real players but weren't detected by either bot collision check).
- **`PathFollower.PreferredSidestepAngle`** — sidestep direction memory. Without it, `TryFindSidestep` recomputed candidates fresh every tick with no memory, and two roughly-equal candidate angles would flip-flop tick to tick, producing visible jitter/spinning.

---

## Key bugs found & fixed this arc (chronological)

1. **No climb animation** → root-caused to the `SendModelState` issue above; fixed for both directions.
2. **`/lr.stop` mid-climb left the bot frozen in a climbing pose** → `ReleaseMovementState` now clears `onLadder` too.
3. **Random crouch during climbs** → leftover `ducked`/`ducking` state from the approach phase was never reset when the vertical climb began; fixed.
4. **Reload orphaned every active bot** → `Unload()` never cancelled timers or despawned survivors; fixed, plus the `Die()` vs `Kill()` fix above.
5. **`/lr.debug.despawnall` count going *up* between calls with no spawns in between** → my first explanation (blamed a spawn keybind) was wrong; user pushed back with a precise counter-example, and re-reading the log found the real cause: global bot-registry scope, not ours. Rescoped.
6. **Corpses had collision for the real player but not for bots** → `ObstacleLayerMask` never included the `Ragdoll` layer.
7. **Follow-detour silently stalled after climbing 2 ladders, no log output at all** → three distinct causes found in sequence over several tests: (a) ladder search radius too tight for the real gap between segments — widened; (b) same-level "no path" cases had no local-stepping fallback on this monument's fragmented baked navmesh — added one; (c) not actually a bug, just under-logged edge cases.
8. **`/lr.climb.monument` immediately failed from raw ground level** → the approach-point math assumed the survivor was already standing on the platform a ladder serves; from ground level the math was inverted (target ended up *above* and unreachable). Added a reach-tolerance check plus a "walk toward the ladder's own position" fallback for the not-yet-at-height case.
9. **Bot rammed the tower's own structural frame, reported absurd step heights** → `powerline_a (1)` is one compound collider spanning the whole tower; the step probe took whatever it hit first in a column with no filter for orientation or known-bad colliders. Fixed via the surface-angle filter + explicit collider exclusion (two passes — the angle filter alone wasn't sufficient).
10. **Stuck needing to duck under a low crossbeam near the peak** → headroom check only tested standing height with no fallback. Added a duck-height retry.
11. **Violent facing-flips / jitter while walking toward a ladder** → I initially misread a trace as "clean," and was corrected by the user re-testing and seeing it snag live. Re-reading the *same* trace at finer granularity (not a new capture) found real rapid-alternation jitter my first pass missed by only checking start/end state. Root cause: no sidestep-direction memory; fixed via `PreferredSidestepAngle`.
12. **A second, slower oscillation (10+s forward-then-reverse walk cycles)** → distinct from #11, found in a later trace after the first fix. Root cause: the sidestep-success code path never checked waypoint-arrival distance or advanced the path's corner index, unlike the normal step path — so the bot kept re-aiming at a stale waypoint behind it. Fixed by mirroring the same arrival-check logic into the sidestep branch.

**Process lesson from #5 and #11, worth remembering going forward:** twice this arc I gave a confident explanation that turned out wrong, and both times the fix was the same — when the user pushes back with a specific counter-example, re-derive from the actual log/trace data rather than defending the first guess. A glance at start/end state is not enough to rule out a slower or subtler pattern hiding in the middle of a trace.

---

## Confirmed working end-to-end (Powerline tower, the small roadside test structure)

Full autonomous climb from raw ground level to several levels up: ramp → `Ladder_1` → platform → `Ladder_2` → platform → ramp-to-ramp crossing → `Ladder_3`, all with correct animation, no crouch glitches, no jitter, via both `/lr.follow` (chasing a live player through the same route) and `/lr.climb.monument` (unattended). This took roughly a dozen fix/test cycles (items 1–12 above) to get solid.

---

## Open as of tonight (2026-08-07) — NOT yet fixed

### New: three bugs on a different monument type (a watchtower)
Tested via a survivor "DirtyReaper," repeatedly climbing up/down a watchtower ladder, then following:

1. **Climb-down animation doesn't apply** — bot "just floats down" instead of showing the climb pose. Not investigated yet.
2. **Climb-down stops ~3/4 of the way, thinks it's finished** — still 1-2m above real ground. **Partially confirmed from the log**: three separate climb-down attempts all bottom out around y=40.09 and log "reached the top of the ladder / finished climbing" there every time. Leading hypothesis, not yet confirmed by re-reading code: either this ladder's `LadderInfo.Bottom` is computed from the wrong reference point, or the `ClimbOvershoot` constant (tuned on the Powerline tower) doesn't generalize to this ladder's proportions.
3. **Walked off the watchtower's edge and glided down at ~45 degrees toward the player** instead of climbing back down the same ladder, when told to follow after reaching the top. Not investigated yet. User's framed end-state: "I need to climb down the same ladder I climbed up," not glide off the platform edge.

### Still open on the Powerline tower itself
Re-tested `/lr.climb.monument` after fix #12 above: the bot no longer gets permanently stuck, but still doesn't route up the ramp correctly — a screenshot shows it standing under the platform, left of and not on the ramp, going back and forth. Consistent with a standing, still-unconfirmed hypothesis: the "walk toward the nearest ladder" fallback aims at the ladder's raw horizontal position, which can point toward the wrong side of the tower's dense base geometry rather than toward the actual ramp/stairs entrance.

---

## Proposed next step: stop generalizing, hardcode authored monument routes

After a *fourth-plus* distinct bug across two monuments all tracing back to the same root cause — generic local step/sidestep logic aiming at raw target geometry instead of the real walkable route — the user proposed, and I agreed, a bigger architecture change instead of continuing to patch the generic heuristic:

**Author fixed waypoint routes per known monument type** (ground → ramp → ramp → ladder mount → ... → next level), keyed by monument prefab name (monument-type detection already exists via `/lr.monuments`/`/lr.monument.where`). `StartClimbingMonument` (and the follow-detour) would check for an authored route first and walk it using the exact same `AdvanceAlongPath`/`TryGetNextStep`/`ClimbState` primitives that are already proven solid — only the "where to aim" decision changes. Falls back to today's generic nearest-ladder heuristic for any monument without an authored route, so nothing already working regresses.

**Not yet implemented** — this is where the next session should start, alongside finishing the three watchtower bug diagnoses above. Waypoint coordinates for both known monuments (Powerline tower, the watchtower) can likely be pulled straight from existing debug scans/traces rather than re-scanning from scratch.

---

## Note on where session memory lives now

Starting tonight, session summaries like this one live here (`G:\Claudes conversation history\`) instead of the Claude Code auto-memory folder under `C:\Users\lukes\.claude\projects\G--\memory\`, at the user's request, to avoid context being split across two locations. The C:\ folder still exists with the same information (up to and including tonight) but won't be added to going forward — treat this file (and future dated files in this folder) as authoritative for catching up on project history.
