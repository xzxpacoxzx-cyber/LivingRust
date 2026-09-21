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
/// Real base-building test replay (2026-08-28) - the very first slice of
/// "resource gathering -> crafting -> base building." Deliberately NOT a
/// general "figure out sockets and decide a layout" system (Lucas's own
/// explicit call: replicating Rust's real client-side raycast-a-ghost-
/// preview-onto-the-nearest-socket flow for a bot with no camera is a
/// large, separate problem) - instead, this replays a REAL captured
/// /lr.debug.tracebuild CSV (LivingRust.Debug.cs) verbatim: the exact
/// same relative shape/order/grades a real player already successfully
/// built, translated to a new origin. Same "record real behaviour, then
/// have the bot replay it" pattern this project already uses for
/// movement (LivingRust.MonumentRoutes.cs, the traceme-replay command).
///
/// Every prefab path is resolved by querying a LIVE entity of that exact
/// ShortPrefabName already standing in the world (BaseNetworkable.
/// PrefabName, confirmed real via decompile) rather than a hardcoded/
/// guessed asset path - zero guessing, same "confirm via real data"
/// standard this whole project holds itself to.
///
/// Deliberately skips real material cost deduction for this first pass -
/// /lr.spawn.basebuilder already gives a bot far more wood/stone than one
/// small structure needs, so proving the actual placement/spawn/parenting
/// mechanism works comes first; real cost accounting (CanAffordToPlace,
/// GetConstructionCost) is a real, known follow-up once the structure
/// itself is confirmed standing correctly.
/// </summary>
public partial class LivingRust
{
    // Cheap memoization - every "foundation"/"wall"/etc appears many times
    // across one replay, no need to re-scan the whole live entity list for
    // every single row.
    private readonly Dictionary<string, string> _resolvedConstructionPrefabPaths = new();

    /// <summary>
    /// Finds a real, currently-alive entity anywhere on the map whose
    /// ShortPrefabName matches, and returns its own real PrefabName (the
    /// full asset path GameManager.server.CreateEntity actually needs).
    /// Works for both real Construction/BuildingBlock pieces (foundation,
    /// wall, wall.doorway, floor, floor.triangle, foundation.triangle) and
    /// deployables (cupboard.tool.deployed, door.hinged.metal) - anything
    /// already standing somewhere real, which for this replay's own
    /// purposes always includes whatever structure was actually traced.
    /// </summary>
    // Real bitmask (2026-08-29, confirmed via decompile -
    // Construction.TestPlacingCloseToRoad's own exact literal) - the
    // combined Road|Roadside TerrainTopology flags real Rust checks a
    // placement's surrounding topology against before allowing
    // construction near a road at all.
    private const int RoadTopologyMask = 0x80800;

    // Matches TestPlacingCloseToRoad's own real probe radius for a
    // near-ground placement (Mathf.Lerp(3f, 0f, heightDelta / 9f) maxes
    // out at 3f right at ground level) - close enough for a single
    // origin-point gate rather than replicating its full per-corner OBB
    // sampling, which only matters for a single piece's own exact bounds.
    private const float RoadProximityCheckRadius = 3f;

    /// <summary>
    /// Real road-proximity check (2026-08-29, Lucas's own live report - a
    /// real build trace recording near a road cut off after only 6 rows,
    /// the walls never finished placing). Confirmed via decompile: this is
    /// the exact real rule (Construction.TestPlacingCloseToRoad,
    /// ConstructionErrors.TooCloseToRoad) that stopped him, not a bug in
    /// tracebuild's own recording - real Rust genuinely refuses
    /// construction within a real topology-based road buffer. Used as a
    /// pre-flight gate before a replay starts, since the replay itself
    /// bypasses the real Planner/CanBuild pipeline entirely (raw
    /// CreateEntity) and would otherwise happily "succeed" building
    /// somewhere a real player never could.
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

    // How far out to sample terrain height around the origin, and how much
    // vertical difference between those samples counts as "too sloped".
    // This replay translates every row by a single constant offset from
    // the trace's own first row (AdvanceBuildReplay's own worldPosition
    // math) - it never re-projects each piece onto the NEW location's own
    // terrain, so it only ever produces a good result on ground that's
    // roughly as flat as wherever the original trace was recorded. 1.2m
    // over a 4m radius is a rough real-world match for what a foundation
    // can actually tolerate before Rust's own real placement checks start
    // rejecting it (a foundation piece is ~0.3m thick with some grade
    // tolerance on top).
    private const float SlopeCheckSampleRadius = 4f;
    private const float MaxSlopeHeightDelta = 1.2f;

    /// <summary>
    /// Real live bug (2026-08-29, fifth round - Lucas's own report: "bots
    /// still be phasing through the floor... 99NervousTrapper wanted to go
    /// 105m away and then ended up in the air"). Confirmed via the log:
    /// the site scan relocated the build origin 105m away, real navmesh
    /// pathing to it failed outright ("no navmesh surface found within
    /// 5m" at the destination - a genuinely isolated/unreachable spot),
    /// and StartWalkingWithRecovery's OWN internal escalation ladder
    /// (LivingRust.Looting.cs's EscalateStuckRecovery) exhausted wiggle,
    /// navmesh-nudge, and emergency-teleport, then did exactly what it's
    /// designed to do for every OTHER caller in this project once all of
    /// those fail: phase straight through everything as a genuine last
    /// resort. Switching the relocation walk from PhaseToBuildDestination
    /// to StartWalkingWithRecovery (an earlier fix this session) only
    /// moved WHERE phasing could still happen from - it never actually
    /// removed it, since that shared ladder's own final tier phases
    /// unconditionally and has no opt-out parameter.
    ///
    /// Rather than add a "don't phase" flag through EscalateStuckRecovery
    /// itself (used by loot-walking, following, and every other movement
    /// task in this project - real risk of a subtle regression somewhere
    /// else for a change only base-building needs), this gives base-
    /// building its own bounded recovery: the same real wiggle/navmesh-
    /// nudge/emergency-teleport tiers (TryWiggleFree/
    /// TryFindNavMeshNudgePoint/TryEmergencyTeleport - the exact same real
    /// helpers EscalateStuckRecovery itself calls, nothing reinvented),
    /// but once all three are exhausted, it just gives up and reports
    /// failure instead of falling through to a phase. Same self-contained-
    /// copy precedent this file already set with PhaseToBuildDestination
    /// itself (a deliberate copy of StartPhasingToDestination with one
    /// piece removed, for the identical reason - not touching the shared
    /// version other systems still rely on unchanged).
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

        // Real "am I stuck ON a tree/ore node itself" check (2026-09-01,
        // Lucas's own explicit ask: "if the bots get stuck and a tree or
        // ore is on the path... have the bot halt that current task to
        // mine the tree or ore to clear the path") - same real check
        // EscalateStuckRecovery already runs (LivingRust.Looting.cs,
        // 2026-08-28 original), just missing here. This ladder is a
        // SEPARATE, older one built specifically for approaching a
        // relocated build site (its own doc comment: deliberately never
        // phases, gives up cleanly instead) - the new autonomous "head to
        // an inland/gather site" walks (LivingRust.HomeSiteStrategy.cs)
        // funnel through here too via WalkToBuildSiteWithRecovery, so
        // without this they'd hit a tree/ore blocking the path and just
        // wiggle/nudge/teleport around it (or give up) instead of clearing
        // it. Checked on every tier (same reasoning as the original), not
        // just tier 0 - resumes at whatever tier this episode was already
        // at once cleared, doesn't reset backward.
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
    /// Real live bug (2026-08-29, Lucas's own report, with screenshots: one
    /// of the bot's own foundations ended up mostly swallowed by the
    /// ground, and separately he couldn't place a real foundation by hand
    /// in a sloped spot at all - "CANNOT BUILD IN THIS AREA"). Root cause
    /// confirmed structural, not a bug to fix in the placement code itself:
    /// this replay applies one constant translation offset to every row
    /// (see AdvanceBuildReplay), so it faithfully reproduces the ORIGINAL
    /// trace's own relative shape but has no way to re-adjust individual
    /// pieces for a new location's different terrain height/slope - on
    /// flat ground the constant offset happens to match well enough, on a
    /// slope it doesn't, and pieces end up too deep/too high exactly the
    /// way his screenshot shows. Gating the replay's own START location on
    /// real terrain flatness (same sampled-height-delta approach
    /// TestPlacingCloseToRoad's own neighbourhood check uses, just for
    /// slope instead of topology) stops it from even starting somewhere
    /// this simple translate-only approach can't handle well, rather than
    /// silently producing a half-buried structure.
    /// </summary>
    // Design-aware terrain analysis (2026-09-21, Lucas's own explicit ask:
    // slope itself isn't the problem for bots or players - a front door
    // that ends up buried in / floating off the terrain is). The replay
    // translates every piece by one constant offset and never re-projects
    // onto the new terrain, so what actually matters is how the terrain sits
    // relative to (a) each foundation and (b) the ground just outside each
    // door, measured against where the DESIGN expects its floor to be.
    // Failed-site memory + reachability (2026-09-21) - a survivor that
    // failed to walk to / build at a site steers well clear of it next
    // time, and a candidate with no navmesh within BuildSiteNavMeshMaxDistance
    // (where the "no navmesh surface within 5m" walk failures came from) is
    // rejected up front instead of after a doomed walk.
    // Build-site reservations (2026-09-21, Lucas's own explicit spec: once a
    // bot rolls to build somewhere, other bots keep 20-30m clear of it - the
    // same idea as a tool cupboard's building-blocked radius). A
    // reservation is the spot a survivor has committed to build at (set once
    // its site passes every check), and an inland survivor's rolled home
    // target counts too. Lapses after BuildSiteReservationLifetimeSeconds,
    // or as soon as the build finishes/fails or the owner dies.
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
        // The original flat-ground check always applies (2026-09-21, live
        // screenshots of half-built bases - walls/doors floating with no
        // foundation under them - after the design-aware check briefly
        // replaced it). Every clean base built before that change came
        // through this check; the design-aware footprint/door analysis is
        // now strictly ADDITIONAL to it, never a substitute.
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

