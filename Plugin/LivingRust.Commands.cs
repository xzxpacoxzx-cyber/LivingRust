using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Core;
using LivingRust.Models;
using LivingRust.Navigation;
using Oxide.Plugins;
using Rust.Ai.Gen2;
using UnityEngine;
using UnityEngine.AI;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Matches BasePlayer's own walkSpeed/runSpeed constants (2.8f/5.5f) -
    // our earlier guesses (4/7) ran visibly faster than a real player.
    private const float WalkSpeed = 2.8f;
    private const float RunSpeed = 5.5f;

    // 70% reduction while swimming (2026-08-15, Lucas's own explicit
    // request), waived entirely for a survivor actually wearing diving
    // fins (real Rust item "diving.fins" - confirmed via
    // Bundles\AssetSceneManifest.json, the same real clothingWaterSpeedBonus
    // GetSpeed() already accounts for on a real player).
    private const float SwimSpeedMultiplier = 0.3f;

    private const string DivingFinsShortname = "diving.fins";

    // Hysteresis, not one shared threshold both ways (2026-08-15, later
    // same session) - a single fixed depth caused real, confirmed
    // oscillation: '7824NervousTrooper' died stuck bouncing between
    // swim/walk Y roughly every second for 40+ seconds right where real
    // underwater terrain has a genuine steep slope crossing the old single
    // 1.2f threshold - the survivor's position straddled that exact depth
    // as it moved, flipping the swim decision back and forth, and each
    // flip computed Y via a completely different method (swim-target-clamp
    // vs normal ground-following), producing a real jump every time
    // (confirmed by the same log line: "step down too far onto 'Terrain'
    // (1.31m > 1.30m max)" firing right at the flip point). Entering
    // swimming now requires depth >= EnterSwimDepthThreshold; once
    // swimming, it takes dropping below the much shallower
    // ExitSwimDepthThreshold to go back to walking - a real gap between
    // the two so a position sitting right at ~1.2m depth can't flicker
    // between states tick to tick.
    private const float EnterSwimDepthThreshold = 1.2f;
    private const float ExitSwimDepthThreshold = 0.6f;

    private static float GetWaterDepth(Vector3 position)
    {
        float waterHeight = WaterLevel.GetWaterLevel(position, waves: false);
        float landHeight = TerrainMeta.HeightMap.GetHeight(position);

        return waterHeight - landHeight;
    }

    /// <summary>
    /// Hysteresis (see EnterSwimDepthThreshold's own doc comment) needs to
    /// know whether the survivor was ALREADY swimming, but rather than
    /// threading a new dedicated bool through every caller, this infers it
    /// from previousY itself - a survivor that was genuinely swimming last
    /// tick has a Y sitting roughly waterHeight-WaterSubmersionDepth
    /// (meaningfully below the surface); one that was walking has a Y
    /// close to the real ground height instead. previousY already flows
    /// through every one of these call sites anyway (needed for the
    /// vertical-transition clamp itself), so this needs no new state.
    /// Callers must use the SAME returned value for both
    /// GetSwimAdjustedSpeed and ClampToWaterSurfaceIfSwimming within one
    /// tick (not recompute independently), so the two stay in agreement.
    /// </summary>
    private static bool IsSwimmingNow(Vector3 position, float previousY)
    {
        float waterHeight = WaterLevel.GetWaterLevel(position, waves: false);
        float depth = GetWaterDepth(position);
        bool wasSwimming = waterHeight - previousY >= ExitSwimDepthThreshold;

        return wasSwimming ? depth >= ExitSwimDepthThreshold : depth >= EnterSwimDepthThreshold;
    }

    private static bool HasFlippersEquipped(BasePlayer npc)
    {
        foreach (Item item in npc.inventory.containerWear.itemList)
        {
            if (item.info.shortname == DivingFinsShortname)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Real per-tick movement speed, reduced while swimming unless the
    /// survivor is wearing diving fins - see SwimSpeedMultiplier's own doc
    /// comment. isSwimming is IsSwimmingNow's OWN result for this exact
    /// tick, passed in rather than recomputed here, so this and
    /// ClampToWaterSurfaceIfSwimming can never disagree within one tick.
    /// </summary>
    private static float GetSwimAdjustedSpeed(BasePlayer npc, float baseSpeed, bool isSwimming)
    {
        if (!isSwimming || HasFlippersEquipped(npc))
        {
            return baseSpeed;
        }

        return baseSpeed * SwimSpeedMultiplier;
    }

    /// <summary>
    /// Clamps a candidate step's Y to the real water surface height when
    /// its (x, z) is over water - without this, a "swimming" survivor's Y
    /// just kept following the real seafloor terrain height the exact same
    /// way normal ground-following movement always has (confirmed live via
    /// trace - 'SneakyGoblin81' visibly walking the ocean floor, Y tracking
    /// real underwater terrain contours instead of floating at the
    /// surface), which reads as walking along the bottom, not swimming
    /// across the top. Real depth/diving mechanics (going deliberately
    /// underwater to reach something) are out of scope - this always floats
    /// at the surface whenever possible.
    /// </summary>
    // Static, not recomputed per-position (2026-08-15, Lucas's own explicit
    // simplification request after round 7's fix still wasn't fully
    // stable) - every earlier version of this recomputed the real local
    // water surface height every tick via WaterLevel.GetWaterLevel, which
    // is exactly what let this keep fighting Unity's own continuous
    // underwater drift: a moving/recomputed target is harder to hold
    // steady than a flat one. Assumes the real Rust ocean's own sea level
    // (0) applies map-wide, which is true for the main ocean but wrong for
    // A player's transform origin is at their FEET, not their torso/head,
    // hence the negative offset - clamping straight to the raw water
    // surface height put the feet AT the surface, reading as standing ON
    // TOP of the water (confirmed live).
    private const float WaterSubmersionDepth = 1.1f;

    /// <summary>
    /// previousY is the survivor's real Y from BEFORE this tick - the
    /// locked swim Y is clamped to move at most MaxClimbSpeed's own rate
    /// away from it for the INITIAL transition into swimming, same as
    /// every other vertical transition in this project already respects.
    /// survivor.LockedSwimY (see its own doc comment) is captured ONCE per
    /// swim session, the moment isSwimming first goes true, and reused for
    /// the rest of that swim rather than recomputed every tick (round 7's
    /// bug: a per-tick-recomputed target was hard to hold steady against
    /// Unity's own continuous underwater drift) - a flat GLOBAL constant
    /// (round 8) was simpler but wrong for a lake/river sitting at a real
    /// elevation different from the main ocean's sea level; locking in
    /// PER SWIM SESSION gets both the stability and the correctness.
    /// Cleared back to null the instant swimming ends, so the next entry
    /// (possibly a different body of water) captures fresh.
    /// </summary>
    private static Vector3 ClampToWaterSurfaceIfSwimming(Survivor survivor, Vector3 position, float previousY, bool isSwimming)
    {
        if (!isSwimming)
        {
            survivor.LockedSwimY = null;
            return position;
        }

        if (!survivor.LockedSwimY.HasValue)
        {
            survivor.LockedSwimY = WaterLevel.GetWaterLevel(position, waves: false) - WaterSubmersionDepth;
        }

        float maxVerticalDelta = MaxClimbSpeed * WalkTickInterval;

        position.y = Mathf.Clamp(survivor.LockedSwimY.Value, previousY - maxVerticalDelta, previousY + maxVerticalDelta);
        return position;
    }

    /// <summary>
    /// Unconditional per-tick swim-depth correction, called once at the
    /// very top of every walk/follow tick regardless of which branch the
    /// rest of the tick takes. Unity's own NavMeshAgent moves an enabled
    /// agent's transform continuously, every real engine frame, completely
    /// independent of this project's own 0.05s tick cadence - normally
    /// harmless, but underwater it follows the baked navmesh toward
    /// whatever real seafloor terrain exists there (confirmed live -
    /// 'SneakyGoblin81' visibly walking the ocean floor before any swim
    /// fix existed). The earlier swim-Y corrections only ran inside the
    /// native movement "successful step" branch specifically, so on any
    /// tick that took a different path this frame (fallback, blocked,
    /// no-progress, hand-built taking over), Unity's own drift went
    /// completely uncorrected between OUR ticks and compounded - confirmed
    /// live via trace ('SavageHermit'): Y crept progressively deeper over
    /// an entire 80+ second swim, never stabilizing near the intended
    /// depth. Running this unconditionally, first thing every tick,
    /// bounds the uncorrected window to at most one tick's worth of drift
    /// instead of however many consecutive ticks skipped the correction.
    /// </summary>
    private void CorrectSwimDrift(Survivor survivor, BasePlayer npc)
    {
        bool isSwimming = IsSwimmingNow(npc.transform.position, survivor.Position.y);

        if (!isSwimming)
        {
            return;
        }

        Vector3 corrected = ClampToWaterSurfaceIfSwimming(survivor, npc.transform.position, survivor.Position.y, true);

        npc.MovePosition(corrected);
        survivor.Position = corrected;
        survivor.Character.Position = corrected;

        // See the native-movement branch's identical line for the full
        // reasoning (ModelState.onground defaults true and nothing else
        // clears it) - set here too since this is the ONE correction that
        // runs unconditionally every tick, unlike the other 3 call sites
        // which only fire inside specific branches.
        if (npc.modelState.onground)
        {
            npc.modelState.onground = false;
            npc.SendModelState(true);
        }
    }

    // 20Hz instead of 5Hz - much smoother than the original tick rate,
    // since MovePosition's own tick-history/interpolation has far less
    // distance to cover between updates.
    private const float WalkTickInterval = 0.05f;

    private const float FollowStopDistance = 1.5f;
    private const float CatchUpDistance = 6f;
    private const float ResumeWalkDistance = 3f;

    // Real, prefab-baked NavMeshAgent values read live off a real
    // scientistnpc_junkpile_pistol via /lr.debug.scientistnav - agentTypeID
    // especially matters, since it identifies which of the map's baked
    // navmesh layers this agent shape can actually use, and Unity's default
    // (0, "Humanoid") has no guarantee of matching what Rust itself baked.
    private const int FollowAgentTypeID = -1372625422;
    private const float FollowAgentRadius = 0.5f;
    private const float FollowAgentHeight = 2f;
    private const float FollowAgentBaseOffset = -0.1f;
    private const float FollowAgentAngularSpeed = 120f;
    private const float FollowAgentAcceleration = 8f;
    private const float FollowAgentStoppingDistance = 0.1f;
    private const int FollowAgentAreaMask = 1;

    // How long native movement gets to show real progress (distance to the
    // follow target actually shrinking, not just raw per-tick displacement -
    // same distinction StartWalking's own stuck-detection needed, see
    // WalkMinProgressDistance's doc comment) before StartFollowing gives up
    // on it for this attempt and falls back to the proven hand-built
    // AdvanceAlongPath/climb engine. Short (~3s) since following re-evaluates
    // every tick anyway - no need for StartWalking's full 10s patience here.
    private const int FollowNativeGiveUpTicks = (int)(3f / WalkTickInterval);

    // How long to keep using the hand-built fallback after native movement
    // fails, before giving native another chance - the target will likely
    // have moved on from whatever tripped it up by then. ~5s.
    private const int FollowNativeRetryCooldownTicks = (int)(5f / WalkTickInterval);

    // How far the follow target needs to move before re-issuing
    // SetDestination to the native agent - mirrors RepathMoveThreshold's
    // reasoning for the hand-built path, avoiding a full repath every single
    // 0.05s tick for a target that's barely moved.
    private const float FollowNativeRepathMoveThreshold = 1f;

    // How far StartWalking will snap a loot-approach destination onto the
    // real navmesh before handing it to Unity's SetDestination - see
    // StartWalking's own doc comment for why this matters: GetApproachPoint
    // computes points via raw geometry (container bounds + a small
    // standoff), with zero navmesh awareness, and a live test showed
    // Unity's SetDestination silently refusing nearly every one of those
    // raw points as a target, never even producing a real path to fail on.
    private const float WalkNativeApproachSnapDistance = 3f;

    // How long native movement gets to show real progress on a one-shot
    // /lr.walk-style destination (StartWalking - used by the loot task's
    // approach movement) before falling back to the hand-built engine for
    // the rest of that same walk - unlike StartFollowing there's no later
    // retry, since a single StartWalking call has one fixed destination,
    // not a continuously moving target to keep re-evaluating against.
    private const int WalkNativeGiveUpTicks = (int)(3f / WalkTickInterval);

    // How often the navmesh path gets recomputed - matters most while
    // following a moving target. ~1s at WalkTickInterval.
    private const int RepathIntervalTicks = 20;

    // Force a repath immediately if the destination has moved this far
    // since the path was last computed, rather than waiting out the tick interval.
    private const float RepathMoveThreshold = 3f;

    private const float WaypointArriveDistance = 0.5f;

    /// <summary>
    /// How close to StartWalking's destination before it automatically
    /// eases off from a sprint to a walk, regardless of shouldWalkCarefully -
    /// applies to every walk, not just loot, so any arrival (a container,
    /// a resource node, wherever) reads as a real approach rather than
    /// sprinting flat into it. User-requested for loot specifically first,
    /// generalized here since it's really about arriving anywhere, not a
    /// loot-only concern.
    /// </summary>
    private const float ApproachSlowdownDistance = 3f;

    // How close a NoPath destination has to be before AdvanceAlongPath
    // falls back to direct local stepping instead of giving up - this
    // monument's fragmented NavMesh can fail to path even across an open,
    // walkable platform, so a short-range fallback matters for ordinary
    // same-level following, not just the ladder-approach case it was
    // originally added for.
    private const float NoPathLocalStepRadius = 10f;

    // ~2s of being genuinely blocked (not water) before giving up as stuck.
    private const int MaxConsecutiveBlockedTicks = 40;

    // Minimum amount a survivor's distance-to-destination must shrink in a
    // tick to count as progress for StartWalking's own stuck-detection
    // give-up below. Deliberately measured against the destination, not
    // raw position delta (unlike ClimbMonumentMinProgressDistance, where
    // there's no fixed target to measure against) - a live trace caught a
    // survivor stranded on top of an elevated platform (a tire pile) whose
    // local-stepping sidestepped along the ledge instead of descending
    // (blocked by MaxStepDownHeight), moving a real ~0.15-0.22m every
    // single tick while its distance to the destination never actually
    // shrank. Raw-displacement tracking saw "progress" every tick and
    // never gave up, so the stuck-recovery escalation (wiggle/nudge/
    // teleport) never got a chance to run. Small enough not to flag
    // genuine slow movement (a normal walk step is ~0.14m).
    private const float WalkMinProgressDistance = 0.02f;

    // Fixed-window threshold (2026-08-16), NOT the same role as
    // WalkMinProgressDistance above - real live bug: the outer walk
    // timer's own give-up watchdog and AdvanceAlongPath's local-step
    // watchdog both re-baseline (reset ticksSinceProgress to 0) on ANY
    // improvement, however tiny, since the LAST baseline - a survivor
    // ('SavageNomad994') that inched forward ~3cm every ~9s, then drifted
    // back, could reset the clock forever, burning the entire 200s
    // maxTicks hard timeout in total silence (no Blocked/Stuck/NoPath
    // outcome ever fires) instead of the intended ~10s give-up. The fix
    // isn't a bigger WalkMinProgressDistance (still gameable, just needs
    // patience) - it's holding the baseline FIXED for the entire
    // WalkGiveUpTicks window and only comparing net progress once that
    // window fully elapses, so no amount of small back-and-forth
    // oscillation within the window can reset the clock. 1.5m over a full
    // 10s window is a deliberately low bar (~5% of WalkSpeed's own 28m/10s)
    // - only meant to catch "not really getting anywhere," not flag
    // genuinely slow-but-real progress.
    private const float WalkWindowMinProgressDistance = 1.5f;

    // Same fixed-window fix as WalkWindowMinProgressDistance, scaled down
    // for the native-movement watchdogs' much shorter (3s) window - real
    // live bug confirmed 2026-08-16 ('2SneakyHunter'): the native-branch
    // watchdogs (StartWalking and StartFollowing both) still had the OLD
    // reset-on-any-improvement design even after the hand-built ones were
    // fixed, since native "give up" was assumed to be a fast, low-stakes
    // decision (just "should we fall back"), not something that could get
    // stuck forever. Live evidence proved that wrong - a bot went
    // completely silent (not even the native-fallback log line, meaning
    // native itself never gave up) for 2+ minutes with zero visible
    // movement, matching the identical oscillation shape the hand-built
    // fix already solved elsewhere. 0.4m over 3s is proportionally as
    // generous as WalkWindowMinProgressDistance's own 1.5m/10s bar.
    private const float WalkNativeWindowMinProgressDistance = 0.4f;

    // ~10s of zero real progress before StartWalking gives up rather than
    // silently ticking until the full 200s maxTicks timeout below -
    // previously this exact case produced no log output at all (it trips
    // neither Stuck nor NoPath, since AdvanceAlongPath keeps reporting
    // Progressing), indistinguishable from a genuine hang until a live
    // trace caught a survivor frozen at the same spot for the entire
    // 200s. Matches ClimbMonumentGiveUpTicks's own timing.
    private const int WalkGiveUpTicks = (int)(10f / WalkTickInterval);

    // ~0.5s of consecutive genuine NoPath outcomes (not the softer Stuck,
    // which already just forces a repath) before StartFollowing escalates
    // to the SAME real wiggle/navmesh-nudge/emergency-teleport recovery
    // ladder StartWalkingWithRecovery already uses - live report 2026-08-14:
    // a real trace/log showed combat's own following had NO recovery at
    // all beyond Stuck's plain repath-forcing, so a genuine NoPath (a
    // spawn/target position with no real navmesh coverage nearby, e.g. on
    // jagged jungle terrain) left a bot frozen in place - modelState.
    // sprinting stuck true, position literally never changing - for over
    // 50 real seconds in one confirmed case, until the fight ended by
    // other means entirely (the target simply wandering past pursueRange).
    // Forcing a repath alone (Stuck's own fix) can't help a genuine
    // NoPath, since there's no path to find regardless of which corner
    // sequence gets tried.
    private const int FollowNoPathRecoveryTicks = (int)(0.5f / WalkTickInterval);

    // ~3s of the real path calculation consistently failing (0 corners,
    // PathInvalid) for the same destination before treating it as certain
    // "no path exists" and giving up immediately, instead of continuing to
    // retry local-stepping indefinitely. A live trace caught a single
    // stuck episode near junkpile_j generate ~1,750 consecutive
    // repath-failed ticks (~88 real seconds) before WalkGiveUpTicks's own
    // distance-progress heuristic finally caught it - the local-stepping
    // fallback was genuinely circling the (unreachable, elevated) target
    // rather than standing frozen, so distance-to-destination kept
    // shrinking by just enough, just often enough, to keep resetting that
    // heuristic's counter. TryCalculatePath itself already knew instantly
    // and consistently there was no path; this trusts that direct signal
    // instead of waiting on the slower, foolable proxy.
    private const int RepathFailureGiveUpTicks = (int)(3f / WalkTickInterval);

    // How many ticks (~0.25s) of being blocked before trying to sidestep
    // around whatever's in the way - short dynamic obstacles like a tree
    // trunk or a piece of junkpile scatter aren't part of the baked
    // navmesh, so repathing alone won't route around them.
    private const int SidestepAfterTicks = 5;

    // Candidate headings to try when sidestepping, in order - alternating
    // left/right at increasing angles from the direct heading.
    private static readonly float[] SidestepAngles = { 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f };

    // Matches BasePlayer's own crawlSpeed constant - used instead of
    // walk/run speed while the bot is in the wounded/downed state.
    private const float CrawlSpeed = 0.72f;

    // Caps how fast the bot's Y position can rise/fall in one tick when
    // stepping onto/off an obstacle. Without this, stepping onto a stack
    // of junkpile clutter snapped the full height difference in a single
    // 0.05s tick, looking like the bot was teleporting/popping upward
    // rather than climbing - this is what read as "ridiculously fast".
    private const float MaxClimbSpeed = 3.5f;

    // How close another player/bot can get before being treated as an
    // obstacle to avoid - roughly a player's own collision radius.
    private const float PlayerAvoidRadius = 0.6f;

    // Deliberately slower than WalkSpeed - climbing should read as
    // deliberate, not a straight speed-walk with the model pasted onto a
    // vertical line.
    private const float ClimbSpeed = 2.0f;

    private const float LadderSearchRadius = 4f;

    // How far in front of the ladder's mount face /lr.climb walks the bot
    // before switching into the vertical climb - needs to clear the ladder's
    // own rung collider (which sits just past the trigger, per the two
    // ladders scanned) without also missing the mount trigger volume itself.
    private const float LadderApproachOffset = 0.6f;

    // Climb this far past the trigger's recorded top before dismounting -
    // the trigger top and the platform surface aren't measured as exactly
    // level (roughly 0.7m apart on the segments scanned), so this clears
    // the gap before stepping off.
    private const float ClimbOvershoot = 0.15f;

    // Fallback-only now that climb-down targets a real ground probe (see
    // BuildClimbState) - height above ladder.Bottom.y the probe starts
    // searching down from, generous enough to clear minor terrain bumps
    // even when ladder.Bottom.y itself is a rough guess.
    private const float GroundProbeStartHeight = 2f;

    // How far below the probe's start height to search - wide enough to
    // find real ground even when the TriggerLadder's own bounds are well
    // off (confirmed up to ~2m short on a watchtower).
    private const float GroundProbeSearchDistance = 6f;

    private const float LadderDismountStepDistance = 0.8f;

    private const float LadderMountArriveDistance = 0.3f;

    // ~10s to close what should only ever be a sub-4m gap (LadderSearchRadius)
    // before giving up on the local-stepping approach to the mount point.
    private const int LadderApproachTimeoutTicks = (int)(10f / WalkTickInterval);

    // Minimum vertical gap to the follow target before a stalled path is
    // worth investigating for a ladder detour - bigger than anything the
    // local obstacle step-up handles on its own, so it only fires for
    // genuine level changes (e.g. a monument's disconnected navmesh
    // islands), not ordinary blocked-path retries.
    private const float FollowClimbMinHeightGap = 1.5f;

    // Wider than LadderSearchRadius (4m) - confirmed via in-game testing
    // that consecutive ladder segments on the Powerline tower aren't
    // necessarily adjacent (Ladder_2's dismount point had no Ladder_3
    // within a 5m debug scan, even standing right on top of it), so a
    // follow-detour needs to look further to find the *next* segment.
    // Approach still uses local stepping with no sidestep fallback, so a
    // ladder found near the edge of this radius may still time out during
    // approach if the platform between them isn't clear - untested how
    // often that happens in practice.
    private const float FollowClimbSearchRadius = 15f;

    // Slack allowed above a climb-up ladder's Bottom (or below a climb-down
    // ladder's Top) before treating the survivor as "not at this level yet."
    // Sized to comfortably cover the known legitimate gap (a climb-up
    // ladder's own Bottom sits ~1.2-1.3m below the real deck surface it
    // serves) while still rejecting a ~2m ground-level gap.
    private const float LadderReachTolerance = 1f;

    // How long to wait after a failed/timed-out follow-climb attempt before
    // trying again - stops TryFindNearestLadder (a Physics.OverlapSphere
    // every call) from firing every single tick while genuinely stuck with
    // no usable ladder nearby. ~5s at WalkTickInterval.
    private const int FollowClimbRetryCooldownTicks = (int)(5f / WalkTickInterval);

    // /lr.climb.monument aims at a fixed point this far above the
    // survivor's starting position, rather than a real target - taller
    // than any real monument, so the vertical gap to it stays "keep
    // ascending" for as long as there's more to climb, and only shrinks
    // toward the small-gap cutoff once genuinely near the real top.
    private const float ClimbMonumentGoalHeight = 100f;

    // How many consecutive ticks of zero progress (not climbing, not
    // stepping, no qualifying ladder found) before /lr.climb.monument
    // concludes it's reached as high as it can get and stops - unlike
    // /lr.follow, this is a one-shot goal, not something that should retry
    // forever waiting for a moving target to come back into reach. ~10s.
    private const int ClimbMonumentGiveUpTicks = (int)(10f / WalkTickInterval);

    // Minimum real distance the survivor must move in a tick to count as
    // progress for the give-up timeout above - confirmed via a captured
    // position trace that AdvanceAlongPath can report Progressing while
    // the survivor's actual position never changes (a short, valid, but
    // useless path on this monument's fragmented navmesh). Small enough
    // not to flag genuine slow movement (a normal walk step is ~0.14m).
    private const float ClimbMonumentMinProgressDistance = 0.02f;

    // Hardcoded landmark for the Powerline tower specifically - it isn't a
    // registered monument (confirmed via /lr.monument.where), just static
    // dressing, so there's no generic monument-type lookup to key an
    // authored route off. Generic step/sidestep logic aiming straight at
    // the ladder's raw position instead of this ramp was the root cause of
    // the climb.monument misrouting bug (stood under the platform, left of
    // and not on the ramp, going back and forth) - naming the actual ramp
    // object directly (from the /lr.debug.nearby scan) sidesteps that
    // heuristic entirely for the one structure it's known to fail on.
    private const string PowerlineTowerRampName = "Lvl0PlatformStairs";

    private const float PowerlineTowerRampSearchRadius = 25f;

    // Once within this range of the ramp's own collider center, hand back
    // off to the generic ladder fallback/climb logic - the ramp landmark's
    // only job is getting the survivor onto the ramp itself, not
    // simulating the climb up it.
    private const float PowerlineTowerRampArriveDistance = 2f;

    private static readonly int PlayerLayerMask = LayerMask.GetMask("Player (Server)");

    private readonly Dictionary<Guid, Timer> _activeMovement = new();

    private enum MovementOutcome
    {
        Progressing,
        ReachedDestination,
        BlockedByWater,
        Blocked,
        Stuck,
        NoPath
    }

    // ============================================================
    // Real top-level "genuinely not going anywhere" safety net
    // (2026-09-01, Lucas's own explicit spec, added after the existing
    // per-episode stuck-recovery ladder (wiggle/nudge/teleport/phase,
    // EscalateStuckRecovery in LivingRust.Looting.cs) was confirmed live
    // to sometimes fail to escalate at all - a 250-bot trace caught
    // several survivors repeating the identical "blocked heading" log
    // line continuously for 40+ real seconds with zero wiggle/nudge/
    // teleport ever firing, despite that ladder existing specifically to
    // catch this). Deliberately a SEPARATE, coarser, survivor-level check
    // rather than a further patch to the exact escalation mechanism that
    // was failing - Lucas's own framing: "if the bot genuinely hasn't
    // done anything productive... in 30 seconds or more, the bot is most
    // likely stuck." This doesn't care WHY nothing changed (a genuinely
    // untraced bug in the movement ladder, a resource-gather retry loop,
    // a combat deadlock, anything) - it only watches the two outward
    // signals of "still alive but not living": real position and current
    // task. Runs independently of and alongside the existing ladder, not
    // instead of it - the ladder still resolves the vast majority of
    // stuck episodes well before this coarse net would ever fire.
    //
    // Lowered 30s -> 15s (2026-09-15, Lucas's own explicit ask, live report
    // watching 'LuckyRanger' retry and fail this exact rescue every 30s for
    // over a minute straight with zero progress) - see
    // TryRawNearbyRelocate's own doc comment below for the real fix that
    // actually stopped those retries from failing identically forever;
    // this halves how long a survivor sits stuck before that fix even gets
    // its first chance to run.
    // ============================================================

    private const float LifeStallCheckIntervalSeconds = 5f;

    private const float LifeStallTimeoutSeconds = 15f;

    // Lucas's own literal figure - "moved positions (more than 2 metres)."
    private const float LifeStallMinMoveDistance = 2f;

    // How far around a confirmed dead-end position every survivor (not
    // just the one that got stuck there) avoids picking a candidate
    // destination/node/container for GlobalStallZoneDurationSeconds - see
    // IsInGlobalStallZone, wired into IsInMonumentAvoidZone
    // (LivingRust.MonumentAvoidZones.cs) so every one of that function's
    // existing call sites (resource nodes, containers, corpses, bags,
    // destination rolling) respects it for free rather than needing this
    // threaded through each one individually.
    private const float GlobalStallZoneRadius = 15f;

    // Matches ResourceNodePoisonDurationSeconds (LivingRust.ResourceGathering.cs)
    // for consistency - both are "give every survivor a real cooldown
    // before anyone retries this exact dead end" mechanisms at a similar
    // real-world timescale.
    private const float GlobalStallZoneDurationSeconds = 600f;

    private readonly Dictionary<Guid, (Vector3 Position, TaskType Task, float Time)> _lifeStallSnapshot = new();

    // Real diagnostic (2026-09-19, live report: '7672CrustyStalker' stood
    // frozen inside its own base for over an hour, invisible to every log
    // line the AI normally produces AND to the watchdog above (which
    // deliberately skips wounded/dead/destroyed survivors on the
    // assumption that native Rust mechanics or OnPlayerWound's own forced-
    // death hook - see LivingRust.Hooks.cs's doc comment - already own
    // that state and will resolve it on their own). If that assumption
    // is ever wrong for a specific survivor (the wounded-forces-death
    // hook fails to actually fire, say), the result is exactly this: a
    // survivor permanently invisible to both normal AI and the one system
    // meant to catch "stuck," with nothing anywhere logging why. These two
    // dicts turn that into a real, bounded-volume log line instead of
    // silence - only once a survivor has sat continuously in the skip
    // branch for a full LifeStallTimeoutSeconds window (so a completely
    // ordinary quick death-then-respawn never logs anything), then at most
    // once per LifeStallSkipDiagCooldownSeconds after that.
    private readonly Dictionary<Guid, float> _lifeStallSkipSince = new();
    private readonly Dictionary<Guid, float> _lifeStallSkipDiagLastLog = new();
    private const float LifeStallSkipDiagCooldownSeconds = 60f;

    private Timer _lifeStallWatchdogTimer;

    /// <summary>
    /// Started from OnServerInitialized (LivingRust.Main.cs), stopped from
    /// Unload - same lifecycle every other engine-wide timer in this
    /// project already follows (see StartOnSightDetection's identical
    /// shape, LivingRust.Combat.cs).
    /// </summary>
    private void StartLifeStallWatchdog()
    {
        _lifeStallWatchdogTimer?.Destroy();
        _lifeStallWatchdogTimer = timer.Every(LifeStallCheckIntervalSeconds, RunLifeStallWatchdog);
    }

    private void StopLifeStallWatchdog()
    {
        _lifeStallWatchdogTimer?.Destroy();
        _lifeStallWatchdogTimer = null;
    }

    // Real hitch fix (2026-09-15, live report: "a lot of rubber-banding
    // occurring whilst i run around" - traced to this watchdog itself.
    // Confirmed via log evidence: 121 separate 'genuinely stuck' rescues
    // fired in the exact same real second, and a second burst of ~157
    // clustered within 5 seconds right after a plugin reload. Each rescue
    // can chain up to 16+ physics/ground-probe queries across its three
    // tiers (TryEmergencyTeleport, TryForceRelocateIgnoringCollision's 8
    // attempts, TryRawNearbyRelocate's 8 more) - a 121-bot burst meant
    // 1000+ expensive queries crammed into one tick of Rust's largely
    // single-threaded main loop, a classic hitch that reads as
    // rubber-banding client-side even while all-core CPU% looks moderate,
    // since a single-thread spike doesn't show up clearly in an averaged
    // percentage. The clustering itself is partly self-inflicted - every
    // plugin reload re-baselines every survivor's stall snapshot at the
    // same instant (the reload captures/restores everyone's state
    // simultaneously), phase-locking a big chunk of the population's
    // 15s clocks together afterward.
    //
    // Fix: cap how many ACTUAL rescues this tick performs, not how many
    // survivors it scans - the scan itself (position/task bookkeeping) is
    // cheap, only RescueGenuinelyStalledSurvivor's own physics-heavy chain
    // is expensive. A survivor that qualifies but doesn't get a rescue
    // slot this tick is left completely untouched (snapshot NOT
    // re-baselined) - it's simply still "stuck" and immediately
    // re-qualifies next tick, so a big backlog drains a few at a time
    // across consecutive 5s cycles instead of ever being silently skipped.
    private const int LifeStallMaxRescuesPerTick = 5;

    /// <summary>
    /// Real fairness fix (2026-09-19, live report: '7672CrustyStalker' sat
    /// frozen inside its own base for over an hour with zero rescue
    /// attempts, despite the watchdog visibly firing every 5s the whole
    /// time - live trace of the last ~25min of rescue lines showed a small
    /// recurring set of chronically RE-stalling bots ('995LoudDigger'
    /// rescued 18x, 'QuietScrapper4' 14x, 'GrimRenegade' 11x, etc. -
    /// relocating them clearly isn't fixing whatever's actually wrong for
    /// them, so they trip LifeStallTimeoutSeconds again almost immediately)
    /// eating the entire LifeStallMaxRescuesPerTick budget every single
    /// tick, forever. The old single-pass loop rescued in
    /// SurvivorManager's raw Dictionary enumeration order - the first N
    /// eligible survivors it happened to reach, not whoever had actually
    /// been stuck longest - and .NET Dictionary enumeration order is
    /// stable across ticks (no removals reorder it), so any survivor that
    /// iterates later than that chronic set stayed starved indefinitely
    /// even though it was genuinely eligible every single tick. Two-pass
    /// fixes this at the same per-tick cost (still exactly one scan over
    /// every survivor): collect every genuinely-stalled candidate first,
    /// THEN service the longest-waiting ones first once the whole
    /// candidate list (and therefore the real backlog size) is known - a
    /// real backlog now drains fairly instead of always favouring whichever
    /// bots happen to sit early in the dictionary.
    /// </summary>
    private void RunLifeStallWatchdog()
    {
        if (_engine == null)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        List<(Survivor Survivor, BasePlayer Npc, Vector3 Position, float StalledFor)> candidates = null;

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            BasePlayer npc = survivor.Player;
            Guid characterId = survivor.Character.Id;

            // Dead/wounded/despawned survivors aren't "stuck" in the sense
            // this watchdog cares about - real Rust mechanics (bleedout,
            // the death-screen pause) already own that state, and a
            // wounded survivor's own real crawl speed would otherwise
            // trip LifeStallMinMoveDistance constantly for no reason.
            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead || npc.IsWounded())
            {
                _lifeStallSnapshot.Remove(characterId);

                if (!_lifeStallSkipSince.TryGetValue(characterId, out float skipSince))
                {
                    _lifeStallSkipSince[characterId] = now;
                }
                else if (now - skipSince >= LifeStallTimeoutSeconds
                    && (!_lifeStallSkipDiagLastLog.TryGetValue(characterId, out float lastDiagLog) || now - lastDiagLog >= LifeStallSkipDiagCooldownSeconds))
                {
                    _lifeStallSkipDiagLastLog[characterId] = now;
                    Puts($"life-stall-diag: '{survivor.Character.Alias}' has sat continuously skipped by the watchdog for {now - skipSince:F0}s (npcNull={npc == null}, destroyed={npc?.IsDestroyed}, state={survivor.Character.State}, wounded={npc?.IsWounded()}) - genuinely stuck in a state this watchdog can't rescue from.");
                }

                continue;
            }

            _lifeStallSkipSince.Remove(characterId);

            // A survivor mid-build-replay stands mostly still by design
            // (clearing trees, placing pieces) - rescuing it restarted the
            // whole build elsewhere and left partial structures behind.
            // See _baseBuildStartedAt's own doc comment.
            if (IsBaseBuildInFlight(characterId))
            {
                _lifeStallSnapshot.Remove(characterId);
                continue;
            }

            // Airdrop participants deliberately stand still holding a
            // position near the drop zone until the crate lands.
            if (IsAirdropParticipant(characterId))
            {
                _lifeStallSnapshot.Remove(characterId);
                continue;
            }

            Vector3 currentPosition = npc.transform.position;
            TaskType currentTask = survivor.Character.CurrentTask;

            if (!_lifeStallSnapshot.TryGetValue(characterId, out (Vector3 Position, TaskType Task, float Time) snapshot)
                || currentTask != snapshot.Task
                || Vector3.Distance(currentPosition, snapshot.Position) >= LifeStallMinMoveDistance)
            {
                _lifeStallSnapshot[characterId] = (currentPosition, currentTask, now);
                continue;
            }

            if (now - snapshot.Time < LifeStallTimeoutSeconds)
            {
                continue;
            }

            candidates ??= new List<(Survivor, BasePlayer, Vector3, float)>();
            candidates.Add((survivor, npc, currentPosition, now - snapshot.Time));
        }

        if (candidates == null)
        {
            return;
        }

        // Longest-stalled first, so a real backlog genuinely drains oldest-
        // first instead of re-favouring whoever iterates earliest.
        candidates.Sort((a, b) => b.StalledFor.CompareTo(a.StalledFor));

        int rescuesThisTick = 0;

        foreach ((Survivor survivor, BasePlayer npc, Vector3 currentPosition, float _) in candidates)
        {
            if (rescuesThisTick >= LifeStallMaxRescuesPerTick)
            {
                // Over budget for this tick - leave every remaining
                // snapshot exactly as it is (do NOT touch it) so each one
                // is still "stuck" and gets first refusal on the next
                // tick's budget, now genuinely in longest-waited order
                // instead of needing to wait out a fresh
                // LifeStallTimeoutSeconds window all over again.
                break;
            }

            RescueGenuinelyStalledSurvivor(survivor, npc, currentPosition);
            rescuesThisTick++;

            // Re-baseline immediately at the rescued position - without
            // this, the very next 5s tick would see "hasn't moved/changed
            // task since the OLD snapshot" and could re-trigger a second
            // rescue instantly if the emergency teleport itself failed to
            // find anywhere better to go.
            _lifeStallSnapshot[survivor.Character.Id] = (npc.transform.position, survivor.Character.CurrentTask, now);
        }
    }

    /// <summary>
    /// Reuses TryEmergencyTeleport (LivingRust.Looting.cs) directly rather
    /// than a bespoke relocation - it already tries Survivor.LastKnownGoodPosition
    /// first (literally "a known location it was moving previously at",
    /// Lucas's own phrasing), validated for real ground/clear space, with
    /// a random-direction fallback if that's unavailable or invalid.
    ///
    /// Real fix (2026-09-15) - this function used to just set CurrentTask
    /// back to None on the (wrong) assumption that "the survivor's own main
    /// loop picks a completely fresh goal next cycle." There IS no such
    /// loop: StartLootForResourcesTask's own doc comment confirms the whole
    /// task pipeline is driven purely by callback chains (StartWalking's
    /// onArrived leading into the next step), "no separate task-tick
    /// timer" - nothing anywhere polls for CurrentTask == None. Setting it
    /// and walking away left every successfully-relocated survivor
    /// permanently orphaned from any callback chain, doing nothing at its
    /// new position until the NEXT LifeStallTimeoutSeconds window caught
    /// the same motionless bot and "rescued" it again - live evidence,
    /// 2026-09-15: 'RustyOutlaw878' and dozens of other bots relocating
    /// every ~15s in an endless loop, zero real activity ever logged
    /// between rescues. Now explicitly re-enters the pipeline the same way
    /// every other interrupt-and-resume case in this project already does
    /// (recycling finished, a monument route completing, EndCombat/EndFlee
    /// resuming - see StartLootForResourcesTask's own doc comment) instead
    /// of assuming something else will.
    /// </summary>
    private void RescueGenuinelyStalledSurvivor(Survivor survivor, BasePlayer npc, Vector3 stalledPosition)
    {
        VerbosePuts($"'{survivor.Character.Alias}' made no real progress (moved < {LifeStallMinMoveDistance:F0}m, same task) for {LifeStallTimeoutSeconds:F0}s - genuinely stuck, relocating and blacklisting the area for every survivor.");

        PoisonGlobalStallZone(stalledPosition);

        CancelActiveMovement(survivor);
        CancelActiveAttack(survivor.Character.Id);
        CancelActiveCombat(survivor.Character.Id);
        CancelActiveRecycling(survivor.Character.Id);
        StopExtendedOreSearch(survivor.Character.Id);

        if (!TryEmergencyTeleport(survivor))
        {
            // A genuine dead end can be bad enough that even
            // TryEmergencyTeleport's own real-ground/clear-space validation
            // fails at every nearby candidate (confirmed live: a 250-bot
            // trace's first 3 rescued survivors ALL failed this and would
            // otherwise have kept re-triggering the exact same rescue every
            // 30s forever, position never actually changing). Falls back
            // to a much further, ground-ONLY relocation - same "genuinely
            // last resort, bypasses collision entirely" philosophy this
            // project already uses for StartPhasingToDestination - rather
            // than leaving the survivor to spin in place indefinitely.
            if (!TryForceRelocateIgnoringCollision(survivor, npc))
            {
                VerbosePuts($"'{survivor.Character.Alias}' - stall-rescue's last-resort relocation also failed to find real ground nearby.");

                // True last resort (2026-09-15) - see TryRawNearbyRelocate's
                // own doc comment for why this exists as a genuinely
                // separate tier rather than another variant of the same
                // ground-probed check both tiers above just failed.
                if (!TryRawNearbyRelocate(survivor, npc))
                {
                    VerbosePuts($"'{survivor.Character.Alias}' - even the ground-probe-free raw relocate found nowhere clear within {StuckRawRelocateAttempts} attempts.");
                }
            }
        }

        BasePlayer rescuedNpc = survivor.Player;

        if (rescuedNpc != null && !rescuedNpc.IsDestroyed && survivor.Character.State != CharacterState.Dead)
        {
            StartLootForResourcesTask(survivor);
        }
        else
        {
            survivor.Character.CurrentTask = TaskType.None;
        }
    }

    private const int StallRescueForceRelocateAttempts = 8;
    private const float StallRescueForceRelocateMinDistance = 30f;
    private const float StallRescueForceRelocateMaxDistance = 80f;

    // Wider than TryValidateEmergencyTeleportCandidate's own tight-radius
    // default (3m) - real terrain naturally varies more than that over a
    // 30-80m search, so the tight default would reject most legitimate
    // candidates outright. TryCalculatePath (inside that same function) is
    // still the real safety guarantee regardless of this value - a rock
    // formation summit fails that check outright no matter how generous
    // this elevation tolerance is.
    private const float StallRescueMaxElevationChange = 15f;

    /// <summary>
    /// Real fix (2026-09-07, Lucas's own live report: several bots ended
    /// up standing 20-30m in the air atop a rock formation, "physically
    /// impossible" - traced directly to this function's own original
    /// version, which did nothing but a raw TryFindGroundBelow raycast
    /// with no elevation cap, no obstruction check, and critically no real
    /// path-connectivity check. TryValidateEmergencyTeleportCandidate
    /// (LivingRust.Looting.cs, the safe, already-proven first-tier rescue
    /// TryEmergencyTeleport uses) has all three - the connectivity check
    /// (TryCalculatePath) in particular is exactly what a rock formation's
    /// flat top would fail, since its own navmesh has no real route down to
    /// the ground. This function only ever runs after that safe, validated
    /// search has ALREADY failed to find anywhere within its own tight
    /// radius - so every close candidate had already failed one of those
    /// three checks - throwing a much wider, completely unguarded net at
    /// that point was exactly what let a tall rock formation's summit pass
    /// as "solid ground" while a real connectivity check would have
    /// rejected it outright. Now reuses that exact same validation, just
    /// against StallRescueForceRelocateMinDistance-MaxDistance's own wider
    /// candidates instead of TryEmergencyTeleport's short-range ones.
    ///
    /// requireRealPath: false (2026-09-07, follow-up fix - a live trace
    /// found 270 distinct bots stuck in an unresolved rescue loop with
    /// ZERO successful relocations after the fix above shipped). The
    /// connectivity check that correctly stopped the rock-formation bug
    /// was ALSO rejecting nearly every real candidate here, for a
    /// structural reason specific to this function: it only ever runs once
    /// a survivor is already stuck somewhere real pathfinding doesn't
    /// work, so requiring TryCalculatePath to succeed FROM that same
    /// broken origin was close to a guaranteed failure - see
    /// TryValidateEmergencyTeleportCandidate's own requireRealPath doc
    /// comment (LivingRust.Looting.cs) for the full reasoning. Ground/
    /// elevation/obstruction checks are all still real and still enforced.
    /// </summary>
    private bool TryForceRelocateIgnoringCollision(Survivor survivor, BasePlayer npc)
    {
        string firstFailureReason = null;

        for (int attempt = 0; attempt < StallRescueForceRelocateAttempts; attempt++)
        {
            float angle = UnityEngine.Random.Range(0f, 360f);
            float distance = UnityEngine.Random.Range(StallRescueForceRelocateMinDistance, StallRescueForceRelocateMaxDistance);
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            Vector3 rawCandidate = npc.transform.position + direction * distance;

            // Real fix (2026-09-07, live diagnostic confirmed root cause:
            // TryFindGroundBelow's own searchHeight/searchDistance params
            // do NOTHING - see its own doc comment, NavigationManager.cs -
            // it only ever probes a small, fixed-range area right at
            // whatever origin point it's given. Passing candidate + up*20f
            // as that origin (the old groundProbeHeight=20f attempt) meant
            // every probe was floating 20m above the candidate's real
            // ground level, only ever finding something if an actual
            // object happened to reach up that high - exactly why it kept
            // landing on rock formation summits before (tall enough to
            // register) and finds nothing now (ordinary flat terrain isn't
            // 20m tall). Seeding a real, sane ground-level guess from the
            // actual terrain heightmap FIRST (same real API
            // TryFindRandomInlandSite already uses, LivingRust.HomeSiteStrategy.cs)
            // gives the small-range probe something legitimate to confirm
            // near, instead of searching blind from a height with zero
            // real information behind it.
            float terrainY = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(rawCandidate) : npc.transform.position.y;
            Vector3 candidate = new Vector3(rawCandidate.x, terrainY, rawCandidate.z);

            if (!TryValidateEmergencyTeleportCandidate(npc, candidate, out Vector3 validated, out string failureReason, StallRescueMaxElevationChange, requireRealPath: false))
            {
                firstFailureReason ??= failureReason;
                continue;
            }

            npc.transform.position = validated;
            npc.MovePosition(validated);
            npc.SendNetworkUpdateImmediate();

            VerbosePuts($"'{survivor.Character.Alias}' force-relocated {distance:F0}m away to escape a dead end no ordinary teleport candidate could clear.");
            return true;
        }

        Puts($"stall-rescue-diag: '{survivor.Character.Alias}' - all {StallRescueForceRelocateAttempts} force-relocate candidates failed, first reason: {firstFailureReason ?? "unknown"}.");

        return false;
    }

    // How far vertically a raw stuck-relocate candidate is allowed to
    // differ from the survivor's own CURRENT position (2026-09-15, Lucas's
    // own explicit spec: "no lower than 5 metres below their current Y
    // axis and no higher than 5 metres above their current Y axis").
    private const float StuckRawRelocateElevationTolerance = 5f;

    // Deliberately tight, and its own separate range from
    // StallRescueForceRelocateMinDistance/MaxDistance (30-80m, the EARLIER
    // ground-probed tier this one only runs after) - live report,
    // 2026-09-15: 'RustyOutlaw878' kept bouncing 60-70m away every ~20s on
    // a perfectly normal road/jungle spot, "wild"/"unnecessary" to watch,
    // when the immediate area was already fine. This tier exists to escape
    // a small, local glitch (an oddly-flagged patch of foliage/geometry
    // confusing the probe, not a genuinely bad region), so it doesn't need
    // to travel far - a short hop clear of whatever's immediately underfoot
    // should be enough, and looks far less jarring than a long-range jump
    // when it fires. Min kept just above 0 so a candidate can't roll right
    // back onto the same spot it's already stuck at.
    private const float StuckRawRelocateMinDistance = 5f;
    private const float StuckRawRelocateMaxDistance = 15f;

    private const int StuckRawRelocateAttempts = 8;

    /// <summary>
    /// Genuinely last resort (2026-09-15) - only ever runs after
    /// TryEmergencyTeleport AND TryForceRelocateIgnoringCollision have BOTH
    /// already failed (RescueGenuinelyStalledSurvivor's own call site).
    /// Live evidence that drove this: 'LuckyRanger' failed both of those
    /// tiers identically every single 15s retry, always "no real ground
    /// found within the probe window" - i.e. TryFindGroundBelow's own
    /// probe (see its doc comment, NavigationManager.cs) had already
    /// failed at every candidate both earlier tiers tried, for over a
    /// minute straight with zero progress.
    ///
    /// Deliberately skips that probe entirely rather than trying a ninth
    /// variant of the same check. The survivor is, by definition,
    /// currently standing somewhere real (a live BasePlayer, not in the
    /// void) - so real terrain height at a nearby point, CLAMPED to within
    /// StuckRawRelocateElevationTolerance of the survivor's own current Y
    /// rather than trusted outright, is very likely also real, standable
    /// ground without ever needing the probe to independently confirm it.
    /// Only remaining safety check is physical obstruction - no
    /// ground-probe, no elevation-delta-from-fresh-terrain-height check, no
    /// path-connectivity check, since every one of those is exactly what
    /// already failed by the time this runs. Trades a small chance of an
    /// odd-looking landing spot (a slight float above/below the real
    /// surface) for actually guaranteeing forward progress instead of
    /// retrying the same failing check forever.
    /// </summary>
    private bool TryRawNearbyRelocate(Survivor survivor, BasePlayer npc)
    {
        Vector3 origin = npc.transform.position;

        for (int attempt = 0; attempt < StuckRawRelocateAttempts; attempt++)
        {
            float angle = UnityEngine.Random.Range(0f, 360f);
            float distance = UnityEngine.Random.Range(StuckRawRelocateMinDistance, StuckRawRelocateMaxDistance);
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            Vector3 rawCandidate = origin + direction * distance;

            float terrainY = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(rawCandidate) : origin.y;
            float clampedY = Mathf.Clamp(terrainY, origin.y - StuckRawRelocateElevationTolerance, origin.y + StuckRawRelocateElevationTolerance);
            Vector3 candidate = new Vector3(rawCandidate.x, clampedY, rawCandidate.z);

            bool obstructed = Physics.CheckSphere(candidate + Vector3.up * 0.9f, 0.4f, LineOfSightBlockingMask, QueryTriggerInteraction.Ignore)
                || IsBlockedByOtherPlayer(candidate, npc, exempt: null);

            if (obstructed)
            {
                continue;
            }

            npc.transform.position = candidate;
            npc.MovePosition(candidate);
            npc.SendNetworkUpdateImmediate();

            Puts($"'{survivor.Character.Alias}' raw-relocated {distance:F0}m away (Y kept within {StuckRawRelocateElevationTolerance:F0}m of its own current elevation) - every ground-probed rescue tier had already failed.");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Per-survivor state for following a navmesh path: the computed
    /// path itself, which corner we're walking toward, and bookkeeping
    /// for when to recompute.
    /// </summary>
    private sealed class PathFollower
    {
        public readonly RustNavMeshPath Path = new();
        public int CornerIndex;
        public Vector3 LastPathTarget;
        public int TicksSinceRepath;
        public int ConsecutiveBlockedTicks;
        public string LastPathFailureReason;

        // Consecutive ticks the REAL path calculation (TryCalculatePath)
        // has failed identically (0 corners, PathInvalid) for the current
        // destination - see RepathFailureGiveUpTicks's own doc comment for
        // why this exists as an independent signal from ConsecutiveBlockedTicks/
        // the distance-progress heuristic, both of which a genuinely
        // unreachable target can fool by producing real (if circular)
        // local movement.
        public int ConsecutiveRepathFailureTicks;

        // Which sidestep angle worked last tick, tried first before
        // re-scanning the full candidate fan - 0 means "no preference yet"
        // (0 itself is never a real candidate; see SidestepAngles). Without
        // this, re-evaluating from scratch every tick let two roughly-
        // opposite candidates alternate "winning" as the tiniest position
        // change flipped which one looked clearer, producing a rapid
        // side-to-side jitter instead of committing to a route around the
        // obstacle.
        public float PreferredSidestepAngle;

        // Real live bug found 2026-08-15 ('4939RoughStalker', supermarket_1
        // doorway): the local-step branch's "not blocked" outcome calls
        // ApplyMovementStep and returns Progressing completely silently,
        // every tick, with zero logging - a real, confirmed-clean-geometry
        // doorway (TryGetNextStep verdict=SteppedUp, blockReason=none, per
        // a live /lr.debug.scan at the exact spot) still produced 25+
        // seconds of total silence and zero visible movement. Root cause:
        // PreferredSidestepAngle only guards against two OPPOSITE
        // candidates alternating - it does nothing to stop the bot
        // committing to one CONSISTENT sidestep direction that never
        // actually closes distance to the real destination (e.g.
        // perpetually stepping parallel to a doorframe), which reads as
        // "success" every single tick from TryGetNextStep/TryFindSidestep's
        // own narrow per-tick view. These two fields track REAL distance
        // progress across ticks, independent of whether any given tick's
        // step technically succeeded - same shape as the main walk timer's
        // own WalkMinProgressDistance/WalkGiveUpTicks watchdog, applied
        // here too so a silently-oscillating follow can't hide from every
        // existing detector the way this one did.
        public float LastLocalStepProgressDistance = float.MaxValue;

        // Real elapsed time (Time.realtimeSinceStartup), NOT a tick count -
        // see LocalStepProgressWatchdogTripped's own doc comment for why
        // this changed from an incrementing int (2026-09-01). -1 means "no
        // stall window open yet."
        public float LocalStepProgressWindowStartTime = -1f;

        // One-shot/periodic diagnostic (2026-08-15) for the noclip debug
        // bypass - see AdvanceAlongPath's own _noclipEnabled branch.
        public bool NoclipDiagLogged;
        public int NoclipDiagTicks;
    }

    /// <summary>
    /// Spawns a real, connectionless BasePlayer near the calling player.
    /// First manual test of the crawl-walk-run vertical slice: just get
    /// a survivor to appear in the world.
    /// </summary>
    [ChatCommand("lr.spawn")]
    private void CmdSpawnSurvivor(BasePlayer player, string command, string[] args)
    {
        RunSpawnSurvivor(player);
    }

    /// <summary>
    /// Console/bindable version - "bind y lr.spawn" fires it instantly on
    /// keypress. Same underlying behaviour as the chat command.
    /// </summary>
    [ConsoleCommand("lr.spawn")]
    private void CmdSpawnSurvivorConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunSpawnSurvivor(player);
        }
    }

    // How far /lr.spawn's aim-point raycast reaches - generous enough to
    // aim across the Powerline tower's full footprint (roughly 28m at its
    // widest) from ground level.
    private const float SpawnAimMaxDistance = 50f;

    private void RunSpawnSurvivor(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Character character = _engine.CharacterManager.CreateInitialSurvivor();
        Survivor survivor = _engine.SurvivorManager.Create(character);

        Vector3 spawnPosition = FindSpawnAimPoint(player, out bool aimedSpawn);

        BasePlayer npc = SpawnSurvivor(survivor, spawnPosition, player.transform.rotation);

        if (npc == null)
        {
            player.ChatMessage("[LivingRust] Failed to spawn survivor.");
            Puts($"ERROR: Failed to spawn survivor '{character.Alias}'.");
            return;
        }

        string where = aimedSpawn ? "where you're looking" : "near you (nothing solid in view)";
        // Bot ID shown as separate info, deliberately not folded into the
        // nametag (displayName stays just the alias) - useful for anything
        // that needs the raw userID directly (e.g. assigning sleeping bag
        // ownership via /lr.debug.claimbag or the server console).
        player.ChatMessage($"[LivingRust] Spawned survivor '{character.Alias}' {where}. (ID {character.BotId})");
    }

    /// <summary>
    /// Where /lr.spawn puts the new survivor: the nearest solid, non-trigger
    /// surface along the caller's view ray - same raycast pattern as
    /// /lr.debug.look's default mode. Lets you stand on an elevated platform
    /// (e.g. partway up the Powerline tower), look at where you want the
    /// bot, and spawn it there directly instead of always at ground level -
    /// useful for testing a specific leg without walking a fresh bot up from
    /// the base every time. Falls back to the old "2m in front at terrain
    /// height" placement if the ray doesn't hit anything solid (aiming at
    /// open sky, etc.), so the command never just fails outright.
    /// </summary>
    private Vector3 FindSpawnAimPoint(BasePlayer player, out bool aimedSpawn)
    {
        Vector3 origin = player.eyes.position;
        Vector3 direction = player.eyes.HeadForward();

        RaycastHit[] hits = Physics.RaycastAll(origin, direction, SpawnAimMaxDistance, ~0, QueryTriggerInteraction.Ignore);
        RaycastHit? nearestSolid = hits.Where(h => !h.collider.isTrigger).OrderBy(h => h.distance).Cast<RaycastHit?>().FirstOrDefault();

        if (nearestSolid.HasValue)
        {
            aimedSpawn = true;
            return nearestSolid.Value.point;
        }

        aimedSpawn = false;

        Vector3 fallback = player.transform.position + player.transform.forward * 2f;
        fallback.y = TerrainMeta.HeightMap.GetHeight(fallback);
        return fallback;
    }

    /// <summary>
    /// Sends the nearest spawned survivor walking to the nearest water,
    /// stopping at the shoreline. No navmesh yet - straight-line steps only.
    /// </summary>
    [ChatCommand("lr.walk")]
    private void CmdWalkToWater(BasePlayer player, string command, string[] args)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor survivor = FindNearestSpawnedSurvivor(player.transform.position);

        if (survivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.spawn first.");
            return;
        }

        BasePlayer npc = survivor.Player;

        if (!_engine.NavigationManager.TryFindNearestWater(npc.transform.position, out Vector3 waterPoint))
        {
            player.ChatMessage($"[LivingRust] Couldn't find any water near '{survivor.Character.Alias}'.");
            return;
        }

        StartWalking(survivor, waterPoint);

        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' is walking to water.");
    }

    /// <summary>
    /// Sends the nearest spawned survivor walking to the nearest monument
    /// matching name (e.g. "harbor", "military tunnel", "dome") - see
    /// /lr.monuments for the full list. Reuses the same navmesh-pathed
    /// walk as /lr.walk, so this doubles as a way to test monument/POI
    /// navigation (tunnels, stairs, ladders, water inside a harbor, etc.)
    /// without needing to physically walk the bot there via /lr.follow.
    /// </summary>
    [ChatCommand("lr.walk.monument")]
    private void CmdWalkToMonument(BasePlayer player, string command, string[] args)
    {
        RunWalkToMonument(player, string.Join(" ", args));
    }

    [ConsoleCommand("lr.walk.monument")]
    private void CmdWalkToMonumentConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunWalkToMonument(player, arg.HasArgs() ? string.Join(" ", arg.Args) : string.Empty);
        }
    }

    private void RunWalkToMonument(BasePlayer player, string name)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            player.ChatMessage("[LivingRust] Usage: /lr.walk.monument <name> - see /lr.monuments for the list.");
            return;
        }

        Survivor survivor = FindNearestSpawnedSurvivor(player.transform.position);

        if (survivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.spawn first.");
            return;
        }

        BasePlayer npc = survivor.Player;

        if (!_engine.NavigationManager.TryFindNearestMonument(npc.transform.position, name, out Vector3 destination, out string matchedName))
        {
            player.ChatMessage($"[LivingRust] No monument matching '{name}' found. See /lr.monuments for the list.");
            return;
        }

        StartWalking(survivor, destination);

        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' is walking to {matchedName}.");
    }

    /// <summary>
    /// Sends the nearest spawned survivor to follow the calling player at
    /// a walking pace, stopping a short distance away and waiting at the
    /// shoreline rather than wading into water to keep up.
    /// </summary>
    [ChatCommand("lr.follow")]
    private void CmdFollow(BasePlayer player, string command, string[] args)
    {
        RunFollow(player);
    }

    /// <summary>
    /// Console/bindable version - "bind u lr.follow" fires it instantly on
    /// keypress. Same underlying behaviour as the chat command.
    /// </summary>
    [ConsoleCommand("lr.follow")]
    private void CmdFollowConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunFollow(player);
        }
    }

    private void RunFollow(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor survivor = FindNearestSpawnedSurvivor(player.transform.position);

        if (survivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.spawn first.");
            return;
        }

        StartFollowing(survivor, player);

        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' is now following you.");
    }

    /// <summary>
    /// Cancels whatever movement behaviour the nearest spawned survivor
    /// is currently doing (walking or following).
    /// </summary>
    [ChatCommand("lr.stop")]
    private void CmdStop(BasePlayer player, string command, string[] args)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor survivor = FindNearestSpawnedSurvivor(player.transform.position);

        if (survivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby.");
            return;
        }

        CancelActiveMovement(survivor);

        // Without this, stopping mid-climb (or mid step-up/crouch) left the
        // bot's animator frozen in whatever pose it was last in - the timer
        // being gone stops movement but never resets modelState on its own.
        ReleaseMovementState(survivor.Player);

        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' has stopped.");
    }

    /// <summary>
    /// Sends the nearest spawned survivor to the nearest ladder and climbs
    /// it - up or down, whichever direction the survivor's current height
    /// relative to the ladder implies. Deliberately a standalone test
    /// command (like /lr.walk and /lr.follow originally were) rather than
    /// wired into general pathfinding yet - proves the climb mechanic in
    /// isolation first.
    /// </summary>
    [ChatCommand("lr.climb")]
    private void CmdClimb(BasePlayer player, string command, string[] args)
    {
        RunClimb(player);
    }

    [ConsoleCommand("lr.climb")]
    private void CmdClimbConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunClimb(player);
        }
    }

    private void RunClimb(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor survivor = FindNearestSpawnedSurvivor(player.transform.position);

        if (survivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.spawn first.");
            return;
        }

        BasePlayer npc = survivor.Player;

        if (!_engine.NavigationManager.TryFindNearestLadder(npc.transform.position, LadderSearchRadius, out NavigationManager.LadderInfo ladder))
        {
            player.ChatMessage($"[LivingRust] No ladder found within {LadderSearchRadius:F0}m of '{survivor.Character.Alias}'.");
            return;
        }

        // Whichever end of the ladder the survivor is currently closer to
        // is the end they're standing at - climb toward the other end.
        // Handles the "ladder's at my feet vs. at my head" framing directly
        // rather than requiring a direction argument.
        float ladderMidpointY = (ladder.Bottom.y + ladder.Top.y) * 0.5f;
        bool climbingDown = npc.transform.position.y >= ladderMidpointY;

        // Deliberately NOT routed through StartWalking/RustNavMesh.CalculatePath -
        // confirmed via the CalculatePath diagnostic that this monument deck's
        // NavMesh is fragmented into small disconnected islands around all its
        // scattered dressing props (a "PathInvalid, 0 corners" failure between
        // two points barely 0.75m apart, both individually well within 1m of a
        // real navmesh point - not an off-mesh problem, a connectivity one).
        // The approach is always short-range (LadderApproachOffset), so local
        // obstacle-aware stepping alone is enough, same as the dismount step.
        StartClimbing(survivor, BuildClimbState(npc, _engine.NavigationManager, ladder, climbingDown));

        string direction = climbingDown ? "down" : "up";
        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' is heading to climb {direction} the ladder.");
    }

    /// <summary>
    /// Sends the nearest spawned survivor climbing upward on its own - no
    /// player to follow, just a standing goal ("get as high up this
    /// monument as you can"). A test of goal-directed movement rather than
    /// reactive following: reuses the exact same ladder-detour and
    /// local-step-fallback machinery /lr.follow does, just aimed at a fixed
    /// point far above the survivor's start position instead of a live
    /// target, so it keeps climbing every reachable ladder in sequence
    /// until it genuinely runs out of monument.
    /// </summary>
    [ChatCommand("lr.climb.monument")]
    private void CmdClimbMonument(BasePlayer player, string command, string[] args)
    {
        RunClimbMonument(player);
    }

    [ConsoleCommand("lr.climb.monument")]
    private void CmdClimbMonumentConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunClimbMonument(player);
        }
    }

    private void RunClimbMonument(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor survivor = FindNearestSpawnedSurvivor(player.transform.position);

        if (survivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.spawn first.");
            return;
        }

        // Authored route takes priority over the generic goal-driven climb -
        // see LivingRust.MonumentRoutes.cs for why. Falls back to the
        // generic heuristic for anything without a known route, so nothing
        // already working regresses.
        if (TryFindMonumentRoute(survivor.Player.transform.position, out string monumentName, out MonumentWaypoint[] route))
        {
            StartMonumentRoute(survivor, route);
            player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' is following the authored route up the {monumentName}.");
            return;
        }

        StartClimbingMonument(survivor);

        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' is heading up the monument on its own.");
    }

    /// <summary>
    /// Creates the real, connectionless BasePlayer for a Survivor by
    /// spawning the same prefab real players use, with no Network.Connection
    /// ever assigned.
    ///
    /// health and restoreInventory are optional and only used when bringing
    /// a still-alive survivor back after a real server restart (see
    /// RestoreSpawnedSurvivors in LivingRust.Persistence.cs) - a normal
    /// /lr.spawn or death-respawn leaves them at their defaults, which
    /// means full health and the standard starting kit (GiveStartingKit),
    /// exactly as before. When restoreInventory is given, it's used
    /// instead of the starting kit - this is "pick up where you left off",
    /// not a fresh life, so re-granting a rock and torch on top of
    /// whatever was actually saved would be wrong.
    /// </summary>
    private BasePlayer SpawnSurvivor(Survivor survivor, Vector3 position, Quaternion rotation, float health = 100f, List<SavedItem> restoreInventory = null, bool useBeachSpawnPoint = false)
    {
        BasePlayer npc = GameManager.server.CreateEntity(
            "assets/prefabs/player/player.prefab",
            useBeachSpawnPoint ? Vector3.zero : position,
            useBeachSpawnPoint ? Quaternion.identity : rotation) as BasePlayer;

        if (npc == null)
        {
            return null;
        }

        // Must be set BEFORE Spawn(): BasePlayer.ServerInit() (called from
        // within Spawn()) only overwrites displayName back to the numeric
        // userID string when userID == 0 at that point, and the entity's
        // first network snapshot to nearby clients goes out during this
        // same Spawn() call - setting displayName afterward is too late,
        // since Rust has no live nametag-refresh path for connected players.
        // Character.BotId, not a freshly minted ID - reusing the same
        // userID for this character's whole life (assigned once by
        // CharacterManager.CreateCharacter) is what lets Rust's own
        // blueprint-unlock and building-privilege systems (both keyed by
        // userID) carry over across a respawn instead of resetting.
        npc.userID = survivor.Character.BotId;
        npc.displayName = survivor.Character.Alias;

        if (useBeachSpawnPoint)
        {
            // Real Rust dedicated spawn system - the exact same
            // ServerMgr.FindSpawnPoint lookup RespawnSurvivor already uses
            // for a real death respawn, reused here (only for
            // /lr.debug.spawnmany - /lr.spawn itself stays exactly as-is,
            // aimed wherever the caller's looking) so a scale test starts
            // bots at real, procedurally-distributed beach spawns instead
            // of scattered arbitrarily around whoever ran the command -
            // matches where every real player on this server would
            // actually start from.
            BasePlayer.SpawnPoint spawnPoint = ServerMgr.FindSpawnPoint(npc);
            position = spawnPoint.pos;
            rotation = spawnPoint.rot;
            npc.transform.position = position;
            npc.transform.rotation = rotation;
        }

        npc.Spawn();
        npc.InitializeHealth(health, 100f);
        npc.SendNetworkUpdateImmediate();

        if (restoreInventory != null)
        {
            RestoreInventory(npc, restoreInventory);
        }
        else
        {
            // Same rock + torch every fresh life starts with - see
            // GiveStartingKit's doc comment.
            GiveStartingKit(npc);
        }

        survivor.Player = npc;
        survivor.Position = position;
        survivor.Character.Position = position;
        survivor.Character.Rotation = rotation;
        survivor.Spawned = true;
        survivor.Character.Spawned = true;

        // A character brought back through here - whether a genuine fresh
        // /lr.spawn or a restart's fresh-respawn fallback - is alive by
        // definition. Without this, a character whose last recorded State
        // was Dead (e.g. it died and the RespawnSurvivor timer got cut
        // short by a plugin reload/restart before it fired, since
        // timer.Once is plugin-scoped and never survives an Unload) would
        // stay stuck at Dead forever: OnPlayerDeath's very first check
        // (Character.State == Dead) would silently no-op on every future
        // kill, since nothing else ever resets State back to Alive.
        survivor.Character.State = CharacterState.Alive;

        // instanceID logged as a temporary diagnostic (2026-08-10) -
        // investigating a real, apparently pre-existing bug: despawnall's
        // own orphan-detection is catching a large fraction of bots
        // (17-47% across different runs) whose survivor.Player doesn't
        // match a live entity holding their exact BotId, and most never
        // died first - it's happening at/near spawn time itself. Ruled
        // out via code review: duplicate BotId issuance, a mid-batch
        // reload, SurvivorManager/CharacterManager bugs, hook
        // interference (OnPlayerDeath is the only hook and doesn't fire
        // for bots that never died). This instanceID, cross-referenced
        // against DespawnAllBots' own diagnostic on the orphan path,
        // will show directly whether survivor.Player is later found
        // null/destroyed (got cleared somehow) or pointing at a
        // genuinely different, still-alive object (a real duplicate) the
        // next time this reproduces.
        Puts($"Spawned BasePlayer for '{survivor.Character.Alias}' (userID {npc.userID}, instanceID {npc.GetInstanceID()}) at {position}.");

        return npc;
    }

    /// <summary>
    /// Starts (or restarts) a navmesh-pathed walk toward destination,
    /// stopping automatically at the shoreline. Routes around large
    /// obstacles via the navmesh, and steps up onto small ones (like a
    /// fallen log) along each local segment.
    ///
    /// Tries Rust's own native navigation (RustNavMeshAgent, see
    /// EnsureNativeNavAgent) first, same as StartFollowing - a live loot
    /// task caught the hand-built engine hitting 9 separate "exhausted
    /// every recovery option" failures scattered across one monument's
    /// concrete-slab area in a single run, while /lr.follow (already
    /// native-first) crossed the exact same physical area without a
    /// single fallback. Falls back to the proven hand-built
    /// AdvanceAlongPath engine (unchanged below) if native can't path or
    /// makes no real progress for WalkNativeGiveUpTicks - unlike
    /// StartFollowing there's no later retry within the same call, since
    /// this walk has one fixed destination, not a continuously moving
    /// target.
    ///
    /// onFailed fires for every way this can end without reaching
    /// destination - genuine Stuck/NoPath, the real-progress give-up
    /// below, hitting the shoreline, or the outer maxTicks timeout -
    /// giving callers a single place to react (e.g. the loot task
    /// abandoning an unreachable container and moving on to the next one)
    /// instead of only ever finding out by the walk silently going quiet.
    ///
    /// Sprints by default, not walks - user's explicit correction: real
    /// Rust players run essentially all the time (there's a real
    /// competitive cost to not doing so), walking is the rare exception
    /// (inside your own base, a risky ledge, deliberately sneaking), not
    /// the default. So running is unconditional here unless
    /// shouldWalkCarefully - evaluated fresh every tick, a generic "is
    /// there a specific reason to be careful right now" hook - says
    /// otherwise; callers decide the actual condition (none exist yet -
    /// base-interior/ledge/stealth awareness are all future work), this
    /// just switches pace (both native and hand-built) accordingly.
    /// Deliberately generic rather than task-specific, so the exact same
    /// mechanism covers every future context (in base, near a ledge,
    /// hiding) without touching this function again. Also always eases
    /// off to a walk automatically within ApproachSlowdownDistance of the
    /// destination regardless of shouldWalkCarefully - user-requested, so
    /// arriving anywhere reads as a real approach, not sprinting flat into
    /// whatever it was heading for.
    /// </summary>
    // Raised from 200s to 600s (2026-09-01, Lucas's own explicit call after
    // a live trace found only 4 of 166 inland-site rolls ever actually
    // arriving, 57 explicitly giving up - "couldn't reach its inland site")
    // - the flat 200s default couldn't geometrically cover a genuinely
    // distant inland-site walk (often 1500-3000m) even at a dead sprint
    // with zero obstruction (RunSpeed 5.5m/s * 200s = ~1100m max), before
    // any real blocking/re-routing time is even factored in. 600s covers
    // ~3300m unobstructed - comfortably past this map's longest realistic
    // inland-site distance - while still giving up eventually rather than
    // leaving a genuinely unreachable destination's walk timer running
    // forever.
    private void StartWalking(Survivor survivor, Vector3 destination, Action onArrived = null, Action onFailed = null, Func<BasePlayer, bool> shouldWalkCarefully = null, float maxSeconds = 600f)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveMovement(survivor);

        int maxTicks = (int)(maxSeconds / WalkTickInterval);
        int ticks = 0;
        var follower = new PathFollower();

        float lastProgressDistance = survivor.Player != null
            ? Vector3.Distance(survivor.Player.transform.position, destination)
            : 0f;
        int ticksSinceProgress = 0;

        bool useNative = !_noclipEnabled;
        bool nativeDestinationSet = false;
        float nativeLastProgressDistance = lastProgressDistance;
        int nativeStuckTicks = 0;
        RustNavMeshAgent nativeAgent = null;
        NavMeshAgent unityAgent = null;

        Timer walkTimer = null;
        bool loggedFirstTick = false;

        walkTimer = timer.Every(WalkTickInterval, () =>
        {
            // Diagnostic only (2026-08-15, that spawn-burst bug long since
            // root-caused and fixed) - downgraded to VerbosePuts (2026-09-19,
            // live report: rubber-banding "really bad," asking for anything
            // short of a full restart). This was deliberately hardcoded to
            // Puts to survive a specific bug hunt that's long over, but at
            // today's ~340-survivor population it's one-shot per walk
            // across every survivor that starts one - a real, continuous,
            // unconditional log-volume cost with no way to turn it off. Now
            // respects _verboseLootLogging like every other per-tick
            // narration line in this project already does.
            if (!loggedFirstTick)
            {
                loggedFirstTick = true;
                VerbosePuts($"walk-diag: '{survivor.Character.Alias}' walk timer reached its first tick toward {destination}.");
            }

            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                walkTimer.Destroy();
                _activeMovement.Remove(characterId);
                return;
            }

            // No longer turns a survivor back from water at all (2026-08-15,
            // later same session) - real swimming instead, Lucas's own
            // explicit call after the road-detour alternative (routing
            // around water via roads) shipped a real bug on its first live
            // test. Real drowning damage is a non-issue for these bots -
            // confirmed via decompile (PlayerMetabolism.ServerUpdate):
            // Rust itself scales oxygen-depletion damage to 1% for any
            // IsConnected==false player, which every survivor always is.
            // Movement speed/animation are untouched here deliberately -
            // Rust's own WaterFactor()/IsSwimming() (real, live-computed
            // from actual position vs the water surface, not something
            // this project sets) already feeds the real swim animation and
            // its own speed curve (BaseEntity.GetSpeed) client-side, same
            // mechanism a real player's own swimming already uses.

            // Wounded/downed - pause entirely rather than fight Rust's own
            // incapacitated state machine. A live report caught a bot
            // "getting back up unnaturally" mid-task: native movement
            // (the default path since the run-by-default refactor) never
            // checked IsWounded() at all, unlike the hand-built
            // AdvanceAlongPath's own partial crawl-speed handling, so it
            // kept calling SetDestination/MovePosition at full pace the
            // entire time regardless, fighting the wounded animation
            // instead of ever letting it actually play out. Doesn't count
            // wounded ticks toward maxTicks either - being incapacitated
            // isn't the walk's own fault to time out over.
            if (npc.IsWounded())
            {
                return;
            }

            // Unconditional, every tick, regardless of which branch this
            // tick otherwise takes below - see CorrectSwimDrift's own doc
            // comment. Without this, the correction only ever ran inside
            // the native movement "successful step" branch specifically,
            // so any tick that took a different path (fallback, blocked,
            // no-progress) left Unity's own continuous drift completely
            // uncorrected - confirmed live via trace ('SavageHermit'): Y
            // crept progressively deeper over the ENTIRE swim (-1.10 ->
            // -2.16 -> -2.67 -> -3.77...), never actually stabilizing at
            // the intended swim depth.
            CorrectSwimDrift(survivor, npc);

            // Periodic (every ~2s) diagnostic (2026-08-16, that
            // 'BrokenTrapper395' silent-stall case long since root-caused
            // by the life-stall watchdog work). Downgraded to VerbosePuts
            // (2026-09-19, live rubber-banding report) for the same reason
            // as walk-diag just above - this was the single largest
            // contributor to sustained log volume at today's population
            // (confirmed live: ~235 of ~400 sampled lines), firing every
            // ~2s for every survivor with an active walk, completely
            // unconditionally.
            if (ticks % 40 == 0)
            {
                VerbosePuts($"walk-progress-diag: '{survivor.Character.Alias}' tick={ticks}, pos={npc.transform.position}, destination={destination}, dist={Vector3.Distance(npc.transform.position, destination):F2}, useNative={useNative}.");
            }

            if (++ticks > maxTicks)
            {
                walkTimer.Destroy();
                _activeMovement.Remove(characterId);
                SnapToGround(npc);

                VerbosePuts($"'{survivor.Character.Alias}' gave up walking to its destination after {maxTicks} ticks (timed out).");
                onFailed?.Invoke();

                return;
            }

            bool nearDestination = Vector3.Distance(npc.transform.position, destination) <= ApproachSlowdownDistance;
            bool walkingCarefully = shouldWalkCarefully != null && shouldWalkCarefully(npc);
            bool hurry = !nearDestination && !walkingCarefully;

            if (useNative)
            {
                // See AdvanceAlongPath's identical call/doc comment - native
                // pathing's own canOpenDoors=true only opens a door via
                // NPCDoorTriggerBox's automatic proximity trigger, which is
                // gated on BaseEntity.IsNpc (never true for a disconnected
                // BasePlayer), so it never actually fires for these
                // survivors. Without this, SetDestination below can genuinely
                // fail its very first path attempt whenever the route
                // requires a currently-closed door, immediately falling back
                // to hand-built movement instead of ever giving native a
                // real chance.
                TryOpenNearbyDoors(npc);

                EnsureNativeNavAgent(npc, out nativeAgent, out unityAgent);
                nativeAgent.speed = GetSwimAdjustedSpeed(npc, hurry ? RunSpeed : WalkSpeed, IsSwimmingNow(npc.transform.position, survivor.Position.y));

                if (!nativeDestinationSet)
                {
                    // GetApproachPoint computes points via raw geometry
                    // (container bounds + a small standoff + a ground
                    // raycast) with zero navmesh awareness - fine for the
                    // hand-built local-stepper, which only needs a rough
                    // direction to probe toward. Snapping onto the real
                    // nearest navmesh point first is still worth doing (a
                    // genuinely off-mesh point IS a real failure mode), but
                    // turned out NOT to be the main cause of native's
                    // near-100% instant-fail rate - /lr.debug.navmeshcheck
                    // later found dense real coverage (0.3-0.9m away)
                    // sitting right at the exact spots this was failing.
                    // See the pathPending wait below for the actual cause.
                    Vector3 nativeTarget = destination;

                    if (nativeAgent.SamplePosition(destination, out NavMeshHit navHit, WalkNativeApproachSnapDistance))
                    {
                        nativeTarget = navHit.position;
                    }

                    nativeAgent.SetDestination(nativeTarget);
                    nativeDestinationSet = true;
                }

                // Unity computes a NavMeshAgent's path asynchronously -
                // hasPath/pathStatus aren't meaningful until pathPending
                // resolves, which can take a tick or more after
                // SetDestination. The previous version judged nativeUsable
                // on the exact same tick SetDestination was called,
                // reading hasPath=False simply because Unity hadn't
                // finished calculating yet - not because the path
                // genuinely failed. Confirmed via /lr.debug.navmeshcheck:
                // real, close navmesh coverage existed at every one of
                // these "failed" destinations, so the fail could only have
                // been a timing artifact, not a real pathing failure.
                // Waiting out pathPending is what gives native an honest
                // chance before judging it.
                //
                // BUT capped (2026-08-16, real live bug) - pathPending can
                // get permanently stuck true for a specific destination
                // (confirmed live via a dedicated periodic diagnostic:
                // '7546WastedScav' sat frozen at the exact same position
                // for 600+ consecutive ticks, zero movement, ZERO log
                // output of any kind) rather than resolving within a tick
                // or two like it normally does. Every watchdog in this
                // function lives AFTER this check, so an unbounded wait
                // here silently defeated all of them at once - the earlier
                // monotonic-best-ever fix, the whole stuck-recovery ladder,
                // even the barricade check, none of it can ever run if
                // this branch returns before reaching any of it. Reuses
                // nativeStuckTicks/WalkNativeGiveUpTicks as a simple tick
                // cap rather than adding new state.
                if (nativeAgent.pathPending)
                {
                    if (++nativeStuckTicks >= WalkNativeGiveUpTicks)
                    {
                        VerbosePuts($"'{survivor.Character.Alias}' - native path calculation never resolved (stuck pathPending) for {WalkNativeGiveUpTicks * WalkTickInterval:F0}s, falling back to hand-built movement.");

                        nativeStuckTicks = 0;
                        nativeLastProgressDistance = float.MaxValue;
                        useNative = false;
                        nativeAgent.ResetPath();
                        unityAgent.enabled = false;
                    }

                    return;
                }

                bool nativeUsable = unityAgent.isOnNavMesh && nativeAgent.hasPath && nativeAgent.pathStatus != NavMeshPathStatus.PathInvalid;

                if (nativeUsable)
                {
                    float distToDest = Vector3.Distance(npc.transform.position, destination);

                    // Reaching the snapped navigation target counts as
                    // arrival too, not just closing the raw distance to
                    // the original (possibly slightly off-navmesh)
                    // approach point - the snap above can legitimately be
                    // up to WalkNativeApproachSnapDistance away from it.
                    // Still bounded by distToDest itself, though - a live
                    // test caught a survivor "arriving" and looting a
                    // container 10-15m away, which this unbounded OR could
                    // have contributed to. LootInteractionRange is the
                    // real hard backstop against that now regardless, but
                    // this shouldn't claim arrival that far off to begin
                    // with.
                    if (distToDest <= WaypointArriveDistance
                        || (nativeAgent.remainingDistance <= WaypointArriveDistance && distToDest <= WalkNativeApproachSnapDistance + WaypointArriveDistance))
                    {
                        walkTimer.Destroy();
                        _activeMovement.Remove(characterId);
                        SnapToGround(npc);

                        unityAgent.enabled = false;

                        VerbosePuts($"'{survivor.Character.Alias}' reached its destination.");
                        onArrived?.Invoke();
                        return;
                    }

                    // Monotonic best-ever-distance record, checked every
                    // tick, not a held-fixed window - see StartWalking's
                    // outer hand-built watchdog's identical fix/doc
                    // comment for why (second live iteration, the windowed
                    // version still wasn't enough - 'MadLooter' spun
                    // silently the entire 200s hard timeout, meaning native
                    // was genuinely clearing WalkNativeWindowMinProgressDistance
                    // often enough per window to never fall back at all).
                    if (nativeLastProgressDistance - distToDest >= WalkNativeWindowMinProgressDistance)
                    {
                        nativeLastProgressDistance = distToDest;
                        nativeStuckTicks = 0;
                    }
                    else
                    {
                        nativeStuckTicks++;
                    }

                    if (nativeStuckTicks < WalkNativeGiveUpTicks)
                    {
                        if (_engine.NavigationManager.IsBodyOverlapping(npc.transform.position))
                        {
                            VerbosePuts($"'{survivor.Character.Alias}' - native movement position would overlap solid geometry, falling back to hand-built movement.");
                        }
                        else
                        {
                            npc.modelState.sprinting = hurry;
                            npc.modelState.ducked = false;
                            npc.modelState.ducking = 0f;
                            // Real player swim animation is driven by this
                            // networked field, normally set every tick by
                            // the client's own real PlayerTick RPC - a
                            // disconnected bot never sends one (confirmed
                            // this project's own past investigation into
                            // viewAngles networking), so nothing was ever
                            // setting it, which is why bots kept visibly
                            // "running" through water even after avoidance
                            // was removed (2026-08-15, Lucas's live report).
                            // WaterFactor() itself is the same real,
                            // server-authoritative computation
                            // IsSwimming()/GetSpeed() already use.
                            npc.modelState.waterLevel = npc.WaterFactor();
                            // ModelState.flags defaults to Flag.OnGround
                            // (DefaultFlags) and nothing in this project
                            // ever cleared it - every survivor's networked
                            // state permanently claimed "I'm on the
                            // ground," even mid-ocean, the whole time.
                            // Real BasePlayer.IsSwimming() threshold
                            // (waterLevel >= 0.65f) reused directly here
                            // since waterLevel was just computed fresh
                            // above - if the client's own animator
                            // prioritizes "on ground" over "swimming" the
                            // way these systems normally do, this was
                            // silently winning that fight the entire time
                            // (2026-08-15, live report: correct swim DEPTH
                            // but never the actual swim animation).
                            npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
                            npc.SendModelState(true);

                            FaceDirection(npc, unityAgent.velocity);

                            // See ClampToWaterSurfaceIfSwimming's own doc
                            // comment (ApplyMovementStep has the identical
                            // fix for hand-built movement) - native
                            // movement's own Y otherwise just follows
                            // whatever the baked navmesh does underwater
                            // (the real seafloor, if the navmesh extends
                            // that far), same "walking the ocean floor"
                            // symptom confirmed live via trace.
                            Vector3 swimAdjustedPosition = ClampToWaterSurfaceIfSwimming(survivor, npc.transform.position, survivor.Position.y, IsSwimmingNow(npc.transform.position, survivor.Position.y));
                            npc.MovePosition(swimAdjustedPosition);
                            survivor.Position = swimAdjustedPosition;
                            survivor.Character.Position = swimAdjustedPosition;
                            return;
                        }
                    }
                    else
                    {
                        VerbosePuts($"'{survivor.Character.Alias}' - native movement made no real progress for {WalkNativeGiveUpTicks * WalkTickInterval:F0}s, falling back to hand-built movement.");
                    }
                }
                else
                {
                    // Previously silent - a live test showed the hand-built
                    // fallback engine running almost exclusively for a
                    // scattered-container loot task, with no
                    // "made no real progress" line ever appearing despite
                    // 1000+ hand-built log lines. Turned out nativeUsable
                    // was failing on tick 1 for most destinations (this
                    // branch), which never logs anything - most likely the
                    // approach point itself (GetApproachPoint, computed to
                    // satisfy the hand-built local-stepping system) sits
                    // just off what Unity's SetDestination considers close
                    // enough to the baked navmesh to accept, unlike a
                    // plain "walk to the player" destination. Logging this
                    // case explicitly is what's needed to actually confirm
                    // that theory instead of guessing.
                    VerbosePuts($"'{survivor.Character.Alias}' - native movement never got a usable path toward {destination} (isOnNavMesh={unityAgent.isOnNavMesh}, hasPath={nativeAgent.hasPath}, pathStatus={nativeAgent.pathStatus}), falling back to hand-built movement immediately.");
                }

                useNative = false;
                nativeAgent.ResetPath();
                unityAgent.enabled = false;

                // The hand-built follower below has never run yet this
                // call - nothing stale to reset, it just starts fresh from
                // wherever native left the survivor.
            }

            float stepDistance = GetSwimAdjustedSpeed(npc, hurry ? RunSpeed : WalkSpeed, IsSwimmingNow(npc.transform.position, survivor.Position.y)) * WalkTickInterval;

            MovementOutcome outcome = AdvanceAlongPath(follower, survivor, npc, destination, stepDistance, sprinting: hurry, followTarget: null, out _);

            if (outcome == MovementOutcome.ReachedDestination || outcome == MovementOutcome.BlockedByWater)
            {
                walkTimer.Destroy();
                _activeMovement.Remove(characterId);
                SnapToGround(npc);

                string reachedWhat = outcome == MovementOutcome.BlockedByWater ? "the shoreline" : "its destination";
                VerbosePuts($"'{survivor.Character.Alias}' reached {reachedWhat}.");

                if (outcome == MovementOutcome.ReachedDestination)
                {
                    onArrived?.Invoke();
                }
                else
                {
                    onFailed?.Invoke();
                }

                return;
            }

            if (outcome != MovementOutcome.Blocked)
            {
                // Monotonic best-ever-distance record, checked every tick,
                // with a MEANINGFULLY sized new-record bar
                // (WalkWindowMinProgressDistance, 1.5m) - not the tiny 2cm
                // WalkMinProgressDistance the original design used, and not
                // a "hold fixed for a whole window then check once" design
                // either (2026-08-16, second live iteration - the windowed
                // version still let 'MadLooter' spin for the full 200s
                // hard timeout with zero log output, meaning it was
                // genuinely clearing 1.5m/10s often enough via inefficient
                // winding/sidestepping to pass window after window while
                // never actually arriving). lastProgressDistance now only
                // ever ratchets DOWN, checked every tick - a bot drifting
                // back toward a distance it's already achieved can never
                // count as "new progress" again, and the 1.5m bar is large
                // enough that pure jitter/oscillation can't satisfy it by
                // accident the way the original 2cm bar could. Skipped for
                // Blocked (handled below, already has its own
                // ConsecutiveBlockedTicks-based give-up) so the two
                // detectors don't fight over the same stuck episode.
                float currentDistance = Vector3.Distance(npc.transform.position, destination);

                if (lastProgressDistance - currentDistance >= WalkWindowMinProgressDistance)
                {
                    lastProgressDistance = currentDistance;
                    ticksSinceProgress = 0;
                }
                else if (++ticksSinceProgress >= WalkGiveUpTicks)
                {
                    walkTimer.Destroy();
                    _activeMovement.Remove(characterId);
                    SnapToGround(npc);

                    VerbosePuts($"'{survivor.Character.Alias}' gave up walking to its destination - stuck making no real progress over the last {WalkGiveUpTicks * WalkTickInterval:F0}s (likely a fragmented navmesh island).");

                    onFailed?.Invoke();
                    return;
                }
            }

            if (outcome == MovementOutcome.Stuck || outcome == MovementOutcome.NoPath)
            {
                walkTimer.Destroy();
                _activeMovement.Remove(characterId);
                SnapToGround(npc);

                string detail = outcome == MovementOutcome.NoPath ? $" - {follower.LastPathFailureReason}" : "";
                VerbosePuts($"'{survivor.Character.Alias}' couldn't find a way to its destination ({outcome}){detail}.");

                onFailed?.Invoke();
            }
        });

        _activeMovement[characterId] = walkTimer;
    }

    /// <summary>
    /// Mutable per-climb state for <see cref="AdvanceClimb"/> - shared
    /// between the standalone /lr.climb command and a follow-in-progress
    /// detour, so both drive the exact same approach/ascend-or-descend/
    /// dismount state machine instead of two copies drifting apart.
    /// </summary>
    private sealed class ClimbState
    {
        public NavigationManager.LadderInfo Ladder;
        public Vector3 IntoLadder;
        public Vector3 MountPoint;
        public bool ClimbingDown;
        public float TargetY;
        public bool Approaching = true;
        public bool ReachedEnd;
        public int ApproachTicks;
        public bool ApproachBlockLogged;
        public bool IgnoreHeadroomOnApproach;
    }

    private enum ClimbOutcome
    {
        InProgress,
        Finished,
        TimedOut
    }

    /// <summary>
    /// Works out the mount point, facing, and ascend/descend target for
    /// climbing a given ladder from the survivor's current position -
    /// shared by the standalone /lr.climb command and the follow-detour
    /// trigger so the two can't disagree on which side to mount from.
    /// </summary>
    private static ClimbState BuildClimbState(BasePlayer npc, NavigationManager navigation, NavigationManager.LadderInfo ladder, bool climbingDown, bool ignoreHeadroomOnApproach = false)
    {
        float mountSide = Vector3.Dot(npc.transform.position - ladder.Bottom, ladder.MountAxis);
        Vector3 approachDirection = (Mathf.Abs(mountSide) > 0.01f ? Mathf.Sign(mountSide) : 1f) * ladder.MountAxis;
        Vector3 intoLadder = -approachDirection;

        // Keeps the survivor's current height rather than ladder.Bottom.y -
        // confirmed via /lr.debug.nearby that the trigger's bottom sits
        // noticeably below the actual deck surface (it needs room to catch
        // you as you step toward the rungs from the platform above). Bottom
        // and Top share the same x/z (both derived from the same trigger
        // bounds.center), so this offset is valid for mounting from either
        // end of the ladder.
        Vector3 mountOffset = ladder.Bottom + approachDirection * LadderApproachOffset;
        Vector3 mountPoint = new Vector3(mountOffset.x, npc.transform.position.y, mountOffset.z);

        float targetY;

        if (climbingDown)
        {
            // ladder.Bottom.y is only the TriggerLadder collider's own
            // bounds.min.y, which doesn't reliably reach the real floor on
            // every ladder (it happened to on the Powerline tower, not on a
            // watchtower at Sulfur Quarry - bot stopped ~1-2m up and reported
            // "finished" every time). Probe for the actual ground instead of
            // trusting a fixed ClimbOvershoot guess, offset toward the far
            // side of the ladder (same direction the dismount step already
            // steps in) so the raycast doesn't clip the ladder's own rungs.
            Vector3 groundProbeOrigin = ladder.Bottom + intoLadder * LadderDismountStepDistance;

            targetY = navigation.TryFindGroundBelow(groundProbeOrigin, GroundProbeStartHeight, GroundProbeSearchDistance, out float groundY, out _)
                ? groundY
                : ladder.Bottom.y - ClimbOvershoot;
        }
        else
        {
            // Climbing up always dismounts onto a platform the survivor
            // approached from below, close enough above ladder.Top that the
            // fixed overshoot (tuned across both known monuments) still
            // holds - no equivalent "trigger doesn't reach the surface"
            // failure mode has shown up on this side.
            targetY = ladder.Top.y + ClimbOvershoot;
        }

        return new ClimbState
        {
            Ladder = ladder,
            IntoLadder = intoLadder,
            MountPoint = mountPoint,
            ClimbingDown = climbingDown,
            TargetY = targetY,
            IgnoreHeadroomOnApproach = ignoreHeadroomOnApproach,
        };
    }

    /// <summary>
    /// Advances a climb one tick: walks to the mount point using local
    /// obstacle-aware stepping only (no RustNavMesh.CalculatePath - this
    /// monument deck's baked NavMesh turned out to be fragmented into small
    /// disconnected islands around its scattered dressing props, so a "big"
    /// navmesh route isn't reliable for this always-short approach anyway),
    /// then climbs straight to the far end of the ladder (up or down, per
    /// climb.ClimbingDown), then takes one obstacle-aware step off in the
    /// mount-normal direction to land on solid ground. No obstacle checking
    /// during the climb itself - a ladder's own vertical shaft is assumed
    /// clear, same as Rust's own ladder climbing.
    /// </summary>
    private ClimbOutcome AdvanceClimb(ClimbState climb, Survivor survivor, BasePlayer npc)
    {
        Vector3 current = npc.transform.position;

        if (climb.Approaching)
        {
            if (++climb.ApproachTicks > LadderApproachTimeoutTicks)
            {
                VerbosePuts($"'{survivor.Character.Alias}' couldn't reach the ladder's mount point.");
                return ClimbOutcome.TimedOut;
            }

            float approachStepDistance = WalkSpeed * WalkTickInterval;

            // A Blocked result leaves nextStep == current (a no-op this
            // tick) - simply retried up to the timeout above rather than
            // sidestepping, since the approach distance is always short.
            // IgnoreHeadroomOnApproach lets a specific authored ladder (see
            // MonumentWaypoint.ClimbLadderIgnoringHeadroom) skip the
            // headroom check here too - confirmed via a live test and a
            // /lr.debug.look scan that the Powerline tower's Ladder_4 sits
            // directly under 'Lvl5PlatformA' (y 41.91-45.02), so the
            // standing-height ray clips it on every approach regardless of
            // how close the walk-in gets first; every other ladder's
            // approach is untouched (defaults to full collision).
            StepResult step = _engine.NavigationManager.TryGetNextStep(current, climb.MountPoint, approachStepDistance, out Vector3 nextStep, out string approachBlockReason, climb.IgnoreHeadroomOnApproach);

            // Logged once per approach attempt, not every tick - "couldn't
            // reach the ladder's mount point" on its own never said why, and
            // the approach step deliberately has no sidestep fallback (the
            // assumption above only holds if nothing's actually in the way),
            // so a genuinely blocked approach silently retries the identical
            // line for the full 10s timeout with zero diagnostic.
            if (step == StepResult.Blocked && !climb.ApproachBlockLogged)
            {
                climb.ApproachBlockLogged = true;
                VerbosePuts($"'{survivor.Character.Alias}' approach to ladder mount point {climb.MountPoint} blocked: {approachBlockReason}");
            }

            ApplyMovementStep(survivor, npc, current, nextStep, step == StepResult.SteppedUp, sprinting: false, approachStepDistance);

            if (Vector3.Distance(nextStep, climb.MountPoint) < LadderMountArriveDistance)
            {
                climb.Approaching = false;

                // Drives the actual client-side climb animation - found
                // via reflection over Assembly-CSharp.dll that
                // ModelState.onLadder is the bool the animator reads,
                // and ladderType (matching TriggerLadder.Type) picks
                // rungs vs. rope. Cleared on dismount below, and in
                // ReleaseMovementState for the /lr.stop-mid-climb case.
                npc.modelState.onLadder = true;
                npc.modelState.ladderType = climb.Ladder.LadderType;

                // ApplyMovementStep (used during the approach above) sets
                // ducked/ducking whenever the last step happened to cross a
                // small obstacle, and nothing else ever clears it - without
                // this, whichever crouch state the final approach step left
                // behind carries into the entire vertical climb, making the
                // bot appear to randomly crouch-climb depending on what it
                // stepped over on the way to the ladder.
                npc.modelState.ducked = false;
                npc.modelState.ducking = 0f;

                // Setting the fields alone isn't enough - our bots are
                // connectionless BasePlayers that never process a real
                // PlayerTick, so nothing else ever diffs modelState
                // against lastModelState and broadcasts it. SendModelState
                // is the public method real players' own tick processing
                // calls internally; force:true skips that diff and sends
                // regardless.
                npc.SendModelState(true);
            }

            return ClimbOutcome.InProgress;
        }

        if (!climb.ReachedEnd)
        {
            float nextY = climb.ClimbingDown
                ? Mathf.Max(current.y - ClimbSpeed * WalkTickInterval, climb.TargetY)
                : Mathf.Min(current.y + ClimbSpeed * WalkTickInterval, climb.TargetY);
            Vector3 nextStep = new Vector3(climb.Ladder.Bottom.x, nextY, climb.Ladder.Bottom.z);

            FaceDirection(npc, climb.IntoLadder);
            npc.modelState.sprinting = false;
            npc.MovePosition(nextStep);

            survivor.Position = nextStep;
            survivor.Character.Position = nextStep;

            bool arrived = climb.ClimbingDown ? nextY <= climb.TargetY + 0.01f : nextY >= climb.TargetY - 0.01f;

            if (arrived)
            {
                climb.ReachedEnd = true;
                string end = climb.ClimbingDown ? "bottom" : "top";
                VerbosePuts($"'{survivor.Character.Alias}' reached the {end} of the ladder.");
            }

            return ClimbOutcome.InProgress;
        }

        // One obstacle-aware step off the ladder onto solid ground, rather
        // than guessing the platform's exact height - the ladder trigger's
        // own top/bottom and the real surface weren't measured as perfectly
        // level, so this leans on the same surface probe normal walking uses.
        Vector3 dismountAim = current + climb.IntoLadder * LadderDismountStepDistance;
        _engine.NavigationManager.TryGetNextStep(current, dismountAim, LadderDismountStepDistance, out Vector3 nextPosition, out _);

        FaceDirection(npc, climb.IntoLadder);
        npc.modelState.onLadder = false;
        npc.SendModelState(true);
        npc.MovePosition(nextPosition);

        survivor.Position = nextPosition;
        survivor.Character.Position = nextPosition;

        VerbosePuts($"'{survivor.Character.Alias}' finished climbing the ladder.");

        return ClimbOutcome.Finished;
    }

    /// <summary>
    /// Runs a climb as a standalone movement behaviour (the /lr.climb
    /// command), driving AdvanceClimb every tick until it finishes or times
    /// out.
    /// </summary>
    private void StartClimbing(Survivor survivor, ClimbState climb)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveMovement(survivor);

        Timer climbTimer = null;

        climbTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                climbTimer.Destroy();
                _activeMovement.Remove(characterId);
                return;
            }

            // Wounded/downed - pause entirely rather than fight Rust's own
            // incapacitated state machine, same rule as the walk/wiggle/
            // stuck-recovery timers (see StartWalking's own doc comment).
            // AdvanceClimb calls MovePosition every tick, so without this
            // check a survivor shot off a ladder would keep getting
            // dragged up/down it while downed.
            if (npc.IsWounded())
            {
                return;
            }

            if (AdvanceClimb(climb, survivor, npc) != ClimbOutcome.InProgress)
            {
                climbTimer.Destroy();
                _activeMovement.Remove(characterId);
            }
        });

        _activeMovement[characterId] = climbTimer;
    }

    /// <summary>
    /// Attaches and configures Rust's own native Gen2 navigation components
    /// - Unity's NavMeshAgent plus the RustNavMeshAgent wrapper real
    /// scientist2 NPCs use - on npc if not already present. Additive and
    /// idempotent (safe to call every tick; does nothing once both
    /// components already exist). Values are real, prefab-baked numbers
    /// read live off an actual scientist via /lr.debug.scientistnav during
    /// this feature's own prototyping, not guesses - see FollowAgentTypeID's
    /// doc comment for why that specifically matters.
    /// </summary>
    // How close a real on-mesh point needs to be before Warp() is trusted
    // to be a harmless re-sync rather than a real relocation (2026-08-29,
    // eighth round) - see this method's own Warp call site for the full
    // story. Generous enough for ordinary terrain noise, nowhere near
    // wide enough to reach past a base's own footprint and "find" real
    // outdoor navmesh from inside it.
    private const float NativeAgentWarpMaxSnapDistance = 1.5f;

    private void EnsureNativeNavAgent(BasePlayer npc, out RustNavMeshAgent agent, out NavMeshAgent unityAgent)
    {
        unityAgent = npc.GetComponent<NavMeshAgent>();

        if (unityAgent == null)
        {
            unityAgent = npc.gameObject.AddComponent<NavMeshAgent>();
            unityAgent.agentTypeID = FollowAgentTypeID;
            unityAgent.radius = FollowAgentRadius;
            unityAgent.height = FollowAgentHeight;
            unityAgent.baseOffset = FollowAgentBaseOffset;
            unityAgent.angularSpeed = FollowAgentAngularSpeed;
            unityAgent.acceleration = FollowAgentAcceleration;
            unityAgent.stoppingDistance = FollowAgentStoppingDistance;
            unityAgent.autoBraking = false;
            // LowQuality (not NoObstacleAvoidance, and not one of the more
            // expensive Good/HighQuality tiers) - confirmed via a live
            // spawnmany test that bots visibly phased straight through each
            // other during the loot phase with avoidance fully off. Low
            // is the cheapest tier that still makes agents aware of each
            // other's bodies, which matters directly for scale testing
            // (many agents means avoidance cost adds up fast per tier).
            unityAgent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;
            // Randomized (2026-08-15), not a flat 50 for every single agent -
            // root-caused a genuine mass-freeze via live trace: a live
            // 200-bot run showed ~32% of bots (83/256 traces) completely
            // motionless for minutes, sprinting=True the whole time (native
            // movement actively trying to run, not idle/stranded like every
            // other freeze fixed this session), and multiple DIFFERENT
            // frozen bots clustered within under 1m of each other at the
            // exact same beach spawn point (confirmed via trace positions:
            // 4 separate bots all within ~1m at (-1548, 6, 693)). This is a
            // known Unity NavMeshAgent crowd-avoidance gotcha - many agents
            // sharing the IDENTICAL avoidancePriority, packed tightly
            // together, have no way for the (Low-quality) avoidance solver
            // to resolve who yields to whom, and can deadlock permanently
            // with each agent perpetually treating the others as equal-
            // priority obstacles. Randomizing gives the solver a real
            // pecking order per agent instead of an unbreakable tie.
            unityAgent.avoidancePriority = UnityEngine.Random.Range(0, 100);
            unityAgent.areaMask = FollowAgentAreaMask;

            // Warp, not just placing the GameObject and hoping Unity
            // self-registers it (2026-08-15, real live bug traced at
            // supermarket_1: 'SneakyRanger' spawned right next to real,
            // close navmesh coverage - confirmed via /lr.debug.scan, real
            // navmesh 0.7-0.8m away at every nearby point checked - yet
            // native pathing reported isOnNavMesh=False from tick one and
            // fell straight to the cruder hand-built fallback for the
            // ENTIRE walk, immediately for a plain 5.5m indoor walk). A
            // freshly-added NavMeshAgent doesn't reliably self-snap onto
            // nearby navmesh just because it's spatially close - a known
            // Unity gotcha, confirmed by exactly this failure pattern
            // (close real coverage, agent still reports off-mesh). Warp()
            // is the real, documented fix: explicitly re-syncs the agent
            // onto the navmesh graph at its current position the instant
            // the component exists, rather than leaving it to whatever
            // Unity's own OnEnable proximity check happens to catch.
            unityAgent.Warp(npc.transform.position);
        }

        // Real live bug (2026-08-29, sixth round - Lucas's own live
        // report: a bot "instantly teleports out of the foundation" the
        // moment its first post-build walk starts, confirmed via trace -
        // a genuine 5.2m single-tick jump, BEFORE any door-open/stuck-
        // recovery log line ever fires, ruling out every door fix this
        // session). Root cause: this agent already existed (every bot
        // walks well before its first native-movement call this session,
        // e.g. during spawn/building) and was left disabled by a previous
        // CancelActiveMovement, same as the comment below already
        // documents - but only a BRAND NEW agent above ever gets Warp()ed
        // onto the survivor's current position. In between, this
        // project's own code moves the real transform directly many times
        // with no NavMeshAgent involved at all (placing each construction
        // piece, SnapToGround, phasing) - Unity's own internal tracking
        // for an existing-but-disabled agent goes stale and is never told
        // about any of that. Re-enabling it further below, then handing
        // it a fresh SetDestination, hits Unity's own well-known
        // "silently reconciles the transform onto wherever it still
        // thinks the agent belongs" behavior - CancelActiveMovement's own
        // doc comment already describes this exact mechanism for an
        // idle-but-enabled agent; this is the same mechanism, just
        // triggered by RE-enabling a stale one instead. Re-enable first
        // (Warp needs an active agent to actually take), then Warp
        // unconditionally on every call, not just first creation - this
        // re-syncs Unity's tracking to the real current position before
        // SetDestination ever runs, the same fix already proven for the
        // brand-new-agent case above, just applied on every reuse too.
        //
        // Re-enables an agent a previous fallback (or CancelActiveMovement)
        // disabled - see CancelActiveMovement's own doc comment for why an
        // enabled-but-idle NavMeshAgent still isn't safe to leave alone
        // (it keeps snapping onto the baked navmesh every frame) and why a
        // fallback disables it outright rather than just clearing its path.
        unityAgent.enabled = true;

        // Real live bug (2026-08-29, eighth round - Lucas's own report: a
        // fresh survivor's very first post-build task walk teleported it
        // straight outside its own base with zero door interaction at
        // all, confirmed via trace - a genuine 4.66m single-tick jump).
        // Root cause: Warp()'s own real, documented Unity behaviour is to
        // silently snap to the NEAREST point actually on the baked
        // NavMesh whenever the given position isn't already on it - and
        // standing inside a survivor's own just-finished base is exactly
        // that (the static navmesh has no idea player-built floors exist
        // at all), so the unconditional Warp introduced two rounds ago to
        // fix the STALE-position bug above traded it for a new, worse one:
        // it now actively relocates a survivor that's genuinely, validly
        // standing somewhere real, just off the static mesh. Only Warp
        // when a real on-mesh point exists within a small, ordinary-
        // terrain-noise tolerance - close enough that Warp's own
        // correction is what the stale-position fix actually needs
        // (re-syncing Unity's tracking to normal outdoor ground it
        // already knows about). Genuinely off-mesh (standing inside a
        // base, e.g.) skips Warp entirely, leaving the transform
        // untouched - native pathing then correctly self-reports
        // isOnNavMesh=False on its own and this project's own proven
        // hand-built fallback takes over, exactly as already happens
        // constantly elsewhere in this project for that same condition.
        if (NavMesh.SamplePosition(npc.transform.position, out NavMeshHit onMeshHit, NativeAgentWarpMaxSnapDistance, NavMesh.AllAreas))
        {
            unityAgent.Warp(npc.transform.position);
        }

        agent = npc.GetComponent<RustNavMeshAgent>();

        if (agent == null)
        {
            agent = npc.gameObject.AddComponent<RustNavMeshAgent>();
            agent.canOpenDoors = true;
        }
    }

    /// <summary>
    /// Starts (or restarts) continuous following of target. Walks at a
    /// comfortable pace normally, but breaks into a run to catch up once
    /// it falls far behind, dropping back to a walk once it's close again.
    ///
    /// Tries Rust's own native navigation (RustNavMeshAgent, the same
    /// component real scientist2 NPCs use - see EnsureNativeNavAgent) for
    /// ordinary movement first, since it routes around junkpile/monument
    /// geometry using the map's own real baked navmesh instead of this
    /// project's hand-built local-stepping. Falls back to the proven
    /// hand-built AdvanceAlongPath/climb engine (unchanged below) whenever
    /// native movement can't path somewhere or makes no real progress for
    /// FollowNativeGiveUpTicks - most importantly for ladder climbing,
    /// which stays exclusively hand-built (native ladder support was never
    /// verified to exist at all, unlike ordinary ground/building
    /// navigation which was). A native failure sticks for
    /// FollowNativeRetryCooldownTicks before trying native again, rather
    /// than fighting the same bad spot every single tick - by then the
    /// target has usually moved on anyway.
    /// </summary>
    /// <summary>
    /// stopDistance defaults to FollowStopDistance (the original companion-
    /// follow distance /lr.follow always used) - added as a parameter
    /// 2026-08-11 so combat (LivingRust.Combat.cs) can reuse this exact
    /// same proven movement engine to hold at a weapon-appropriate range
    /// instead of walking all the way to point-blank, rather than
    /// duplicating a parallel movement system of its own.
    ///
    /// skipIdleFacing (added 2026-08-13, combat-only) - once stopped/holding
    /// at stopDistance, this method normally keeps calling FaceDirection
    /// every tick to face the target. Live report: a combat bot would stop,
    /// keep visibly facing its LAST WALK direction while already shooting,
    /// then "snap" to face the target several seconds later. Root cause -
    /// FaceDirection sets OverrideViewAngles but never itself calls
    /// SendNetworkUpdate (it relies on incidental position-sync broadcasts
    /// to carry viewAngles along "for free," per AimAtPlayer's own doc
    /// comment - a mechanism that doesn't fire while genuinely stationary).
    /// Combat's own AimAtPlayer (LivingRust.Combat.cs) already owns facing
    /// during a fight, precisely computing the eye-to-eye aim direction and
    /// reliably pushing SendNetworkUpdateImmediate - but this method's own
    /// idle FaceDirection call, running independently on its own timer,
    /// kept fighting it for control of the same viewAngles value, and only
    /// AimAtPlayer's writes were reliably broadcast, so the stale
    /// FaceDirection value could linger visually until AimAtPlayer's next
    /// push happened to win the race. Setting this true lets combat opt out
    /// of this method's own idle facing entirely - normal companion-follow/
    /// looting callers are unaffected (default false, unchanged behaviour).
    /// </summary>
    // target widened from BasePlayer to BaseEntity (2026-08-14, animal
    // interaction) - this method only ever touches target.IsDestroyed and
    // target.transform.position, both present on BaseEntity, so a
    // BaseAnimalNPC (Bear/Wolf2/Polarbear, no BasePlayer relation at all)
    // can be followed exactly the same way a real player already is.
    private void StartFollowing(Survivor survivor, BaseEntity target, float stopDistance = FollowStopDistance, bool skipIdleFacing = false)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveMovement(survivor);

        bool sprinting = false;
        var follower = new PathFollower();
        ClimbState climb = null;
        int climbRetryCooldown = 0;
        float ladderSidestepAngle = 0f;
        bool reachedRampLandmark = false;

        bool useNative = !_noclipEnabled;
        int nativeCooldownTicks = 0;
        float nativeLastProgressDistance = float.MaxValue;
        int nativeStuckTicks = 0;
        RustNavMeshAgent nativeAgent = null;
        NavMeshAgent unityAgent = null;

        // See FollowNoPathRecoveryTicks' own doc comment - counts
        // consecutive NoPath outcomes so a single transient blip doesn't
        // trigger the full recovery ladder, only genuine persistence.
        int consecutiveNoPathTicks = 0;

        Timer followTimer = null;

        followTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            // !target.IsConnected was removed from this check 2026-08-11 -
            // combat (LivingRust.Combat.cs) reuses this method to follow a
            // bot-vs-bot attacker, which is never "connected" the way a
            // real player is. A real player target is always connected
            // anyway, so this is a strict superset of the original
            // behaviour, not a change to it.
            if (npc == null || npc.IsDestroyed || target == null || target.IsDestroyed)
            {
                followTimer.Destroy();
                _activeMovement.Remove(characterId);
                nativeAgent?.ResetPath();
                return;
            }

            // Wounded/downed - pause entirely rather than fight Rust's own
            // incapacitated state machine. See StartWalking's identical
            // check for the full reasoning (a live report of a bot
            // "getting back up unnaturally" while still nominally
            // following mid-task).
            if (npc.IsWounded())
            {
                return;
            }

            // See StartWalking's identical call/doc comment - unconditional
            // per-tick correction for Unity's own continuous underwater
            // drift, regardless of which branch the rest of this tick
            // takes.
            CorrectSwimDrift(survivor, npc);

            // No longer turns a following survivor back from water -
            // see StartWalking's identical fix/doc comment (2026-08-15,
            // later same session) for the full reasoning (real swimming
            // instead, drowning damage is a non-issue for these bots).

            if (climb != null)
            {
                ClimbOutcome climbOutcome = AdvanceClimb(climb, survivor, npc);

                if (climbOutcome != ClimbOutcome.InProgress)
                {
                    climb = null;

                    // The survivor just moved to a different navmesh island
                    // than whichever one the last path was computed on -
                    // force a fresh path next tick instead of resuming
                    // against stale corners.
                    follower.TicksSinceRepath = RepathIntervalTicks;
                    follower.ConsecutiveBlockedTicks = 0;

                    if (climbOutcome == ClimbOutcome.TimedOut)
                    {
                        climbRetryCooldown = FollowClimbRetryCooldownTicks;
                    }
                }

                return;
            }

            Vector3 current = npc.transform.position;
            Vector3 destination = target.transform.position;
            float distance = Vector3.Distance(current, destination);

            if (distance <= stopDistance)
            {
                if (!skipIdleFacing)
                {
                    FaceDirection(npc, destination - current);
                }

                npc.modelState.sprinting = false;
                npc.SendModelState(true);
                sprinting = false;
                nativeAgent?.ResetPath();
                SnapToGround(npc);
                return;
            }

            // Hysteresis avoids flip-flopping speed right at a boundary distance.
            if (distance >= CatchUpDistance)
            {
                sprinting = true;
            }
            else if (distance <= ResumeWalkDistance)
            {
                sprinting = false;
            }

            if (useNative)
            {
                // See AdvanceAlongPath's identical call/doc comment - a
                // currently-closed door in the way never gets opened by
                // native's own canOpenDoors=true on its own (that only fires
                // via NPCDoorTriggerBox's IsNpc-gated proximity trigger,
                // dead for a disconnected BasePlayer), so SetDestination
                // below can fail its very first attempt at any route that
                // needs one.
                TryOpenNearbyDoors(npc);

                // Always (not just on first creation) - re-enables an
                // agent a previous fallback disabled, since the cooldown
                // branch below only ever flips useNative back to true, it
                // never itself touches the components.
                EnsureNativeNavAgent(npc, out nativeAgent, out unityAgent);

                nativeAgent.speed = GetSwimAdjustedSpeed(npc, sprinting ? RunSpeed : WalkSpeed, IsSwimmingNow(npc.transform.position, survivor.Position.y));

                if (!nativeAgent.hasPath || Vector3.Distance((Vector3)nativeAgent.destination, destination) > FollowNativeRepathMoveThreshold)
                {
                    // Snapped onto the real navmesh first - see
                    // StartWalking's own doc comment for why a raw,
                    // non-navmesh-aware destination can make Unity's
                    // SetDestination silently refuse it. Lower-risk here
                    // than for a loot approach point (this destination is
                    // just wherever the real player is standing, normally
                    // solid ground already), but cheap insurance for
                    // whenever that's not quite true (e.g. right next to
                    // the same kind of wall/sandbag geometry).
                    Vector3 nativeTarget = destination;

                    if (nativeAgent.SamplePosition(destination, out NavMeshHit navHit, WalkNativeApproachSnapDistance))
                    {
                        nativeTarget = navHit.position;
                    }

                    nativeAgent.SetDestination(nativeTarget);
                }

                // See StartWalking's own doc comment - Unity computes a
                // path asynchronously, so hasPath/pathStatus aren't
                // trustworthy until pathPending resolves. Without this,
                // the tick right after a fresh SetDestination could read
                // as an instant failure and trigger a pointless fallback,
                // even though real navmesh coverage was right there.
                //
                // BUT capped, same as StartWalking's identical fix - a
                // permanently-stuck pathPending would otherwise silently
                // defeat every watchdog below it forever.
                if (nativeAgent.pathPending)
                {
                    if (++nativeStuckTicks >= FollowNativeGiveUpTicks)
                    {
                        VerbosePuts($"'{survivor.Character.Alias}' - native follow path calculation never resolved (stuck pathPending) for {FollowNativeGiveUpTicks * WalkTickInterval:F0}s, falling back to hand-built movement for a while.");

                        nativeStuckTicks = 0;
                        nativeLastProgressDistance = float.MaxValue;
                        useNative = false;
                        nativeCooldownTicks = FollowNativeRetryCooldownTicks;
                        nativeAgent.ResetPath();
                        unityAgent.enabled = false;
                    }

                    return;
                }

                bool nativeUsable = unityAgent.isOnNavMesh && nativeAgent.hasPath && nativeAgent.pathStatus != NavMeshPathStatus.PathInvalid;

                if (nativeUsable)
                {
                    // Fixed-window check, not reset-on-any-improvement -
                    // see WalkNativeWindowMinProgressDistance's own doc
                    // comment for the real live bug this replaced.
                    nativeStuckTicks++;

                    if (nativeStuckTicks >= FollowNativeGiveUpTicks)
                    {
                        if (nativeLastProgressDistance - distance >= WalkNativeWindowMinProgressDistance)
                        {
                            nativeLastProgressDistance = distance;
                            nativeStuckTicks = 0;
                        }
                    }

                    if (nativeStuckTicks < FollowNativeGiveUpTicks
                        && !_engine.NavigationManager.IsBodyOverlapping(npc.transform.position))
                    {
                        npc.modelState.sprinting = sprinting;
                        npc.modelState.ducked = false;
                        npc.modelState.ducking = 0f;
                        // See StartWalking's native branch for the full
                        // reasoning - real swim animation needs this set
                        // every tick ourselves.
                        npc.modelState.waterLevel = npc.WaterFactor();
                        // See StartWalking's identical line - clears the
                        // permanently-stuck-true OnGround flag while
                        // swimming.
                        npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
                        npc.SendModelState(true);

                        // skipIdleFacing was originally only checked at the
                        // "arrived/idle" branch above (distance <=
                        // stopDistance) - this native-movement branch
                        // ignored it entirely and always faced travel
                        // direction while actively pathing. Live trace
                        // evidence 2026-08-14: combat's own AimAtPlayer (now
                        // running every combat tick regardless of sprinting,
                        // see its own doc comment) was still fighting THIS
                        // call for facing ownership - both run on
                        // independent 0.05s timers, so whichever tick landed
                        // last won, producing rapid alternation between
                        // travel-direction and aim-direction rather than one
                        // big snap. Combat always passes skipIdleFacing:true
                        // specifically because AimAtPlayer already owns
                        // facing completely whenever it has LOS - this
                        // native branch needs to respect that the same way
                        // the idle branch already does, not just while
                        // stopped.
                        if (!skipIdleFacing)
                        {
                            FaceDirection(npc, unityAgent.velocity);
                        }

                        // See StartWalking's native branch for the full
                        // reasoning - floats a swimming survivor at the
                        // real water surface instead of following the
                        // baked navmesh's own (potentially seafloor) Y.
                        Vector3 swimAdjustedFollowPosition = ClampToWaterSurfaceIfSwimming(survivor, npc.transform.position, survivor.Position.y, IsSwimmingNow(npc.transform.position, survivor.Position.y));
                        npc.MovePosition(swimAdjustedFollowPosition);
                        survivor.Position = swimAdjustedFollowPosition;
                        survivor.Character.Position = swimAdjustedFollowPosition;

                        // Keeps the hand-built follower's own path state
                        // fresh in case we fall back later, so it repaths
                        // immediately instead of resuming a stale route.
                        follower.TicksSinceRepath = RepathIntervalTicks;
                        follower.ConsecutiveBlockedTicks = 0;

                        return;
                    }

                    VerbosePuts($"'{survivor.Character.Alias}' - native follow movement made no real progress for {FollowNativeGiveUpTicks * WalkTickInterval:F0}s, falling back to hand-built movement for a while.");
                }

                useNative = false;
                nativeCooldownTicks = FollowNativeRetryCooldownTicks;
                nativeStuckTicks = 0;
                nativeLastProgressDistance = float.MaxValue;
                nativeAgent.ResetPath();

                // Disabling outright, not just clearing the path - see
                // CancelActiveMovement's own doc comment for why this
                // specific gap (an enabled-but-pathless NavMeshAgent still
                // actively constrains its entity's position to the baked
                // ground navmesh every frame) was what actually caused a
                // live ladder-climb freeze: the hand-built fallback below
                // (including a ladder-climb detour, which routes through
                // AdvanceClimb rather than this function at all once
                // started) needs full, uncontested control of npc's
                // position, not just "no destination currently set."
                unityAgent.enabled = false;
            }
            else if (nativeCooldownTicks > 0)
            {
                nativeCooldownTicks--;

                if (nativeCooldownTicks <= 0)
                {
                    useNative = true;
                }
            }

            float speed = GetSwimAdjustedSpeed(npc, sprinting ? RunSpeed : WalkSpeed, IsSwimmingNow(npc.transform.position, survivor.Position.y));
            float stepDistance = speed * WalkTickInterval;

            MovementOutcome outcome = AdvanceAlongPath(follower, survivor, npc, destination, stepDistance, sprinting, followTarget: target as BasePlayer, out bool wasBlocked, skipFacing: skipIdleFacing);

            // wasBlocked alone (not just Stuck/NoPath) so a ladder detour
            // still gets a chance even on a tick a sidestep rescued into
            // Progressing - see AdvanceAlongPath's wasBlocked doc comment.
            if (outcome == MovementOutcome.Stuck || outcome == MovementOutcome.NoPath || wasBlocked)
            {
                if (climbRetryCooldown > 0)
                {
                    climbRetryCooldown--;
                }
                else if (TryBuildFollowClimb(survivor, npc, destination, out ClimbState newClimb))
                {
                    climb = newClimb;
                    return;
                }
                else
                {
                    climbRetryCooldown = FollowClimbRetryCooldownTicks;
                }

                // No ladder is directly climbable from here yet (e.g. the
                // nearest one within range is the segment just finished,
                // correctly rejected as the wrong direction) - confirmed
                // via a captured trace that this happens for real near the
                // top of the tower too, not just at ground level: the
                // target had moved on to the next platform up while the
                // survivor was still down near the previous one, and
                // straight-line stepping toward the target rammed into the
                // tower's own structural frame (a "step too high" block)
                // instead of finding the real ramp beside it. Walking
                // toward the nearest ladder's actual position - the same
                // fallback that solved the ground-level ramp for
                // /lr.climb.monument - gives a concrete, stable landmark
                // that's more likely to route across whatever ramp/stairs
                // actually connect the two levels. Ascending specifically,
                // try the Powerline tower's named ramp landmark first (see
                // TryStepTowardKnownRampEntry) before the raw-ladder-position
                // walk that misroutes into this tower's base structure.
                bool climbingDown = destination.y < npc.transform.position.y;

                if (climbingDown || !TryStepTowardKnownRampEntry(survivor, npc, ref ladderSidestepAngle, ref reachedRampLandmark))
                {
                    TryStepTowardNearestLadder(survivor, npc, climbingDown, ref ladderSidestepAngle);
                }
            }

            // Unlike a one-shot /lr.walk, following must never just give up -
            // but retrying the exact same failing motion forever (previously:
            // this return value was discarded entirely) is exactly what
            // produced the "stuck a few metres out, turning back and forth"
            // loop: the sidestep logic can get trapped fighting the same
            // obstruction from alternating angles since CornerIndex never
            // advances while sidestepping. Forcing an immediate repath at
            // least gives the navmesh solver a fresh chance to pick a
            // different corner sequence instead of retrying identical geometry.
            if (outcome == MovementOutcome.Stuck)
            {
                VerbosePuts($"'{survivor.Character.Alias}' is stuck following - forcing a repath.");
                follower.TicksSinceRepath = RepathIntervalTicks;
                follower.ConsecutiveBlockedTicks = 0;
                consecutiveNoPathTicks = 0;
                return;
            }

            // See FollowNoPathRecoveryTicks' own doc comment - a genuine
            // NoPath (not the softer Stuck above) means repathing alone
            // can't help, so this escalates to the real recovery ladder
            // instead of retrying the same doomed path forever.
            if (outcome == MovementOutcome.NoPath)
            {
                consecutiveNoPathTicks++;

                if (consecutiveNoPathTicks < FollowNoPathRecoveryTicks)
                {
                    return;
                }

                VerbosePuts($"'{survivor.Character.Alias}' following hit a persistent NoPath - trying real stuck-recovery (wiggle/navmesh-nudge/emergency-teleport) instead of just repathing.");

                followTimer.Destroy();
                _activeMovement.Remove(characterId);

                // Target's CURRENT position, not a stale snapshot - the
                // whole point of recovery here is just to get the survivor
                // unstuck from this one bad local spot, not to actually
                // "arrive" at the target (a moving player/bot). Both
                // outcomes simply resume normal following once recovery
                // finishes (or exhausts) - stopDistance/skipIdleFacing
                // carried through unchanged, same as a fresh StartFollowing
                // call from here.
                StartWalkingWithRecovery(
                    survivor,
                    target.transform.position,
                    onArrived: () => StartFollowing(survivor, target, stopDistance, skipIdleFacing),
                    onFailed: () => StartFollowing(survivor, target, stopDistance, skipIdleFacing));

                return;
            }

            consecutiveNoPathTicks = 0;
        });

        _activeMovement[characterId] = followTimer;
    }

    /// <summary>
    /// Drives a survivor toward a fixed point far above its starting
    /// position, using the same AdvanceAlongPath/ladder-detour loop
    /// StartFollowing uses. Unlike following, this is a one-shot goal: it
    /// terminates (rather than retrying forever) once ClimbMonumentGiveUpTicks
    /// of consecutive real movement (climbing counts, local-step counts,
    /// navmesh progress counts) go by, on the assumption that means it's
    /// genuinely reached as high as this monument goes.
    /// </summary>
    private void StartClimbingMonument(Survivor survivor)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveMovement(survivor);

        BasePlayer startNpc = survivor.Player;
        Vector3 destination = startNpc.transform.position + Vector3.up * ClimbMonumentGoalHeight;

        var follower = new PathFollower();
        ClimbState climb = null;
        int climbRetryCooldown = 0;
        int noProgressTicks = 0;
        float ladderSidestepAngle = 0f;
        bool reachedRampLandmark = false;

        Timer climbMonumentTimer = null;

        climbMonumentTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                climbMonumentTimer.Destroy();
                _activeMovement.Remove(characterId);
                return;
            }

            // Wounded/downed - pause entirely rather than fight Rust's own
            // incapacitated state machine. See StartWalking's identical
            // check for the full reasoning.
            if (npc.IsWounded())
            {
                return;
            }

            Vector3 positionBeforeTick = npc.transform.position;

            if (climb != null)
            {
                ClimbOutcome climbOutcome = AdvanceClimb(climb, survivor, npc);

                if (climbOutcome != ClimbOutcome.InProgress)
                {
                    climb = null;
                    noProgressTicks = 0;
                    follower.TicksSinceRepath = RepathIntervalTicks;
                    follower.ConsecutiveBlockedTicks = 0;

                    if (climbOutcome == ClimbOutcome.TimedOut)
                    {
                        climbRetryCooldown = FollowClimbRetryCooldownTicks;
                    }
                }

                return;
            }

            MovementOutcome outcome = AdvanceAlongPath(follower, survivor, npc, destination, WalkSpeed * WalkTickInterval, sprinting: false, followTarget: null, out bool wasBlocked);

            if (outcome == MovementOutcome.ReachedDestination)
            {
                climbMonumentTimer.Destroy();
                _activeMovement.Remove(characterId);
                VerbosePuts($"'{survivor.Character.Alias}' reached the top.");
                return;
            }

            // wasBlocked alone (not just Stuck/NoPath) so a ladder detour
            // still gets a chance even on a tick a sidestep rescued into
            // Progressing - see AdvanceAlongPath's wasBlocked doc comment.
            if (outcome == MovementOutcome.Stuck || outcome == MovementOutcome.NoPath || wasBlocked)
            {
                if (climbRetryCooldown > 0)
                {
                    climbRetryCooldown--;
                }
                else if (TryBuildFollowClimb(survivor, npc, destination, out ClimbState newClimb))
                {
                    climb = newClimb;
                    noProgressTicks = 0;
                    return;
                }
                else
                {
                    climbRetryCooldown = FollowClimbRetryCooldownTicks;
                }

                // No ladder is climbable from here yet (e.g. rejected by
                // LadderReachTolerance because it's above/below the
                // survivor's current level) - unlike a live follow target,
                // the fixed "100m up" destination has no real walking
                // direction of its own for AdvanceAlongPath's local-step
                // fallback to aim at, so that fallback can never make
                // progress here on its own. Walking toward the nearest
                // ladder's actual position instead gives a concrete,
                // reachable landmark - reaching it typically means crossing
                // whatever ramp/stairs the monument actually provides.
                // climbingDown is always false here - this whole command is
                // "go up," never down. Try the Powerline tower's named ramp
                // landmark first - see TryStepTowardKnownRampEntry - and
                // only fall back to the raw-ladder-position walk (the
                // heuristic that misroutes into this tower's base
                // structure) when no named ramp is nearby.
                if (!TryStepTowardKnownRampEntry(survivor, npc, ref ladderSidestepAngle, ref reachedRampLandmark))
                {
                    TryStepTowardNearestLadder(survivor, npc, climbingDown: false, ref ladderSidestepAngle);
                }
            }

            // AdvanceAlongPath (and the ladder-walk fallback above) can
            // both report success without the survivor actually having
            // gone anywhere - confirmed via a captured position trace
            // where the bot sat frozen at the exact same coordinates for
            // 17+ seconds while AdvanceAlongPath kept silently returning
            // Progressing (most likely a short, real, but useless path on
            // this monument's fragmented navmesh - technically valid, just
            // not one that gets any closer to a ladder or the real
            // destination). Tracking real distance moved makes the give-up
            // timeout robust to that instead of trusting the outcome label
            // alone.
            float movedDistance = Vector3.Distance(npc.transform.position, positionBeforeTick);

            if (movedDistance > ClimbMonumentMinProgressDistance)
            {
                noProgressTicks = 0;
                return;
            }

            if (++noProgressTicks > ClimbMonumentGiveUpTicks)
            {
                climbMonumentTimer.Destroy();
                _activeMovement.Remove(characterId);
                VerbosePuts($"'{survivor.Character.Alias}' couldn't find any more of the monument to climb - stopping at height {npc.transform.position.y:F1}.");
            }
        });

        _activeMovement[characterId] = climbMonumentTimer;
    }

    /// <summary>
    /// Steps once toward the nearest ladder's own mount-relevant position -
    /// its Bottom if ultimately climbing up, its Top if climbing down -
    /// using local obstacle-aware stepping, rather than assuming the
    /// survivor's current height like BuildClimbState does. Used as a
    /// fallback when no ladder is climbable yet (see LadderReachTolerance)
    /// - the ladder is still a concrete, reachable landmark worth walking
    /// toward even when the climb itself isn't available from here.
    ///
    /// climbingDown must come from the caller's own stable destination-vs-
    /// current-height comparison, not be re-derived here from the
    /// survivor's live position against the ladder's midpoint - confirmed
    /// via a captured trace that doing the latter flips the target between
    /// Top and Bottom on alternating ticks whenever the survivor's height
    /// hovers near a ladder's midpoint, producing a violent side-to-side
    /// jitter (visible in-game as "climbing" a non-ladder surface in a
    /// clearly inhuman, back-and-forth way) instead of steady progress.
    ///
    /// Falls back to the same sidestep fan AdvanceAlongPath uses for
    /// ordinary obstacles - without it, a straight line to the ladder that
    /// happens to clip solid structure (e.g. the underside of a ramp/
    /// stairs, confirmed via a captured trace + screenshot of a survivor
    /// frozen right at a ramp's edge) leaves this fallback with no way to
    /// route around it, even though a real path exists just to the side.
    ///
    /// Filters TryFindNearestLadder to only ladders that go the right way,
    /// rather than taking nearest-overall and rejecting it afterward -
    /// without that, if the segment just climbed is still the nearest
    /// collider in range (it always is - standing right on top of it,
    /// 0.3-2.9m away per a captured debug scan, versus 8m+ for the real
    /// next ladder), this would aim straight at that same ladder's *other*
    /// end and stop there permanently: every candidate angle still points
    /// at the same wrong-direction target, and the actually-reachable next
    /// ladder is never even considered even though it's well within
    /// FollowClimbSearchRadius. Confirmed via a captured trace: the
    /// survivor went completely motionless right after finishing a climb
    /// instead of continuing to the next segment.
    /// </summary>
    private bool TryStepTowardNearestLadder(Survivor survivor, BasePlayer npc, bool climbingDown, ref float preferredSidestepAngle)
    {
        Vector3 origin = npc.transform.position;

        if (!_engine.NavigationManager.TryFindNearestLadder(origin, FollowClimbSearchRadius, out NavigationManager.LadderInfo ladder,
                candidate => LadderGoesRightWay(candidate, origin, climbingDown)))
        {
            return false;
        }

        Vector3 target = climbingDown ? ladder.Top : ladder.Bottom;

        float stepDistance = WalkSpeed * WalkTickInterval;
        Vector3 current = npc.transform.position;

        StepResult step = _engine.NavigationManager.TryGetNextStep(current, target, stepDistance, out Vector3 nextStep, out _);

        if (step == StepResult.Blocked)
        {
            if (!TryFindSidestep(current, target, stepDistance, npc, exempt: null, ref preferredSidestepAngle, out nextStep, out step))
            {
                return false;
            }
        }
        else
        {
            preferredSidestepAngle = 0f;
        }

        ApplyMovementStep(survivor, npc, current, nextStep, step == StepResult.SteppedUp, sprinting: false, stepDistance);
        return true;
    }

    /// <summary>
    /// Steps toward the Powerline tower's ground-level ramp/stairs entrance
    /// by name (see PowerlineTowerRampName), when one exists nearby - see
    /// TryFindNamedStructure's doc comment for why this one structure needs
    /// a name-based landmark instead of the monument/ladder lookups
    /// everything else uses. Only relevant ascending: the misrouting bug
    /// this fixes only ever showed up climbing up from ground level, where
    /// TryStepTowardNearestLadder's straight line to the ladder's raw
    /// position cuts through the tower's dense base structure instead of
    /// finding the ramp beside it.
    ///
    /// Latches permanently false (via reachedRamp) the first time the
    /// survivor gets within arrive distance, rather than re-checking that
    /// distance live every tick - confirmed via a captured trace + log that
    /// a plain live threshold oscillates forever right at the boundary:
    /// once inside PowerlineTowerRampArriveDistance this returns false and
    /// TryStepTowardNearestLadder takes over aiming at ladder.Bottom, which
    /// sits far enough from the ramp's own center that walking toward it
    /// immediately exits the arrive radius again, flipping this back to
    /// true next tick - a ~160-170 degree heading reversal every tick,
    /// visible in-game as a violent near-180 degree spin. This only needs
    /// to get the survivor onto the ramp once; there's no reason to ever
    /// re-target it after that.
    /// </summary>
    private bool TryStepTowardKnownRampEntry(Survivor survivor, BasePlayer npc, ref float preferredSidestepAngle, ref bool reachedRamp)
    {
        if (reachedRamp)
        {
            return false;
        }

        if (!_engine.NavigationManager.TryFindNamedStructure(npc.transform.position, PowerlineTowerRampName, PowerlineTowerRampSearchRadius, out Vector3 rampPosition))
        {
            return false;
        }

        Vector3 current = npc.transform.position;

        if (Vector3.Distance(current, rampPosition) < PowerlineTowerRampArriveDistance)
        {
            reachedRamp = true;
            return false;
        }

        float stepDistance = WalkSpeed * WalkTickInterval;
        StepResult step = _engine.NavigationManager.TryGetNextStep(current, rampPosition, stepDistance, out Vector3 nextStep, out _);

        if (step == StepResult.Blocked)
        {
            if (!TryFindSidestep(current, rampPosition, stepDistance, npc, exempt: null, ref preferredSidestepAngle, out nextStep, out step))
            {
                return false;
            }
        }
        else
        {
            preferredSidestepAngle = 0f;
        }

        ApplyMovementStep(survivor, npc, current, nextStep, step == StepResult.SteppedUp, sprinting: false, stepDistance);
        return true;
    }

    /// <summary>
    /// Whether climbing this particular ladder in climbingDown's direction
    /// actually makes sense from the survivor's current position - true
    /// only when they're standing at the END of the ladder that direction
    /// climbs away from (near the Bottom to climb up, near the Top to
    /// climb down). Rejects e.g. re-climbing the segment just finished,
    /// which is often still the nearest one in range right after
    /// dismounting even though the real next ladder isn't reachable yet.
    /// </summary>
    private static bool LadderGoesRightWay(NavigationManager.LadderInfo ladder, Vector3 fromPosition, bool climbingDown)
    {
        float ladderMidpointY = (ladder.Bottom.y + ladder.Top.y) * 0.5f;
        bool nearTopOfLadder = fromPosition.y >= ladderMidpointY;

        return climbingDown == nearTopOfLadder;
    }

    /// <summary>
    /// Checks whether a nearby ladder would close the vertical gap toward
    /// destination, and if so builds a ClimbState for it. Called by both
    /// StartFollowing and StartClimbingMonument when path-following reports
    /// Stuck or NoPath - on this monument deck's fragmented navmesh (see
    /// NavigationManager notes), "destination is on a different level"
    /// surfaces as exactly that kind of stall rather than a normal
    /// blocked-path retry. The name/log wording still says "following"
    /// since that's still the more common caller.
    /// </summary>
    private bool TryBuildFollowClimb(Survivor survivor, BasePlayer npc, Vector3 destination, out ClimbState climb)
    {
        climb = null;

        Vector3 origin = npc.transform.position;
        float verticalGap = destination.y - origin.y;

        if (Mathf.Abs(verticalGap) < FollowClimbMinHeightGap)
        {
            // Silent on purpose in the common case (this fires on every
            // ordinary same-level blocked-path retry, which would flood the
            // log) - but confirmed via a captured trace + log that this can
            // also be the actual dead end: destination within height-gap
            // tolerance but AdvanceAlongPath still can't route there
            // (fragmented navmesh, no ladder needed) produces total silence
            // with zero movement, indistinguishable from a hang. Logging
            // here specifically is safe because this whole function is
            // already cooldown-gated to ~once per 5s by the caller.
            VerbosePuts($"'{survivor.Character.Alias}' can't progress following - within height range of destination ({verticalGap:F1}m) but still can't path there; no ladder detour applies.");
            return false;
        }

        bool climbingDown = verticalGap < 0f;

        // Direction is computed and filtered into the search itself, not
        // checked afterward against nearest-overall - confirmed via a
        // captured debug scan that the ladder just climbed sits only
        // 0.3-2.9m away (always "nearest") while the real next ladder can
        // be 8m+ away but still well within FollowClimbSearchRadius.
        // Rejecting nearest-overall for going the wrong way, rather than
        // considering the next-nearest candidate, permanently stalled here
        // even though a reachable ladder existed in range - confirmed via a
        // captured trace: the survivor went completely motionless right
        // after finishing a climb instead of continuing to the next segment.
        if (!_engine.NavigationManager.TryFindNearestLadder(origin, FollowClimbSearchRadius, out NavigationManager.LadderInfo ladder,
                candidate => LadderGoesRightWay(candidate, origin, climbingDown)))
        {
            VerbosePuts($"'{survivor.Character.Alias}' can't progress following - no ladder within {FollowClimbSearchRadius:F0}m goes the right way.");
            return false;
        }

        // BuildClimbState's approach assumes the survivor is already
        // roughly at the ladder's mount height (it uses their *current* Y
        // for the mount point, only offsetting sideways) - true whenever
        // they're genuinely standing on the platform a climb-up ladder
        // serves, since that trigger's own Bottom sits a bit *below* the
        // real deck surface by design (confirmed via /lr.debug.nearby - it
        // needs room to catch a player stepping toward the rungs from
        // above). It is NOT true from ground level below an elevated
        // ladder (e.g. still needs a ramp/stairs first) - confirmed via a
        // screenshot showing a survivor stuck trying to approach a ladder
        // ~2m above it, walking into the platform's support structure
        // instead of the ramp beside it. Reject rather than attempt a
        // physically wrong approach in that case.
        float relevantEndY = climbingDown ? ladder.Top.y : ladder.Bottom.y;
        bool notYetAtThisLevel = climbingDown
            ? npc.transform.position.y > relevantEndY + LadderReachTolerance
            : npc.transform.position.y < relevantEndY - LadderReachTolerance;

        if (notYetAtThisLevel)
        {
            VerbosePuts($"'{survivor.Character.Alias}' can't progress following - nearest ladder isn't at its height yet (needs a ramp/stairs first).");
            return false;
        }

        climb = BuildClimbState(npc, _engine.NavigationManager, ladder, climbingDown);

        string direction = climbingDown ? "down" : "up";
        VerbosePuts($"'{survivor.Character.Alias}' is detouring to climb {direction} a ladder while following.");

        return true;
    }

    /// <summary>
    /// Returns true once local-stepping has gone a full WalkGiveUpTicks
    /// window without shrinking real distance-to-destination by at least
    /// WalkWindowMinProgressDistance, updating follower's tracking fields
    /// as a side effect. Shared by every "we're about to report
    /// Progressing" site in AdvanceAlongPath - originally only the plain
    /// unblocked-step case checked this, but a sidestep rescue
    /// (TryFindSidestep succeeding) hits the identical silent-spin shape:
    /// each individual tick "succeeds" by finding some nearby step,
    /// resetting ConsecutiveBlockedTicks to 0 every time, so it can
    /// alternate direction indefinitely without ever advancing toward
    /// destination or tripping the separate Blocked/Stuck escalation at
    /// all. Confirmed live: a survivor following an admin standing atop
    /// an unreachable rooftop vent produced repath-failed spam for ~3s then
    /// went completely silent for over a minute - short of the plain
    /// not-blocked watchdog's own 10s threshold, consistent with control
    /// having moved into an unguarded sidestep-rescue loop instead.
    ///
    /// Fixed-window, not reset-on-any-improvement (2026-08-16) - see
    /// WalkWindowMinProgressDistance's own doc comment for the real live
    /// bug (SavageNomad994) this same reset-on-any-tiny-improvement shape
    /// caused in the outer walk timer's watchdog. The baseline here is
    /// likewise held fixed for the whole window instead of re-baselining
    /// on every sub-2cm wiggle, so small oscillation can no longer reset
    /// the clock indefinitely.
    ///
    /// Real elapsed TIME, not a tick count (2026-09-01 - a live 250-bot
    /// trace found this exact watchdog almost never tripping: 63 of 81
    /// heavily-blocked survivors got zero escalation at all within a 75s
    /// capture window, one logged the identical "blocked heading" line
    /// continuously for 43+ real seconds straight). Root cause: the OLD
    /// int tick-counter only incremented once per CALL to this method, but
    /// this method is called at wildly different real cadences depending
    /// on the caller - the plain unblocked-step site calls it every real
    /// movement tick (~0.05s), while the sidestep-rescue site only calls
    /// it once per SUCCESSFUL sidestep, which itself only happens once
    /// ConsecutiveBlockedTicks reaches SidestepAfterTicks (5) - so the
    /// "same" WalkGiveUpTicks threshold represented ~10s of real stall time
    /// for one caller and ~5x that (~50s+) for the other, silently, with
    /// the log message unconditionally claiming "10s" regardless. Using
    /// Time.realtimeSinceStartup directly makes the threshold mean the same
    /// real duration no matter which site or how often it's called.
    /// </summary>
    private bool LocalStepProgressWatchdogTripped(PathFollower follower, Survivor survivor, Vector3 appliedPosition, Vector3 destination)
    {
        float distanceRemaining = Vector3.Distance(appliedPosition, destination);
        float now = Time.realtimeSinceStartup;

        if (follower.LocalStepProgressWindowStartTime < 0f)
        {
            follower.LocalStepProgressWindowStartTime = now;
            follower.LastLocalStepProgressDistance = distanceRemaining;
            return false;
        }

        if (follower.LastLocalStepProgressDistance - distanceRemaining >= WalkWindowMinProgressDistance)
        {
            follower.LastLocalStepProgressDistance = distanceRemaining;
            follower.LocalStepProgressWindowStartTime = now;
            return false;
        }

        if (now - follower.LocalStepProgressWindowStartTime < WalkGiveUpTicks * WalkTickInterval)
        {
            return false;
        }

        VerbosePuts($"'{survivor.Character.Alias}' local-stepping toward {destination} made no real progress over the last {WalkGiveUpTicks * WalkTickInterval:F0}s despite individual steps 'succeeding' (including sidestep rescues) - treating as stuck rather than continuing to spin silently.");

        follower.LastLocalStepProgressDistance = float.MaxValue;
        follower.LocalStepProgressWindowStartTime = -1f;
        return true;
    }

    // How far past a hard-avoid animal's own position the detour waypoint
    // pushes out to the side (2026-09-15) - wide enough to give
    // HardAvoidAnimalRadius's own 50m detection a real miss, not a token
    // nudge that just re-triggers the check next repath.
    private const float HardAvoidAnimalSidestepDistance = 20f;

    /// <summary>
    /// Bends the immediate path target sideways around threatPosition,
    /// without ever touching the survivor's real destination - only the
    /// caller's local pathTarget for this one repath cycle is affected (see
    /// AdvanceAlongPath's own call site). Picks whichever side of the
    /// current->destination line the threat ISN'T already on, so the detour
    /// actually moves away from it rather than potentially cutting closer.
    /// </summary>
    private static Vector3 ComputeHardAvoidDetourPoint(Vector3 current, Vector3 destination, Vector3 threatPosition)
    {
        Vector3 toDestination = destination - current;
        float distanceToDestination = toDestination.magnitude;
        Vector3 forward = distanceToDestination > 0.01f ? toDestination.normalized : Vector3.forward;

        Vector3 sideways = Vector3.Cross(Vector3.up, forward).normalized;
        Vector3 currentToThreat = threatPosition - current;

        if (Vector3.Dot(sideways, currentToThreat) > 0f)
        {
            sideways = -sideways;
        }

        float forwardStep = Mathf.Min(HardAvoidAnimalSidestepDistance, distanceToDestination);
        return current + forward * forwardStep + sideways * HardAvoidAnimalSidestepDistance;
    }

    /// <summary>
    /// Advances one movement tick along a navmesh path toward destination -
    /// recomputing the path periodically (or immediately if the destination
    /// has moved significantly since the last calculation), then using the
    /// local step logic (obstacle step-up/block, water) for whichever
    /// segment of the path we're currently walking.
    /// </summary>
    private MovementOutcome AdvanceAlongPath(PathFollower follower, Survivor survivor, BasePlayer npc, Vector3 destination, float stepDistance, bool sprinting, BasePlayer followTarget, out bool wasBlocked, bool skipFacing = false)
    {
        Vector3 current = npc.transform.position;

        // See _noclipEnabled's own doc comment - a straight-line 3D flight
        // toward destination with zero collision/ground/navmesh awareness
        // at all, purely to isolate a real physical obstruction from a
        // pathing/logic bug. Deliberately bypasses everything below,
        // including ApplyMovementStep's own door-opening/water/step-clamp
        // logic - this is meant to look and feel like flying through walls,
        // not "movement but slightly more permissive."
        if (_noclipEnabled)
        {
            wasBlocked = false;
            Vector3 toDestination = destination - current;
            float remaining = toDestination.magnitude;
            Vector3 flown = remaining <= stepDistance ? destination : current + toDestination.normalized * stepDistance;

            if (!follower.NoclipDiagLogged)
            {
                follower.NoclipDiagLogged = true;
                Puts($"noclip-diag: '{survivor.Character.Alias}' entering noclip bypass at {current}, flying toward {destination} ({remaining:F1}m away), stepDistance={stepDistance:F2}.");
            }

            npc.MovePosition(flown);
            survivor.Position = flown;
            survivor.Character.Position = flown;

            // Periodic (every ~2s), not one-shot - proving whether the
            // position write above actually sticks by the NEXT tick, or
            // whether something else (Rust's own physics/collision
            // resolution on the real BasePlayer) is silently pushing it
            // back out. If flownPosition and actualPositionNextTick stay
            // in lockstep this is a pure logic bug elsewhere; if they
            // diverge, the MovePosition call itself isn't sticking.
            if (++follower.NoclipDiagTicks % 40 == 0)
            {
                Puts($"noclip-diag: '{survivor.Character.Alias}' intended {flown}, npc.transform.position now reads {npc.transform.position} ({remaining:F1}m from destination).");
            }

            if (!skipFacing && toDestination.sqrMagnitude > 0.0001f)
            {
                FaceDirection(npc, toDestination);
            }

            return remaining <= WaypointArriveDistance
                ? MovementOutcome.ReachedDestination
                : MovementOutcome.Progressing;
        }

        // True whenever the direct line toward the current target was
        // Blocked this tick, even if a sidestep then rescued it into
        // Progressing - callers use this to still offer a ladder detour a
        // chance to intercept (see the call sites' TryBuildFollowClimb
        // check). Without it, a ladder mount trigger - which has no solid
        // collision, only a Trigger volume our raycasts correctly ignore -
        // could get walked/sidestepped straight past instead of mounted,
        // since a successful sidestep used to fully mask the block from the
        // caller by reporting Progressing. Confirmed via a captured trace:
        // survivors repeatedly ending up on the far side of Ladder_1 or
        // Ladder_2, at the same low height as the ladder's base, instead of
        // ever triggering on_ladder.
        wasBlocked = false;

        // Real, decompile-confirmed root cause (2026-08-15) for the
        // "doorways act like brick walls" reports - a closed door carves a
        // real NavMeshObstacle hole in Rust's own baked navmesh (the exact
        // one RustNavMeshHelpers.CalculatePath below queries), and only
        // uncarves once actually opened. NPCDoorTriggerBox's automatic
        // open-on-approach only fires for BaseEntity.IsNpc==true, which a
        // plain disconnected BasePlayer never is - so the REAL pathfinder
        // was being asked to route through a door that, from its
        // perspective, genuinely doesn't exist as passable yet, every
        // single time. Previously this only ever got opened deep inside
        // ApplyMovementStep, by which point the real pathfinder had
        // already failed and handed off to the local stepper - too late to
        // help it succeed on a retry. Opening doors unconditionally BEFORE
        // every repath attempt gives the real navmesh a fair chance.
        TryOpenNearbyDoors(npc);

        // Wounded/downed overrides whatever speed the caller asked for -
        // a real player can't sprint while crawling.
        if (npc.IsWounded())
        {
            stepDistance = CrawlSpeed * WalkTickInterval;
            sprinting = false;
        }

        bool needsRepath = follower.Path.corners.Count == 0
            || follower.TicksSinceRepath >= RepathIntervalTicks
            || Vector3.Distance(follower.LastPathTarget, destination) > RepathMoveThreshold;

        if (needsRepath)
        {
            follower.TicksSinceRepath = 0;
            follower.LastPathTarget = destination;

            // Hard-avoid animal detour (2026-09-15, Lucas's own explicit
            // ask: "scan for hostile animals... hard avoid them (change
            // routing if possible away from the animal) and continue to the
            // original path where they wanted to go") - only ever bends
            // WHAT WE ASK THE PATHFINDER FOR this one repath cycle, never
            // the survivor's own real destination (follower.LastPathTarget
            // above already captured the real one). Once the animal's no
            // longer within HardAvoidAnimalRadius, the very next repath goes
            // straight back to the real destination with nothing to
            // "resume" - there's no separate avoidance state to clean up.
            //
            // Real destination is always tried as a fallback if the detour
            // point itself fails to path to (bad terrain, water, whatever) -
            // a failed DETOUR must never count against
            // ConsecutiveRepathFailureTicks/the give-up ladder below, which
            // has to keep reflecting the real destination's own
            // reachability only. Same "don't let a heuristic candidate
            // corrupt real failure state" lesson as this session's own
            // force-relocate ground-probe fix.
            Vector3 pathTarget = destination;
            bool avoidingHardThreat = TryFindNearbyHardAvoidAnimal(npc, out BaseEntity hardAvoidThreat);

            if (avoidingHardThreat)
            {
                pathTarget = ComputeHardAvoidDetourPoint(current, destination, hardAvoidThreat.transform.position);
            }

            bool pathed = _engine.NavigationManager.TryCalculatePath(current, pathTarget, follower.Path, out string pathFailureReason);

            if (!pathed && avoidingHardThreat)
            {
                pathed = _engine.NavigationManager.TryCalculatePath(current, destination, follower.Path, out pathFailureReason);
                avoidingHardThreat = false;
            }

            if (pathed)
            {
                follower.CornerIndex = follower.Path.corners.Count > 1 ? 1 : 0;
                follower.ConsecutiveRepathFailureTicks = 0;

                if (avoidingHardThreat)
                {
                    VerbosePuts($"'{survivor.Character.Alias}' steering around a '{hardAvoidThreat.ShortPrefabName}' within {HardAvoidAnimalRadius:F0}m - detouring, not fighting.");
                }
            }
            else
            {
                follower.LastPathFailureReason = pathFailureReason;
                follower.ConsecutiveRepathFailureTicks++;

                // Meant to log once per repath (~1s), not once per tick -
                // the branch below this can otherwise fail completely
                // silently (no Puts at all beyond NoPathLocalStepRadius),
                // which confirmed via a captured trace + log as
                // indistinguishable from a genuine hang: the survivor sat
                // frozen for 12+ real seconds with zero log output while
                // this exact case was happening underneath.
                //
                // The throttle actually has to be enforced here explicitly
                // (2026-09-15) - needsRepath's own corners.Count==0 check
                // re-fires every single tick once a path has failed (a
                // failed TryCalculatePath leaves corners empty), so without
                // this the "once per repath" framing above was aspirational
                // only: a genuinely stuck survivor was logging this line on
                // literally every tick (20/s) for the full
                // RepathFailureGiveUpTicks window, confirmed via a captured
                // trace showing dozens of identical-timestamp lines for the
                // same survivor. Retry cadence/give-up timing is unchanged -
                // only the logging is throttled.
                if (follower.ConsecutiveRepathFailureTicks == 1 || follower.ConsecutiveRepathFailureTicks % RepathIntervalTicks == 0)
                {
                    float distanceToDestination = Vector3.Distance(current, destination);
                    string localStepNote = distanceToDestination <= NoPathLocalStepRadius
                        ? "trying local stepping instead"
                        : $"too far ({distanceToDestination:F0}m > {NoPathLocalStepRadius:F0}m) for local stepping to help";
                    VerbosePuts($"'{survivor.Character.Alias}' repath failed ({pathFailureReason}), {localStepNote}.");
                }
            }
        }
        else
        {
            follower.TicksSinceRepath++;
        }

        if (follower.Path.corners.Count == 0)
        {
            if (follower.ConsecutiveRepathFailureTicks >= RepathFailureGiveUpTicks)
            {
                // See RepathFailureGiveUpTicks's own doc comment - the real
                // path calculation has failed identically, this consistently,
                // for long enough that local-stepping's own step-by-step
                // "did this exact tick succeed" view can't be trusted to
                // ever notice on its own; give up now rather than let it
                // keep circling.
                VerbosePuts($"'{survivor.Character.Alias}' repath has failed for {RepathFailureGiveUpTicks * WalkTickInterval:F0}s straight ({follower.LastPathFailureReason}) - treating as genuinely unreachable rather than continuing to retry.");

                wasBlocked = true;

                ReleaseMovementState(npc);
                if (!skipFacing) { FaceDirection(npc, destination - current); }
                return MovementOutcome.NoPath;
            }

            // This monument's baked NavMesh is fragmented into small
            // disconnected islands around its own scattered dressing props
            // (same finding that made the ladder-approach code deliberately
            // skip RustNavMesh.CalculatePath entirely) - a nearby target can
            // fail to path to even when it's a completely open, walkable
            // straight line away. Rather than give up outright, fall back to
            // the same local obstacle-aware stepping the ladder approach
            // uses, but only within a short radius - beyond that a failed
            // path is more likely a genuine "can't get there locally" case
            // (e.g. needs a ladder) that local stepping alone can't solve.
            if (Vector3.Distance(current, destination) <= NoPathLocalStepRadius)
            {
                StepResult localStep = _engine.NavigationManager.TryGetNextStep(current, destination, stepDistance, out Vector3 localNextStep, out string localBlockReason);

                if (localStep == StepResult.Blocked)
                {
                    wasBlocked = true;

                    // Unlike the normal below-corner-path branch, this one
                    // used to give up the instant the single direct line was
                    // blocked - no sidestep attempt at all. Confirmed via a
                    // captured trace that this is exactly what strands a
                    // followed survivor near a PathInvalid navmesh gap: since
                    // the destination is the live followed player, it keeps
                    // moving enough to force a fresh repath almost every
                    // tick, which routes through this branch instead of the
                    // sidestep-aware one below, so it never even tries to
                    // route around whatever it's actually blocked by.
                    if (TryFindSidestep(current, destination, stepDistance, npc, followTarget, ref follower.PreferredSidestepAngle, out Vector3 sidestepTarget, out StepResult sidestepResult))
                    {
                        follower.ConsecutiveBlockedTicks = 0;
                        Vector3 sidestepApplied = ApplyMovementStep(survivor, npc, current, sidestepTarget, sidestepResult == StepResult.SteppedUp, sprinting, stepDistance, skipFacing);

                        if (LocalStepProgressWatchdogTripped(follower, survivor, sidestepApplied, destination))
                        {
                            wasBlocked = true;
                            ReleaseMovementState(npc);
                            if (!skipFacing) { FaceDirection(npc, destination - current); }
                            return MovementOutcome.NoPath;
                        }

                        return MovementOutcome.Progressing;
                    }

                    // Logged once per stuck episode, not every tick - this
                    // branch previously had no diagnostic at all beyond the
                    // repath-failed line above, which only names the
                    // destination's nearest navmesh point, never what's
                    // actually stopping the survivor from reaching it. A
                    // captured trace showed a survivor frozen here for 20+
                    // seconds with no way to tell which collider (or a
                    // headroom/duck check) was the real cause without a
                    // manual debug scan.
                    if (follower.ConsecutiveBlockedTicks == 0)
                    {
                        VerbosePuts($"'{survivor.Character.Alias}' local-step blocked heading toward {destination} (no path, sidestep also failed): {localBlockReason}");
                    }

                    follower.ConsecutiveBlockedTicks++;
                }
                else
                {
                    follower.ConsecutiveBlockedTicks = 0;
                    follower.PreferredSidestepAngle = 0f;

                    Vector3 applied = ApplyMovementStep(survivor, npc, current, localNextStep, localStep == StepResult.SteppedUp, sprinting, stepDistance, skipFacing);

                    if (Vector3.Distance(applied, destination) < WaypointArriveDistance)
                    {
                        follower.LastLocalStepProgressDistance = float.MaxValue;
                        follower.LocalStepProgressWindowStartTime = -1f;
                        return MovementOutcome.ReachedDestination;
                    }

                    if (LocalStepProgressWatchdogTripped(follower, survivor, applied, destination))
                    {
                        wasBlocked = true;
                        ReleaseMovementState(npc);
                        if (!skipFacing) { FaceDirection(npc, destination - current); }
                        return MovementOutcome.NoPath;
                    }

                    return MovementOutcome.Progressing;
                }
            }

            // Also covers the "too far for local stepping to help" case
            // (the outer distance check above) - a fixed destination far
            // beyond NoPathLocalStepRadius with a real vertical gap is
            // exactly what a ladder detour is for (see
            // StartClimbingMonument's "100m straight up" goal), not
            // something local stepping alone could ever close anyway.
            wasBlocked = true;

            ReleaseMovementState(npc);
            if (!skipFacing) { FaceDirection(npc, destination - current); }
            return MovementOutcome.NoPath;
        }

        Vector3 waypoint = follower.CornerIndex < follower.Path.corners.Count
            ? (Vector3)follower.Path.corners[follower.CornerIndex]
            : destination;

        // See TryGetNextStep's own ignoreStepHeight doc comment - a
        // swimming survivor's Y is externally locked regardless of real
        // underwater terrain shape, so the terrain's own step-height
        // limits shouldn't be able to block it at all.
        bool isSwimmingThisStep = IsSwimmingNow(current, survivor.Position.y);

        StepResult step = _engine.NavigationManager.TryGetNextStep(current, waypoint, stepDistance, out Vector3 nextStep, out string blockReason, ignoreStepHeight: isSwimmingThisStep);

        // No longer blocks on water at all (2026-08-15, later same session) -
        // this was the hand-built stepper's OWN independent water guard,
        // separate from (and missed by) the native-movement-focused
        // "turn back from water" removal earlier this exact fix - bots
        // falling back to hand-built movement (common - see the many
        // "falling back to hand-built movement" log lines) were still
        // hard-stopping at any water at all even after that change, an
        // inconsistency depending purely on which movement engine happened
        // to be active at the water's edge. Real swimming now applies
        // uniformly regardless of engine.

        bool blockedByOtherPlayer = step != StepResult.Blocked && IsBlockedByOtherPlayer(nextStep, npc, followTarget);
        bool blockedByHorse = step != StepResult.Blocked && !blockedByOtherPlayer && IsBlockedByHorse(nextStep);
        bool blockedByBike = step != StepResult.Blocked && !blockedByOtherPlayer && !blockedByHorse && IsBlockedByBike(nextStep);
        bool blockedByCactus = step != StepResult.Blocked && !blockedByOtherPlayer && !blockedByHorse && !blockedByBike && IsBlockedByCactus(nextStep);
        bool blockedByBalloon = step != StepResult.Blocked && !blockedByOtherPlayer && !blockedByHorse && !blockedByBike && !blockedByCactus && IsBlockedByHotAirBalloon(nextStep);
        bool blockedByWoodBarricade = step != StepResult.Blocked && !blockedByOtherPlayer && !blockedByHorse && !blockedByBike && !blockedByCactus && !blockedByBalloon && IsBlockedByWoodBarricade(nextStep);

        if (step == StepResult.Blocked || blockedByOtherPlayer || blockedByHorse || blockedByBike || blockedByCactus || blockedByBalloon || blockedByWoodBarricade)
        {
            wasBlocked = true;
            follower.ConsecutiveBlockedTicks++;

            // Logged once per blocked "episode" (not every tick, which at
            // 20Hz would flood the console) - this is the actual diagnostic
            // for the ramp oscillation: names exactly which collider stopped
            // it (or that a step was too tall) the moment it first happens,
            // instead of guessing from the symptom alone.
            if (follower.ConsecutiveBlockedTicks == 1)
            {
                string reason = blockedByOtherPlayer ? "another player/bot is in the way" : blockedByHorse ? "a horse is in the way" : blockedByBike ? "a bike is in the way" : blockedByCactus ? "a cactus is in the way" : blockedByBalloon ? "a hot air balloon is in the way" : blockedByWoodBarricade ? "a wooden barricade is in the way" : blockReason;
                VerbosePuts($"'{survivor.Character.Alias}' blocked heading to waypoint {waypoint}: {reason}");
            }

            // Trees, junkpile scatter, other players/bots, and other
            // dynamic obstacles aren't part of the baked navmesh, so
            // repathing alone won't route around them - try sidestepping
            // around whatever's directly in front instead of just freezing.
            if (follower.ConsecutiveBlockedTicks >= SidestepAfterTicks
                && TryFindSidestep(current, waypoint, stepDistance, npc, followTarget, ref follower.PreferredSidestepAngle, out Vector3 sidestepTarget, out StepResult sidestepResult))
            {
                Vector3 sidestepApplied = ApplyMovementStep(survivor, npc, current, sidestepTarget, sidestepResult == StepResult.SteppedUp, sprinting, stepDistance, skipFacing);
                follower.ConsecutiveBlockedTicks = 0;

                // Without this, a sidestep never counted as "arriving"
                // anywhere - CornerIndex only ever advanced from a normal,
                // unblocked step below. Confirmed via a captured trace: over
                // several sidestep ticks the survivor can walk past or
                // alongside the waypoint it's dodging around, at which point
                // it's aiming at a point now behind it, producing a walk-
                // forward-then-reverse cycle repeating for 10+ seconds
                // instead of ever advancing to the next corner.
                if (Vector3.Distance(sidestepTarget, waypoint) < WaypointArriveDistance && follower.CornerIndex < follower.Path.corners.Count - 1)
                {
                    follower.CornerIndex++;
                }

                // See LocalStepProgressWatchdogTripped's own doc comment -
                // this sidestep-rescue can itself repeat indefinitely
                // (ConsecutiveBlockedTicks resets to 0 every successful
                // rescue), so it needs the exact same real-distance
                // watchdog as a plain unblocked step, not just the
                // CornerIndex bookkeeping above.
                if (LocalStepProgressWatchdogTripped(follower, survivor, sidestepApplied, destination))
                {
                    wasBlocked = true;
                    ReleaseMovementState(npc);
                    if (!skipFacing) { FaceDirection(npc, destination - current); }
                    return MovementOutcome.NoPath;
                }

                return MovementOutcome.Progressing;
            }

            ReleaseMovementState(npc);
            if (!skipFacing) { FaceDirection(npc, waypoint - current); }

            return follower.ConsecutiveBlockedTicks >= MaxConsecutiveBlockedTicks
                ? MovementOutcome.Stuck
                : MovementOutcome.Blocked;
        }

        follower.ConsecutiveBlockedTicks = 0;
        follower.PreferredSidestepAngle = 0f;

        nextStep = ApplyMovementStep(survivor, npc, current, nextStep, step == StepResult.SteppedUp, sprinting, stepDistance, skipFacing);

        if (Vector3.Distance(nextStep, waypoint) < WaypointArriveDistance && follower.CornerIndex < follower.Path.corners.Count - 1)
        {
            follower.CornerIndex++;
        }

        if (Vector3.Distance(nextStep, destination) < WaypointArriveDistance)
        {
            follower.LastLocalStepProgressDistance = float.MaxValue;
            follower.LocalStepProgressWindowStartTime = -1f;
            return MovementOutcome.ReachedDestination;
        }

        // Same silent-stall shape as the corners.Count==0 fallback branch
        // above (see LocalStepProgressWatchdogTripped's own doc comment),
        // but for the far more common case: a real path WAS found,
        // individual steps keep reporting "not blocked" (e.g. repeatedly
        // sidestepping near a corner without CornerIndex ever advancing),
        // yet real distance to the final destination never meaningfully
        // shrinks. Confirmed live: a survivor went fully silent for 36+
        // seconds right after this exact branch took over from a
        // failed-repath fallback, with no Blocked/stuck log of any kind.
        if (LocalStepProgressWatchdogTripped(follower, survivor, nextStep, destination))
        {
            wasBlocked = true;
            ReleaseMovementState(npc);
            if (!skipFacing) { FaceDirection(npc, destination - current); }
            return MovementOutcome.NoPath;
        }

        return MovementOutcome.Progressing;
    }

    /// <summary>
    /// Tries a fan of headings around the direct line to waypoint, looking
    /// for one that isn't blocked - lets the bot flow around a small,
    /// localized obstruction it doesn't have any strategic route around.
    ///
    /// preferredAngle (0 means "none yet") is tried first, before scanning
    /// the full fan - confirmed via a captured trace that always scanning
    /// fresh from the same fixed order let two roughly-opposite candidates
    /// alternate "winning" as the tiniest position change flipped which one
    /// looked clearer, producing a rapid side-to-side facing/position
    /// jitter instead of committing to a route around the obstacle. Reset
    /// to 0 by the caller once actually unblocked again, so a stale
    /// preference from one obstacle doesn't linger into an unrelated one.
    /// </summary>
    private bool TryFindSidestep(Vector3 current, Vector3 waypoint, float stepDistance, BasePlayer self, BasePlayer exempt, ref float preferredAngle, out Vector3 sidestepTarget, out StepResult resultOut, bool ignoreHeadroom = false)
    {
        Vector3 baseDirection = waypoint - current;
        baseDirection.y = 0f;

        if (baseDirection.sqrMagnitude > 0.0001f)
        {
            baseDirection.Normalize();

            if (preferredAngle != 0f
                && TrySidestepAngle(current, baseDirection, preferredAngle, stepDistance, self, exempt, out sidestepTarget, out resultOut, ignoreHeadroom))
            {
                return true;
            }

            foreach (float angle in SidestepAngles)
            {
                if (TrySidestepAngle(current, baseDirection, angle, stepDistance, self, exempt, out sidestepTarget, out resultOut, ignoreHeadroom))
                {
                    preferredAngle = angle;
                    return true;
                }
            }
        }

        sidestepTarget = current;
        resultOut = StepResult.Blocked;
        preferredAngle = 0f;
        return false;
    }

    private bool TrySidestepAngle(Vector3 current, Vector3 baseDirection, float angle, float stepDistance, BasePlayer self, BasePlayer exempt, out Vector3 sidestepTarget, out StepResult resultOut, bool ignoreHeadroom = false)
    {
        Vector3 candidateDirection = Quaternion.Euler(0f, angle, 0f) * baseDirection;
        Vector3 candidateAimPoint = current + candidateDirection * (stepDistance * 3f);

        // See TryGetNextStep's own ignoreStepHeight doc comment - self's
        // own current Y stands in for a tracked "previous Y" here (no
        // Survivor reference available in this function), close enough
        // for a sidestep's own minor local hysteresis check.
        bool isSwimmingThisSidestep = IsSwimmingNow(current, self.transform.position.y);

        StepResult candidateResult = _engine.NavigationManager.TryGetNextStep(current, candidateAimPoint, stepDistance, out Vector3 candidateStep, out _, ignoreHeadroom, ignoreStepHeight: isSwimmingThisSidestep);

        if (candidateResult != StepResult.Blocked
            && !_engine.NavigationManager.IsWater(candidateStep)
            && !IsBlockedByOtherPlayer(candidateStep, self, exempt)
            && !IsBlockedByHorse(candidateStep)
            && !IsBlockedByBike(candidateStep)
            && !IsBlockedByCactus(candidateStep)
            && !IsBlockedByHotAirBalloon(candidateStep)
            && !IsBlockedByWoodBarricade(candidateStep))
        {
            sidestepTarget = candidateStep;
            resultOut = candidateResult;
            return true;
        }

        sidestepTarget = current;
        resultOut = StepResult.Blocked;
        return false;
    }

    /// <summary>
    /// Applies one movement step: facing, sprint/duck animation state,
    /// the actual position update, and keeping the Survivor's tracked
    /// position in sync. Clamps how far the Y position can rise/fall in
    /// one tick (MaxClimbSpeed) so stepping onto an obstacle looks like
    /// climbing rather than teleporting upward instantly - and returns
    /// the actually-applied (clamped) position so callers use the real
    /// value for any distance checks. Logs a warning if horizontal+
    /// vertical movement still exceeds the intended step distance after
    /// clamping, as a diagnostic for catching any future oversized-step bug.
    /// </summary>
    /// <summary>
    /// Doors near the survivor, right before every hand-built step -
    /// real, decompile-confirmed gap found live tonight (2026-08-15): the
    /// ONLY door-opening code in this entire project was
    /// `agent.canOpenDoors = true` on the NATIVE RustNavMeshAgent
    /// (EnsureNativeNavAgent), which internally calls the real
    /// NPCDoorTriggerBox.TryOpenDoorFor() on its own. The hand-built
    /// fallback stepper (this function, and everything that calls it -
    /// walk/follow/climb/sidestep) had ZERO door logic at all, and
    /// NPCDoorTriggerBox's own automatic OnTriggerEnter open-for-NPCs path
    /// never helps either, since it gates on BaseEntity.IsNpc, which
    /// defaults to false and is never overridden for a plain disconnected
    /// BasePlayer (confirmed via decompile - only real NPCPlayer subclasses
    /// override it true). Given native pathing fails and falls back to
    /// this exact engine constantly (confirmed repeatedly across tonight's
    /// live traces at Abandoned Supermarket), a closed door was
    /// structurally impassable the instant hand-built took over - matching
    /// Lucas's own live report that /lr.follow got stuck at a doorway too,
    /// independent of any loot-zone logic. Fixed by calling the exact same
    /// real API RustNavMeshAgent itself uses (NPCDoorTriggerBox.AllDoors,
    /// a real spatial SparseGrid) directly from here, so hand-built
    /// movement gets door-opening regardless of which engine is currently
    /// driving.
    /// </summary>
    private static void TryOpenNearbyDoors(BasePlayer npc)
    {
        List<NPCDoorTriggerBox> nearbyDoors = new();
        NPCDoorTriggerBox.AllDoors.GetNeighboors(npc.transform.position, nearbyDoors);

        foreach (NPCDoorTriggerBox doorBox in nearbyDoors)
        {
            doorBox.TryOpenDoorFor(npc);
        }
    }

    private Vector3 ApplyMovementStep(Survivor survivor, BasePlayer npc, Vector3 current, Vector3 nextStep, bool steppingUp, bool sprinting, float expectedStepDistance, bool skipFacing = false)
    {
        TryOpenNearbyDoors(npc);

        float maxVerticalDelta = MaxClimbSpeed * WalkTickInterval;
        nextStep.y = Mathf.Clamp(nextStep.y, current.y - maxVerticalDelta, current.y + maxVerticalDelta);

        // See ClampToWaterSurfaceIfSwimming's own doc comment - overrides
        // whatever ground-following Y the clamp above computed, so a
        // swimming survivor floats at the real water surface instead of
        // walking the seafloor.
        nextStep = ClampToWaterSurfaceIfSwimming(survivor, nextStep, current.y, IsSwimmingNow(nextStep, current.y));

        float actualDistance = Vector3.Distance(current, nextStep);

        // expectedStepDistance is a pure HORIZONTAL speed budget
        // (Speed * WalkTickInterval) - actualDistance is the full 3D
        // distance including whatever vertical rise/fall terrain following
        // legitimately added this tick (clamped to maxVerticalDelta above,
        // but still often well past the old flat +0.05f tolerance on
        // ordinary rolling terrain, which is most of a Rust map). A live
        // 200-bot trace found this WARNING firing on 87% of all log lines -
        // not rare anomalies, just normal slope-following being compared
        // against a horizontal-only expectation. Pythagorean-combining the
        // real vertical delta into the expectation (rather than a flat
        // fudge factor) keeps this a genuine diagnostic for an actual
        // oversized step, instead of near-constant noise on any incline.
        float verticalDelta = Mathf.Abs(nextStep.y - current.y);
        float expectedDistanceWithClimb = Mathf.Sqrt(expectedStepDistance * expectedStepDistance + verticalDelta * verticalDelta);

        if (actualDistance > expectedDistanceWithClimb + 0.05f)
        {
            Puts($"WARNING: '{survivor.Character.Alias}' moved {actualDistance:F2}m in one tick (expected <= {expectedDistanceWithClimb:F2}m accounting for {verticalDelta:F2}m of climb, sprinting: {sprinting}) from {current} to {nextStep}.");
        }

        // skipFacing (2026-08-14) - combat's own AimAtPlayer already owns
        // facing entirely whenever it has LOS (see StartFollowing's
        // skipIdleFacing doc comment) - this is the hand-built movement
        // fallback's equivalent of the same gate already applied to native
        // movement's own FaceDirection call. Live trace evidence: a bot
        // observed with several metres of vertical separation from its
        // target froze facing for the better part of a second then snapped
        // hard - the native NavMeshAgent path is exactly what's most likely
        // to fail near uneven/elevated terrain (see this codebase's own
        // prior notes on rooftop/slope navmesh gaps), dropping combat
        // movement into THIS fallback, whose FaceDirection call was still
        // completely unguarded and fighting AimAtPlayer the same way the
        // native branch used to.
        if (!skipFacing)
        {
            FaceDirection(npc, nextStep - current);
        }

        npc.modelState.sprinting = sprinting;

        // Real "why are bots jogging, not sprinting" fix (2026-09-01,
        // Lucas's own live report once bots were actually covering ground
        // again post-terrain-climb-fix: "not full sprint, not walking,
        // halfway inbetween"). Root cause: this used to be `steppingUp`
        // (StepResult.SteppedUp from TryGetNextStep, which fires for ANY
        // step with more than a bare 5cm of rise - NavigationManager.cs's
        // own `stepUpHeight > 0.05f` threshold) - on ordinary outdoor
        // terrain (grass, gentle slopes, basically anywhere that isn't a
        // paved road), a survivor moving at real RunSpeed covers enough
        // ground per 0.05s tick that a 5cm+ rise is completely normal, not
        // a genuine "step up onto an obstacle" moment. ducked/ducking is
        // Rust's own real CROUCH-WALK state, a distinct, visibly hunched,
        // slower-looking animation - tying it to ordinary terrain noise
        // meant survivors spent most of any real run rendered mid-crouch,
        // which reads exactly like neither a walk nor a sprint. steppingUp
        // itself isn't used for anything else inside this function (the
        // actual step height was already resolved into nextStep.y by the
        // caller before this ever runs) - always false here now, matching
        // every other real ducked/ducking assignment in this project
        // (native movement, combat's own facing/aim ticks), none of which
        // ever tied this to step height either.
        npc.modelState.ducked = false;
        npc.modelState.ducking = 0f;
        // See the native-movement branch's identical line for the full
        // reasoning - real swim animation needs this set every tick
        // ourselves, since a disconnected bot never sends the real
        // PlayerTick RPC that normally keeps it in sync.
        npc.modelState.waterLevel = npc.WaterFactor();
        // See the native-movement branch's identical line - clears the
        // permanently-stuck-true OnGround flag while swimming.
        npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
        npc.SendModelState(true);

        npc.MovePosition(nextStep);

        survivor.Position = nextStep;
        survivor.Character.Position = nextStep;

        // Every successful step here (walk, follow, wiggle) is by
        // definition real, unblocked movement - see
        // Survivor.LastKnownGoodPosition's own doc comment for why this
        // matters: emergency-teleport recovery prefers relocating back to
        // the most recent position actually walked to, over a blind
        // random-direction guess.
        survivor.LastKnownGoodPosition = nextStep;

        return nextStep;
    }

    /// <summary>
    /// Whether another player/bot (not self, and not an explicitly exempt
    /// target - e.g. the player being followed) occupies this position,
    /// so bots don't walk through each other or through other players.
    ///
    /// Excludes IsNpc entities (shopkeepers, scientists, etc.) - unlike a
    /// real player or another survivor bot, a stationary safe-zone
    /// shopkeeper never moves out of the way, so treating one as a
    /// temporary obstacle to wait out left bots permanently stuck on the
    /// same step near Bandit Camp/The Ranch (confirmed live:
    /// 'CrustyDrifter' blocked ~65 consecutive ticks at the same waypoint
    /// right next to Bandit Camp's shopkeeper cluster).
    /// </summary>
    private static bool IsBlockedByOtherPlayer(Vector3 position, BasePlayer self, BasePlayer exempt)
    {
        Collider[] nearby = Physics.OverlapSphere(position, PlayerAvoidRadius, PlayerLayerMask, QueryTriggerInteraction.Ignore);

        foreach (Collider col in nearby)
        {
            BasePlayer other = col.GetComponentInParent<BasePlayer>();

            if (other != null && other != self && other != exempt && !other.IsNpc)
            {
                return true;
            }
        }

        return false;
    }

    // How far out to treat a horse as a hard obstacle to route around -
    // real horses are far bulkier than a player (PlayerAvoidRadius's own
    // 0.6f). A live report caught a bot literally climbing up onto and
    // getting stuck standing on top of a horse instead of walking around
    // it: RidableHorse : BaseVehicle sits on the same Vehicle World/
    // Vehicle Detailed layers ObstacleLayerMask already treats as solid,
    // steppable terrain - fine for a stationary car chassis, wrong for a
    // horse, which a real player would just walk around, not climb.
    private const float HorseAvoidRadius = 2f;

    // Same "Vehicle World"/"Vehicle Detailed" layers NavigationManager's
    // own ObstacleLayerMask includes (that field lives on a different
    // class, not directly reachable from here) - a horse's real colliders
    // sit on one of these two.
    private static readonly int HorseLayerMask = LayerMask.GetMask("Vehicle World", "Vehicle Detailed");

    private static bool IsBlockedByHorse(Vector3 position)
    {
        Collider[] nearby = Physics.OverlapSphere(position, HorseAvoidRadius, HorseLayerMask, QueryTriggerInteraction.Ignore);

        foreach (Collider col in nearby)
        {
            if (col.GetComponentInParent<RidableHorse>() != null)
            {
                return true;
            }
        }

        return false;
    }

    // Same reasoning as IsBlockedByHorse - bicycles/motorbikes (Bike :
    // GroundVehicle : BaseVehicle, confirmed via decompile, one class
    // covers both the pedal and motor variants) sit on the same Vehicle
    // World/Vehicle Detailed obstacle layers, so without this a bot would
    // try to climb/step onto one instead of walking around it, same as the
    // horse case. Lucas's own explicit request (2026-08-15), parked
    // "for the meantime" alongside horses/cacti/etc rather than anything
    // bike-specific being investigated yet.
    private const float BikeAvoidRadius = 2f;

    private static bool IsBlockedByBike(Vector3 position)
    {
        Collider[] nearby = Physics.OverlapSphere(position, BikeAvoidRadius, HorseLayerMask, QueryTriggerInteraction.Ignore);

        foreach (Collider col in nearby)
        {
            if (col.GetComponentInParent<Bike>() != null)
            {
                return true;
            }
        }

        return false;
    }

    // How far out to steer clear of a cactus - confirmed live via a
    // /lr.debug.scan hit (DE_Cactus_01/cactus-1, layer 30 "Tree",
    // MeshCollider, bounds size 0.90x4.73x1.37 - tall and narrow) plus a
    // tracked stuck incident right next to it: 'TinyRock' repeatedly hit
    // "PathInvalid"/"body would overlap solid geometry" for several
    // ticks, exhausted wiggling and a navmesh nudge, and only recovered
    // via emergency teleport. A real tree of similar size doesn't cause
    // this - a cactus's actual collider shape (thin trunk plus jutting
    // arm colliders) evidently carves an awkward, easy-to-wedge-into gap
    // in the local geometry that repathing/sidestepping can't reliably
    // solve on its own, so it's treated as a hard obstacle to route
    // around from a comfortable distance instead, same "avoid rather than
    // fight the local geometry" approach IsBlockedByHorse already uses.
    private const float CactusAvoidRadius = 2f;

    // Same "Tree" layer the /lr.debug.scan hit that diagnosed this
    // reported the cactus's own MeshCollider sitting on.
    private static readonly int CactusLayerMask = LayerMask.GetMask("Tree");

    private static bool IsBlockedByCactus(Vector3 position)
    {
        Collider[] nearby = Physics.OverlapSphere(position, CactusAvoidRadius, CactusLayerMask, QueryTriggerInteraction.Ignore);

        foreach (Collider col in nearby)
        {
            BaseEntity entity = col.GetComponentInParent<BaseEntity>();

            // Substring match against the real prefab family (cactus_1
            // through cactus_7, confirmed via the bundled
            // AssetSceneManifest.json) rather than an exhaustive list,
            // same reasoning RequiresDestructionToLoot's own doc comment
            // gives for its own substring matching.
            if (entity != null && entity.ShortPrefabName.IndexOf("cactus", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    // How far out to steer clear of a hot air balloon's own cage/gondola
    // cluster - confirmed live via a /lr.debug.scan right next to one:
    // a dense stack of overlapping colliders (Cage, Corners, Entrance,
    // GasCollider, a SnareTrigger, several Prevent_move zones) all within
    // roughly 2-5m of the balloon's own position, the same kind of
    // tightly-packed local geometry that already proved (via cactus) hard
    // for repathing/sidestepping alone to reliably escape. Its own
    // "hab_storage" loot container is separately excluded from the loot
    // search entirely (IsHotAirBalloonStorage, LivingRust.Looting.cs) -
    // this is the general safety net for a bot just passing near one for
    // an unrelated reason.
    private const float HotAirBalloonAvoidRadius = 4.5f;

    private static bool IsBlockedByHotAirBalloon(Vector3 position)
    {
        // Same "Vehicle World"/"Vehicle Detailed" layers the scan
        // confirmed the balloon's own colliders sit on - reusing
        // HorseLayerMask rather than a near-duplicate GetMask call.
        Collider[] nearby = Physics.OverlapSphere(position, HotAirBalloonAvoidRadius, HorseLayerMask, QueryTriggerInteraction.Ignore);

        foreach (Collider col in nearby)
        {
            if (col.GetComponentInParent<HotAirBalloon>() != null)
            {
                return true;
            }
        }

        return false;
    }

    // Real prefab "barricade.wood" (confirmed via AssetSceneManifest.json) -
    // distinct from barricade.sandbags/barricade.stone, which aren't
    // reported as a movement problem. Same "avoid rather than fight the
    // local geometry" treatment as cactus - a barricade's angled wooden
    // stakes are the same kind of thin/jutting collider shape that already
    // proved hard for repathing/sidestepping alone to reliably escape.
    private const float WoodBarricadeAvoidRadius = 2f;

    private static bool IsBlockedByWoodBarricade(Vector3 position)
    {
        Collider[] nearby = Physics.OverlapSphere(position, WoodBarricadeAvoidRadius, ~0, QueryTriggerInteraction.Ignore);

        foreach (Collider col in nearby)
        {
            BaseEntity entity = col.GetComponentInParent<BaseEntity>();

            if (entity != null && entity.ShortPrefabName.IndexOf("barricade.wood", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resets movement-related animation state to neutral - called
    /// whenever the bot stops taking a step, so it can't freeze mid-pose
    /// (e.g. stuck crouched from a step-up attempt that then got blocked).
    /// </summary>
    private static void ReleaseMovementState(BasePlayer npc)
    {
        npc.modelState.sprinting = false;
        npc.modelState.ducked = false;
        npc.modelState.ducking = 0f;
        npc.modelState.onLadder = false;
        npc.SendModelState(true);
    }

    /// <summary>
    /// Cancels any in-progress movement behaviour for survivor, if one
    /// exists - and releases whatever state that movement left behind, not
    /// just the timer. A live test caught two related bugs from skipping
    /// this: re-issuing /lr.follow mid-climb left the bot visibly frozen in
    /// the climbing animation forever (nothing had ever reset
    /// modelState.onLadder etc, since the old climb was abandoned rather
    /// than finished), and simply calling RustNavMeshAgent.ResetPath() when
    /// StartFollowing's native movement falls back to hand-built wasn't
    /// enough to stop Unity's own NavMeshAgent from fighting it - an
    /// enabled NavMeshAgent keeps constraining its entity's position to the
    /// baked ground navmesh every single frame regardless of whether it
    /// has an active path, which is exactly what produced a ladder climb
    /// frozen at one exact position (the hand-built climb step and Unity's
    /// own navmesh-snap fighting for the same transform, tick after tick).
    /// Explicitly disabling it here - not just clearing its path - is what
    /// actually stops that.
    /// </summary>
    /// <summary>
    /// One-time, unclamped ground-height correction called right as a
    /// walk/follow stops (arrived, gave up, timed out, stuck) - see
    /// NavigationManager.TryGetGroundHeight's own doc comment for the real
    /// root cause this fixes (a bot settling mid-way through a clamped
    /// vertical step-catch-up, left visibly hovering). Deliberately NOT
    /// used mid-movement - ApplyMovementStep's own MaxClimbSpeed clamp is
    /// what makes climbing look gradual instead of teleporting, this only
    /// ever fires once movement has already decided to stop.
    /// </summary>
    // Real safety cap (2026-09-01, live report from /lr.debug.forcereturnhome:
    // a survivor "phased through the ground... not physically possible").
    // A legitimate settle correction (the actual case this function exists
    // for) is small - a few tens of cm from a clamped step catch-up.
    // Anything bigger almost certainly means TryGetGroundHeight's probe
    // found the WRONG surface entirely - most plausibly real outdoor
    // terrain metres below an elevated base floor, once GhostDepositIntoOwnedBoxes/
    // TryFillOwnedFurnaces (LivingRust.Looting.cs) started calling this
    // same shared function on arrival at a box/furnace inside a base -
    // rather than the floor the survivor was actually standing on.
    private const float SnapToGroundMaxCorrection = 2f;

    private void SnapToGround(BasePlayer npc)
    {
        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        if (_engine.NavigationManager.TryGetGroundHeight(npc.transform.position, out float groundHeight))
        {
            Vector3 position = npc.transform.position;
            float diff = position.y - groundHeight;

            if (Mathf.Abs(diff) > SnapToGroundMaxCorrection)
            {
                VerbosePuts($"SnapToGround: '{npc.displayName}' ground probe returned {groundHeight:F2} vs current {position.y:F2} ({diff:F2}m off) - skipping as a likely bad probe rather than snapping.");
                return;
            }

            if (Mathf.Abs(diff) > 0.01f)
            {
                position.y = groundHeight;
                npc.transform.position = position;
                npc.MovePosition(position);
            }
        }
    }

    private void CancelActiveMovement(Survivor survivor)
    {
        Guid characterId = survivor.Character.Id;

        if (_activeMovement.TryGetValue(characterId, out Timer existingTimer))
        {
            existingTimer.Destroy();
            _activeMovement.Remove(characterId);

            BasePlayer npc = survivor.Player;

            if (npc != null && !npc.IsDestroyed)
            {
                ReleaseMovementState(npc);

                NavMeshAgent unityAgent = npc.GetComponent<NavMeshAgent>();

                if (unityAgent != null)
                {
                    unityAgent.enabled = false;
                }
            }
        }
    }

    /// <summary>
    /// Full clean-slate wipe, exposed as /lr.debug.despawnall - not just
    /// killing every survivor's BasePlayer, but erasing the persistent
    /// Character records themselves (CharacterManager, SurvivorManager,
    /// and the world.json roster on disk) so nothing lingers as backend
    /// bookkeeping with no live entity behind it. Originally this only
    /// killed the BasePlayer and left the Character record intact (a
    /// "kill and respawn" reset, not a real wipe) - upgraded on request
    /// once the population-bloat problem (world.json ballooning to 350+
    /// characters, most from PopulationManager auto-creating survivors on
    /// every boot, never cleaned up) made clear a proper wipe was worth
    /// having. Since this sweeps every Character this engine instance
    /// tracks (spawned or not - LivingRustEngine.Start creates a Survivor
    /// for every loaded Character regardless), running this also clears
    /// that backlog as a side effect.
    ///
    /// Deliberately does NOT reset CharacterManager's BotId counter back
    /// down - freeing up low IDs for reuse would reopen the exact
    /// same-session collision risk with Rust's own native NPCs that
    /// CharacterManager's 5,000,000 starting value exists to avoid (see
    /// its own doc comment). A "clean slate" here means no leftover
    /// Characters, not literally reclaiming their old numbers - BotIds
    /// keep climbing monotonically regardless.
    ///
    /// Deliberately scoped to SurvivorManager, not Rust's global
    /// BasePlayer.bots registry - it used to sweep that instead, to also
    /// catch orphaned survivors left behind by a since-fixed bug where
    /// Kill() alone didn't properly despawn a bot. That registry turned
    /// out to contain every bot-tier NPC on the entire map (any
    /// Rust-native NPCPlayer with userID &lt; 10,000,000), not just ours -
    /// confirmed via user testing that despawnall kept reporting nonzero,
    /// even climbing, counts run seconds apart with zero new /lr.spawn
    /// calls in between. The orphan-catching need was a one-time historical
    /// cleanup for a bug that's now fixed; scanning the whole map for
    /// unrelated NPCs was never worth that risk once it was.
    ///
    /// Also does a second, narrowly-scoped pass re-checking each BotId
    /// directly against the live world (FindExistingBotEntity, the same
    /// lookup RestoreSpawnedSurvivors uses) rather than trusting
    /// survivor.Player alone - a user report of bots left "still
    /// standing" after a wipe (that command's own count correctly showed
    /// nonzero and world.json genuinely ended up empty, ruling out the
    /// method not running at all) is most consistent with survivor.Player
    /// being stale relative to the actual live entity for that BotId, not
    /// Die()/Kill() themselves failing. This re-check is scoped to
    /// exactly the BotIds this wipe already intended to remove, not the
    /// abandoned broad-registry sweep above.
    /// </summary>
    private int DespawnAllBots()
    {
        if (_engine == null)
        {
            return 0;
        }

        // Snapshot first - SurvivorManager.GetAll() returns a live view
        // over the same dictionary this removes from below, so mutating
        // it while iterating directly would throw.
        var survivors = new List<Survivor>(_engine.SurvivorManager.GetAll());

        // Untracked BEFORE anything gets killed, not after - calling
        // Die() on a still-tracked survivor fires Carbon's OnPlayerDeath
        // hook exactly like a real combat death would (it doesn't
        // distinguish the cause), and that hook's FindSurvivorByPlayer
        // lookup would still find a match if the survivor were still in
        // SurvivorManager at that point - scheduling a completely
        // unwanted RespawnSurvivor 5 seconds later for a Character this
        // command is in the middle of deleting. Confirmed via a live
        // report: bots died, then visibly respawned once (the orphaned
        // timer firing), and only stopped respawning on a *second* kill
        // (by which point FindSurvivorByPlayer correctly found nothing).
        // Removing tracking first means every Die() call below is
        // already untraceable back to a Character by the time
        // OnPlayerDeath's lookup runs, so it can never fire.
        foreach (Survivor survivor in survivors)
        {
            _engine.SurvivorManager.Remove(survivor.Character.Id);
            _engine.CharacterManager.RemoveCharacter(survivor.Character.Id);
        }

        int killed = 0;
        int alreadyGone = 0;
        int orphansFound = 0;

        foreach (Survivor survivor in survivors)
        {
            CancelActiveMovement(survivor);
            CancelActiveAttack(survivor.Character.Id);
            CancelActiveCombat(survivor.Character.Id);
            CancelActiveFlee(survivor.Character.Id);
            CancelActiveRecycling(survivor.Character.Id);
            _pendingRecyclerToResume.Remove(survivor.Character.Id);
            _pendingGhostRouteToResume.Remove(survivor.Character.Id);
            _ghostRouteDetourAttempts.Remove(survivor.Character.Id);
            StopGhostRouteLootScan(survivor.Character.Id);
            _reloadExposureWindowUntil.Remove(survivor.Character.Id);
            _combatRetreatUntilTime.Remove(survivor.Character.Id);
            CancelPendingResumeCombatTarget(survivor.Character.Id);

            BasePlayer bot = survivor.Player;

            if (bot == null || bot.IsDestroyed)
            {
                alreadyGone++;
            }
            else
            {
                bool destroyed = KillBotEntity(bot);

                if (destroyed)
                {
                    killed++;
                }
                else
                {
                    Puts($"despawnall: WARNING - '{survivor.Character.Alias}' (userID {survivor.Character.BotId}) still not destroyed after Die()+Kill().");
                }
            }

            // Re-check this exact BotId against the live world regardless
            // of what happened above - if survivor.Player was stale (a
            // different, still-alive entity actually holds this userID),
            // this is the only thing that catches it.
            BasePlayer existing = FindExistingBotEntity(survivor.Character.BotId);

            if (existing != null && existing != bot && !existing.IsDestroyed)
            {
                orphansFound++;

                // Diagnostic (2026-08-10, expanded 2026-08-11) - see
                // SpawnSurvivor's own doc comment on the instanceID logging
                // this pairs with. Reports survivor.Player's exact state
                // (was it genuinely null, a destroyed reference, or a real
                // but different live object?) to distinguish "something
                // cleared/never-set Player" from "a genuine second live
                // BasePlayer exists for the same BotId."
                //
                // Expanded (2026-08-11) with full detail on `existing`
                // itself - a real precedent just below this block (the old,
                // now-removed third sweep pass) confirms vanilla Rust NPCs
                // (bandit camp guards, confirmed live) CAN carry a userID
                // that numerically collides with this plugin's own issued
                // BotId range, something FindExistingBotEntity's plain
                // `userID == botId` match has no way to rule out on its
                // own. IsNpc/ShortPrefabName directly answers "is this
                // actually one of ours, or a coincidentally-numbered
                // vanilla NPC" without guessing - a real player.prefab-
                // spawned survivor is never IsNpc:True and always carries
                // the exact "assets/prefabs/player/player.prefab" prefab.
                string botState = bot == null ? "null" : bot.IsDestroyed ? $"destroyed (instanceID {bot.GetInstanceID()})" : $"alive (instanceID {bot.GetInstanceID()})";
                Puts($"despawnall: '{survivor.Character.Alias}' (userID {survivor.Character.BotId}) had a live entity survivor.Player didn't reference - killing it too. [diagnostic: survivor.Player was {botState}, live entity found was instanceID {existing.GetInstanceID()}, ShortPrefabName='{existing.ShortPrefabName}', IsNpc={existing.IsNpc}, displayName='{existing.displayName}', IsConnected={existing.IsConnected}, position={existing.transform.position}]");

                KillBotEntity(existing);
            }
        }

        // A third, broader pass used to sweep the live world for any
        // BasePlayer whose userID fell inside LivingRust's own issued BotId
        // ranges (5,000,000+, plus a narrow legacy 1000-1099 band),
        // entirely independent of what SurvivorManager tracked - meant to
        // catch a live entity that outlived its Character record. Removed
        // after a live report: it killed real, invincible bandit camp
        // guards standing inside the safe zone (dropping unlootable loot
        // bags there, since players can never normally kill them) - proof
        // the "these ranges are realistically only ever populated by this
        // plugin" assumption the doc comment above made was simply wrong,
        // not just theoretically risky. This command's whole point is a
        // safe, surgical debug/testing reset, not a heuristic sweep of
        // anything numerically bot-shaped - the two passes above (this
        // plugin's own tracked Characters, plus a per-BotId recheck
        // scoped to exactly those same known BotIds) are the only things
        // provably ours, so that's the actual boundary now. An entity
        // genuinely orphaned from Character tracking entirely (no live
        // Survivor ever pointed at it) is no longer reachable by this
        // command at all - a real gap versus the old behaviour, but a
        // deliberate trade for never touching anything this plugin didn't
        // create.

        // Real sleeping-bag cleanup (2026-08-28, Lucas's own explicit
        // request: "wipe clean = destroy, not unclaim them") - a genuine
        // clean slate needs to remove what these bots left behind in the
        // world, not just the bots themselves, or a repeated spawn/wipe
        // testing cycle (exactly what this command exists for) leaves an
        // ever-growing trail of orphaned bags nobody will ever reclaim.
        // Same scoping philosophy the rest of this command already commits
        // to (see the removed-third-pass doc comment above) - only touches
        // BaseEntity.OwnerID values that exactly match one of THIS wipe's
        // own BotIds, never a broad "every SleepingBag on the map" sweep
        // that could catch something this plugin didn't create.
        var wipedBotIds = new HashSet<ulong>(survivors.Select(survivor => survivor.Character.BotId));
        int bagsDestroyed = 0;

        // Real base-cleanup extension (2026-08-29, Lucas's own explicit
        // ask: "the bases/foundations/any objects the bot has placed"
        // should get wiped too, mainly for cleaning up between base-build
        // replay tests). Same exact scoping philosophy as the sleeping-bag
        // pass right above (and the doc comment further up explaining why
        // the old broad map-wide sweep got removed) - only ever touches a
        // BaseEntity whose OwnerID exactly matches one of THIS wipe's own
        // BotIds, never a blanket "every construction piece on the map"
        // sweep. Every single piece the replay system places (foundation/
        // wall/floor/door/cupboard/lock/furnace/shelves/box/workbench -
        // LivingRust.BaseBuilding.cs) explicitly sets `newEntity.OwnerID =
        // npc.userID` right after creation, so this reliably catches
        // everything a wiped bot ever built, real BuildingBlocks and
        // every deployable/lock alike, in one real ownership check -
        // BasePlayer is excluded since that's already handled by
        // KillBotEntity above, not because it isn't "placed."
        int placedObjectsDestroyed = 0;

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is SleepingBag bag && !bag.IsDestroyed && wipedBotIds.Contains(bag.OwnerID))
            {
                bag.Kill();
                bagsDestroyed++;
            }
            else if (entity is BaseEntity placed && placed is not BasePlayer && placed is not SleepingBag && !placed.IsDestroyed && wipedBotIds.Contains(placed.OwnerID))
            {
                placed.Kill();
                placedObjectsDestroyed++;
            }
        }

        // Persisted immediately rather than waiting for the next Unload/
        // OnServerSave to happen to run - the whole point of this command
        // is an on-demand clean slate, so world.json needs to actually
        // reflect "nothing" right away, not just in memory until
        // something else triggers a save.
        _engine.SaveManager.SaveCharacters(_engine.CharacterManager.GetAllCharacters());

        Puts($"despawnall: wiped {survivors.Count} character(s) - {killed} killed, {alreadyGone} already had no live entity, {orphansFound} tracked-but-stale-reference orphan(s), {bagsDestroyed} placed sleeping bag(s) destroyed, {placedObjectsDestroyed} other placed object(s) destroyed (bases/deployables/locks).");

        return survivors.Count;
    }

    /// <summary>
    /// Forces a bot's BasePlayer to actually leave the world - Kill()
    /// alone (the generic BaseNetworkable teardown) left it standing
    /// frozen, invincible, and still solid to collision, confirmed via
    /// in-game testing; only Die() (the real combat-death path, the same
    /// one that runs when a bot gets shot) properly ragdolls it and lets
    /// it eventually despawn. Waking a sleeping bot first matters too - a
    /// sleeping player is in a reduced-processing state (see
    /// StartSleeping's own doc comment) and this project has already hit
    /// more than one case of a disconnected bot's state not behaving the
    /// same as normal until explicitly woken. Kill() afterward forces
    /// immediate removal instead of waiting on Rust's own corpse-decay
    /// timer. Returns whether the entity actually ended up destroyed.
    /// </summary>
    private bool KillBotEntity(BasePlayer bot)
    {
        if (bot.IsSleeping())
        {
            bot.EndSleeping();
        }

        // Stripped before Die() (2026-08-15, Lucas's own live report) -
        // this is the ONLY caller of KillBotEntity (both DespawnAllBots
        // call sites), a debug/test clean-slate wipe, not a real combat
        // death - real Rust death-drop behaviour (a lootable corpse full
        // of gear) is correct for an actual in-game death but pure clutter
        // here: a /lr.debug.despawnall of 200 bots was leaving ~200 real
        // PlayerCorpse entities scattered near beaches/roads, which Lucas
        // flagged as a real risk of confusing future debugging (loot
        // searches/traces picking them up as if they were organic world
        // state). PlayerInventory.Strip() (confirmed via decompile - clears
        // all 3 containers in place via ItemContainer.Clear(), no dropped-
        // item side effect) empties the bot first, so Die() still runs
        // Rust's normal pipeline but the resulting corpse has nothing in
        // it - and TryFindNearestLootableCorpse's own candidate filter
        // already requires itemList.Count > 0, so an empty corpse is
        // automatically excluded from every future loot search anyway,
        // without needing to hunt down and destroy the corpse entity
        // itself.
        if (bot.inventory != null)
        {
            bot.inventory.Strip();
        }

        bot.Die();

        if (!bot.IsDestroyed)
        {
            bot.Kill();
        }

        return bot.IsDestroyed;
    }

    private static void FaceDirection(BasePlayer npc, Vector3 direction)
    {
        direction.y = 0f;

        if (direction.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        Quaternion rotation = Quaternion.LookRotation(direction);

        npc.transform.rotation = rotation;
        npc.OverrideViewAngles(rotation.eulerAngles);
    }

    /// <summary>
    /// Finds the closest spawned survivor to a position (used to target
    /// the last-spawned bot without needing to track it explicitly).
    /// </summary>
    private Survivor FindNearestSpawnedSurvivor(Vector3 position)
    {
        Survivor nearest = null;
        float nearestDistanceSqr = float.MaxValue;

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            if (!survivor.Spawned || survivor.Player == null || survivor.Player.IsDestroyed)
            {
                continue;
            }

            float distanceSqr = (survivor.Player.transform.position - position).sqrMagnitude;

            if (distanceSqr < nearestDistanceSqr)
            {
                nearestDistanceSqr = distanceSqr;
                nearest = survivor;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Finds the Survivor wrapping a specific BasePlayer, or null if it
    /// isn't one of ours (a real connected player, a vanilla NPC, etc.) -
    /// for hooks that receive a raw BasePlayer (e.g. OnPlayerDeath) and
    /// need to know whether/which LivingRust survivor it corresponds to.
    /// </summary>
    // Per-userID Survivor cache for hot per-tick callers (2026-09-21) -
    // FindSurvivorByPlayer is a linear scan. Safe to cache: a Survivor
    // object outlives its BasePlayer across deaths/respawns (BotId is
    // stable), and this dictionary is rebuilt fresh on every plugin reload.
    private readonly Dictionary<ulong, Survivor> _survivorByUserIdCache = new();

    private Survivor GetSurvivorCached(BasePlayer player)
    {
        if (player == null)
        {
            return null;
        }

        if (_survivorByUserIdCache.TryGetValue(player.userID, out Survivor cached))
        {
            return cached;
        }

        Survivor found = FindSurvivorByPlayer(player);

        if (found != null)
        {
            _survivorByUserIdCache[player.userID] = found;
        }

        return found;
    }

    private Survivor FindSurvivorByPlayer(BasePlayer player)
    {
        if (_engine == null || player == null)
        {
            return null;
        }

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            if (survivor.Player == player)
            {
                return survivor;
            }
        }

        return null;
    }
}
