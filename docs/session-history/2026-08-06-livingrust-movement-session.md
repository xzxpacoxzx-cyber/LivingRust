# LivingRust — Movement & Pathfinding Session Summary

**Date:** 2026-08-06
**Project:** `G:\Projects\TemplateProject` (Carbon plugin, live test server under `Rust\server\`, identity "LivingRust")
**Scope this session:** taking bot movement from a straight-line walk to real navmesh pathfinding + local obstacle handling, and testing it against increasingly hard terrain (rocks, trees, junkpiles, vehicles, a tall climbable structure).

Paused deliberately before implementing ladder traversal — that's the agreed next step when this resumes.

---

## Current command reference

All commands have a chat version (`/lr.xxx`) and most have a console/bindable version too (`bind <key> lr.xxx`), useful because opening chat freezes your view angle at the moment you press Enter, which makes short-range aim-based commands unreliable from chat.

| Command | What it does |
|---|---|
| `/lr.spawn` | Spawns a real, connectionless `BasePlayer` survivor near the caller. |
| `/lr.follow` | Nearest spawned survivor follows the caller: walks normally, sprints to catch up if it falls >6m behind, drops back to walking within 3m, stops ~1.5m away. Routes around large obstacles via navmesh, steps over/around small ones locally. |
| `/lr.walk` | Nearest survivor walks to the nearest water and stops at the shoreline. |
| `/lr.walk.monument <name>` | Nearest survivor walks to the nearest monument whose name contains `<name>` (case-insensitive). Aims at the closest point on the monument's bounds, not its center. |
| `/lr.stop` | Cancels whatever the nearest survivor is doing. |
| `/lr.monuments` | Lists every monument's display name on the map. |
| `/lr.monument.where <name>` | Lists every monument matching `<name>` with exact coordinates and distance from you, nearest first — use this before `/lr.walk.monument` if a name might match multiple instances (e.g. 4 "Substation"s on this map). |
| `/lr.debug.look [all]` | Raycasts from your view; reports the nearest *solid* (non-trigger) hit by default, or the full stack of colliders along the ray with `all`. Chat version is aim-angle-unreliable at close range (see above) — bind the console version for that. |
| `/lr.debug.nearby` | Reports every unique collider within 5m of you regardless of look direction — use this when `debug.look`'s aim is the problem. |

---

## Architecture as it stands

- **`Navigation\NavigationManager.cs`** — pure navigation logic, no Carbon/plugin dependencies:
  - `TryCalculatePath` — wraps `Rust.Ai.Gen2.RustNavMesh.CalculatePath` (Rust's own baked navmesh, same one vanilla NPCs/animals use). This is the **strategic** routing layer — handles going around anything solid/large enough to be excluded from the walkable mesh at bake time (buildings, big rock formations).
  - `TryGetNextStep` — the **tactical** layer, evaluated for each straight-line segment between navmesh waypoints. Measures the actual obstacle surface height via a small cluster of raycasts (not `SphereCast` — that silently ignores non-convex mesh colliders, which caused bots to end up embedded in irregular rocks). Steps up onto anything within `MaxStepUpHeight` (1.3m, bumped from 1.0m as an experiment for junkpile-height clutter), blocks on anything taller. Also checks head-height clearance (`HeadClearance`, 1.7m) separately from step height, since conflating the two caused short objects near 1m to false-block.
  - `TryFindNearestWater` / `IsWater` — ring search + `WaterLevel.GetWaterLevel` (not the raw `WaterMap`, which misses ocean height entirely).
  - `TryFindNearestMonument` / `FindAllMonumentMatches` / `GetMonumentNames` — wraps `TerrainMeta.Path.Monuments`.

- **`Plugin\LivingRust.Commands.cs`** — orchestration: `StartWalking`/`StartFollowing` run a `timer.Every` tick loop driving a `PathFollower` (holds the current `RustNavMeshPath`, which corner it's walking toward, repath bookkeeping). `AdvanceAlongPath` is the shared per-tick logic: repaths periodically (~1s, or immediately if the destination moved >3m), walks the current segment, handles being blocked by **sidestepping** (trying a fan of alternate headings — trees/junkpile scatter/other players aren't part of the baked navmesh, so repathing alone won't route around them), avoids other players/bots (`IsBlockedByOtherPlayer`, exempting the current follow target), and reduces to crawl speed (`BasePlayer.IsWounded()` → 0.72 m/s, matching real player crawl speed) when downed.

- **`Plugin\LivingRust.Debug.cs`** — the debug/lookup tooling above.

**Important:** our own `LivingRust.Core.Logger` (`Console.WriteLine`-based) never actually reaches any log file readable from outside the game process — discovered this the hard way. All plugin-side logging now goes through Carbon's own `Puts()`, which does reliably land in `Rust\server\LivingRust_log.txt`.

---

## Key bugs found & fixed this session (roughly chronological)

1. **Occlusion-system crash on spawn** — bot `userID` needs to be under 10,000,000 (`BasePlayer.IsBot`) or Rust's server-occlusion bookkeeping crashes for a connectionless player.
2. **Nametag showed the numeric ID** — `displayName` must be set *before* `Spawn()`; `ServerInit()` (called inside `Spawn()`) stomps it back to the ID string if `userID == 0` at that point, and the first network snapshot goes out during that same call.
3. **Water undetected** — raw `TerrainMeta.WaterMap.GetHeight` only reflects locally-baked rivers/lakes; ocean height only appears via `WaterLevel.GetWaterLevel`'s topology-aware logic.
4. **Trees/logs phased through** — wrong obstacle layer mask; built it up empirically via `/lr.debug.look` rather than guessing: `Terrain, World, Construction, Default, Tree, Vehicle World, Vehicle Detailed`.
5. **Bot ended up embedded in some rocks** — `SphereCast` silently ignores non-convex mesh colliders (real Unity limitation); replaced with a small cluster of plain raycasts.
6. **Cardboard-box-height objects wrongly blocked** — the "is there a wall" headroom check and the "how tall is this obstacle" check both used the same reference height; separated into `MaxStepUpHeight` (feet) vs `HeadClearance` (head).
7. **Movement looked "ridiculously fast"** — turned out to be the Y coordinate snapping instantly (up to 1m in one 0.05s tick) when stepping onto stacked junkpile clutter, not actual horizontal speed. Added `MaxClimbSpeed` clamp so climbing looks smooth.
8. **Frozen mid-crouch, later found "invincible"** — `modelState.ducked`/`ducking` never got reset when movement stopped entirely; added explicit reset (`ReleaseMovementState`). Root cause of the invincibility specifically was never fully confirmed — noted as a loose end.
9. **Bots stopped working through trees/junkpile clusters until the player repositioned** — individual trees/junkpile props aren't part of the baked navmesh, so repathing to the same target kept producing the identical blocked route. Added sidestep-around-obstacle logic.
10. **Bots phased through each other and the player** — `MovePosition` bypasses Rust's normal physics collision entirely; added an explicit other-player proximity check before each step.
11. **Ramp climbing stalled partway up** — the big one. Step-up height was computed relative to the *coarse terrain heightmap* (always ≈ ground level) instead of the bot's actual current elevation. Fine near the ramp's base, but the higher it climbed, the more "height above sea level" accumulated even though each real step was small — until it crossed `MaxStepUpHeight` and wrongly called the ramp unclimbable. Fixed by anchoring both the obstacle probe and the height comparison to `current.y`.

---

## Confirmed-working validation

Real wildlife (a boar) attacked and downed a bot into the genuine wounded/crawling state, with **zero integration code written for it** — pure validation of the original architectural bet (real connectionless `BasePlayer`, not a scripted NPC — see memory `livingrust-bot-embodiment`). Every other Rust system (combat, wounding, and eventually scientist AI) treats the bot as a legitimate player automatically.

---

## Scope clarification from this session (important for future work)

Lucas explicitly said the bot does **not** need monument-specific scripting ("it doesn't specifically need to be scripted in this instance to 'powerline_a→e'"). The goal is generic capability:

- Navigate *any* monument/POI generically (climb, route around, traverse).
- Loot generically — find nearby lootable containers (barrels, crates) and interact with them, regardless of which specific monument they're in.

This session stayed scoped to movement only, per his direction ("this is purely just to test movement, so let's stick to that"). Looting is future work, and when it happens it should be generic, not per-monument-named.

---

## The structure we were testing when we paused

Not a registered monument (confirmed via `/lr.monument.where substation` — all 4 Substations were 392–586m away, nowhere near it). Root object name: **`powerline_a`** (layer 16/World, no `BaseEntity` — a static prop, not a networked entity). Likely a small unlabeled procedural structure (the `MonumentZiplineDismountPoint` name suggests it's built from monument-prefab pieces even though it doesn't register as a map-visible monument).

Known sub-parts from `/lr.debug.nearby` scans:
- `Lvl0PlatformA`, `Lvl0PlatformStairs`, `Lvl1PlatformA`/`B`, `Lvl2PlatformA`/`B`, `Lvl3PlatformB`, `Lvl3PlatformRamp`, `Lvl4PlatformA`, `Lvl4PlatformRamp`, `Lvl6PlatformA` — mostly layer 0 (Default), a couple on layer 16 (World).
- `Ladder_1`, `Ladder_2`, `Ladder_3` — layer 16 (World), each paired with a `Ladder Trigger` on layer 18 (Trigger).
- A `PreventBuilding` zone (layer 29, as expected — not a real obstacle, already excluded from our obstacle mask).

The ramp/platform climb now works (fixed in bug #11 above). **Ladders are the unimplemented piece** — Rust's ladder climbing is a distinct mechanic (holding a movement key against a ladder trigger) that our step-up heuristic doesn't do at all. This is where we pick back up.

## Next step (when resumed)

Figure out ladder traversal. Rough starting ideas, not yet evaluated:
- Check whether the baked navmesh includes an off-mesh link across the ladder (some Rust monuments bake these for NPCs) — if so, `RustNavMeshPath.corners` might already produce a waypoint on the far side, and the question becomes how to *animate* the vertical traversal, not how to *path* it.
- If no navmesh link exists, will need custom logic: detect a `Ladder Trigger` in the way, and drive a controlled vertical ascent along the ladder's line (probably using `MaxClimbSpeed`-style clamped vertical movement, similar to the ramp fix, but explicitly following the ladder's geometry rather than a stepped-up surface).
- Worth checking the decompiled `Ladder`-related entity class in `Assembly-CSharp.dll` (already have a full decompile cached from this session) for how real players' ladder-climbing state machine works, before guessing at the mechanic.
