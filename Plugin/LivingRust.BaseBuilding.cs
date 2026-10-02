using LivingRust.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingRust.Models;
using LivingRust.Navigation;
using Oxide.Plugins;
using Rust;
using Rust.Ai.Gen2;
using UnityEngine;
using UnityEngine.AI;

namespace Carbon.Plugins;

/// <summary>
/// Replays a captured build trace CSV to construct a base at a new origin, reusing the shape,
/// order, and grades from the original recording.
/// </summary>
public partial class LivingRust
{
    // Caches resolved prefab paths per shortname so repeated pieces in a replay don't re-scan
    // the live entity list each time.
    private readonly Dictionary<string, string> _resolvedConstructionPrefabPaths = new();

    /// <summary>
    /// Finds a live entity matching the given ShortPrefabName and returns its full PrefabName
    /// asset path, for both construction pieces and deployables.
    /// </summary>
    // Combined Road|Roadside topology bitmask Rust uses to block construction near roads.
    private const int RoadTopologyMask = 0x80800;

    // Probe radius for a near-ground road-proximity check, approximating Rust's own placement check.
    private const float RoadProximityCheckRadius = 3f;

    /// <summary>
    /// Checks whether a position is too close to a road, matching Rust's own construction rule.
    /// Used as a pre-flight gate before a replay starts, since the replay bypasses the normal
    /// build pipeline and would otherwise place structures where a player never could.
    /// </summary>
    private bool IsTooCloseToRoad(Vector3 position)
    {
        TerrainTopologyMap topologyMap = TerrainMeta.TopologyMap;

        if (topologyMap == null)
        {
            return false;
        }

        int topology = topologyMap.GetTopology(position, RoadProximityCheckRadius);
        return (topology & RoadTopologyMask) != 0;
    }

    // Radius to sample terrain height around the origin and the max vertical delta allowed before
    // a site is considered too sloped. Since a replay translates all rows by one constant offset
    // without re-projecting onto new terrain, this only works well on ground roughly as flat as
    // where the original trace was recorded.
    private const float SlopeCheckSampleRadius = 4f;
    private const float MaxSlopeHeightDelta = 1.2f;

    /// <summary>
    /// Walks a survivor to a relocated build site with bounded stuck-recovery (wiggle, navmesh
    /// nudge, emergency teleport) that gives up cleanly instead of phasing through geometry as a
    /// last resort, unlike the shared recovery ladder used elsewhere in the project.
    /// </summary>
    private void WalkToBuildSiteWithRecovery(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed)
    {
        StartWalking(survivor, destination, onArrived, onFailed: () => RecoverTowardBuildSite(survivor, destination, onArrived, onFailed, tier: 0));
    }

