using System;
using System.Collections.Generic;
using LivingRust.Core;
using LivingRust.Models;
using LivingRust.Navigation;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Authored waypoint routes for specific monument-like structures whose
/// generic step/ladder-search heuristics kept misrouting on their real
/// geometry (the Powerline tower took a full session of individual bug
/// fixes - ramp entry, ladder-down grounding, wrong-ladder search - and
/// still only got two ladder segments up before running out of mapped
/// ground). Rather than keep chasing edge cases in the generic heuristic,
/// a known structure gets a fixed, author-once route instead: an ordered
/// list of named landmarks (reusing NavigationManager.TryFindNamedStructure,
/// already proven on the ramp - names survive the structure being relocated
/// or replaced by a different instance on a regenerated map, unlike raw
/// world coordinates) and ladder climbs (reusing the existing ClimbState/
/// AdvanceClimb machinery, already proven solid). Anything without an
/// authored route still falls back to the generic heuristic in
/// StartClimbingMonument, so nothing already working regresses.
/// </summary>
public partial class LivingRust
{
    private enum MonumentWaypointKind
    {
        WalkToNamed,
        WalkToPosition,
        PhaseTo,
        ClimbNearestLadder
    }

    private readonly struct MonumentWaypoint
    {
        public readonly MonumentWaypointKind Kind;
        public readonly string ObjectName;
        public readonly Vector3 Position;
        public readonly bool IgnoreHeadroom;

        public MonumentWaypoint(string objectName)
        {
            Kind = MonumentWaypointKind.WalkToNamed;
            ObjectName = objectName;
            Position = default;
            IgnoreHeadroom = false;
        }

        private MonumentWaypoint(MonumentWaypointKind kind, Vector3 position, bool ignoreHeadroom = false)
        {
            Kind = kind;
            ObjectName = null;
            Position = position;
            IgnoreHeadroom = ignoreHeadroom;
        }

        private MonumentWaypoint(MonumentWaypointKind kind)
        {
            Kind = kind;
            ObjectName = null;
            Position = default;
            IgnoreHeadroom = false;
        }

        public static readonly MonumentWaypoint ClimbLadder = new(MonumentWaypointKind.ClimbNearestLadder);

        /// <summary>
        /// Climbs the nearest ladder exactly like ClimbLadder, but the
        /// approach step (AdvanceClimb walking to the computed mount point)
        /// also skips the headroom check - for a ladder whose approach sits
        /// directly under low structural geometry the whole way in, not just
        /// along the walk before it. Confirmed via a live test on the
        /// Powerline tower's Ladder_4: even starting the approach less than
        /// 1m from its mount point, the standing-height ray still clipped
        /// 'Lvl5PlatformA' (a /lr.debug.look scan showed it spanning y
        /// 41.91-45.02 right over the mount point), so getting the walk-in
        /// closer alone can't fix it - the approach step itself needs the
        /// same bypass NoHeadroomTo gives ordinary walk legs.
        /// </summary>
        public static readonly MonumentWaypoint ClimbLadderIgnoringHeadroom = new(MonumentWaypointKind.ClimbNearestLadder, default, ignoreHeadroom: true);

        /// <summary>
        /// Walks to a fixed world position using the same local-stepping/
        /// sidestep logic as WalkToNamed, just without a name lookup - for
        /// legs where no convenient named landmark sits on the real
        /// walkable route. Deliberately the fallback, not the default - a
        /// raw coordinate won't survive the structure being relocated by a
        /// map regen the way WalkToNamed does.
        /// </summary>
        public static MonumentWaypoint ToPosition(Vector3 position) => new(MonumentWaypointKind.WalkToPosition, position);

        /// <summary>
        /// Walks to a fixed world position exactly like ToPosition - full
        /// ground/step-up/step-down collision stays active, so feet/legs
        /// still collide with the platform instead of phasing through it -
        /// but with TryGetNextStep's headroom/duck check skipped entirely
        /// for this leg. For the Powerline tower's plank-crossing gap near
        /// Ladder_4, where the tower's own oversized frame collider still
        /// clips the headroom ray at head height even after
        /// NonSteppableColliderNames excludes it from the ground probe and
        /// duck fallback. Replaces the previous PhaseTo (full collision
        /// bypass, including ground) for this leg, which could teleport-walk
        /// straight through the floor.
        /// </summary>
        public static MonumentWaypoint NoHeadroomTo(Vector3 position) => new(MonumentWaypointKind.WalkToPosition, position, ignoreHeadroom: true);

        /// <summary>
        /// Moves straight to a fixed world position over PhaseThroughTicks,
        /// bypassing TryGetNextStep/collision entirely - for a known choke
        /// point (the Powerline tower's PlankBlocker duck gap, flanked by
        /// its own bogus compound frame collider and a large Tarp mesh)
        /// where the generic step/sidestep logic stays unreliable even
        /// after fixing the duck-height and headroom-exclusion bugs that
        /// caused it to hard-block or oscillate outright. Deliberately
        /// scoped to just this one authored leg - general movement and duck
        /// logic elsewhere (used by /lr.follow, other monuments, etc.) is
        /// untouched.
        /// </summary>
        public static MonumentWaypoint PhaseTo(Vector3 position) => new(MonumentWaypointKind.PhaseTo, position);
    }

