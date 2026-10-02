using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Handles card-reader puzzle solving: a survivor carrying the matching keycard(s) and a fuse
/// can solve a monument's puzzle room instead of just avoiding it. It only attempts a puzzle when
/// it already has everything the route needs, and reuses the ghost-route engine to walk it.
/// Military Tunnel is a special case needing three keycards across one continuous route.
/// Interactions with fuseboxes, switches, and card readers use the game's real entity API.
/// </summary>
public partial class LivingRust
{
    private enum KeycardTier
    {
        Green,
        Blue,
        Red,
    }

    private static readonly Dictionary<KeycardTier, string> KeycardShortnames = new()
    {
        [KeycardTier.Green] = "keycard_green",
        [KeycardTier.Blue] = "keycard_blue",
        [KeycardTier.Red] = "keycard_red",
    };

    /// <summary>
    /// Maps a card reader's access level to a keycard tier. The mapping is 1-indexed.
    /// </summary>
    private static readonly Dictionary<int, KeycardTier> KeycardTierByReaderAccessLevel = new()
    {
        [1] = KeycardTier.Green,
        [2] = KeycardTier.Blue,
        [3] = KeycardTier.Red,
    };

    /// <summary>
    /// Checks whether a survivor has a keycard of the given tier with remaining durability.
    /// </summary>
    private static bool HasUsableKeycard(BasePlayer npc, KeycardTier tier)
    {
        Item keycard = npc.inventory.FindItemByItemName(KeycardShortnames[tier]);
        return keycard != null && keycard.condition > 0f;
    }

    /// <summary>
    /// The item shortnames for the two fuse types. Either type satisfies any puzzle's fuse requirement.
    /// </summary>
    private static readonly string[] CardPuzzleFuseShortnames = { "fuse", "fuse.highgrade" };

    /// <summary>
    /// Maps a monument name substring to its puzzle-route folder, the keycard tier(s) the route
    /// needs, and how many fuses are required. Uses the same substring-match convention as
    /// MonumentGhostRouteFolders.
    /// </summary>
    private static readonly Dictionary<string, (string Folder, KeycardTier[] RequiredTiers, int RequiredFuseCount)> CardPuzzleRouteFolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["harbor_2"] = ("Cardreader_A", new[] { KeycardTier.Green }, 1),

            // Harbor 1, a distinct monument from harbor_2/ferry_terminal_1. Green tier.
            ["harbor_1"] = ("Cardreader_B", new[] { KeycardTier.Green }, 1),

            // Satellite Dish, green tier.
            ["satellite_dish"] = ("Cardreader_C", new[] { KeycardTier.Green }, 1),

            // Radtown (roadside monument, distinct from radtown_small/Sewer Branch), green tier.
            ["radtown_1"] = ("Cardreader_D", new[] { KeycardTier.Green }, 1),

            // Military Tunnel: one continuous route through three gated doors, needing all three
            // keycard tiers simultaneously. Reuses the normal loot route folder for this monument.
            ["military_tunnel"] = ("MilitaryTunnel_A", new[] { KeycardTier.Green, KeycardTier.Blue, KeycardTier.Red }, 1),

            // Sphere Tank ("Dome"), green tier.
            ["sphere_tank"] = ("Cardreader_F", new[] { KeycardTier.Green }, 1),

            // Ferry Terminal, distinct from harbor_1/harbor_2, green tier.
            ["ferry_terminal"] = ("Cardreader_G", new[] { KeycardTier.Green }, 1),

            // Powerplant, Tier 2/Medium monument. Green gates the generator room, blue is the reader.
            ["powerplant"] = ("Cardreader_H", new[] { KeycardTier.Green, KeycardTier.Blue }, 1),

            // Sewer Branch (prefab radtown_small), Tier 1 monument, green tier.
            ["radtown_small"] = ("Cardreader_I", new[] { KeycardTier.Green }, 1),

            // Arctic Research Base, Tier 2/Medium monument. Blue keycard only, no fuse required.
            ["arctic_research_base"] = ("Cardreader_J", new[] { KeycardTier.Blue }, 0),

            // Airfield, Tier 2/Medium monument. Needs green + blue and 2 fuses of any type.
            ["airfield"] = ("Cardreader_K", new[] { KeycardTier.Green, KeycardTier.Blue }, 2),

            // Water Treatment Plant, Tier 2/Medium monument. Blue keycard + 1 fuse, no green.
            // Includes a WheelSwitch-driven roller door.
            ["water_treatment_plant"] = ("Cardreader_L", new[] { KeycardTier.Blue }, 1),

            // Nuclear Missile Silo, Tier 3/High monument. Red keycard only, no fuse. Puzzle and
            // loot share one combined route, reusing the normal loot route folder.
            ["nuclear_missile_silo"] = ("NuclearMissileSilo_A", new[] { KeycardTier.Red }, 0),

