using LivingRust.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingRust.Models;
using Newtonsoft.Json;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Monument-aware looting: auto-detects loose loot areas within a monument type by clustering
/// StorageContainer positions, rather than hardcoding a fixed enter/finish path. Stored in each
/// monument's local space and keyed by monument type, so a zone scanned once applies to every
/// instance of that monument on any map/wipe. Persisted to disk so it survives a plugin reload
/// without needing to be re-scanned.
/// </summary>
public partial class LivingRust
{
    private sealed class MonumentLootZone
    {
        public Vector3 LocalOffset;
        public float Radius;
        public int ContainerCountAtScan;
    }

    private const string MonumentLootZoneSaveFile = "LivingRust/monument_loot_zones.json";

    // How close two containers need to be to count as the same cluster during the auto-detect
    // scan, generous enough to merge a room's containers into one zone.
    private const float LootZoneClusterDistance = 12f;

    // Hard cap on how far apart two containers in the same cluster can be vertically, so a cluster
    // can't span a floor-to-roof gap.
    private const float LootZoneMaxVerticalSpan = 3f;

    // A cluster with fewer containers than this is treated as noise rather than a real loot area.
    private const int LootZoneMinClusterSize = 2;

    // Extra margin added on top of a cluster's container spread when computing its stored radius.
    private const float LootZoneRadiusMargin = 4f;

    // Fallback scan radius for monuments whose Bounds field is degenerate/zero.
    private const float MonumentLootScanFallbackRadius = 80f;

    // Spreads each bot's walk target across the zone's own footprint instead of one shared point,
    // so several bots targeting the same zone don't converge on the identical coordinate and jam
    // each other.
    private const float LootZoneJitterFraction = 0.6f;

    /// <summary>
    /// How long a task stays committed to actively working a monument before giving up on its
    /// zones and escalating to road-following. Ranges scale with tier, and this is only a minimum
    /// commitment - a survivor still finding zones to check keeps going past the deadline.
    /// </summary>
    private const float LowTierMonumentDwellMinSeconds = 180f;
    private const float LowTierMonumentDwellMaxSeconds = 600f;
    private const float MediumTierMonumentDwellMinSeconds = 480f;
    private const float MediumTierMonumentDwellMaxSeconds = 1200f;
    private const float HighTierMonumentDwellMinSeconds = 1200f;
    private const float HighTierMonumentDwellMaxSeconds = 1800f;

    /// <summary>
    /// Wider range check for a monument the survivor has already committed to working this task,
    /// so wandering to the far side of a large complex doesn't read as having left it. Only
    /// applies to an already-committed monument, not the initial discovery decision.
    /// </summary>
    private const float CommittedMonumentRangeRadius = 150f;

    private bool TryGetCommittedMonument(Vector3 position, LootTaskState state, out MonumentInfo monument)
    {
        monument = null;

        if (state.CommittedMonumentName == null || MonumentAccess.GetAllMonuments().Count == 0)
        {
            return false;
        }

        foreach (MonumentInfo candidate in MonumentAccess.GetAllMonuments())
        {
            if (candidate != null && candidate.name == state.CommittedMonumentName)
            {
                monument = candidate;
                return Vector3.Distance(position, candidate.transform.position) <= CommittedMonumentRangeRadius;
            }
        }

        return false;
    }

    private float GetMonumentDwellSeconds(string monumentName)
    {
        return GetMonumentTier(monumentName) switch
        {
            MonumentTier.TierTwo => UnityEngine.Random.Range(MediumTierMonumentDwellMinSeconds, MediumTierMonumentDwellMaxSeconds),
            MonumentTier.TierThree => UnityEngine.Random.Range(HighTierMonumentDwellMinSeconds, HighTierMonumentDwellMaxSeconds),
            // TierZero and TierOne share the same low-commitment dwell bucket.
            _ => UnityEngine.Random.Range(LowTierMonumentDwellMinSeconds, LowTierMonumentDwellMaxSeconds),
        };
    }

    /// <summary>
    /// Picks a jittered walk target within the zone, resolving real ground height via
    /// TryFindGroundBelow and then snapping onto the navmesh via SnapApproachPointToNavMesh, the
    /// same two-step validation GetApproachPoint uses elsewhere.
    /// </summary>
    private Vector3 JitterZoneDestination(Vector3 zoneWorldPosition, float zoneRadius, BasePlayer npc)
    {
        float maxOffset = zoneRadius * LootZoneJitterFraction;
        Vector2 offset = UnityEngine.Random.insideUnitCircle * maxOffset;
        Vector3 jittered = zoneWorldPosition + new Vector3(offset.x, 0f, offset.y);

        bool groundProbeSucceeded = _engine.NavigationManager.TryFindGroundBelow(jittered + Vector3.up * 4f, 4f, 6f, out float groundY, out Collider groundCollider);

        if (groundProbeSucceeded)
        {
            jittered.y = groundY;
        }

        Vector3 snapped = SnapApproachPointToNavMesh(npc, jittered);

        // Logs which step (ground-probe vs navmesh-snap) is responsible whenever a jittered zone
        // destination ends up clearly elevated.
        if (Mathf.Abs(snapped.y - zoneWorldPosition.y) > LootVerticalReachLimit)
        {
            VerbosePuts($"jitterzone-diag: zone base {zoneWorldPosition} -> jittered XZ {jittered} (ground-probe succeeded={groundProbeSucceeded}, groundY={groundY:F2}, collider '{(groundCollider != null ? groundCollider.gameObject.name : "none")}') -> after SnapApproachPointToNavMesh = {snapped} (delta {snapped.y - zoneWorldPosition.y:F2}m).");
        }

        return snapped;
    }

