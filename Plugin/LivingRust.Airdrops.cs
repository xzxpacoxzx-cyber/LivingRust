using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Airdrops act as a king-of-the-hill hotspot: survivors learn a drop's landing
    // position and time, some commit to it, each picks its own rally point nearby,
    // and they hold until the crate lands before moving in and looting. Participants
    // prioritize PvP on sight near the drop. Capped at AirdropMaxParticipants.
    private const int AirdropMaxParticipants = 30;
    private const float AirdropMaxTravelDistance = 1800f;
    private const float AirdropRunSpeedEstimate = 3.5f;
    private const float AirdropParachuteSeconds = 400f;
    private const float AirdropEarlyArrivalMinSeconds = 60f;
    private const float AirdropEarlyArrivalMaxSeconds = 180f;
    private const float AirdropRallyMinRadius = 25f;
    private const float AirdropRallyMaxRadius = 70f;
    private const float AirdropPostLandLifetimeSeconds = 150f;
    private const float AirdropGiveUpAfterExpectedLandSeconds = 200f;
    private const float AirdropPollSeconds = 2f;
    private const float AirdropArrivalScanRadius = 50f;

    private sealed class AirdropInfo
    {
        public Vector3 Position;
        public float ExpectedLandTime;
        public LootContainer Drop;
        public Guid HackerId;
        public bool Landed;
        public bool Done;
        public Vector3 LastDropPosition;
        public int StillPolls;
        public Guid LooterId;
        public readonly HashSet<Guid> Participants = new();
    }

    private readonly List<AirdropInfo> _airdrops = new();
    private readonly Dictionary<Guid, AirdropInfo> _airdropParticipants = new();

    private bool IsAirdropParticipant(Guid characterId)
    {
        return _airdropParticipants.ContainsKey(characterId);
    }

    // While a survivor is participating in a live drop, its normal loot/task chain
    // is held off until it is actually looting the crate or participation ends.
    private readonly HashSet<Guid> _airdropLootAllowed = new();

    private bool ShouldHoldForAirdrop(Guid characterId)
    {
        return _airdropParticipants.TryGetValue(characterId, out AirdropInfo info)
            && !info.Done
            && !_airdropLootAllowed.Contains(characterId);
    }

    private void OnEntitySpawned(BaseNetworkable entity)
    {
        if (_engine == null)
        {
            return;
        }

        if (entity is CargoPlane plane)
        {
            // Reads plane fields a moment after spawn rather than racing the spawn itself.
            timer.Once(1f, () => OnCargoPlaneAnnounced(plane));
        }
        else if (entity is SupplyDrop drop)
        {
            timer.Once(0.5f, () => OnSupplyDropSpawned(drop));
        }
        else if (entity is HackableLockedCrate crate)
        {
            timer.Once(1f, () => OnHackableCrateSpawned(crate));
        }
        else if (entity is PatrolHelicopter heli)
        {
            _patrolHelis.Add(heli);
        }
    }

    private void OnCargoPlaneAnnounced(CargoPlane plane)
    {
        if (plane == null || plane.IsDestroyed)
        {
            return;
        }

        Vector3 dropPosition = GetCargoPlaneDropPosition(plane);

        if (dropPosition == Vector3.zero)
        {
            return;
        }

        Vector3 planeStartPos = GetCargoPlaneStartPos(plane);
        float flightSeconds = GetCargoPlaneSecondsToTake(plane);
        float totalDistance = Vector3.Distance(planeStartPos, GetCargoPlaneEndPos(plane));
        float toDropFraction = totalDistance > 1f ? Vector3.Distance(planeStartPos, dropPosition) / totalDistance : 0.5f;
        float secondsUntilDrop = Mathf.Max(5f, flightSeconds * toDropFraction - GetCargoPlaneSecondsTaken(plane));

        AirdropInfo info = new()
        {
            Position = dropPosition,
            ExpectedLandTime = Time.realtimeSinceStartup + secondsUntilDrop + AirdropParachuteSeconds,
        };

        _airdrops.RemoveAll(a => a.Done);
        _airdrops.Add(info);
        StartAirdropMonitor(info);

        int recruited = RecruitForAirdrop(info);

        Puts($"airdrop: cargo plane inbound - drop expected at {dropPosition} in ~{secondsUntilDrop:F0}s (lands ~{secondsUntilDrop + AirdropParachuteSeconds:F0}s from now), {recruited} survivor(s) heading out.");
    }

    private void OnSupplyDropSpawned(SupplyDrop drop)
    {
        if (drop == null || drop.IsDestroyed)
        {
            return;
        }

        AirdropInfo match = _airdrops
            .Where(a => !a.Done && a.Drop == null
                && Vector2.Distance(new Vector2(a.Position.x, a.Position.z), new Vector2(drop.transform.position.x, drop.transform.position.z)) < 250f)
            .OrderBy(a => a.ExpectedLandTime)
            .FirstOrDefault();

        // A crate already near the ground with no announced plane was reloaded from
        // the world save at server start, not a new drop, so it is ignored.
        if (match == null && drop.transform.position.y < 300f)
        {
            return;
        }

        if (match == null)
        {
            // An unannounced drop (admin call, another plugin) - still a real
            // hotspot, just with less warning.
            match = new AirdropInfo
            {
                Position = drop.transform.position,
                ExpectedLandTime = Time.realtimeSinceStartup + AirdropParachuteSeconds,
            };

            _airdrops.Add(match);
            RecruitForAirdrop(match);
        }

        match.Drop = drop;
        match.Position = drop.transform.position;
        match.LastDropPosition = drop.transform.position;
        Puts($"airdrop: supply drop spawned at {drop.transform.position} (tracking it for landing).");
        StartAirdropMonitor(match);
    }

    private void OnAirdropEntityKilled(LootContainer drop)
    {
        foreach (AirdropInfo info in _airdrops)
        {
            if (info.Drop == drop)
            {
                info.Done = true;
            }
        }
    }

    private int RecruitForAirdrop(AirdropInfo info, int maxNew = int.MaxValue)
    {
        if (info.Drop is HackableLockedCrate)
        {
            LogCrateRecruitReasons(info);
        }

        List<Survivor> candidates = _engine.SurvivorManager.GetAll()
            .Where(s => s.Player != null && !s.Player.IsDestroyed
                && s.Character.State != CharacterState.Dead
                && !s.Player.IsWounded()
                && !_airdropParticipants.ContainsKey(s.Character.Id)
                && !_activeCombat.ContainsKey(s.Character.Id)
                && !IsBaseBuildInFlight(s.Character.Id)
                && (!(info.Drop is HackableLockedCrate) || IsFitForCrateHack(s))
                && Vector3.Distance(s.Player.transform.position, info.Position) <= AirdropMaxTravelDistance)
            .OrderBy(_ => UnityEngine.Random.value)
            .ToList();

        int recruited = 0;

        foreach (Survivor survivor in candidates)
        {
            if (info.Participants.Count >= AirdropMaxParticipants || recruited >= maxNew)
            {
                break;
            }

            // Weaker gear means more interest, though geared survivors are never fully disinterested.
            // A hack event is a fight for armed survivors, so well-geared ones are far keener on it than on a supply drop.
            float interest = Mathf.Clamp(0.9f - GetGearScore(survivor.Player) / 100f, info.Drop is HackableLockedCrate ? 0.75f : 0.35f, 0.9f);

            if (UnityEngine.Random.value > interest)
            {
                continue;
            }

            ScheduleAirdropJourney(survivor, info);
            recruited++;
        }

        return recruited;
    }

    // A runner that dies or drops out before the crate lands frees its slot, and
    // the next eligible survivor is recruited in its place.
    private void OnAirdropRunnerLost(Guid characterId)
    {
        _airdropParticipants.Remove(characterId);
        _airdropLootAllowed.Remove(characterId);

        foreach (AirdropInfo info in _airdrops)
        {
            if (info.Done || !info.Participants.Remove(characterId))
            {
                continue;
            }

            // A hack event lasts 15+ minutes, so a lost participant is replaced even after the crate has landed.
            bool stillRecruiting = !info.Landed || (info.Drop is HackableLockedCrate crate && !IsHackComplete(crate));

            if (stillRecruiting && info.Participants.Count < AirdropMaxParticipants && RecruitForAirdrop(info, maxNew: 1) > 0)
            {
                Puts($"airdrop: a runner was lost - the next survivor in the queue is heading out ({info.Participants.Count}/{AirdropMaxParticipants}).");
            }
        }
    }

    // A survivor above this gear score still runs for the drop but keeps a
    // safer distance and only closes in once the crate is uncontested.
    private const int AirdropCautiousGearScore = 50;
    private const float AirdropCautiousRallyMinRadius = 110f;
    private const float AirdropCautiousRallyMaxRadius = 170f;
    private const float AirdropContestedRadius = 35f;

    private bool IsAirdropCrateContested(AirdropInfo info, Guid selfId)
    {
        foreach (Survivor other in _engine.SurvivorManager.GetAll())
        {
            BasePlayer otherNpc = other.Player;

            if (other.Character.Id != selfId && otherNpc != null && !otherNpc.IsDestroyed
                && other.Character.State != CharacterState.Dead
                && Vector3.Distance(otherNpc.transform.position, info.Position) <= AirdropContestedRadius)
            {
                return true;
            }
        }

        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            if (player != null && !player.IsDestroyed && player.IsAlive() && !player.IsSleeping()
                && Vector3.Distance(player.transform.position, info.Position) <= AirdropContestedRadius)
            {
                return true;
            }
        }

        return false;
    }

    private void ScheduleAirdropJourney(Survivor survivor, AirdropInfo info)
    {
        Guid characterId = survivor.Character.Id;
        info.Participants.Add(characterId);

        Vector3 rally = info.Position;

        // Re-picks the rally point until it is on dry ground and clear of any
        // monument safezone (LivingRust.MonumentAvoidZones.cs).
        for (int attempt = 0; attempt < 12; attempt++)
        {
            float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
            bool cautiousRally = GetGearScore(survivor.Player) > AirdropCautiousGearScore;
            float radius = cautiousRally
                ? UnityEngine.Random.Range(AirdropCautiousRallyMinRadius, AirdropCautiousRallyMaxRadius)
                : UnityEngine.Random.Range(AirdropRallyMinRadius, AirdropRallyMaxRadius);
            rally = info.Position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            rally.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(rally) : info.Position.y;

            if (WaterLevel.GetWaterLevel(rally, waves: false) <= rally.y + 0.3f && !IsInMonumentAvoidZone(rally))
            {
                break;
            }
        }

        float distance = Vector3.Distance(survivor.Player.transform.position, rally);
        float travelSeconds = distance / AirdropRunSpeedEstimate;
        float untilLand = info.ExpectedLandTime - Time.realtimeSinceStartup;
        float earlyArrivalSeconds = UnityEngine.Random.Range(AirdropEarlyArrivalMinSeconds, AirdropEarlyArrivalMaxSeconds);
        float departIn = Mathf.Max(0f, untilLand - travelSeconds - earlyArrivalSeconds);

        BasePlayer scheduledNpc = survivor.Player;
        timer.Once(departIn, () => BeginAirdropJourney(survivor, info, rally, scheduledNpc));
    }

    private void BeginAirdropJourney(Survivor survivor, AirdropInfo info, Vector3 rally, BasePlayer scheduledNpc)
    {
        BasePlayer npc = survivor.Player;

        if (info.Done || npc == null || npc.IsDestroyed || npc != scheduledNpc || survivor.Character.State == CharacterState.Dead)
        {
            info.Participants.Remove(survivor.Character.Id);
            return;
        }

        if (IsBaseBuildInFlight(survivor.Character.Id) || _activeCombat.ContainsKey(survivor.Character.Id))
        {
            if (info.Drop is HackableLockedCrate)
            {
                // A hack event is long: finish the fight, then set out.
                timer.Once(5f, () => BeginAirdropJourney(survivor, info, rally, scheduledNpc));
                return;
            }

            // Mid-build or mid-fight: sits this drop out rather than abandoning it.
            info.Participants.Remove(survivor.Character.Id);
            return;
        }

        // A hack event is a fight, not a loot run: a survivor with a base banks everything that is not weapon,
        // ammunition, clothing or medical supplies first, then sets out (2026-10-04, Lucas).
        if (info.Drop is HackableLockedCrate
            && !_chinookDepositDone.Contains(survivor.Character.Id)
            && CountChinookSurplusStacks(npc) >= ChinookSurplusStacksBeforeDeposit)
        {
            _chinookDepositDone.Add(survivor.Character.Id);

            if (!CanBankBeforeCrate(survivor))
            {
                // Loaded with loot and no base close enough to bank it: it sits this one out rather than
                // turning up to a hack with a pack full of wood and stone.
                Puts($"chinook-crate: '{survivor.Character.Alias}' is carrying loot it cannot bank in time - sitting this one out.");
                info.Participants.Remove(survivor.Character.Id);
                return;
            }

            Puts($"chinook-crate: '{survivor.Character.Alias}' is carrying loot it does not need - banking it at its base before heading to the crate.");
            _chinookDepositing.Add(survivor.Character.Id);

            GhostReturnHomeAndDeposit(survivor, () =>
            {
                _chinookDepositing.Remove(survivor.Character.Id);
                BeginAirdropJourney(survivor, info, rally, survivor.Player);
            });
            return;
        }

        _airdropParticipants[survivor.Character.Id] = info;

        CancelActiveMovement(survivor);

        CancelActiveAttack(survivor.Character.Id);
        CancelActiveRecycling(survivor.Character.Id);

        Puts($"airdrop: '{survivor.Character.Alias}' setting out for the drop zone ({Vector3.Distance(npc.transform.position, rally):F0}m to its rally point).");

        bool arrived = false;
        int journeyRetries = 0;
        Guid characterId = survivor.Character.Id;

        void StartJourneyWalk()
        {
            StartLongDistanceWalk(
                survivor,
                rally,
                "an airdrop",
                onArrived: () =>
                {
                    arrived = true;
                    HoldForAirdrop(survivor, info);
                },
                onFailed: () =>
                {
                    if (arrived)
                    {
                        return;
                    }

                    // A hack event is worth a few more tries before giving up on the walk.
                    if (info.Drop is HackableLockedCrate && !info.Done && ++journeyRetries <= 4)
                    {
                        timer.Once(3f, StartJourneyWalk);
                        return;
                    }

                    EndAirdropParticipation(survivor, resume: true);
                });
        }

        StartJourneyWalk();

        // Periodically re-issues the walk if the survivor isn't fighting/fleeing and
        // isn't making progress toward its rally point, since combat or another
        // task can otherwise silently cancel the movement.
        float lastDistance = Vector3.Distance(npc.transform.position, rally);
        Timer journeyTimer = null;

        journeyTimer = timer.Every(AirdropJourneyCheckSeconds, () =>
        {
            BasePlayer current = survivor.Player;

            if (arrived || info.Done || current == null || current.IsDestroyed || current != scheduledNpc
                || survivor.Character.State == CharacterState.Dead || !_airdropParticipants.ContainsKey(characterId))
            {
                journeyTimer.Destroy();
                return;
            }

            if (_activeCombat.ContainsKey(characterId) || _activeFlee.ContainsKey(characterId) || _activeAttacks.ContainsKey(characterId))
            {
                return;
            }

            // Skips re-issuing the walk while a hard-avoid animal is nearby, so this
            // loop doesn't shove the survivor straight back into a predator it's
            // detouring around.
            if (TryFindNearbyHardAvoidAnimal(current, out _))
            {
                lastDistance = Vector3.Distance(current.transform.position, rally);
                return;
            }

            float distance = Vector3.Distance(current.transform.position, rally);

            if (lastDistance - distance < AirdropJourneyMinProgressMeters)
            {
                CancelActiveMovement(survivor);
                StartJourneyWalk();
            }

            lastDistance = distance;
        });
    }

    private const float AirdropJourneyCheckSeconds = 6f;
    private const float AirdropJourneyMinProgressMeters = 8f;

    // PvP-on-sight applies only once a participant is near the drop, not for the
    // whole run there.
    private const float AirdropHotZoneRadius = 150f;

    private bool IsInAirdropHotZone(Guid characterId, Vector3 position)
    {
        return _airdropParticipants.TryGetValue(characterId, out AirdropInfo info)
            && Vector3.Distance(position, info.Position) <= AirdropHotZoneRadius;
    }

    // Holds near the rally point until the crate is actually down (or the
    // drop is gone / never shows), then moves in and loots. One timer per
    // participant doubles as its lifetime cleanup.
    private void HoldForAirdrop(Survivor survivor, AirdropInfo info)
    {
        bool movingIn = false;
        BasePlayer holdNpc = survivor.Player;
        HashSet<NetworkableId> scannedCorpses = new();
        Puts($"airdrop: '{survivor.Character.Alias}' reached its rally point near the drop and is holding.");
        float arrivedAt = Time.realtimeSinceStartup;
        Timer poll = null;
        float nextRoamAt = 0f;

        poll = timer.Every(AirdropPollSeconds, () =>
        {
            BasePlayer npc = survivor.Player;
            float now = Time.realtimeSinceStartup;

            if (npc == null || npc.IsDestroyed || npc != holdNpc || survivor.Character.State == CharacterState.Dead)
            {
                poll.Destroy();
                EndAirdropParticipation(survivor, resume: false);
                return;
            }

            UpdateAirdropLanded(info);

            bool crateEmpty = info.Drop != null && !info.Drop.IsDestroyed && info.Drop.inventory != null && info.Drop.inventory.itemList.Count == 0;

            if (info.Done || crateEmpty
                || (info.Drop == null && now > info.ExpectedLandTime + AirdropGiveUpAfterExpectedLandSeconds)
                || (info.Landed && now > info.ExpectedLandTime + (info.Drop is HackableLockedCrate ? AirdropHackEventLifetimeSeconds : AirdropPostLandLifetimeSeconds)))
            {
                poll.Destroy();
                EndAirdropParticipation(survivor, resume: true);
                return;
            }

            // A survivor with a primitive kit checks nearby lootable corpses for a
            // better weapon while idle and not moving in. At a hack event everyone checks the bodies around the
            // crate - ammo, medical supplies and gear - before starting the hack (2026-10-04, Lucas).
            if (!movingIn
                && (info.Drop is HackableLockedCrate || !HasReadyRangedWeapon(npc))
                && !_activeMovement.ContainsKey(survivor.Character.Id)
                && !_activeCombat.ContainsKey(survivor.Character.Id)
                && !_activeAttacks.ContainsKey(survivor.Character.Id)
                && _engine.NavigationManager.TryFindNearestLootableCorpse(
                    npc.transform.position,
                    AirdropArrivalScanRadius,
                    out LootableCorpse scanCorpse,
                    c => !scannedCorpses.Contains(c.net.ID)
                        && c.containers != null
                        && c.containers.Any(ct => ct != null && ct.itemList.Count > 0)))
            {
                scannedCorpses.Add(scanCorpse.net.ID);
                VerbosePuts($"airdrop: '{survivor.Character.Alias}' (primitive kit) is checking a body near the drop zone for a better weapon.");

                StartWalkingWithRecovery(
                    survivor,
                    scanCorpse.transform.position,
                    onArrived: () => LootCorpseAndContinue(survivor, scanCorpse, new LootTaskState()),
                    onFailed: null);
            }

            // Re-arms the move-in walk if it got cancelled without calling onFailed.
            if (movingIn
                && !_activeMovement.ContainsKey(survivor.Character.Id)
                && !_activeCombat.ContainsKey(survivor.Character.Id)
                && !_activeAttacks.ContainsKey(survivor.Character.Id)
                && !(info.Drop != null && !info.Drop.IsDestroyed && IsWithinLootRange(npc, info.Drop)))
            {
                movingIn = false;
            }

            // A locked crate is hacked first (one survivor starts it, the rest hold) and only looted once it unlocks.
            bool hackPending = info.Drop is HackableLockedCrate lockedCrate && !IsHackComplete(lockedCrate);

            if (hackPending && info.Landed)
            {
                DriveCrateHack(survivor, info, npc);

                if (now >= nextRoamAt
                    && !_activeMovement.ContainsKey(survivor.Character.Id)
                    && !_activeCombat.ContainsKey(survivor.Character.Id)
                    && !_activeAttacks.ContainsKey(survivor.Character.Id))
                {
                    nextRoamAt = now + UnityEngine.Random.Range(6f, 16f);
                    RoamAroundCrate(survivor, info);
                }
            }

            bool cautious = GetGearScore(npc) > AirdropCautiousGearScore;

            if (info.Landed && !hackPending && !movingIn && !_activeCombat.ContainsKey(survivor.Character.Id)
                && !(cautious && IsAirdropCrateContested(info, survivor.Character.Id)))
            {
                movingIn = true;
                Puts($"airdrop: '{survivor.Character.Alias}' is moving in on the landed crate.");

                // Loots from arm's reach rather than the crate's exact center, which
                // its own collider blocks.
                if (info.Drop != null && !info.Drop.IsDestroyed && IsWithinLootRange(npc, info.Drop))
                {
                    LootAirdropCrate(survivor, info);
                }
                else
                {
                    Vector3 approach = info.Drop != null && !info.Drop.IsDestroyed ? GetApproachPoint(info.Drop, npc) : info.Position;

                    StartWalkingWithRecovery(
                        survivor,
                        approach,
                        onArrived: () => LootAirdropCrate(survivor, info),
                        onFailed: () => movingIn = false);
                }
            }
        });
    }

    // Tracks each drop's landing independently of any bot, so a drop nobody has
    // reached yet still registers as landed.
    private void StartAirdropMonitor(AirdropInfo info)
    {
        Timer monitor = null;

        monitor = timer.Every(AirdropPollSeconds, () =>
        {
            UpdateAirdropLanded(info);

            if (info.Done || Time.realtimeSinceStartup > info.ExpectedLandTime + 900f)
            {
                monitor.Destroy();
            }
        });
    }

    // King of the hill: the first survivor to reach the crate claims and loots it
    // directly. A later arrival either backs off (a coin flip) or stays and fights
    // the current looter under the hot-zone on-sight rule. Looted gear is equipped
    // through the normal loot pipeline so the survivor can defend it.
    private void LootAirdropCrate(Survivor survivor, AirdropInfo info)
    {
        Guid characterId = survivor.Character.Id;
        _airdropLootAllowed.Add(characterId);

        if (info.Done || info.Drop == null || info.Drop.IsDestroyed)
        {
            EndAirdropParticipation(survivor, resume: true);
            return;
        }

        bool looterAlive = info.LooterId != Guid.Empty
            && info.LooterId != characterId
            && _engine.SurvivorManager.Get(info.LooterId) is Survivor looter
            && looter.Player != null && !looter.Player.IsDestroyed
            && looter.Character.State != CharacterState.Dead;

        if (looterAlive)
        {
            if (UnityEngine.Random.value < AirdropLateArrivalRunAwayChance)
            {
                Puts($"airdrop: '{survivor.Character.Alias}' arrived after the crate was claimed - backing off.");
                EndAirdropParticipation(survivor, resume: true);
                return;
            }

            ContinueLootTask(survivor, new LootTaskState(), forceLocalScan: true);
            return;
        }

        info.LooterId = characterId;
        Puts($"airdrop: '{survivor.Character.Alias}' got to the crate first and is looting it.");
        LootContainerAndContinue(survivor, info.Drop, new LootTaskState());
        LeaveAirdropSoon(survivor, info);
    }

    // The looter equips its best weapon and leaves once the crate is empty or a
    // short grace period passes, but never mid-fight; the hot-zone on-sight rule
    // stays active until it actually leaves.
    private const float AirdropLeaveAfterLootSeconds = 20f;

    private void LeaveAirdropSoon(Survivor survivor, AirdropInfo info)
    {
        float startedAt = Time.realtimeSinceStartup;
        bool armed = false;
        Timer leaveTimer = null;

        leaveTimer = timer.Every(3f, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead || !_airdropParticipants.ContainsKey(survivor.Character.Id))
            {
                leaveTimer.Destroy();
                return;
            }

            if (!armed && Time.realtimeSinceStartup - startedAt >= 8f)
            {
                armed = true;
                EquipBestWeaponForDisplay(survivor);
            }

            bool crateEmpty = info.Drop == null || info.Drop.IsDestroyed || info.Drop.inventory == null || info.Drop.inventory.itemList.Count == 0;
            bool graceOver = Time.realtimeSinceStartup - startedAt >= AirdropLeaveAfterLootSeconds;

            if ((crateEmpty || graceOver) && !_activeCombat.ContainsKey(survivor.Character.Id))
            {
                leaveTimer.Destroy();
                EquipBestWeaponForDisplay(survivor);
                Puts($"airdrop: '{survivor.Character.Alias}' has what it came for - leaving the drop zone.");

                if (info.Drop is HackableLockedCrate && survivor.Character.Home != null)
                {
                    // The haul goes home and into storage (the normal base trip also handles gear upgrades).
                    EndAirdropParticipation(survivor, resume: false);
                    Puts($"chinook-crate: '{survivor.Character.Alias}' is taking the crate loot home.");
                    GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor));
                    return;
                }

                EndAirdropParticipation(survivor, resume: true);
            }
        });
    }

    private const float AirdropLateArrivalRunAwayChance = 0.5f;

    private void UpdateAirdropLanded(AirdropInfo info)
    {
        if (info.Landed || info.Drop == null || info.Drop.IsDestroyed)
        {
            return;
        }

        Vector3 current = info.Drop.transform.position;

        if (Vector3.Distance(current, info.LastDropPosition) < 0.2f)
        {
            info.StillPolls++;
        }
        else
        {
            info.StillPolls = 0;
        }

        info.LastDropPosition = current;
        info.Position = current;

        // A crate on water floats and bobs, so it's never fully still; also counts
        // as landed once within a few meters of the surface below it.
        float surface = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(current) : current.y - 100f;
        float waterSurface = WaterLevel.GetWaterLevel(current, waves: false);
        surface = Mathf.Max(surface, waterSurface);

        if (info.StillPolls >= 2 || current.y - surface < 4f)
        {
            info.Landed = true;
            Puts($"airdrop: crate landed at {current} - {info.Participants.Count} survivor(s) converging.");
        }
    }

    private void EndAirdropParticipation(Survivor survivor, bool resume)
    {
        Guid characterId = survivor.Character.Id;
        _airdropLootAllowed.Remove(characterId);
        _chinookDepositDone.Remove(characterId);
        _chinookDepositing.Remove(characterId);

        if (!_airdropParticipants.Remove(characterId, out AirdropInfo info))
        {
            return;
        }

        info.Participants.Remove(characterId);

        if (resume && survivor.Player != null && !survivor.Player.IsDestroyed && survivor.Character.State != CharacterState.Dead)
        {
            StartLootForResourcesTask(survivor);
        }
    }
}