    // Keyed by the structure's own root GameObject name (confirmed via
    // /lr.debug.nearby - e.g. every collider on the Powerline tower reports
    // 'powerline_a' as its ultimate parent), not a registered monument name -
    // this structure isn't a registered monument at all (confirmed via
    // /lr.monument.where), just static dressing, so there's no
    // TerrainMeta.Path.Monuments entry to key off.
    //
    // Waypoints are numbered 1..X in the order authored below. Reaching the
    // last one retraces the same list back to the first (see
    // AdvanceMonumentRoute) rather than needing a separate authored
    // "way down" - climbing a ladder in the same ClimbState machinery
    // already handles either direction.
    //
    // Extended to Ladder_4 using a captured trace (trace_CrazyGrub7652_
    // 20260807_205906.csv) of the cleanest full climb yet - all 4 ladders,
    // via /lr.follow with the duck-height/headroom-exclusion and ladder-
    // detour-masking fixes both live. Coordinates below are taken directly
    // from that trace's dwell points (positions where the path settles for
    // several ticks between direction changes), not guessed.
    //
    // Ladder_1 needs no explicit WalkToPosition before it (ramp landmark
    // lands within FollowClimbSearchRadius (15m) with a clear line).
    // Ladder_1->Ladder_2 does NOT, despite also being within 15m - a
    // /lr.climb.monument test showed it walking straight off the platform
    // edge there instead of around the real corner (TryGetNextStep finds no
    // surface once it crosses the edge, falls back to raw terrain height,
    // and MaxClimbSpeed's clamp makes the resulting ~4m drop look like a
    // smooth controlled descent rather than an obvious glitch - it lands
    // around y=26.2, nowhere near Ladder_2). That specific test turned out
    // to have been running the generic StartClimbingMonument heuristic, not
    // this authored route at all (see the dictionary key fix below) - but
    // ClimbNearestLadder's own approach step is the same naive no-sidestep
    // primitive, so the same failure was a real risk here too. Kept as
    // insurance; the two WalkToPosition legs below are the real corner,
    // taken from a /lr.follow trace of the correct path
    // (trace_LostFarmer2751_20260807_211922.csv) rather than guessed.
    // Ladder_2->Ladder_3 also doesn't have a clear straight line (the real
    // route climbs via several intermediate steps), so it likewise gets
    // explicit waypoints instead of relying on ClimbNearestLadder's naive
    // approach to find its own way there.
    //
    // Ladder_2 through Ladder_3 (the duck-climb dwell points below) and
    // Ladder_3's dismount onward were re-captured via a dedicated, purpose-
    // built /lr.follow session (trace_BrokenScav_20260807_214450 through
    // _214653, continuing from trace_RustyBean326_20260807_214328/_214412
    // for the ramp/corner legs before it) after the CrazyGrub7652-derived
    // dwell points below Ladder_3 twice failed a live /lr.climb.monument
    // test (glided down and blocked on raw 'Terrain' both times) - rather
    // than keep guessing intermediate waypoints from the old trace, this
    // session walked the exact intended path once, purpose-built for
    // authoring. Coordinates are the real dwell points (positions where the
    // path settles for several ticks between direction changes) from that
    // capture, not guessed.
    //
    // Ladder_3's dismount through to Ladder_4 uses NoHeadroomTo, not a plain
    // walk or PhaseTo - see MonumentWaypoint.NoHeadroomTo's doc comment. A
    // live test with the first version of this leg (a single jump straight
    // to Ladder_4's base) stepped down onto the wrong, lower platform
    // ('Lvl2PlatformB') instead of following the real duck-sprint riser the
    // fresh capture shows - same "straight line cuts across real geometry"
    // failure as the duck-climb legs above, just one leg further along. Split
    // into the same shape as a result: an entry point right before the riser
    // and the riser's own top, both taken from the fresh capture, before a
    // final leg to (-161.81, 43.34, 152.52) - a real player's own confirmed-
    // solid standing position beside Ladder_4, taken from a live
    // /lr.debug.look scan rather than the earlier CrazyGrub7652-derived guess
    // it replaces.
    //
    // Ladder_4 itself uses ClimbLadderIgnoringHeadroom, not ClimbLadder - a
    // live test showed the approach step blocked by 'Lvl5PlatformA' even
    // starting under 1m from the mount point (see ApproachBlockLogged's
    // diagnostic output), and the same /lr.debug.look scan confirmed that
    // platform sits y 41.91-45.02, directly over the mount point the whole
    // way in - getting the walk-in closer alone couldn't fix it, only
    // skipping the approach step's own headroom check could. The rest of
    // the route up to it is solid, and leaving it out would make the route
    // artificially stop short rather than surface the real
    // remaining gap.
    //
    // Keyed by 'powerline_a (1)', NOT 'powerline_a' - confirmed via every
    // debug scan this project has ever taken of this structure that
    // 'powerline_a' is a pure organizational parent transform with no
    // collider of its own; every actual collider (including the tower's
    // compound frame) lives on a child named 'powerline_a (1)'.
    // TryFindMonumentRoute's Physics.OverlapSphere + exact-name match can
    // only ever find a real collider, so keying on the parent's name meant
    // this lookup silently failed 100% of the time and /lr.climb.monument
    // fell through to the generic heuristic on every single call - this
    // whole authored route, old 3-waypoint version included, has never
    // actually run before this fix.
    private static readonly Dictionary<string, MonumentWaypoint[]> KnownMonumentRoutes = new()
    {
        ["powerline_a (1)"] = new[]
        {
            new MonumentWaypoint(PowerlineTowerRampName),
            // WalkToNamed's arrival check is 2m from the ramp object's
            // bounding-box CENTER, not its top - a real /lr.climb.monument
            // test showed it counting as "arrived" while the survivor was
            // still partway up (y~22.6-23.2, real platform height is
            // 25.84), which then fed ClimbLadder a mount point computed
            // from that too-low current height ("step too high onto
            // 'Lvl0PlatformA'"). This forces genuine arrival at the real
            // platform height before Ladder_1's approach ever runs.
            MonumentWaypoint.ToPosition(new Vector3(-158.02f, 25.84f, 147.73f)),
            MonumentWaypoint.ClimbLadder,                                      // Ladder_1
            MonumentWaypoint.ToPosition(new Vector3(-155.23f, 30.25f, 153.87f)), // first turn around the platform corner past Ladder_1
            MonumentWaypoint.ToPosition(new Vector3(-155.85f, 30.15f, 155.75f)), // second turn, lines up the final approach to Ladder_2
            // The /lr.follow trace this corner was taken from ended here -
            // a live /lr.climb.monument test showed the remaining gap from
            // this point to Ladder_2's actual mount also has no clear
            // straight line (ClimbLadder's approach step, unlike
            // WalkToPosition, has no sidestep fallback: it found no surface
            // partway across, fell back to raw terrain height, and glided
            // down ~4m before blocking). This point is confirmed from the
            // other clean trace (CrazyGrub7652) as the real dwell right
            // before Ladder_2's mount.
            MonumentWaypoint.ToPosition(new Vector3(-159.13f, 30.32f, 157.34f)),
            MonumentWaypoint.ClimbLadder,                                      // Ladder_2
            MonumentWaypoint.ToPosition(new Vector3(-164.02f, 34.83f, 147.89f)), // dwell after Ladder_2, before the duck-climb up
            MonumentWaypoint.ToPosition(new Vector3(-169.71f, 36.26f, 149.34f)), // dwell after the first duck-climb rise
            // A live /lr.climb.monument test confirmed the second duck-climb
            // isn't a straight line either: going straight from here to the
            // pre-Ladder_3 dwell tracked real terrain right off the edge of
            // the actual platforms and glided ~8.5m down before blocking.
            // The real path first walks flat in +z, then ducks up a
            // diagonal rise, then walks flat again - these two extra points
            // (the elbow before the duck, and the top of the duck) are taken
            // from the same fresh capture as everything else in this stretch.
            MonumentWaypoint.ToPosition(new Vector3(-169.41f, 36.26f, 151.66f)), // flat approach, right before the second duck-climb starts
            MonumentWaypoint.ToPosition(new Vector3(-168.30f, 37.82f, 153.92f)), // top of the second duck-climb's diagonal rise
            MonumentWaypoint.ToPosition(new Vector3(-166.87f, 37.79f, 156.65f)), // top of the second duck-climb rise, right before Ladder_3
            MonumentWaypoint.ClimbLadder,                                      // Ladder_3
            MonumentWaypoint.ToPosition(new Vector3(-166.17f, 41.99f, 153.38f)), // dwell after Ladder_3, right at the plank-crossing choke zone
            // A live test confirmed this leg needed splitting too - going
            // straight from the dwell above to Ladder_4's base cut across
            // the real duck-sprint riser and stepped down onto the lower
            // 'Lvl2PlatformB' instead, blocking (1.84m > 1.30m max). The
            // fresh capture's real climb happens over a short, sharp rise
            // (roughly y 42.3 to 43.5); these two extra points (entry point
            // right before the rise, and its top) keep each leg's direct
            // line close enough to the real geometry to find the actual
            // ascending surface instead of the platform beneath it.
            MonumentWaypoint.NoHeadroomTo(new Vector3(-165.04f, 42.18f, 152.14f)), // entry point right before the duck-sprint riser
            MonumentWaypoint.NoHeadroomTo(new Vector3(-162.12f, 43.49f, 151.23f)), // top of the duck-sprint riser
            // (-161.81, 43.34, 152.52) is a real player's own standing
            // position, confirmed solid via a live /lr.debug.look scan taken
            // from directly beside Ladder_4 (a bot's own attempt to walk on
            // from here previously stopped almost exactly on this same spot
            // by coincidence, within MonumentWaypointArriveDistance of the
            // old (-160.68, 43.81, 152.33) guess) - replaces that guess as
            // the final walk-in point.
            MonumentWaypoint.NoHeadroomTo(new Vector3(-161.81f, 43.34f, 152.52f)), // plank-crossing zone: ground collision stays on, headroom check off - right beside Ladder_4
            // A live test still blocked after this: ClimbNearestLadder's own
            // approach step (BuildClimbState's computed mount point, offset
            // ~0.6m out from the ladder's centerline) sat "step too high onto
            // 'Lvl6PlatformA' (3.96m)" - Lvl4PlatformRamp/Lvl5PlatformA/
            // Lvl6PlatformA all overlap this x/z in the /lr.debug.look scan,
            // stacked ~5m apart, and the approach step's naive no-sidestep
            // probe landed on the wrong (much higher) one. The walk-in above
            // never hit this over the same stretch of ground, because
            // WalkToPosition/NoHeadroomTo has a sidestep fallback the ladder
            // approach deliberately doesn't - so pushing the authored walk
            // right up to the ladder trigger itself (bounds center taken
            // from the same scan: x -160.53..-160.10, z 152.41..152.99,
            // bottom y 43.82) leaves the approach step almost nothing left
            // to cross, using the more capable walk logic for the ground
            // this monument keeps needing sidestep to get right.
            MonumentWaypoint.NoHeadroomTo(new Vector3(-160.35f, 43.82f, 152.70f)), // right at Ladder_4's trigger footprint
            // ClimbLadderIgnoringHeadroom, not ClimbLadder - the approach
            // step itself (not just the walk-in above) sits directly under
            // 'Lvl5PlatformA' (y 41.91-45.02 per the same scan), so it needs
            // the same headroom bypass; see ClimbLadderIgnoringHeadroom's
            // doc comment for the live-test evidence.
            MonumentWaypoint.ClimbLadderIgnoringHeadroom,                      // Ladder_4
        },
    };