    private readonly Dictionary<string, List<MonumentLootZone>> _monumentLootZones = new();

    private void LoadMonumentLootZones()
    {
        if (!File.Exists(MonumentLootZoneSaveFile))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(MonumentLootZoneSaveFile);
            Dictionary<string, List<MonumentLootZone>>? loaded = JsonConvert.DeserializeObject<Dictionary<string, List<MonumentLootZone>>>(json);

            if (loaded != null)
            {
                _monumentLootZones.Clear();

                foreach (KeyValuePair<string, List<MonumentLootZone>> entry in loaded)
                {
                    _monumentLootZones[entry.Key] = entry.Value;
                }

                Puts($"Loaded monument loot zones for {_monumentLootZones.Count} monument type(s) from {MonumentLootZoneSaveFile}.");
            }
        }
        catch (Exception ex)
        {
            Puts($"WARNING: Failed to load monument loot zones from {MonumentLootZoneSaveFile}: {ex.Message}");
        }
    }

    private void SaveMonumentLootZones()
    {
        try
        {
            if (!Directory.Exists("LivingRust"))
            {
                Directory.CreateDirectory("LivingRust");
            }

            string json = JsonConvert.SerializeObject(_monumentLootZones, Formatting.Indented);
            File.WriteAllText(MonumentLootZoneSaveFile, json);
        }
        catch (Exception ex)
        {
            Puts($"WARNING: Failed to save monument loot zones to {MonumentLootZoneSaveFile}: {ex.Message}");
        }
    }

    /// <summary>
    /// Called once from OnServerInitialized to scan every distinct monument type on this map/wipe
    /// up front, rather than waiting for a bot's loot search to stumble onto each one lazily.
    /// Multiple instances of the same monument type collapse to a single scan, and a type already
    /// known from a previous session's persisted data is skipped entirely.
    /// </summary>
    private void ScanAllMonumentsOnBoot()
    {
        if (MonumentAccess.GetAllMonuments().Count == 0)
        {
            Puts("WARNING: MonumentAccess.GetAllMonuments() isn't available yet - skipping the boot-time monument loot scan.");
            return;
        }

        HashSet<string> seenTypesThisScan = new();
        int scannedCount = 0;
        int skippedAlreadyKnownCount = 0;

        foreach (MonumentInfo monument in MonumentAccess.GetAllMonuments())
        {
            if (monument == null || !seenTypesThisScan.Add(monument.name))
            {
                continue;
            }

            if (IsMonumentExcludedFromAutonomy(monument))
            {
                continue;
            }

            if (_monumentLootZones.TryGetValue(monument.name, out List<MonumentLootZone> existingZones) && existingZones.Count > 0)
            {
                skippedAlreadyKnownCount++;
                continue;
            }

            ScanMonumentForLootZones(monument);
            scannedCount++;
        }

        Puts($"monument-loot-zones: boot scan complete - {seenTypesThisScan.Count} distinct monument type(s) on this map, {scannedCount} newly scanned, {skippedAlreadyKnownCount} already known from a previous session.");
    }

    /// <summary>
    /// Per-monument-type override capping how far above the monument's base height a container
    /// can be before it's excluded from the zone scan entirely, since some monuments have
    /// rooftop-only containers that are unreachable or only reachable via a bugged navmesh
    /// connection. Scoped to specific monument types rather than a blanket rule, since other
    /// monuments have genuinely reachable multi-floor interiors.
    /// </summary>
    private static readonly Dictionary<string, float> MonumentZoneMaxHeightAboveBase = new(StringComparer.OrdinalIgnoreCase)
    {
        ["supermarket"] = 3f,
    };

    private static bool TryGetMonumentZoneMaxHeight(string monumentName, out float maxHeight)
    {
        foreach (KeyValuePair<string, float> entry in MonumentZoneMaxHeightAboveBase)
        {
            if (monumentName.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                maxHeight = entry.Value;
                return true;
            }
        }

        maxHeight = 0f;
        return false;
    }

    /// <summary>
    /// Runs the auto-detect scan against one monument instance and stores the result under its
    /// monument type, overwriting any previous scan for that type. Reuses the same lootability
    /// filters the normal open-world search applies, so a zone never gets built around containers
    /// a bot wouldn't actually loot.
    /// </summary>
    private List<MonumentLootZone> ScanMonumentForLootZones(MonumentInfo monument)
    {
        float scanRadius = Mathf.Max(monument.Bounds.extents.x, monument.Bounds.extents.z, MonumentLootScanFallbackRadius / 2f) + 15f;

        bool hasHeightCap = TryGetMonumentZoneMaxHeight(monument.name, out float maxHeightAboveBase);

        // A monument prefab's pivot is frequently not at real ground level, so ground height is
        // probed directly rather than trusting the pivot.
        float monumentBaseY = monument.transform.position.y;
        bool groundProbeSucceeded = _engine.NavigationManager.TryFindGroundBelow(monument.transform.position + Vector3.up * 4f, 4f, 6f, out float realMonumentGroundY, out Collider monumentGroundCollider);

        if (groundProbeSucceeded)
        {
            monumentBaseY = realMonumentGroundY;
        }

        if (hasHeightCap)
        {
            VerbosePuts($"scanmonumentloot-diag: '{monument.name}' pivot Y={monument.transform.position.y:F2}, ground-probe succeeded={groundProbeSucceeded}, resolved monumentBaseY={monumentBaseY:F2} (collider '{(monumentGroundCollider != null ? monumentGroundCollider.gameObject.name : "none")}'), cap={maxHeightAboveBase:F1}m -> excluding anything above Y={monumentBaseY + maxHeightAboveBase:F2}.");
        }

        List<StorageContainer> containers = new();

        _engine.NavigationManager.GetAllLootContainersInRange(
            monument.transform.position,
            scanRadius,
            candidate => candidate.inventory != null
                && !IsVehicleFuelStorage(candidate)
                && !IsHotAirBalloonStorage(candidate)
                && !IsRowboatStorage(candidate)
                && !IsMailbox(candidate)
                && (!hasHeightCap || (candidate.transform.position.y - monumentBaseY) <= maxHeightAboveBase),
            containers);

        // Simple single-link clustering: each container joins the nearest existing cluster within
        // LootZoneClusterDistance of its running centroid, or starts a new one.
        List<List<Vector3>> clusters = new();

        foreach (StorageContainer container in containers)
        {
            Vector3 position = container.transform.position;
            List<Vector3>? bestCluster = null;
            float bestDistance = LootZoneClusterDistance;

            foreach (List<Vector3> cluster in clusters)
            {
                Vector3 centroid = GetCentroid(cluster);
                float distance = Vector3.Distance(centroid, position);

                if (distance > bestDistance)
                {
                    continue;
                }

                // A purely vertical check, independent of the 3D distance gate above, so a
                // single-room cluster can't chain-link ground-floor containers to rooftop ones
                // through stepping-stone containers on separate levels.
                if (cluster.Any(member => Mathf.Abs(member.y - position.y) > LootZoneMaxVerticalSpan))
                {
                    continue;
                }

                bestDistance = distance;
                bestCluster = cluster;
            }

            if (bestCluster != null)
            {
                bestCluster.Add(position);
            }
            else
            {
                clusters.Add(new List<Vector3> { position });
            }
        }

        List<MonumentLootZone> zones = new();

        foreach (List<Vector3> cluster in clusters)
        {
            if (cluster.Count < LootZoneMinClusterSize)
            {
                continue;
            }

            Vector3 centroid = GetCentroid(cluster);
            float maxMemberDistance = 0f;

            foreach (Vector3 member in cluster)
            {
                maxMemberDistance = Mathf.Max(maxMemberDistance, Vector3.Distance(centroid, member));
            }

            zones.Add(new MonumentLootZone
            {
                LocalOffset = monument.transform.InverseTransformPoint(centroid),
                Radius = maxMemberDistance + LootZoneRadiusMargin,
                ContainerCountAtScan = cluster.Count,
            });
        }

        _monumentLootZones[monument.name] = zones;
        SaveMonumentLootZones();

        return zones;
    }

    private static Vector3 GetCentroid(List<Vector3> points)
    {
        Vector3 sum = Vector3.zero;

        foreach (Vector3 point in points)
        {
            sum += point;
        }

        return sum / points.Count;
    }

    private bool TryFindMonumentByFilter(string? nameFilter, Vector3 fallbackOrigin, out MonumentInfo monument)
    {
        monument = null;

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            monument = MonumentAccess.GetAllMonuments()
                .Where(candidate => candidate.name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(candidate => Vector3.Distance(candidate.transform.position, fallbackOrigin))
                .FirstOrDefault();

            return monument != null;
        }

        return TryGetNearestMonument(fallbackOrigin, MonumentLootScanFallbackRadius * 3f, out monument);
    }

    [ChatCommand("lr.debug.scanmonumentloot")]
    private void CmdScanMonumentLoot(BasePlayer player, string command, string[] args)
    {
        RunScanMonumentLoot(player, args.Length > 0 ? string.Join(" ", args) : null);
    }

    // Finishing the loot route through an Abandoned Supermarket or Oxum's Gas Station hands the
    // survivor a green keycard and one fuse regardless of what it found, so it can start on the
    // green-card puzzle monuments. Tops up rather than stacks, so repeat visits can't farm cards.
    private static readonly string[] EarlyProgressionKitMonumentSubstrings = { "supermarket", "gas_station" };

    private void GrantEarlyProgressionKit(Survivor survivor, string monumentName)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed
            || !EarlyProgressionKitMonumentSubstrings.Any(s => monumentName.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return;
        }

        List<string> granted = new();

        if (!HasUsableKeycard(npc, KeycardTier.Green))
        {
            GiveItem(npc, KeycardShortnames[KeycardTier.Green], 1);
            granted.Add("green keycard");
        }

        int heldFuses = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => Array.IndexOf(CardPuzzleFuseShortnames, item.info.shortname) >= 0)
            .Sum(item => item.amount);

        if (heldFuses < 1)
        {
            GiveItem(npc, CardPuzzleFuseShortnames[0], 1);
            granted.Add("fuse");
        }

        if (granted.Count > 0)
        {
            Puts($"early-kit: '{survivor.Character.Alias}' finished '{monumentName}' and was handed a {string.Join(" and a ", granted)} to help it start on the green-card monuments.");
        }
    }

    [ConsoleCommand("lr.debug.scanmonumentloot")]
    private void CmdScanMonumentLootConsole(ConsoleSystem.Arg arg)
    {
        RunScanMonumentLoot(arg.Player(), arg.HasArgs() ? string.Join(" ", arg.Args) : null);
    }

    private void RunScanMonumentLoot(BasePlayer? player, string? nameFilter)
    {
        if (player == null)
        {
            Puts("scanmonumentloot: needs a real player position to find the nearest monument from (run in-game or specify a name filter close enough to resolve unambiguously).");
            return;
        }

        if (!TryFindMonumentByFilter(nameFilter, player.transform.position, out MonumentInfo monument))
        {
            string message = string.IsNullOrWhiteSpace(nameFilter)
                ? "No monument found nearby."
                : $"No monument matching '{nameFilter}' found.";
            player.ChatMessage($"[LivingRust] {message}");
            return;
        }

        List<MonumentLootZone> zones = ScanMonumentForLootZones(monument);

        int totalContainers = zones.Sum(zone => zone.ContainerCountAtScan);
        string summary = $"Scanned '{monument.name}' - found {zones.Count} loot zone(s) covering {totalContainers} container(s).";

        player.ChatMessage($"[LivingRust] {summary}");
        Puts($"scanmonumentloot: {summary}");

        foreach (MonumentLootZone zone in zones)
        {
            VerbosePuts($"scanmonumentloot: zone at local offset {zone.LocalOffset}, radius {zone.Radius:F0}m, {zone.ContainerCountAtScan} container(s).");
        }
    }

    [ChatCommand("lr.debug.lootmonument")]
    private void CmdLootMonument(BasePlayer player, string command, string[] args)
    {
        RunLootMonument(player, args.Length > 0 ? string.Join(" ", args) : null);
    }

    [ConsoleCommand("lr.debug.lootmonument")]
    private void CmdLootMonumentConsole(ConsoleSystem.Arg arg)
    {
        RunLootMonument(arg.Player(), arg.HasArgs() ? string.Join(" ", arg.Args) : null);
    }

    /// <summary>
    /// Debug command that walks a survivor to the nearest detected loot zone and hard-commits it
    /// to that monument (LootTaskState.CommittedMonumentName, a 1-hour dwell deadline), instead of
    /// handing off to StartLootForResourcesTask which would re-roll its destination on arrival.
    /// Useful for repeatable in-monument navigation testing.
    /// </summary>
    private void RunLootMonument(BasePlayer? player, string? nameFilter)
    {
        if (_engine == null)
        {
            player?.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        if (player == null)
        {
            Puts("lootmonument: needs a real player position to find the nearest monument/survivor from.");
            return;
        }

        if (!TryFindMonumentByFilter(nameFilter, player.transform.position, out MonumentInfo monument))
        {
            string message = string.IsNullOrWhiteSpace(nameFilter)
                ? "No monument found nearby."
                : $"No monument matching '{nameFilter}' found.";
            player.ChatMessage($"[LivingRust] {message}");
            return;
        }

        if (!_monumentLootZones.TryGetValue(monument.name, out List<MonumentLootZone> zones) || zones.Count == 0)
        {
            VerbosePuts($"lootmonument: no cached zones for '{monument.name}' yet - scanning now.");
            zones = ScanMonumentForLootZones(monument);
        }

        if (zones.Count == 0)
        {
            player.ChatMessage($"[LivingRust] '{monument.name}' has no detectable loot zones (too few clustered containers).");
            return;
        }

        Survivor survivor = FindNearestSpawnedSurvivor(player.transform.position);

        if (survivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.spawn first.");
            return;
        }

        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' has no live BasePlayer.");
            return;
        }

        // Applies the same height sanity check EscalateSearchToMonumentZone uses for real
        // gameplay, and doesn't fall back to an unreachable zone if every one reprojects too high.
        MonumentLootZone? nearestZone = zones
            .OrderBy(zone => Vector3.Distance(npc.transform.position, monument.transform.TransformPoint(zone.LocalOffset)))
            .FirstOrDefault(zone => (monument.transform.TransformPoint(zone.LocalOffset).y - npc.transform.position.y) <= LootVerticalReachLimit);

        if (nearestZone == null)
        {
            player.ChatMessage($"[LivingRust] '{monument.name}' has {zones.Count} cached zone(s), but every one reprojects too high above '{survivor.Character.Alias}' at this instance - nothing reachable to hard-commit to. Try /lr.debug.scanmonumentloot standing at THIS instance specifically.");
            return;
        }

        Vector3 zoneWorldPosition = JitterZoneDestination(monument.transform.TransformPoint(nearestZone.LocalOffset), nearestZone.Radius, npc);

        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' heading to a loot zone in '{monument.name}' ({nearestZone.ContainerCountAtScan} container(s) at scan time) - hard-committing there for navigation testing.");
        Puts($"lootmonument: '{survivor.Character.Alias}' walking to a loot zone in '{monument.name}' at {zoneWorldPosition}.");

        survivor.Character.CurrentTask = TaskType.LootForResources;

        StartWalkingWithRecovery(
            survivor,
            zoneWorldPosition,
            onArrived: () =>
            {
                LootTaskState state = new()
                {
                    CommittedMonumentName = monument.name,
                    CurrentMonumentZoneKey = monument.name,
                    CommittedMonumentDeadline = UnityEngine.Time.realtimeSinceStartup + 3600f,
                };

                ContinueLootTask(survivor, state, forceLocalScan: true);
            },
            onFailed: () =>
            {
                // Uses StartWalkingWithRecovery rather than plain StartWalking, so a failed
                // initial walk doesn't leave the survivor's task stuck with nothing driving it.
                player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' couldn't reach a loot zone in '{monument.name}' even after full stuck-recovery - genuinely done.");
                Puts($"lootmonument: '{survivor.Character.Alias}' failed to reach '{monument.name}' after full recovery.");
                survivor.Character.CurrentTask = TaskType.None;
            });
    }

    [ChatCommand("lr.debug.testrecycler")]
    private void CmdDebugTestRecycler(BasePlayer player, string command, string[] args)
    {
        RunDebugTestRecycler(player);
    }

    [ConsoleCommand("lr.debug.testrecycler")]
    private void CmdDebugTestRecyclerConsole(ConsoleSystem.Arg arg)
    {
        RunDebugTestRecycler(arg.Player());
    }

    // Component shortnames that all qualify as IsRecycleFodder, used to fill a test survivor's
    // inventory with realistic recyclable items.
    private static readonly string[] TestRecyclerComponentShortnames =
    {
        "metalblade", "metalpipe", "sheetmetal", "semibody", "techparts",
        "smgbody", "tarp", "metalspring", "sewingkit", "roadsigns", "gears",
    };

    // Termination guarantee against a bad/unresolvable shortname spinning IsContainerFull's while
    // loop forever.
    private const int TestRecyclerMaxFillAttempts = 200;

    /// <summary>
    /// Debug-only stress test for the full-inventory-to-recycler pipeline. Spawns a fresh survivor,
    /// stuffs its inventory with random component stacks until full, then hands off to
    /// StartLootForResourcesTask, which sends it straight to recycling.
    /// </summary>
    private void RunDebugTestRecycler(BasePlayer? player)
    {
        if (_engine == null)
        {
            player?.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        if (player == null)
        {
            Puts("testrecycler: needs a real player position to spawn near.");
            return;
        }

        Character character = _engine.CharacterManager.CreateInitialSurvivor();
        Survivor survivor = _engine.SurvivorManager.Create(character);

        Vector3 spawnPosition = FindSpawnAimPoint(player, out _);
        BasePlayer npc = SpawnSurvivor(survivor, spawnPosition, player.transform.rotation);

        if (npc == null)
        {
            player.ChatMessage("[LivingRust] Failed to spawn test survivor.");
            Puts($"testrecycler: failed to spawn survivor '{character.Alias}'.");
            return;
        }

        int stacksAdded = 0;

        for (int attempt = 0; attempt < TestRecyclerMaxFillAttempts && !IsContainerFull(npc.inventory.containerMain); attempt++)
        {
            string shortname = TestRecyclerComponentShortnames[UnityEngine.Random.Range(0, TestRecyclerComponentShortnames.Length)];
            Item item = ItemManager.CreateByName(shortname, UnityEngine.Random.Range(1, 25));

            if (item == null)
            {
                continue;
            }

            if (!item.MoveToContainer(npc.inventory.containerMain))
            {
                item.Remove();
                break;
            }

            stacksAdded++;
        }

        player.ChatMessage($"[LivingRust] '{character.Alias}' spawned with {stacksAdded} random component stack(s) filling its inventory - starting its loot task, should head straight for a recycler.");
        Puts($"testrecycler: '{character.Alias}' spawned pre-filled with {stacksAdded} component stack(s) (main inventory full: {IsContainerFull(npc.inventory.containerMain)}).");

        StartLootForResourcesTask(survivor);
    }

    // How close a survivor needs to be to a monument's transform for its zones to be considered.
    private const float MonumentLootZoneDetectionRadius = 60f;

    /// <summary>
    /// Called from ContinueLootTask once its normal radius scan comes up empty, discovering or
    /// reusing loot zones for whichever monument the survivor is near. Falls through to
    /// EscalateSearchAlongRoad whenever there's no monument nearby, it has no detectable zones, or
    /// every zone has already been visited this task.
    /// </summary>
    private void EscalateSearchToMonumentZone(Survivor survivor, LootTaskState state, bool isRetryAfterClear = false)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.None;
            return;
        }

        // Already committed to a specific monument this task - uses the wider
        // CommittedMonumentRangeRadius check against that exact monument instead of the generic
        // discovery gate, so wandering to a far corner of a large complex doesn't read as leaving.
        MonumentInfo monument;
        bool foundMonument;

        if (state.CommittedMonumentName != null)
        {
            foundMonument = TryGetCommittedMonument(npc.transform.position, state, out monument);
        }
        else
        {
            foundMonument = TryGetNearestMonument(npc.transform.position, MonumentLootZoneDetectionRadius, out monument);
        }

        if (!foundMonument || IsMonumentExcludedFromAutonomy(monument)
            || (!HasCompletedEarlyGameMilestones(survivor) && IsNearEarlyGameRestrictedMonument(monument.transform.position))
            || (ShouldAvoidInventoryOrPrepGatedMonument(survivor) && IsNearInventoryOrPrepGatedMonument(monument.transform.position)))
        {
            if (state.OnMonumentZonesExhausted != null)
            {
                state.OnMonumentZonesExhausted();
            }
            else
            {
                EscalateSearchAlongRoad(survivor, state);
            }

            return;
        }

        // Opportunistic card-puzzle detour, checked ahead of the normal ghost route below since
        // it's a narrower, higher-value detour only attempted if the survivor already has the
        // right card and fuse.
        if (TryStartCardPuzzleDetour(survivor, monument, state))
        {
            return;
        }

        // Authored ghost route detour: a survivor near this monument's recorded trouble spot takes
        // a pre-validated recorded path instead of the generic zone-walk search below. Capped at
        // once per monument per task so finishing it can't immediately re-trigger it in a loop.
        if (!state.GhostRouteVisitedMonuments.Contains(monument.name)
            && TryClaimGhostRouteForMonument(monument.name, survivor.Character.Id, out string ghostRouteFilePath)
            && TryLoadTraceWaypoints(ghostRouteFilePath, out List<GhostRouteWaypoint> ghostWaypoints, out MonumentInfo recordedAtMonument, monument.name)
            && ghostWaypoints.Count > 0)
        {
            // Re-projects onto this specific monument instance, which may be a different instance
            // of the same type than the one the route was recorded at. Only projects if the trace
            // actually localized to a monument; otherwise the waypoints are already world
            // coordinates.
            List<GhostRouteWaypoint> worldWaypoints = recordedAtMonument != null
                ? ProjectGhostRouteToMonument(ghostWaypoints, monument)
                : ghostWaypoints;

            state.GhostRouteVisitedMonuments.Add(monument.name);

            // Sets these here too (not just in the zone-scan branch below) so the wider
            // CommittedMonumentRangeRadius check applies once the route hands back control,
            // rather than leaving the task's committed monument untouched while the route runs.
            state.CommittedMonumentName = monument.name;
            state.CommittedMonumentDeadline = UnityEngine.Time.realtimeSinceStartup + GetMonumentDwellSeconds(monument.name);

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' is heading for '{monument.name}''s known-hard navigation stretch - running to the recorded route's start ('{ghostRouteFilePath}') before taking it.");
            Puts($"ghostroute: '{survivor.Character.Alias}' committed to a ghost route through '{monument.name}' via '{ghostRouteFilePath}' - walking there first.");

            // A collision-respecting walk to the route's first waypoint, rather than phasing
            // straight there; StartGhostRoute's phase-through movement only starts from this
            // arrival point, keeping the collision bypass scoped to the recorded interior stretch.
            Action onGhostRouteComplete = () =>
            {
                state.GhostRouteCompletedMonuments.Add(monument.name);
                CompleteGhostRoute(ghostRouteFilePath);
                GrantEarlyProgressionKit(survivor, monument.name);
                ContinueLootTask(survivor, state);
            };

            // Registered here too, not just inside StartGhostRoute, so an interruption (e.g.
            // combat) during the approach walk retries the same approach rather than abandoning
            // the commitment entirely. StartGhostRoute overwrites this with real progress once it
            // starts moving through the route.
            _pendingGhostRouteToResume[survivor.Character.Id] = (worldWaypoints, 0, onGhostRouteComplete);

            // Opportunistic detours to nearby loot while phasing through the route.
            StartGhostRouteLootScan(survivor, onGhostRouteComplete);

            StartLongDistanceWalk(
                survivor,
                worldWaypoints[0].Position,
                monument.name,
                onArrived: () => StartGhostRoute(survivor, worldWaypoints, 0, onGhostRouteComplete),
                onFailed: () =>
                {
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach the ghost route's start near '{monument.name}' - falling back to the generic path instead.");
                    _pendingGhostRouteToResume.Remove(survivor.Character.Id);
                    StopGhostRouteLootScan(survivor.Character.Id);

                    if (state.OnMonumentZonesExhausted != null)
                    {
                        state.OnMonumentZonesExhausted();
                    }
                    else
                    {
                        EscalateSearchAlongRoad(survivor, state);
                    }
                });

            return;
        }

        // Ghost-route-only monument (Launch Site) with no route to run (none free, or the visit's
        // route/puzzle already finished): leave. No ambient zone looting there - that is what piled
        // up 50+ corpses. A genuinely completed route/puzzle falls through to the normal wrap-up
        // below so its rewards still apply.
        if (IsGhostRouteOnlyMonument(monument)
            && !state.GhostRouteCompletedMonuments.Contains(monument.name)
            && !state.CardPuzzleCompletedMonuments.Contains(monument.name))
        {
            Puts($"loot-task: '{survivor.Character.Alias}' is at '{monument.name}' with no ghost route or puzzle available to run - leaving (this monument is ghost-route-only).");
            state.CommittedMonumentName = null;

            timer.Once(0.5f, () =>
            {
                BasePlayer liveNpc = survivor.Player;

                if (liveNpc != null && !liveNpc.IsDestroyed && survivor.Character.State != CharacterState.Dead)
                {
                    StartLootForResourcesTask(survivor);
                }
            });

            return;
        }

        if (!_monumentLootZones.TryGetValue(monument.name, out List<MonumentLootZone> zones) || zones.Count == 0)
        {
            zones = ScanMonumentForLootZones(monument);

            if (zones.Count > 0)
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' discovered {zones.Count} loot zone(s) in '{monument.name}' while exploring it - remembered for every survivor from now on.");
            }
        }

        // A different monument than the last escalation - start visited-zone tracking fresh
        // rather than carrying over indices from a different zone list.
        if (state.CurrentMonumentZoneKey != monument.name)
        {
            state.VisitedMonumentZoneIndices.Clear();
            state.CurrentMonumentZoneKey = monument.name;

            float dwellSeconds = GetMonumentDwellSeconds(monument.name);
            state.CommittedMonumentName = monument.name;
            state.CommittedMonumentDeadline = UnityEngine.Time.realtimeSinceStartup + dwellSeconds;

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' is committing to '{monument.name}' ({GetMonumentTier(monument.name)} tier) for at least {dwellSeconds:F0}s before it'll consider moving on.");
        }

        MonumentLootZone? nextZone = null;
        int nextZoneIndex = -1;
        float nearestDistance = float.MaxValue;

        for (int i = 0; i < zones.Count; i++)
        {
            if (state.VisitedMonumentZoneIndices.Contains(i))
            {
                continue;
            }

            Vector3 candidateWorldPosition = monument.transform.TransformPoint(zones[i].LocalOffset);

            // A zone confirmed unreachable (see the onFailed handler below) is treated like an
            // already-visited one, so a fresh task doesn't keep re-picking the same doomed zone.
            if (IsInMonumentAvoidZone(candidateWorldPosition, survivor))
            {
                continue;
            }

            // Same proactive height gate as IsUnreachableCrateOrBarrel/IsUnreachableDroppedItem:
            // a zone can have real navmesh nearby (e.g. a disconnected rooftop) that simply isn't
            // reachable from here, which this rejects proactively rather than waiting for repeated
            // failures to accumulate into a permanent avoid-zone record.
            if ((candidateWorldPosition.y - npc.transform.position.y) > LootVerticalReachLimit)
            {
                continue;
            }

            float distance = Vector3.Distance(npc.transform.position, candidateWorldPosition);

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nextZone = zones[i];
                nextZoneIndex = i;
            }
        }

        if (nextZone == null)
        {
            // Every known zone's already been checked, but if this monument is still within its
            // minimum commitment window, that's not a dead end yet - re-open every zone and
            // patrol again rather than bailing to the road. isRetryAfterClear caps this to exactly
            // one retry per call to avoid unbounded recursion.
            if (!isRetryAfterClear && zones.Count > 0 && state.CommittedMonumentName == monument.name && UnityEngine.Time.realtimeSinceStartup < state.CommittedMonumentDeadline)
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' checked every known zone in '{monument.name}' but is still committed to it for {state.CommittedMonumentDeadline - UnityEngine.Time.realtimeSinceStartup:F0}s more - patrolling its zones again instead of leaving.");
                state.VisitedMonumentZoneIndices.Clear();
                EscalateSearchToMonumentZone(survivor, state, isRetryAfterClear: true);
                return;
            }

            // Either no zones at all, or the commitment window's expired - nothing more this
            // system can offer, so this is treated as "finished a loot run at this monument" for
            // reward purposes. See TryGrantMonumentHandicapReward for the full reward-table spec.
            MonumentTier finishedMonumentTier = GetMonumentTier(monument.name);
            bool didRealGhostRouteThisVisit = state.GhostRouteCompletedMonuments.Contains(monument.name) || state.CardPuzzleCompletedMonuments.Contains(monument.name);

            state.CommittedMonumentName = null;

            // A rushing survivor gets its own separate, boosted reward path (ConcludeMonumentRush)
            // instead of the standard one, checked before the normal reward grants below.
            if (_pursuingMonumentRushGoal.Remove(survivor.Character.Id))
            {
                _monumentRushDeadline.Remove(survivor.Character.Id);
                ConcludeMonumentRush(survivor, npc, finishedMonumentTier, monument.name, didRealGhostRouteThisVisit);
                return;
            }

            TryGrantMonumentHandicapReward(
                survivor,
                npc,
                finishedMonumentTier,
                didRealGhostRoute: didRealGhostRouteThisVisit,
                didCardPuzzle: state.CardPuzzleCompletedMonuments.Contains(monument.name));

            // Separate roll/table from the weapon handicap above.
            TryGrantMonumentKeycardReward(survivor, npc, finishedMonumentTier, monument.name);

            // Independent of all of the above - a bonus clothing/armor item, a bonus medical
            // item, and a bonus tool/weapon item.
            TryGrantMonumentClothingReward(survivor, npc, finishedMonumentTier);
            TryGrantMonumentMedicalReward(survivor, npc, finishedMonumentTier);
            TryGrantMonumentToolBonusReward(survivor, npc, finishedMonumentTier);
            TryGrantMonumentBlueprintFragmentReward(survivor, npc, finishedMonumentTier);

            // The monument-run timer has run out: the one non-full-inventory moment a survivor
            // heads to a recycler (2026-10-03, Lucas's spec). Returns false if nothing worth
            // recycling or no recycler in range, in which case it carries on as before.
            if (TryStartRecyclingTask(survivor))
            {
                return;
            }

            if (state.OnMonumentZonesExhausted != null)
            {
                state.OnMonumentZonesExhausted();
            }
            else
            {
                EscalateSearchAlongRoad(survivor, state);
            }

            return;
        }

        state.VisitedMonumentZoneIndices.Add(nextZoneIndex);

        Vector3 zoneWorldPosition = JitterZoneDestination(monument.transform.TransformPoint(nextZone.LocalOffset), nextZone.Radius, npc);

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' checking a known loot zone in '{monument.name}' ({nextZone.ContainerCountAtScan} container(s) at last scan, {nearestDistance:F0}m away).");

        StartLongDistanceWalk(
            survivor,
            zoneWorldPosition,
            monument.name,
            onArrived: () => ContinueLootTask(survivor, state),
            onFailed: () =>
            {
                // Full recovery escalation already exhausted (that's the
                // only way StartWalkingWithRecovery calls onFailed) -
                // exactly the strong per-incident evidence PoisonAreaNow/
                // the monument avoid-zone system exists for. Without this,
                // this specific zone would just get re-picked by the loop
                // above next time (see its own doc comment).
                PoisonAreaNow(survivor, state, zoneWorldPosition);
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach a known loot zone in '{monument.name}' - trying the road-following fallback instead.");
                EscalateSearchAlongRoad(survivor, state);
            });
    }

    /// <summary>
    /// The last resort in the loot escalation ladder, called only once EscalateSearchAlongRoad has
    /// fully exhausted itself. Only considers monuments already known to have zone data, and is
    /// capped at one attempt per task so an unreachable monument can't chain into further trips.
    /// </summary>
    private void EscalateSearchToKnownMonument(Survivor survivor, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.None;
            return;
        }

        if (state.TraveledToDistantMonument)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' already tried a distant monument this task and found nothing further - rolling for something new instead of giving up.");
            NeverIdleFallback(survivor, state);
            return;
        }

        if (_monumentLootZones.Count == 0 || MonumentAccess.GetAllMonuments().Count == 0)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' found nothing left to loot nearby, no road to follow, and no known monument to head for either - rolling for something new instead of giving up.");
            NeverIdleFallback(survivor, state);
            return;
        }

        MonumentInfo nearestKnownMonument = null;
        MonumentLootZone nearestZone = null;
        Vector3 nearestZoneWorldPosition = Vector3.zero;
        float nearestDistance = float.MaxValue;

        foreach (MonumentInfo candidate in MonumentAccess.GetAllMonuments())
        {
            if (candidate == null || IsMonumentExcludedFromAutonomy(candidate) || IsGhostRouteOnlyMonument(candidate)
                || (!HasCompletedEarlyGameMilestones(survivor) && IsNearEarlyGameRestrictedMonument(candidate.transform.position))
                || (ShouldAvoidInventoryOrPrepGatedMonument(survivor) && IsNearInventoryOrPrepGatedMonument(candidate.transform.position))
                || !_monumentLootZones.TryGetValue(candidate.name, out List<MonumentLootZone> zones) || zones.Count == 0)
            {
                continue;
            }

            foreach (MonumentLootZone zone in zones)
            {
                Vector3 zoneWorldPosition = candidate.transform.TransformPoint(zone.LocalOffset);
                float distance = Vector3.Distance(npc.transform.position, zoneWorldPosition);

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestKnownMonument = candidate;
                    nearestZone = zone;
                    nearestZoneWorldPosition = zoneWorldPosition;
                }
            }
        }

        if (nearestKnownMonument == null || nearestZone == null)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' found nothing left to loot nearby, no road to follow, and no known monument to head for either - rolling for something new instead of giving up.");
            NeverIdleFallback(survivor, state);
            return;
        }

        state.TraveledToDistantMonument = true;

        // Walks to the nearest known zone's world position rather than the monument's own
        // transform origin, since a zone is a real container cluster's centroid.
        VerbosePuts($"loot-task: '{survivor.Character.Alias}' found nothing left nearby - heading for a known loot zone in '{nearestKnownMonument.name}' ({nearestDistance:F0}m away), as a last resort.");
        Puts($"loot-task: '{survivor.Character.Alias}' making a long trip to '{nearestKnownMonument.name}' ({nearestDistance:F0}m) after exhausting everything closer.");

        StartLongDistanceWalk(
            survivor,
            nearestZoneWorldPosition,
            nearestKnownMonument.name,
            onArrived: () => ContinueLootTask(survivor, state),
            onFailed: () =>
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach '{nearestKnownMonument.name}' either - rolling for something new instead of giving up.");
                NeverIdleFallback(survivor, state);
            });
    }

    /// <summary>
    /// Replaces an outright give-up at every genuine dead end in the escalation ladder. Tries
    /// recycling first, then rolls a coin between a fresh gear-weighted task and a walk to the
    /// nearest road, so a dead end always makes forward progress instead of idling.
    /// </summary>
    private void NeverIdleFallback(Survivor survivor, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.None;
            return;
        }

        // No recycler trip here (2026-10-03, Lucas's spec): a survivor only recycles when its
        // inventory is full or the monument-run timer runs out - not at every dead end.

        if (UnityEngine.Random.value < 0.5f)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' hit a dead end - rolling for a brand new monument instead of idling.");
            StartLootForResourcesTask(survivor);
            return;
        }

        if (_engine.NavigationManager.TryFindNearestRoadPoint(npc.transform.position, RoadSearchFallbackRadius, out Vector3 roadPoint, out _, out _))
        {
            Vector3 destination = roadPoint;

            if (_engine.NavigationManager.TryFindGroundBelow(roadPoint + Vector3.up * 4f, 4f, 6f, out float groundY, out _))
            {
                destination.y = groundY;
            }

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' hit a dead end - heading for the closest road instead of idling ({Vector3.Distance(npc.transform.position, roadPoint):F0}m away).");

            StartLongDistanceWalkDirect(
                survivor,
                destination,
                onArrived: () => ContinueLootTask(survivor, new LootTaskState()),
                onFailed: () => StartLootForResourcesTask(survivor));
            return;
        }

        // No road within reach either - the only real dead end left, so
        // fall back to a fresh gear-weighted roll rather than idling
        // regardless (that roll's own Local-tier option still works even
        // with zero road/monument data nearby).
        StartLootForResourcesTask(survivor);
    }
}
