using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Monument-specific cover-point discovery (2026-08-24) - the second half
/// of TryFindCoverPoint (LivingRust.Combat.cs). Confirmed live (2026-08-23)
/// that a fresh per-fight raycast fan works well for open/organic terrain,
/// but Lucas's own read matched a real, well-precedented gap: monuments are
/// dense, multi-story, compound-mesh structures, and a live search has to
/// fight through all of that complexity fresh every single fight, with real
/// odds of missing a narrow gap between sampled angles or tripping over the
/// same kind of oversized/wrong-layer collider this project has hit before
/// (NonSteppableColliderNames' whole history).
///
/// The fix mirrors an ALREADY-PROVEN pattern in this exact codebase, not a
/// new technique: LivingRust.MonumentLootZones.cs already pre-scans every
/// monument TYPE once, caches results in that monument's own local space,
/// and re-projects them onto whichever real instance spawns on a given
/// map/seed. Cover points get identical treatment here - a one-time-per-
/// monument-type walkthrough (no live time pressure, unlike a per-fight
/// scan) that samples points near substantial real structure, then a cheap
/// per-fight lookup that just tests the cached candidates against whoever
/// the actual live attacker is, instead of blindly fanning out fresh every
/// time.
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
    /// Same "scan every distinct monument type once, skip anything already
    /// known from a previous session" shape as ScanAllMonumentsOnBoot -
    /// deliberately a SEPARATE pass rather than folding into that one, so a
    /// monument already known for loot zones but not yet for cover points
    /// (e.g. right after this feature first ships) still gets scanned.
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
    /// Only real STRUCTURE counts as a candidate wall to hug - deliberately
    /// excludes "Terrain" from the search mask (unlike
    /// CombatLineOfSightBlockingMask, which needs it for the live per-fight
    /// check). Terrain is one enormous continuous mesh with a degenerate
    /// "bounds" that would swallow the whole map if treated as a single
    /// huggable object - real hill/ground-slope cover is already handled
    /// fine by the live organic-terrain search in TryFindCoverPoint, this
    /// scan only needs to find discrete building/prop colliders.
    /// </summary>
    private static readonly int MonumentCoverStructureMask = LayerMask.GetMask("World", "Construction", "Tree", "Default");

    /// <summary>
    /// A collider narrower than this on both horizontal axes is excluded
    /// entirely - 2026-08-24, lowered from an initial 2f after Lucas's own
    /// live report/screenshot: real concrete bollards/road barriers sitting
    /// in plain, close LOS-blocking view of him never got flagged as
    /// candidates at all, because a single bollard is well under 2m in
    /// both horizontal dimensions - the exact kind of low, partial cover
    /// (his own framing: "shield 60-70% of the player model... only the
    /// torso and upper body/neck showing") this whole feature exists to
    /// recognize was being filtered out before the real cover test
    /// (IsPartialCoverPoint) ever got a chance to evaluate it. Safe to set
    /// low - this is only a cheap pre-filter to avoid wasting a candidate
    /// slot on truly trivial clutter (a soda can, a small rock); anything
    /// genuinely too small to block a real body-height sightline still
    /// gets rejected for real by the actual raycast test afterward.
    /// </summary>
    private const float CoverPointMinStructureSize = 0.5f;

    /// <summary>
    /// How far out from a qualifying structure's own face the sampled
    /// candidate point sits - same real purpose as CoverStandoffDistance in
    /// TryFindCoverPoint (don't stand flush against the wall), just applied
    /// at scan time instead of pulled back afterward, since here the
    /// "direction away from the wall" is already known directly from which
    /// face was sampled.
    /// </summary>
    private const float CoverPointStandoffFromStructure = 1.5f;

    /// <summary>
    /// Cached points closer together than this (in real world space, at
    /// scan time) collapse to one - a single large structure can otherwise
    /// generate several near-duplicate candidates from adjacent sampled
    /// faces.
    /// </summary>
    private const float CoverPointDedupDistance = 4f;

    /// <summary>
    /// Hard cap on how many cover points get cached per monument type -
    /// 2026-08-24, raised from an initial 40 (Lucas's own live report: "40
    /// seems a bit low... almost anything can be used for cover if done
    /// correctly") after confirming every single monument scanned hit the
    /// old cap exactly, including small ones with barely 179 raw structure
    /// colliders to begin with - the cap, not real cover availability, was
    /// the limiting factor everywhere, not just at huge monuments like
    /// Launch Site. This is a one-time BOOT-time scan, not a live per-fight
    /// cost - the runtime lookup (TryFindCoverPointNearMonument) is still
    /// just sorting/iterating a list, cheap even at a few hundred entries,
    /// so there's real room to be generous here.
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
                // Real prop/decoration, not substantial structure - see
                // CoverPointMinStructureSize's own doc comment.
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

        Puts($"scanmonumentcover-diag: '{monument.name}' found {coverPoints.Count} candidate cover point(s) from {hits.Length} nearby structure collider(s) (scan radius {scanRadius:F0}m).");

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
    /// Same "is this position near this monument" question TryGetNearestMonument
    /// (LivingRust.MonumentAvoidZones.cs) already answers, but scaled to
    /// each monument's own REAL size instead of one flat radius for every
    /// monument type - 2026-08-24, real live bug: a flat 100m check
    /// (matching this project's more typical monument-proximity scale)
    /// silently failed almost everywhere inside Launch Site, since that
    /// monument's own real half-extent is close to 200m (confirmed by its
    /// own boot-scan radius, computed via the identical formula used here:
    /// Mathf.Max(Bounds.extents.x, Bounds.extents.z, fallback/2) + margin -
    /// Launch Site alone came out to 215m). Reusing the scan's own radius
    /// formula for the "is it nearby" check too means a monument this
    /// large is never silently invisible to a bot standing well inside its
    /// own real footprint, while a small monument doesn't falsely claim a
    /// huge, unrelated stretch of open world around it either.
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
    /// Runtime lookup, called from TryFindCoverPoint (LivingRust.Combat.cs)
    /// before it falls back to its own live fan search - re-projects this
    /// monument TYPE's cached local-offset points onto wherever this real
    /// INSTANCE actually spawned, then just runs the same cheap real
    /// Linecast check TryFindCoverPoint's own live search uses, nearest
    /// candidate first, against whoever the actual current attacker is.
    /// Far cheaper than a live fan search (a handful of pre-validated
    /// points instead of 30 freshly-probed ones) and immune to the live
    /// search's real weak spots at monuments specifically (compound-mesh
    /// false positives, narrow gaps between sampled angles) since the
    /// EXPENSIVE ground/overlap validation already happened once at scan
    /// time, not under real-time pressure mid-fight.
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

        // Same "which way is away from the attacker" direction the live
        // fan search computes, needed here too since CoverMaxDirectionAngle
        // filters against it below - see CoverSearchAngleOffsets' own doc
        // comment for why a near-total-reversal direction is excluded.
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
                // desperate skips this filter entirely - critically low
                // health means any direction is fair game, see
                // TryFindCoverPoint's own doc comment.
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
    /// How far a cached cover point may sit off the direct line from origin
    /// to destination and still count as "roughly on the way" (2026-08-24,
    /// Lucas's own explicit request: bots pushing blind toward a lost
    /// attacker's last known position should opportunistically swing
    /// through real cover along that route, without turning it into a
    /// literal wall-hugging pathfinder - "it doesn't need to be overly
    /// complex... otherwise that is also semi predictable and looks odd").
    /// Deliberately generous compared to CoverMaxDirectionAngle's tight
    /// attacker-relative arc - this isn't testing whether a point breaks a
    /// live sightline, just whether it's a reasonable waypoint on the way
    /// to somewhere else.
    /// </summary>
    private const float CoverAlongPathMaxOffsetDistance = 8f;

    /// <summary>
    /// Opportunistic waypoint lookup for a BLIND push toward a lost
    /// attacker's last known position (LivingRust.Combat.cs's own
    /// PushTowardLastKnownPosition) - deliberately separate from
    /// TryFindCoverPointNearMonument, which is attacker-relative (tests
    /// whether a point currently breaks LOS to a live attacker) and biased
    /// away from them (a retreat point). This is the opposite shape: no
    /// live attacker to test against at all (that's the whole reason this
    /// push is blind), just "is there a real, pre-validated cover point
    /// roughly between where I am and where I'm going" - reuses the exact
    /// same cache (ground/overlap validation already happened once at scan
    /// time) with a plain geometric on-the-way filter instead of a Linecast
    /// test. Only ever consulted near a scanned monument - open terrain has
    /// no cache to draw from, so a straight walk is exactly right there
    /// too.
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
                // Behind the survivor or past the actual destination - not
                // a waypoint on the way, either the wrong direction or a
                // needless detour beyond where it's already headed.
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
