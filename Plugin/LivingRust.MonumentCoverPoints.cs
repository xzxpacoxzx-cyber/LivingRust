using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Monument-specific cover-point discovery, the second half of TryFindCoverPoint.
/// Pre-scans each monument type once, caches results in the monument's local
/// space, and re-projects them onto the real instance on the current map, so
/// runtime lookups test cached candidates instead of scanning live each fight.
/// </summary>
public partial class LivingRust
{
    private sealed class MonumentCoverPoint
    {
        public Vector3 LocalOffset;
    }

    private const string MonumentCoverPointsSaveFile = "LivingRust/monument_cover_points.json";

    private readonly Dictionary<string, List<MonumentCoverPoint>> _monumentCoverPoints = new();

    private void LoadMonumentCoverPoints()
    {
        if (!File.Exists(MonumentCoverPointsSaveFile))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(MonumentCoverPointsSaveFile);
            Dictionary<string, List<MonumentCoverPoint>>? loaded = JsonConvert.DeserializeObject<Dictionary<string, List<MonumentCoverPoint>>>(json);

            if (loaded != null)
            {
                _monumentCoverPoints.Clear();

                foreach (KeyValuePair<string, List<MonumentCoverPoint>> entry in loaded)
                {
                    _monumentCoverPoints[entry.Key] = entry.Value;
                }

                Puts($"Loaded monument cover points for {_monumentCoverPoints.Count} monument type(s) from {MonumentCoverPointsSaveFile}.");
            }
        }
        catch (Exception ex)
        {
            Puts($"WARNING: Failed to load monument cover points from {MonumentCoverPointsSaveFile}: {ex.Message}");
        }
    }

    private void SaveMonumentCoverPoints()
    {
        try
        {
            if (!Directory.Exists("LivingRust"))
            {
                Directory.CreateDirectory("LivingRust");
            }

            string json = JsonConvert.SerializeObject(_monumentCoverPoints, Formatting.Indented);
            File.WriteAllText(MonumentCoverPointsSaveFile, json);
        }
        catch (Exception ex)
        {
            Puts($"WARNING: Failed to save monument cover points to {MonumentCoverPointsSaveFile}: {ex.Message}");
        }
    }

    /// <summary>
    /// Scans every distinct monument type once, skipping types already known from a
    /// previous boot. Runs separately from ScanAllMonumentsOnBoot.
    /// </summary>
    private void ScanAllMonumentsForCoverPointsOnBoot()
    {
        if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null)
        {
            Puts("WARNING: TerrainMeta.Path.Monuments isn't available yet - skipping the boot-time monument cover scan.");
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

            if (_monumentCoverPoints.TryGetValue(monument.name, out List<MonumentCoverPoint> existingPoints) && existingPoints.Count > 0)
            {
                skippedAlreadyKnownCount++;
                continue;
            }

            ScanMonumentForCoverPoints(monument);
            scannedCount++;
        }

        Puts($"monument-cover-points: boot scan complete - {seenTypesThisScan.Count} distinct monument type(s) on this map, {scannedCount} newly scanned, {skippedAlreadyKnownCount} already known from a previous session.");
    }

    /// <summary>
    /// Only real structure counts as a candidate wall to hug; excludes "Terrain"
    /// from the search mask since it is one continuous mesh covering the whole map.
    /// </summary>
    private static readonly int MonumentCoverStructureMask = LayerMask.GetMask("World", "Construction", "Tree", "Default");

    /// <summary>
    /// A collider narrower than this on both horizontal axes is excluded as trivial
    /// clutter; genuine sightline blocking is still verified later by IsPartialCoverPoint.
    /// </summary>
    private const float CoverPointMinStructureSize = 0.5f;

    /// <summary>
    /// How far out from a qualifying structure's face the sampled candidate point
    /// sits, so it doesn't stand flush against the wall.
    /// </summary>
    private const float CoverPointStandoffFromStructure = 1.5f;

    /// <summary>
    /// Cached points closer together than this collapse to one, since a single
    /// large structure can otherwise generate several near-duplicate candidates
    /// from adjacent sampled faces.
    /// </summary>
    private const float CoverPointDedupDistance = 4f;

    /// <summary>
    /// Hard cap on how many cover points get cached per monument type. This is a
    /// one-time boot-time scan, not a live per-fight cost, and the runtime lookup
    /// stays cheap even at a few hundred entries, so there's room to be generous.
    /// </summary>
    private const int CoverPointMaxPerMonument = 200;

    private List<MonumentCoverPoint> ScanMonumentForCoverPoints(MonumentInfo monument)
    {
        float scanRadius = Mathf.Max(monument.Bounds.extents.x, monument.Bounds.extents.z, MonumentLootScanFallbackRadius / 2f) + 15f;

        Collider[] hits = Physics.OverlapSphere(monument.transform.position, scanRadius, MonumentCoverStructureMask, QueryTriggerInteraction.Ignore);

        List<Vector3> rawCandidates = new();
        Vector3[] sideDirections = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };

        foreach (Collider hit in hits)
        {
            Bounds bounds = hit.bounds;

            if (Mathf.Max(bounds.size.x, bounds.size.z) < CoverPointMinStructureSize)
            {
                // Prop/decoration, not substantial structure.
                continue;
            }

            foreach (Vector3 direction in sideDirections)
            {
                Vector3 facePoint = bounds.center + Vector3.Scale(direction, bounds.extents);
                rawCandidates.Add(facePoint + direction * CoverPointStandoffFromStructure);
            }
        }

        List<MonumentCoverPoint> coverPoints = new();

        foreach (Vector3 candidateXZ in rawCandidates)
        {
            if (coverPoints.Count >= CoverPointMaxPerMonument)
            {
                break;
            }

            if (!_engine.NavigationManager.TryFindGroundBelow(candidateXZ + Vector3.up * 4f, 4f, 8f, out float groundY, out _))
            {
                // No real ground near this candidate at all - not a viable
                // standing point regardless of what structure it's next to.
                continue;
            }

            Vector3 groundPoint = new Vector3(candidateXZ.x, groundY, candidateXZ.z);

            if (_engine.NavigationManager.IsBodyOverlapping(groundPoint))
            {
                continue;
            }

            bool isDuplicate = coverPoints.Any(existing =>
                Vector3.Distance(monument.transform.TransformPoint(existing.LocalOffset), groundPoint) < CoverPointDedupDistance);

            if (isDuplicate)
            {
                continue;
            }

            coverPoints.Add(new MonumentCoverPoint { LocalOffset = monument.transform.InverseTransformPoint(groundPoint) });
        }

        _monumentCoverPoints[monument.name] = coverPoints;
        SaveMonumentCoverPoints();

        VerbosePuts($"scanmonumentcover-diag: '{monument.name}' found {coverPoints.Count} candidate cover point(s) from {hits.Length} nearby structure collider(s) (scan radius {scanRadius:F0}m).");

        return coverPoints;
    }

    /// <summary>
    /// Extra flat margin added on top of a monument's own real bounds-based
    /// radius (same formula ScanMonumentForCoverPoints itself uses) when
    /// deciding whether a position counts as "near" it for cover purposes -
    /// covers a fight breaking out just outside the monument's own strict
    /// footprint without needing a second, unrelated constant.
    /// </summary>
    private const float CoverSearchMonumentMargin = 20f;

    /// <summary>
    /// Like TryGetNearestMonument in LivingRust.MonumentAvoidZones.cs, but scales
    /// the proximity radius to each monument's actual bounds instead of a single
    /// flat radius, so very large monuments are still detected correctly.
    /// </summary>
    private bool TryGetNearestMonumentForCover(Vector3 position, out MonumentInfo monument)
    {
        monument = null;

        if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null)
        {
            return false;
        }

        float bestDistanceSqr = float.MaxValue;

        foreach (MonumentInfo candidate in TerrainMeta.Path.Monuments)
        {
            if (candidate == null)
            {
                continue;
            }

            float candidateRadius = Mathf.Max(candidate.Bounds.extents.x, candidate.Bounds.extents.z, MonumentLootScanFallbackRadius / 2f) + CoverSearchMonumentMargin;
            float distanceSqr = (candidate.transform.position - position).sqrMagnitude;

            if (distanceSqr > candidateRadius * candidateRadius)
            {
                continue;
            }

            if (distanceSqr < bestDistanceSqr)
            {
                bestDistanceSqr = distanceSqr;
                monument = candidate;
            }
        }

        return monument != null;
    }

    /// <summary>
    /// Runtime lookup used by TryFindCoverPoint (LivingRust.Combat.cs) before it
    /// falls back to a live fan search. Re-projects this monument type's cached
    /// points onto the real spawned instance and tests them nearest-first against
    /// the current attacker.
    /// </summary>
    private bool TryFindCoverPointNearMonument(BasePlayer npc, BaseCombatEntity attacker, MonumentInfo monument, out Vector3 coverPoint, bool desperate = false)
    {
        coverPoint = default;

        if (!_monumentCoverPoints.TryGetValue(monument.name, out List<MonumentCoverPoint> cachedPoints) || cachedPoints.Count == 0)
        {
            return false;
        }

        Vector3 selfPos = npc.transform.position;
        Vector3 attackerEyePos = GetAimPoint(attacker);
        float eyeHeight = npc.eyes.position.y - selfPos.y;

        // The direction away from the attacker, used below by the CoverMaxDirectionAngle filter.
        Vector3 awayFromAttacker = selfPos - attackerEyePos;
        awayFromAttacker.y = 0f;

        if (awayFromAttacker.sqrMagnitude < 0.01f)
        {
            awayFromAttacker = npc.transform.forward;
            awayFromAttacker.y = 0f;
        }

        awayFromAttacker.Normalize();

        IEnumerable<Vector3> candidatesNearestFirst = cachedPoints
            .Select(point => monument.transform.TransformPoint(point.LocalOffset))
            .Where(worldPoint => Mathf.Abs(worldPoint.y - selfPos.y) <= CoverMaxVerticalDelta)
            .Where(worldPoint => Vector3.Distance(selfPos, worldPoint) <= CoverMaxUsefulDistance)
            .Where(worldPoint =>
            {
                // Desperate mode skips this filter entirely: any direction is fair game.
                if (desperate)
                {
                    return true;
                }

                Vector3 flatDirection = worldPoint - selfPos;
                flatDirection.y = 0f;
                return flatDirection.sqrMagnitude < 0.01f || Vector3.Angle(awayFromAttacker, flatDirection) <= CoverMaxDirectionAngle;
            })
            .OrderBy(worldPoint => Vector3.Distance(selfPos, worldPoint));

        foreach (Vector3 worldPoint in candidatesNearestFirst)
        {
            if (IsPartialCoverPoint(worldPoint, attackerEyePos, eyeHeight))
            {
                coverPoint = worldPoint;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How far a cached cover point may sit off the direct line from origin to
    /// destination and still count as roughly on the way. Deliberately more
    /// generous than CoverMaxDirectionAngle, since this only checks whether a
    /// point is a reasonable waypoint, not whether it breaks a live sightline.
    /// </summary>
    private const float CoverAlongPathMaxOffsetDistance = 8f;

    /// <summary>
    /// Opportunistic waypoint lookup for a blind push toward a lost attacker's last
    /// known position (PushTowardLastKnownPosition in LivingRust.Combat.cs).
    /// Unlike TryFindCoverPointNearMonument, there is no live attacker to test
    /// against, so this just finds a cached cover point roughly on the way.
    /// </summary>
    private bool TryFindCoverPointAlongPath(Vector3 origin, Vector3 destination, MonumentInfo monument, out Vector3 coverPoint)
    {
        coverPoint = default;

        if (!_monumentCoverPoints.TryGetValue(monument.name, out List<MonumentCoverPoint> cachedPoints) || cachedPoints.Count == 0)
        {
            return false;
        }

        Vector3 path = destination - origin;
        path.y = 0f;
        float pathLength = path.magnitude;

        if (pathLength < 1f)
        {
            return false;
        }

        Vector3 pathDirection = path / pathLength;

        Vector3 bestPoint = default;
        float bestDistanceFromOrigin = float.MaxValue;
        bool found = false;

        foreach (MonumentCoverPoint cached in cachedPoints)
        {
            Vector3 worldPoint = monument.transform.TransformPoint(cached.LocalOffset);

            if (Mathf.Abs(worldPoint.y - origin.y) > CoverMaxVerticalDelta)
            {
                continue;
            }

            Vector3 fromOrigin = worldPoint - origin;
            fromOrigin.y = 0f;

            float alongPath = Vector3.Dot(fromOrigin, pathDirection);

            if (alongPath <= 0.5f || alongPath >= pathLength)
            {
                // Behind the survivor or past the destination, so not a useful waypoint.
                continue;
            }

            float perpendicularOffset = (fromOrigin - pathDirection * alongPath).magnitude;

            if (perpendicularOffset > CoverAlongPathMaxOffsetDistance)
            {
                continue;
            }

            if (alongPath < bestDistanceFromOrigin)
            {
                bestDistanceFromOrigin = alongPath;
                bestPoint = worldPoint;
                found = true;
            }
        }

        coverPoint = bestPoint;
        return found;
    }
}
