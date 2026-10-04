using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Chinook locked-crate event and patrol-helicopter avoidance (2026-10-04, Lucas's spec).
///
/// A hackable locked crate dropped by a Chinook reuses the airdrop machinery (recruit a group, run to a rally
/// point, hold, converge - see LivingRust.Airdrops.cs) with one extra stage: once the crate is down the first
/// survivor to reach it starts the hack, everyone holds and fights for the whole hack timer, and only when the
/// crate unlocks does the normal king-of-the-hill looting begin. The patrol helicopter is simply avoided.
/// </summary>
public partial class LivingRust
{
    // Seconds the crate is expected to take to come down after it is released (it parachutes); the monitor
    // detects the actual landing, this only paces how soon the recruits set out.
    private const float ChinookCrateFallSeconds = 60f;

    // A Chinook crate is released right under the helicopter - how close a CH47 has to be at spawn to count.
    private const float ChinookCrateSourceRadius = 150f;

    // Upper bound on a whole hack event (hack timer plus the loot scramble) before participants give up.
    private const float AirdropHackEventLifetimeSeconds = 1500f;

    private void OnHackableCrateSpawned(HackableLockedCrate crate)
    {
        if (crate == null || crate.IsDestroyed || _engine == null)
        {
            return;
        }

        // Only a crate that a Chinook just released: a CH47 right next to it. Oil rig / cargo ship crates and
        // anything reloaded from the world save have no helicopter beside them and are ignored.
        bool fromChinook = false;

        foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
        {
            if (networkable is CH47HelicopterAIController chinook && !chinook.IsDestroyed
                && Vector3.Distance(chinook.transform.position, crate.transform.position) <= ChinookCrateSourceRadius)
            {
                fromChinook = true;
                break;
            }
        }

        if (!fromChinook)
        {
            return;
        }

        RegisterChinookCrate(crate, ChinookCrateFallSeconds);
    }

    private void RegisterChinookCrate(HackableLockedCrate crate, float landsInSeconds)
    {
        // Already tracked: just try to bring in more survivors (an earlier call may have found none fit).
        AirdropInfo existing = _airdrops.FirstOrDefault(a => !a.Done && a.Drop == crate);

        if (existing != null)
        {
            int more = RecruitForAirdrop(existing);
            Puts($"chinook-crate: crate at {crate.transform.position} already tracked - recruited {more} more survivor(s) ({existing.Participants.Count} in total).");
            return;
        }

        AirdropInfo info = new()
        {
            Position = crate.transform.position,
            ExpectedLandTime = Time.realtimeSinceStartup + landsInSeconds,
            Drop = crate,
            LastDropPosition = crate.transform.position,
        };

        _airdrops.RemoveAll(a => a.Done);
        _airdrops.Add(info);
        StartAirdropMonitor(info);

        int recruited = RecruitForAirdrop(info);
        Puts($"chinook-crate: locked crate at {crate.transform.position} - {recruited} survivor(s) heading out to hack and hold it.");
    }

    /// <summary>
    /// After a plugin reload: a crate a Chinook already dropped is still sitting in the world, so it is adopted
    /// as a fresh event (the reload wiped the old one's tracking).
    /// </summary>
    private void AdoptExistingDroppedCrates()
    {
        int total = 0;
        int adopted = 0;

        foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities.ToList())
        {
            if (networkable is not HackableLockedCrate crate || crate.IsDestroyed)
            {
                continue;
            }

            total++;

            // Dry land only: a dropped crate that sank into the sea, or sits at an oil rig, is not worth a trip.
            // Not parented to anything: a cargo ship crate rides the ship and is not a drop to wait at.
            if (crate.wasDropped && !crate.IsFullyHacked() && crate.GetParentEntity() == null
                && WaterLevel.GetWaterLevel(crate.transform.position, waves: false) <= crate.transform.position.y + 0.3f)
            {
                RegisterChinookCrate(crate, 0f);
                adopted++;
            }
        }

        Puts($"chinook-crate: startup scan found {total} hackable crate(s), adopted {adopted} dropped locked one(s).");
    }

    [ChatCommand("lr.debug.crates")]
    private void CmdDebugCrates(BasePlayer player, string command, string[] args)
    {
        RunDebugCrates(player, args.Length > 0 && args[0].Equals("adopt", StringComparison.OrdinalIgnoreCase));
    }

    [ConsoleCommand("lr.debug.crates")]
    private void CmdDebugCratesConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugCrates(player, arg.HasArgs() && arg.Args[0].Equals("adopt", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Lists every hackable crate (position, distance, dropped / locked / hacking) and, with "adopt", registers the
    /// nearest locked one as a Chinook-crate event regardless of how it spawned - for testing a forced drop.
    /// </summary>
    private void RunDebugCrates(BasePlayer player, bool adopt)
    {
        HackableLockedCrate nearest = null;
        float nearestDistance = float.MaxValue;
        int count = 0;

        foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities.ToList())
        {
            if (networkable is not HackableLockedCrate crate || crate.IsDestroyed)
            {
                continue;
            }

            count++;
            float distance = Vector3.Distance(player.transform.position, crate.transform.position);
            player.ChatMessage($"[LivingRust] crate at {crate.transform.position} ({distance:F0}m): dropped={crate.wasDropped}, locked={crate.IsLocked()}, hacking={crate.IsBeingHacked()}, fullyHacked={crate.IsFullyHacked()}");

            if (!crate.IsFullyHacked() && distance < nearestDistance)
            {
                nearest = crate;
                nearestDistance = distance;
            }
        }

        player.ChatMessage($"[LivingRust] {count} hackable crate(s) on the map.");

        if (adopt && nearest != null)
        {
            RegisterChinookCrate(nearest, 0f);
            player.ChatMessage($"[LivingRust] registered the nearest un-hacked crate ({nearestDistance:F0}m away) as a hack event.");
        }
    }

    // ---- what a hack-event participant carries ----

    private const int ChinookSurplusStacksBeforeDeposit = 3;
    private const float ChinookMaxDepositDetour = 500f;
    private readonly HashSet<Guid> _chinookDepositDone = new();

    /// <summary>
    /// Stacks the survivor carries that a hack fight does not need: weapons, ammunition, medical supplies, food
    /// and clothing are the kit; resources, components, construction pieces and the rest are surplus.
    /// </summary>
    private static int CountChinookSurplusStacks(BasePlayer npc)
    {
        int surplus = 0;

        foreach (Item item in npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList))
        {
            ItemCategory category = item.info.category;

            if (category == ItemCategory.Weapon || category == ItemCategory.Ammunition || category == ItemCategory.Medical
                || category == ItemCategory.Food || category == ItemCategory.Attire || category == ItemCategory.Tool
                || item.info.GetComponent<ItemModWearable>() != null)
            {
                continue;
            }

            surplus++;
        }

        return surplus;
    }

    /// <summary>
    /// Whether a survivor is fit to join a hack event: an armed ranged weapon with ammunition, and not buried in
    /// loot it has nowhere to put (a survivor with a base banks it first instead).
    /// </summary>
    private bool IsFitForCrateHack(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (!HasReadyRangedWeapon(npc))
        {
            return false;
        }

        // Travelling light already: fine. Otherwise it needs a base close enough to bank the loot on the way -
        // a survivor miles from home would spend the whole hack timer on the detour.
        return CountChinookSurplusStacks(npc) < ChinookSurplusStacksBeforeDeposit || CanBankBeforeCrate(survivor);
    }

    private bool CanBankBeforeCrate(Survivor survivor)
    {
        HomeBase home = survivor.Character.Home;

        return home != null
            && survivor.Player != null
            && Vector3.Distance(survivor.Player.transform.position, home.Position) <= ChinookMaxDepositDetour;
    }

    // Survivors on their bank-first trip, and everyone in a hack event, ignore the loot they would normally detour
    // for (ore, trees, containers on the way): they are travelling with kit only.
    private readonly HashSet<Guid> _chinookDepositing = new();

    private bool IsTravellingLightForCrate(Guid characterId)
    {
        return _chinookDepositing.Contains(characterId)
            || (_airdropParticipants.TryGetValue(characterId, out AirdropInfo info) && info.Drop is HackableLockedCrate);
    }

    private static bool IsHackComplete(HackableLockedCrate crate)
    {
        // Keyed off the hack progress, not the Locked flag: a crate spawned by command (or by another plugin) is
        // not always flagged locked even though it still has to be hacked.
        return crate == null || crate.IsDestroyed || crate.IsFullyHacked();
    }

    /// <summary>
    /// Called from a holding participant's poll while a landed crate is still locked. The first survivor with
    /// nothing else on its hands becomes the hacker: it walks up and starts the hack. Everyone else keeps
    /// holding. The hack timer runs on its own once started, so losing the hacker afterwards changes nothing;
    /// losing it before the start just hands the job to the next survivor to poll.
    /// </summary>
    private void DriveCrateHack(Survivor survivor, AirdropInfo info, BasePlayer npc)
    {
        if (info.Drop is not HackableLockedCrate crate || crate.IsDestroyed)
        {
            return;
        }

        Guid id = survivor.Character.Id;

        if (info.HackerId != Guid.Empty && info.HackerId != id && !info.Participants.Contains(info.HackerId))
        {
            info.HackerId = Guid.Empty;
        }

        if (crate.IsBeingHacked() || (info.HackerId != Guid.Empty && info.HackerId != id))
        {
            return;
        }

        if (_activeCombat.ContainsKey(id) || _activeAttacks.ContainsKey(id) || _activeMovement.ContainsKey(id))
        {
            return;
        }

        info.HackerId = id;

        if (IsWithinLootRange(npc, crate))
        {
            crate.StartHacking();
            Puts($"chinook-crate: '{survivor.Character.Alias}' started hacking the locked crate - holding the position for the hack timer.");
            return;
        }

        StartWalkingWithRecovery(
            survivor,
            GetApproachPoint(crate, npc),
            onArrived: null,
            onFailed: () => info.HackerId = Guid.Empty);
    }

    /// <summary>
    /// While the hack timer runs, an idle participant patrols the area around the crate with what it carries
    /// instead of standing still: short hops to random dry points between about 10 and 60m from the crate.
    /// </summary>
    private void RoamAroundCrate(Survivor survivor, AirdropInfo info)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float radius = UnityEngine.Random.Range(10f, 60f);
            Vector3 point = info.Position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            point.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(point) : info.Position.y;

            if (WaterLevel.GetWaterLevel(point, waves: false) > point.y + 0.3f || IsInMonumentAvoidZone(point))
            {
                continue;
            }

            StartWalkingWithRecovery(survivor, point, onArrived: null, onFailed: null);
            return;
        }
    }

    // ============================================================
    // Patrol helicopter avoidance

    // ============================================================

    // A survivor this close (flat distance) to the helicopter runs away from it.
    private const float HeliAvoidRadius = 170f;
    private const float HeliFleeDistance = 350f;
    private const float HeliFleeCooldownSeconds = 25f;

    private readonly List<PatrolHelicopter> _patrolHelis = new();
    private readonly Dictionary<Guid, float> _heliFleeUntil = new();
    private Timer _heliAvoidTimer;

    private void StartHeliAvoidance()
    {
        _patrolHelis.Clear();

        foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
        {
            if (networkable is PatrolHelicopter heli && !heli.IsDestroyed)
            {
                _patrolHelis.Add(heli);
            }
        }

        _heliAvoidTimer?.Destroy();
        _heliAvoidTimer = timer.Every(2f, RunHeliAvoidance);
    }

    private void StopHeliAvoidance()
    {
        _heliAvoidTimer?.Destroy();
        _heliAvoidTimer = null;
    }

    private void RunHeliAvoidance()
    {
        if (_engine == null)
        {
            return;
        }

        _patrolHelis.RemoveAll(h => h == null || h.IsDestroyed);

        if (_patrolHelis.Count == 0)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            BasePlayer npc = survivor.Player;
            Guid id = survivor.Character.Id;

            if (npc == null || npc.IsDestroyed || !npc.IsAlive() || survivor.Character.State == CharacterState.Dead
                || (_heliFleeUntil.TryGetValue(id, out float until) && until > now)
                || IsAirdropParticipant(id))
            {
                continue;
            }

            PatrolHelicopter nearest = null;
            float nearestDistance = HeliAvoidRadius;

            foreach (PatrolHelicopter heli in _patrolHelis)
            {
                Vector3 delta = heli.transform.position - npc.transform.position;
                delta.y = 0f;

                if (delta.magnitude < nearestDistance)
                {
                    nearestDistance = delta.magnitude;
                    nearest = heli;
                }
            }

            if (nearest != null)
            {
                FleeFromHelicopter(survivor, npc, nearest);
            }
        }
    }

    private void FleeFromHelicopter(Survivor survivor, BasePlayer npc, PatrolHelicopter heli)
    {
        Guid id = survivor.Character.Id;
        Vector3 away = npc.transform.position - heli.transform.position;
        away.y = 0f;

        if (away.sqrMagnitude < 1f)
        {
            away = UnityEngine.Random.insideUnitSphere;
            away.y = 0f;
        }

        away.Normalize();

        // Tries the straight-away direction first, then swings out either side until the point is dry land.
        Vector3 destination = npc.transform.position;
        bool found = false;

        foreach (float angle in new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f })
        {
            Vector3 candidate = npc.transform.position + Quaternion.Euler(0f, angle, 0f) * away * HeliFleeDistance;
            candidate.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(candidate) : npc.transform.position.y;

            if (WaterLevel.GetWaterLevel(candidate, waves: false) <= candidate.y + 0.3f && !IsInMonumentAvoidZone(candidate))
            {
                destination = candidate;
                found = true;
                break;
            }
        }

        _heliFleeUntil[id] = Time.realtimeSinceStartup + HeliFleeCooldownSeconds;

        if (!found)
        {
            return;
        }

        Puts($"heli-avoid: '{survivor.Character.Alias}' is {Vector3.Distance(npc.transform.position, heli.transform.position):F0}m from the patrol helicopter - running away from it.");

        CancelActiveMovement(survivor);
        CancelActiveAttack(id);

        StartLongDistanceWalk(
            survivor,
            destination,
            "away from the patrol helicopter",
            onArrived: () => ResumeAfterHeli(survivor),
            onFailed: () => ResumeAfterHeli(survivor));
    }

    private void ResumeAfterHeli(Survivor survivor)
    {
        if (survivor.Player != null && !survivor.Player.IsDestroyed && survivor.Character.State != CharacterState.Dead)
        {
            StartLootForResourcesTask(survivor);
        }
    }
}