    // Real site-selection tuning (2026-08-29, Lucas's own explicit ask -
    // "find a way the bot can avoid building inside of trees, ores or
    // physical unbreakable structures... scan for trees/ores within 20
    // metres... also scan for cover that breaks LOS... move 15 metres
    // away and re-run both scans"). Kept as its own named constants
    // rather than folded into the slope/road ones since these tune a
    // conceptually different question (is the SPOT itself already
    // occupied) rather than terrain suitability.
    private const float BuildSiteObstacleCheckRadius = 20f;

    // Real, deliberately separate radius (2026-08-29, second round -
    // Lucas's own explicit ask: "drop the scan for trees or ores near me
    // from 20m to 10m") - tree/ore clearing (ClearBuildSiteThenReplay)
    // gets its own tighter radius now, distinct from the large-obstacle/
    // cover checks above which stay at the wider 20m.
    private const float TreeOreClearCheckRadius = 10f;

    private const float BuildSiteRelocateDistance = 15f;
    private const int BuildSiteRelocateMaxAttempts = 14;
    private const float LargeStaticObstacleMinBoundsSize = 4f;

    // Real live bug (2026-08-29, second round - Lucas's own report: "still
    // too close to a large obstacle, nothing visually is in the way").
    // Confirmed via the log - the flagged obstacle really was
    // 'rock_formation_small_d_arid', a genuinely SMALL rock prefab whose
    // bounding box still crossed 4m, because an axis-aligned bounding box
    // around an irregular/diagonal rock cluster is routinely far bigger
    // than the rock's own actual solid footprint (a rock scattered across
    // a wide area with lots of real open space between the individual
    // pieces still gets ONE big bounding box for the whole prefab). The
    // real fix isn't a bigger/smaller size threshold - it's checking how
    // close the collider's own REAL surface actually is
    // (Collider.ClosestPoint, real per-triangle nearest point, not a
    // bounding-box guess) rather than "does anything with a big bounding
    // box exist somewhere within 20m."
    private const float LargeStaticObstacleProximityDistance = 6f;
    private const float LosClutterCheckDistance = 8f;
    private const int LosClutterDirectionCount = 8;
    private const int LosClutterBlockedDirectionThreshold = 3;

    // How many clearing actions (one tree or ore node each) a single
    // replay will do before giving up and building anyway - real safety
    // valve against an unbounded loop if a gather genuinely can't
    // succeed (no tool, node stuck respawning), not a number Lucas asked
    // for specifically.
    private const int BuildSiteMaxClearingActions = 20;