    private void RecoverTowardBuildSite(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed, int tier)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            onFailed?.Invoke();
            return;
        }

        if (npc.IsWounded())
        {
            timer.Once(StuckReassessPause, () => RecoverTowardBuildSite(survivor, destination, onArrived, onFailed, tier));
            return;
        }

        FaceDirection(npc, destination - npc.transform.position);

        // If a tree or ore node is blocking the path, gather it to clear the way instead of just
        // wiggling/nudging/teleporting around it. Checked on every tier and resumes at the same
        // tier once cleared.
        if (_engine.NavigationManager.TryFindNearestTreeEntity(npc.transform.position, StuckResourceNodeDetectionRadius, out TreeEntity blockingTree, candidate => !IsInMonumentAvoidZone(candidate.transform.position) && !IsResourceNodePoisoned(candidate))
            && HasAnyGatherCapableTool(npc, TreeGatherToolPriority))
        {
            VerbosePuts($"'{survivor.Character.Alias}' is stuck right next to a live tree ('{blockingTree.ShortPrefabName}') on the way to a build site - gathering it before continuing.");

            StartGatheringResourceNode(
                survivor,
                blockingTree,
                TreeGatherToolPriority,
                onSuccess: () => WalkToBuildSiteWithRecovery(survivor, destination, onArrived, onFailed),
                onFailed: () => RecoverTowardBuildSite(survivor, destination, onArrived, onFailed, tier));

            return;
        }

        if (_engine.NavigationManager.TryFindNearestOreResourceEntity(npc.transform.position, StuckResourceNodeDetectionRadius, out OreResourceEntity blockingOre, candidate => !IsInMonumentAvoidZone(candidate.transform.position) && !IsResourceNodePoisoned(candidate))
            && HasAnyGatherCapableTool(npc, OreGatherToolPriority))
        {
            VerbosePuts($"'{survivor.Character.Alias}' is stuck right next to a live ore node ('{blockingOre.ShortPrefabName}') on the way to a build site - gathering it before continuing.");

            StartGatheringResourceNode(
                survivor,
                blockingOre,
                OreGatherToolPriority,
                onSuccess: () => WalkToBuildSiteWithRecovery(survivor, destination, onArrived, onFailed),
                onFailed: () => RecoverTowardBuildSite(survivor, destination, onArrived, onFailed, tier));

            return;
        }

        switch (tier)
        {
            case 0:
                timer.Once(StuckReassessPause, () =>
                {
                    BasePlayer reassessNpc = survivor.Player;

                    if (reassessNpc == null || reassessNpc.IsDestroyed)
                    {
                        onFailed?.Invoke();
                        return;
                    }

                    TryWiggleFree(survivor, destination, wiggled =>
                        StartWalking(survivor, destination, onArrived, onFailed: () => RecoverTowardBuildSite(survivor, destination, onArrived, onFailed, 1)));
                });
                return;

            case 1:
                timer.Once(StuckReassessPause, () =>
                {
                    BasePlayer reassessNpc = survivor.Player;

                    if (reassessNpc == null || reassessNpc.IsDestroyed)
                    {
                        onFailed?.Invoke();
                        return;
                    }

                    if (TryFindNavMeshNudgePoint(reassessNpc.transform.position, out Vector3 navMeshPoint)
                        && _engine.NavigationManager.TryCalculatePath(reassessNpc.transform.position, navMeshPoint, new RustNavMeshPath(), out _))
                    {
                        StartWalking(
                            survivor,
                            navMeshPoint,
                            onArrived: () => StartWalking(survivor, destination, onArrived, onFailed: () => RecoverTowardBuildSite(survivor, destination, onArrived, onFailed, 2)),
                            onFailed: () => RecoverTowardBuildSite(survivor, destination, onArrived, onFailed, 2));
                        return;
                    }

                    RecoverTowardBuildSite(survivor, destination, onArrived, onFailed, 2);
                });
                return;

            case 2:
                timer.Once(StuckReassessPause, () =>
                {
                    BasePlayer reassessNpc = survivor.Player;

                    if (reassessNpc == null || reassessNpc.IsDestroyed)
                    {
                        onFailed?.Invoke();
                        return;
                    }

                    if (TryEmergencyTeleport(survivor))
                    {
                        StartWalking(survivor, destination, onArrived, onFailed: () => RecoverTowardBuildSite(survivor, destination, onArrived, onFailed, 3));
                        return;
                    }

                    RecoverTowardBuildSite(survivor, destination, onArrived, onFailed, 3);
                });
                return;

            default:
                Puts($"basebuild-replay: '{survivor.Character.Alias}' couldn't reach the relocated build site through any real movement (wiggle/navmesh nudge/emergency teleport all failed) - giving up on this site rather than phasing through walls to reach it.");
                onFailed?.Invoke();
                return;
        }
    }

    /// <summary>
    /// Because a replay applies one constant offset to every row without re-adjusting for the new
    /// location's terrain, sloped sites can leave pieces too deep or too high. Gating the replay's
    /// start location on terrain flatness avoids starting somewhere this approach can't handle.
    /// </summary>
    // Design-aware terrain analysis checks how terrain sits relative to each foundation and the
    // ground outside each door, against where the design expects its floor to be.
    // Failed-site memory + reachability: a survivor that failed to walk to or build at a site
    // avoids it next time, and a candidate with no navmesh within BuildSiteNavMeshMaxDistance is
    // rejected up front instead of after a doomed walk.
    // Build-site reservations: once a bot commits to building somewhere, other bots keep clear of
    // it. A reservation is the spot a survivor has committed to build at, set once the site passes
    // every check; an inland survivor's rolled home target counts too. Lapses after
    // BuildSiteReservationLifetimeSeconds, or once the build finishes/fails or the owner dies.
    private const float BuildSiteReservationRadius = 30f;
    private const float BuildSiteReservationLifetimeSeconds = 900f;
    private readonly Dictionary<Guid, (Vector3 Position, float ExpiresAt)> _buildSiteReservations = new();

    private void ReserveBuildSite(Guid ownerId, Vector3 position)
    {
        _buildSiteReservations[ownerId] = (position, UnityEngine.Time.realtimeSinceStartup + BuildSiteReservationLifetimeSeconds);
    }

    private bool IsNearAnotherSurvivorsBuildSite(Vector3 candidate, Guid ownerId)
    {
        float now = UnityEngine.Time.realtimeSinceStartup;

        foreach (KeyValuePair<Guid, (Vector3 Position, float ExpiresAt)> reservation in _buildSiteReservations)
        {
            if (reservation.Key != ownerId
                && reservation.Value.ExpiresAt > now
                && Vector3.Distance(reservation.Value.Position, candidate) < BuildSiteReservationRadius)
            {
                return true;
            }
        }

        foreach (KeyValuePair<Guid, Vector3> target in _homeSiteTarget)
        {
            if (target.Key != ownerId && Vector3.Distance(target.Value, candidate) < BuildSiteReservationRadius)
            {
                return true;
            }
        }

        return false;
    }

    private const float FailedBuildSiteAvoidRadius = 30f;
    private const float BuildSiteNavMeshMaxDistance = 6f;
    private const int FailedBuildSiteMemory = 5;
    private readonly Dictionary<Guid, List<Vector3>> _failedBuildSites = new();

    private void RecordFailedBuildSite(Guid characterId, Vector3 site)
    {
        if (!_failedBuildSites.TryGetValue(characterId, out List<Vector3> sites))
        {
            sites = new List<Vector3>();
            _failedBuildSites[characterId] = sites;
        }

        sites.Add(site);

        if (sites.Count > FailedBuildSiteMemory)
        {
            sites.RemoveAt(0);
        }
    }

    private const float DesignFoundationClipTolerance = 1.2f;
    private const float DesignFoundationFloatTolerance = 3.0f;
    private const float DesignDoorRiseTolerance = 0.5f;
    private const float DesignDoorDropTolerance = 1.2f;
    private const float DesignDoorFarRiseTolerance = 1.0f;
    private const float DesignDoorFarDropTolerance = 1.8f;

    private sealed class DesignFootprint
    {
        public readonly List<Vector3> FoundationRel = new();
        public readonly List<(Vector3 Rel, bool Near)> DoorApronRel = new();
    }

    private readonly Dictionary<string, DesignFootprint> _designFootprintCache = new();

    private DesignFootprint GetDesignFootprint(string designPath)
    {
        if (_designFootprintCache.TryGetValue(designPath, out DesignFootprint cached))
        {
            return cached;
        }

        DesignFootprint footprint = null;

        try
        {
            List<BuildTraceRow> rows = ReadBuildTraceCsv(designPath);

            if (rows.Count > 0)
            {
                footprint = new DesignFootprint();
                Vector3 traceOrigin = rows[0].Position;
                Vector3 sum = Vector3.zero;

                foreach (BuildTraceRow row in rows)
                {
                    if (row.EventType == "build" && (row.Shortname == "foundation" || row.Shortname == "foundation.triangle"))
                    {
                        Vector3 rel = row.Position - traceOrigin;
                        footprint.FoundationRel.Add(rel);
                        sum += rel;
                    }
                }

                Vector3 centroid = footprint.FoundationRel.Count > 0 ? sum / footprint.FoundationRel.Count : Vector3.zero;
                string designDir = Path.GetDirectoryName(designPath)?.Replace('\\', '/') ?? string.Empty;
                string doorsFolder = $"{designDir}/{Path.GetFileNameWithoutExtension(designPath)}_doors";

                if (Directory.Exists(doorsFolder))
                {
                    foreach (string routeFile in Directory.GetFiles(doorsFolder, "*.csv"))
                    {
                        if (TryReadDoorRouteEndpoints(routeFile, out Vector3 first, out Vector3 last))
                        {
                            bool firstIsOutside = Vector2.Distance(new Vector2(first.x, first.z), new Vector2(centroid.x, centroid.z))
                                > Vector2.Distance(new Vector2(last.x, last.z), new Vector2(centroid.x, centroid.z));
                            Vector3 outside = firstIsOutside ? first : last;
                            Vector3 inside = firstIsOutside ? last : first;
                            Vector3 direction = new Vector3(outside.x - inside.x, 0f, outside.z - inside.z);

                            if (direction.sqrMagnitude < 0.01f)
                            {
                                continue;
                            }

                            direction.Normalize();
                            footprint.DoorApronRel.Add((outside, true));
                            footprint.DoorApronRel.Add((outside + direction * 1.5f, true));
                            footprint.DoorApronRel.Add((outside + direction * 3f, false));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Puts($"basebuild-site: couldn't analyse design footprint for '{designPath}' ({ex.Message}) - falling back to the flat slope check.");
        }

        _designFootprintCache[designPath] = footprint;
        return footprint;
    }

    private static bool TryReadDoorRouteEndpoints(string path, out Vector3 first, out Vector3 last)
    {
        first = last = Vector3.zero;
        string[] lines = File.ReadAllLines(path);

        if (lines.Length < 3)
        {
            return false;
        }

        string[] header = lines[0].Split(',');
        int xi = Array.IndexOf(header, "x");
        int yi = Array.IndexOf(header, "y");
        int zi = Array.IndexOf(header, "z");

        if (xi < 0 || yi < 0 || zi < 0)
        {
            return false;
        }

        bool Parse(string line, out Vector3 v)
        {
            string[] parts = line.Split(',');
            v = Vector3.zero;

            return parts.Length > Math.Max(xi, Math.Max(yi, zi))
                && float.TryParse(parts[xi], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v.x)
                && float.TryParse(parts[yi], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v.y)
                && float.TryParse(parts[zi], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v.z);
        }

        return Parse(lines[1], out first) && Parse(lines[^1].Length > 0 ? lines[^1] : lines[^2], out last);
    }

    /// <summary>
    /// True (with a reason) if terrain around candidate would clip a
    /// foundation, leave one floating, or bury / drop away from a door.
    /// Falls back to the old flat slope check if the design can't be read.
    /// </summary>
    private bool IsTerrainUnsuitableForDesign(Vector3 candidate, string designPath, out string reason)
    {
        reason = null;
        TerrainHeightMap heightMap = TerrainMeta.HeightMap;

        if (heightMap == null)
        {
            return false;
        }

        DesignFootprint footprint = GetDesignFootprint(designPath);

        if (footprint == null || footprint.FoundationRel.Count == 0)
        {
            if (IsTooSlopedToBuild(candidate))
            {
                reason = "standing on too much of a slope";
                return true;
            }

            return false;
        }

        float baseHeight = heightMap.GetHeight(candidate);

        foreach (Vector3 rel in footprint.FoundationRel)
        {
            float floor = baseHeight + rel.y;

            foreach (Vector2 corner in new[] { Vector2.zero, new Vector2(1.4f, 0f), new Vector2(-1.4f, 0f), new Vector2(0f, 1.4f), new Vector2(0f, -1.4f) })
            {
                float terrain = heightMap.GetHeight(new Vector3(candidate.x + rel.x + corner.x, 0f, candidate.z + rel.z + corner.y));

                if (terrain - floor > (DesignFoundationClipTolerance * _terrainRelaxFactor))
                {
                    reason = $"terrain would clip a foundation ({terrain - floor:F1}m above its floor)";
                    return true;
                }

                if (floor - terrain > (DesignFoundationFloatTolerance * _terrainRelaxFactor))
                {
                    reason = $"a foundation would float ({floor - terrain:F1}m above terrain)";
                    return true;
                }
            }
        }

        foreach ((Vector3 rel, bool near) in footprint.DoorApronRel)
        {
            float floor = baseHeight + rel.y;
            float terrain = heightMap.GetHeight(new Vector3(candidate.x + rel.x, 0f, candidate.z + rel.z));
            float rise = terrain - floor;

            if (rise > (near ? (DesignDoorRiseTolerance * _terrainRelaxFactor) : (DesignDoorFarRiseTolerance * _terrainRelaxFactor)))
            {
                reason = $"terrain outside a door rises {rise:F1}m above its threshold";
                return true;
            }

            if (-rise > (near ? (DesignDoorDropTolerance * _terrainRelaxFactor) : (DesignDoorFarDropTolerance * _terrainRelaxFactor)))
            {
                reason = $"terrain outside a door drops {-rise:F1}m below its threshold";
                return true;
            }
        }

        return false;
    }

    private bool IsSiteTerrainUnsuitable(Vector3 candidate, string designPath, out string reason)
    {
        // The original flat-ground check always applies; the design-aware footprint/door
        // analysis is additional to it, never a substitute.
        reason = "standing on too much of a slope";

        if (IsTooSlopedToBuild(candidate))
        {
            return true;
        }

        if (designPath != null)
        {
            return IsTerrainUnsuitableForDesign(candidate, designPath, out reason);
        }

        reason = null;
        return false;
    }

    private bool IsTooSlopedToBuild(Vector3 position)
    {
        TerrainHeightMap heightMap = TerrainMeta.HeightMap;

        if (heightMap == null)
        {
            return false;
        }

        float centerHeight = heightMap.GetHeight(position);
        float maxDelta = 0f;

        foreach (Vector3 direction in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
        {
            float sampleHeight = heightMap.GetHeight(position + direction * SlopeCheckSampleRadius);
            maxDelta = Mathf.Max(maxDelta, Mathf.Abs(sampleHeight - centerHeight));
        }

        return maxDelta > MaxSlopeHeightDelta * _terrainRelaxFactor;
    }

    // Site-selection tuning: scan radius for nearby trees/ores/obstacles and LOS-breaking cover,
    // kept separate from the slope/road constants since these check whether the spot itself is
    // occupied rather than terrain suitability.
    private const float BuildSiteObstacleCheckRadius = 20f;

    // Tighter, separate radius for tree/ore clearing (ClearBuildSiteThenReplay), distinct from the
    // wider large-obstacle/cover checks above.
    private const float TreeOreClearCheckRadius = 10f;

    private const float BuildSiteRelocateDistance = 15f;
    private const int BuildSiteRelocateMaxAttempts = 14;
    private const float LargeStaticObstacleMinBoundsSize = 4f;

    // Proximity distance uses the collider's actual closest surface rather than just a bounding
    // box size threshold, since an irregular rock formation's bounding box can be much larger
    // than its real solid footprint.
    private const float LargeStaticObstacleProximityDistance = 6f;
    private const float LosClutterCheckDistance = 8f;
    private const int LosClutterDirectionCount = 8;
    private const int LosClutterBlockedDirectionThreshold = 3;

    // Safety valve limiting how many clearing actions (one tree or ore node each) a single replay
    // will do before giving up and building anyway, to avoid an unbounded loop if gathering can't
    // succeed.
    private const int BuildSiteMaxClearingActions = 20;

    /// <summary>
    /// Checks for a large static obstacle (rock formation, pylon, cliff geometry) near a position,
    /// filtering by collider bounds size rather than a fixed prefab list.
    /// </summary>
    private bool IsLargeStaticObstacleNearby(Vector3 position, out string obstacleDescription)
    {
        Collider[] hits = Physics.OverlapSphere(position, BuildSiteObstacleCheckRadius, LayerMask.GetMask("World"), QueryTriggerInteraction.Ignore);

        foreach (Collider hit in hits)
        {
            // Checks horizontal footprint (X/Z) rather than height, since a tall but narrow
            // obstacle doesn't actually block a build the way a wide one does.
            Vector3 size = hit.bounds.size;
            float horizontalFootprint = Mathf.Max(size.x, size.z);

            if (horizontalFootprint < LargeStaticObstacleMinBoundsSize)
            {
                continue;
            }

            // Uses ClosestPointOnBounds rather than Collider.ClosestPoint, since the latter isn't
            // supported for non-convex MeshColliders (common on irregular rock formations) and
            // would silently misreport distance as zero.
            float realDistance = Vector3.Distance(position, hit.ClosestPointOnBounds(position));

            if (realDistance > LargeStaticObstacleProximityDistance)
            {
                continue;
            }

            obstacleDescription = $"a large static obstacle ('{hit.gameObject.name}', {realDistance:F1}m away)";
            return true;
        }

        obstacleDescription = null;
        return false;
    }

    /// <summary>
    /// Checks whether a position is surrounded by line-of-sight-breaking cover by casting a ring
    /// of raycasts at eye height and counting how many are blocked within a short range.
    /// </summary>
    private bool IsSurroundedByLosBreakingCover(Vector3 position)
    {
        Vector3 eyePosition = position + Vector3.up * 1.5f;
        int blockedDirections = 0;
        int mask = LayerMask.GetMask("World", "Default");

        for (int i = 0; i < LosClutterDirectionCount; i++)
        {
            float angle = i * (360f / LosClutterDirectionCount) * Mathf.Deg2Rad;
            Vector3 direction = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

            if (Physics.Raycast(eyePosition, direction, LosClutterCheckDistance, mask, QueryTriggerInteraction.Ignore))
            {
                blockedDirections++;
            }
        }

        return blockedDirections >= LosClutterBlockedDirectionThreshold;
    }

    // How close another survivor's or player's tool cupboard can be before a bot steers away,
    // approximating Rust's own building-privilege radius.
    private const float NearbyPrivilegeAvoidRadius = 25f;

    // Buffer past a monument's actual bounds counted as too close to build, keeping the check
    // size-aware across monuments ranging from small to very large.
    private const float MonumentNoBuildBuffer = 50f;

    /// <summary>
    /// Checks whether a position is within another tool cupboard's privilege zone. Uses no layer
    /// filter and instead filters by component type, so it works regardless of physics layer.
    /// </summary>
    private bool IsTooCloseToAnotherCupboard(Vector3 position, out string description)
    {
        foreach (Collider hit in Physics.OverlapSphere(position, NearbyPrivilegeAvoidRadius, -1, QueryTriggerInteraction.Collide))
        {
            BuildingPrivlidge privilege = hit.GetComponentInParent<BuildingPrivlidge>();

            if (privilege == null || privilege.IsDestroyed)
            {
                continue;
            }

            description = $"{Vector3.Distance(position, privilege.transform.position):F1}m from an existing tool cupboard";
            return true;
        }

        description = null;
        return false;
    }

    /// <summary>
    /// Checks whether a position is inside a monument's no-build zone, using each monument's
    /// actual bounds rather than a flat radius from its center.
    /// </summary>
    private bool IsInsideMonumentNoBuildZone(Vector3 position, out string description)
    {
        if (MonumentAccess.GetAllMonuments().Count == 0)
        {
            description = null;
            return false;
        }

        foreach (MonumentInfo monument in MonumentAccess.GetAllMonuments())
        {
            if (monument == null)
            {
                continue;
            }

            Bounds expandedBounds = monument.Bounds;
            expandedBounds.Expand(MonumentNoBuildBuffer * 2f);

            // Uses an oriented box in the monument's own space, since a world-aligned check would
            // miss corners of rotated monuments.
            if (new OBB(monument.transform, expandedBounds).Contains(position))
            {
                description = $"inside '{monument.name}''s no-build zone (+{MonumentNoBuildBuffer:F0}m buffer)";
                return true;
            }
        }

        description = null;
        return false;
    }

    /// <summary>
    /// Site-selection entry point. Runs the relocate-worthy checks (road, slope, large obstacle,
    /// LOS-breaking cover, nearby cupboard privilege, monument no-build zone) against
    /// startPosition; on failure, steps further out along rotating compass directions and re-runs
    /// the full set, up to BuildSiteRelocateMaxAttempts times. Trees/ore are deliberately not
    /// checked here since those are cleared rather than avoided (see ClearBuildSiteThenReplay).
    /// Returns false with a reason once every relocate attempt has failed.
    /// </summary>
    // Escalating terrain tolerance: every few consecutive failed searches for a survivor loosens
    // all terrain tolerances (slope, door apron, foundation clip/float) further, up to a cap, so a
    // bot that keeps failing eventually accepts a workable site instead of searching forever.
    // Resets once a site is found.
    private float _terrainRelaxFactor = 1f;
    private readonly Dictionary<Guid, int> _buildSiteSearchFailures = new();

    private bool TryFindClearBuildOrigin(Vector3 startPosition, out Vector3 clearOrigin, out float expectedGroundHeight, out string failureReason, string designPath = null, List<Vector3> avoidSites = null, Guid ownerId = default)
    {
        int failures = ownerId != default && _buildSiteSearchFailures.TryGetValue(ownerId, out int f) ? f : 0;
        _terrainRelaxFactor = 1f + 0.25f * Mathf.Min(failures / 3, 4);

        try
        {
            return TryFindClearBuildOriginCore(startPosition, out clearOrigin, out expectedGroundHeight, out failureReason, designPath, avoidSites, ownerId);
        }
        finally
        {
            _terrainRelaxFactor = 1f;
        }
    }

    private bool TryFindClearBuildOriginCore(Vector3 startPosition, out Vector3 clearOrigin, out float expectedGroundHeight, out string failureReason, string designPath, List<Vector3> avoidSites, Guid ownerId)
    {
        Vector3 candidate = startPosition;
        string lastReason = "unknown";

        for (int attempt = 0; attempt <= BuildSiteRelocateMaxAttempts; attempt++)
        {
            if (avoidSites != null && avoidSites.Any(site => Vector3.Distance(site, candidate) < FailedBuildSiteAvoidRadius))
            {
                lastReason = "near a site this survivor already failed to build at";
            }
            else if (ownerId != default && IsNearAnotherSurvivorsBuildSite(candidate, ownerId))
            {
                lastReason = "another survivor has claimed a build site here";
            }
            else if (IsTooCloseToRoad(candidate))
            {
                lastReason = "too close to a road";
            }
            else if (IsSiteTerrainUnsuitable(candidate, designPath, out string terrainReason))
            {
                lastReason = terrainReason;
            }
            else if (IsLargeStaticObstacleNearby(candidate, out string obstacleDescription))
            {
                lastReason = $"too close to {obstacleDescription}";
            }
            else if (IsSurroundedByLosBreakingCover(candidate))
            {
                lastReason = "hemmed in by nearby cover that breaks line of sight";
            }
            else if (IsTooCloseToAnotherCupboard(candidate, out string cupboardDescription))
            {
                lastReason = cupboardDescription;
            }
            else if (IsInsideMonumentNoBuildZone(candidate, out string monumentDescription))
            {
                lastReason = monumentDescription;
            }
            else
            {
                // Reads ground height independently from TerrainMeta.HeightMap rather than the
                // navmesh-based probe used elsewhere, so a corrupted navmesh position can be
                // caught by comparing against this terrain-sourced height.
                expectedGroundHeight = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(candidate) : candidate.y;

                // Ensures the candidate's Y is corrected to the current terrain height rather than
                // carrying a stale Y from wherever the survivor originally stood, which would
                // otherwise propagate a wrong height to the walk destination.
                clearOrigin = candidate;
                clearOrigin.y = expectedGroundHeight;
                failureReason = null;
                return true;
            }

            // Uses a rotating compass step (deterministic and reproducible) to spread attempts in
            // a full circle around the original spot.
            float angle = attempt * (360f / (BuildSiteRelocateMaxAttempts + 1)) * Mathf.Deg2Rad;
            Vector3 step = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            candidate = startPosition + step * BuildSiteRelocateDistance * (attempt + 1);

            // Updates the candidate's Y to the current terrain height before the next iteration's
            // checks run, so obstacle/cover checks sample at the correct elevation.
            if (TerrainMeta.HeightMap != null)
            {
                candidate.y = TerrainMeta.HeightMap.GetHeight(candidate);
            }
        }

        clearOrigin = candidate;
        expectedGroundHeight = candidate.y;
        failureReason = lastReason;
        return false;
    }

    /// <summary>
    /// Finds the nearest cactus entity within a radius, matching by prefab name substring on the
    /// Tree layer, reusing the same detection approach used for cactus movement avoidance.
    /// </summary>
    private bool TryFindNearestCactus(Vector3 position, float radius, out BaseEntity cactus)
    {
        Collider[] hits = Physics.OverlapSphere(position, radius, LayerMask.GetMask("Tree"), QueryTriggerInteraction.Ignore);
        BaseEntity closest = null;
        float closestDistSqr = float.MaxValue;

        foreach (Collider hit in hits)
        {
            BaseEntity entity = hit.GetComponentInParent<BaseEntity>();

            if (entity == null || entity.IsDestroyed || entity.ShortPrefabName.IndexOf("cactus", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            float distSqr = (entity.transform.position - position).sqrMagnitude;

            if (distSqr < closestDistSqr)
            {
                closestDistSqr = distSqr;
                closest = entity;
            }
        }

        cactus = closest;
        return closest != null;
    }

    /// <summary>
    /// Reads a trace CSV and totals the resource cost to build it from scratch. When
    /// freeStarterEssentials is set, the first two doors, all code locks, and the furnace are
    /// excluded from the total, since those are needed for a first base to be functional.
    /// </summary>
    private Dictionary<string, int> CalculateTraceResourceRequirements(string csvPath, bool freeStarterEssentials = false)
    {
        Dictionary<string, int> totals = new();
        List<BuildTraceRow> rows = ReadBuildTraceCsv(csvPath);
        Dictionary<(Vector3, string), BuildingGrade.Enum> gradeByPiece = new();
        int freeDoorsRemaining = freeStarterEssentials ? 2 : 0;

        foreach (BuildTraceRow row in rows)
        {
            if (freeStarterEssentials && row.EventType != "upgrade")
            {
                if (row.Shortname == "lock.code" || row.Shortname == "furnace")
                {
                    continue;
                }

                if (row.Shortname.StartsWith("door.hinged.", StringComparison.OrdinalIgnoreCase) && freeDoorsRemaining > 0)
                {
                    freeDoorsRemaining--;
                    continue;
                }
            }

            bool isConstructionPiece = ConstructionPieceShortnames.Contains(row.Shortname);

            if (isConstructionPiece)
            {
                if (string.IsNullOrEmpty(row.Grade) || !Enum.TryParse(row.Grade, out BuildingGrade.Enum targetGrade))
                {
                    continue;
                }

                var pieceKey = (RoundTracePosition(row.Position), row.Shortname);
                BuildingGrade.Enum fromGrade = row.EventType == "upgrade" && gradeByPiece.TryGetValue(pieceKey, out BuildingGrade.Enum currentGrade)
                    ? currentGrade
                    : BuildingGrade.Enum.None;

                if (TryResolveConstructionPrefabPath(row.Shortname, out string prefabPath))
                {
                    Construction construction = PrefabAttribute.server.Find<Construction>(StringPool.Get(prefabPath));
                    ConstructionGrade constructionGrade = construction?.GetGrade(targetGrade, 0);

                    if (constructionGrade != null)
                    {
                        foreach (ItemAmount cost in constructionGrade.CostToBuild(fromGrade))
                        {
                            if (cost.itemDef != null && cost.amount > 0f)
                            {
                                AddToResourceTotal(totals, cost.itemDef.shortname, Mathf.CeilToInt(cost.amount));
                            }
                        }
                    }
                }

                gradeByPiece[pieceKey] = targetGrade;
            }
            else if (row.EventType != "upgrade")
            {
                // A deployable (door/cupboard/lock/furnace/shelves/box/workbench) uses its
                // crafting cost, not a construction cost.
                ItemDefinition itemDef = ItemManager.FindItemDefinition(row.Shortname);
                ItemBlueprint blueprint = itemDef?.Blueprint;

                if (blueprint == null)
                {
                    continue;
                }

                float amountCreatedPerCraft = Mathf.Max(1, blueprint.amountToCreate);

                foreach (ItemAmount ingredient in blueprint.GetIngredients())
                {
                    if (ingredient.itemDef != null && ingredient.amount > 0f)
                    {
                        AddToResourceTotal(totals, ingredient.itemDef.shortname, Mathf.CeilToInt(ingredient.amount / amountCreatedPerCraft));
                    }
                }
            }
        }

        return totals;
    }

    private static void AddToResourceTotal(Dictionary<string, int> totals, string shortname, int amount)
    {
        totals[shortname] = totals.TryGetValue(shortname, out int existing) ? existing + amount : amount;
    }

    /// <summary>
    /// Checks whether npc's inventory currently holds enough of each resource in totals (from
    /// CalculateTraceResourceRequirements).
    /// </summary>
    private bool CanAffordResourceTotals(BasePlayer npc, Dictionary<string, int> totals)
    {
        foreach (KeyValuePair<string, int> cost in totals)
        {
            int itemId = ItemManager.FindItemDefinition(cost.Key)?.itemid ?? 0;

            if (itemId == 0 || npc.inventory.GetAmount(itemId) < cost.Value)
            {
                return false;
            }
        }

        return true;
    }

    // Extracts the trailing digits from a tier folder name ("tier3" -> 3) so tiers can be ranked
    // richest-first without assuming a fixed number of tiers exist.
    private static int GetTierRank(string tierFolderName)
    {
        string digits = new(tierFolderName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int rank) ? rank : -1;
    }

    // Prerequisite gate for tier2 and up: requires a lower-tier base already built, plus a minimum
    // number of blueprint fragments (a higher count for tier3/tier4). Bots earn these through
    // normal high-tier monument looting.
    private const string BlueprintFragmentShortname = "blueprintbase";
    private const int Tier2BlueprintFragmentRequirement = 5;
    private const int Tier3PlusBlueprintFragmentRequirement = 100;

    /// <summary>
    /// Checks the tier2+ prerequisite gate (see the constants above) and returns false with a
    /// reason if either gate fails. Checked before affordability, not instead of it.
    /// </summary>
    // Reuses the same GearScore band minimums used for monument-tier loot destination weighting,
    // since a survivor geared enough to farm Tier2/Tier3 monuments is also the one who should be
    // eligible to build the base tier that loot funds.
    private const int Tier2GearScoreRequirement = MediumTierBandMin;
    private const int Tier3PlusGearScoreRequirement = HighTierBandMin;

    private bool IsTierUnlocked(Survivor survivor, BasePlayer npc, int tierRank, out string reason)
    {
        if (!survivor.Character.HasCompletedLowerTierBaseBuild)
        {
            reason = "hasn't completed a tier0/tier1 base build yet";
            return false;
        }

        int requiredGearScore = tierRank >= 3 ? Tier3PlusGearScoreRequirement : Tier2GearScoreRequirement;
        int gearScore = GetGearScore(npc);

        if (gearScore < requiredGearScore)
        {
            reason = $"gear score {gearScore}/{requiredGearScore} isn't there yet";
            return false;
        }

        int requiredFragments = tierRank >= 3 ? Tier3PlusBlueprintFragmentRequirement : Tier2BlueprintFragmentRequirement;
        int fragmentItemId = ItemManager.FindItemDefinition(BlueprintFragmentShortname)?.itemid ?? 0;
        int ownedFragments = fragmentItemId != 0 ? npc.inventory.GetAmount(fragmentItemId) : 0;

        if (ownedFragments < requiredFragments)
        {
            reason = $"only has {ownedFragments}/{requiredFragments} blueprint fragments";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Chooses which base design a survivor should build. Walks populated tier folders under
    /// BaseDesignsDirectory from richest to poorest, skipping tier2+ unless the prerequisite gate
    /// passes, and returns the first design it can afford. Falls back to a random tier0 design if
    /// nothing is currently affordable or unlocked.
    /// </summary>
    private bool TryChooseAffordableBaseDesign(Survivor survivor, out string chosenTier, out string designPath, out Dictionary<string, int> chosenCost)
    {
        BasePlayer npc = survivor.Player;

        if (Directory.Exists(BaseDesignsDirectory))
        {
            IEnumerable<string> tiers = Directory.GetDirectories(BaseDesignsDirectory)
                .Select(Path.GetFileName)
                .OrderByDescending(GetTierRank);

            foreach (string tier in tiers)
            {
                int tierRank = GetTierRank(tier);

                if (tierRank >= 2 && !IsTierUnlocked(survivor, npc, tierRank, out string _))
                {
                    continue;
                }

                string[] designs = Directory.GetFiles($"{BaseDesignsDirectory}/{tier}", "*.csv")
                    .OrderBy(_ => UnityEngine.Random.value)
                    .ToArray();

                foreach (string design in designs)
                {
                    Dictionary<string, int> totals = CalculateTraceResourceRequirements(design);

                    if (totals.Count > 0 && CanAffordResourceTotals(npc, totals))
                    {
                        chosenTier = tier;
                        designPath = design;
                        chosenCost = totals;
                        return true;
                    }
                }
            }
        }

        string fallbackTierDirectory = $"{BaseDesignsDirectory}/{DefaultBaseDesignTier}";

        if (Directory.Exists(fallbackTierDirectory))
        {
            string[] fallbackDesigns = Directory.GetFiles(fallbackTierDirectory, "*.csv");

            if (fallbackDesigns.Length > 0)
            {
                designPath = fallbackDesigns[UnityEngine.Random.Range(0, fallbackDesigns.Length)];
                chosenTier = DefaultBaseDesignTier;
                chosenCost = CalculateTraceResourceRequirements(designPath);
                return true;
            }
        }

        chosenTier = null;
        designPath = null;
        chosenCost = null;
        return false;
    }

    /// <summary>
    /// Clears trees/ore near the build site before building. Gathers the nearest tree or ore node
    /// to clearOrigin one at a time, recursing to check again since a gather can reveal another
    /// node, bounded by remainingClearingActions to avoid an unbounded loop. Starts the actual
    /// replay once the area is clear or the action limit is hit.
    /// </summary>
    private void ClearBuildSiteThenReplay(Survivor survivor, Vector3 clearOrigin, float expectedGroundHeight, string csvPath, int remainingClearingActions, Action<int, int> onComplete, bool skipCodeLocks = false)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        if (remainingClearingActions <= 0)
        {
            Puts($"basebuild-replay: '{survivor.Character.Alias}' hit the clearing action limit ({BuildSiteMaxClearingActions}) - building anyway rather than looping forever.");
            ReplayBuildTrace(survivor, csvPath, clearOrigin, expectedGroundHeight, onComplete, skipCodeLocks);
            return;
        }

        Action continueClearing = () => ClearBuildSiteThenReplay(survivor, clearOrigin, expectedGroundHeight, csvPath, remainingClearingActions - 1, onComplete, skipCodeLocks);

        // A cactus's collider shape can exhaust the full stuck-recovery ladder and risks an
        // emergency teleport landing somewhere unreachable. Cacti aren't harvestable, so this
        // removes the cactus directly rather than walking the survivor up to it.
        if (TryFindNearestCactus(clearOrigin, TreeOreClearCheckRadius, out BaseEntity cactus))
        {
            Puts($"basebuild-replay: '{survivor.Character.Alias}' is clearing a cactus ('{cactus.ShortPrefabName}') out of the way of the build site.");
            cactus.Kill();
            continueClearing();
            return;
        }

        if (_engine.NavigationManager.TryFindNearestTreeEntity(clearOrigin, TreeOreClearCheckRadius, out TreeEntity tree))
        {
            Puts($"basebuild-replay: '{survivor.Character.Alias}' is clearing a tree ('{tree.ShortPrefabName}') within the build site before continuing.");

            StartWalkingWithRecovery(
                survivor,
                GetResourceNodeApproachPoint(tree, npc),
                onArrived: () => StartGatheringResourceNode(survivor, tree, TreeGatherToolPriority, onSuccess: continueClearing, onFailed: continueClearing),
                onFailed: continueClearing);
            return;
        }

        if (_engine.NavigationManager.TryFindNearestOreResourceEntity(clearOrigin, TreeOreClearCheckRadius, out OreResourceEntity ore))
        {
            Puts($"basebuild-replay: '{survivor.Character.Alias}' is clearing an ore node ('{ore.ShortPrefabName}') within the build site before continuing.");

            StartWalkingWithRecovery(
                survivor,
                GetApproachPoint(ore, npc),
                onArrived: () => StartGatheringResourceNode(survivor, ore, OreGatherToolPriority, onSuccess: continueClearing, onFailed: continueClearing),
                onFailed: continueClearing);
            return;
        }

        ReplayBuildTrace(survivor, csvPath, clearOrigin, expectedGroundHeight, onComplete, skipCodeLocks);
    }

    // Hardcoded prefab paths for construction pieces and deployables, covering the shortnames used
    // by saved tier0-4 designs, so resolution doesn't depend on a matching live entity already
    // existing somewhere on the map. Checked before the live-entity scan below.
    private static readonly Dictionary<string, string> KnownDeployablePrefabPaths = new()
    {
        ["foundation"] = "assets/prefabs/building core/foundation/foundation.prefab",
        ["foundation.triangle"] = "assets/prefabs/building core/foundation.triangle/foundation.triangle.prefab",
        ["wall"] = "assets/prefabs/building core/wall/wall.prefab",
        ["wall.doorway"] = "assets/prefabs/building core/wall.doorway/wall.doorway.prefab",
        ["wall.half"] = "assets/prefabs/building core/wall.half/wall.half.prefab",
        ["wall.low"] = "assets/prefabs/building core/wall.low/wall.low.prefab",
        ["floor"] = "assets/prefabs/building core/floor/floor.prefab",
        ["floor.triangle"] = "assets/prefabs/building core/floor.triangle/floor.triangle.prefab",
        ["block.stair.ushape"] = "assets/prefabs/building core/stairs.u/block.stair.ushape.prefab",
        ["door.hinged.wood"] = "assets/prefabs/building/door.hinged/door.hinged.wood.prefab",
        ["door.hinged.metal"] = "assets/prefabs/building/door.hinged/door.hinged.metal.prefab",
        ["door.hinged.toptier"] = "assets/prefabs/building/door.hinged/door.hinged.toptier.prefab",
        ["cupboard.tool.deployed"] = "assets/prefabs/deployable/tool cupboard/cupboard.tool.deployed.prefab",
        ["workbench1.deployed"] = "assets/prefabs/deployable/tier 1 workbench/workbench1.deployed.prefab",
        ["workbench2.deployed"] = "assets/prefabs/deployable/tier 2 workbench/workbench2.deployed.prefab",
        ["workbench3.deployed"] = "assets/prefabs/deployable/tier 3 workbench/workbench3.deployed.prefab",
        ["furnace"] = "assets/prefabs/deployable/furnace/furnace.prefab",
        ["box.wooden.large"] = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab",
        ["shelves"] = "assets/prefabs/deployable/shelves/shelves.prefab",
    };

    private bool TryResolveConstructionPrefabPath(string shortname, out string prefabPath)
    {
        if (_resolvedConstructionPrefabPaths.TryGetValue(shortname, out prefabPath))
        {
            return true;
        }

        if (KnownDeployablePrefabPaths.TryGetValue(shortname, out prefabPath))
        {
            _resolvedConstructionPrefabPaths[shortname] = prefabPath;
            return true;
        }

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity.ShortPrefabName == shortname)
            {
                prefabPath = entity.PrefabName;
                _resolvedConstructionPrefabPaths[shortname] = prefabPath;
                return true;
            }
        }

        prefabPath = null;
        return false;
    }

    // Construction pieces placed via the generic building planner rather than by holding a
    // deployable item, unlike doors and cupboards.
    private static readonly HashSet<string> ConstructionPieceShortnames = new()
    {
        "foundation", "foundation.triangle", "wall", "wall.doorway",
        "wall.frame", "wall.window", "wall.low", "floor", "floor.triangle",
        "floor.frame", "roof", "stairs.spiral",
    };

    /// <summary>
    /// Equips the inventory item matching shortname onto the belt and makes it the active held
    /// item. Returns false if the survivor doesn't have that item.
    /// </summary>
    private bool EquipHeldItemByShortname(BasePlayer npc, string shortname)
    {
        int itemId = ItemManager.FindItemDefinition(shortname)?.itemid ?? 0;

        if (itemId == 0)
        {
            return false;
        }

        Item item = npc.inventory.FindItemByItemID(itemId);

        if (item == null)
        {
            return false;
        }

        Item currentlyEquipped = npc.GetActiveItem();

        if (currentlyEquipped != null && currentlyEquipped.uid == item.uid)
        {
            return true;
        }

        if (!npc.inventory.containerBelt.itemList.Contains(item))
        {
            if (!item.MoveToContainer(npc.inventory.containerBelt))
            {
                return false;
            }
        }

        npc.UpdateActiveItem(item.uid);
        ForceRefreshHeldEntity(npc);
        return true;
    }

    /// <summary>
    /// Phases a survivor directly to a build destination without the final ground-snap correction
    /// that StartPhasingToDestination normally applies, since that correction can incorrectly snap
    /// the survivor onto a freshly-placed floor above the intended walk position.
    /// </summary>
    private void PhaseToBuildDestination(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveMovement(survivor);

        Timer phaseTimer = null;

        phaseTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                phaseTimer.Destroy();
                _activeMovement.Remove(characterId);
                onFailed?.Invoke();
                return;
            }

            if (npc.IsWounded())
            {
                return;
            }

            Vector3 current = npc.transform.position;
            Vector3 toDestination = destination - current;
            float remaining = toDestination.magnitude;

            if (remaining < WaypointArriveDistance)
            {
                phaseTimer.Destroy();
                _activeMovement.Remove(characterId);
                onArrived?.Invoke();
                return;
            }

            float stepDistance = RunSpeed * WalkTickInterval;
            Vector3 next = remaining <= stepDistance ? destination : current + toDestination.normalized * stepDistance;

            npc.modelState.sprinting = true;
            npc.modelState.ducked = false;
            npc.modelState.ducking = 0f;
            npc.modelState.waterLevel = npc.WaterFactor();
            npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
            npc.SendModelState(true);

            FaceDirection(npc, toDestination);

            npc.transform.position = next;
            npc.MovePosition(next);
            survivor.Position = next;
            survivor.Character.Position = next;
        });

        _activeMovement[characterId] = phaseTimer;
    }

    private sealed class BuildTraceRow
    {
        public string EventType;
        public string Shortname;
        public Vector3 Position;
        public Vector3 Rotation;
        public string Grade;
    }

    /// <summary>
    /// Parses a build trace CSV by header column name rather than fixed position, so it works
    /// with or without an event_type column (missing columns default rows to "build").
    /// </summary>
    private List<BuildTraceRow> ReadBuildTraceCsv(string path)
    {
        List<BuildTraceRow> rows = new();

        // Opens with explicit FileShare.ReadWrite so a trace file that's still being written to
        // can be read without throwing a sharing violation.
        string[] lines;

        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (StreamReader reader = new(stream))
        {
            List<string> readLines = new();
            string line;

            while ((line = reader.ReadLine()) != null)
            {
                readLines.Add(line);
            }

            lines = readLines.ToArray();
        }

        if (lines.Length < 2)
        {
            return rows;
        }

        string[] header = lines[0].Split(',');
        Dictionary<string, int> columnIndex = new();

        for (int i = 0; i < header.Length; i++)
        {
            columnIndex[header[i]] = i;
        }

        int IndexOf(string column) => columnIndex.TryGetValue(column, out int idx) ? idx : -1;

        int eventTypeIdx = IndexOf("event_type");
        int shortnameIdx = IndexOf("shortname");
        int xIdx = IndexOf("x");
        int yIdx = IndexOf("y");
        int zIdx = IndexOf("z");
        int rotXIdx = IndexOf("rot_x");
        int rotYIdx = IndexOf("rot_y");
        int rotZIdx = IndexOf("rot_z");
        int gradeIdx = IndexOf("grade");

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            string[] cols = lines[i].Split(',');

            rows.Add(new BuildTraceRow
            {
                EventType = eventTypeIdx >= 0 && eventTypeIdx < cols.Length ? cols[eventTypeIdx] : "build",
                Shortname = cols[shortnameIdx],
                Position = new Vector3(float.Parse(cols[xIdx]), float.Parse(cols[yIdx]), float.Parse(cols[zIdx])),
                Rotation = new Vector3(float.Parse(cols[rotXIdx]), float.Parse(cols[rotYIdx]), float.Parse(cols[rotZIdx])),
                Grade = gradeIdx >= 0 && gradeIdx < cols.Length ? cols[gradeIdx] : string.Empty,
            });
        }

        // tier0/tier1 designs use wood doors and key locks instead of metal doors and code locks -
        // metal.fragments is a real bottleneck this early (barrel/crate spawn rates, recycler
        // congestion at monuments), and an early base doesn't need the extra security a
        // tier2+/tier-upgrade base does. Substituted here, the single shared read point every
        // caller (cost calculation, affordability checks, actual placement) already goes through,
        // so all three stay consistent automatically.
        if (path.Contains("/tier0/") || path.Contains("/tier1/"))
        {
            foreach (BuildTraceRow row in rows)
            {
                if (row.Shortname == "door.hinged.metal")
                {
                    row.Shortname = "door.hinged.wood";
                }
                else if (row.Shortname == "lock.code")
                {
                    row.Shortname = "lock.key";
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Tracks per-replay walking state, carrying the walk-then-place-then-continue chain forward
    /// one row at a time via callbacks, since each step needs a real walk to finish first.
    /// </summary>
    private sealed class BuildReplayState
    {
        public List<BuildTraceRow> Rows;
        public int Index;
        public Vector3 TraceOrigin;
        public Vector3 OriginPosition;

        // Source design path, relative to BaseDesignsDirectory (e.g. "tier0/base3.csv"), kept so
        // a hardcoded door route authored for this design can be found and re-projected later.
        public string CsvPath;

        // True only for a deliberate no-lock replay; a normal bot build always places locks
        // normally. Skips lock.code rows entirely rather than placing then removing them, since
        // placing one briefly locks the door.
        public bool SkipCodeLocks;

        // Independent ground-truth height, sourced from TerrainMeta.HeightMap rather than the
        // navmesh-based probe used to place the first foundation.
        public float ExpectedGroundHeight;

        // Tier rank of the design being replayed. -1 means unknown and never counts toward the
        // tier0/tier1 prerequisite.
        public int TierRank = -1;

        public BaseEntity LastDoorway;

        // Reference to the tool cupboard this replay places, if any, so upkeep materials can be
        // deposited into it once the build finishes.
        public BuildingPrivlidge Cupboard;

        // Reference to the highest-tier workbench this replay places, if any.
        public BaseEntity Workbench;
        public int HighestWorkbenchTier = -1;

        // Lets a later "upgrade" row find the exact BuildingBlock a matching earlier "build" row
        // already spawned, keyed by trace-space position AND shortname (not position alone, since
        // a foundation.triangle and its wall.doorway can share the exact same position).
        public BaseEntity LastDoor;
        public Dictionary<(Vector3 Position, string Shortname), BuildingBlock> BlocksByTracePosition = new();

        // Lets PlaceCodeLockReplayRow find which lockable target (door or tool cupboard) a
        // lock.code row was actually recorded next to, keyed by trace-space position, since a
        // trace can have multiple lockable targets.
        public Dictionary<Vector3, BaseEntity> LockableTargetsByTracePosition = new();

        // Tracks building membership so decay/upkeep/privilege work correctly across pieces
        // instead of each piece getting its own isolated buildingID.
        public DecayEntity LastDecayEntity;

        // Per-foundation ground reference, sourced from whichever foundation-tier piece was
        // placed most recently, since walls/floors/doorways stack off their local foundation
        // rather than the terrain itself.
        public bool HasFoundationReference;
        public float LastFoundationOriginalY;
        public float LastFoundationNewY;

        public int Placed;
        public int Failed;
        public Action<int, int> OnComplete;
    }

    // How far above the ground-probed height to bias the first foundation's placement, since a
    // raycast ground probe can read slightly low near an edge/slope/riverbank.
    private const float FoundationGroundClearance = 1f;

    // How far the navmesh-based ground probe's reading can differ from the independently-sourced
    // terrain height before this replay concludes the survivor ended up somewhere it shouldn't
    // have, rather than just standing on uneven ground.
    private const float GroundHeightMismatchTolerance = 1.5f;

    // Rounds to 2 decimal places so float parse/round-trip noise between a piece's "build" row and
    // its later "upgrade" row can't miss the BlocksByTracePosition lookup above.
    private static Vector3 RoundTracePosition(Vector3 position)
    {
        return new Vector3(Mathf.Round(position.x * 100f) / 100f, Mathf.Round(position.y * 100f) / 100f, Mathf.Round(position.z * 100f) / 100f);
    }

    // Pause between one placement finishing and the survivor moving on to the next, so the
    // sequence doesn't read as a single instant burst.
    private const float BuildPlacementPaceSeconds = 1.5f;

    // How far short of each piece's exact placement spot to stop, so the survivor isn't standing
    // where new solid geometry is about to spawn.
    private const float BuildApproachStandoffDistance = 2f;

    /// <summary>
    /// Walks the survivor to each piece's position in turn and places/pays for one piece at a
    /// time, translated so the trace's first row lands at originPosition. Grade is applied
    /// directly rather than replaying upgrade rows step-by-step. The door parents onto the most
    /// recently placed wall.doorway, and the tool cupboard grants building authority.
    // How far either side of the door, along the building-centroid-to-door axis, InsidePoint and
    // OutsidePoint sit, clear of the doorway's frame/threshold collision.
    private const float HomeDoorRoutePointDistance = 2.5f;

    /// <summary>
    /// Computes a ghost route between the inside and outside of the base's door. Building centroid
    /// is the average position of every placed BuildingBlock, and the door is whichever
    /// door.hinged.* was placed most recently. A design with no door leaves home.DoorPosition at
    /// its default, which callers treat as "no route available."
    /// </summary>
    private void ComputeHomeDoorRoute(BuildReplayState state, HomeBase home)
    {
        if (state.LastDoor == null || state.LastDoor.IsDestroyed || state.BlocksByTracePosition.Count == 0)
        {
            return;
        }

        Vector3 centroidSum = Vector3.zero;
        int pieceCount = 0;

        foreach (BuildingBlock block in state.BlocksByTracePosition.Values)
        {
            if (block == null || block.IsDestroyed)
            {
                continue;
            }

            centroidSum += block.transform.position;
            pieceCount++;
        }

        if (pieceCount == 0)
        {
            return;
        }

        Vector3 centroid = centroidSum / pieceCount;
        Vector3 doorPosition = state.LastDoor.transform.position;
        Vector3 outwardDirection = doorPosition - centroid;
        outwardDirection.y = 0f;

        if (outwardDirection.sqrMagnitude < 0.01f)
        {
            outwardDirection = state.LastDoor.transform.forward;
            outwardDirection.y = 0f;
        }

        outwardDirection = outwardDirection.sqrMagnitude > 0.01f ? outwardDirection.normalized : Vector3.forward;

        home.DoorPosition = doorPosition;
        home.OutsidePoint = doorPosition + outwardDirection * HomeDoorRoutePointDistance;
        home.InsidePoint = doorPosition - outwardDirection * HomeDoorRoutePointDistance;
    }

    // Radius to look for a live door near a translated door-route waypoint, generous enough to
    // catch natural variance in where the recording player stood relative to the door.
    private const float DoorRouteAnchorSearchRadius = 2f;

    // Sentinel value passed as requiredMonumentName so a door-route recording is never
    // re-projected onto a nearby monument; door routes are always base-design-relative.
    private const string DoorRouteMonumentAnchorSentinel = "__no_real_monument_ever_matches_this__";

    /// <summary>
    /// Loads saved hardcoded door ghost routes for this design, re-projecting each one onto this
    /// live instance and resolving which live door each route belongs to by proximity.
    /// </summary>
    private void LoadHomeDoorRoutes(HomeBase home)
    {
        if (string.IsNullOrEmpty(home.SourceDesignPath))
        {
            return;
        }

        string designDirectory = Path.GetDirectoryName(home.SourceDesignPath)?.Replace('\\', '/') ?? string.Empty;
        string designName = Path.GetFileNameWithoutExtension(home.SourceDesignPath);
        string doorsFolder = $"{BaseDesignsDirectory}/{designDirectory}/{designName}_doors";

        if (!Directory.Exists(doorsFolder))
        {
            return;
        }

        foreach (string routeFile in Directory.GetFiles(doorsFolder, "*.csv"))
        {
            if (!TryLoadTraceWaypoints(routeFile.Replace('\\', '/'), out List<GhostRouteWaypoint> waypoints, out _, DoorRouteMonumentAnchorSentinel) || waypoints.Count == 0)
            {
                continue;
            }

            List<Vector3> worldWaypoints = waypoints.Select(w => w.Position + home.BuildOriginPosition).ToList();

            Vector3 doorAnchor = Vector3.zero;
            bool foundAnchor = false;

            foreach (Vector3 waypoint in worldWaypoints)
            {
                Collider[] hits = Physics.OverlapSphere(waypoint, DoorRouteAnchorSearchRadius, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

                Door nearbyDoor = hits.Select(hit => hit.GetComponentInParent<Door>()).FirstOrDefault(candidate => candidate != null && !candidate.IsDestroyed);

                if (nearbyDoor != null)
                {
                    doorAnchor = nearbyDoor.transform.position;
                    foundAnchor = true;
                    break;
                }
            }

            if (!foundAnchor)
            {
                Puts($"basebuild-replay: door route '{Path.GetFileName(routeFile)}' for '{home.SourceDesignPath}' has no real door anywhere near its own path once re-projected here - skipping it for this instance.");
                continue;
            }

            home.DoorRoutes.Add(new HomeDoorRoute
            {
                Waypoints = worldWaypoints,
                DoorAnchorPosition = doorAnchor,
            });
        }

        if (home.DoorRoutes.Count > 0)
        {
            Puts($"basebuild-replay: loaded {home.DoorRoutes.Count} hardcoded door route(s) for '{home.SourceDesignPath}'.");
        }
    }

    private void ReplayBuildTrace(Survivor survivor, string csvPath, Vector3 originPosition, float expectedGroundHeight, Action<int, int> onComplete, bool skipCodeLocks = false)
    {
        // Upgrade rows are not filtered out; the CSV's row order is already chronological, so
        // replaying every row in sequence naturally builds the shape first then applies upgrades.
        List<BuildTraceRow> rows = ReadBuildTraceCsv(csvPath);

        if (rows.Count == 0)
        {
            onComplete(0, 0);
            return;
        }

        // Derived from the design's own file path (e.g. ".../base_designs/tier2/base3.csv"); -1
        // (unknown) for anything replayed outside the tier pool.
        int tierRank = GetTierRank(Path.GetFileName(Path.GetDirectoryName(csvPath)) ?? string.Empty);

        BuildReplayState state = new()
        {
            Rows = rows,
            Index = 0,
            TraceOrigin = rows[0].Position,
            OriginPosition = originPosition,
            ExpectedGroundHeight = expectedGroundHeight,
            TierRank = tierRank,
            CsvPath = csvPath.Replace('\\', '/'),
            OnComplete = onComplete,
            SkipCodeLocks = skipCodeLocks,
        };

        AdvanceBuildReplay(survivor, state);
    }

    // Minimum upkeep stock each tool cupboard should be filled with.
    private const int CupboardMinimumWood = 1000;
    private const int CupboardMinimumStone = 2000;
    private const int CupboardMinimumMetalFragments = 300;

    /// <summary>
    /// Stocks a tool cupboard with minimum upkeep materials, drawing from the survivor's own
    /// inventory first and topping up with freshly-created items only for the remaining shortfall.
    /// Skips a resource already at or above the minimum.
    /// </summary>
    private void StockToolCupboard(BasePlayer npc, BuildingPrivlidge cupboard)
    {
        DepositMinimumIntoCupboard(npc, cupboard, WoodShortname, CupboardMinimumWood);
        DepositMinimumIntoCupboard(npc, cupboard, StoneShortname, CupboardMinimumStone);
        DepositMinimumIntoCupboard(npc, cupboard, "metal.fragments", CupboardMinimumMetalFragments);
    }

    private void DepositMinimumIntoCupboard(BasePlayer npc, BuildingPrivlidge cupboard, string shortname, int minimumAmount)
    {
        ItemDefinition itemDef = ItemManager.FindItemDefinition(shortname);

        if (itemDef == null)
        {
            return;
        }

        int alreadyStocked = cupboard.inventory.GetAmount(itemDef.itemid, onlyUsableAmounts: false);
        int stillNeeded = minimumAmount - alreadyStocked;

        if (stillNeeded <= 0)
        {
            return;
        }

        List<Item> collected = new();
        npc.inventory.Take(collected, itemDef.itemid, stillNeeded);
        int collectedAmount = 0;

        foreach (Item item in collected)
        {
            collectedAmount += item.amount;

            if (!item.MoveToContainer(cupboard.inventory))
            {
                // If the cupboard is full, give the item back rather than destroying it.
                item.MoveToContainer(npc.inventory.containerMain);
            }
        }

        int remainingShortfall = stillNeeded - collectedAmount;

        if (remainingShortfall > 0)
        {
            Item topUp = ItemManager.CreateByItemID(itemDef.itemid, remainingShortfall);

            if (topUp != null && !topUp.MoveToContainer(cupboard.inventory))
            {
                topUp.Remove();
            }
        }

        Puts($"basebuild-replay: stocked '{shortname}' in '{npc.displayName}' tool cupboard up to {minimumAmount} ({collectedAmount} from its own inventory, {Mathf.Max(0, remainingShortfall)} freshly topped up).");
    }

    private void AdvanceBuildReplay(Survivor survivor, BuildReplayState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || state.Index >= state.Rows.Count)
        {
            // Stocks the tool cupboard with upkeep materials only once the whole replay has
            // finished, and only if a cupboard was actually placed.
            if (npc != null && !npc.IsDestroyed && state.Cupboard != null && !state.Cupboard.IsDestroyed)
            {
                StockToolCupboard(npc, state.Cupboard);
            }

            // Persists the base as the survivor's home, anchored to the cupboard; a design with
            // no cupboard row has nothing to anchor "home" to.
            if (state.Placed > 0 && state.Cupboard != null && !state.Cupboard.IsDestroyed)
            {
                HomeBase home = new()
                {
                    Position = state.Cupboard.transform.position,
                    TierRank = state.TierRank,
                    CupboardNetId = state.Cupboard.net?.ID.Value ?? 0uL,
                    WorkbenchNetId = state.Workbench != null && !state.Workbench.IsDestroyed ? state.Workbench.net?.ID.Value ?? 0uL : 0uL,
                    BuildOriginPosition = state.OriginPosition,
                    SourceDesignPath = state.CsvPath != null && state.CsvPath.StartsWith(BaseDesignsDirectory + "/", StringComparison.OrdinalIgnoreCase)
                        ? state.CsvPath.Substring(BaseDesignsDirectory.Length + 1)
                        : null,
                };

                ComputeHomeDoorRoute(state, home);
                LoadHomeDoorRoutes(home);
                survivor.Character.Home = home;

                // Once set, this flag is never cleared, so a tier0/tier1 build satisfies the
                // prerequisite for every future tier2+ attempt by this character.
                if (state.TierRank is 0 or 1)
                {
                    survivor.Character.HasCompletedLowerTierBaseBuild = true;
                }

                // Saved immediately rather than waiting for the next natural save point, so a
                // restart can't lose a base that was only ever in memory.
                _engine.SaveManager.SaveCharacters(_engine.CharacterManager.GetAllCharacters());

                // Explicitly exits the base first if the survivor ended up inside it, since the
                // native movement system's NavMeshAgent.Warp can otherwise snap the survivor
                // outside instantly on the very next walk, bypassing door-crossing checks.
                ExitHomeIfInside(survivor, home, () => state.OnComplete(state.Placed, state.Failed));
                return;
            }

            state.OnComplete(state.Placed, state.Failed);
            return;
        }

        BuildTraceRow row = state.Rows[state.Index];
        state.Index++;

        Vector3 worldPosition = state.OriginPosition + (row.Position - state.TraceOrigin);

        // Rather than applying one constant Y offset derived from the survivor's starting height
        // for every row, the first foundation's height is ground-checked against real terrain.
        bool isFirstFoundationRow = !state.HasFoundationReference && row.EventType != "upgrade" && (row.Shortname == "foundation" || row.Shortname == "foundation.triangle");

        if (isFirstFoundationRow)
        {
            // Ground-checked once only, on the first foundation; every later piece stacks
            // relative to that one sample so the footprint stays level, using
            // NavigationManager.TryGetGroundHeight (a raycast-based probe against the actual
            // terrain collider) rather than the coarser TerrainMeta.HeightMap grid.
            if (_engine.NavigationManager.TryGetGroundHeight(worldPosition, out float groundHeight))
            {
                // Cross-checks against ExpectedGroundHeight (an independent terrain-only reading
                // taken when the site was validated) to catch the survivor having ended up
                // somewhere it shouldn't be, such as an orphaned navmesh island from a bad
                // stuck-recovery teleport.
                if (Mathf.Abs(groundHeight - state.ExpectedGroundHeight) > GroundHeightMismatchTolerance)
                {
                    Puts($"basebuild-replay: '{survivor.Character.Alias}' is {Mathf.Abs(groundHeight - state.ExpectedGroundHeight):F1}m away from the real expected ground height here (currently at {groundHeight:F1}, expected ~{state.ExpectedGroundHeight:F1}) - looks like it ended up somewhere it shouldn't have (a bad stuck-recovery teleport is the likely cause). Aborting rather than building in mid-air.");
                    state.OnComplete(state.Placed, state.Failed + 1);
                    return;
                }

                // Biases the result slightly upward since even a raycast-based ground probe can
                // read a touch low near an edge or slope; a foundation's support pillars fill any
                // small gap.
                worldPosition.y = groundHeight + FoundationGroundClearance;
                state.LastFoundationOriginalY = row.Position.y;
                state.LastFoundationNewY = worldPosition.y;
                state.HasFoundationReference = true;
            }
        }
        else if (state.HasFoundationReference)
        {
            worldPosition.y = state.LastFoundationNewY + (row.Position.y - state.LastFoundationOriginalY);
        }

        // Uses phasing rather than the normal walk/stuck-recovery pipeline between placement
        // points, since navmesh-based pathing can't cope with a construction site that changes
        // shape as pieces are placed. The walk destination's Y is pinned to the ground-level
        // foundation reference rather than each piece's exact recorded height, since a player
        // reaches up to place a ceiling rather than standing at its height. Assumes a single-story
        // footprint.
        Vector3 walkDestination = worldPosition;
        walkDestination.y = state.HasFoundationReference ? state.LastFoundationNewY : state.OriginPosition.y;

        Vector3 approachDirection = npc.transform.position - walkDestination;
        approachDirection.y = 0f;

        if (approachDirection.sqrMagnitude > 0.01f)
        {
            walkDestination += approachDirection.normalized * BuildApproachStandoffDistance;
        }

        PhaseToBuildDestination(
            survivor,
            walkDestination,
            onArrived: () => PlaceBuildReplayRow(survivor, state, row, worldPosition),
            onFailed: () =>
            {
                Puts($"basebuild-replay: '{survivor.Character.Alias}' couldn't reach the spot for '{row.Shortname}' - skipping it.");
                state.Failed++;
                AdvanceBuildReplay(survivor, state);
            });
    }

    private void PlaceBuildReplayRow(Survivor survivor, BuildReplayState state, BuildTraceRow row, Vector3 worldPosition)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            state.OnComplete(state.Placed, state.Failed);
            return;
        }

        // An upgrade row doesn't spawn anything new; it changes the grade of a piece a matching
        // earlier "build" row already placed, so it's handled entirely separately.
        if (row.EventType == "upgrade")
        {
            PlaceUpgradeReplayRow(survivor, state, row);
            return;
        }

        // A code lock isn't a free-standing piece; it slots onto the most recently placed door via
        // its lock slot, not a normal CreateEntity-at-worldPosition placement.
        if (row.Shortname == CodeLockShortname)
        {
            if (state.SkipCodeLocks)
            {
                timer.Once(BuildPlacementPaceSeconds, () => AdvanceBuildReplay(survivor, state));
                return;
            }

            PlaceCodeLockReplayRow(survivor, state, row, worldPosition);
            return;
        }

        if (!TryResolveConstructionPrefabPath(row.Shortname, out string prefabPath))
        {
            Puts($"basebuild-replay: couldn't resolve a real prefab path for '{row.Shortname}' - no live entity of that type found anywhere to query. Skipping this piece.");
            state.Failed++;
            timer.Once(BuildPlacementPaceSeconds, () => AdvanceBuildReplay(survivor, state));
            return;
        }

        // A construction piece is placed while holding the generic building planner, while a
        // deployable is placed by holding the item itself; this keeps what the bot is visibly
        // holding honest, though it doesn't affect the actual spawn below.
        bool isConstructionPiece = ConstructionPieceShortnames.Contains(row.Shortname);
        EquipHeldItemByShortname(npc, isConstructionPiece ? BuildingPlanShortname : row.Shortname);

        Quaternion worldRotation = Quaternion.Euler(row.Rotation);
        BaseEntity newEntity = GameManager.server.CreateEntity(prefabPath, worldPosition, worldRotation);

        if (newEntity == null)
        {
            Puts($"basebuild-replay: failed to create '{row.Shortname}' from '{prefabPath}'.");
            state.Failed++;
            timer.Once(BuildPlacementPaceSeconds, () => AdvanceBuildReplay(survivor, state));
            return;
        }

        // Attaches each new piece to the previous one so they merge into one shared building
        // (matching Construction.CreateConstruction's own AttachToBuilding-before-Spawn order).
        // Without this, each piece would get its own isolated buildingID, breaking upkeep cost,
        // decay protection, and the privilege UI. The first piece gets null, starting a new
        // buildingID as expected.
        if (newEntity is DecayEntity newDecayEntity)
        {
            newDecayEntity.AttachToBuilding(state.LastDecayEntity);
        }

        newEntity.OwnerID = npc.userID;
        newEntity.Spawn();
        state.Placed++;

        if (newEntity is DecayEntity spawnedDecayEntity)
        {
            state.LastDecayEntity = spawnedDecayEntity;
        }

        // blockDefinition (and everything SetGrade reads off it) is only populated after Spawn(),
        // so grade/health are applied after, not before.
        if (newEntity is BuildingBlock block && !string.IsNullOrEmpty(row.Grade) && Enum.TryParse(row.Grade, out BuildingGrade.Enum gradeEnum))
        {
            // Placement is always Twigs first; anything higher means this piece was upgraded
            // after being placed, so the hammer is equipped for that step to keep the held item
            // honest, even though ChangeGrade itself is a direct call rather than the swing RPC.
            if (gradeEnum != BuildingGrade.Enum.Twigs)
            {
                EquipHeldItemByShortname(npc, HammerShortname);
            }

            // Uses ChangeGrade rather than a bare SetGrade, since it also handles the upkeep/decay
            // bookkeeping (SendNetworkUpdate, ResetUpkeepTime, BuildingManager.Dirty) that a bare
            // setter would skip.
            block.ChangeGrade(gradeEnum, playEffect: true);
            block.SetHealthToMax();
            DeductRealConstructionCost(npc, block, gradeEnum);
            state.BlocksByTracePosition[(RoundTracePosition(row.Position), row.Shortname)] = block;

            // Plays the piece's placement effect, falling back to a generic one if it has none.
            string blockPlaceEffect = block.blockDefinition.placeEffect.isValid
                ? block.blockDefinition.placeEffect.resourcePath
                : "assets/bundled/prefabs/fx/build/frame_place.prefab";

            Effect.server.Run(blockPlaceEffect, newEntity, 0u, Vector3.zero, Vector3.zero);
        }
        else
        {
            // A deployable (cupboard/door) was already crafted/given and paid for; placing it just
            // consumes the one inventory item, with no separate construction cost.
            Item heldItem = npc.inventory.FindItemByItemID(ItemManager.FindItemDefinition(row.Shortname)?.itemid ?? 0);
            heldItem?.UseItem(1);

            // Plays the deployable's placement effect at the world position.
            Deployable deployableDefinition = PrefabAttribute.server.Find<Deployable>(newEntity.prefabID);

            if (deployableDefinition != null && deployableDefinition.placeEffect.isValid)
            {
                Effect.server.Run(deployableDefinition.placeEffect.resourcePath, worldPosition, Vector3.up);
            }
        }

        if (row.Shortname == "wall.doorway")
        {
            state.LastDoorway = newEntity;
        }
        else if (row.Shortname.StartsWith("door.hinged."))
        {
            if (state.LastDoorway != null)
            {
                newEntity.SetParent(state.LastDoorway, worldPositionStays: true);
            }

            state.LastDoor = newEntity;
            state.LockableTargetsByTracePosition[RoundTracePosition(row.Position)] = newEntity;
        }
        else if (newEntity is BuildingPrivlidge buildingPrivilege)
        {
            buildingPrivilege.AddPlayer(npc, npc.userID);
            state.Cupboard = buildingPrivilege;

            // A tool cupboard can also hold a lock slot, so a lock.code row may target the
            // cupboard instead of a door; tracked the same way for the nearest-match lookup below.
            state.LockableTargetsByTracePosition[RoundTracePosition(row.Position)] = newEntity;
        }
        else if (row.Shortname.StartsWith("workbench") && row.Shortname.EndsWith(".deployed"))
        {
            // Keeps whichever workbench is the highest tier this design places, since that's the
            // one that determines what the survivor can craft at home.
            string tierDigits = new(row.Shortname.Where(char.IsDigit).ToArray());

            if (int.TryParse(tierDigits, out int workbenchTier) && workbenchTier > state.HighestWorkbenchTier)
            {
                state.HighestWorkbenchTier = workbenchTier;
                state.Workbench = newEntity;
            }
        }

        Puts($"basebuild-replay: '{survivor.Character.Alias}' placed '{row.Shortname}' at {worldPosition}.");

        timer.Once(BuildPlacementPaceSeconds, () => AdvanceBuildReplay(survivor, state));
    }

    // Fixed prefab path used as a fallback when no code lock entity already exists on the map for
    // TryResolveConstructionPrefabPath to find.
    private const string RealCodeLockPrefabPath = "assets/prefabs/locks/keypad/lock.code.prefab";

    /// <summary>
    /// Places a code lock onto a door or cupboard, mirroring the slot-anchor sequence a real
    /// deploy uses: parenting to the target's lock slot before Spawn(), then setting the slot
    /// after. Sets a random 4-digit code and whitelists the survivor, reaching the same end state
    /// as a real client deploy.
    /// </summary>
    private void PlaceCodeLockReplayRow(Survivor survivor, BuildReplayState state, BuildTraceRow row, Vector3 worldPosition)
    {
        BasePlayer npc = survivor.Player;

        // Finds the closest lockable target by each candidate's original trace-space position,
        // rather than always using whichever door was placed most recently, so each lock matches
        // the specific door/cupboard it was recorded beside.
        BaseEntity lockTarget = null;
        float bestDistSqr = float.MaxValue;

        foreach (KeyValuePair<Vector3, BaseEntity> candidate in state.LockableTargetsByTracePosition)
        {
            if (candidate.Value == null || candidate.Value.IsDestroyed || candidate.Value.GetSlot(BaseEntity.Slot.Lock) != null)
            {
                continue;
            }

            float distSqr = (candidate.Key - row.Position).sqrMagnitude;

            if (distSqr < bestDistSqr)
            {
                bestDistSqr = distSqr;
                lockTarget = candidate.Value;
            }
        }

        if (lockTarget == null)
        {
            Puts($"basebuild-replay: '{survivor.Character.Alias}' has no door or cupboard yet to lock - skipping the code lock.");
            state.Failed++;
            timer.Once(BuildPlacementPaceSeconds, () => AdvanceBuildReplay(survivor, state));
            return;
        }

        if (!TryResolveConstructionPrefabPath(row.Shortname, out string prefabPath))
        {
            prefabPath = RealCodeLockPrefabPath;
        }

        EquipHeldItemByShortname(npc, row.Shortname);

        // Spawns at the origin with identity rotation (matching the real SpawnLock call) rather
        // than at the trace's recorded world position, since SetParent with worldPositionStays
        // false reinterprets the entity's existing local offset relative to the new parent - a
        // nonzero starting position/rotation would land the lock in the wrong place or tilted.
        BaseEntity spawned = GameManager.server.CreateEntity(prefabPath, Vector3.zero, Quaternion.identity);

        if (spawned is not CodeLock codeLock)
        {
            Puts($"basebuild-replay: failed to create a real code lock from '{prefabPath}'.");
            state.Failed++;
            timer.Once(BuildPlacementPaceSeconds, () => AdvanceBuildReplay(survivor, state));
            return;
        }

        codeLock.SetParent(lockTarget, lockTarget.GetSlotAnchorName(BaseEntity.Slot.Lock));
        codeLock.OwnerID = npc.userID;
        codeLock.SetFlagLocal(BaseEntity.Flags.Locked, true);
        codeLock.Spawn();
        lockTarget.SetSlot(BaseEntity.Slot.Lock, codeLock);

        string realCode = UnityEngine.Random.Range(1000, 10000).ToString();
        codeLock.code = realCode;
        codeLock.whitelistPlayers.Clear();
        codeLock.whitelistPlayers.Add(npc.userID);
        codeLock.SendNetworkUpdate();

        Item heldItem = npc.inventory.FindItemByItemID(ItemManager.FindItemDefinition(row.Shortname)?.itemid ?? 0);
        heldItem?.UseItem(1);

        if (codeLock.effectLocked.isValid)
        {
            Effect.server.Run(codeLock.effectLocked.resourcePath, codeLock, 0u, Vector3.zero, Vector3.zero);
        }

        state.Placed++;

        // Logs the resolved prefab path and the lock's final position/rotation for diagnostics.
        Puts($"basebuild-replay: '{survivor.Character.Alias}' placed and locked a code lock (code {realCode}) on '{lockTarget.ShortPrefabName}' - prefab '{prefabPath}', final pos {codeLock.transform.position}, net.ID {codeLock.net?.ID}, parent bone anchor '{lockTarget.GetSlotAnchorName(BaseEntity.Slot.Lock)}'.");

        timer.Once(BuildPlacementPaceSeconds, () => AdvanceBuildReplay(survivor, state));
    }

    /// <summary>
    /// Applies an upgrade row to an existing piece: swaps to the hammer and applies the
    /// incremental cost/grade change directly, since the piece is already in place and doesn't
    /// need a fresh approach walk.
    /// </summary>
    private void PlaceUpgradeReplayRow(Survivor survivor, BuildReplayState state, BuildTraceRow row)
    {
        BasePlayer npc = survivor.Player;

        if (!state.BlocksByTracePosition.TryGetValue((RoundTracePosition(row.Position), row.Shortname), out BuildingBlock block) || block == null || block.IsDestroyed)
        {
            Puts($"basebuild-replay: '{survivor.Character.Alias}' couldn't find the piece this upgrade row belongs to - skipping it.");
            state.Failed++;
            timer.Once(BuildPlacementPaceSeconds, () => AdvanceBuildReplay(survivor, state));
            return;
        }

        if (string.IsNullOrEmpty(row.Grade) || !Enum.TryParse(row.Grade, out BuildingGrade.Enum targetGrade) || targetGrade == block.grade)
        {
            AdvanceBuildReplay(survivor, state);
            return;
        }

        EquipHeldItemByShortname(npc, HammerShortname);

        BuildingGrade.Enum fromGrade = block.grade;

        block.ChangeGrade(targetGrade, playEffect: true);
        block.SetHealthToMax();
        DeductRealConstructionCost(npc, block, targetGrade, fromGrade);
        state.BlocksByTracePosition[(RoundTracePosition(row.Position), row.Shortname)] = block;

        // Plays the upgrade sound/particle via ClientRPC rather than Effect.server.Run, since
        // that's how the upgrade effect is triggered client-side.
        block.ClientRPC(RpcTarget.NetworkGroup("DoUpgradeEffect"), (int)targetGrade, 0uL);

        state.Placed++;
        Puts($"basebuild-replay: '{survivor.Character.Alias}' upgraded a piece from {fromGrade} to {targetGrade}.");

        timer.Once(BuildPlacementPaceSeconds, () => AdvanceBuildReplay(survivor, state));
    }

    /// <summary>
    /// Deducts the real construction cost from the survivor's inventory at the moment of
    /// placement, using Construction.GetGrade/ConstructionGrade.CostToBuild for the ingredient
    /// list and PlayerInventory.Take plus UseItem to consume the matching items.
    /// </summary>
    private void DeductRealConstructionCost(BasePlayer npc, BuildingBlock block, BuildingGrade.Enum gradeEnum, BuildingGrade.Enum fromGrade = BuildingGrade.Enum.None)
    {
        Construction construction = PrefabAttribute.server.Find<Construction>(block.prefabID);
        ConstructionGrade constructionGrade = construction?.GetGrade(gradeEnum, 0);

        if (constructionGrade == null)
        {
            return;
        }

        // fromGrade defaults to None (full from-scratch cost); an upgrade row passes the piece's
        // current grade instead, so CostToBuild only charges the incremental difference.
        foreach (ItemAmount cost in constructionGrade.CostToBuild(fromGrade))
        {
            if (cost.itemDef == null || cost.amount <= 0f)
            {
                continue;
            }

            List<Item> collected = new();
            npc.inventory.Take(collected, cost.itemDef.itemid, (int)cost.amount);

            foreach (Item item in collected)
            {
                item.UseItem(item.amount);
            }
        }
    }
}
