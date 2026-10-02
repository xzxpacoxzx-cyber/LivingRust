using System;
using System.Collections.Generic;
using LivingRust.Core;
using LivingRust.Models;
using LivingRust.Navigation;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Authored waypoint routes for specific monument-like structures whose generic
/// step/ladder-search heuristics misroute on their real geometry. A known structure
/// gets a fixed route of named landmarks and ladder climbs instead; anything without
/// an authored route falls back to the generic heuristic in StartClimbingMonument.
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
        /// Climbs the nearest ladder like ClimbLadder, but also skips the headroom
        /// check on the approach step, for a ladder whose approach sits directly
        /// under low structural geometry the whole way in.
        /// </summary>
        public static readonly MonumentWaypoint ClimbLadderIgnoringHeadroom = new(MonumentWaypointKind.ClimbNearestLadder, default, ignoreHeadroom: true);

        /// <summary>
        /// Walks to a fixed world position using the same stepping/sidestep logic as
        /// WalkToNamed, without a name lookup. Used as a fallback for legs with no
        /// convenient named landmark.
        /// </summary>
        public static MonumentWaypoint ToPosition(Vector3 position) => new(MonumentWaypointKind.WalkToPosition, position);

        /// <summary>
        /// Walks to a fixed world position like ToPosition, with ground collision
        /// active but the headroom/duck check skipped for this leg. Used for legs
        /// where an oversized collider would otherwise falsely block on headroom.
        /// </summary>
        public static MonumentWaypoint NoHeadroomTo(Vector3 position) => new(MonumentWaypointKind.WalkToPosition, position, ignoreHeadroom: true);

        /// <summary>
        /// Moves straight to a fixed world position over PhaseThroughTicks, bypassing
        /// collision entirely. Used for a known choke point where the generic
        /// step/sidestep logic is unreliable; scoped to this one authored leg only.
        /// </summary>
        public static MonumentWaypoint PhaseTo(Vector3 position) => new(MonumentWaypointKind.PhaseTo, position);
    }

    // Keyed by the structure's own root GameObject name rather than a registered
    // monument name, since this structure has no TerrainMeta.Path.Monuments entry.
    // Waypoints are numbered in authoring order; reaching the last one retraces the
    // list back to the first (see AdvanceMonumentRoute) instead of needing a
    // separate "way down" route.
    //
    // Some legs use explicit WalkToPosition/NoHeadroomTo waypoints instead of relying
    // on ClimbNearestLadder's approach search, for corners and climbs where no clear
    // straight line exists between landmarks.
    //
    // Keyed by 'powerline_a (1)', not 'powerline_a': the latter is a pure
    // organizational parent transform with no collider of its own, while the actual
    // colliders live on the child named 'powerline_a (1)'.
    private static readonly Dictionary<string, MonumentWaypoint[]> KnownMonumentRoutes = new()
    {
        ["powerline_a (1)"] = new[]
        {
            new MonumentWaypoint(PowerlineTowerRampName),
            // Forces genuine arrival at the real platform height before Ladder_1's
            // approach runs, since the ramp's arrival check is based on its
            // bounding-box center rather than its top.
            MonumentWaypoint.ToPosition(new Vector3(-158.02f, 25.84f, 147.73f)),
            MonumentWaypoint.ClimbLadder,                                      // Ladder_1
            MonumentWaypoint.ToPosition(new Vector3(-155.23f, 30.25f, 153.87f)), // first turn around the platform corner past Ladder_1
            MonumentWaypoint.ToPosition(new Vector3(-155.85f, 30.15f, 155.75f)), // second turn, lines up the final approach to Ladder_2
            // Extra dwell point for the remaining gap to Ladder_2's mount, since
            // ClimbLadder's approach step has no sidestep fallback for it.
            MonumentWaypoint.ToPosition(new Vector3(-159.13f, 30.32f, 157.34f)),
            MonumentWaypoint.ClimbLadder,                                      // Ladder_2
            MonumentWaypoint.ToPosition(new Vector3(-164.02f, 34.83f, 147.89f)), // dwell after Ladder_2, before the duck-climb up
            MonumentWaypoint.ToPosition(new Vector3(-169.71f, 36.26f, 149.34f)), // dwell after the first duck-climb rise
            // Extra points for the second duck-climb's elbow and rise top, since it
            // is not a straight line to the pre-Ladder_3 dwell.
            MonumentWaypoint.ToPosition(new Vector3(-169.41f, 36.26f, 151.66f)), // flat approach, right before the second duck-climb starts
            MonumentWaypoint.ToPosition(new Vector3(-168.30f, 37.82f, 153.92f)), // top of the second duck-climb's diagonal rise
            MonumentWaypoint.ToPosition(new Vector3(-166.87f, 37.79f, 156.65f)), // top of the second duck-climb rise, right before Ladder_3
            MonumentWaypoint.ClimbLadder,                                      // Ladder_3
            MonumentWaypoint.ToPosition(new Vector3(-166.17f, 41.99f, 153.38f)), // dwell after Ladder_3, right at the plank-crossing choke zone
            // Extra entry/top points for the duck-sprint riser, so the direct line
            // between legs follows the actual ascending surface rather than cutting
            // across it onto a lower platform.
            MonumentWaypoint.NoHeadroomTo(new Vector3(-165.04f, 42.18f, 152.14f)), // entry point right before the duck-sprint riser
            MonumentWaypoint.NoHeadroomTo(new Vector3(-162.12f, 43.49f, 151.23f)), // top of the duck-sprint riser
            // A confirmed-solid standing position beside Ladder_4, used as the final
            // walk-in point.
            MonumentWaypoint.NoHeadroomTo(new Vector3(-161.81f, 43.34f, 152.52f)), // plank-crossing zone: ground collision stays on, headroom check off - right beside Ladder_4
            // Pushes the authored walk right up to the ladder trigger footprint,
            // using the walk logic's sidestep fallback since the ladder approach
            // step's own naive probe can land on the wrong overlapping platform here.
            MonumentWaypoint.NoHeadroomTo(new Vector3(-160.35f, 43.82f, 152.70f)), // right at Ladder_4's trigger footprint
            // Uses ClimbLadderIgnoringHeadroom since the approach step itself sits
            // directly under low structural geometry here.
            MonumentWaypoint.ClimbLadderIgnoringHeadroom,                      // Ladder_4
        },
    };

    // Coarse check for whether a known structure is nearby at all, deliberately wider
    // than any single waypoint's own search radius, since this only decides whether
    // to use the authored route in the first place.
    private const float MonumentRouteDetectionRadius = 50f;

    // How close to a named walk waypoint counts as "arrived."
    private const float MonumentWaypointArriveDistance = 2f;

    // How long a single walk-to-named-waypoint leg gets before the whole route gives
    // up rather than retrying a permanently blocked step forever.
    private const int MonumentWaypointTimeoutTicks = (int)(15f / WalkTickInterval);

    // How long a PhaseTo leg takes to cross, tuned to read as a purposeful dash
    // rather than a floaty teleport.
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
    /// Runs an authored waypoint route as a standalone movement behaviour. Reaching
    /// the last waypoint retraces the list back to the first, so the survivor leaves
    /// the structure the way it came up instead of being stranded at the top.
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

            // Pauses entirely while wounded/downed rather than fighting Rust's own
            // incapacitated state machine, same rule as every other movement timer.
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

        // WalkToNamed and WalkToPosition only differ in how waypointPosition is
        // resolved; everything after that is identical.
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
                // Logged once per stuck episode rather than every tick, to avoid
                // spamming while it silently keeps retrying.
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
    /// Advances a PhaseTo leg one tick, moving straight to the target over
    /// PhaseThroughTicks with no collision check, while driving facing and sprint
    /// state so it reads as a deliberate dash.
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
