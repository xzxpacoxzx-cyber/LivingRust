# LivingRust — Carbon Update Recovery & Monument Routes Session Summary

**Date:** 2026-08-07 (later the same day as `2026-08-07-livingrust-ladder-climbing-session.md` — read that one first for full context on the ladder-climbing arc this continues)
**Project:** `G:\Projects\TemplateProject` (Carbon plugin, live test server under `Rust\server\`, identity "LivingRust")
**Scope:** unblocking the dev server after a Rust wipe-day update broke Carbon, fixing the two ladder bugs left open from the prior session (now confirmed cross-monument and root-caused precisely), finding and fixing a fresh ramp-oscillation bug introduced by this session's own work, and then landing the architecture change proposed at the end of the prior session: authored waypoint routes for monument-like structures, now actually implemented for the Powerline tower.

---

## Part 1: Carbon/Rust version mismatch (not a plugin bug)

Session opened with the server refusing to boot — Carbon hook-parsing errors (`Signature for 'X.Y' not found`, null refs) on launch.

**Root cause:** Carbon's production build only self-updates on a Rust protocol change (confirmed via carbonmod.gg docs). 2026-08-06 was Rust's monthly force-wipe Thursday; Carbon auto-updated itself (2.0.253.0 → 2.0.256.0, `SelfUpdating.Enabled: true` in `carbon/config.json`) to match the new protocol, but the local `RustDedicated.exe`/`Assembly-CSharp.dll` were still the pre-wipe build (buildid `24253458`, frozen since 2026-08-05). Carbon's new hooks referenced game methods that didn't exist in the stale assembly.

**Fix:** updated the Rust dedicated server via the bundled `Rust\steam\steamcmd.exe` (App ID 258550) to the current build (buildid → `24587531`, ~5.9GB install). Chosen over rolling Carbon back, since keeping the game current is the durable fix and the plugin's own decompile-based reverse-engineering work isn't tied to a specific frozen build.

**Side effect of the game update:** `Rust.Ai.Gen2.RustNavMesh` (used for `CalculatePath`/`AllAreas`) was renamed to `RustNavMeshHelpers` in the new build — confirmed via PowerShell reflection over the updated `Assembly-CSharp.dll`, not guessed. One-line fix in `NavigationManager.TryCalculatePath` (`Navigation\NavigationManager.cs:314`). `RustNavMeshPath` (the path object type) was unaffected, only the static helper class.

**Process note:** launching the server programmatically (`cmd /c start` from Git Bash, then a plain `run.bat` invocation from both Git Bash and PowerShell) failed twice on cwd/PATH resolution quirks — ended up asking the user to launch it manually, which is also the right call long-term since the user's workflow needs the interactive console (`c.reload`, chat commands) that a headless background job wouldn't provide.

---

## Bugs found & fixed this session

1. **Ladder climb-down stopping 1-2m short of real ground, logging "finished" anyway** (carried over as *open* from the prior session, now fixed). Root cause: `ladder.Bottom.y` is just the `TriggerLadder` collider's own `bounds.min.y`, which doesn't reliably reach the real floor on every ladder (it happened to on the Powerline tower, not on the Sulfur Quarry watchtower). The descent target was a fixed `ClimbOvershoot` (0.15m) offset from that wrong baseline, *and* the final dismount step silently discarded a `Blocked` `StepResult` and logged success regardless, masking the failure. Fixed by adding `NavigationManager.TryFindGroundBelow` (same raycast-cluster pattern as `TryGetNextStep`) and having climb-down target real detected ground, offset laterally via the mount-approach direction so the probe doesn't clip the ladder's own rungs. Old formula kept only as a fallback if the probe finds nothing.

2. **Powerline tower `/lr.climb.monument`/`/lr.follow` misrouting near the ramp entrance** (carried over as *open*). Fixed via `TryStepTowardKnownRampEntry` + `NavigationManager.TryFindNamedStructure` — a new name-based landmark lookup (search by exact GameObject name within a radius, same `Physics.OverlapSphere` pattern as ladder search). Targets the ramp's actual sub-object (`Lvl0PlatformStairs`, named directly from a debug scan) instead of the raw ladder position that was cutting through the tower's base structure. Deliberately name-based, not coordinate-based, so it survives the structure being relocated by a map regen (validated later this session — see below).

3. **New bug: ~160-170° heading oscillation ("90 degree takeoff") right after reaching the ramp.** Introduced by fix #2 above. The ramp-arrival check (`< 2m of the ramp's center`) had no hysteresis: once inside 2m it handed off to the old nearest-ladder step, which aimed at a point far enough away to immediately exit the 2m ring, flipping control back next tick. Fixed by making the ramp landmark a one-way latch (`ref bool reachedRampLandmark`) — once reached, never re-targeted for the rest of that movement session, instead of re-checking a live distance threshold every tick.

4. **Root cause of "stops dead after finishing a ladder, no further log output":** `TryFindNearestLadder` only ever returned the single closest `TriggerLadder`, with direction (`LadderGoesRightWay`) checked *after* picking it. Right after dismounting, the ladder just climbed is always nearest (confirmed via a captured `/lr.debug.nearby` scan: 0.3-2.9m away) — closer than the real next ladder (confirmed via the same scan: Ladder_2 was 8.1m from Ladder_1's dismount point, well inside the 15m search radius). The nearest-overall ladder gets correctly rejected for going the wrong way, and the actually-reachable next one is never considered. Fixed by pushing the direction filter into the search itself (`TryFindNearestLadder` now takes an optional `Func<LadderInfo, bool> filter`, applied per-candidate before distance comparison, not after). Both `TryStepTowardNearestLadder` and `TryBuildFollowClimb` updated to pass `candidate => LadderGoesRightWay(candidate, origin, climbingDown)`.

5. **Diagnostic gap, not a behavior bug:** found (while chasing #4) that both `TryBuildFollowClimb` (vertical-gap-too-small early return) and `AdvanceAlongPath` (repath-fails-and-destination-too-far-for-local-stepping branch) could give up completely silently, indistinguishable from a genuine hang. Added `Puts` logging to both, naturally throttled (cooldown-gated / once-per-repath respectively) rather than per-tick.

**Process note, same lesson as last session:** the "stopped after finishing a ladder" investigation was initially misdiagnosed as a `/lr.follow` player-position issue, because `TryBuildFollowClimb`'s log messages always say "following" regardless of caller (shared code, worded for its more common caller — documented in its own doc comment, which got missed on first read). The user corrected this ("I was using lr.climb.monument") and the real cause (bug #4 above) was found immediately once the actual destination math was used instead of an assumed live-player position.

---

## Validated end-to-end this session

A captured trace (`trace_AngryBuilder_20260807_113848.csv`) shows `/lr.climb.monument` climbing **both** Ladder_1 and Ladder_2 cleanly (smooth mount, steady climb speed, clean dismount, no oscillation) after fixes #3 and #4 above — first time it's gotten past a single ladder segment. It continued onto a third level (reached y≈35 from a y≈22 start) before running out of *mapped* route and stalling at a new, not-yet-investigated spot (`-165.88, 31.89, 156.29`) — expected, since that's past what's been scanned, not a regression.

Also confirmed live: the Powerline tower relocated on this map (protocol-bump regenerated the world — same seed, different procgen output, consistent with the `.sav` filename's protocol suffix forcing a fresh generation). The name-based ramp landmark (fix #2) kept working without any code change once the user found the tower's new location, which is exactly the robustness that design choice was for.

---

## Architecture change landed this session: authored monument routes

This was proposed at the end of the prior session and agreed to at the start of this one; now actually implemented in the new file **`Plugin\LivingRust.MonumentRoutes.cs`**:

- **`MonumentWaypoint`**: either `WalkToNamed(string objectName)` (reuses `TryFindNamedStructure`) or `ClimbNearestLadder` (reuses `TryFindNearestLadder` + `BuildClimbState`/`AdvanceClimb` — the same direction-filtered search from fix #4, and the same climb machinery already proven solid).
- **`KnownMonumentRoutes`**: `Dictionary<string, MonumentWaypoint[]>` keyed by the structure's *root* GameObject name (`"powerline_a"` — confirmed via debug scans as the ultimate parent of every sub-collider on the tower), not a registered monument name, since this structure isn't one.
- Waypoints are numbered 1..X per the user's framing. Reaching the last one **retraces the same list back to the first** (climbing down wherever it climbed up) rather than stopping at the top — the user's explicit design ask, so the survivor actually leaves the monument the same way it came.
- `/lr.climb.monument` (`RunClimbMonument`) checks for a known route first (`TryFindMonumentRoute`, a coarse 50m proximity scan for any known root name); only falls back to the old generic goal-driven `StartClimbingMonument` heuristic if no authored route is found nearby — so nothing already working regresses.
- **Current route content**: `["powerline_a"] = [ramp ("Lvl0PlatformStairs"), ClimbLadder, ClimbLadder]` — i.e. ramp → Ladder_1 → Ladder_2, matching exactly what's been confirmed working today. Deliberately not extended further yet.
- Built, deployed (0 compile errors), **not yet live-tested** — the session wrapped up right after landing this, before running `/lr.climb.monument` again to confirm the route (and especially the retrace-back-down half, which has never been exercised) actually works end-to-end.

### Deferred design decision, not yet acted on

Discussed where a future generic "notice and loot nearby containers" behavior should hook into an authored route: **at waypoint boundaries only** (after finishing a leg, before starting the next, while standing on solid ground), not by interrupting mid-climb or mid-step. Reasoning: the `ClimbState` machinery has no pause/resume primitive, and forcing one in would reintroduce the exact class of fragility this whole session was spent fixing. Not implemented — looting itself is still unscoped future work (per the 2026-08-06 movement session: should be generic across any monument, not hardcoded).

---

## Open / next steps for when this resumes

1. **Test the monument-route system live** — hasn't been run yet. Specifically worth watching: does the retrace-down half work as cleanly as the ascent did (climbing down Ladder_2 then Ladder_1, dismounting to the actual ramp base)?
2. **Extend the route past Ladder_2.** The trace shows the survivor reaching a third level (y≈35) then descending partway (to y≈32) along what's probably a connecting ramp before stalling at `(-165.88, 31.89, 156.29)`. Worth a `/lr.debug.nearby` scan there to find the next named landmark (a `Lvl2PlatformRamp`-style object or `Ladder_3`, per the original tower's naming pattern from the 2026-08-06 session) and append it to the `KnownMonumentRoutes["powerline_a"]` array.
3. **Bug #3 from the prior session (glide off a watchtower edge instead of climbing down)** — recommendation was to defer and re-check after the authored-route work landed, on the theory it's the same "generic step-down heuristic doesn't know about the sanctioned route" root cause as the ramp/ladder bugs fixed this session. Not re-tested yet.
4. **A second monument type** worth authoring a route for once one's identified/scanned (Sulfur Quarry watchtower is the other structure this arc has touched, and still has its own two open bugs from the prior session — climb-down animation not applying, and no confirmed fix for its climb-down grounding specifically since fix #1 above was only validated on the Powerline tower).
5. The user explicitly framed monument-type structures like this (Powerline towers, watchtowers) as "monuments" colloquially even though they know some (Powerline) aren't registered Rust monuments the way Dome or Military Tunnels are — worth keeping that distinction in mind (registered monuments are discoverable via `/lr.monuments`; structures like Powerline towers need the name-based `TryFindNamedStructure` approach instead).
6. **Perception (line-of-sight vs. omniscient) is a real gap, explicitly deferred.** Confirmed via a code check that `StartFollowing` currently reads `target.transform.position` directly every tick (`Plugin\LivingRust.Commands.cs:974`) with zero raycast/vision-cone gating - fully omniscient, works through walls/trees/across the map. Not an oversight: `/lr.follow` was scoped from the start (2026-08-06 session) as a movement-testing tool, not the real AI, and its omniscience has been genuinely useful this session for isolating movement bugs from perception bugs. **User's idea for implementing the real thing, explicitly flagged to revisit later:** rather than building detection/LOS from scratch, adapt the existing line-of-sight/combat-detection logic vanilla Rust already ships for its own NPCs (scientists, tunnel dwellers, monument AI) - likely reachable via the same decompiled `Assembly-CSharp.dll` reflection approach already used elsewhere in this project (e.g. the `ModelState`/`SendModelState` and `RustNavMesh`→`RustNavMeshHelpers` discoveries this session) rather than reinventing raycast/FOV-cone logic independently. Rough shape discussed (not committed to): periodic (not per-tick) raycast from eye height using the existing obstacle layer mask, an FOV cone check, and a "last known position" the survivor pursues for a bit after losing sight rather than instantly forgetting. Should sit as a layer on top of the existing movement primitives (`StartFollowing`/`AdvanceAlongPath`), not replace them.