    // Coarse "is a known structure nearby at all" check - deliberately wider
    // than any single waypoint's own search radius, since this only decides
    // whether to use the authored route in the first place. The Powerline
    // tower's own compound collider spans roughly 23x40x28m (confirmed via
    // /lr.debug.nearby), so this comfortably covers starting anywhere near
    // its base.
    private const float MonumentRouteDetectionRadius = 50f;

    // Same idea as PowerlineTowerRampArriveDistance - how close to a named
    // walk waypoint counts as "arrived."
    private const float MonumentWaypointArriveDistance = 2f;

    // Mirrors LadderApproachTimeoutTicks - how long a single walk-to-named-
    // waypoint leg gets before the whole route gives up rather than retrying
    // a permanently blocked step forever.
    private const int MonumentWaypointTimeoutTicks = (int)(15f / WalkTickInterval);

    // How long a PhaseTo leg takes to cross - fast enough to read as a
    // purposeful dash rather than a floaty teleport. ~5.2 m/s over the
    // ~8m Powerline tower plank-crossing gap, close to sprint pace.
    private const float PhaseThroughDuration = 1.5f;
    private const int PhaseThroughTicks = (int)(PhaseThroughDuration / WalkTickInterval);

    private sealed class MonumentRouteState
    {
        public MonumentWaypoint[] Waypoints;
        public int Index;
        public bool Retracing;
        public ClimbState Climb;
        public float SidestepAngle;
        public int LegTicks;
        public int PhaseTicks;
        public Vector3 PhaseStart;
        public int BlockLogged;
    }

