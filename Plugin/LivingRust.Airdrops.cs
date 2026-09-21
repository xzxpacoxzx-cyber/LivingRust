using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Airdrops as a king-of-the-hill hotspot (2026-09-21, Lucas's own
    // explicit spec). Survivors get the same heads-up a real player does
    // when the cargo plane spawns: they learn where it will drop, roughly
    // when it'll land, some of them (weak gear first) commit, each picks its
    // OWN randomized rally point in the vicinity (not stacked on the crate),
    // leaves timed so it arrives about when the drop lands, then holds until
    // the crate is actually down before moving in. Participants prioritise
    // PvP on sight against any bot or player the whole time (see
    // IsAirdropParticipant's uses in LivingRust.Combat.cs) - whoever holds
    // the ground gets the loot. Capped at AirdropMaxParticipants.
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
        public SupplyDrop Drop;
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

    // Airdrop priority (2026-09-21, live report: a gear-95 runner reached the
    // crate, got pulled into a fight, and its ordinary task pipeline - which
    // resumes on its own once combat ends - sent it hunting a stag instead of
    // finishing the drop). While a survivor is a participant of a live drop
    // its normal loot/task chain is held off (the journey loop and hold loop
    // own its movement), until it's actually at the crate looting
    // (_airdropLootAllowed) or the participation ends.
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
            // Fields (drop position, path) are populated around Spawn - read
            // them a moment later rather than racing the spawn itself.
            timer.Once(1f, () => OnCargoPlaneAnnounced(plane));
        }
        else if (entity is SupplyDrop drop)
        {
            timer.Once(0.5f, () => OnSupplyDropSpawned(drop));
        }
    }

    private void OnCargoPlaneAnnounced(CargoPlane plane)
    {
        if (plane == null || plane.IsDestroyed)
        {
            return;
        }

        Vector3 dropPosition = plane.dropPosition;

        if (dropPosition == Vector3.zero)
        {
            return;
        }

        float flightSeconds = plane.secondsToTake;
        float totalDistance = Vector3.Distance(plane.startPos, plane.endPos);
        float toDropFraction = totalDistance > 1f ? Vector3.Distance(plane.startPos, dropPosition) / totalDistance : 0.5f;
        float secondsUntilDrop = Mathf.Max(5f, flightSeconds * toDropFraction - plane.secondsTaken);

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

        // A crate that's already near the ground with no announced plane is
        // one reloaded from the world save (server start), not a new drop -
        // ignore it (2026-09-21, live test: ~18 stale crates each logged as
        // a fresh, instantly-"landed" drop).
        if (match == null && drop.transform.position.y < 300f)
        {
            return;
        }

        if (match == null)
        {
            // A drop nobody announced (admin call, another plugin) - still a
            // real hotspot, just with less warning.
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

    private void OnAirdropEntityKilled(SupplyDrop drop)
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
        List<Survivor> candidates = _engine.SurvivorManager.GetAll()
            .Where(s => s.Player != null && !s.Player.IsDestroyed
                && s.Character.State != CharacterState.Dead
                && !s.Player.IsWounded()
                && !_airdropParticipants.ContainsKey(s.Character.Id)
                && !_activeCombat.ContainsKey(s.Character.Id)
                && !IsBaseBuildInFlight(s.Character.Id)
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

            // Weak gear = far more interested (an early-game bot gains the
            // most from a drop), never zero for a geared one either.
            float interest = Mathf.Clamp(0.9f - GetGearScore(survivor.Player) / 100f, 0.35f, 0.9f);

            if (UnityEngine.Random.value > interest)
            {
                continue;
            }

            ScheduleAirdropJourney(survivor, info);
            recruited++;
        }

        return recruited;
    }

    // Queue behaviour (2026-09-21, Lucas's own explicit ask: "30 bots lining
    // up to run towards the airdrop - if any die, another joins the queue"):
    // a runner that dies (or drops out) before the crate lands frees its
    // slot and the next eligible survivor is recruited in its place.
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

            if (!info.Landed && info.Participants.Count < AirdropMaxParticipants && RecruitForAirdrop(info, maxNew: 1) > 0)
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

        // Land-only rally points (2026-09-21): a drop near the coast has water
        // within its 25-70m ring - re-pick until the point is dry ground.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
            bool cautiousRally = GetGearScore(survivor.Player) > AirdropCautiousGearScore;
            float radius = cautiousRally
                ? UnityEngine.Random.Range(AirdropCautiousRallyMinRadius, AirdropCautiousRallyMaxRadius)
                : UnityEngine.Random.Range(AirdropRallyMinRadius, AirdropRallyMaxRadius);
            rally = info.Position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            rally.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(rally) : info.Position.y;

            if (WaterLevel.GetWaterLevel(rally, waves: false) <= rally.y + 0.3f)
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
            // Mid-build or mid-fight: not worth abandoning - it sits this one out.
            info.Participants.Remove(survivor.Character.Id);
            return;
        }

        _airdropParticipants[survivor.Character.Id] = info;

        CancelActiveMovement(survivor);
        CancelActiveAttack(survivor.Character.Id);
        CancelActiveRecycling(survivor.Character.Id);

        Puts($"airdrop: '{survivor.Character.Alias}' setting out for the drop zone ({Vector3.Distance(npc.transform.position, rally):F0}m to its rally point).");

        bool arrived = false;
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
                    if (!arrived)
                    {
                        EndAirdropParticipation(survivor, resume: true);
                    }
                });
        }

        StartJourneyWalk();

        // Insistent journey (2026-09-21, live test: every recruited bot got
        // pulled into a fight within seconds of setting out, and combat
        // cancels movement - nothing ever restarted the walk, so none reached
        // the drop). Every few seconds, if the bot isn't fighting or fleeing
        // and isn't actually closing on its rally point (combat ended, or an
        // unrelated task grabbed its movement), the walk is re-issued.
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

            // Don't shove a survivor back toward a predator it's steering
            // around (2026-09-21, live test: one bot logged 421 flee events
            // against a single crocodile because this loop re-issued the
            // walk straight at it every few seconds). While a hard-avoid
            // animal is close, the walk's own detour logic has the wheel.
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

    // PvP-on-sight is a HOT-ZONE rule (2026-09-21): it applies only once a
    // participant is actually near the drop, not for the whole cross-map
    // run there (where it made every bot pick fights with everyone it
    // passed).
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
                || (info.Landed && now > info.ExpectedLandTime + AirdropPostLandLifetimeSeconds))
            {
                poll.Destroy();
                EndAirdropParticipation(survivor, resume: true);
                return;
            }

            // Weapon scan on arrival (2026-09-21, Lucas's own explicit spec): a
            // survivor with a primitive kit (less than a bow and arrows)
            // checks the 50m around the drop zone for lootable corpses -
            // somebody may already have died there holding a better weapon.
            // One corpse at a time, only while idle and not moving in; a
            // survivor with a bow+arrows or better arrives as normal.
            if (!movingIn
                && !HasReadyRangedWeapon(npc)
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

            // A fight (or anything else) can cancel the move-in walk without
            // ever calling its onFailed - re-arm it once the bot is idle
            // and still not at the crate.
            if (movingIn
                && !_activeMovement.ContainsKey(survivor.Character.Id)
                && !_activeCombat.ContainsKey(survivor.Character.Id)
                && !_activeAttacks.ContainsKey(survivor.Character.Id)
                && !(info.Drop != null && !info.Drop.IsDestroyed && IsWithinLootRange(npc, info.Drop)))
            {
                movingIn = false;
            }

            bool cautious = GetGearScore(npc) > AirdropCautiousGearScore;

            if (info.Landed && !movingIn && !_activeCombat.ContainsKey(survivor.Character.Id)
                && !(cautious && IsAirdropCrateContested(info, survivor.Character.Id)))
            {
                movingIn = true;
                Puts($"airdrop: '{survivor.Character.Alias}' is moving in on the landed crate.");

                // Loot from arm's reach, not from the crate's exact centre
                // (2026-09-21, live report: RowdySkinner oscillated around the
                // crate trying to reach its centre point, which the crate's own
                // collider blocks, instead of looting from 0.5-1m away).
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

    // Tracks each drop's landing independently of any bot (2026-09-21) - it
    // used to be checked only from a participant's hold loop, so a drop
    // nobody had reached yet never registered as landed.
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

    // King of the hill (2026-09-21, Lucas's own explicit spec): the crate is
    // the priority target - the first survivor to reach it claims it and
    // loots it directly (not via the generic nearest-container search); any
    // later arrival either backs off and starts something new (a coin flip
    // when there's already a live looter to contest) or stays, where the
    // hot-zone on-sight rule has it fight the holder for whatever's left.
    // Anything looted (weapons included) is equipped through the normal loot
    // pipeline so the survivor can defend what it just took.
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

    // Grab, arm, defend, go (2026-09-21, Lucas's own explicit spec: "protect
    // themselves, but don't dwell and wait around"). The looter equips the
    // best weapon it now holds and leaves as soon as the crate is empty or
    // a short grace has passed - but never mid-fight (combat has to end
    // first), and it keeps the hot-zone on-sight rule until it actually goes.
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

        // A crate that comes down on water floats and bobs, so it's never
        // "still" (2026-09-21, live test: crate confirmed on the ground/water
        // by Lucas, my log never registered it as landed). Also counts as
        // landed once it's within a few metres of the surface below it.
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