            // Launch Site, Tier 3/High monument. Green + Red keycards, 2 fuses. Puzzle and loot
            // share one combined route, reusing the normal loot route folder.
            ["launch_site"] = ("LaunchSite_A", new[] { KeycardTier.Green, KeycardTier.Red }, 2),
        };

    /// <summary>
    /// Maximum distance for a survivor to interact with a fusebox, switch, or reader.
    /// </summary>
    private const float CardPuzzleInteractionRadius = 3f;

    /// <summary>
    /// Maximum distance for a wheel switch to accept rotation input, with a small safety margin
    /// below the game's own 2m cutoff.
    /// </summary>
    private const float WheelSwitchMaxRotateDistance2D = 1.8f;

    /// <summary>
    /// Continuous hold duration that guarantees a wheel-switch door fully opens. Used alongside
    /// the door's own open-progress value as a completion signal.
    /// </summary>
    private const float WheelHoldCompletionSeconds = 8f;

    /// <summary>
    /// Tracks when each wheel's current unbroken hold began, keyed by wheel id. Reset whenever a
    /// hold is interrupted, so a resumed hold starts counting from zero.
    /// </summary>
    private readonly Dictionary<NetworkableId, float> _wheelHoldStartTimes = new();

    /// <summary>
    /// Tracks which survivors currently have an active card-puzzle detour running. Used to gate
    /// fusebox/switch/reader interactions so they only happen during a committed puzzle attempt.
    /// </summary>
    private readonly HashSet<Guid> _activeCardPuzzleSurvivors = new();

    /// <summary>
    /// Tracks which card readers a survivor has already swiped (or failed) during the current
    /// puzzle attempt, to avoid re-attempting the same reader on nearby waypoints.
    /// </summary>
    private readonly Dictionary<Guid, HashSet<NetworkableId>> _handledReadersThisPuzzle = new();

    /// <summary>
    /// Tracks which keycard tiers a survivor's active puzzle attempt actually requires, so a
    /// reader is only swiped with the card tier this specific route needs.
    /// </summary>
    private readonly Dictionary<Guid, KeycardTier[]> _activePuzzleRequiredTiers = new();

    /// <summary>
    /// Tracks which wheel switches a survivor has already given a full hold to, so a wheel is
    /// never re-attempted once completed even if the door later decays back down.
    /// </summary>
    private readonly Dictionary<Guid, HashSet<NetworkableId>> _handledWheelsThisPuzzle = new();

    /// <summary>
    /// Tracks which wheel switches currently have an active turn timer running, keyed globally
    /// by wheel id, to avoid starting a second overlapping timer for the same wheel.
    /// </summary>
    private readonly Dictionary<NetworkableId, Timer> _activeWheelTurnTimers = new();

    /// <summary>
    /// Tracks which elevator and elevator-lift entities have already been triggered on this
    /// ghost route, per survivor. The call panel and the cabin are tracked independently since
    /// each needs its own single trigger.
    /// </summary>
    private readonly Dictionary<Guid, HashSet<NetworkableId>> _handledElevatorsThisRoute = new();

    /// <summary>
    /// Same substring-match lookup as TryGetGhostRouteForMonument, against CardPuzzleRouteFolders.
    /// </summary>
    private static bool TryGetCardPuzzleRoute(string monumentName, out string folder, out KeycardTier[] requiredTiers, out int requiredFuseCount)
    {
        folder = null;
        requiredTiers = null;
        requiredFuseCount = 1;

        if (string.IsNullOrEmpty(monumentName))
        {
            return false;
        }

        foreach (KeyValuePair<string, (string Folder, KeycardTier[] RequiredTiers, int RequiredFuseCount)> entry in CardPuzzleRouteFolders)
        {
            if (monumentName.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            folder = entry.Value.Folder;
            requiredTiers = entry.Value.RequiredTiers;
            requiredFuseCount = entry.Value.RequiredFuseCount;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Same random-pick-from-folder pattern as TryGetGhostRouteForMonument, using a separate
    /// folder tree for card-puzzle routes since they're a distinct opportunistic detour.
    /// </summary>
    private bool TryGetCardPuzzleRouteFile(string folder, out string traceFilePath)
    {
        traceFilePath = null;

        string folderPath = $"{TraceDirectory}/{folder}";

        if (!Directory.Exists(folderPath))
        {
            return false;
        }

        string[] candidates = Directory.GetFiles(folderPath, "*.csv");

        if (candidates.Length == 0)
        {
            return false;
        }

        string chosen = candidates[UnityEngine.Random.Range(0, candidates.Length)];
        traceFilePath = chosen.Replace('\\', '/');
        return true;
    }

    /// <summary>
    /// Searches every waypoint along a puzzle route for an electric switch, used for the entry
    /// gate check. Returns the first switch found, since any one being on means the puzzle was
    /// likely already solved recently.
    /// </summary>
    private static bool TryFindCardPuzzleSwitch(List<GhostRouteWaypoint> waypoints, out ElectricSwitch found)
    {
        foreach (GhostRouteWaypoint waypoint in waypoints)
        {
            Collider[] hits = Physics.OverlapSphere(waypoint.Position, CardPuzzleInteractionRadius, CardReaderLayerMask, QueryTriggerInteraction.Collide);

            foreach (Collider hit in hits)
            {
                ElectricSwitch candidate = hit.GetComponentInParent<ElectricSwitch>();

                if (candidate != null && !candidate.IsDestroyed)
                {
                    found = candidate;
                    return true;
                }
            }
        }

        found = null;
        return false;
    }

    /// <summary>
    /// Test-only shortcut that teleports a survivor straight to a puzzle route's first waypoint,
    /// skipping the normal approach walk. The entry gate and interaction logic still run as normal.
    /// </summary>
    private bool TryTeleportToCardPuzzle(Survivor survivor, MonumentInfo monument, out string failureReason)
    {
        BasePlayer npc = survivor.Player;
        failureReason = null;

        if (npc == null || npc.IsDestroyed)
        {
            failureReason = "no live BasePlayer.";
            return false;
        }

        if (!TryGetCardPuzzleRoute(monument.name, out string folder, out KeycardTier[] requiredTiers, out int requiredFuseCount))
        {
            failureReason = $"'{monument.name}' has no registered card-puzzle route.";
            return false;
        }

        if (!TryGetCardPuzzleRouteFile(folder, out string traceFilePath)
            || !TryLoadTraceWaypoints(traceFilePath, out List<GhostRouteWaypoint> puzzleWaypoints, out MonumentInfo recordedAtMonument, monument.name)
            || puzzleWaypoints.Count == 0)
        {
            failureReason = $"couldn't load a route from '{folder}' - does it have any .csv traces yet?";
            return false;
        }

        List<GhostRouteWaypoint> worldWaypoints = recordedAtMonument != null
            ? ProjectGhostRouteToMonument(puzzleWaypoints, monument)
            : puzzleWaypoints;

        if (TryFindCardPuzzleSwitch(worldWaypoints, out ElectricSwitch existingSwitch) && existingSwitch.IsOn())
        {
            failureReason = "the real puzzle switch is already on - already solved recently. Flip it off yourself (or find an unsolved instance) to test a fresh attempt.";
            return false;
        }

        Guid characterId = survivor.Character.Id;
        Vector3 startPosition = worldWaypoints[0].Position;

        npc.transform.position = startPosition;
        npc.MovePosition(startPosition);
        npc.SendNetworkUpdateImmediate();

        _activeCardPuzzleSurvivors.Add(characterId);
        _handledReadersThisPuzzle[characterId] = new HashSet<NetworkableId>();
        _handledWheelsThisPuzzle[characterId] = new HashSet<NetworkableId>();
        _activePuzzleRequiredTiers[characterId] = requiredTiers;

        Action onPuzzleComplete = () =>
        {
            _activeCardPuzzleSurvivors.Remove(characterId);
            _handledReadersThisPuzzle.Remove(characterId);
            _handledWheelsThisPuzzle.Remove(characterId);
            _activePuzzleRequiredTiers.Remove(characterId);
            survivor.Character.CurrentTask = TaskType.None;
        };

        _pendingGhostRouteToResume[characterId] = (worldWaypoints, 0, onPuzzleComplete);
        StartGhostRouteLootScan(survivor, onPuzzleComplete);
        StartGhostRoute(survivor, worldWaypoints, 0, onPuzzleComplete);

        return true;
    }

    /// <summary>
    /// Opportunistic card-puzzle detour, checked right before the normal per-monument ghost
    /// route. Returns true and takes over entirely once it commits. Capped at one attempt per
    /// monument per task so it can't immediately re-trigger in a loop.
    /// </summary>
    private static int GetPuzzleMinGearScore(MonumentTier tier)
    {
        return tier switch
        {
            MonumentTier.TierZero => 0,
            MonumentTier.TierOne => LowTierBandMin,
            MonumentTier.TierTwo => MediumTierBandMin,
            _ => HighTierBandMin,
        };
    }

    private bool TryStartCardPuzzleDetour(Survivor survivor, MonumentInfo monument, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed
            || state.CardPuzzleVisitedMonuments.Contains(monument.name)
            || !TryGetCardPuzzleRoute(monument.name, out string folder, out KeycardTier[] requiredTiers, out int requiredFuseCount))
        {
            return false;
        }

        // The gear-score gate applies only here: a survivor may walk any monument's loot path at
        // any gear score, but the puzzle rooms have scientists, so the puzzle itself requires the
        // gear band matching the monument's tier.
        if (GetGearScore(npc) < GetPuzzleMinGearScore(GetMonumentTier(monument.name)))
        {
            return false;
        }

        // Opportunistic only: never sends a survivor hunting for a card or fuse it doesn't
        // already have. Requires every keycard tier the route needs. Fuse count is summed by
        // amount rather than item count, since fuses are stackable.
        if (requiredFuseCount > 0)
        {
            int heldFuseCount = npc.inventory.containerMain.itemList
                .Concat(npc.inventory.containerBelt.itemList)
                .Where(item => Array.IndexOf(CardPuzzleFuseShortnames, item.info.shortname) >= 0)
                .Sum(item => item.amount);

            if (heldFuseCount < requiredFuseCount)
            {
                return false;
            }
        }

        foreach (KeycardTier tier in requiredTiers)
        {
            Item keycard = npc.inventory.FindItemByItemName(KeycardShortnames[tier]);

            if (keycard == null || keycard.condition <= 0f)
            {
                return false;
            }
        }

        if (!TryGetCardPuzzleRouteFile(folder, out string traceFilePath)
            || !TryLoadTraceWaypoints(traceFilePath, out List<GhostRouteWaypoint> puzzleWaypoints, out MonumentInfo recordedAtMonument, monument.name)
            || puzzleWaypoints.Count == 0)
        {
            return false;
        }

        List<GhostRouteWaypoint> worldWaypoints = recordedAtMonument != null
            ? ProjectGhostRouteToMonument(puzzleWaypoints, monument)
            : puzzleWaypoints;

        state.CardPuzzleVisitedMonuments.Add(monument.name);

        // Entry gate: if the puzzle switch is already on, the puzzle has probably already been
        // solved recently, so skip it rather than waste limited keycard uses.
        if (TryFindCardPuzzleSwitch(worldWaypoints, out ElectricSwitch existingSwitch) && existingSwitch.IsOn())
        {
            VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' found '{monument.name}''s puzzle switch already on - already solved recently, skipping.");
            return false;
        }

        // Sets the same committed-monument state the main ghost-route commit block sets, since
        // this function returns before reaching the normal zone-scan code that would set it.
        state.CommittedMonumentName = monument.name;
        state.CommittedMonumentDeadline = UnityEngine.Time.realtimeSinceStartup + GetMonumentDwellSeconds(monument.name);

        Puts($"card-puzzle: '{survivor.Character.Alias}' is heading to solve '{monument.name}''s card puzzle via '{traceFilePath}'.");

        Guid characterId = survivor.Character.Id;
        _activeCardPuzzleSurvivors.Add(characterId);
        _handledReadersThisPuzzle[characterId] = new HashSet<NetworkableId>();
        _handledWheelsThisPuzzle[characterId] = new HashSet<NetworkableId>();
        _activePuzzleRequiredTiers[characterId] = requiredTiers;

        Action onPuzzleComplete = () =>
        {
            state.CardPuzzleCompletedMonuments.Add(monument.name);
            _activeCardPuzzleSurvivors.Remove(characterId);
            _handledReadersThisPuzzle.Remove(characterId);
            _handledWheelsThisPuzzle.Remove(characterId);
            _activePuzzleRequiredTiers.Remove(characterId);
            ContinueLootTask(survivor, state);
        };

        // Same pre-registration-before-the-approach-walk pattern the normal
        // ghost route commit block already uses (LivingRust.MonumentLootZones.cs) -
        // combat interrupting the approach itself, before StartGhostRoute
        // ever runs once, still has something real to resume afterward.
        _pendingGhostRouteToResume[characterId] = (worldWaypoints, 0, onPuzzleComplete);
        StartGhostRouteLootScan(survivor, onPuzzleComplete);

        StartLongDistanceWalk(
            survivor,
            worldWaypoints[0].Position,
            monument.name,
            onArrived: () => StartGhostRoute(survivor, worldWaypoints, 0, onPuzzleComplete),
            onFailed: () =>
            {
                VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' couldn't reach the puzzle route's start near '{monument.name}' - falling back to normal looting.");
                _pendingGhostRouteToResume.Remove(characterId);
                StopGhostRouteLootScan(characterId);
                _activeCardPuzzleSurvivors.Remove(characterId);
                _handledReadersThisPuzzle.Remove(characterId);
                _handledWheelsThisPuzzle.Remove(characterId);
                _activePuzzleRequiredTiers.Remove(characterId);
                ContinueLootTask(survivor, state);
            });

        return true;
    }

    /// <summary>
    /// Handles fuse/switch/reader interactions during a ghost route. Only acts when the survivor
    /// has an active card-puzzle detour, so it is a no-op on normal loot routes. Does not cover
    /// WheelSwitch/PressButton, which are handled separately and unconditionally elsewhere.
    /// </summary>
    private void TryHandleCardPuzzleInteractions(Survivor survivor, BasePlayer npc)
    {
        Guid characterId = survivor.Character.Id;

        if (!_activeCardPuzzleSurvivors.Contains(characterId))
        {
            return;
        }

        Collider[] hits = Physics.OverlapSphere(npc.transform.position, CardPuzzleInteractionRadius, CardReaderLayerMask, QueryTriggerInteraction.Collide);

        // Fusebox/switch/reader props each expose several child colliders resolving to the same
        // entity, so results are deduplicated per entity to avoid double-handling.
        //
        // Switches are deduped by instance (HashSet<ElectricSwitch>) rather than a single
        // variable, since some monuments have more than one switch within interaction range.
        ItemBasedFlowRestrictor fusebox = null;
        CardReader reader = null;
        HashSet<ElectricSwitch> switches = new();

        // TimerSwitch is a separate class from ElectricSwitch (a spring-loaded/timed "activate"
        // prop with its own countdown), so it needs its own dedup set.
        HashSet<TimerSwitch> timerSwitches = new();

        foreach (Collider hit in hits)
        {
            fusebox ??= hit.GetComponentInParent<ItemBasedFlowRestrictor>();
            reader ??= hit.GetComponentInParent<CardReader>();

            ElectricSwitch hitSwitch = hit.GetComponentInParent<ElectricSwitch>();

            if (hitSwitch != null)
            {
                switches.Add(hitSwitch);
            }

            TimerSwitch hitTimerSwitch = hit.GetComponentInParent<TimerSwitch>();

            if (hitTimerSwitch != null)
            {
                timerSwitches.Add(hitTimerSwitch);
            }
        }

        if (fusebox != null && !fusebox.IsDestroyed && !fusebox.HasPassthroughItem())
        {
            TryInsertFuse(survivor, npc, fusebox);
        }

        foreach (ElectricSwitch electricSwitch in switches)
        {
            if (!electricSwitch.IsDestroyed && !electricSwitch.IsOn())
            {
                electricSwitch.SetSwitch(true);
                VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' flipped a switch.");
            }
        }

        foreach (TimerSwitch timerSwitch in timerSwitches)
        {
            // SwitchPressed checks IsPowered() itself before starting the countdown, so calling
            // it before the upstream circuit is live is a safe no-op.
            if (!timerSwitch.IsDestroyed && !timerSwitch.IsOn())
            {
                timerSwitch.SwitchPressed();
                VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' pressed a timed activate switch.");
            }
        }

        if (reader != null && !reader.IsDestroyed
            && _handledReadersThisPuzzle.TryGetValue(characterId, out HashSet<NetworkableId> handledReaders)
            && handledReaders.Add(reader.net.ID))
        {
            // Add() both records this reader as handled and reports whether it was already
            // handled, so each distinct reader on a multi-reader route is attempted only once.
            TrySwipeCard(survivor, npc, reader);
        }
    }

    /// <summary>
    /// Handles WheelSwitch and PressButton interactions unconditionally on every ghost route,
    /// not just puzzle-committed ones, since these cost nothing and some monuments need them to
    /// simply pass through. Excludes fusebox/switch/reader interactions, which stay puzzle-gated
    /// since they consume durability. Also handles elevators. Returns true if anything handled
    /// here is still pending, so the caller can wait before advancing.
    /// </summary>
    private bool TryHandleFreeMonumentInteractions(Survivor survivor, BasePlayer npc)
    {
        Collider[] hits = Physics.OverlapSphere(npc.transform.position, CardPuzzleInteractionRadius, CardReaderLayerMask, QueryTriggerInteraction.Collide);

        HashSet<WheelSwitch> wheelSwitches = new();
        HashSet<PressButton> pressButtons = new();
        HashSet<Elevator> elevators = new();
        HashSet<ElevatorLift> elevatorLifts = new();

        foreach (Collider hit in hits)
        {
            WheelSwitch hitWheelSwitch = hit.GetComponentInParent<WheelSwitch>();

            if (hitWheelSwitch != null)
            {
                wheelSwitches.Add(hitWheelSwitch);
            }

            PressButton hitPressButton = hit.GetComponentInParent<PressButton>();

            if (hitPressButton != null)
            {
                pressButtons.Add(hitPressButton);
            }

            // Elevator is the per-floor call point, ElevatorLift is the moving cabin itself -
            // two distinct entity classes, so both need their own lookup.
            Elevator hitElevator = hit.GetComponentInParent<Elevator>();

            if (hitElevator != null)
            {
                elevators.Add(hitElevator);
            }

            ElevatorLift hitElevatorLift = hit.GetComponentInParent<ElevatorLift>();

            if (hitElevatorLift != null)
            {
                elevatorLifts.Add(hitElevatorLift);
            }
        }

        foreach (PressButton pressButton in pressButtons)
        {
            // Momentary, not held: IsOn() naturally flips back to false once pressDuration
            // elapses, so no "already handled" tracking is needed like the wheel switch has.
            if (!pressButton.IsDestroyed && !pressButton.IsOn())
            {
                pressButton.Press();
                VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' pressed a button.");
            }
        }

        bool anyPending = false;

        foreach (WheelSwitch wheelSwitch in wheelSwitches)
        {
            if (!wheelSwitch.IsDestroyed)
            {
                ProgressDoor connectedDoor = TryResolveConnectedDoor(wheelSwitch);

                TryStartWheelTurn(survivor, npc, wheelSwitch, connectedDoor);

                // Only pending if a turn attempt is genuinely running right now, not merely
                // because the door isn't open yet - _activeWheelTurnTimers only holds an entry
                // once a turn has actually started.
                if (connectedDoor != null && !connectedDoor.IsDestroyed && connectedDoor.openProgress < 1f
                    && _activeWheelTurnTimers.ContainsKey(wheelSwitch.net.ID))
                {
                    anyPending = true;
                }
            }
        }

        foreach (Elevator elevator in elevators)
        {
            if (!elevator.IsDestroyed && TryCallElevator(survivor, npc, elevator))
            {
                anyPending = true;
            }
        }

        foreach (ElevatorLift elevatorLift in elevatorLifts)
        {
            if (!elevatorLift.IsDestroyed && TryRideElevatorDown(survivor, npc, elevatorLift))
            {
                anyPending = true;
            }
        }

        return anyPending;
    }

    private void TryInsertFuse(Survivor survivor, BasePlayer npc, ItemBasedFlowRestrictor fusebox)
    {
        // Uses direct field access instead of reflection, since Carbon compiles plugins against
        // a publicized reference assembly that exposes private members at compile time.
        ItemContainer fuseboxInventory = fusebox.inventory;

        if (fuseboxInventory == null)
        {
            VerbosePuts($"card-puzzle-diag: '{survivor.Character.Alias}' fusebox.inventory (direct field access) is still null.");
            return;
        }

        Item fuseItem = CardPuzzleFuseShortnames
            .Select(shortname => npc.inventory.FindItemByItemName(shortname))
            .FirstOrDefault(candidate => candidate != null);

        if (fuseItem == null)
        {
            VerbosePuts($"card-puzzle-diag: '{survivor.Character.Alias}' has no fuse/fuse.highgrade in inventory at all.");
            return;
        }

        if (!fusebox.IsValidPassthroughItem(fuseItem))
        {
            VerbosePuts($"card-puzzle-diag: '{survivor.Character.Alias}' holds '{fuseItem.info.shortname}' but IsValidPassthroughItem rejected it (validPassthroughItems mismatch?).");
            return;
        }

        VerbosePuts($"card-puzzle-diag: '{survivor.Character.Alias}' attempting MoveToContainer('{fuseItem.info.shortname}') into fusebox inventory (capacity {fuseboxInventory.capacity}, current count {fuseboxInventory.itemList.Count}).");

        if (fuseItem.MoveToContainer(fuseboxInventory))
        {
            VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' fed a '{fuseItem.info.shortname}' into a fusebox.");
        }
        else
        {
            VerbosePuts($"card-puzzle-diag: '{survivor.Character.Alias}' MoveToContainer returned false - fusebox rejected the fuse.");
        }
    }

    /// <summary>
    /// Resolves whichever ProgressDoor a wheel switch's IO output wiring actually connects to,
    /// by walking its output slots. This is more reliable than pairing by proximity, since some
    /// monuments have multiple doors near the same wheel.
    /// </summary>
    private static ProgressDoor TryResolveConnectedDoor(WheelSwitch wheel)
    {
        if (wheel.outputs == null)
        {
            return null;
        }

        foreach (IOEntity.IOSlot output in wheel.outputs)
        {
            IOEntity connected = output.connectedTo?.Get(false);

            if (connected is ProgressDoor door && !door.IsDestroyed)
            {
                return door;
            }
        }

        return null;
    }

    /// <summary>
    /// Drives a wheel switch's real RotateProgress() method repeatedly to simulate a player
    /// holding the interact key, naturally matching whatever duration that wheel is tuned for.
    /// Completion is checked against the connected door's openProgress, not the wheel's own
    /// rotateProgress value, which is an uncapped animation value rather than a 0-1 signal.
    /// The hold must be continuous, since the door decays back down once released.
    /// Idempotent both within a single call and across repeat visits: once a survivor completes
    /// a full hold on a wheel, that wheel is marked done for the rest of the puzzle attempt and
    /// never restarted, so the route can move on regardless of the door's later state.
    /// </summary>
    private void TryStartWheelTurn(Survivor survivor, BasePlayer npc, WheelSwitch wheel, ProgressDoor door)
    {
        Guid characterId = survivor.Character.Id;
        bool alreadyHandled = _handledWheelsThisPuzzle.TryGetValue(characterId, out HashSet<NetworkableId> handledWheels)
            && handledWheels.Contains(wheel.net.ID);

        bool alreadyOpen = door != null && !door.IsDestroyed && door.openProgress >= 1f;

        if (alreadyHandled || alreadyOpen || _activeWheelTurnTimers.ContainsKey(wheel.net.ID))
        {
            return;
        }

        if (door == null)
        {
            // No door found for this wheel, so there's no way to detect completion; refuse to
            // start rather than pumping energy forever with no stop condition.
            VerbosePuts($"card-puzzle-diag: '{survivor.Character.Alias}' found a wheel but no nearby ProgressDoor to pair it with - not starting (no way to detect completion).");
            return;
        }

        // Checks distance to the wheel before committing to start, since the interaction radius
        // is wider than the wheel's own real rotate range. Refusing to start when too far avoids
        // burning an attempt that would immediately self-cancel.
        float startDistance2D = Vector2.Distance(
            new Vector2(npc.transform.position.x, npc.transform.position.z),
            new Vector2(wheel.transform.position.x, wheel.transform.position.z));

        if (startDistance2D > WheelSwitchMaxRotateDistance2D)
        {
            VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' is {startDistance2D:F2}m from a wheel (real turn range is ~2m) - not starting yet.");
            return;
        }

        wheel.rotatorPlayer = npc;

        VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' started turning a wheel (tick every {wheel.progressTickRate:F2}s, door openProgress={door.openProgress:F2}, distance2D={startDistance2D:F2}m).");

        NetworkableId wheelId = wheel.net.ID;
        Timer turnTimer = null;
        _wheelHoldStartTimes[wheelId] = UnityEngine.Time.realtimeSinceStartup;

        turnTimer = timer.Every(Mathf.Max(wheel.progressTickRate, 0.05f), () =>
        {
            if (npc == null || npc.IsDestroyed || wheel == null || wheel.IsDestroyed || door.IsDestroyed)
            {
                turnTimer.Destroy();
                _activeWheelTurnTimers.Remove(wheelId);
                _wheelHoldStartTimes.Remove(wheelId);
                return;
            }

            wheel.RotateProgress();

            float heldSeconds = UnityEngine.Time.realtimeSinceStartup - _wheelHoldStartTimes[wheelId];

            if (door.openProgress >= 1f || heldSeconds >= WheelHoldCompletionSeconds)
            {
                VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' finished turning the wheel - door openProgress={door.openProgress:F2}, held {heldSeconds:F1}s - moving on regardless of what the door does next.");
                turnTimer.Destroy();
                _activeWheelTurnTimers.Remove(wheelId);
                _wheelHoldStartTimes.Remove(wheelId);

                // Lazily creates the set if missing, since a normal loot-route survivor never
                // goes through the puzzle-detour paths that would otherwise pre-create it.
                if (!_handledWheelsThisPuzzle.TryGetValue(characterId, out HashSet<NetworkableId> completedWheels))
                {
                    completedWheels = new HashSet<NetworkableId>();
                    _handledWheelsThisPuzzle[characterId] = completedWheels;
                }

                completedWheels.Add(wheelId);

                return;
            }

            if (wheel.rotatorPlayer != npc)
            {
                float nowDistance2D = Vector2.Distance(
                    new Vector2(npc.transform.position.x, npc.transform.position.z),
                    new Vector2(wheel.transform.position.x, wheel.transform.position.z));

                VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' stopped turning the wheel early (real RotateProgress cancelled it - too far, dead, or asleep - distance2D now {nowDistance2D:F2}m, held {heldSeconds:F1}s).");
                turnTimer.Destroy();
                _wheelHoldStartTimes.Remove(wheelId);
                _activeWheelTurnTimers.Remove(wheelId);
            }
        });

        _activeWheelTurnTimers[wheelId] = turnTimer;
    }

    /// <summary>
    /// Resolves the real "mover" Elevator entity for a given per-floor call point. An
    /// ElevatorStatic redirects to its ownerElevator when set; a plain Elevator is its own mover.
    /// </summary>
    private static Elevator TryResolveElevatorMover(Elevator elevator)
    {
        if (elevator is ElevatorStatic elevatorStatic && elevatorStatic.ownerElevator != null)
        {
            return elevatorStatic.ownerElevator;
        }

        return elevator;
    }

    /// <summary>
    /// Calls the elevator to a given floor's call panel, calling the underlying elevator API
    /// directly rather than going through the game's RPC message. Guarded by
    /// _handledElevatorsThisRoute so repeat waypoints don't keep re-issuing the request while
    /// already travelling or arrived.
    /// </summary>
    private bool TryCallElevator(Survivor survivor, BasePlayer npc, Elevator elevator)
    {
        Guid characterId = survivor.Character.Id;
        Elevator mover = TryResolveElevatorMover(elevator);

        if (!_handledElevatorsThisRoute.TryGetValue(characterId, out HashSet<NetworkableId> handledElevators))
        {
            handledElevators = new HashSet<NetworkableId>();
            _handledElevatorsThisRoute[characterId] = handledElevators;
        }

        if (handledElevators.Add(elevator.net.ID))
        {
            mover.Server_RaiseLowerElevator(elevator.Floor, false, out _);
            VerbosePuts($"elevator: '{survivor.Character.Alias}' called the elevator to floor {elevator.Floor}.");
        }

        return mover.IsBusy();
    }

    /// <summary>
    /// Presses the cabin's down button once boarded, a separate detection path from
    /// TryCallElevator since it targets the moving cabin entity rather than the call point.
    /// Always moves down, matching every recorded route so far.
    /// </summary>
    private bool TryRideElevatorDown(Survivor survivor, BasePlayer npc, ElevatorLift elevatorLift)
    {
        Elevator mover = elevatorLift.ownerElevator.Get(false);

        if (mover == null)
        {
            return false;
        }

        Guid characterId = survivor.Character.Id;

        if (!_handledElevatorsThisRoute.TryGetValue(characterId, out HashSet<NetworkableId> handledElevators))
        {
            handledElevators = new HashSet<NetworkableId>();
            _handledElevatorsThisRoute[characterId] = handledElevators;
        }

        if (handledElevators.Add(elevatorLift.net.ID))
        {
            mover.Server_RaiseLowerElevator(-1, true, out _);
            VerbosePuts($"elevator: '{survivor.Character.Alias}' pressed the down button inside the elevator.");
        }

        return mover.IsBusy();
    }

    [ChatCommand("lr.debug.inspectfusebox")]
    private void CmdDebugInspectFusebox(BasePlayer player, string command, string[] args)
    {
        RunDebugInspectFusebox(player);
    }

    [ConsoleCommand("lr.debug.inspectfusebox")]
    private void CmdDebugInspectFuseboxConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugInspectFusebox(player);
        }
    }

    /// <summary>
    /// Debug command that reports the live state of the fusebox the admin is looking at, for
    /// diagnosing fuse-insertion issues.
    /// </summary>
    private void RunDebugInspectFusebox(BasePlayer player)
    {
        Vector3 origin = player.eyes.position;
        Vector3 direction = player.eyes.HeadForward();

        RaycastHit[] hits = Physics.RaycastAll(origin, direction, 15f, ~0, QueryTriggerInteraction.Collide);
        ItemBasedFlowRestrictor fusebox = hits
            .OrderBy(h => h.distance)
            .Select(h => h.collider.GetComponentInParent<ItemBasedFlowRestrictor>())
            .FirstOrDefault(f => f != null);

        if (fusebox == null)
        {
            player.ChatMessage("[LivingRust] No fusebox found along your look direction (within 15m).");
            return;
        }

        bool hasPassthroughItem = fusebox.HasPassthroughItem();
        bool gotPassthroughItem = fusebox.GetPassthroughItem(out Item passthroughItem);

        // Uses direct field access, which Carbon's publicized compile-time reference assembly allows.
        ItemContainer directInventory = fusebox.inventory;

        string inventoryFieldReport = directInventory == null
            ? "NULL (direct field access)"
            : $"non-null, {directInventory.itemList.Count}/{directInventory.capacity} slot(s) used: [{string.Join(", ", directInventory.itemList.Select(i => i.info.shortname))}]";

        string report = $"fusebox-diag: at {fusebox.transform.position} - "
            + $"HasPassthroughItem={hasPassthroughItem}, "
            + $"GetPassthroughItem={gotPassthroughItem} (item={(passthroughItem != null ? passthroughItem.info.shortname : "null")}), "
            + $"direct inventory field={inventoryFieldReport}, "
            + $"validPassthroughItems=[{string.Join(", ", fusebox.validPassthroughItems?.Select(d => d.shortname) ?? Enumerable.Empty<string>())}], "
            + $"numSlots={fusebox.numSlots}, allowedContents={fusebox.allowedContents}, IsOn={fusebox.IsOn()}.";

        Puts(report);
        player.ChatMessage($"[LivingRust] {report}");
    }

    [ChatCommand("lr.debug.inspectcardreader")]
    private void CmdDebugInspectCardReader(BasePlayer player, string command, string[] args)
    {
        RunDebugInspectCardReader(player);
    }

    [ConsoleCommand("lr.debug.inspectcardreader")]
    private void CmdDebugInspectCardReaderConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugInspectCardReader(player);
        }
    }

    /// <summary>
    /// Debug command that reports a card reader's accessLevel and its resolved keycard tier.
    /// </summary>
    private void RunDebugInspectCardReader(BasePlayer player)
    {
        Vector3 origin = player.eyes.position;
        Vector3 direction = player.eyes.HeadForward();

        RaycastHit[] hits = Physics.RaycastAll(origin, direction, 15f, ~0, QueryTriggerInteraction.Collide);
        CardReader reader = hits
            .OrderBy(h => h.distance)
            .Select(h => h.collider.GetComponentInParent<CardReader>())
            .FirstOrDefault(r => r != null);

        if (reader == null)
        {
            player.ChatMessage("[LivingRust] No card reader found along your look direction (within 15m).");
            return;
        }

        string resolvedTier = KeycardTierByReaderAccessLevel.TryGetValue(reader.accessLevel, out KeycardTier tier)
            ? tier.ToString()
            : "UNKNOWN - not in KeycardTierByReaderAccessLevel";

        string report = $"cardreader-diag: at {reader.transform.position} - accessLevel={reader.accessLevel}, resolved tier={resolvedTier}, IsOn={reader.IsOn()}.";

        Puts(report);
        player.ChatMessage($"[LivingRust] {report}");
    }

    [ChatCommand("lr.debug.inspectswitch")]
    private void CmdDebugInspectSwitch(BasePlayer player, string command, string[] args)
    {
        RunDebugInspectSwitch(player);
    }

    [ConsoleCommand("lr.debug.inspectswitch")]
    private void CmdDebugInspectSwitchConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugInspectSwitch(player);
        }
    }

    /// <summary>
    /// Debug command that reports every switch, door, button, and elevator entity near the
    /// admin, for diagnosing puzzle-interaction detection issues.
    /// </summary>
    private void RunDebugInspectSwitch(BasePlayer player)
    {
        Collider[] hits = Physics.OverlapSphere(player.transform.position, CardPuzzleInteractionRadius, CardReaderLayerMask, QueryTriggerInteraction.Collide);

        HashSet<ElectricSwitch> switches = new();

        // TimerSwitch, the "ACTIVATE" prop, is unrelated to ElectricSwitch.
        HashSet<TimerSwitch> timerSwitches = new();

        // WheelSwitch, the "TURN" prop, is unrelated to the switch classes above.
        HashSet<WheelSwitch> wheelSwitches = new();

        // ProgressDoor is the real completion signal for a wheel-driven door (openProgress),
        // not WheelSwitch.rotateProgress itself.
        HashSet<ProgressDoor> progressDoors = new();

        // PressButton is the momentary door-release button, unrelated to every class above.
        HashSet<PressButton> pressButtons = new();

        // Elevator is the per-floor call point, ElevatorLift is the moving cabin itself.
        // Neither derives from the other.
        HashSet<Elevator> elevators = new();
        HashSet<ElevatorLift> elevatorLifts = new();

        foreach (Collider hit in hits)
        {
            ElectricSwitch hitSwitch = hit.GetComponentInParent<ElectricSwitch>();

            if (hitSwitch != null)
            {
                switches.Add(hitSwitch);
            }

            TimerSwitch hitTimerSwitch = hit.GetComponentInParent<TimerSwitch>();

            if (hitTimerSwitch != null)
            {
                timerSwitches.Add(hitTimerSwitch);
            }

            WheelSwitch hitWheelSwitch = hit.GetComponentInParent<WheelSwitch>();

            if (hitWheelSwitch != null)
            {
                wheelSwitches.Add(hitWheelSwitch);
            }

            ProgressDoor hitProgressDoor = hit.GetComponentInParent<ProgressDoor>();

            if (hitProgressDoor != null)
            {
                progressDoors.Add(hitProgressDoor);
            }

            PressButton hitPressButton = hit.GetComponentInParent<PressButton>();

            if (hitPressButton != null)
            {
                pressButtons.Add(hitPressButton);
            }

            Elevator hitElevator = hit.GetComponentInParent<Elevator>();

            if (hitElevator != null)
            {
                elevators.Add(hitElevator);
            }

            ElevatorLift hitElevatorLift = hit.GetComponentInParent<ElevatorLift>();

            if (hitElevatorLift != null)
            {
                elevatorLifts.Add(hitElevatorLift);
            }
        }

        if (switches.Count == 0 && timerSwitches.Count == 0 && wheelSwitches.Count == 0 && progressDoors.Count == 0
            && pressButtons.Count == 0 && elevators.Count == 0 && elevatorLifts.Count == 0)
        {
            player.ChatMessage($"[LivingRust] No ElectricSwitch/TimerSwitch/WheelSwitch/ProgressDoor/PressButton/Elevator/ElevatorLift found within {CardPuzzleInteractionRadius}m on the World layer (CardReaderLayerMask).");
        }

        foreach (ElectricSwitch electricSwitch in switches)
        {
            float distance = Vector3.Distance(player.transform.position, electricSwitch.transform.position);
            string report = $"switch-diag: '{electricSwitch.ShortPrefabName}' (net.ID={electricSwitch.net?.ID}) at {electricSwitch.transform.position}, {distance:F2}m away - IsOn={electricSwitch.IsOn()}, IsDestroyed={electricSwitch.IsDestroyed}, layer={LayerMask.LayerToName(electricSwitch.gameObject.layer)}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }

        foreach (TimerSwitch timerSwitch in timerSwitches)
        {
            float distance = Vector3.Distance(player.transform.position, timerSwitch.transform.position);
            string report = $"timerswitch-diag: '{timerSwitch.ShortPrefabName}' (net.ID={timerSwitch.net?.ID}) at {timerSwitch.transform.position}, {distance:F2}m away - IsOn={timerSwitch.IsOn()}, IsDestroyed={timerSwitch.IsDestroyed}, timerLength={timerSwitch.timerLength}, layer={LayerMask.LayerToName(timerSwitch.gameObject.layer)}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }

        foreach (WheelSwitch wheelSwitch in wheelSwitches)
        {
            float distance = Vector3.Distance(player.transform.position, wheelSwitch.transform.position);
            string report = $"wheelswitch-diag: '{wheelSwitch.ShortPrefabName}' (net.ID={wheelSwitch.net?.ID}) at {wheelSwitch.transform.position}, {distance:F2}m away - rotateProgress={wheelSwitch.rotateProgress:F2} (NOT the completion signal - see ProgressDoor below), rotatorPlayer={wheelSwitch.rotatorPlayer?.displayName ?? "null"}, progressTickRate={wheelSwitch.progressTickRate:F2}, requiresPowerToTurn={wheelSwitch.requiresPowerToTurn}, IsDestroyed={wheelSwitch.IsDestroyed}, activeTimer={_activeWheelTurnTimers.ContainsKey(wheelSwitch.net.ID)}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }

        foreach (ProgressDoor progressDoor in progressDoors)
        {
            float distance = Vector3.Distance(player.transform.position, progressDoor.transform.position);
            string report = $"progressdoor-diag: '{progressDoor.ShortPrefabName}' (net.ID={progressDoor.net?.ID}) at {progressDoor.transform.position}, {distance:F2}m away - openProgress={progressDoor.openProgress:F2} (THIS is the real completion signal), storedEnergy={progressDoor.storedEnergy:F2}, energyForOpen={progressDoor.energyForOpen:F2}, IsDestroyed={progressDoor.IsDestroyed}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }

        foreach (PressButton pressButton in pressButtons)
        {
            float distance = Vector3.Distance(player.transform.position, pressButton.transform.position);
            string report = $"pressbutton-diag: '{pressButton.ShortPrefabName}' (net.ID={pressButton.net?.ID}) at {pressButton.transform.position}, {distance:F2}m away - IsOn={pressButton.IsOn()}, pressDuration={pressButton.pressDuration:F2}, IsDestroyed={pressButton.IsDestroyed}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");

            // Wiring dump: walks the button's output slots to report what it's actually wired to.
            if (pressButton.outputs != null)
            {
                foreach (IOEntity.IOSlot output in pressButton.outputs)
                {
                    IOEntity connected = output.connectedTo?.Get(false);
                    string wireReport = connected == null
                        ? $"pressbutton-wiring-diag: '{pressButton.ShortPrefabName}' output slot has no connection."
                        : $"pressbutton-wiring-diag: '{pressButton.ShortPrefabName}' output -> '{connected.ShortPrefabName}' (type={connected.GetType().Name}, net.ID={connected.net?.ID}).";

                    Puts(wireReport);
                    player.ChatMessage($"[LivingRust] {wireReport}");
                }
            }
        }

        foreach (Elevator elevator in elevators)
        {
            float distance = Vector3.Distance(player.transform.position, elevator.transform.position);
            Elevator mover = TryResolveElevatorMover(elevator);
            string report = $"elevator-diag: '{elevator.ShortPrefabName}' (net.ID={elevator.net?.ID}) at {elevator.transform.position}, {distance:F2}m away - Floor={elevator.Floor}, IsStatic={elevator is ElevatorStatic}, mover.net.ID={mover.net?.ID}, mover.IsBusy={mover.IsBusy()}, IsDestroyed={elevator.IsDestroyed}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }

        foreach (ElevatorLift elevatorLift in elevatorLifts)
        {
            float distance = Vector3.Distance(player.transform.position, elevatorLift.transform.position);
            Elevator mover = elevatorLift.ownerElevator.Get(false);
            string report = $"elevatorlift-diag: '{elevatorLift.ShortPrefabName}' (net.ID={elevatorLift.net?.ID}) at {elevatorLift.transform.position}, {distance:F2}m away - ownerElevator.net.ID={mover?.net?.ID.ToString() ?? "null"}, ownerElevator.Floor={(mover != null ? mover.Floor.ToString() : "n/a")}, ownerElevator.IsBusy={(mover != null ? mover.IsBusy().ToString() : "n/a")}, IsDestroyed={elevatorLift.IsDestroyed}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }

        // All-layers, all-colliders diagnostic pass, catches anything the World-layer-only scan
        // above would miss.
        Collider[] rawHits = Physics.OverlapSphere(player.transform.position, CardPuzzleInteractionRadius, ~0, QueryTriggerInteraction.Collide);
        HashSet<BaseEntity> reportedEntities = new();

        foreach (Collider hit in rawHits)
        {
            BaseEntity entity = hit.GetComponentInParent<BaseEntity>();

            if (entity == null || !reportedEntities.Add(entity))
            {
                continue;
            }

            string typeName = entity.GetType().Name;

            // Also matches "elevator"/"lift" to catch a call-panel entity that might sit on a
            // physics layer CardReaderLayerMask doesn't cover.
            bool looksRelevant = typeName.IndexOf("Switch", StringComparison.OrdinalIgnoreCase) >= 0
                || entity.ShortPrefabName.IndexOf("switch", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Elevator", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Lift", StringComparison.OrdinalIgnoreCase) >= 0
                || entity.ShortPrefabName.IndexOf("elevator", StringComparison.OrdinalIgnoreCase) >= 0
                || entity.ShortPrefabName.IndexOf("lift", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!looksRelevant)
            {
                continue;
            }

            float distance = Vector3.Distance(player.transform.position, entity.transform.position);
            string report = $"switch-diag-rawscan: '{entity.ShortPrefabName}' (type={typeName}, net.ID={entity.net?.ID}) at {entity.transform.position}, {distance:F2}m away, collider layer={LayerMask.LayerToName(hit.gameObject.layer)}, entity layer={LayerMask.LayerToName(entity.gameObject.layer)}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }
    }

    /// <summary>
    /// Swipes a keycard at a reader. GrantCard() has no built-in tier validation, so the tier
    /// match is checked here first using reader.accessLevel. Deducts durability per swipe and
    /// removes the card once fully spent.
    /// </summary>
    private void TrySwipeCard(Survivor survivor, BasePlayer npc, CardReader reader)
    {
        if (!KeycardTierByReaderAccessLevel.TryGetValue(reader.accessLevel, out KeycardTier requiredTier))
        {
            VerbosePuts($"card-puzzle-diag: '{survivor.Character.Alias}' found a reader with accessLevel={reader.accessLevel}, not in KeycardTierByReaderAccessLevel - can't tell which card it needs.");
            return;
        }

        // Only swipe if this tier is actually one the puzzle route needs, since some monuments
        // have readers of multiple tiers co-located but only require one of them.
        if (_activePuzzleRequiredTiers.TryGetValue(survivor.Character.Id, out KeycardTier[] requiredTiers)
            && Array.IndexOf(requiredTiers, requiredTier) < 0)
        {
            VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' found a {requiredTier} reader (accessLevel={reader.accessLevel}) but this puzzle doesn't need that tier - not swiping.");
            return;
        }

        Item card = npc.inventory.FindItemByItemName(KeycardShortnames[requiredTier]);

        if (card == null || card.condition <= 0f)
        {
            VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' found a {requiredTier} reader (accessLevel={reader.accessLevel}) but isn't carrying a usable {KeycardShortnames[requiredTier]}.");
            return;
        }

        // Does not make the survivor visibly hold the keycard for the swipe, since rapid held-item
        // swaps are fragile for a connectionless bot. The reader's own swipe effect plays
        // automatically regardless of what the survivor is holding.
        reader.GrantCard();

        card.condition -= 1f;

        bool cardConsumed = card.condition <= 0f;

        if (cardConsumed)
        {
            card.Remove(0f);
        }
        else
        {
            card.MarkDirty();
        }

        VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' swiped a '{KeycardShortnames[requiredTier]}' at a reader (accessLevel={reader.accessLevel}, condition now {Mathf.Max(card.condition, 0f):F0}).");
    }
}
