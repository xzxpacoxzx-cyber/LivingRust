using LivingRust.Core;
using System;
using System.Collections.Generic;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    /// <summary>
    /// A single confirmed-bad pocket inside one monument type, stored in that monument's
    /// own local space. Requires repeat evidence before it affects anything.
    /// </summary>
    private sealed class MonumentAvoidZone
    {
        public Vector3 LocalOffset;
        public float Radius;
        public int ConfirmCount;
    }

    /// <summary>
    /// How large a single avoid zone is once confirmed - covers one bad pocket of
    /// geometry, not the whole monument footprint.
    /// </summary>
    private const float AvoidZoneRadius = 5f;

    /// <summary>
    /// How many separate stuck reports at the same monument-local spot are required
    /// before a candidate zone starts affecting anything.
    /// </summary>
    private const int AvoidZoneConfirmThreshold = 2;

    /// <summary>
    /// How close two stuck reports need to be, in monument-local space, to count as "the
    /// same pocket" and merge into one zone's confirm count.
    /// </summary>
    private const float AvoidZoneMergeDistance = 4f;

    /// <summary>
    /// Maximum distance from a monument for a stuck position to be attributed to it at
    /// all, so open-terrain stucks aren't wrongly blamed on a distant monument.
    /// </summary>
    private const float AvoidZoneMonumentSearchRadius = 60f;

    /// <summary>
    /// In-memory only for now - resets on every plugin reload/server restart.
    /// </summary>
    private readonly Dictionary<string, List<MonumentAvoidZone>> _monumentAvoidZones = new();

    /// <summary>
    /// Records one stuck/failed-navigation report as potential evidence of a permanent
    /// bad pocket in the nearest monument. A confirmed entry is remembered
    /// monument-type-wide, for any survivor.
    /// </summary>
    private void RecordPotentialAvoidZone(Vector3 worldPosition)
    {
        if (!TryGetNearestMonument(worldPosition, AvoidZoneMonumentSearchRadius, out MonumentInfo monument))
        {
            return;
        }

        Vector3 localOffset = monument.transform.InverseTransformPoint(worldPosition);

        if (!_monumentAvoidZones.TryGetValue(monument.name, out List<MonumentAvoidZone> zones))
        {
            zones = new List<MonumentAvoidZone>();
            _monumentAvoidZones[monument.name] = zones;
        }

        foreach (MonumentAvoidZone zone in zones)
        {
            if (Vector3.Distance(zone.LocalOffset, localOffset) <= AvoidZoneMergeDistance)
            {
                zone.ConfirmCount++;

                if (zone.ConfirmCount == AvoidZoneConfirmThreshold)
                {
                    Puts($"monument-avoid-zone: confirmed a {AvoidZoneRadius:F0}m avoid zone in '{monument.name}' at local offset {zone.LocalOffset} after {AvoidZoneConfirmThreshold} separate stuck reports there - future pathing near this monument type will steer clear of just this pocket.");
                }

                return;
            }
        }

        zones.Add(new MonumentAvoidZone { LocalOffset = localOffset, Radius = AvoidZoneRadius, ConfirmCount = 1 });
    }

    /// <summary>
    /// Whether worldPosition falls inside a confirmed avoid zone for whichever monument is
    /// nearest. Scoped to a small radius per zone, not the whole monument.
    /// </summary>
    // Whole-monument-type exclusions: blanket-excluded from the start, with no
    // repeat-evidence confirmation needed. Matched by substring against the prefab name.
    // Includes safezones plus monuments where bots get stuck on interior/vertical geometry.
    // launch_site is deliberately not included here (excluded only from deliberate
    // destination-rolling, not opportunistic wandering-by looting and stuck-recovery).
    private static readonly string[] FullyAvoidedMonumentNameSubstrings =
    {
        "water_well", "lighthouse", "underwater_lab", "ranch",
        "barn", "compound", "bandit", "fishing_village", "stables",
        "apartments_complex",
    };

    // Dedicated radius for the FullyAvoidedMonumentNameSubstrings check. Deliberately
    // separate from the confirmed-avoid-zone system's AvoidZoneMonumentSearchRadius (60m),
    // which would otherwise clip this larger radius down.
    private const float FullyAvoidedMonumentRadius = 100f;

    private bool IsNearFullyAvoidedMonument(Vector3 worldPosition)
    {
        if (MonumentAccess.GetAllMonuments().Count == 0)
        {
            return false;
        }

        foreach (MonumentInfo monument in MonumentAccess.GetAllMonuments())
        {
            if ((monument.transform.position - worldPosition).sqrMagnitude > FullyAvoidedMonumentRadius * FullyAvoidedMonumentRadius)
            {
                continue;
            }

            // Checks displayPhrase too, since some names never had a real internal-name
            // match to find in the first place.
            string displayName = monument.displayPhrase.IsValid() ? monument.displayPhrase.english : null;

            foreach (string blockedSubstring in FullyAvoidedMonumentNameSubstrings)
            {
                if (monument.name.IndexOf(blockedSubstring, StringComparison.OrdinalIgnoreCase) >= 0
                    || (displayName != null && displayName.IndexOf(blockedSubstring, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Train tunnel network prefabs ("tunnel.single.*"/"tunnel.double.*" segments). Not a
    // MonumentInfo at all - a separate underground structural network with nothing in
    // MonumentAccess.GetAllMonuments() to match. Excludes "military_tunnel" (a distinct
    // monument handled separately) and "tent_tunnel" (unrelated decoration).
    private static readonly string[] TrainTunnelPrefabSubstrings = { "tunnel.single.", "tunnel.double." };

    // Generous radius covering a whole tunnel entrance/junction area, not just the exact
    // prefab footprint, since confined interior geometry is what traps a bot.
    private const float TrainTunnelAvoidRadius = 40f;

    private bool IsNearTrainTunnelEntrance(Vector3 position)
    {
        Collider[] hits = Physics.OverlapSphere(position, TrainTunnelAvoidRadius, ~0, QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            string name = hit.gameObject.name;

            foreach (string substring in TrainTunnelPrefabSubstrings)
            {
                if (name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Global, cross-survivor "known dead end" blacklist, populated by the stall watchdog
    /// in LivingRust.Commands.cs. A simple List rather than a Dictionary, since a stall
    /// position has no natural unique key and this list stays small.
    /// </summary>
    private readonly List<(Vector3 Position, float Radius, float ExpiresAt)> _globalStallZones = new();

    private void PoisonGlobalStallZone(Vector3 position)
    {
        _globalStallZones.Add((position, GlobalStallZoneRadius, Time.realtimeSinceStartup + GlobalStallZoneDurationSeconds));

        VerbosePuts($"stall-watchdog: blacklisting the area around {position} ({GlobalStallZoneRadius:F0}m) for the next {GlobalStallZoneDurationSeconds:F0}s - every survivor will avoid it.");
    }

    private bool IsInGlobalStallZone(Vector3 position)
    {
        float now = Time.realtimeSinceStartup;

        for (int i = _globalStallZones.Count - 1; i >= 0; i--)
        {
            if (_globalStallZones[i].ExpiresAt <= now)
            {
                // Opportunistic cleanup, since this function already walks the whole list.
                _globalStallZones.RemoveAt(i);
                continue;
            }

            if (Vector3.Distance(position, _globalStallZones[i].Position) <= _globalStallZones[i].Radius)
            {
                return true;
            }
        }

        return false;
    }

    // Early-game hard-avoid list: applies conditionally per survivor until
    // HasCompletedEarlyGameMilestones is true, unlike the permanent blanket exclusions
    // above. These are high-threat military monuments a freshly-spawned, under-geared
    // survivor shouldn't wander into. "military_tunnel" is distinct from
    // TrainTunnelPrefabSubstrings' dotted prefixes - a separate, real monument.
    // "arctic_research_base" and "military_base" are governed by the looser
    // ShouldAvoidInventoryOrPrepGatedMonument condition below instead.
    private static readonly string[] EarlyGameRestrictedMonumentNameSubstrings =
    {
        "nuclear_missile_silo", "launch_site", "military_tunnel",
    };

    // Inventory/preparedness gate for Arctic Research Base and Abandoned Military Base:
    // avoided while inventory is full, no base is built yet, or the survivor is still in
    // the primitive checklist stage. Re-evaluated fresh every cycle rather than a one-way
    // milestone flag, since these conditions are transient.
    private static readonly string[] InventoryOrPrepGatedMonumentNameSubstrings =
    {
        "arctic_research_base", "military_base",
    };

    private bool IsNearInventoryOrPrepGatedMonument(Vector3 worldPosition)
    {
        if (MonumentAccess.GetAllMonuments().Count == 0)
        {
            return false;
        }

        foreach (MonumentInfo monument in MonumentAccess.GetAllMonuments())
        {
            if ((monument.transform.position - worldPosition).sqrMagnitude > FullyAvoidedMonumentRadius * FullyAvoidedMonumentRadius)
            {
                continue;
            }

            string displayName = monument.displayPhrase.IsValid() ? monument.displayPhrase.english : null;

            foreach (string blockedSubstring in InventoryOrPrepGatedMonumentNameSubstrings)
            {
                if (monument.name.IndexOf(blockedSubstring, StringComparison.OrdinalIgnoreCase) >= 0
                    || (displayName != null && displayName.IndexOf(blockedSubstring, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True while any of: full inventory (no backpack room either), no base built yet, or
    /// still working through the primitive checklist.
    /// </summary>
    private bool ShouldAvoidInventoryOrPrepGatedMonument(Survivor survivor)
    {
        BasePlayer npc = survivor?.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return false;
        }

        return (IsInventoryFull(npc.inventory) && !HasBackpackRoom(npc))
            || survivor.Character.Home == null
            || _pursuingPrimitiveGoals.Contains(survivor.Character.Id);
    }

    // Reuses FullyAvoidedMonumentRadius's 100m rather than inventing a second constant,
    // since these are large monuments where a tighter radius would still be dangerous.
    private bool IsNearEarlyGameRestrictedMonument(Vector3 worldPosition)
    {
        if (MonumentAccess.GetAllMonuments().Count == 0)
        {
            return false;
        }

        foreach (MonumentInfo monument in MonumentAccess.GetAllMonuments())
        {
            if ((monument.transform.position - worldPosition).sqrMagnitude > FullyAvoidedMonumentRadius * FullyAvoidedMonumentRadius)
            {
                continue;
            }

            string displayName = monument.displayPhrase.IsValid() ? monument.displayPhrase.english : null;

            foreach (string blockedSubstring in EarlyGameRestrictedMonumentNameSubstrings)
            {
                if (monument.name.IndexOf(blockedSubstring, StringComparison.OrdinalIgnoreCase) >= 0
                    || (displayName != null && displayName.IndexOf(blockedSubstring, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the survivor has graduated past the restricted early game: a real base
    /// down AND has completed one real deposit trip. Both required, not either.
    /// </summary>
    private bool HasCompletedEarlyGameMilestones(Survivor survivor)
    {
        return survivor?.Character != null && survivor.Character.Home != null && survivor.Character.HasDepositedInitialLoot;
    }

    /// <summary>
    /// Survivor-aware overload - layers the conditional early-game restriction on top of
    /// the unconditional checks the position-only overload already does. survivor is
    /// optional; null preserves the old behaviour.
    /// </summary>
    private bool IsInMonumentAvoidZone(Vector3 worldPosition, Survivor survivor)
    {
        if (survivor != null && !HasCompletedEarlyGameMilestones(survivor) && IsNearEarlyGameRestrictedMonument(worldPosition))
        {
            return true;
        }

        if (survivor != null && ShouldAvoidInventoryOrPrepGatedMonument(survivor) && IsNearInventoryOrPrepGatedMonument(worldPosition))
        {
            return true;
        }

        return IsInMonumentAvoidZone(worldPosition);
    }

    private bool IsInMonumentAvoidZone(Vector3 worldPosition)
    {
        // Ghost-route-only monuments (Launch Site) are closed to ambient looting: nothing inside is a
        // valid generic loot/zone/gather candidate - the authored routes are the only way in.
        if (IsNearFullyAvoidedMonument(worldPosition) || IsNearTrainTunnelEntrance(worldPosition) || IsInGlobalStallZone(worldPosition)
            || IsInGhostRouteOnlyMonumentArea(worldPosition))
        {
            return true;
        }

        if (!TryGetNearestMonument(worldPosition, AvoidZoneMonumentSearchRadius, out MonumentInfo monument))
        {
            return false;
        }

        if (!_monumentAvoidZones.TryGetValue(monument.name, out List<MonumentAvoidZone> zones))
        {
            return false;
        }

        Vector3 localOffset = monument.transform.InverseTransformPoint(worldPosition);

        foreach (MonumentAvoidZone zone in zones)
        {
            if (zone.ConfirmCount >= AvoidZoneConfirmThreshold && Vector3.Distance(zone.LocalOffset, localOffset) <= zone.Radius)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetNearestMonument(Vector3 worldPosition, float maxDistance, out MonumentInfo monument)
    {
        monument = null;
        float bestDistanceSqr = maxDistance * maxDistance;

        foreach (MonumentInfo candidate in MonumentAccess.GetAllMonuments())
        {
            float distanceSqr = (candidate.transform.position - worldPosition).sqrMagnitude;

            if (distanceSqr < bestDistanceSqr)
            {
                bestDistanceSqr = distanceSqr;
                monument = candidate;
            }
        }

        return monument != null;
    }

    /// <summary>
    /// Same nearest-search as TryGetNearestMonument, but optionally constrained to
    /// monuments whose .name exactly matches requiredMonumentName. null falls back to the
    /// "nearest of any type" behaviour.
    /// </summary>
    private bool TryGetNearestMonumentOfType(Vector3 worldPosition, float maxDistance, string requiredMonumentName, out MonumentInfo monument)
    {
        if (string.IsNullOrEmpty(requiredMonumentName))
        {
            return TryGetNearestMonument(worldPosition, maxDistance, out monument);
        }

        monument = null;
        float bestDistanceSqr = maxDistance * maxDistance;

        foreach (MonumentInfo candidate in MonumentAccess.GetAllMonuments())
        {
            if (candidate.name != requiredMonumentName)
            {
                continue;
            }

            float distanceSqr = (candidate.transform.position - worldPosition).sqrMagnitude;

            if (distanceSqr < bestDistanceSqr)
            {
                bestDistanceSqr = distanceSqr;
                monument = candidate;
            }
        }

        return monument != null;
    }
}
