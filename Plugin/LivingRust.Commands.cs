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
    // Matches the default player walk and run speeds.
    private const float WalkSpeed = 2.8f;
    private const float RunSpeed = 5.5f;

    // Reduces swim speed by 70%, waived when the survivor is wearing diving fins.
    private const float SwimSpeedMultiplier = 0.3f;

    private const string DivingFinsShortname = "diving.fins";

    // Separate enter/exit depth thresholds prevent rapid swim/walk state flickering near the boundary.
    private const float EnterSwimDepthThreshold = 1.2f;
    private const float ExitSwimDepthThreshold = 0.6f;

    private static float GetWaterDepth(Vector3 position)
    {
        float waterHeight = WaterLevel.GetWaterLevel(position, waves: false);
        float landHeight = TerrainMeta.HeightMap.GetHeight(position);

        return waterHeight - landHeight;
    }

    /// <summary>
    /// Determines whether the survivor is currently swimming by comparing depth against the enter/exit thresholds, inferring prior swim state from the previous Y position.
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
    /// Returns the movement speed for this tick, reduced while swimming unless diving fins are equipped.
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
    /// Clamps a candidate step's Y to the water surface when over water, so a swimming survivor floats at the surface instead of following the seafloor.
    /// </summary>
    // Offset below the water surface used for the clamp; kept fixed rather than recomputed each tick for stability, and accounts for the player's transform origin being at the feet rather than the torso.
    private const float WaterSubmersionDepth = 1.1f;

    /// <summary>
    /// Locks the swim Y to the water surface height once per swim session and clamps vertical movement toward it, so swimming stays stable across different bodies of water.
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
    /// Runs a swim-depth correction unconditionally at the start of every tick to prevent the NavMeshAgent from drifting toward seafloor terrain while swimming.
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

        // ModelState.onground defaults true and nothing else clears it, so it is reset here explicitly.
        if (npc.modelState.onground)
        {
            npc.modelState.onground = false;
            npc.SendModelState(true);
        }
    }

    // Movement tick rate (20Hz) for smooth interpolated movement.
    private const float WalkTickInterval = 0.05f;

    private const float FollowStopDistance = 1.5f;
    private const float CatchUpDistance = 6f;
    private const float ResumeWalkDistance = 3f;

    // NavMeshAgent settings matching the game's own NPC agent profile, so the agent uses the correct baked navmesh layer.
    private const int FollowAgentTypeID = -1372625422;
    private const float FollowAgentRadius = 0.5f;
    private const float FollowAgentHeight = 2f;
    private const float FollowAgentBaseOffset = -0.1f;
    private const float FollowAgentAngularSpeed = 120f;
    private const float FollowAgentAcceleration = 8f;
    private const float FollowAgentStoppingDistance = 0.1f;
    private const int FollowAgentAreaMask = 1;

    // How long native movement gets to show progress before falling back to the hand-built movement engine.
    private const int FollowNativeGiveUpTicks = (int)(3f / WalkTickInterval);

    // How long to use the hand-built fallback before giving native movement another chance.
    private const int FollowNativeRetryCooldownTicks = (int)(5f / WalkTickInterval);

    // How far the follow target must move before re-issuing SetDestination, avoiding a repath every tick.
    private const float FollowNativeRepathMoveThreshold = 1f;

    // How far a destination is snapped onto the navmesh before being handed to SetDestination, since raw approach points have no navmesh awareness.
    private const float WalkNativeApproachSnapDistance = 3f;

    // How long native movement gets to show progress on a one-shot destination before falling back to the hand-built engine.
    private const int WalkNativeGiveUpTicks = (int)(3f / WalkTickInterval);

    // How often the navmesh path is recomputed while following a target.
    private const int RepathIntervalTicks = 20;

    // Forces an immediate repath if the destination has moved this far since the last computed path.
    private const float RepathMoveThreshold = 3f;

    private const float WaypointArriveDistance = 0.5f;

    /// <summary>
    /// Distance from the destination at which movement eases from a sprint to a walk, so arrivals look natural.
    /// </summary>
    private const float ApproachSlowdownDistance = 3f;

    // How close a NoPath destination has to be before falling back to direct local stepping instead of giving up.
    private const float NoPathLocalStepRadius = 10f;

    // How long the agent can be genuinely blocked (not water) before being treated as stuck.
    private const int MaxConsecutiveBlockedTicks = 40;

    // Minimum shrink in distance-to-destination per tick to count as progress for stuck detection, measured against the destination rather than raw movement.
    private const float WalkMinProgressDistance = 0.02f;

    // Minimum net progress required over a fixed time window (rather than re-baselining on any tiny improvement) to avoid the give-up timer resetting indefinitely on small back-and-forth movement.
    private const float WalkWindowMinProgressDistance = 1.5f;

    // Same fixed-window progress check as WalkWindowMinProgressDistance, scaled to the shorter native-movement watchdog window.
    private const float WalkNativeWindowMinProgressDistance = 0.4f;

    // How long with zero real progress before a walk gives up rather than running to the full timeout.
    private const int WalkGiveUpTicks = (int)(10f / WalkTickInterval);

    // How many consecutive NoPath outcomes before following escalates to the wiggle/nudge/teleport recovery sequence, since repathing alone cannot fix a genuinely missing path.
    private const int FollowNoPathRecoveryTicks = (int)(0.5f / WalkTickInterval);

    // How long path calculation can consistently fail for the same destination before treating it as unreachable and giving up immediately.
    private const int RepathFailureGiveUpTicks = (int)(3f / WalkTickInterval);

    // How long being blocked before trying to sidestep around dynamic obstacles not covered by the baked navmesh.
    private const int SidestepAfterTicks = 5;

    // Candidate headings to try when sidestepping, alternating left/right at increasing angles.
    private static readonly float[] SidestepAngles = { 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f };

    // Movement speed used while the bot is wounded/downed.
    private const float CrawlSpeed = 0.72f;

    // Caps vertical movement speed per tick so climbing an obstacle looks gradual rather than an instant snap.
    private const float MaxClimbSpeed = 3.5f;

    // Radius within which another player/bot is treated as an obstacle to avoid.
    private const float PlayerAvoidRadius = 0.6f;

    // Movement speed while climbing, deliberately slower than a normal walk.
    private const float ClimbSpeed = 2.0f;

    private const float LadderSearchRadius = 4f;

    // Distance in front of the ladder's mount face to walk before switching to a vertical climb, clearing the rung collider without missing the mount trigger.
    private const float LadderApproachOffset = 0.6f;

    // Extra distance climbed past the trigger's recorded top before dismounting, to clear the gap between the trigger and the platform surface.
    private const float ClimbOvershoot = 0.15f;

    // Fallback height above the ladder bottom the ground probe starts searching down from.
    private const float GroundProbeStartHeight = 2f;

    // How far below the probe's start height to search for ground.
    private const float GroundProbeSearchDistance = 6f;

    private const float LadderDismountStepDistance = 0.8f;

    private const float LadderMountArriveDistance = 0.3f;

    // How long to try the local-stepping approach to the ladder mount point before giving up.
    private const int LadderApproachTimeoutTicks = (int)(10f / WalkTickInterval);

    // Minimum vertical gap to the follow target before a stalled path is worth investigating for a ladder detour.
    private const float FollowClimbMinHeightGap = 1.5f;

    // Search radius for the next ladder segment during a follow detour, wider than the normal ladder search radius since consecutive segments aren't always adjacent.
    private const float FollowClimbSearchRadius = 15f;

    // Slack allowed above/below a ladder's bottom/top before treating the survivor as not yet at that level.
    private const float LadderReachTolerance = 1f;

    // How long to wait after a failed follow-climb attempt before trying again, to avoid repeated expensive ladder searches.
    private const int FollowClimbRetryCooldownTicks = (int)(5f / WalkTickInterval);

    // Height above the survivor's starting position that a monument climb aims at, taller than any real monument so ascent continues until genuinely near the top.
    private const float ClimbMonumentGoalHeight = 100f;

    // How long with zero climbing progress before a monument climb concludes it has reached as high as it can and stops.
    private const int ClimbMonumentGiveUpTicks = (int)(10f / WalkTickInterval);

    // Minimum real distance moved per tick to count as progress for the monument climb give-up timeout.
    private const float ClimbMonumentMinProgressDistance = 0.02f;

    // Name of the ramp landmark object used to route climbing at the Powerline tower, since it isn't a registered monument.
    private const string PowerlineTowerRampName = "Lvl0PlatformStairs";

    private const float PowerlineTowerRampSearchRadius = 25f;

    // Distance from the ramp's collider center at which control hands back to the generic ladder/climb logic.
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

    // Top-level safety net that detects a survivor making no real progress (position and task unchanged) and triggers a rescue, running independently alongside the existing per-episode stuck-recovery logic.

    private const float LifeStallCheckIntervalSeconds = 5f;

    private const float LifeStallTimeoutSeconds = 15f;

    // Minimum distance a survivor must move to not be considered stalled.
    private const float LifeStallMinMoveDistance = 2f;

    // Radius around a confirmed dead-end position that all survivors avoid when picking a destination/node/container, for a cooldown period.
    private const float GlobalStallZoneRadius = 15f;

    // How long a dead-end position stays avoided after being flagged.
    private const float GlobalStallZoneDurationSeconds = 600f;

    private readonly Dictionary<Guid, (Vector3 Position, TaskType Task, float Time)> _lifeStallSnapshot = new();

    // Tracks survivors skipped by the watchdog (dead/wounded/destroyed) so a diagnostic can log if one stays stuck in that state unexpectedly long.
    private readonly Dictionary<Guid, float> _lifeStallSkipSince = new();
    private readonly Dictionary<Guid, float> _lifeStallSkipDiagLastLog = new();
    private const float LifeStallSkipDiagCooldownSeconds = 60f;

    private Timer _lifeStallWatchdogTimer;

    /// <summary>
    /// Starts the recurring life-stall watchdog timer.
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

    // Caps how many actual rescues run per tick to avoid a performance hitch from many survivors rescuing simultaneously; the position/task scan itself stays cheap regardless.
    private const int LifeStallMaxRescuesPerTick = 5;

    /// <summary>
    /// Scans all survivors for stalled ones, then rescues the longest-stalled first (up to the per-tick budget) so the rescue backlog drains fairly instead of favoring whichever survivors are scanned first.
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

            // Dead/wounded/despawned survivors are skipped, since existing game mechanics already own that state.
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
                    VerbosePuts($"life-stall-diag: '{survivor.Character.Alias}' has sat continuously skipped by the watchdog for {now - skipSince:F0}s (npcNull={npc == null}, destroyed={npc?.IsDestroyed}, state={survivor.Character.State}, wounded={npc?.IsWounded()}) - genuinely stuck in a state this watchdog can't rescue from.");
                }

                continue;
            }

            _lifeStallSkipSince.Remove(characterId);

            // A survivor mid-build-replay stands mostly still by design, so it is excluded from stall detection.
            if (IsBaseBuildInFlight(characterId))
            {
                _lifeStallSnapshot.Remove(characterId);
                continue;
            }

            // Airdrop participants deliberately stand still near the drop zone until the crate lands.
            if (IsAirdropParticipant(characterId))
            {
                _lifeStallSnapshot.Remove(characterId);
                continue;
            }

            // A survivor crafting at its base workbench stands still by design (and must not get
            // its own base blacklisted as a "dead end").
            if (IsInWorkshop(characterId))
            {
                _lifeStallSnapshot.Remove(characterId);
                continue;
            }

            // A survivor in active combat (ranged or melee) is expected to hold close
            // to its target rather than cover real distance - melee in particular closes
            // to MeleeEngagementRange (0.5m) and stays there. Without this exemption the
            // watchdog misread a normal (or lightly oscillating) melee hold as "stalled"
            // and emergency-teleported the survivor mid-fight (2026-10-02, Lucas's live
            // report: a melee bot "teleported under the map... then teleported back up
            // after 5-10 seconds to continue chasing" - the teleport landing briefly
            // below the surface before correction, then combat simply re-engaging since
            // the attacker was still right there).
            if (_activeCombat.ContainsKey(characterId))
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

        // Longest-stalled first, so the backlog drains oldest-first.
        candidates.Sort((a, b) => b.StalledFor.CompareTo(a.StalledFor));

        int rescuesThisTick = 0;

        foreach ((Survivor survivor, BasePlayer npc, Vector3 currentPosition, float _) in candidates)
        {
            if (rescuesThisTick >= LifeStallMaxRescuesPerTick)
            {
                // Over budget for this tick - leave remaining candidates untouched so they get priority next tick.
                break;
            }

            RescueGenuinelyStalledSurvivor(survivor, npc, currentPosition);
            rescuesThisTick++;

            // Re-baseline immediately at the rescued position to avoid instantly re-triggering another rescue.
            _lifeStallSnapshot[survivor.Character.Id] = (npc.transform.position, survivor.Character.CurrentTask, now);
        }
    }

    /// <summary>
    /// Relocates a genuinely stalled survivor using the emergency teleport (with escalating fallback tiers), then re-enters the task pipeline so it resumes real activity rather than sitting idle at the new position.
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
            // If the standard emergency teleport fails to find valid ground nearby, fall back to a wider, ground-only relocation.
            if (!TryForceRelocateIgnoringCollision(survivor, npc))
            {
                VerbosePuts($"'{survivor.Character.Alias}' - stall-rescue's last-resort relocation also failed to find real ground nearby.");

                // Last resort: a raw nearby relocation with minimal validation.
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

    // Elevation tolerance for candidate relocation points, wider than the default since candidates are searched over a larger 30-80m radius.
    private const float StallRescueMaxElevationChange = 15f;

    /// <summary>
    /// Searches for a valid relocation point in a wide radius, validating ground/elevation/obstruction but skipping the path-connectivity check since the survivor is already stuck somewhere pathfinding does not work.
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

            // Seed a ground-level guess from the terrain heightmap so the ground probe searches near a legitimate starting height rather than blind.
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

        VerbosePuts($"stall-rescue-diag: '{survivor.Character.Alias}' - all {StallRescueForceRelocateAttempts} force-relocate candidates failed, first reason: {firstFailureReason ?? "unknown"}.");

        return false;
    }

    // Maximum vertical difference allowed between a raw relocate candidate and the survivor's current position.
    private const float StuckRawRelocateElevationTolerance = 5f;

    // Short relocation distance for this last-resort tier, intended to clear a small local glitch rather than travel far.
    private const float StuckRawRelocateMinDistance = 5f;
    private const float StuckRawRelocateMaxDistance = 15f;

    private const int StuckRawRelocateAttempts = 8;

    /// <summary>
    /// Last-resort relocation that skips ground-probe validation and only checks for physical obstruction, trading landing precision for guaranteed forward progress once earlier rescue tiers have failed.
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
    /// Per-survivor state for following a navmesh path, including the computed path, current target corner, and repath bookkeeping.
    /// </summary>
    private sealed class PathFollower
    {
        public readonly RustNavMeshPath Path = new();
        public int CornerIndex;
        public Vector3 LastPathTarget;
        public int TicksSinceRepath;
        public int ConsecutiveBlockedTicks;
        public string LastPathFailureReason;

        // Consecutive ticks the path calculation has failed identically for the current destination, tracked independently of the blocked-ticks/distance-progress heuristics since an unreachable target can fool those via circular local movement.
        public int ConsecutiveRepathFailureTicks;

        // Which sidestep angle worked last tick, tried first to avoid rapid jitter between alternating candidate angles.
        public float PreferredSidestepAngle;

        // Tracks real distance progress across ticks independent of per-tick step success, so a consistently-stepping-but-not-progressing follow (e.g. parallel to a doorframe) can still be detected as stalled.
        public float LastLocalStepProgressDistance = float.MaxValue;

        // Real elapsed time the current stall window started; -1 means no window is open yet.
        public float LocalStepProgressWindowStartTime = -1f;

        // Diagnostic state for the noclip debug bypass.
        public bool NoclipDiagLogged;
        public int NoclipDiagTicks;
    }

    /// <summary>
    /// Spawns a connectionless survivor BasePlayer near the calling player.
    /// </summary>
    [ChatCommand("lr.spawn")]
    private void CmdSpawnSurvivor(BasePlayer player, string command, string[] args)
    {
        RunSpawnSurvivor(player);
    }

    /// <summary>
    /// Console/bindable version of the spawn command, with the same behavior as the chat command.
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

    // Maximum reach of the spawn command's aim-point raycast.
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
        // Bot ID is shown separately from the nametag alias, for use with commands or the console that need the raw userID.
        player.ChatMessage($"[LivingRust] Spawned survivor '{character.Alias}' {where}. (ID {character.BotId})");
    }

    /// <summary>
    /// Finds the nearest solid, non-trigger surface along the caller's view ray to spawn at, falling back to a point in front of the player at terrain height if nothing is hit.
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
    /// Sends the nearest spawned survivor walking to the nearest water, stopping at the shoreline.
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
    /// Sends the nearest spawned survivor walking to the nearest monument matching the given name.
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
    /// Sends the nearest spawned survivor to follow the calling player at a walking pace, stopping a short distance away and waiting at the shoreline instead of entering water.
    /// </summary>
    [ChatCommand("lr.follow")]
    private void CmdFollow(BasePlayer player, string command, string[] args)
    {
        RunFollow(player);
    }

    /// <summary>
    /// Console/bindable version of the follow command, with the same behavior as the chat command.
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
    /// Cancels whatever movement behavior the nearest spawned survivor is currently doing (walking or following).
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

        // Resets the animator/model state so the bot doesn't stay frozen mid-climb or mid step-up/crouch.
        ReleaseMovementState(survivor.Player);

        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' has stopped.");
    }

    /// <summary>
    /// Sends the nearest spawned survivor to the nearest ladder and climbs it, up or down depending on the survivor's current height relative to the ladder.
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

        // Climbs toward whichever end of the ladder the survivor is not currently closer to.
        float ladderMidpointY = (ladder.Bottom.y + ladder.Top.y) * 0.5f;
        bool climbingDown = npc.transform.position.y >= ladderMidpointY;

        // Uses local obstacle-aware stepping instead of the navmesh path, since the navmesh around ladders is unreliable for short-range approaches.
        StartClimbing(survivor, BuildClimbState(npc, _engine.NavigationManager, ladder, climbingDown));

        string direction = climbingDown ? "down" : "up";
        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' is heading to climb {direction} the ladder.");
    }

    /// <summary>
    /// Sends the nearest spawned survivor climbing upward on its own toward a fixed high point, climbing every reachable ladder in sequence.
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

        // Uses an authored route when one exists for this monument, falling back to the generic climb heuristic otherwise.
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
    /// Creates the real, connectionless BasePlayer for a Survivor by spawning the same prefab real players use, with no Network.Connection assigned.
    /// health and restoreInventory are optional and are used when restoring a still-alive survivor after a server restart, using its saved inventory instead of the default starting kit.
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

        // Must be set before Spawn(), since displayName and userID need to be in place before the entity's first network snapshot goes out.
        // Reuses Character.BotId as the userID so Rust's blueprint-unlock and building-privilege systems carry over across a respawn.
        npc.userID = survivor.Character.BotId;
        npc.displayName = survivor.Character.Alias;

        if (useBeachSpawnPoint)
        {
            // Uses Rust's own dedicated spawn point lookup so bots start at real, procedurally-distributed beach spawns,
            // re-rolled if another survivor is already standing on the chosen point (see LivingRust.SpawnSpacing.cs).
            FindUnoccupiedSpawnPosition(npc, survivor.Character.Id, out position, out rotation);
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
            // Grants the default starting kit for a fresh life.
            GiveStartingKit(npc);
        }

        survivor.Player = npc;
        survivor.Position = position;
        survivor.Character.Position = position;
        survivor.Character.Rotation = rotation;
        survivor.Spawned = true;
        survivor.Character.Spawned = true;

        // A character brought back through here is alive by definition, so its state is reset in case it was left at Dead from an interrupted respawn.
        survivor.Character.State = CharacterState.Alive;

        // Logs the instanceID as a diagnostic for tracing orphaned bot entities.
        Puts($"Spawned BasePlayer for '{survivor.Character.Alias}' (userID {npc.userID}, instanceID {npc.GetInstanceID()}) at {position}.");

        return npc;
    }

    /// <summary>
    /// Starts (or restarts) a navmesh-pathed walk toward a destination, stopping automatically at the shoreline. Routes around large obstacles via the navmesh and steps up onto small ones along the way.
    /// Tries Rust's native navigation first and falls back to the hand-built pathing engine if native movement stalls.
    /// onFailed fires for every way the walk can end without reaching the destination (stuck, no path, timeout, or shoreline), giving callers one place to react.
    /// Sprints by default unless shouldWalkCarefully says otherwise for a given tick, and always eases to a walk within ApproachSlowdownDistance of the destination.
    /// </summary>
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
            // Diagnostic log for the first tick of a walk, gated behind verbose logging.
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

            // Survivors swim through water rather than being routed around it; the game's own water/swim mechanics handle drowning damage and animation.

            // Wounded/downed survivors pause movement entirely rather than fighting the incapacitated state machine, and wounded ticks do not count toward the walk timeout.
            if (npc.IsWounded())
            {
                return;
            }

            // Runs every tick regardless of branch, to correct any swim-depth drift.
            CorrectSwimDrift(survivor, npc);

            // Periodic progress diagnostic, roughly every 2 seconds.
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
                // Opens nearby doors manually since the automatic door-proximity trigger only fires for real NPCs, not a disconnected survivor.
                TryOpenNearbyDoors(npc);

                EnsureNativeNavAgent(npc, out nativeAgent, out unityAgent);
                nativeAgent.speed = GetSwimAdjustedSpeed(npc, hurry ? RunSpeed : WalkSpeed, IsSwimmingNow(npc.transform.position, survivor.Position.y));

                if (!nativeDestinationSet)
                {
                    // Snap the raw approach point onto the nearest navmesh point before handing it to the native agent.
                    Vector3 nativeTarget = destination;

                    if (nativeAgent.SamplePosition(destination, out NavMeshHit navHit, WalkNativeApproachSnapDistance))
                    {
                        nativeTarget = navHit.position;
                    }

                    nativeAgent.SetDestination(nativeTarget);
                    nativeDestinationSet = true;
                }

                // Path calculation is asynchronous, so wait for pathPending to resolve before judging whether the path is usable, but capped so a permanently-stuck pathPending cannot block every other watchdog.
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

                    // Arrival counts either at the original destination or at the snapped navmesh target, bounded by the raw distance to avoid claiming arrival too far away.
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

                    // Tracks the best-ever distance to destination every tick to detect stalled progress.
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
                            // Sets the networked water level manually since a disconnected bot never sends the client tick that normally updates it, which drives the swim animation.
                            npc.modelState.waterLevel = npc.WaterFactor();
                            // Clears the on-ground flag while swimming, matching the game's own swim threshold, since it otherwise defaults true and is never cleared.
                            npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
                            npc.SendModelState(true);

                            FaceDirection(npc, unityAgent.velocity);

                            // Clamps Y to the water surface so native movement does not follow the seafloor underwater.
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
                    // Logs when native movement never gets a usable path, to help diagnose destinations the navmesh rejects immediately.
                    VerbosePuts($"'{survivor.Character.Alias}' - native movement never got a usable path toward {destination} (isOnNavMesh={unityAgent.isOnNavMesh}, hasPath={nativeAgent.hasPath}, pathStatus={nativeAgent.pathStatus}), falling back to hand-built movement immediately.");
                }

                useNative = false;
                nativeAgent.ResetPath();
                unityAgent.enabled = false;

                // The hand-built follower starts fresh from wherever native movement left the survivor.
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
                // Tracks the best-ever distance to destination each tick to detect a stalled walk; skipped for Blocked since that has its own give-up tracking.
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
    /// Mutable per-climb state for <see cref="AdvanceClimb"/>, shared between the standalone climb command and a follow-in-progress detour.
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
    /// Computes the mount point, facing, and ascend/descend target for climbing a ladder from the survivor's current position.
    /// </summary>
    private static ClimbState BuildClimbState(BasePlayer npc, NavigationManager navigation, NavigationManager.LadderInfo ladder, bool climbingDown, bool ignoreHeadroomOnApproach = false)
    {
        float mountSide = Vector3.Dot(npc.transform.position - ladder.Bottom, ladder.MountAxis);
        Vector3 approachDirection = (Mathf.Abs(mountSide) > 0.01f ? Mathf.Sign(mountSide) : 1f) * ladder.MountAxis;
        Vector3 intoLadder = -approachDirection;

        // Keeps the survivor's current height rather than the ladder trigger's bottom, since the trigger sits below the actual deck surface.
        Vector3 mountOffset = ladder.Bottom + approachDirection * LadderApproachOffset;
        Vector3 mountPoint = new Vector3(mountOffset.x, npc.transform.position.y, mountOffset.z);

        float targetY;

        if (climbingDown)
        {
            // Probes for actual ground instead of trusting the ladder trigger's bottom bound, which does not reliably reach the real floor on every ladder.
            Vector3 groundProbeOrigin = ladder.Bottom + intoLadder * LadderDismountStepDistance;

            targetY = navigation.TryFindGroundBelow(groundProbeOrigin, GroundProbeStartHeight, GroundProbeSearchDistance, out float groundY, out _)
                ? groundY
                : ladder.Bottom.y - ClimbOvershoot;
        }
        else
        {
            // Climbing up dismounts onto the platform above the ladder's top, offset by the fixed overshoot.
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
    /// Advances a climb by one tick: approaches the mount point with local obstacle-aware stepping, climbs the ladder, then steps off onto solid ground.
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

            // A Blocked result is a no-op this tick and simply retries up to the timeout, rather than sidestepping. IgnoreHeadroomOnApproach lets specific authored ladders skip the headroom check where it would otherwise clip nearby geometry.
            StepResult step = _engine.NavigationManager.TryGetNextStep(current, climb.MountPoint, approachStepDistance, out Vector3 nextStep, out string approachBlockReason, climb.IgnoreHeadroomOnApproach);

            // Logged once per approach attempt rather than every tick, to explain a blocked approach without spamming.
            if (step == StepResult.Blocked && !climb.ApproachBlockLogged)
            {
                climb.ApproachBlockLogged = true;
                VerbosePuts($"'{survivor.Character.Alias}' approach to ladder mount point {climb.MountPoint} blocked: {approachBlockReason}");
            }

            ApplyMovementStep(survivor, npc, current, nextStep, step == StepResult.SteppedUp, sprinting: false, approachStepDistance);

            if (Vector3.Distance(nextStep, climb.MountPoint) < LadderMountArriveDistance)
            {
                climb.Approaching = false;

                // Drives the client-side climb animation via the networked model state.
                npc.modelState.onLadder = true;
                npc.modelState.ladderType = climb.Ladder.LadderType;

                // Clears any leftover crouch state from the approach so it doesn't carry into the climb.
                npc.modelState.ducked = false;
                npc.modelState.ducking = 0f;

                // Forces the model state to broadcast, since a connectionless bot never triggers the normal diff-and-send tick.
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

        // Steps off the ladder using the normal obstacle-aware surface probe rather than guessing the platform's exact height.
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
    /// Runs a climb as a standalone movement behavior, driving AdvanceClimb every tick until it finishes or times out.
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

            // Wounded/downed survivors pause the climb entirely rather than continuing to be moved along the ladder.
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
    /// Attaches and configures the native NavMeshAgent and RustNavMeshAgent wrapper on the given NPC if not already present. Idempotent and safe to call every tick.
    /// </summary>
    // Maximum distance to a real on-mesh point for Warp() to be treated as a harmless re-sync rather than a real relocation.
    private const float NativeAgentWarpMaxSnapDistance = 1.5f;

    private void EnsureNativeNavAgent(BasePlayer npc, out RustNavMeshAgent agent, out NavMeshAgent unityAgent)
    {
        unityAgent = npc.GetComponent<NavMeshAgent>();

        if (unityAgent == null)
        {
            unityAgent = npc.gameObject.AddComponent<NavMeshAgent>();

            // Left disabled here to avoid a spurious "no valid NavMesh" log on enable; it's only turned on later once navmesh coverage is confirmed.
            unityAgent.enabled = false;

            unityAgent.agentTypeID = FollowAgentTypeID;
            unityAgent.radius = FollowAgentRadius;
            unityAgent.height = FollowAgentHeight;
            unityAgent.baseOffset = FollowAgentBaseOffset;
            unityAgent.angularSpeed = FollowAgentAngularSpeed;
            unityAgent.acceleration = FollowAgentAcceleration;
            unityAgent.stoppingDistance = FollowAgentStoppingDistance;
            unityAgent.autoBraking = false;
            // Uses the cheapest obstacle-avoidance tier that still makes agents aware of each other's bodies, for scale-test performance.
            unityAgent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;
            // Randomizes avoidance priority per agent so agents packed tightly together don't deadlock with equal-priority avoidance.
            unityAgent.avoidancePriority = UnityEngine.Random.Range(0, 100);
            unityAgent.areaMask = FollowAgentAreaMask;
        }

        // Re-syncs the agent's position via Warp before it is reused, so a stale disabled agent doesn't snap the survivor to an outdated tracked position.
        // Only warps when a real on-mesh point exists nearby, so a survivor standing off-mesh (e.g. inside their own base) is left untouched and the hand-built fallback takes over instead.
        // Only enables the agent once real navmesh coverage is confirmed, to avoid a spurious "no valid NavMesh" warning; otherwise it stays disabled and the hand-built fallback takes over.
        if (NavMesh.SamplePosition(npc.transform.position, out NavMeshHit onMeshHit, NativeAgentWarpMaxSnapDistance, NavMesh.AllAreas))
        {
            unityAgent.enabled = true;
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
    /// Starts (or restarts) continuous following of a target, walking normally and breaking into a run to catch up when far behind.
    /// Tries native navigation first for ordinary movement, falling back to the hand-built movement engine when native pathing fails or stalls; ladder climbing always uses the hand-built engine.
    /// stopDistance lets callers hold at a custom range instead of the default follow distance. skipIdleFacing lets a caller that already manages facing opt out of this method's idle facing.
    /// </summary>
    // target is BaseEntity rather than BasePlayer so non-player entities (e.g. animals) can be followed too.
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

        // Counts consecutive NoPath outcomes so a single transient blip doesn't trigger the full recovery sequence.
        int consecutiveNoPathTicks = 0;

        Timer followTimer = null;

        followTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || target == null || target.IsDestroyed)
            {
                followTimer.Destroy();
                _activeMovement.Remove(characterId);
                nativeAgent?.ResetPath();
                return;
            }

            // Wounded/downed survivors pause movement entirely rather than fighting the incapacitated state machine.
            if (npc.IsWounded())
            {
                return;
            }

            // Unconditional per-tick correction for swim-depth drift, regardless of which branch the rest of this tick takes.
            CorrectSwimDrift(survivor, npc);

            if (climb != null)
            {
                ClimbOutcome climbOutcome = AdvanceClimb(climb, survivor, npc);

                if (climbOutcome != ClimbOutcome.InProgress)
                {
                    climb = null;

                    // Forces a fresh path next tick since climbing may have moved the survivor to a different navmesh island.
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
                // Opens nearby doors manually since native's own door-proximity trigger never fires for a disconnected survivor.
                TryOpenNearbyDoors(npc);

                // Re-enables the native agent's components if a previous fallback disabled them.
                EnsureNativeNavAgent(npc, out nativeAgent, out unityAgent);

                nativeAgent.speed = GetSwimAdjustedSpeed(npc, sprinting ? RunSpeed : WalkSpeed, IsSwimmingNow(npc.transform.position, survivor.Position.y));

                if (!nativeAgent.hasPath || Vector3.Distance((Vector3)nativeAgent.destination, destination) > FollowNativeRepathMoveThreshold)
                {
                    // Snaps onto the real navmesh first as cheap insurance against a raw destination being off-mesh.
                    Vector3 nativeTarget = destination;

                    if (nativeAgent.SamplePosition(destination, out NavMeshHit navHit, WalkNativeApproachSnapDistance))
                    {
                        nativeTarget = navHit.position;
                    }

                    nativeAgent.SetDestination(nativeTarget);
                }

                // Waits for asynchronous path calculation to resolve before judging usability, capped so a permanently-stuck pathPending cannot block every watchdog below it.
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
                    // Fixed-window progress check rather than reset-on-any-improvement.
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
                        // Sets the networked water level manually to drive the swim animation.
                        npc.modelState.waterLevel = npc.WaterFactor();
                        // Clears the on-ground flag while swimming.
                        npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
                        npc.SendModelState(true);

                        // Respects skipIdleFacing here too, so a caller managing its own facing (e.g. combat aiming) doesn't fight this call for facing ownership while actively pathing.
                        if (!skipIdleFacing)
                        {
                            FaceDirection(npc, unityAgent.velocity);
                        }

                        // Floats a swimming survivor at the real water surface instead of following the baked navmesh's own seafloor Y.
                        Vector3 swimAdjustedFollowPosition = ClampToWaterSurfaceIfSwimming(survivor, npc.transform.position, survivor.Position.y, IsSwimmingNow(npc.transform.position, survivor.Position.y));
                        npc.MovePosition(swimAdjustedFollowPosition);
                        survivor.Position = swimAdjustedFollowPosition;
                        survivor.Character.Position = swimAdjustedFollowPosition;

                        // Keeps the hand-built follower's path state fresh in case of a later fallback, so it repaths immediately instead of resuming a stale route.
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

                // Disables the agent outright (not just clearing the path), since an enabled-but-pathless agent still constrains position to the navmesh, which would fight the hand-built fallback's control.
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

            // Checks wasBlocked alone (not just Stuck/NoPath) so a ladder detour still gets a chance even on a tick a sidestep rescued into Progressing.
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

                // When no ladder is directly climbable yet, walk toward the nearest ladder as a concrete landmark, preferring the named ramp entry first when ascending.
                bool climbingDown = destination.y < npc.transform.position.y;

                if (climbingDown || !TryStepTowardKnownRampEntry(survivor, npc, ref ladderSidestepAngle, ref reachedRampLandmark))
                {
                    TryStepTowardNearestLadder(survivor, npc, climbingDown, ref ladderSidestepAngle);
                }
            }

            // Following must never simply give up; forces an immediate repath so the navmesh solver gets a fresh chance instead of retrying the same geometry.
            if (outcome == MovementOutcome.Stuck)
            {
                VerbosePuts($"'{survivor.Character.Alias}' is stuck following - forcing a repath.");
                follower.TicksSinceRepath = RepathIntervalTicks;
                follower.ConsecutiveBlockedTicks = 0;
                consecutiveNoPathTicks = 0;
                return;
            }

            // A genuine NoPath means repathing alone cannot help, so this escalates to the full stuck-recovery sequence.
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

                // Uses the target's current position; recovery resumes normal following once it finishes or exhausts.
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
    /// Drives a survivor toward a fixed point far above its starting position, using the same movement/ladder-detour loop as following. Unlike following, this is a one-shot goal that stops once no progress is made for a while, assuming it has reached as high as the structure goes.
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

            // Wounded/downed survivors pause movement entirely.
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

            // Checks wasBlocked alone (not just Stuck/NoPath) so a ladder detour still gets a chance even on a tick a sidestep rescued into Progressing.
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

                // No ladder is climbable from here yet, so this walks toward the nearest ladder (or the Powerline tower's named ramp, tried first) as a concrete landmark instead. Always ascending here, never down.
                if (!TryStepTowardKnownRampEntry(survivor, npc, ref ladderSidestepAngle, ref reachedRampLandmark))
                {
                    TryStepTowardNearestLadder(survivor, npc, climbingDown: false, ref ladderSidestepAngle);
                }
            }

            // Tracks real distance moved, since movement can report success (Progressing) without the survivor actually having gone anywhere, making the give-up timeout robust to that.
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
    /// Steps once toward the nearest ladder's mount-relevant position using local obstacle-aware stepping, as a fallback landmark when no ladder is climbable yet.
    /// climbingDown must come from the caller's stable destination-vs-height comparison rather than being re-derived here, to avoid jitter near a ladder's midpoint.
    /// Falls back to the same sidestep logic as ordinary obstacle avoidance, and filters candidates to only ladders going the right direction so it does not re-target the ladder just climbed.
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
    /// Steps toward the Powerline tower's ground-level ramp/stairs entrance by name, when one exists nearby, so ascent routes around the tower's base structure instead of cutting through it.
    /// Latches permanently false once the survivor gets within arrive distance, rather than re-checking live every tick, to avoid oscillating back and forth right at the boundary.
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
    /// Whether climbing this ladder in the given direction makes sense from the survivor's current position, true only when standing at the end the direction climbs away from. Rejects re-climbing the segment just finished.
    /// </summary>
    private static bool LadderGoesRightWay(NavigationManager.LadderInfo ladder, Vector3 fromPosition, bool climbingDown)
    {
        float ladderMidpointY = (ladder.Bottom.y + ladder.Top.y) * 0.5f;
        bool nearTopOfLadder = fromPosition.y >= ladderMidpointY;

        return climbingDown == nearTopOfLadder;
    }

    /// <summary>
    /// Checks whether a nearby ladder would close the vertical gap toward the destination, and if so builds a ClimbState for it. Called by both StartFollowing and StartClimbingMonument when path-following stalls.
    /// </summary>
    private bool TryBuildFollowClimb(Survivor survivor, BasePlayer npc, Vector3 destination, out ClimbState climb)
    {
        climb = null;

        Vector3 origin = npc.transform.position;
        float verticalGap = destination.y - origin.y;

        if (Mathf.Abs(verticalGap) < FollowClimbMinHeightGap)
        {
            // Logs this case since it can indicate a genuine dead end (in range of the destination but still unable to path there), not just an ordinary same-level retry.
            VerbosePuts($"'{survivor.Character.Alias}' can't progress following - within height range of destination ({verticalGap:F1}m) but still can't path there; no ladder detour applies.");
            return false;
        }

        bool climbingDown = verticalGap < 0f;

        // Filters candidates by direction during the search itself, rather than picking nearest-overall, so the ladder just climbed isn't re-selected over the real next segment.
        if (!_engine.NavigationManager.TryFindNearestLadder(origin, FollowClimbSearchRadius, out NavigationManager.LadderInfo ladder,
                candidate => LadderGoesRightWay(candidate, origin, climbingDown)))
        {
            VerbosePuts($"'{survivor.Character.Alias}' can't progress following - no ladder within {FollowClimbSearchRadius:F0}m goes the right way.");
            return false;
        }

        // BuildClimbState assumes the survivor is already roughly at the ladder's mount height. From ground level below an elevated ladder that isn't true yet (a ramp/stairs is needed first), so that case is rejected here.
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
    /// Returns true once local-stepping has gone a full time window without shrinking real distance-to-destination by a meaningful amount, updating the follower's tracking fields as a side effect.
    /// Shared by every "about to report Progressing" site in AdvanceAlongPath, including the sidestep-rescue path, so a survivor that keeps stepping without ever making real progress is still detected as stalled.
    /// Uses real elapsed time rather than a tick count, since this method is called at different cadences by different callers and a tick count would not represent the same stall duration for each.
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

        // Feeds the same self-learning monument-avoid-zone system every other stuck path
        // uses. Without this, an oscillation trip here only ever triggers the recovery
        // ladder (wiggle/nudge/teleport) for the one survivor currently stuck - it never
        // gets remembered, so every later survivor that targets the same tight spot
        // (a recycler or card reader wedged in an alcove is the common case) oscillates
        // through this same 10s stall from scratch instead of the spot ever being learned.
        RecordPotentialAvoidZone(appliedPosition);

        follower.LastLocalStepProgressDistance = float.MaxValue;
        follower.LocalStepProgressWindowStartTime = -1f;
        return true;
    }

    // How far past a hard-avoid animal's position the detour waypoint pushes out to the side, wide enough to give the detection radius a real miss.
    private const float HardAvoidAnimalSidestepDistance = 20f;

    /// <summary>
    /// Bends the immediate path target sideways around threatPosition without touching the survivor's real destination. Picks whichever side of the current-to-destination line the threat isn't already on.
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

        // Noclip mode flies straight toward the destination with no collision/ground/navmesh awareness, bypassing all step/door/water logic below.
        if (_noclipEnabled)
        {
            wasBlocked = false;
            Vector3 toDestination = destination - current;
            float remaining = toDestination.magnitude;
            Vector3 flown = remaining <= stepDistance ? destination : current + toDestination.normalized * stepDistance;

            if (!follower.NoclipDiagLogged)
            {
                follower.NoclipDiagLogged = true;
                VerbosePuts($"noclip-diag: '{survivor.Character.Alias}' entering noclip bypass at {current}, flying toward {destination} ({remaining:F1}m away), stepDistance={stepDistance:F2}.");
            }

            npc.MovePosition(flown);
            survivor.Position = flown;
            survivor.Character.Position = flown;

            // Periodic diagnostic checking whether the position write actually sticks by the next tick.
            if (++follower.NoclipDiagTicks % 40 == 0)
            {
                VerbosePuts($"noclip-diag: '{survivor.Character.Alias}' intended {flown}, npc.transform.position now reads {npc.transform.position} ({remaining:F1}m from destination).");
            }

            if (!skipFacing && toDestination.sqrMagnitude > 0.0001f)
            {
                FaceDirection(npc, toDestination);
            }

            return remaining <= WaypointArriveDistance
                ? MovementOutcome.ReachedDestination
                : MovementOutcome.Progressing;
        }

        // True whenever the direct line toward the current target was blocked this tick, even if a sidestep then rescued it into Progressing, so callers can still offer a ladder detour a chance to intercept.
        wasBlocked = false;

        // Opens nearby doors unconditionally before every repath attempt, since a closed door carves a hole in the baked navmesh and a disconnected bot never triggers the game's own auto-open.
        TryOpenNearbyDoors(npc);

        // Wounded/downed survivors can't sprint while crawling, overriding whatever speed the caller asked for.
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

            // Detours around a nearby hostile animal by bending only this repath cycle's pathfinder target, never the survivor's real destination. Falls back to the real destination if the detour point itself fails to path to, so a failed detour never corrupts the real reachability tracking below.
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

                // Throttled to roughly once per repath rather than every tick, to avoid flooding the log while a survivor is stuck.
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
                // Path calculation has failed consistently for long enough that it's treated as genuinely unreachable rather than retried further.
                VerbosePuts($"'{survivor.Character.Alias}' repath has failed for {RepathFailureGiveUpTicks * WalkTickInterval:F0}s straight ({follower.LastPathFailureReason}) - treating as genuinely unreachable rather than continuing to retry.");

                wasBlocked = true;

                ReleaseMovementState(npc);
                if (!skipFacing) { FaceDirection(npc, destination - current); }
                return MovementOutcome.NoPath;
            }

            // The baked navmesh can be fragmented into small disconnected islands, so a nearby target can fail to path to even along an open, walkable line. Falls back to local obstacle-aware stepping within a short radius rather than giving up outright.
            if (Vector3.Distance(current, destination) <= NoPathLocalStepRadius)
            {
                StepResult localStep = _engine.NavigationManager.TryGetNextStep(current, destination, stepDistance, out Vector3 localNextStep, out string localBlockReason);

                if (localStep == StepResult.Blocked)
                {
                    wasBlocked = true;

                    // Attempts a sidestep instead of giving up immediately, so a followed survivor near a navmesh gap can still route around the obstacle.
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

                    // Logged once per stuck episode rather than every tick, naming what's actually blocking the survivor.
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

            // Also covers destinations too far for local stepping to help, such as a vertical gap that needs a ladder detour instead.
            wasBlocked = true;

            ReleaseMovementState(npc);
            if (!skipFacing) { FaceDirection(npc, destination - current); }
            return MovementOutcome.NoPath;
        }

        Vector3 waypoint = follower.CornerIndex < follower.Path.corners.Count
            ? (Vector3)follower.Path.corners[follower.CornerIndex]
            : destination;

        // A swimming survivor's Y is externally locked regardless of underwater terrain shape, so step-height limits should not block it.
        bool isSwimmingThisStep = IsSwimmingNow(current, survivor.Position.y);

        StepResult step = _engine.NavigationManager.TryGetNextStep(current, waypoint, stepDistance, out Vector3 nextStep, out string blockReason, ignoreStepHeight: isSwimmingThisStep);

        // Does not block on water; swimming applies uniformly regardless of which movement engine is active.

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

            // Logged once per blocked episode rather than every tick, naming exactly what stopped movement.
            if (follower.ConsecutiveBlockedTicks == 1)
            {
                string reason = blockedByOtherPlayer ? "another player/bot is in the way" : blockedByHorse ? "a horse is in the way" : blockedByBike ? "a bike is in the way" : blockedByCactus ? "a cactus is in the way" : blockedByBalloon ? "a hot air balloon is in the way" : blockedByWoodBarricade ? "a wooden barricade is in the way" : blockReason;
                VerbosePuts($"'{survivor.Character.Alias}' blocked heading to waypoint {waypoint}: {reason}");
            }

            // Dynamic obstacles (trees, scatter, other players/bots) aren't part of the baked navmesh, so a sidestep is tried instead of just freezing.
            if (follower.ConsecutiveBlockedTicks >= SidestepAfterTicks
                && TryFindSidestep(current, waypoint, stepDistance, npc, followTarget, ref follower.PreferredSidestepAngle, out Vector3 sidestepTarget, out StepResult sidestepResult))
            {
                Vector3 sidestepApplied = ApplyMovementStep(survivor, npc, current, sidestepTarget, sidestepResult == StepResult.SteppedUp, sprinting, stepDistance, skipFacing);
                follower.ConsecutiveBlockedTicks = 0;

                // Lets a sidestep count as arriving at the waypoint too, so the survivor doesn't walk past it and get stuck aiming behind itself.
                if (Vector3.Distance(sidestepTarget, waypoint) < WaypointArriveDistance && follower.CornerIndex < follower.Path.corners.Count - 1)
                {
                    follower.CornerIndex++;
                }

                // Applies the same real-distance stall watchdog to sidestep rescues as to a plain unblocked step, since a sidestep can otherwise repeat indefinitely.
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

        // Same stall watchdog as the no-path fallback branch, for the common case where a real path was found but individual steps keep "succeeding" without real progress toward the destination.
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
    /// Tries a fan of headings around the direct line to waypoint, looking for one that isn't blocked, so the bot can flow around a small, localized obstruction.
    /// preferredAngle is tried first, before scanning the full fan, to avoid alternating between two candidates and producing side-to-side jitter. Reset to 0 by the caller once actually unblocked.
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

        // Uses self's current Y as a stand-in for a tracked previous Y, since no Survivor reference is available here.
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
    /// Applies one movement step: facing, sprint/duck animation state, the position update, and keeping the Survivor's tracked position in sync.
    /// Clamps vertical rise/fall per tick so climbing an obstacle looks gradual, and logs a warning if a step still exceeds the expected distance after clamping.
    /// </summary>
    /// <summary>
    /// Opens doors near the survivor right before every hand-built step, using the same door API the native NavMeshAgent uses, since a disconnected bot never triggers the game's own auto-open trigger.
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

        // Overrides the ground-following Y computed above, so a swimming survivor floats at the water surface instead of walking the seafloor.
        nextStep = ClampToWaterSurfaceIfSwimming(survivor, nextStep, current.y, IsSwimmingNow(nextStep, current.y));

        float actualDistance = Vector3.Distance(current, nextStep);

        // Combines the expected horizontal step distance with the actual vertical delta, so normal slope-following terrain isn't flagged as an oversized step.
        float verticalDelta = Mathf.Abs(nextStep.y - current.y);
        float expectedDistanceWithClimb = Mathf.Sqrt(expectedStepDistance * expectedStepDistance + verticalDelta * verticalDelta);

        if (actualDistance > expectedDistanceWithClimb + 0.05f)
        {
            Puts($"WARNING: '{survivor.Character.Alias}' moved {actualDistance:F2}m in one tick (expected <= {expectedDistanceWithClimb:F2}m accounting for {verticalDelta:F2}m of climb, sprinting: {sprinting}) from {current} to {nextStep}.");
        }

        // skipFacing lets a caller that already manages facing (e.g. combat's own aim logic) opt out here too, matching the same gate applied to native movement.
        if (!skipFacing)
        {
            FaceDirection(npc, nextStep - current);
        }

        npc.modelState.sprinting = sprinting;

        // Always false rather than tied to step height, since ordinary terrain rises are common at run speed and shouldn't trigger the visibly slower crouch-walk animation.
        npc.modelState.ducked = false;
        npc.modelState.ducking = 0f;
        // Sets the networked water level manually to drive the swim animation, since a disconnected bot never sends the tick that normally keeps it in sync.
        npc.modelState.waterLevel = npc.WaterFactor();
        // Clears the on-ground flag while swimming, since it otherwise defaults true and is never cleared.
        npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
        npc.SendModelState(true);

        npc.MovePosition(nextStep);

        survivor.Position = nextStep;
        survivor.Character.Position = nextStep;

        // Tracks the most recent real, unblocked step so emergency-teleport recovery can relocate back to it instead of guessing a random direction.
        survivor.LastKnownGoodPosition = nextStep;

        return nextStep;
    }

    /// <summary>
    /// Whether another player/bot (not self, and not an explicitly exempt target) occupies this position, so bots don't walk through each other or other players.
    /// Excludes IsNpc entities like shopkeepers, since a stationary safe-zone NPC never moves out of the way and would leave a bot permanently stuck waiting.
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

    // How far out to treat a horse as a hard obstacle to route around, wider than a player, so bots walk around a horse instead of climbing onto it.
    private const float HorseAvoidRadius = 2f;

    // The vehicle layers a horse's colliders sit on.
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

    // Same reasoning as IsBlockedByHorse - bikes/motorbikes sit on the same obstacle layers, so this avoids a bot climbing onto one instead of walking around it.
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

    // How far out to steer clear of a cactus. Its thin trunk and jutting colliders make an awkward, easy-to-wedge-into gap that repathing/sidestepping can't reliably solve, so it's avoided from a distance instead.
    private const float CactusAvoidRadius = 2f;

    // The layer a cactus's collider sits on.
    private static readonly int CactusLayerMask = LayerMask.GetMask("Tree");

    private static bool IsBlockedByCactus(Vector3 position)
    {
        Collider[] nearby = Physics.OverlapSphere(position, CactusAvoidRadius, CactusLayerMask, QueryTriggerInteraction.Ignore);

        foreach (Collider col in nearby)
        {
            BaseEntity entity = col.GetComponentInParent<BaseEntity>();

            // Substring match against the cactus prefab family rather than an exhaustive list.
            if (entity != null && entity.ShortPrefabName.IndexOf("cactus", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    // How far out to steer clear of a hot air balloon's cage/gondola cluster, a densely packed set of colliders that's hard for repathing/sidestepping alone to escape.
    private const float HotAirBalloonAvoidRadius = 4.5f;

    private static bool IsBlockedByHotAirBalloon(Vector3 position)
    {
        // Reuses HorseLayerMask since a balloon's colliders sit on the same vehicle layers.
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

    // Matches the "barricade.wood" prefab specifically; its angled wooden stakes are the same thin/jutting collider shape that's hard for repathing/sidestepping alone to escape.
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
    /// Resets movement-related animation state to neutral, called whenever the bot stops taking a step, so it can't freeze mid-pose.
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
    /// Cancels any in-progress movement behavior for the survivor, if one exists, and releases whatever animation/agent state that movement left behind, not just the timer.
    /// Explicitly disables the NavMeshAgent (not just clearing its path), since an enabled agent otherwise keeps constraining position to the navmesh and fights hand-built movement.
    /// </summary>
    /// <summary>
    /// One-time, unclamped ground-height correction called right as a walk/follow stops, to fix a bot left visibly hovering mid-way through a clamped step catch-up.
    /// Not used mid-movement, since ApplyMovementStep's own clamp is what makes climbing look gradual.
    /// </summary>
    // Caps the correction distance, since anything larger likely means the ground probe found the wrong surface entirely (e.g. outdoor terrain below an elevated base floor) rather than a legitimate small settle correction.
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
    /// Full clean-slate wipe, exposed as /lr.debug.despawnall. Kills every survivor's BasePlayer and erases the persistent Character records themselves (CharacterManager, SurvivorManager, and the world.json roster).
    /// Does not reset the BotId counter, to avoid reopening the collision risk with Rust's native NPCs that the counter's starting value avoids.
    /// Scoped to only this plugin's tracked SurvivorManager entries, plus a narrow re-check of each BotId against the live world in case survivor.Player is stale.
    /// </summary>
    private int DespawnAllBots()
    {
        if (_engine == null)
        {
            return 0;
        }

        // Snapshots first, since SurvivorManager.GetAll() returns a live view that would throw if mutated while iterating.
        var survivors = new List<Survivor>(_engine.SurvivorManager.GetAll());

        // Untracks every survivor before killing any of them, so Die() below can't trigger OnPlayerDeath's respawn logic for a Character this command is deleting.
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
                // Resets blueprint unlocks too, since that native Rust subsystem is keyed by userID and persists independently of Character/Survivor tracking.
                try
                {
                    ResetPlayerBlueprints(bot.blueprints);
                }
                catch (Exception ex)
                {
                    Puts($"despawnall: WARNING - failed to reset blueprints for '{survivor.Character.Alias}' ({ex.Message}).");
                }

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

            // Re-checks this BotId against the live world regardless of what happened above, in case survivor.Player was stale and a different live entity actually holds this userID.
            BasePlayer existing = FindExistingBotEntity(survivor.Character.BotId);

            if (existing != null && existing != bot && !existing.IsDestroyed)
            {
                orphansFound++;

                // Diagnostic distinguishing whether survivor.Player was null, destroyed, or a genuine second live entity for the same BotId, plus detail on the found entity to rule out a coincidentally-numbered vanilla NPC.
                string botState = bot == null ? "null" : bot.IsDestroyed ? $"destroyed (instanceID {bot.GetInstanceID()})" : $"alive (instanceID {bot.GetInstanceID()})";
                Puts($"despawnall: '{survivor.Character.Alias}' (userID {survivor.Character.BotId}) had a live entity survivor.Player didn't reference - killing it too. [diagnostic: survivor.Player was {botState}, live entity found was instanceID {existing.GetInstanceID()}, ShortPrefabName='{existing.ShortPrefabName}', IsNpc={existing.IsNpc}, displayName='{existing.displayName}', IsConnected={existing.IsConnected}, position={existing.transform.position}]");

                KillBotEntity(existing);
            }
        }

        // This command only touches entities provably owned by this plugin's own tracked Characters, to avoid ever affecting entities it didn't create (such as vanilla NPCs sharing a numeric range).

        // Destroys sleeping bags left behind by wiped bots, scoped strictly to OwnerID values matching this wipe's own BotIds.
        var wipedBotIds = new HashSet<ulong>(survivors.Select(survivor => survivor.Character.BotId));
        int bagsDestroyed = 0;

        // Also destroys any other placed objects (bases, foundations, deployables, locks) owned by a wiped bot, using the same OwnerID scoping.
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

        // Persists immediately rather than waiting for the next save, since this command is meant to be an on-demand clean slate.
        _engine.SaveManager.SaveCharacters(_engine.CharacterManager.GetAllCharacters());

        Puts($"despawnall: wiped {survivors.Count} character(s) - {killed} killed, {alreadyGone} already had no live entity, {orphansFound} tracked-but-stale-reference orphan(s), {bagsDestroyed} placed sleeping bag(s) destroyed, {placedObjectsDestroyed} other placed object(s) destroyed (bases/deployables/locks).");

        return survivors.Count;
    }

    /// <summary>
    /// Forces a bot's BasePlayer to actually leave the world. Kill() alone leaves it frozen, invincible, and still solid; Die() properly ragdolls it and lets it despawn.
    /// Wakes a sleeping bot first, since a sleeping player is in a reduced-processing state. Kill() afterward forces immediate removal instead of waiting on the corpse-decay timer. Returns whether the entity was actually destroyed.
    /// </summary>
    private bool KillBotEntity(BasePlayer bot)
    {
        // Refuses to touch a real vanilla Rust NPC, since stripping and forcing Die()/Kill() on one bypasses its own AI death pipeline and can leave a stuck, undespawnable remnant.
        if (bot.IsNpc)
        {
            Puts($"despawnall: WARNING - refusing to touch '{bot.displayName}' (userID {bot.userID}, instanceID {bot.GetInstanceID()}) - this is a real vanilla Rust NPC (IsNpc:True), not one of this plugin's own bots.");
            return false;
        }

        if (bot.IsSleeping())
        {
            bot.EndSleeping();
        }

        // Strips the inventory before Die(), since this is a debug/test wipe rather than a real combat death - an empty corpse is automatically excluded from future loot searches instead of leaving clutter behind.
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
    /// Finds the Survivor wrapping a specific BasePlayer, or null if it isn't one of ours, for hooks that receive a raw BasePlayer and need to know which LivingRust survivor it corresponds to.
    /// </summary>
    // Caches Survivor by userID for hot per-tick callers, since FindSurvivorByPlayer is a linear scan. Rebuilt fresh on every plugin reload.
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
