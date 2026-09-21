using System;
using System.Collections.Generic;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    /// <summary>
    /// A single confirmed-bad pocket inside one monument type, stored in
    /// that monument's own local space (relative to its transform, not raw
    /// world coordinates) so the same zone applies correctly no matter
    /// where that monument prefab happens to spawn on a given map/seed.
    /// Deliberately small (AvoidZoneRadius) and requires repeat evidence
    /// (ConfirmCount reaching AvoidZoneConfirmThreshold) before it actually
    /// affects anything - see IsInMonumentAvoidZone's own comment for why
    /// that matters.
    /// </summary>
    private sealed class MonumentAvoidZone
    {
        public Vector3 LocalOffset;
        public float Radius;
        public int ConfirmCount;
    }

    /// <summary>
    /// How large a single avoid zone is once confirmed - deliberately just
    /// big enough to cover one bad pocket of geometry (a stuck-prone
    /// stretch near an awning/monument structure, the case this was built
    /// for), not the whole monument footprint. A real live monument
    /// (desert_military_base_d) is 50-100m+ across, so this stays a small
    /// fraction of it - the whole point is that a bot should still freely
    /// loot/explore every other part of the monument, only steering clear
    /// of this one specific pocket.
    /// </summary>
    private const float AvoidZoneRadius = 5f;

    /// <summary>
    /// How many separate stuck reports at essentially the same
    /// monument-local spot are required before a candidate zone actually
    /// starts affecting anything (see IsInMonumentAvoidZone). A single bad
    /// walk could be a fluke (bad luck with a passing entity, a momentary
    /// physics hiccup) - requiring repeat evidence at the same pocket
    /// specifically (not just "this monument had 2 failures anywhere")
    /// keeps this self-learning system from overreacting to one-off
    /// events, while still catching genuinely bad geometry within a
    /// realistic number of encounters.
    /// </summary>
    private const int AvoidZoneConfirmThreshold = 2;

    /// <summary>
    /// How close two stuck reports need to be, in monument-local space, to
    /// count as "the same pocket" and merge into one zone's confirm count,
    /// rather than each starting its own separate unconfirmed candidate.
    /// </summary>
    private const float AvoidZoneMergeDistance = 4f;

    /// <summary>
    /// Maximum distance from a monument's own transform for a stuck
    /// position to be attributed to it at all - stops a bot getting stuck
    /// in genuinely open terrain from being (wrongly) blamed on whichever
    /// monument happens to be nearest, however far away that actually is.
    /// </summary>
    private const float AvoidZoneMonumentSearchRadius = 60f;

    /// <summary>
    /// In-memory only for now (not written to a data file) - resets on
    /// every plugin reload/server restart, re-learning from scratch rather
    /// than persisting across sessions. Fine as a first cut since
    /// confirmation only takes two encounters, but worth revisiting if
    /// zones turn out to matter enough to want them remembered long-term.
    /// </summary>
    private readonly Dictionary<string, List<MonumentAvoidZone>> _monumentAvoidZones = new();

    /// <summary>
    /// Records one stuck/failed-navigation report as potential evidence of
    /// a permanent bad pocket in whatever monument is nearest - called from
    /// PoisonAreaNow, which only ever fires after a walk has already
    /// exhausted the full wiggle/nudge/teleport recovery escalation (see
    /// its own comment), already a strong per-incident signal. This layers
    /// a second, monument-relative memory on top: where the existing
    /// poisoned-zone system forgets after PoisonedZoneDuration and only
    /// applies to the current survivor's current loot task, a confirmed
    /// entry here is remembered monument-type-wide, for any survivor,
    /// applying beyond just looting - the user's own explicit motivation
    /// (real scientist2 spawns are placed clear of the exact same pocket
    /// this was built from, live evidence that even Facepunch's own
    /// tooling treats it as bad ground).
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
    /// Whether worldPosition falls inside a CONFIRMED avoid zone (ConfirmCount
    /// reached AvoidZoneConfirmThreshold) for whichever monument is nearest.
    /// Deliberately requires confirmation, not just any recorded candidate -
    /// an unconfirmed single report shouldn't stop a bot from ever
    /// approaching that spot again, only repeat evidence should. And
    /// deliberately scoped to one small radius per zone rather than the
    /// monument as a whole, so a confirmed bad pocket never reads as "avoid
    /// this entire monument" - the rest of a desert_military_base_d (or
    /// any other monument type) stays fully available for looting/
    /// exploring even once one of its pockets is known-bad.
    /// </summary>
    // Real whole-monument-type exclusions (2026-08-28, Lucas's own
    // explicit request: "have bots avoid the waterwell monument entirely.
    // just have a poisoned area around it, no real requirement for a bot
    // to venture inside other than for food crates") - unlike every other
    // entry in _monumentAvoidZones (a single learned pocket within an
    // otherwise-fine monument), these are blanket-excluded from the very
    // start, no repeat-evidence confirmation needed. Matched by substring
    // against the real prefab name (confirmed via AssetSceneManifest.json:
    // water_well_a through water_well_e, five separate tiny-monument
    // variants scattered across the map) rather than an exact name, same
    // reasoning RequiresDestructionToLoot's own doc comment gives for its
    // own substring matching. A water well's real loot value is low enough
    // (Lucas's own framing) that it isn't worth the navmesh/pathing risk
    // every other tiny monument already carries.
    // lighthouse/underwater_lab added 2026-09-01 (Lucas's own explicit,
    // urgent report: "so many bots seem to get stuck/caught in lighthouse
    // and there is easily 20-30 of them inside of each other idling/trying
    // to grab a crate"). ranch added same session, same report pattern
    // ("bots are still getting stuck at Ranch and are piling up A LOT").
    // All three were ALREADY excluded from ever being deliberately ROLLED
    // as a destination (AutonomyExcludedMonumentSubstrings, LivingRust.
    // GearScore.cs), but that only ever gated "should a survivor choose to
    // travel HERE" - it was never checked by the generic nearby-container/
    // corpse/bag/resource-node scans a bot runs constantly while just
    // wandering. A bot that happened to pass within loot-search range of
    // one of these would still detect and path to its containers via that
    // completely separate generic search, bypassing the monument-level
    // exclusion entirely - IsInMonumentAvoidZone is checked at nearly
    // every one of those generic candidate-selection sites project-wide
    // (containers, corpses, dropped bags, resource nodes, recyclers,
    // crafting-ingredient gathering, combat LOS-seeking), so adding an
    // entry here is the single real choke point that actually stops it,
    // rather than needing the same fix repeated at a dozen separate call
    // sites. barn/compound (Outpost)/bandit (Bandit Camp)/fishing_village/
    // stables added proactively same session (2026-09-01, Lucas's own
    // explicit follow-up: "basically every monument with a safezone the
    // bot should hard avoid, there is no requirement for them to be
    // there") - every one of these was ALREADY in
    // AutonomyExcludedMonumentSubstrings for the identical "it's a
    // safezone, genuinely unlootable" reason, just never checked by the
    // generic scans the way ranch/lighthouse/underwater_lab now are here.
    // apartments_complex added 2026-09-01 (live report: bots piling up
    // there with NO corpses beforehand this time - ruling out the
    // corpse-detour bug specifically, pointing instead at the same
    // generic-scan gap this whole list exists to close: apartments_complex
    // was already in AutonomyExcludedMonumentSubstrings, "no reason given"
    // per that list's own doc comment, but never in THIS one - a real,
    // legitimately lootable monument (unlike the safezones above), so bots
    // wandering nearby would detect its genuine containers via the
    // ordinary passive scan and walk in, then get stuck on its real
    // multi-floor interior geometry, the same category of problem this
    // project has already had to solve with dedicated ghost-routes for
    // other vertically-complex monuments).
    // launch_site deliberately NOT here (2026-09-01, Lucas's own explicit
    // scope call: "only avoid it during the initial roll the dice 'go
    // inland'... if they venture there and die, it's on them") - it IS
    // excluded from deliberate destination-rolling
    // (AutonomyExcludedMonumentSubstrings, LivingRust.GearScore.cs), just
    // not from this list, which also blocks opportunistic wandering-by
    // looting and stuck-recovery - a bot that happens to end up near
    // Launch Site during ordinary activity is allowed to loot/gather there
    // same as any other real monument, Bradley risk and all.
    private static readonly string[] FullyAvoidedMonumentNameSubstrings =
    {
        "water_well", "lighthouse", "underwater_lab", "ranch",
        "barn", "compound", "bandit", "fishing_village", "stables",
        "apartments_complex",
    };

    // Real, dedicated radius for the FullyAvoidedMonumentNameSubstrings
    // check specifically (2026-09-01, Lucas's own explicit ask for ranch:
    // "avoid completely any area 100 metres from the centre... in a large
    // circle"). Deliberately a SEPARATE lookup from the confirmed-avoid-
    // zone system below (AvoidZoneMonumentSearchRadius, 60m) - that one
    // caps how far a stuck report can be attributed to its nearest
    // monument at all, which would have silently clipped a 100m ask down
    // to 60m if reused here (TryGetNearestMonument returns nothing once
    // the nearest monument itself is further away than the given max
    // distance, regardless of what radius the caller actually wanted to
    // check against).
    private const float FullyAvoidedMonumentRadius = 100f;

    private bool IsNearFullyAvoidedMonument(Vector3 worldPosition)
    {
        if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null)
        {
            return false;
        }

        foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
        {
            if ((monument.transform.position - worldPosition).sqrMagnitude > FullyAvoidedMonumentRadius * FullyAvoidedMonumentRadius)
            {
                continue;
            }

            // Checks displayPhrase too, same reasoning as
            // IsMonumentExcludedFromAutonomy's own MonumentInfo overload
            // (LivingRust.GearScore.cs) - "ranch" never had a real
            // internal-name match to find in the first place.
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

    // Real train tunnel network prefabs (2026-09-01, live report + screenshot:
    // "a lot of bots are getting very caught up there attempting to loot
    // containers... hard avoid for these tunnel systems/entrances" -
    // confirmed via the game's own asset manifest that these are real
    // structural prefabs under Assets/Content/Structures/Tunnels/ -
    // "tunnel.single.*"/"tunnel.double.*" (entrance, junction, straight,
    // corner, gate, splitter segments). NOT a MonumentInfo at all (unlike
    // every other avoid entry above) - this is Rust's separate underground
    // train-tunnel network, scattered structural pieces rather than a
    // single registered monument, so TerrainMeta.Path.Monuments has
    // nothing to match against here. Deliberately excludes "military_tunnel"
    // (a real, different, dot-less-named monument already handled
    // separately) and "tent_tunnel" (military tent decoration, unrelated) -
    // the "tunnel.single."/"tunnel.double." dotted prefix is specific to
    // this one real prefab family.
    private static readonly string[] TrainTunnelPrefabSubstrings = { "tunnel.single.", "tunnel.double." };

    // Generous radius (2026-09-01, Lucas's own explicit "hard avoid... they
    // get stuck EASILY") - a whole tunnel entrance/junction area, not just
    // the exact prefab footprint, since the confined interior geometry is
    // what actually traps a bot once it's committed to heading inside.
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
    /// Global, cross-survivor "known dead end" blacklist - see
    /// RescueGenuinelyStalledSurvivor's own doc comment (LivingRust.Commands.cs)
    /// for the watchdog that populates this. Deliberately a simple List,
    /// not a Dictionary keyed by position - unlike the resource-node
    /// poison (which has a natural unique key, the node's own NetworkableId),
    /// a stall position is an arbitrary point in space with no such key,
    /// and this list is expected to stay small (a genuine 30s+ full-life
    /// stall should be rare after the existing per-episode ladder already
    /// resolves most cases).
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
                // Opportunistic cleanup - checked here rather than a
                // separate periodic sweep since this function already
                // walks the whole list on every real call.
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

    private bool IsInMonumentAvoidZone(Vector3 worldPosition)
    {
        if (IsNearFullyAvoidedMonument(worldPosition) || IsNearTrainTunnelEntrance(worldPosition) || IsInGlobalStallZone(worldPosition))
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

        foreach (MonumentInfo candidate in TerrainMeta.Path.Monuments)
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
    /// Same nearest-search as TryGetNearestMonument, but optionally
    /// constrained to monuments whose .name EXACTLY matches
    /// requiredMonumentName (2026-08-18, real bug fix - see
    /// TryLoadTraceWaypoints' own doc comment for the live incident this
    /// fixes). null falls back to the old "nearest of any type" behaviour.
    /// </summary>
    private bool TryGetNearestMonumentOfType(Vector3 worldPosition, float maxDistance, string requiredMonumentName, out MonumentInfo monument)
    {
        if (string.IsNullOrEmpty(requiredMonumentName))
        {
            return TryGetNearestMonument(worldPosition, maxDistance, out monument);
        }

        monument = null;
        float bestDistanceSqr = maxDistance * maxDistance;

        foreach (MonumentInfo candidate in TerrainMeta.Path.Monuments)
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