    /// <summary>
    /// Looks for a known structure (by root object name) within
    /// MonumentRouteDetectionRadius of origin and returns its authored route.
    /// </summary>
    private bool TryFindMonumentRoute(Vector3 origin, out string monumentName, out MonumentWaypoint[] waypoints)
    {
        foreach (KeyValuePair<string, MonumentWaypoint[]> entry in KnownMonumentRoutes)
        {
            if (_engine.NavigationManager.TryFindNamedStructure(origin, entry.Key, MonumentRouteDetectionRadius, out _))
            {
                monumentName = entry.Key;
                waypoints = entry.Value;
                return true;
            }
        }

        monumentName = null;
        waypoints = null;
        return false;
    }

    /// <summary>
    /// Runs an authored waypoint route as a standalone movement behaviour,
    /// the same way StartClimbingMonument runs the generic goal - reaching
    /// the last waypoint retraces the whole list back to the first instead
    /// of stopping, so the survivor actually leaves the structure the same
    /// way it came up rather than being stranded at the top.
    /// </summary>
    private void StartMonumentRoute(Survivor survivor, MonumentWaypoint[] waypoints)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveMovement(survivor);

        var state = new MonumentRouteState { Waypoints = waypoints };

        Timer routeTimer = null;

        routeTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                routeTimer.Destroy();
                _activeMovement.Remove(characterId);
                return;
            }

            // Wounded/downed - pause entirely rather than fight Rust's own
            // incapacitated state machine, same rule as every other
            // movement timer (see StartWalking's own doc comment).
            // AdvanceMonumentRoute (via AdvanceClimb/MovePosition) writes
            // position every tick otherwise.
            if (npc.IsWounded())
            {
                return;
            }

            if (!AdvanceMonumentRoute(state, survivor, npc))
            {
                routeTimer.Destroy();
                _activeMovement.Remove(characterId);
            }
        });

        _activeMovement[characterId] = routeTimer;
    }

    /// <summary>
    /// Advances the route one tick. Returns false once the route is
    /// finished (back at waypoint 1 after retracing) or has given up, at
    /// which point StartMonumentRoute tears the timer down.
    /// </summary>
    private bool AdvanceMonumentRoute(MonumentRouteState state, Survivor survivor, BasePlayer npc)
    {
        if (state.Climb != null)
        {
            ClimbOutcome outcome = AdvanceClimb(state.Climb, survivor, npc);

            if (outcome == ClimbOutcome.InProgress)
            {
                return true;
            }

            state.Climb = null;
            state.LegTicks = 0;

            if (outcome == ClimbOutcome.TimedOut)
            {
                Puts($"'{survivor.Character.Alias}' gave up on the monument route - couldn't reach the next ladder.");
                return false;
            }

            return AdvanceMonumentWaypoint(state, survivor);
        }

        MonumentWaypoint target = state.Waypoints[state.Index];

        if (target.Kind == MonumentWaypointKind.ClimbNearestLadder)
        {
            bool climbingDown = state.Retracing;

            if (!_engine.NavigationManager.TryFindNearestLadder(npc.transform.position, FollowClimbSearchRadius, out NavigationManager.LadderInfo ladder,
                    candidate => LadderGoesRightWay(candidate, npc.transform.position, climbingDown)))
            {
                Puts($"'{survivor.Character.Alias}' gave up on the monument route - no ladder within {FollowClimbSearchRadius:F0}m goes the right way.");
                return false;
            }

            state.Climb = BuildClimbState(npc, _engine.NavigationManager, ladder, climbingDown, target.IgnoreHeadroom);
            state.LegTicks = 0;
            return true;
        }

        if (target.Kind == MonumentWaypointKind.PhaseTo)
        {
            return AdvanceMonumentPhase(state, target, survivor, npc);
        }

        // WalkToNamed and WalkToPosition only differ in how waypointPosition
        // is resolved - a live name lookup vs. the waypoint's own stored
        // coordinate. Everything after that (timeout, local-stepping,
        // sidestep, arrival) is identical.
        if (++state.LegTicks > MonumentWaypointTimeoutTicks)
        {
            string description = target.Kind == MonumentWaypointKind.WalkToNamed ? $"'{target.ObjectName}'" : target.Position.ToString();
            Puts($"'{survivor.Character.Alias}' gave up on the monument route - couldn't reach {description}.");
            return false;
        }

        Vector3 waypointPosition;

        if (target.Kind == MonumentWaypointKind.WalkToNamed)
        {
            if (!_engine.NavigationManager.TryFindNamedStructure(npc.transform.position, target.ObjectName, MonumentRouteDetectionRadius, out waypointPosition))
            {
                Puts($"'{survivor.Character.Alias}' gave up on the monument route - lost track of '{target.ObjectName}'.");
                return false;
            }
        }
        else
        {
            waypointPosition = target.Position;
        }

        Vector3 current = npc.transform.position;
        float stepDistance = WalkSpeed * WalkTickInterval;
        StepResult step = _engine.NavigationManager.TryGetNextStep(current, waypointPosition, stepDistance, out Vector3 nextStep, out string blockReason, target.IgnoreHeadroom);

        if (step == StepResult.Blocked)
        {
            if (!TryFindSidestep(current, waypointPosition, stepDistance, npc, exempt: null, ref state.SidestepAngle, out nextStep, out step, target.IgnoreHeadroom))
            {
                // Logged once per stuck episode, not every tick - this
                // branch previously had no diagnostic at all short of the
                // full MonumentWaypointTimeoutTicks (15s) giving up, which
                // is indistinguishable from a genuine hang while it's still
                // silently retrying (confirmed via a captured trace: a
                // survivor sat blocked here with zero log output).
                if (state.BlockLogged == 0)
                {
                    Puts($"'{survivor.Character.Alias}' blocked on the monument route heading toward {waypointPosition} (sidestep also failed): {blockReason}");
                }

                state.BlockLogged++;
                return true;
            }

            state.BlockLogged = 0;
        }
        else
        {
            state.BlockLogged = 0;
            state.SidestepAngle = 0f;
        }

        ApplyMovementStep(survivor, npc, current, nextStep, step == StepResult.SteppedUp, sprinting: false, stepDistance);

        if (Vector3.Distance(nextStep, waypointPosition) < MonumentWaypointArriveDistance)
        {
            return AdvanceMonumentWaypoint(state, survivor);
        }

        return true;
    }

    /// <summary>
    /// Advances a PhaseTo leg one tick: linearly moves straight to the
    /// target over PhaseThroughTicks with no TryGetNextStep/collision
    /// check at all - see MonumentWaypoint.PhaseTo's doc comment for why.
    /// Still drives facing and sprint animation state directly (mirroring
    /// the relevant parts of ApplyMovementStep) so it reads as a fast,
    /// deliberate dash rather than a floaty teleport.
    /// </summary>
    private bool AdvanceMonumentPhase(MonumentRouteState state, MonumentWaypoint target, Survivor survivor, BasePlayer npc)
    {
        if (state.PhaseTicks == 0)
        {
            state.PhaseStart = npc.transform.position;
        }

        state.PhaseTicks++;
        float t = Mathf.Clamp01(state.PhaseTicks / (float)PhaseThroughTicks);
        Vector3 next = Vector3.Lerp(state.PhaseStart, target.Position, t);

        FaceDirection(npc, next - npc.transform.position);
        npc.modelState.sprinting = true;
        npc.modelState.ducked = false;
        npc.modelState.ducking = 0f;
        npc.SendModelState(true);
        npc.MovePosition(next);
        survivor.Position = next;
        survivor.Character.Position = next;

        if (t < 1f)
        {
            return true;
        }

        state.PhaseTicks = 0;
        return AdvanceMonumentWaypoint(state, survivor);
    }

    /// <summary>
    /// Moves to the next waypoint index, flipping into (or continuing)
    /// retrace once the last waypoint is reached, and finishing the route
    /// once retracing walks back past the first one.
    /// </summary>
    private bool AdvanceMonumentWaypoint(MonumentRouteState state, Survivor survivor)
    {
        if (!state.Retracing)
        {
            state.Index++;

            if (state.Index < state.Waypoints.Length)
            {
                return true;
            }

            state.Retracing = true;
            state.Index = state.Waypoints.Length - 2;

            if (state.Index >= 0)
            {
                Puts($"'{survivor.Character.Alias}' reached the end of the monument route - retracing back down.");
                return true;
            }
        }
        else
        {
            state.Index--;

            if (state.Index >= 0)
            {
                return true;
            }
        }

        Puts($"'{survivor.Character.Alias}' finished the monument route.");
        return false;
    }
}