    /// <summary>
    /// Real "unbreakable physical structure" check (2026-08-29) - a
    /// large static world collider (a rock formation, a powerline pylon,
    /// cliff geometry) rather than a tree/ore (handled separately above)
    /// or actual construction (nothing exists to build INTO yet at
    /// site-selection time). Filters by real collider bounds size rather
    /// than a fixed prefab list, since there's no clean closed set of
    /// "big rock formation" shortnames to enumerate - anything genuinely
    /// large enough to matter for a foundation footprint clears the bar,
    /// anything small (a pebble, a bush) doesn't.
    /// </summary>
    private bool IsLargeStaticObstacleNearby(Vector3 position, out string obstacleDescription)
    {
        Collider[] hits = Physics.OverlapSphere(position, BuildSiteObstacleCheckRadius, LayerMask.GetMask("World"), QueryTriggerInteraction.Ignore);

        foreach (Collider hit in hits)
        {
            // Real live bug (2026-08-29, Lucas's own report - a build got
            // refused over a "small rock formation"). The original check
            // flagged a collider whose HEIGHT alone crossed the threshold,
            // even with a tiny footprint - a real small rock can easily be
            // a bit tall/jagged without actually blocking anything a
            // foundation would need to sit next to. What actually matters
            // for a build footprint is horizontal FOOTPRINT (X/Z), not
            // height - a real player builds right up against a spire-like
            // rock all the time, just not through a wide one.
            Vector3 size = hit.bounds.size;
            float horizontalFootprint = Mathf.Max(size.x, size.z);

            if (horizontalFootprint < LargeStaticObstacleMinBoundsSize)
            {
                continue;
            }

            // Second round fix (see LargeStaticObstacleProximityDistance's
            // own doc comment) - a big bounding box existing somewhere in
            // the 20m sweep isn't the same as it actually being close.
            // Deliberately ClosestPointOnBounds, not Collider.ClosestPoint -
            // the latter isn't supported for non-convex MeshColliders
            // (exactly what an irregular real rock formation almost
            // certainly uses) and silently returns the query point itself
            // in that case, which would make realDistance always read as
            // 0 and flag EVERY rock as touching regardless of real
            // distance. ClosestPointOnBounds works for every collider type
            // unconditionally - less precise than the exact mesh surface,
            // but immune to that silent failure mode.
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
    /// Real "surrounded by cover that breaks LOS" check (2026-08-29,
    /// Lucas's own explicit ask - "scan for cover near me that... breaks
    /// LOS"). Casts a ring of short raycasts outward at roughly eye
    /// height and counts how many are blocked within a short range -
    /// several blocked directions means the spot is genuinely hemmed in
    /// by nearby solid cover (rocks, dense terrain clutter), not just one
    /// stray object at the edge of the check radius.
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

    // How close another survivor's (or a real player's) tool cupboard can
    // be before a bot steers away and tries somewhere else (2026-09-01,
    // Lucas's own explicit ask: "if there is a building blocked area -
    // another tool cupboard is within that area... 20-30m away should
    // do"). Matches real Rust's own building-privilege radius closely
    // enough to catch the actual case that matters - attempting to build
    // inside a zone that would just refuse real construction/upgrades
    // anyway - without needing to replicate BuildingPrivlidge's own exact
    // radius math.
    private const float NearbyPrivilegeAvoidRadius = 25f;

    // How far past a real monument's own actual footprint (Bounds, not a
    // guessed fixed radius - monuments range from a small power
    // substation to the entire Launch Site) counts as "too close to
    // build" (2026-09-01, Lucas's own explicit ask: "monuments and other
    // premade objects... have a no build zone to stop players abusing
    // indestructible objects"). A flat buffer past the real bounds keeps
    // this size-aware instead of either trapping bots near small
    // monuments or letting them build against a huge one's edge.
    private const float MonumentNoBuildBuffer = 50f;

    /// <summary>
    /// Real "another cupboard's privilege zone is already here" check
    /// (2026-09-01) - see NearbyPrivilegeAvoidRadius's own doc comment.
    /// Physics.OverlapSphere with no layer filter (deliberately, unlike
    /// most other checks in this file - BuildingPrivlidge's own real
    /// physics layer isn't something this project has confirmed anywhere
    /// else, and getting a layer mask wrong here would silently make this
    /// check never fire at all rather than fail loudly) - filtered by
    /// component type instead, which works regardless of layer.
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
    /// Real monument no-build-zone check (2026-09-01) - see
    /// MonumentNoBuildBuffer's own doc comment. Uses each real monument's
    /// own actual Bounds rather than a flat radius, since "20m from a
    /// power substation" and "20m from Launch Site's own edge" mean
    /// completely different real distances from the monument's center.
    /// </summary>
    private bool IsInsideMonumentNoBuildZone(Vector3 position, out string description)
    {
        if (TerrainMeta.Path?.Monuments == null)
        {
            description = null;
            return false;
        }

        foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
        {
            if (monument == null)
            {
                continue;
            }

            Bounds expandedBounds = monument.Bounds;
            expandedBounds.Expand(MonumentNoBuildBuffer * 2f);

            // Oriented box in the monument's own space (2026-09-21) - the
            // old world-aligned Contains was only correct for monuments
            // that happen to face north, and missed corners of rotated ones.
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
    /// Real site-selection entry point (2026-08-29, Lucas's own explicit
    /// ask, quoted in full in each individual check's own doc comment
    /// above). Runs the real relocate-worthy checks (road, slope, large
    /// obstacle, LOS-breaking cover, nearby cupboard privilege, monument
    /// no-build zone) against startPosition; if any of them fail, steps
    /// BuildSiteRelocateDistance further out along a rotating set of
    /// compass directions and re-runs the FULL set again, up to
    /// BuildSiteRelocateMaxAttempts times. Deliberately does NOT check for
    /// trees/ore here (2026-08-29, second round - Lucas's own follow-up:
    /// "if there are trees or ores within 20m... have it mine/farm those
    /// THEN begin building" rather than relocating away from them) - that
    /// case is handled separately by ClearBuildSiteThenReplay below, since
    /// a tree/ore node is a real obstacle worth clearing rather than
    /// avoiding, unlike a road/slope/rock formation/cover cluster which
    /// this replay has no way to fix. Returns false (with a clear reason)
    /// only once every relocate attempt in that budget has failed.
    /// </summary>
    // Escalating terrain tolerance (2026-09-21, Lucas's own explicit spec:
    // bots should get a base down ASAP after the primitive window). Live
    // numbers on the fresh hilly map: 833 failed site searches against 32
    // builds started, ~600 of them terrain rules. Every 3 consecutive failed
    // searches for a survivor loosens ALL terrain tolerances (slope, door
    // apron, foundation clip/float) by another 25%, up to 2x - the first
    // attempts stay strict so a genuinely good site is still preferred, but
    // a bot that keeps failing eventually accepts a workable one instead of
    // searching forever. Reset once it finds a site.
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
                // Real, independent ground-truth reading (2026-08-29,
                // Lucas's own explicit ask - "we need to avoid the bots
                // building up in the air"). Sourced from TerrainMeta.
                // HeightMap - the same real terrain-only API
                // IsTooSlopedToBuild already trusts for this exact spot -
                // deliberately NOT the navmesh-based ground probe
                // AdvanceBuildReplay itself uses to place the first
                // foundation. The two are independent data sources on
                // purpose: if the survivor ends up somewhere between now
                // and when building actually starts (a bad emergency-
                // teleport recovery mid-walk is the known real culprit -
                // NavMesh.SamplePosition has no ground-connectivity
                // guarantee and can drop a survivor onto orphaned/elevated
                // navmesh with no real climb ever happening), the navmesh
                // probe would happily report THAT as valid ground with no
                // way to tell it's wrong on its own - comparing it against
                // this independently-sourced real terrain height is what
                // actually catches it.
                expectedGroundHeight = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(candidate) : candidate.y;

                // Real live bug (2026-08-29, sixth round - Lucas's own
                // direct question: "is it possible the bots are deciding
                // to build the base off the Y axis they were initially on
                // when the command was given?"). Confirmed exactly right:
                // candidate's Y was NEVER updated as this loop stepped
                // further and further away (each relocate step only ever
                // touched X/Z, see below) - so a relocation 105m away
                // could return a "clear origin" still carrying whatever Y
                // the survivor happened to be standing at back at the
                // ORIGINAL spot, wildly wrong for the new location's real
                // terrain height. Every consumer of this origin - most
                // importantly, the real walk destination itself - was
                // built on that stale height. This is almost certainly
                // the actual real root cause behind "no navmesh surface
                // found within 5m" at a relocated destination (a
                // destination floating in open air or buried underground
                // has no navmesh anywhere near it) that then cascaded
                // into the whole stuck-recovery chain. Correcting it here,
                // once, to the same real terrain height just computed
                // above.
                clearOrigin = candidate;
                clearOrigin.y = expectedGroundHeight;
                failureReason = null;
                return true;
            }

            // Rotating compass step, not a random one - deterministic and
            // reproducible for testing, and spreads attempts out in a
            // full circle around the original spot rather than drifting
            // in one direction.
            float angle = attempt * (360f / (BuildSiteRelocateMaxAttempts + 1)) * Mathf.Deg2Rad;
            Vector3 step = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            candidate = startPosition + step * BuildSiteRelocateDistance * (attempt + 1);

            // Same real Y-axis fix as the success branch above, applied
            // BEFORE the next iteration's own checks run too - the large-
            // obstacle/LOS-cover checks are real Physics.OverlapSphere/
            // Raycast calls centered on candidate's own Y, so a stale
            // height carried from wherever the survivor originally stood
            // could make them sample well above or below the real ground
            // at this new XZ, missing genuinely nearby obstacles or
            // flagging irrelevant ones at the wrong elevation.
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
    /// Same real detection this project already uses for cactus movement
    /// avoidance (IsBlockedByCactus, LivingRust.Commands.cs) - substring
    /// match against the real prefab family (cactus_1 through cactus_7,
    /// confirmed via AssetSceneManifest.json) on the real "Tree" layer
    /// cacti live on, rather than a fresh implementation.
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
    /// Real bill-of-materials calculator (2026-08-29, Lucas's own explicit
    /// ask: "focus on the resource requirement for the bots to build their
    /// bases"). Reads a trace CSV and adds up the exact real cost every
    /// row in it would actually take to build from scratch, using nothing
    /// but real game APIs already trusted elsewhere in this replay:
    /// Construction/ConstructionGrade.CostToBuild (the exact real method
    /// DeductRealConstructionCost itself calls during an actual replay)
    /// for every BuildingBlock build/upgrade row, and each deployable's
    /// own real ItemBlueprint.GetIngredients() (the same real API
    /// /lr.debug.recipe already uses) for every door/cupboard/lock/
    /// furnace/shelves/box/workbench row - a deployable isn't grown from
    /// raw materials by CreateEntity the way a BuildingBlock is, it has to
    /// be crafted first, so its real cost is what CRAFTING it costs, not
    /// what placing it costs (placing consumes the one already-crafted
    /// item, DeductRealConstructionCost's own deployable branch has no
    /// separate construction cost the way a BuildingBlock does).
    ///
    /// Tracks each individual piece's own current grade across the trace
    /// (keyed by trace position + shortname, the same real
    /// BlocksByTracePosition collision fix from earlier this session -
    /// two different pieces at the same spot, like a foundation.triangle
    /// under its own wall.doorway, must never share one grade-tracking
    /// slot) so an "upgrade" row correctly charges only the real
    /// INCREMENTAL cost (CostToBuild(fromGrade)) rather than double-
    /// counting the piece's full cost a second time.
    /// </summary>
    /// <summary>
    /// freeFirstTwoDoorsAndLocks (2026-09-01, Lucas's own explicit ask,
    /// autonomous "gather for a base" only): "happy for the first two doors
    /// the bot places + the code locks it places to be free... a bot might
    /// die mid building base or get caught with an inventory full of items
    /// and this could potentially cause it to idle" - metal.fragments is
    /// the real bottleneck (no farmable node, only recycling), and
    /// door.hinged.metal (150x) + every lock.code (100x each) are a
    /// meaningful chunk of a design's total metal.fragments cost. Defaults
    /// false so every OTHER caller (TryChooseAffordableBaseDesign's real
    /// affordability check, in particular) keeps seeing the true total
    /// cost unchanged - only the autonomous gather-target calculation
    /// opts in.
    /// </summary>
    private Dictionary<string, int> CalculateTraceResourceRequirements(string csvPath, bool freeFirstTwoDoorsAndLocks = false)
    {
        Dictionary<string, int> totals = new();
        List<BuildTraceRow> rows = ReadBuildTraceCsv(csvPath);
        Dictionary<(Vector3, string), BuildingGrade.Enum> gradeByPiece = new();
        int freeDoorsRemaining = freeFirstTwoDoorsAndLocks ? 2 : 0;

        foreach (BuildTraceRow row in rows)
        {
            if (freeFirstTwoDoorsAndLocks && row.EventType != "upgrade")
            {
                if (row.Shortname == "lock.code")
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
                // A deployable (door/cupboard/lock/furnace/shelves/box/
                // workbench) - real crafting cost, not a construction cost.
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
    /// Real affordability check (2026-08-29) - compares totals (from
    /// CalculateTraceResourceRequirements) against what npc's own real
    /// inventory currently holds, via the same real PlayerInventory.
    /// GetAmount already used throughout this project for exactly this
    /// kind of "do I actually have enough" question.
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

    // Extracts the trailing digits from a tier folder name ("tier3" -> 3)
    // so tiers can be ranked richest-first without assuming a fixed count
    // of tiers exist - whatever folders Lucas has actually populated
    // under BaseDesignsDirectory get considered, in descending order.
    private static int GetTierRank(string tierFolderName)
    {
        string digits = new(tierFolderName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int rank) ? rank : -1;
    }

    // Real prerequisite gate for tier2 and up (2026-08-29, Lucas's own
    // explicit ask): "these bases require tier2 and tier3 workbenches...
    // locked until a bot A. has a tier1 or tier0 base already built, B.
    // has 5 basic blueprint fragments, C. has [100] blueprint fragments
    // (only applicable for tier3 and tier4)" - clarified live that C is
    // the SAME real item (Blueprint Fragments, shortname "blueprintbase"),
    // just a higher count - PENDING RECONFIRMATION (2026-08-29, second
    // round): a live /lr.debug.scan of two dropped fragments showed two
    // GENUINELY DIFFERENT real prefabs ("basicblueprintfragment" and
    // "advanceblueprintfragment"), contradicting the original "no
    // separate advanced variant" read - the real distinct item shortname
    // for the advanced one still needs confirming (WorldItem.item.info.
    // shortname, now surfaced by /lr.debug.scan's own recent fix) before
    // this constant set gets corrected to match. Bots are expected to
    // earn these through real high-tier monument loot (missile silo,
    // launch site) rather than anything special this project needs to
    // farm for directly - blueprintbase isn't on any never-loot
    // exclusion, so normal autonomous looting already picks it up.
    private const string BlueprintFragmentShortname = "blueprintbase";
    private const int Tier2BlueprintFragmentRequirement = 5;
    private const int Tier3PlusBlueprintFragmentRequirement = 100;

    /// <summary>
    /// Real prerequisite check for tier2+ (see the constants/field group
    /// right above for the full real ask this implements). Returns false
    /// with a human-readable reason the moment either gate fails -
    /// checked before affordability, not instead of it, so a tier2+
    /// design still has to clear CanAffordResourceTotals afterward too.
    /// Prerequisite A is now the real persisted Character.
    /// HasCompletedLowerTierBaseBuild flag (2026-08-29, second round -
    /// Lucas's own explicit ask: "if the bot has built a base of any
    /// tier, it is persisted throughout server restarts") - replaces an
    /// earlier in-memory-only HashSet that would have silently forgotten
    /// every survivor's progress on every restart.
    /// </summary>
    // Real GearScore gate for tier2+ base builds (2026-09-01, Lucas's own
    // explicit ask: "the gearscore threshold... should honestly govern how
    // a bot reacts/decides what to do"). Deliberately reuses the EXACT
    // same band minimums TryStartWithGearWeightedDestination's own
    // GetTierWeights already uses to decide which monument tier a
    // survivor's loot destination rolls favor (MediumTierBandMin=25 for
    // Tier2 monuments, HighTierBandMin=58 for Tier3) - a survivor whose
    // gear already makes it realistic for them to be farming Tier2/Tier3
    // monuments is the same survivor who should be eligible to build the
    // base tier those monuments' loot (blueprint fragments, high-end
    // components) actually funds. One real signal driving both decisions
    // instead of two independent, possibly-disagreeing thresholds.
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
    /// Real tier-decision entry point (2026-08-29, Lucas's own explicit
    /// ask: "figure out how they decide what tier of base they want to
    /// build"). Walks every real populated tier folder under
    /// BaseDesignsDirectory from richest to poorest (GetTierRank) - tier2
    /// and above are skipped entirely unless the prerequisite gate above
    /// passes first, regardless of affordability - and within each
    /// remaining tier, checks every saved design in a shuffled order (so
    /// it's not always base1 specifically) against the survivor's own
    /// current real inventory (CanAffordResourceTotals) - returns the
    /// FIRST design anywhere in that search it can genuinely afford
    /// outright, on the reasoning that a survivor with the means to build
    /// a bigger, better-defended base should actually build one rather
    /// than defaulting to the cheapest option out of habit.
    ///
    /// Falls back to a random tier0 design (the same roll
    /// /lr.debug.replaybuild's own explicit-tier mode already does) if
    /// NOTHING anywhere is currently affordable/unlocked - matching the
    /// explicit design call already recorded on this project's own
    /// roadmap: commit to a real design and farm the shortfall rather
    /// than endlessly re-rolling in search of something cheap enough.
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
    /// Real pre-build clearing chain (2026-08-29, Lucas's own explicit
    /// ask: "if there are trees or ores within that 20m of where the bot
    /// wants to build the base, have it mine/farm those trees and ores
    /// THEN begin building the base"). Walks to and fully gathers whatever
    /// real tree or ore node (in that priority - same real order
    /// TryStartResourceGatheringFallback already uses) is nearest to
    /// clearOrigin, one at a time, then recurses to check again - a single
    /// gather can easily reveal or leave behind another node that was
    /// previously the SECOND-closest, so this keeps looping (bounded by
    /// remainingClearingActions, a real safety valve against an
    /// unbounded loop if a gather genuinely can't succeed - no tool, a
    /// node stuck respawning) until the real 20m radius is genuinely
    /// clear, then finally starts the actual replay. Reuses
    /// StartGatheringResourceNode directly (LivingRust.ResourceGathering.cs)
    /// - the same real walk-equip-swing-until-destroyed primitive normal
    /// autonomous gathering uses, just with its own continuation instead
    /// of resuming ContinueLootTask afterward.
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

        // Real live bug (2026-08-29, Lucas's own report: a bot teleported
        // 10-15m into the air and started building there). Root cause is
        // almost certainly the SAME known cactus issue already documented
        // in this project's own movement code (IsBlockedByCactus,
        // LivingRust.Commands.cs) - a cactus's real collider shape (thin
        // trunk plus jutting arm colliders) is a confirmed repeat offender
        // for exhausting the FULL stuck-recovery ladder (wiggle -> navmesh
        // nudge -> emergency teleport) even at close range, and emergency
        // teleport's own NavMesh.SamplePosition call has no ground-
        // connectivity guarantee - it can drop a survivor onto orphaned/
        // elevated navmesh coverage with no real climb ever happening
        // (the same suspected mechanism behind an earlier "bot found dead
        // on an inaccessible rooftop" report). A cactus isn't actually
        // harvestable in real Rust (that's hemp bushes, not cacti - no
        // resource yield exists to fake here), so this clears it directly
        // rather than walking the survivor right up to it first - sending
        // it toward the cactus's own position would risk triggering the
        // exact same stuck/collision problem this exists to avoid.
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

    // Real, confirmed prefab paths for deployables that DON'T reliably
    // exist anywhere on a live map to query (2026-08-29, Lucas's own
    // report - furnace/shelves/box.wooden.large were all being silently
    // skipped, "couldn't resolve a real prefab path... no live entity of
    // that type found anywhere"). Doors/cupboards/locks worked so far
    // because something of that type usually already exists somewhere on
    // the map (another player's base, a monument, a prior test); these
    // three don't have that luck, and this was always the known real gap
    // flagged back when TryResolveConstructionPrefabPath was first built -
    // "bots cannot rely on that base being built somewhere else in the
    // world." Confirmed real via a direct grep of this server's own
    // AssetSceneManifest.json (Assets/Bundles), not guessed - same
    // "confirm via real data" standard as every other hardcoded path in
    // this project (RealCodeLockPrefabPath, /lr.spawn.basebuilder's own
    // shortnames). Checked BEFORE the live-entity scan below, so it also
    // works as a correctness guarantee even when a live entity DOES
    // happen to exist somewhere.
    // Extended to cover the FULL real set of shortnames every currently
    // saved tier0-4 design actually uses (2026-08-29, second round -
    // Lucas's own explicit ask: "remove the requirement... for those
    // items/prefabs existing in the world" entirely, not just patch the
    // three that had already broken). Every path here confirmed the same
    // way - grepped directly out of this server's own
    // AssetSceneManifest.json, picking the plain default "Building Core"/
    // base deployable variant (several of these, e.g. foundation.triangle
    // and wall.half, also have unrelated monument/skin-specific prefab
    // variants in the same manifest under other folders - picked the one
    // matching the same family every already-confirmed-working piece like
    // wall.doorway/cupboard.tool.deployed already comes from). Checked
    // before the live-entity scan, so nothing in a saved trace depends on
    // a live example of it happening to already exist somewhere on the
    // map anymore - the live scan below now only matters for a shortname
    // that shows up in some FUTURE trace this list hasn't caught up with
    // yet.
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

    // Real construction-piece family (2026-08-29, Lucas's own live report:
    // "the bot needs to have the building.planner out to place the
    // foundations and the hammer to upgrade the walls. It can't have the
    // hammer out to place foundations etc.") - none of these shortnames
    // have their own ItemDefinition (they're placed via the generic
    // building.planner the same way a real player's own building menu
    // works), unlike a deployable (cupboard.tool.deployed, door.hinged.*)
    // which is placed by holding the deployable ITEM itself. This is the
    // real, closed set this replay's own traces can produce.
    private static readonly HashSet<string> ConstructionPieceShortnames = new()
    {
        "foundation", "foundation.triangle", "wall", "wall.doorway",
        "wall.frame", "wall.window", "wall.low", "floor", "floor.triangle",
        "floor.frame", "roof", "stairs.spiral",
    };

    /// <summary>
    /// Equips whichever real inventory item matches shortname onto the
    /// belt and makes it the active held item - same real MoveToContainer-
    /// then-UpdateActiveItem-then-ForceRefreshHeldEntity sequence this
    /// project already uses for gather-tool switching
    /// (LivingRust.ResourceGathering.cs's own EquipBestMeleeTool). Returns
    /// false (does nothing) if the survivor doesn't actually have that item
    /// - callers should already know it exists (planner/hammer come from
    /// /lr.spawn.basebuilder, deployables from crafting or the same kit).
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
    /// Real live bug (2026-08-29, third round - Lucas's own report: "still
    /// teleporting to the roof occasionally" even after the walk
    /// destination's Y got clamped to ground level). Root cause: the
    /// general-purpose StartPhasingToDestination this replay was calling
    /// always finishes with a SnapToGround(npc) correction (see its own
    /// doc comment) - a real physics ground-height probe
    /// (NavigationManager.TryGetGroundHeight) that's exactly right for
    /// normal terrain (settling a bot that stopped mid-step on uneven
    /// ground) but wrong here: once the ceiling/floor tier has actually
    /// been placed directly above this exact footprint, that probe finds
    /// the freshly-placed floor's own TOP surface as the nearest solid
    /// collider straight down/up through the walk position and "corrects"
    /// the survivor up onto it - occasional because it only bites once
    /// that tier exists AND a later row's footprint sits underneath it
    /// (the door/cupboard here, both directly under the recorded ceiling).
    /// This is a straight copy of StartPhasingToDestination's own tick
    /// loop with only the final ground-snap removed - the walk Y this
    /// replay computes (AdvanceBuildReplay's own ground clamp) is already
    /// exactly where the survivor should end up, no post-arrival
    /// correction wanted at all.
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
    /// Generic column-index-by-header parsing (2026-08-28) - reads
    /// whatever columns are actually present rather than assuming a fixed
    /// position, so this keeps working whether the CSV was captured
    /// before or after OnStructureUpgraded's own event_type column was
    /// added (LivingRust.Debug.cs) - a file with no event_type column at
    /// all just defaults every row to "build", exactly matching what it
    /// actually recorded.
    /// </summary>
    private List<BuildTraceRow> ReadBuildTraceCsv(string path)
    {
        List<BuildTraceRow> rows = new();

        // Real live bug (2026-08-29) - File.ReadAllLines opens with
        // exclusive-by-default sharing, which throws a real
        // IOException("Sharing violation") whenever the trace this is
        // reading is STILL actively open for writing (the caller never
        // toggled /lr.debug.tracebuild back off first) - the whole replay
        // command threw before ever dispatching a single walk, which is
        // exactly why the survivor just stood there doing nothing at all.
        // Explicit FileShare.ReadWrite means this can read a still-being-
        // written trace file regardless of whether the writer's still
        // open, not just after it's been properly closed.
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

        return rows;
    }

    /// <summary>
    /// Real per-replay walking state (2026-08-28, Lucas's own live
    /// correction: the first pass just teleported the whole structure into
    /// existence in one instant, with the survivor never actually walking
    /// anywhere or spending real materials - "the bot didn't do anything
    /// nor did it remove any items from its inventory"). This carries the
    /// walk-then-place-then-continue chain forward one row at a time, same
    /// shape every other multi-step task in this project already uses
    /// (GatherTreeAndContinue, etc) - a callback chain, not a loop, since
    /// each step needs a real walk to actually finish first.
    /// </summary>
    private sealed class BuildReplayState
    {
        public List<BuildTraceRow> Rows;
        public int Index;
        public Vector3 TraceOrigin;
        public Vector3 OriginPosition;

        // Real source design path (2026-08-29, seventh round) - kept so
        // HomeBase.SourceDesignPath/BuildOriginPosition can be recorded on
        // completion, letting a hardcoded door route authored against any
        // instance of this exact design be found and re-projected onto
        // every other instance later (see HomeDoorRoute's own doc
        // comment). Relative to BaseDesignsDirectory, e.g. "tier0/base3.csv".
        public string CsvPath;

        // Real recording-workflow flag (2026-08-29, seventh round -
        // Lucas's own explicit ask: "spawns the base build type from each
        // tier, then I can trace it (without codelocked doors) to walk
        // through it"). Only ever true for a deliberate
        // /lr.debug.replaybuild ... nolock call - a real bot's own build
        // always places its locks normally. Skips every lock.code row
        // outright rather than placing-then-removing, since placing one
        // at all briefly locks the door for real (SetOpen/CanOpenDoor
        // would reject the recording player the instant it landed).
        public bool SkipCodeLocks;

        // Real independent ground-truth cross-check (2026-08-29) - see
        // TryFindClearBuildOrigin's own doc comment for why this is
        // sourced from TerrainMeta.HeightMap rather than the navmesh-based
        // probe AdvanceBuildReplay itself uses to place the first
        // foundation.
        public float ExpectedGroundHeight;

        // Real tier rank of the design being replayed (2026-08-29) - see
        // IsTierUnlocked's own doc comment. -1 means unknown (a raw trace
        // replayed outside the tier pool, e.g. via the old fallback path)
        // and never counts toward the tier0/tier1 prerequisite.
        public int TierRank = -1;

        public BaseEntity LastDoorway;

        // Real live reference to the tool cupboard this replay places, if
        // any (2026-08-29) - kept so StockToolCupboard can deposit real
        // upkeep materials into it once the whole build finishes.
        public BuildingPrivlidge Cupboard;

        // Real live reference to the HIGHEST-tier workbench this replay
        // places, if any (2026-08-29) - see the doc comment where these
        // get set for why "highest" matters.
        public BaseEntity Workbench;
        public int HighestWorkbenchTier = -1;

        // Real live entity references (2026-08-29, added for upgrade + code
        // lock replay). BlocksByTracePosition lets a later "upgrade" row
        // find the exact BuildingBlock a matching earlier "build" row
        // already spawned - keyed by the row's own ORIGINAL trace-space
        // position AND shortname (2026-08-29, second round - Lucas's own
        // report: a triangle foundation "gets missed and stays as
        // thatch"). Position ALONE turned out not to be unique enough -
        // confirmed via a real trace: a foundation.triangle and its own
        // wall.doorway sit at the EXACT same real X/Y/Z (the doorway is
        // centered directly on its triangle), so the doorway's own later
        // "build" row silently overwrote the triangle's dictionary entry
        // (same key, last write wins) - the triangle's own later upgrade
        // row then found and upgraded the WRONG piece (the doorway, a
        // second time) while the triangle itself never got touched at
        // all. Shortname added to the key so two genuinely different
        // piece types that happen to share a position can never collide.
        public BaseEntity LastDoor;
        public Dictionary<(Vector3 Position, string Shortname), BuildingBlock> BlocksByTracePosition = new();

        // Real lockable-target lookup (2026-08-29, second round - Lucas's
        // own report: "the bot placed two code locks on the same door, the
        // front door was left with no code lock"). A real trace can have
        // MULTIPLE lockable targets (more than one door, or a door AND the
        // tool cupboard - both real HasSlot(Slot.Lock) entities, confirmed
        // via decompile), but the old code always attached every lock.code
        // row to whichever ONE door was placed most recently, with no idea
        // which door a given lock was actually recorded next to. Keyed the
        // same way BlocksByTracePosition is - by each target's own ORIGINAL
        // trace-space position - so PlaceCodeLockReplayRow can find the
        // target whose real recorded position the lock's own row is
        // actually closest to, instead of guessing "the last one."
        public Dictionary<Vector3, BaseEntity> LockableTargetsByTracePosition = new();

        // Real building-membership chain (2026-08-29) - see the doc
        // comment right where this gets read in PlaceBuildReplayRow for
        // the full real bug this fixes (decay/upkeep/privilege never
        // working because every piece got its own isolated buildingID).
        public DecayEntity LastDecayEntity;

        // Real per-foundation ground reference (2026-08-29) - see
        // AdvanceBuildReplay's own doc comment for the full real bug this
        // fixes (foundations ending up buried/floating because a single
        // constant Y offset for the whole trace can't track real terrain
        // height changing across the footprint). Sourced from whichever
        // foundation-tier piece was placed most recently, since walls/
        // floors/doorways always stack directly off their own local
        // foundation rather than the terrain itself.
        public bool HasFoundationReference;
        public float LastFoundationOriginalY;
        public float LastFoundationNewY;

        public int Placed;
        public int Failed;
        public Action<int, int> OnComplete;
    }

    // How far above the real ground-probed height to bias the first
    // foundation's own placement - see AdvanceBuildReplay's own doc
    // comment for the full reasoning (a raycast ground probe can still
    // read a touch low right at a real edge/slope/riverbank).
    private const float FoundationGroundClearance = 1f;

    // How far the navmesh-based ground probe's reading can differ from
    // the independently-sourced real terrain height before this replay
    // concludes the survivor ended up somewhere it shouldn't have (see
    // AdvanceBuildReplay's own doc comment) rather than just standing on
    // slightly uneven ground. Generous enough that IsTooSlopedToBuild's
    // own real slope tolerance (MaxSlopeHeightDelta, 1.2m) plus normal
    // real-world terrain noise never false-triggers this.
    //
    // Tightened from 4f (2026-08-29, fifth round - Lucas's own live
    // report: a completed base's own front door ended up sitting ~3.07m
    // above the surrounding real ground/navmesh height - a bot phasing
    // through its own door correctly locked onto that door's real
    // recorded height and landed square on the roof, unable to path back
    // down, exactly reading as "instantly teleports outside and
    // oscillates on the walls"). Confirmed via every real base_designs/
    // trace file that no design's own door row is ever recorded away
    // from its base's foundation height - the corruption is a bad
    // ONE-TIME ground-height sample at THIS build's own first foundation
    // (every later piece in AdvanceBuildReplay's replay stacks relative
    // to that single reference, per its own doc comment above), not
    // anything baked into a trace file. A ~3m error like this was real
    // and should have aborted, but sat just under the old 4f tolerance.
    private const float GroundHeightMismatchTolerance = 1.5f;

    // Rounds to 2 decimal places so float parse/round-trip noise between a
    // piece's "build" row and its later "upgrade" row can't miss the
    // BlocksByTracePosition lookup above.
    private static Vector3 RoundTracePosition(Vector3 position)
    {
        return new Vector3(Mathf.Round(position.x * 100f) / 100f, Mathf.Round(position.y * 100f) / 100f, Mathf.Round(position.z * 100f) / 100f);
    }

    // Believable pause between one placement finishing and the survivor
    // moving on to the next - a real player doesn't instant-chain fifteen
    // placements either, this just keeps the sequence from reading as a
    // single instant burst even though there's no real swing/channel
    // animation construction placement itself needs.
    private const float BuildPlacementPaceSeconds = 1.5f;

    // How far short of each piece's exact placement spot to stop - see
    // AdvanceBuildReplay's own doc comment for the real live bug this
    // fixes (standing exactly where new solid geometry is about to spawn).
    private const float BuildApproachStandoffDistance = 2f;

    /// <summary>
    /// Real replay (2026-08-28, Lucas's own explicit request: "let's test
    /// the base building") - walks the survivor to each real piece's own
    /// position in turn (translated so the CSV's first row lands at
    /// originPosition, every other row keeping its exact same relative
    /// offset/rotation), placing and paying for one piece at a time rather
    /// than spawning the whole structure in a single instant. Grade is
    /// applied directly via SetGrade (confirmed real, non-RPC setter) -
    /// upgrade rows aren't replayed step-by-step, the end state is
    /// identical either way and this is far simpler. The door parents onto
    /// the most recently placed wall.doorway (matches the real trace
    /// evidence - the door's own recorded position was IDENTICAL to its
    /// wall.doorway's), and the tool cupboard grants real building
    /// authority via BuildingPrivlidge.AddPlayer (the exact same real call
    /// ItemModDeployable.OnDeployed already makes for a real player -
    /// confirmed via decompile during the sleeping bag work).
    // How far either side of the door, along the real building-centroid-
    // to-door axis, InsidePoint/OutsidePoint sit (2026-08-29). Comfortably
    // clear of the doorway's own frame/threshold collision either way.
    private const float HomeDoorRoutePointDistance = 2.5f;

    /// <summary>
    /// Real ghost-route computation (2026-08-29, Lucas's own explicit
    /// ask - see HomeBase's own doc comment for the full reasoning).
    /// Building centroid is the average real position of every
    /// BuildingBlock this replay actually placed (state.
    /// BlocksByTracePosition's own live entity references, not the raw
    /// trace coordinates - reflects wherever the pieces really ended up
    /// after ground-checking/translation), and the door is whichever
    /// door.hinged.* this replay placed most recently (state.LastDoor) -
    /// the same "most recent door" convention PlaceCodeLockReplayRow's
    /// own fallback already uses elsewhere in this file. A design with no
    /// door at all leaves home.DoorPosition at its default Vector3.zero -
    /// callers already know to treat that as "no route available."
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

    // Real radius to look for a live door near a translated door-route
    // waypoint (2026-08-29, seventh round) - generous enough to catch a
    // route recorded with a couple metres of natural variance in exactly
    // where the recording player stood relative to the door itself.
    private const float DoorRouteAnchorSearchRadius = 2f;

    // Obviously-unmatched sentinel (2026-08-29) - passed as
    // TryLoadTraceWaypoints' own requiredMonumentName so it NEVER
    // re-projects a door-route recording onto a nearby real monument,
    // regardless of how close a base happens to be built to one; door
    // routes are always base-design-relative (BuildOriginPosition),
    // never monument-relative, and TryLoadTraceWaypoints has no other way
    // to opt out of that conversion entirely.
    private const string DoorRouteMonumentAnchorSentinel = "__no_real_monument_ever_matches_this__";

    /// <summary>
    /// Real hardcoded door ghost routes (2026-08-29, seventh round) - see
    /// HomeBase.DoorRoutes' own doc comment for the full story. Loads
    /// every *.csv LivingRust.Debug.cs's own /lr.debug.savedoorroute has
    /// saved for this exact design (silently does nothing if none exist
    /// yet - same "folder starts empty" convention as
    /// MonumentGhostRouteFolders), re-projects each one onto THIS live
    /// instance by adding home.BuildOriginPosition back on (the exact
    /// inverse of what /lr.debug.savedoorroute subtracted when it saved
    /// it), and resolves which real live door each route belongs to by
    /// proximity.
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
        // No longer filtering "upgrade" rows out (2026-08-29, Lucas's own
        // explicit ask - "let's now work on upgrading the base with the
        // hammer"). The CSV's own row order is already real chronological
        // order (that's the order OnEntityBuilt/OnStructureUpgraded wrote
        // them in), so replaying every row in sequence naturally means
        // "build the whole shape first, then walk back around upgrading
        // whatever was upgraded later" - exactly what actually happened.
        List<BuildTraceRow> rows = ReadBuildTraceCsv(csvPath);

        if (rows.Count == 0)
        {
            onComplete(0, 0);
            return;
        }

        // Derived directly from the design's own file path rather than
        // threading a new parameter through every caller in this chain
        // (ClearBuildSiteThenReplay, RunDebugReplayBuild) - every real
        // design path already looks like ".../base_designs/tier2/base3.csv",
        // so the tier folder name is right there. -1 (unknown) for
        // anything replayed outside the tier pool, e.g. the old raw-trace
        // fallback path.
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

    // Real minimum upkeep stock (2026-08-29, Lucas's own exact numbers:
    // "each tool cupboard container should be filled with at least
    // 1000wood, 2000 stone and 300 metal fragments").
    private const int CupboardMinimumWood = 1000;
    private const int CupboardMinimumStone = 2000;
    private const int CupboardMinimumMetalFragments = 300;

    /// <summary>
    /// Real tool-cupboard stocking (2026-08-29, Lucas's own explicit ask:
    /// "for a decent start"). Draws from the survivor's OWN real
    /// inventory first (PlayerInventory.Take, the same real collection
    /// method DeductRealConstructionCost already uses) - genuine leftover
    /// materials from farming should fund this before anything gets
    /// conjured from nothing - and only tops up whatever's still short
    /// with freshly-created items, so the real "at least" minimum is
    /// always guaranteed regardless of how much the survivor happened to
    /// have left over. Skips a resource that's already at or above the
    /// minimum already sitting in the cupboard (e.g. a design that places
    /// its own opening deposit as part of the trace itself).
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
                // Cupboard's genuinely full - give it back rather than
                // silently destroying real materials the survivor owned.
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
            // Real live ask (2026-08-29, Lucas's own explicit request: "we
            // need to have the bots fill their tool cupboards with
            // resources... at least 1000 wood, 2000 stone and 300 metal
            // fragments"). Only once the whole replay has actually
            // finished (not on every intermediate row), and only if a
            // cupboard actually got placed this run.
            if (npc != null && !npc.IsDestroyed && state.Cupboard != null && !state.Cupboard.IsDestroyed)
            {
                StockToolCupboard(npc, state.Cupboard);
            }

            // Real home-base persistence (2026-08-29, Lucas's own
            // explicit ask: "if the bot has built a base of any tier, it
            // is persisted throughout server restarts... this is where
            // the bot will deposit loot and store items, as well as
            // craft higher tiered items"). Only recorded once a real
            // cupboard actually exists to anchor it to - a design with no
            // cupboard row genuinely has nothing for "home" to mean here.
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

                // Real prerequisite tracking (2026-08-29) - see
                // IsTierUnlocked's own doc comment. Never cleared once
                // true, unlike Home above which tracks the latest base -
                // a tier0/tier1 build that actually placed at least one
                // real piece satisfies prerequisite A for every tier2+
                // attempt from now on for this Character, even after it
                // later builds something bigger.
                if (state.TierRank is 0 or 1)
                {
                    survivor.Character.HasCompletedLowerTierBaseBuild = true;
                }

                // Saved immediately, same reasoning /lr.debug.despawnall's
                // own doc comment already gives for doing this rather than
                // waiting for the next natural save point - a real
                // restart shouldn't be able to silently lose a base that
                // was only ever in memory.
                _engine.SaveManager.SaveCharacters(_engine.CharacterManager.GetAllCharacters());

                // Real explicit exit-before-anything-else gate (2026-08-29,
                // eighth round - Lucas's own proposed fix after yet
                // another live "teleported straight out, no door
                // interaction at all" report: "I have finished task -> am
                // I inside of the base I built? Yes? -> exit base using
                // ghostroute in reverse... re-roll task for whatever I
                // want to do next"). Root cause of that specific report:
                // a survivor's very next task decision (loot/craft/
                // whatever) kicks off a normal walk toward WHATEVER
                // destination it rolls, and that walk's own native-
                // movement setup (EnsureNativeNavAgent) calls Unity's
                // real NavMeshAgent.Warp() unconditionally - which
                // SILENTLY snaps to the nearest point actually ON the
                // baked static navmesh whenever the given position isn't
                // already on it (real, documented Unity behaviour). A
                // survivor standing inside the base it JUST finished
                // building is standing somewhere the static navmesh has
                // no idea exists at all - so the very first native walk
                // afterward can yank it several metres outside instantly,
                // with none of this project's own door-crossing checks
                // ever getting a chance to run first (they run before
                // this, not after). Rather than keep chasing every new
                // way the general walk system can get blindsided, this
                // makes leaving home an explicit, guaranteed FIRST step
                // right at the one moment it's certain to be needed -
                // before ANY native movement/Warp is ever engaged for
                // this life's next task.
                ExitHomeIfInside(survivor, home, () => state.OnComplete(state.Placed, state.Failed));
                return;
            }

            state.OnComplete(state.Placed, state.Failed);
            return;
        }

        BuildTraceRow row = state.Rows[state.Index];
        state.Index++;

        Vector3 worldPosition = state.OriginPosition + (row.Position - state.TraceOrigin);

        // Real live bug (2026-08-29, Lucas's own report: "the bot is still
        // placing foundations well below the ground Y axis... not possible
        // to this extent"). Root cause: this whole replay only ever applies
        // ONE constant Y offset to every row (immediately above), derived
        // from the survivor's own single starting height - it never
        // resamples real terrain height per piece, so it only produces a
        // correct result when the new location's terrain is exactly as
        // flat/level as wherever the trace was originally recorded (the
        // same structural gap IsTooSlopedToBuild's pre-flight check flags,
        // just not fully solved by refusing to start on a bad slope alone -
        // even "acceptably flat" ground can still be a bit off from the
        // original). Fixed via a real terrain height ground-check
        // (TerrainMeta.HeightMap.GetHeight, the same real API
        // TestPlacingCloseToRoad itself uses).
        //
        // Ground-checked ONCE only, on the very first foundation
        // (2026-08-29, second round - Lucas's own follow-up report:
        // ground-checking EVERY foundation independently made adjacent
        // foundations sit at different heights, since no two terrain
        // samples are ever perfectly identical - "extremely weird" as he
        // put it, and real foundations always connect flush regardless of
        // minor terrain bumps underneath). Every foundation after the
        // first, same as every wall/floor/doorway/deployable, now stacks
        // relative to that one real ground sample instead of independently
        // re-sampling - keeps the whole footprint level and connected the
        // way a real build actually looks.
        bool isFirstFoundationRow = !state.HasFoundationReference && row.EventType != "upgrade" && (row.Shortname == "foundation" || row.Shortname == "foundation.triangle");

        if (isFirstFoundationRow)
        {
            // Real live bug (2026-08-29, Lucas's own report, second round -
            // the structure is now level/connected but sitting BELOW real
            // ground, "impossible"). Root cause: TerrainMeta.HeightMap.
            // GetHeight only samples the coarse, low-resolution base
            // terrain height GRID - it has no idea about rocks, terrain
            // paint detail, or local surface variation the actual visible/
            // walkable ground includes, and can read meaningfully lower
            // than the real surface at any given spot. Switched to
            // NavigationManager.TryGetGroundHeight instead - this
            // project's own already-proven real ground probe (used by
            // SnapToGround to fix bots hovering/sinking elsewhere), a real
            // multi-ray consensus raycast against the actual terrain
            // collider rather than a coarse height array lookup, so it
            // matches whatever surface a player would actually see/stand
            // on at that exact spot.
            if (_engine.NavigationManager.TryGetGroundHeight(worldPosition, out float groundHeight))
            {
                // Real live bug (2026-08-29, fourth round - Lucas's own
                // report: a bot teleported 10-15m into the air and started
                // building there, "we need to avoid the bots building up
                // in the air"). The navmesh-based probe above answers
                // "what's directly under the survivor right now" - it
                // can't tell the difference between real ground and some
                // orphaned/elevated navmesh island a bad emergency-
                // teleport recovery dropped the survivor onto mid-walk
                // (NavMesh.SamplePosition has no ground-connectivity
                // guarantee - see StartWalkingWithRecovery's own stuck-
                // recovery chain). Cross-checking it against
                // ExpectedGroundHeight - a real terrain-only reading
                // (TerrainMeta.HeightMap) taken independently back when
                // this site was first validated, before any walk that
                // could go wrong - catches exactly that: a huge mismatch
                // means the survivor genuinely isn't where it's supposed
                // to be anymore, not that the terrain here is just a bit
                // uneven.
                if (Mathf.Abs(groundHeight - state.ExpectedGroundHeight) > GroundHeightMismatchTolerance)
                {
                    Puts($"basebuild-replay: '{survivor.Character.Alias}' is {Mathf.Abs(groundHeight - state.ExpectedGroundHeight):F1}m away from the real expected ground height here (currently at {groundHeight:F1}, expected ~{state.ExpectedGroundHeight:F1}) - looks like it ended up somewhere it shouldn't have (a bad stuck-recovery teleport is the likely cause). Aborting rather than building in mid-air.");
                    state.OnComplete(state.Placed, state.Failed + 1);
                    return;
                }

                // Small real safety margin (2026-08-29, third round -
                // Lucas's own explicit ask: "have the bot try place the
                // foundations at 1 metre above their current Y axis
                // level"). Even a real raycast-based ground probe can
                // still read a touch low right at an edge (a slope, a
                // riverbank, a cliff) - biasing the result 1m up costs
                // nothing real (a foundation always has real support
                // pillars auto-filling any gap underneath, same as any
                // real player's elevated foundation over uneven ground)
                // and trades a small, harmless visible gap for never
                // clipping/phasing into terrain again.
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

        // Real live bug (2026-08-29, Lucas's own report, second round):
        // "keeps teleporting out... does not look smooth at all". The
        // normal StartWalkingWithRecovery pipeline (navmesh CalculatePath +
        // wiggle/nudge/emergency-teleport escalation) is built for organic
        // terrain navigation, and it was never going to cope with a
        // construction site that changes shape every 1.5s - the navmesh
        // has no idea a wall or floor piece just appeared, so the survivor
        // kept "getting stuck" against its own just-placed geometry and
        // tripping the jarring emergency-teleport tier repeatedly (the
        // "teleporting out" being described).
        //
        // Lucas's own explicit fix, matching what he already suggested for
        // ceilings last session ("If the bot uses phasing... then maybe it
        // wouldn't get stuck") and confirmed again live just now ("they
        // just need to be inside on top of the foundations they place"):
        // this whole replay doesn't need real terrain pathing at all - it's
        // a short, already-known-safe hop between two points on the SAME
        // structure the bot itself is actively building. StartPhasingToDestination
        // (this project's own existing raw-position, collision-bypassing
        // walk, already used as the last-resort tier of the normal stuck
        // ladder) reads as smooth ordinary running with zero risk of
        // getting caught on new geometry.
        //
        // Still keeping the WALK destination pinned to the replay's own
        // ground level even so (2026-08-29, still true after switching to
        // phasing - Lucas's own report: "still goes to the roof to place
        // the ceiling(s)"): a real player reaches UP to place a ceiling
        // from standing on the wall/foundation below, never actually rises
        // to stand in mid-air at the piece's own recorded height, and
        // Lucas's original "on top of the foundations" framing was about
        // not standing OUTSIDE the footprint on raw unbuilt terrain, not
        // about matching every row's exact Y. The entity itself still
        // SPAWNS at its true recorded height (below) regardless of where
        // the survivor's own feet are standing - only the walk target's Y
        // is pinned. Assumes one flat single-story footprint, matching
        // this replay's own current scope.
        // Walk Y tracks the same real ground-checked foundation reference
        // above rather than the replay's original starting height - keeps
        // this consistent with the piece's own real spawn height now that
        // both come from an actual terrain sample instead of an assumed-
        // flat constant.
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

        // Real, separate replay branch (2026-08-29, Lucas's own explicit
        // ask - "upgrading the base with the hammer") - an upgrade row
        // doesn't spawn anything new, it changes the grade of a piece a
        // matching earlier "build" row already placed. Handled entirely
        // separately since none of the create/equip/parent logic below
        // applies to it.
        if (row.EventType == "upgrade")
        {
            PlaceUpgradeReplayRow(survivor, state, row);
            return;
        }

        // Real, separate replay branch (2026-08-29, Lucas's own explicit
        // ask - "placing code locks") - a code lock isn't a free-standing
        // piece, it slots onto the most recently placed real door via
        // BaseEntity's own Slot.Lock anchor (confirmed via decompile,
        // SpawnLock/Reskin_Restore's real pattern), not a normal
        // CreateEntity-at-worldPosition placement.
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

        // Real held-tool switch (2026-08-29) - a construction piece
        // (foundation/wall/floor/etc) is placed by holding the generic
        // building.planner, never the piece itself (there's no such item);
        // a deployable is placed by holding the deployable item itself.
        // Equipping the wrong tool doesn't actually block CreateEntity
        // below (that's a direct server-side spawn, not the real client
        // Planner.DoBuild RPC), but this keeps what the bot is visibly
        // holding honest instead of leaving whatever gather tool was out
        // beforehand.
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

        // Real live bug (2026-08-29, Lucas's own report: the tool cupboard
        // shows decaying even with resources inside, no upkeep cost list,
        // and no "building privilege/blocked" toast near it at all).
        // Confirmed via decompile: real construction NEVER goes through a
        // bare GameManager.server.CreateEntity - it's always
        // Construction.CreateConstruction, which immediately calls
        // decayEntity.AttachToBuilding(target.entity as DecayEntity) BEFORE
        // Spawn() (same real order used here). Every piece this replay
        // creates was skipping that entirely, so every single foundation/
        // wall/floor/cupboard/door ended up as its OWN isolated one-block
        // "building" with its own random buildingID - BuildingManager could
        // never find more than one block per building, so upkeep cost,
        // decay protection, and the privilege UI all had nothing real to
        // compute against. Chaining each new piece onto the PREVIOUS one
        // (state.LastDecayEntity) merges them all into one real shared
        // building, exactly matching what actually happens when a real
        // player places each piece touching the last (AttachToBuilding
        // internally calls BuildingManager.server.CheckMerge too, so
        // physically touching pieces later placed in different order still
        // merge correctly regardless of chain order). The very first piece
        // legitimately gets null (no earlier piece exists yet), which is
        // the exact real trigger for a brand new buildingID.
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

        // Real live bug (2026-08-28) - blockDefinition (and everything
        // SetGrade reads off it) is only populated during the entity's own
        // spawn lifecycle, confirmed via a real NullReferenceException when
        // this was called before Spawn(). Grade/health applied AFTER, not
        // before.
        if (newEntity is BuildingBlock block && !string.IsNullOrEmpty(row.Grade) && Enum.TryParse(row.Grade, out BuildingGrade.Enum gradeEnum))
        {
            // Real placement is always Twigs first, everything above that
            // is a separate hammer upgrade swing in real Rust - so a row
            // recording anything higher means this piece was upgraded
            // after being placed. Swap to the hammer for that step alone
            // (Lucas's own report: "the hammer to upgrade the walls") even
            // though SetGrade itself is a direct setter, not the real
            // swing RPC - this keeps the held item honest either way.
            if (gradeEnum != BuildingGrade.Enum.Twigs)
            {
                EquipHeldItemByShortname(npc, HammerShortname);
            }

            // Real full grade-change path (2026-08-29, upgraded from a bare
            // SetGrade+SetHealthToMax) - ChangeGrade is the actual public
            // method BuildingBlock.DoUpgradeToGrade itself calls (confirmed
            // via decompile), and does the real bookkeeping a bare SetGrade
            // skips: SendNetworkUpdate, ResetUpkeepTime, and
            // UpdateSurroundingEntities/BuildingManager.Dirty (the exact
            // real upkeep/decay wiring Lucas's own earlier report flagged -
            // "the base decays without it" - a tool cupboard alone doesn't
            // help if the blocks themselves never registered the change).
            block.ChangeGrade(gradeEnum, playEffect: true);
            block.SetHealthToMax();
            DeductRealConstructionCost(npc, block, gradeEnum);
            state.BlocksByTracePosition[(RoundTracePosition(row.Position), row.Shortname)] = block;

            // Real placement sound (2026-08-29, Lucas's own explicit ask -
            // "every time I place a foundation, wall, anything... it
            // triggers a noise"). Confirmed via decompile: Planner.DoBuild
            // itself plays block.blockDefinition.placeEffect (falling back
            // to the same generic "frame_place" effect the real client
            // falls back to when a piece has none of its own) through the
            // entity-attached overload - blockDefinition is only populated
            // post-Spawn, same as the SetGrade timing fix above.
            string blockPlaceEffect = block.blockDefinition.placeEffect.isValid
                ? block.blockDefinition.placeEffect.resourcePath
                : "assets/bundled/prefabs/fx/build/frame_place.prefab";

            Effect.server.Run(blockPlaceEffect, newEntity, 0u, Vector3.zero, Vector3.zero);
        }
        else
        {
            // A deployable (cupboard/door) - already crafted/given, paid
            // for once at craft time (LivingRust.Crafting.cs) or by
            // /lr.spawn.basebuilder directly. Placing it just consumes the
            // ONE inventory item, same real UseItem(1) call Deployer.
            // DoDeploy_Regular already makes - no separate construction
            // cost the way a BuildingBlock has.
            Item heldItem = npc.inventory.FindItemByItemID(ItemManager.FindItemDefinition(row.Shortname)?.itemid ?? 0);
            heldItem?.UseItem(1);

            // Real placement sound for deployables (2026-08-29, same ask -
            // "furnaces make a noise when placed, doors, tool cupboards,
            // wood boxes etc"). Confirmed via decompile: Planner.DoBuild's
            // own deployable branch plays Deployable.placeEffect (a real
            // PrefabAttribute, looked up off the entity's own prefabID -
            // same PrefabAttribute.server.Find<T> pattern already used for
            // Construction in DeductRealConstructionCost) at the world
            // position/normal, not attached to the entity.
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

            // Real Rust: the tool cupboard has its own real HasSlot(Slot.Lock)
            // too (confirmed via /lr.debug.scan on a live one earlier this
            // session - "components: [..., BuildingPrivlidge, ...]" with a
            // real Slot.Lock check), so a lock.code row can be meant for the
            // cupboard, not just a door - tracked the same way for the
            // nearest-match lookup below.
            state.LockableTargetsByTracePosition[RoundTracePosition(row.Position)] = newEntity;
        }
        else if (row.Shortname.StartsWith("workbench") && row.Shortname.EndsWith(".deployed"))
        {
            // Real home-base tracking (2026-08-29) - keeps whichever
            // workbench is the HIGHEST tier this design places (a design
            // could legitimately place more than one), since that's the
            // one that actually matters for "what can this survivor craft
            // at home" (Lucas's own explicit ask).
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

    // Real, fixed prefab path (2026-08-29, confirmed via decompile -
    // BaseOccupiedTerritory.SpawnLock's own literal, the exact call a real
    // player's own lock deploy ends up going through) - used only as a
    // fallback when no code lock already exists anywhere on the map for
    // TryResolveConstructionPrefabPath to find live, same reasoning
    // /lr.spawn.basebuilder's own hardcoded shortnames already document.
    private const string RealCodeLockPrefabPath = "assets/prefabs/locks/keypad/lock.code.prefab";

    /// <summary>
    /// Real code lock placement (2026-08-29, Lucas's own explicit ask -
    /// "placing code locks", held off until now per his own earlier call:
    /// "let's hold off implementing it until we're actually placing the
    /// lock as part of the replay sequence"). Mirrors the real, confirmed
    /// SpawnLock/Reskin_Restore slot-anchor sequence exactly: SetParent
    /// using the door's own real GetSlotAnchorName(Slot.Lock) BEFORE
    /// Spawn(), then the door's own SetSlot(Slot.Lock, ...) AFTER - a code
    /// lock isn't a free-floating deployable, it only exists meaningfully
    /// attached to one specific door. Sets a real random 4-digit code and
    /// whitelists the survivor's own userID - confirmed via decompile
    /// (CodeLock.RPC_ChangeCode) that TryLock/TryUnlock only ever check
    /// code.Length == 4 and whitelistPlayers.Contains(userID), so this
    /// reaches the exact same end state a real player's own RPC call would,
    /// without needing to fake the RPC itself. Flags.Locked is set
    /// directly too (RPC_ChangeCode's own real behaviour first time a code
    /// is set) so the lock actually reads as locked, not just coded.
    /// </summary>
    private void PlaceCodeLockReplayRow(Survivor survivor, BuildReplayState state, BuildTraceRow row, Vector3 worldPosition)
    {
        BasePlayer npc = survivor.Player;

        // Real live bug (2026-08-29, second round - Lucas's own report:
        // "the bot placed two code locks on the same door, the front door
        // was left with no code lock"). A trace with more than one door
        // (or a door AND the cupboard) records each lock.code row right
        // next to whichever specific target it actually belongs to, but
        // always grabbing "whatever was placed most recently" ignored
        // that entirely. Finds the REAL closest lockable target by each
        // one's own original trace-space position instead, matching each
        // lock to the specific door/cupboard it was actually recorded
        // beside.
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

        // Real live bug found (2026-08-29, Lucas's own live report - he
        // teleported to the lock's logged position and got kicked for an
        // "insideterrain violation", finding the lock had ended up deep
        // underground). Root cause confirmed via decompile of
        // BaseEntity.SetParent(entity, boneID, worldPositionStays): when
        // worldPositionStays is FALSE (what the real SpawnLock/
        // Reskin_Restore pattern below uses, and what CodeLock.RPC_ChangeCode
        // itself never needs to touch since a real client always deploys a
        // lock with local space already zeroed), Unity's own Transform.
        // SetParent does NOT recompute the child's world position from its
        // OLD one - it keeps whatever LOCAL offset the child already had
        // and reinterprets it relative to the new parent. Creating the
        // codeLock at the trace's full WORLD position first (like every
        // other row in this replay does) meant its local offset started
        // out as roughly that same coordinate - so parenting onto the door
        // with worldPositionStays:false then applied THAT as an
        // ADDITIONAL local offset on top of the door's own real position,
        // landing the lock nowhere near either. Spawning at the origin
        // instead (matching the real SpawnLock call, which creates the
        // entity with no position argument at all) means the local offset
        // starts at zero, so parenting onto the anchor bone lands it
        // exactly there, same as a real player's own deploy.
        //
        // Same exact reasoning applies to ROTATION too (2026-08-29,
        // Lucas's own live report: the lock visually sits tilted ~45
        // degrees on the cupboard) - the trace's own recorded world
        // rotation gets kept as a stale LOCAL rotation once parented, then
        // reapplied on top of whatever the new parent's own orientation
        // is, instead of aligning naturally with the anchor bone. Real
        // SpawnLock never sets an explicit rotation at all - identity, so
        // the anchor bone's own real orientation is what actually
        // determines how the lock sits.
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

        // Extra diagnostic detail (2026-08-29, Lucas's own live report -
        // the lock places server-side with no error but never actually
        // shows up client-side). Logs exactly what SpawnLock's own real
        // sequence would produce - the resolved prefab path (and whether
        // it came from a live map match or our own hardcoded fallback),
        // and the lock's own FINAL position/rotation after
        // SetParent(..., GetSlotAnchorName(Slot.Lock)) actually ran, so
        // this can be compared directly against a real player-placed
        // lock's own data via /lr.debug.look aimed at each one in turn.
        Puts($"basebuild-replay: '{survivor.Character.Alias}' placed and locked a code lock (code {realCode}) on '{lockTarget.ShortPrefabName}' - prefab '{prefabPath}', final pos {codeLock.transform.position}, net.ID {codeLock.net?.ID}, parent bone anchor '{lockTarget.GetSlotAnchorName(BaseEntity.Slot.Lock)}'.");

        timer.Once(BuildPlacementPaceSeconds, () => AdvanceBuildReplay(survivor, state));
    }

    /// <summary>
    /// Real wall/foundation upgrade replay (2026-08-29, Lucas's own
    /// explicit ask - "upgrading the base with the hammer"). Doesn't walk
    /// to the piece itself first the way AdvanceBuildReplay's normal build
    /// rows do (an upgrade row's own recorded position already matches an
    /// existing piece 1:1, so there's nothing new to approach - the caller
    /// already walked/stood there for this exact row like any other), just
    /// swaps to the hammer and applies the real incremental cost/grade
    /// change directly. ConstructionGrade.CostToBuild(fromGrade) is the
    /// exact real incremental cost method BuildingBlock.PayForUpgrade
    /// itself calls (confirmed via decompile) - NOT the full from-scratch
    /// cost DeductRealConstructionCost's own initial-placement caller uses.
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

        // Real upgrade sound/particle (2026-08-29, same ask as the
        // placement noise fix - confirmed via decompile:
        // BuildingBlock.DoUpgradeToGrade's own real call, a client-side
        // effect triggered purely through this ClientRPC, not
        // Effect.server.Run/a placeEffect field the way initial placement
        // works).
        block.ClientRPC(RpcTarget.NetworkGroup("DoUpgradeEffect"), (int)targetGrade, 0uL);

        state.Placed++;
        Puts($"basebuild-replay: '{survivor.Character.Alias}' upgraded a piece from {fromGrade} to {targetGrade}.");

        timer.Once(BuildPlacementPaceSeconds, () => AdvanceBuildReplay(survivor, state));
    }

    /// <summary>
    /// Real construction cost, paid at the moment of placement (2026-08-28,
    /// Lucas's own explicit ask - the bot needs to actually spend
    /// materials, not just have them appear pre-paid). Construction.GetGrade
    /// (confirmed real via decompile - the exact method Planner.DoBuild
    /// itself calls) returns the real ConstructionGrade for this piece/
    /// grade combination, and ConstructionGrade.CostToBuild() is the exact
    /// real ingredient list - nothing here guesses at costs, same standard
    /// every other recipe in this project already holds to.
    /// PlayerInventory.Take collects matching items without destroying them
    /// (confirmed via decompile, same real method ItemCrafter.
    /// CollectIngredient already uses) - UseItem on each collected item is
    /// what actually consumes/destroys them, mirroring FinishCrafting's own
    /// real consumption step.
    /// </summary>
    private void DeductRealConstructionCost(BasePlayer npc, BuildingBlock block, BuildingGrade.Enum gradeEnum, BuildingGrade.Enum fromGrade = BuildingGrade.Enum.None)
    {
        Construction construction = PrefabAttribute.server.Find<Construction>(block.prefabID);
        ConstructionGrade constructionGrade = construction?.GetGrade(gradeEnum, 0);

        if (constructionGrade == null)
        {
            return;
        }

        // fromGrade defaults to None (the full from-scratch cost, real
        // initial-placement behaviour) but an upgrade replay row passes the
        // piece's own real CURRENT grade instead - CostToBuild(fromGrade)
        // is the exact real incremental-cost method
        // BuildingBlock.PayForUpgrade/CanAffordUpgrade both call (confirmed
        // via decompile), so a wood->stone upgrade only ever charges the
        // real difference, not wood's own cost twice over.
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
