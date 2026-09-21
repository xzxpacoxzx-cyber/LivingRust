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
/// Monument-aware looting (2026-08-15), Lucas's own explicit framing:
/// rather than hardcoding a fixed enter-here/finish-there path through a
/// monument (the MonumentRoutes.cs approach, built for climbing specific
/// authored structures like Powerline_A), this "highlights" loose areas
/// worth checking for loot within a monument type - auto-detected by
/// clustering real StorageContainer positions, not manually authored. A
/// bot given one of these zones just knows "there's usually loot around
/// here," the same way a real player who's been to a warehouse before
/// remembers roughly where the crates tend to be without needing the exact
/// same walk-in-walk-out route every time.
///
/// Stored in each monument's own LOCAL space (relative to its transform,
/// same InverseTransformPoint trick LivingRust.MonumentAvoidZones.cs
/// already uses) and keyed by monument TYPE (MonumentInfo.name), not by
/// map-specific world position - Lucas's own explicit correction,
/// 2026-08-15: "a monument's coordinates will change varying upon each
/// wipe/reset... the bots should just pull the coordinates every time the
/// server starts to find where they are relative to the map they are on."
/// A zone scanned once against any instance of "warehouse" on any map
/// applies correctly to every warehouse on every future map/wipe, since
/// it's re-resolved to world space live via that instance's own transform
/// at use time, never a fixed coordinate.
///
/// Persisted to disk (unlike the self-learned, ephemeral avoid zones) -
/// this is deliberately-authored reference data (even though the
/// authoring itself is automatic, not manual), meant to survive a plugin
/// reload without needing to be re-scanned every time.
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

    /// <summary>
    /// How close two containers need to be to count as "the same cluster"
    /// during the auto-detect scan - real loot rooms tend to have several
    /// containers within a few metres of each other (a locker + 2 crates
    /// in one room, say), while genuinely separate rooms/areas are much
    /// further apart. Deliberately generous enough to merge "this room's"
    /// containers into one zone rather than fragmenting every single
    /// crate into its own zone.
    /// </summary>
    private const float LootZoneClusterDistance = 12f;

    /// <summary>
    /// Hard cap on how far apart two containers in the SAME cluster can be
    /// vertically (2026-08-15) - see the clustering loop's own doc comment
    /// for the real live bug this fixes. A generous ceiling for legitimate
    /// same-room variance (a container on a raised shelf/mezzanine ledge
    /// within one real room easily differs by 1-2m), but well under the
    /// real observed floor-to-roof gap this session's traces confirmed
    /// (~4.1-5.9m at Abandoned Supermarket specifically).
    /// </summary>
    private const float LootZoneMaxVerticalSpan = 3f;

    /// <summary>
    /// A cluster with fewer real containers than this is treated as noise
    /// (a single stray barrel out in a monument's yard, say) rather than a
    /// real "loot area" worth remembering - Lucas's own framing was
    /// "highlighting specific areas," which implies genuine hotspots, not
    /// every single container individually.
    /// </summary>
    private const int LootZoneMinClusterSize = 2;

    /// <summary>
    /// Extra margin added on top of a cluster's own real container spread
    /// when computing its stored Radius - covers containers that spawn
    /// just outside the exact scanned positions (procedural placement
    /// jitter) without needing a re-scan.
    /// </summary>
    private const float LootZoneRadiusMargin = 4f;

    /// <summary>
    /// Fallback scan radius for monuments whose real Bounds field is
    /// degenerate/zero (some monument types never set it meaningfully) -
    /// generous enough to cover any real monument footprint in the game
    /// without accidentally sweeping in an unrelated neighbouring
    /// structure.
    /// </summary>
    private const float MonumentLootScanFallbackRadius = 80f;

    /// <summary>
    /// Live 75-bot test (2026-08-15) showed a real pileup: every survivor
    /// heading to the same MonumentLootZone walks to the exact same fixed
    /// LocalOffset point, so whenever several bots pick the same zone at
    /// once (easy at a small monument like Ranch/Stables with only one
    /// cluster) they all converge on the identical coordinate and jam each
    /// other - confirmed via trace, 'SneakyBoomer' cycling "another
    /// player/bot is in the way" against the same waypoint for seconds
    /// straight, plus a screenshot of ~8 bots stacked in one spot outside
    /// Ranch. JitterZoneDestination spreads each bot's actual walk target
    /// across the zone's own footprint instead of one shared point - capped
    /// well under the zone's Radius so it still lands among the zone's real
    /// containers.
    /// </summary>
    private const float LootZoneJitterFraction = 0.6f;

    /// <summary>
    /// How long a task stays committed to actively working a monument it's
    /// started checking, before it's allowed to give up on that monument's
    /// zones and escalate away to road-following (2026-08-15) - Lucas's own
    /// explicit numbers and reasoning: without this, a survivor that
    /// exhausted its currently-KNOWN zones (not necessarily every real
    /// container the monument actually has - zones get discovered
    /// incrementally) immediately bailed to the road, which read as
    /// arriving somewhere significant only to instantly consider leaving.
    /// Ranges scale with tier since a High-tier monument's zones are spread
    /// across a much larger structure than a Low-tier one's. Purely a
    /// MINIMUM commitment, not a hard cap - a survivor that's still finding
    /// real zones to check past its own deadline keeps going, the deadline
    /// only matters once EVERY known zone comes up empty (see its use in
    /// EscalateSearchToMonumentZone).
    /// </summary>
    private const float LowTierMonumentDwellMinSeconds = 180f;
    private const float LowTierMonumentDwellMaxSeconds = 600f;
    private const float MediumTierMonumentDwellMinSeconds = 480f;
    private const float MediumTierMonumentDwellMaxSeconds = 1200f;
    private const float HighTierMonumentDwellMinSeconds = 1200f;
    private const float HighTierMonumentDwellMaxSeconds = 1800f;

    /// <summary>
    /// Wider range check for a monument the survivor has ALREADY committed
    /// to working this task (2026-08-15, Lucas's own explicit request) -
    /// MonumentLootZoneDetectionRadius (60m) is measured from the
    /// monument's own transform ORIGIN, a single point that can sit far
    /// from wherever the survivor currently is inside a genuinely large
    /// complex (Launch Site being the extreme case, but Lucas's own
    /// estimate is most monuments other than it stay under ~200m across
    /// anyway). Without this, a survivor working the far side of a big
    /// monument could wander past 60m from that one origin point, read as
    /// "no monument nearby" by the generic check, and prematurely bail to
    /// road-following mid-commitment even though it never actually left.
    /// Deliberately only widens the range for a monument ALREADY committed
    /// to (matched by name, not "whichever's nearest") - the initial "is
    /// there a monument worth engaging at all" discovery decision still
    /// uses the tighter 60m gate, this doesn't make far-off monuments
    /// suddenly eligible for a survivor just wandering the open world.
    /// </summary>
    private const float CommittedMonumentRangeRadius = 150f;

    private bool TryGetCommittedMonument(Vector3 position, LootTaskState state, out MonumentInfo monument)
    {
        monument = null;

        if (state.CommittedMonumentName == null || TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null)
        {
            return false;
        }

        foreach (MonumentInfo candidate in TerrainMeta.Path.Monuments)
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
            // TierZero and TierOne both share the same low-commitment dwell
            // bucket - no separate TierZero numbers requested (2026-08-19).
            _ => UnityEngine.Random.Range(LowTierMonumentDwellMinSeconds, LowTierMonumentDwellMaxSeconds),
        };
    }

    /// <summary>
    /// Real live bug found (2026-08-15) - traced 'LuckyStalker15' at
    /// supermarket_1: after the Warp() fix confirmed the AGENT's own
    /// seating problem was solved (isOnNavMesh=True this time, unlike the
    /// earlier SneakyRanger trace), native pathing STILL failed - this
    /// time because the DESTINATION itself was 4.32m from the nearest real
    /// navmesh point. Root cause: unlike GetApproachPoint (which now both
    /// resolves real ground height via TryFindGroundBelow AND snaps onto
    /// the navmesh via SnapApproachPointToNavMesh), this function did
    /// neither - a pure X/Z geometric offset around the zone's own raw
    /// LocalOffset Y, with zero awareness of real ground height or navmesh
    /// coverage. A zone whose real containers span different heights (e.g.
    /// a shelf display item above a floor-level crate) computes a centroid
    /// Y that isn't real floor height at all, and the jitter could then
    /// land the actual walk target on/inside/above a shelf instead of the
    /// walkable aisle floor beside it - exactly what stranded
    /// LuckyStalker15 (destination sampled at Y=29.41, a shelf/mezzanine
    /// height, versus the real floor's ~23.8-23.9 confirmed via
    /// /lr.debug.scan earlier the same session). Now takes npc so it can
    /// resolve real ground height (same TryFindGroundBelow pattern
    /// ComputeApproachPoint already uses) THEN snap onto real navmesh
    /// (SnapApproachPointToNavMesh, LivingRust.Looting.cs) before ever
    /// being handed to StartWalking - the same two-step validation
    /// GetApproachPoint now does for every container/corpse/bag/dropped-
    /// item/recycler approach point, applied here too.
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

        // Diagnostic (2026-08-15) - isolating which of the two steps
        // (ground-probe vs navmesh-snap) is responsible whenever a jittered
        // zone destination ends up somewhere clearly elevated, rather than
        // guessing again. See TryFindGroundBelow's own NonSteppableColliderNames
        // fix - this logs specifically so a THIRD unexcluded compound
        // collider (or SnapApproachPointToNavMesh's wide 20m fallback
        // grabbing a disconnected elevated navmesh island) can be told
        // apart from each other.
        if (Mathf.Abs(snapped.y - zoneWorldPosition.y) > LootVerticalReachLimit)
        {
            Puts($"jitterzone-diag: zone base {zoneWorldPosition} -> jittered XZ {jittered} (ground-probe succeeded={groundProbeSucceeded}, groundY={groundY:F2}, collider '{(groundCollider != null ? groundCollider.gameObject.name : "none")}') -> after SnapApproachPointToNavMesh = {snapped} (delta {snapped.y - zoneWorldPosition.y:F2}m).");
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
    /// Called once from OnServerInitialized (2026-08-15) - scans every
    /// DISTINCT monument type currently present on this map/wipe right
    /// away, rather than waiting for some bot's own loot search to
    /// stumble onto each one lazily over time (EscalateSearchToMonumentZone
    /// still exists and still self-heals any monument type this somehow
    /// missed, e.g. one added by a separate plugin after boot). Lucas's
    /// own explicit framing: "replicates what a player does - check the
    /// map, what monuments exist... there is no advantage/disadvantage to
    /// knowing what monuments exist on the map at all," since real loot
    /// SPAWN LOCATIONS never move, only whether a given crate happens to
    /// be populated at the moment a player/bot actually opens it.
    ///
    /// Multiple real instances of the same monument type on one map (two
    /// separate "harbor_1" placements, say) collapse to a single scan -
    /// same prefab means the same interior layout, so scanning one
    /// representative instance is exactly as informative as scanning both,
    /// at a fraction of the boot cost. A monument type already known from
    /// a PREVIOUS session's persisted data (LoadMonumentLootZones, called
    /// just before this) is skipped entirely - its local-space zones are
    /// still structurally valid on this map/wipe regardless of where this
    /// specific instance actually landed (see this file's own top-level
    /// doc comment), so there's nothing new a re-scan would find.
    /// </summary>
    private void ScanAllMonumentsOnBoot()
    {
        if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null)
        {
            Puts("WARNING: TerrainMeta.Path.Monuments isn't available yet - skipping the boot-time monument loot scan.");
            return;
        }

        HashSet<string> seenTypesThisScan = new();
        int scannedCount = 0;
        int skippedAlreadyKnownCount = 0;

        foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
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
    /// Per-monument-TYPE override (2026-08-15, Lucas's own explicit call
    /// after a real live-traced session at Abandoned Supermarket) capping
    /// how far above the monument's own base height (monument.transform.
    /// position.y) a container is allowed to be before it's excluded from
    /// the zone scan ENTIRELY - not a per-selection runtime check like
    /// EscalateSearchToMonumentZone's own height gate (which still lets a
    /// bot ALREADY elevated loot up there), this stops the zone from ever
    /// existing in the data at all. Deliberately scoped to specific
    /// monument TYPES, not a blanket rule for every monument - several
    /// Medium/High tier monuments have genuinely reachable multi-floor
    /// interiors with real stairs, and a universal cap would wrongly
    /// exclude legitimate loot there. Abandoned Supermarket specifically
    /// has a confirmed real gap: real rooftop containers, only reachable
    /// via a ladder on the far side of the building from where the
    /// loot actually sits, PLUS a genuine phantom navmesh connection
    /// (native pathing finds a "valid" route straight through solid
    /// floor/ceiling at one specific vent coordinate, confirmed via live
    /// trace - not a real climbable path at all) - between the two, every
    /// bot that ever targeted the roof there got stuck or exploited a
    /// bug to reach it. Matched by monument name substring, same pattern
    /// as every other monument-name list in this project.
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
    /// Runs the auto-detect scan against one monument instance and stores
    /// the result under its monument TYPE (name) - overwrites any
    /// previous scan for that type, so re-running this after a container
    /// layout looks different (e.g. scanning a second instance of the
    /// same monument type for a sanity check) simply replaces the old
    /// data rather than merging with it. Reuses the exact same lootability
    /// filters (IsVehicleFuelStorage/IsHotAirBalloonStorage/etc) the
    /// normal open-world search already applies, so a zone never gets
    /// built around containers a bot wouldn't actually loot anyway.
    /// </summary>
    private List<MonumentLootZone> ScanMonumentForLootZones(MonumentInfo monument)
    {
        float scanRadius = Mathf.Max(monument.Bounds.extents.x, monument.Bounds.extents.z, MonumentLootScanFallbackRadius / 2f) + 15f;

        bool hasHeightCap = TryGetMonumentZoneMaxHeight(monument.name, out float maxHeightAboveBase);

        // NOT monument.transform.position.y - a monument prefab's pivot is
        // frequently NOT at real ground level (confirmed live 2026-08-15:
        // a zone at Y=37.48 still passed this monument's 3f height cap,
        // even though every ground-probe scan all session long put real
        // floor height here at ~31.4 - meaning the prefab's own pivot sits
        // several metres above the actual walkable floor). Same real-
        // ground-probe pattern JitterZoneDestination/ComputeApproachPoint
        // already use, rather than trusting the pivot to mean anything
        // about height above the floor.
        float monumentBaseY = monument.transform.position.y;
        bool groundProbeSucceeded = _engine.NavigationManager.TryFindGroundBelow(monument.transform.position + Vector3.up * 4f, 4f, 6f, out float realMonumentGroundY, out Collider monumentGroundCollider);

        if (groundProbeSucceeded)
        {
            monumentBaseY = realMonumentGroundY;
        }

        if (hasHeightCap)
        {
            Puts($"scanmonumentloot-diag: '{monument.name}' pivot Y={monument.transform.position.y:F2}, ground-probe succeeded={groundProbeSucceeded}, resolved monumentBaseY={monumentBaseY:F2} (collider '{(monumentGroundCollider != null ? monumentGroundCollider.gameObject.name : "none")}'), cap={maxHeightAboveBase:F1}m -> excluding anything above Y={monumentBaseY + maxHeightAboveBase:F2}.");
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

        // Simple single-link clustering: each container joins the nearest
        // existing cluster within LootZoneClusterDistance of its own
        // current running centroid, or starts a new one - deliberately
        // basic (no k-means/proper DBSCAN), real monument loot rooms are
        // small and dense enough that this converges to sensible clusters
        // without needing anything fancier.
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

                // Real single-room loot clusters don't span a genuine
                // floor-to-roof/mezzanine gap (2026-08-15, real live bug
                // traced across 3 separate bots at Abandoned Supermarket: a
                // 14m-radius, 7-container zone - nearly double every other
                // zone's radius - turned out to chain-link ground-floor
                // containers to rooftop ones through intermediate
                // stepping-stone containers, each individually within
                // LootZoneClusterDistance of the last even though the
                // ENDS were on completely separate, only ladder/jump-
                // connected levels. The resulting centroid landed
                // somewhere no bot could walk to, stranding every one that
                // tried. This is a purely VERTICAL check, independent of
                // the full-3D distance gate above, so a horizontally close
                // but vertically separate container can't sneak in just by
                // being near the centroid in X/Z.
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
            monument = TerrainMeta.Path.Monuments
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

    // Early-game handicap (2026-09-21, Lucas's own explicit spec): finishing
    // the loot route through an Abandoned Supermarket or Oxum's Gas Station
    // hands the survivor a green keycard and one fuse regardless of what it
    // actually found, so it can start on the green-card puzzle monuments
    // (Harbor and so on) instead of waiting on luck. Tops up rather than
    // stacks - it only gives what the survivor doesn't already hold, so
    // repeat visits can't farm cards.
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
    /// First real pass at monument-aware looting (2026-08-15) - Lucas's
    /// own explicit scope-setting: getting a bot to reliably navigate
    /// INTO an arbitrary detected zone (through whatever doorway/stairwell
    /// a specific monument actually has) needs real per-monument
    /// hardcoding/navmesh testing over time, not something to solve in one
    /// pass. Walks to the single nearest zone's centre (native/hand-built
    /// movement, same as every other walk in this project), then - as of
    /// the SnapApproachPointToNavMesh fix (2026-08-15, later same session) -
    /// hard-commits to that exact monument (LootTaskState.CommittedMonumentName,
    /// a 1-hour dwell deadline, forceLocalScan) instead of handing off to
    /// StartLootForResourcesTask. That old handoff re-rolled the full
    /// gear-weighted destination system on arrival, meaning this command
    /// could walk a bot to a monument only for it to immediately roll away
    /// somewhere else - useless for repeatable in-monument navigation
    /// testing, which is exactly what this command exists for now (Lucas's
    /// own explicit ask: a fast way to test the navmesh-snap fix without
    /// waiting on the natural escalation ladder). The committed state means
    /// every ContinueLootTask cycle from here on stays local/zone-focused
    /// within THIS monument only - same mechanism /lr.debug.testrecycler
    /// already uses for the identical reason.
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

        // Same height sanity check EscalateSearchToMonumentZone applies for
        // real gameplay - this debug command previously skipped it
        // entirely (just nearest-by-raw-distance), which meant it kept
        // hard-committing bots to elevated zones during navigation testing
        // even after the real height gate/cap fixes landed, since this
        // command never calls that function at all.
        //
        // No longer falls back to the nearest zone regardless of height
        // (2026-08-16) - confirmed live this was actively harmful: a
        // monument instance whose EVERY cached zone reprojects elevated
        // (the same cross-instance LocalOffset issue chased most of this
        // session) silently defeated the height check every time via this
        // exact fallback, hard-committing to a known-bad target anyway.
        // Real gameplay's EscalateSearchToMonumentZone doesn't fall back
        // like this either - it correctly gives up and tries the road
        // instead. Matching that honest behavior here: report nothing
        // reachable rather than walk into a target this same command just
        // proved (via the height check above) is too high to trust.
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
                // Real live bug (2026-08-15) - the original plain StartWalking
                // call here had NO onFailed handler at all, so a failed
                // initial walk left the survivor's CurrentTask permanently
                // stuck on LootForResources with nothing driving it - no
                // retry, no fallback, genuinely stuck forever. Switched to
                // StartWalkingWithRecovery (the real wiggle/nudge/emergency-
                // teleport ladder every other production walk in this
                // project gets) instead of plain StartWalking, so this test
                // command's own initial approach gets the same resilience
                // a real bot's walk would - not just a bare give-up.
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

    /// <summary>
    /// Real component shortnames, confirmed via live loot logs this same
    /// session (e.g. '66BluntDiver' looting a corpse: metalblade,
    /// metalpipe, sheetmetal, semibody, techparts, smgbody, tarp,
    /// metalspring, sewingkit, roadsigns) - all genuine Resources/Component
    /// category items with real Blueprints, so every one of them qualifies
    /// as IsRecycleFodder without needing to guess at the item database.
    /// </summary>
    private static readonly string[] TestRecyclerComponentShortnames =
    {
        "metalblade", "metalpipe", "sheetmetal", "semibody", "techparts",
        "smgbody", "tarp", "metalspring", "sewingkit", "roadsigns", "gears",
    };

    // Purely a termination guarantee against a bad/unresolvable shortname
    // spinning IsContainerFull's while loop forever - same "near-impossible
    // but not actually impossible" reasoning as every other safety cap in
    // this project (MaxDistanceDecisionRerolls, MaxRecycleRoundsPerVisit).
    private const int TestRecyclerMaxFillAttempts = 200;

    /// <summary>
    /// Debug-only stress test for the full-inventory -> recycler pipeline
    /// (2026-08-15, Lucas's own explicit simplification of the original
    /// "loot an entire monument to fill up naturally" version - "let's just
    /// have a bot spawn in specifically for this as a test with a full
    /// inventory of random components"). Spawns a completely fresh survivor
    /// at the admin's aim point (same placement as /lr.spawn), stuffs its
    /// main inventory with random real component stacks until genuinely
    /// full, then hands off to the real, unmodified StartLootForResourcesTask.
    /// No monument targeting, no zone walking needed at all - ContinueLootTask's
    /// own IsContainerFull(main) check fires on its very first cycle since
    /// the inventory's already full the instant the task starts, sending
    /// the survivor straight to TryStartRecyclingTask exactly as it would
    /// in production once a real bot fills up organically. Whatever
    /// recycler is nearest within RecyclerSearchRadius (400m) of wherever
    /// the survivor spawns gets used - stand near one before running this.
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

    /// <summary>
    /// How close a survivor needs to be to a monument's own transform for
    /// its zones to even be considered - same value/reasoning as
    /// MonumentAvoidZones.cs's own AvoidZoneMonumentSearchRadius (a real
    /// monument footprint is well within this), kept as its own separate
    /// constant since the two systems are conceptually independent even
    /// though they happen to agree on this number.
    /// </summary>
    private const float MonumentLootZoneDetectionRadius = 60f;

    /// <summary>
    /// Called from ContinueLootTask once its normal radius scan comes up
    /// completely empty (2026-08-15) - the live, automatic counterpart to
    /// the manual /lr.debug.scanmonumentloot + /lr.debug.lootmonument
    /// commands. Lucas's own explicit framing: "can the bot do a scan as
    /// it enters [a monument]? That way it 'knows' where the loot is...
    /// realistically this isn't a disadvantage to players because players
    /// learn the monuments." A survivor that wanders near ANY monument -
    /// scanned before or not - discovers/reuses its loot zones on the fly
    /// here, with zero manual setup required; the debug commands remain
    /// useful for pre-seeding a monument's zones before a bot ever visits,
    /// or for inspecting what got detected, but are no longer the only way
    /// zones get built.
    ///
    /// Falls through to EscalateSearchAlongRoad (the previous sole
    /// escalation) whenever there's no monument nearby, the monument has
    /// no detectable zones (auto-scanned fresh here if never scanned
    /// before), or every zone in it has already been visited THIS task -
    /// so a survivor genuinely not near any monument, or one that's
    /// finished checking every known zone, behaves exactly as it did
    /// before this existed.
    /// </summary>
    private void EscalateSearchToMonumentZone(Survivor survivor, LootTaskState state, bool isRetryAfterClear = false)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.None;
            return;
        }

        // Already committed to a specific monument this task - use the
        // wider CommittedMonumentRangeRadius (150m) check against that
        // EXACT monument by name, instead of the generic "nearest monument
        // within 60m" discovery gate, so wandering to a far corner of a
        // large complex doesn't read as having left it. Only applies once
        // a commitment already exists - see TryGetCommittedMonument's own
        // doc comment.
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

        if (!foundMonument || IsMonumentExcludedFromAutonomy(monument))
        {
            EscalateSearchAlongRoad(survivor, state);
            return;
        }

        // Opportunistic card-puzzle detour (2026-08-17) - see
        // TryStartCardPuzzleDetour's own doc comment (LivingRust.CardPuzzles.cs).
        // Checked ahead of the normal per-monument ghost route below since
        // it's a strictly narrower, higher-value detour (only ever attempted
        // if the survivor's already carrying the exact card+fuse it needs) -
        // returns true and takes over entirely the instant it commits, same
        // calling convention as the ghost-route block right after it.
        if (TryStartCardPuzzleDetour(survivor, monument, state))
        {
            return;
        }

        // Authored ghost route detour (2026-08-16) - see
        // MonumentGhostRouteFolders' own doc comment. A survivor genuinely
        // near this monument's own recorded trouble spot takes a real,
        // pre-validated recorded path through it instead of the generic
        // zone-walk/TryGetNextStep search below, exactly the way
        // KnownMonumentRoutes (LivingRust.MonumentRoutes.cs) already
        // substitutes an authored route for the Powerline tower's own
        // generic climb heuristic. Capped at once per monument per task
        // (GhostRouteVisitedMonuments) so finishing the route and resuming
        // ContinueLootTask nearby can't immediately re-trigger it in a loop;
        // a fresh task (new LootTaskState) is free to trigger it again on a
        // later visit.
        if (!state.GhostRouteVisitedMonuments.Contains(monument.name)
            && TryGetGhostRouteForMonument(monument.name, out string ghostRouteFilePath)
            && TryLoadTraceWaypoints(ghostRouteFilePath, out List<GhostRouteWaypoint> ghostWaypoints, out MonumentInfo recordedAtMonument, monument.name)
            && ghostWaypoints.Count > 0)
        {
            // Re-project onto THIS specific monument instance - monument is
            // already the real instance the survivor is actually near
            // (found above), which may well be a different instance of the
            // same monument type than whichever one the route was recorded
            // at (see TryLoadTraceWaypoints' own doc comment - this is the
            // real fix for "what if there are 2 supermarkets"). Only
            // projects if the trace actually localized to a monument in the
            // first place; otherwise ghostWaypoints are already raw world
            // coordinates from the original recording.
            List<GhostRouteWaypoint> worldWaypoints = recordedAtMonument != null
                ? ProjectGhostRouteToMonument(ghostWaypoints, monument)
                : ghostWaypoints;

            state.GhostRouteVisitedMonuments.Add(monument.name);

            // 2026-08-18, real gap Lucas caught: this whole branch returns
            // before ever reaching the zone-scan code below that normally
            // sets CommittedMonumentName/CommittedMonumentDeadline - meaning
            // a ghost route used to leave the task's "committed monument"
            // untouched the entire time it ran. Once the route (and any
            // card-puzzle re-check after it) finished, ContinueLootTask's
            // own nearRealMonument check had nothing to fall back on except
            // the generic 60m MonumentLootZoneDetectionRadius search from
            // wherever the route happened to end - fine for a compact
            // monument, but ghost routes exist specifically FOR sprawling
            // ones where normal navigation struggles, so the route could
            // easily end well outside 60m of the monument's own transform
            // origin, silently resolving a different monument (or none) on
            // the very next escalation instead of ever re-checking THIS one
            // for a card puzzle picked up mid-route. Setting these here too
            // (same real dwell-time deadline the zone-scan branch already
            // uses) means the wider CommittedMonumentRangeRadius (150m)
            // check applies once the route hands back control, not just the
            // narrow generic one.
            state.CommittedMonumentName = monument.name;
            state.CommittedMonumentDeadline = UnityEngine.Time.realtimeSinceStartup + GetMonumentDwellSeconds(monument.name);

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' is heading for '{monument.name}''s known-hard navigation stretch - running to the recorded route's start ('{ghostRouteFilePath}') before taking it.");
            Puts($"ghostroute: '{survivor.Character.Alias}' committed to a ghost route through '{monument.name}' via '{ghostRouteFilePath}' - walking there first.");

            // Real, collision-respecting walk to the route's own first
            // waypoint (2026-08-16, Lucas's own explicit correction:
            // "it actually has to run to the location and then starts the
            // hardcoded path from the beginning to end" - not phase straight
            // there from wherever it currently is). StartGhostRoute's own
            // phase-through movement only ever starts from THIS arrival
            // point now, not the survivor's original position, so the
            // collision bypass is scoped to exactly the recorded interior
            // stretch it exists for - the same StartLongDistanceWalk used
            // for every other real monument-zone approach elsewhere in this
            // function handles the actual approach, barricades and all.
            Action onGhostRouteComplete = () =>
            {
                GrantEarlyProgressionKit(survivor, monument.name);
                ContinueLootTask(survivor, state);
            };

            // Registered here too, not just inside StartGhostRoute itself
            // (2026-08-16, real live bug: a bot committed to a ghost route,
            // then got pulled into combat DURING this exact approach walk,
            // before StartGhostRoute ever ran even once - _pendingGhostRouteToResume
            // had nothing recorded yet, so TryResumeGhostRoute found nothing
            // to resume once the fight ended, and the whole commitment was
            // silently abandoned in favour of an unrelated fresh task - "the
            // bot went completely haywire and didn't follow the ghostroute
            // at all"). Index 0 here means an interruption during the
            // approach just retries the SAME approach walk, exactly as if
            // nothing had happened - StartGhostRoute overwrites this with
            // real progress the moment it actually starts moving through
            // the route itself.
            _pendingGhostRouteToResume[survivor.Character.Id] = (worldWaypoints, 0, onGhostRouteComplete);

            // See StartGhostRouteLootScan's own doc comment - opportunistic
            // detours to real nearby loot while phasing through the route,
            // sharing the same 8-event budget combat detours already use.
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
                    EscalateSearchAlongRoad(survivor, state);
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

        // A different monument than whichever this task last escalated
        // toward (or the first escalation this task at all) - start the
        // per-task visited-zone tracking fresh rather than carrying over
        // indices that belong to a completely different zone list.
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

            // A zone confirmed unreachable (see the onFailed handler below)
            // gets treated exactly like an already-visited one - live trace
            // found a bot ('SilentHunter', power_sub_small_2) looping this
            // whole escalation forever because a fresh LootTaskState wipes
            // VisitedMonumentZoneIndices, and the zone's real containers
            // sitting right at a shoreline with no reachable navmesh meant
            // every single fresh task re-picked the identical doomed zone.
            if (IsInMonumentAvoidZone(candidateWorldPosition))
            {
                continue;
            }

            // Same proactive height gate as IsUnreachableCrateOrBarrel/
            // IsUnreachableDroppedItem (2026-08-15) - real live evidence:
            // a genuinely rooftop-only zone at Abandoned Supermarket
            // (Y=27.99, real navmesh up there, just structurally
            // disconnected from ground level) kept getting selected as a
            // target by FOUR separate bots in a row ('LuckyStalker15',
            // 'TwistedDiver3', '3RaggedBuilder', 'GrungyHermit', all
            // hitting the identical coordinate to 2 decimal places) despite
            // the durable avoid-zone system existing - each bot's own
            // stuck-recovery/hand-built stepping produced enough real,
            // different-looking symptoms downstream (doorway freezes,
            // "body would overlap solid geometry," repath floods) that it
            // read as a NEW bug every time rather than the same root cause.
            // The navmesh-snap fix (SnapApproachPointToNavMesh) alone can't
            // catch this - the rooftop genuinely HAS real navmesh, it's
            // just not reachable from here, which a plain "does navmesh
            // exist nearby" check can't distinguish from "is this
            // navmesh connected to where I'm standing." Rejecting the
            // candidate proactively, before it's ever picked, is far more
            // reliable than waiting for repeated failures to accumulate
            // into a permanent avoid-zone record.
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
            // Every known zone's already been checked this task (or there
            // simply aren't any) - but if this monument is still within its
            // own minimum commitment window (see GetMonumentDwellSeconds),
            // that's not actually a dead end yet. Re-open every zone and
            // patrol them again rather than immediately bailing to the
            // road - real containers can still turn up (state.Visited is
            // per-CONTAINER, not per-zone, so anything genuinely already
            // looted stays excluded; this only re-opens the zone
            // CENTRE-POINTS as places worth walking back to and
            // re-scanning). zones.Count > 0 guards against looping forever
            // on a monument that never had any real zones to begin with.
            // isRetryAfterClear (2026-08-16, real live bug found via
            // Carbon.Core.log - a bot's own recursive call stack 974 frames
            // deep, freezing the whole server for the entire synchronous
            // call chain) caps this to exactly ONE retry per call instead of
            // recursing without limit. Clearing VisitedMonumentZoneIndices
            // only helps when every zone got skipped for having ALREADY
            // been visited this cycle - if every zone is instead being
            // rejected by the avoid-zone or height-gate checks above (a
            // real, permanent reason, not "already checked"), clearing that
            // set changes nothing and nextZone comes back null again on the
            // very next call, forever. One retry still gets the intended
            // "patrol zones again" behaviour for the visited-only case;
            // anything still empty after that genuinely has nothing left to
            // offer and falls through to the road fallback below instead.
            if (!isRetryAfterClear && zones.Count > 0 && state.CommittedMonumentName == monument.name && UnityEngine.Time.realtimeSinceStartup < state.CommittedMonumentDeadline)
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' checked every known zone in '{monument.name}' but is still committed to it for {state.CommittedMonumentDeadline - UnityEngine.Time.realtimeSinceStartup:F0}s more - patrolling its zones again instead of leaving.");
                state.VisitedMonumentZoneIndices.Clear();
                EscalateSearchToMonumentZone(survivor, state, isRetryAfterClear: true);
                return;
            }

            // Either no zones at all (a monument too sparse to cluster
            // anything) or the commitment window's expired - nothing more
            // this system can offer, hand off to the pre-existing
            // road-following escalation same as always.
            state.CommittedMonumentName = null;
            EscalateSearchAlongRoad(survivor, state);
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
    /// The genuine last resort in the whole loot escalation ladder
    /// (2026-08-15) - called only once EscalateSearchAlongRoad has fully
    /// exhausted itself (normal hop progression AND the 800m "any road at
    /// all" fallback). Deliberately placed last, not earlier - Lucas's own
    /// explicit reasoning: monuments carry real risk (radiation, other
    /// players) worth reserving for a genuine "nothing safer/cheaper
    /// worked" situation rather than reaching for eagerly, and the trip
    /// itself is a real commitment (potentially a long walk, fully exposed
    /// the whole way) that shouldn't be undertaken lightly.
    ///
    /// Only ever considers monuments this project already has zone data
    /// for (_monumentLootZones) - a monument nobody's scanned yet has no
    /// known loot to justify the trip, so it's not a candidate here (it
    /// would still get discovered organically via
    /// EscalateSearchToMonumentZone if a survivor happened to wander near
    /// it some other way). Capped at one attempt per task
    /// (state.TraveledToDistantMonument) so a monument whose zones also
    /// turn out to be exhausted/unreachable doesn't chain into an
    /// unbounded sequence of further monument trips - one genuine long
    /// shot, then real done, same as the give-up this replaces.
    ///
    /// No risk-awareness yet (radiation protection, ammo/health reserves) -
    /// Lucas's own explicit scoping: "we can hook other 'requirements' per
    /// monument later." This unconditionally commits to the trip once
    /// triggered.
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

        if (_monumentLootZones.Count == 0 || TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' found nothing left to loot nearby, no road to follow, and no known monument to head for either - rolling for something new instead of giving up.");
            NeverIdleFallback(survivor, state);
            return;
        }

        MonumentInfo nearestKnownMonument = null;
        MonumentLootZone nearestZone = null;
        Vector3 nearestZoneWorldPosition = Vector3.zero;
        float nearestDistance = float.MaxValue;

        foreach (MonumentInfo candidate in TerrainMeta.Path.Monuments)
        {
            if (candidate == null || IsMonumentExcludedFromAutonomy(candidate) || !_monumentLootZones.TryGetValue(candidate.name, out List<MonumentLootZone> zones) || zones.Count == 0)
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

        // Walks straight to the nearest known ZONE's world position, not
        // just the monument's own transform origin - that's an arbitrary
        // anchor point (not necessarily near real loot, or even walkable),
        // where a zone is a real container cluster's centroid, exactly
        // like every other zone-targeted walk in this file already does.
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
    /// Replaces the old outright give-up (CurrentTask = TaskType.None) at
    /// every genuine dead end in the escalation ladder (2026-08-15) -
    /// Lucas's own explicit call after watching bots stall out for long
    /// stretches: "I think if they reach [the end of the ladder] they
    /// should revert to rolling for a new monument or the closest road to
    /// them at that specific time. This should hopefully get bots never
    /// idling EVER." Rolls a coin between two genuinely different restarts
    /// rather than retrying the exact thing that just failed: a completely
    /// fresh gear-weighted task (the same TryStartWithGearWeightedDestination
    /// machinery a brand new spawn uses, which can land on a totally
    /// different tier/monument, not just the one just exhausted) or a walk
    /// to whichever road is nearest right now, resuming the ordinary local
    /// search from there once it arrives. Both branches make real forward
    /// progress, so this can't loop the same dead end forever the way
    /// re-picking the identical monument/zone would.
    ///
    /// Tries TryStartRecyclingTask FIRST, before either roll (2026-08-15,
    /// Lucas's own explicit second recycler trigger) - reaching this point
    /// means every known loot zone this task could find has genuinely been
    /// exhausted, which is exactly when converting whatever junk the
    /// survivor's still carrying into usable scrap/materials is worth a
    /// detour, independent of whether inventory happens to have room to
    /// spare. Only falls through to the reroll/road coin flip if recycling
    /// isn't actually viable (nothing worth recycling, or nothing
    /// reachable) - see TryStartRecyclingTask's own doc comment.
    /// </summary>
    private void NeverIdleFallback(Survivor survivor, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.None;
            return;
        }

        if (TryStartRecyclingTask(survivor))
        {
            return;
        }

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
