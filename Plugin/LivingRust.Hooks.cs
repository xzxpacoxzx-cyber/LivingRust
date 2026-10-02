using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using Rust;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Delay before a dead survivor respawns.
    private const float RespawnDelaySeconds = 5f;

    // Tracks consecutive quick-succession deaths per survivor. Past the
    // threshold, RespawnSurvivor destroys owned sleeping bags and uses a beach spawn instead.
    private const float DeathLoopQuickSuccessionSeconds = 60f;
    private const int DeathLoopRescueThreshold = 5;
    private readonly Dictionary<Guid, int> _deathStreak = new();

    // A survivor's own death spot, remembered so it can detour there once respawned to scavenge
    // its own death bag plus whatever else is lying around (a killer's own drops, loose scraps)
    // before resuming whatever it was doing. See TryPursueDeathSiteLoot's own doc comment.
    private sealed class DeathSiteLoot
    {
        public Vector3 Position;
        public float ExpiresAt;
        public int Steps;
    }

    private const float DeathSiteLootLifetimeSeconds = 300f;
    private readonly Dictionary<Guid, DeathSiteLoot> _pendingDeathSiteLoot = new();
    private readonly Dictionary<Guid, float> _lastDeathTime = new();

    /// <summary>
    /// Forces a survivor straight to death instead of the wounded/crawl state, which
    /// doesn't resolve reliably for bot players. The Die() call is deferred by one tick.
    /// </summary>
    private void OnPlayerWound(BasePlayer player, HitInfo info)
    {
        Survivor survivor = FindSurvivorByPlayer(player);

        if (survivor == null)
        {
            return;
        }

        // HitInfo is pooled and may be reused before the deferred Die() call runs, so
        // its fields are captured here and passed to a fresh HitInfo instead.
        BaseEntity initiator = info?.Initiator;
        Vector3 hitPosition = info?.HitPositionWorld ?? player.transform.position;
        DamageTypeList clonedDamageTypes = info?.damageTypes?.Clone();

        timer.Once(0f, () =>
        {
            if (player != null && !player.IsDestroyed)
            {
                HitInfo freshInfo = new HitInfo
                {
                    Initiator = initiator,
                    HitPositionWorld = hitPosition,
                };

                if (clonedDamageTypes != null)
                {
                    freshInfo.damageTypes = clonedDamageTypes;
                }

                player.Die(freshInfo);
            }
        });
    }

    /// <summary>
    /// Handles a survivor's death by stopping any in-progress LivingRust bookkeeping
    /// (movement, timers, etc.) and scheduling the same Character to respawn.
    /// </summary>
    private void OnPlayerDeath(BasePlayer player, HitInfo info)
    {
        Survivor survivor = FindSurvivorByPlayer(player);

        if (survivor == null || survivor.Character.State == CharacterState.Dead)
        {
            return;
        }

        CancelActiveMovement(survivor);
        CancelActiveAttack(survivor.Character.Id);
        CancelActiveCombat(survivor.Character.Id);
        CancelActiveFlee(survivor.Character.Id);
        CancelActiveRecycling(survivor.Character.Id);
        _pendingRecyclerToResume.Remove(survivor.Character.Id);
        ReleaseMonumentOccupancy(survivor.Character.Id);
        OnAirdropRunnerLost(survivor.Character.Id);
        _buildSiteReservations.Remove(survivor.Character.Id);
        _pendingGhostRouteToResume.Remove(survivor.Character.Id);
        _ghostRouteDetourAttempts.Remove(survivor.Character.Id);
        _pendingGhostRouteLosRecheck.Remove(survivor.Character.Id);
        StopGhostRouteLootScan(survivor.Character.Id);
        _reloadExposureWindowUntil.Remove(survivor.Character.Id);
        _combatRetreatUntilTime.Remove(survivor.Character.Id);
        _lastCombatEndTime.Remove(survivor.Character.Id);
        _lastSelfHealTime.Remove(survivor.Character.Id);
        _healingUntilTime.Remove(survivor.Character.Id);
        ClearCombatDecisionTracking(survivor.Character.Id);
        ClearTacticalDecisionState(survivor.Character.Id);
        StopHuntSearch(survivor.Character.Id);
        _activeCardPuzzleSurvivors.Remove(survivor.Character.Id);
        _handledReadersThisPuzzle.Remove(survivor.Character.Id);
        _recentOwnKillPositions.Remove(survivor.Character.Id);
        StopExtendedOreSearch(survivor.Character.Id);
        _totalStoneGathered.Remove(survivor.Character.Id);
        _totalMetalOreGathered.Remove(survivor.Character.Id);
        _totalSulfurOreGathered.Remove(survivor.Character.Id);
        _totalWoodGathered.Remove(survivor.Character.Id);
        _resourceGatherTypeLock.Remove(survivor.Character.Id);
        _hasPlacedSleepingBag.Remove(survivor.Character.Id);

        if (_pendingSleepingBagDeployTimers.TryGetValue(survivor.Character.Id, out Timer pendingBagDeployTimer))
        {
            pendingBagDeployTimer.Destroy();
            _pendingSleepingBagDeployTimers.Remove(survivor.Character.Id);
        }

        if (_pendingArrowCraftTimers.TryGetValue(survivor.Character.Id, out Timer pendingArrowCraftTimer))
        {
            pendingArrowCraftTimer.Destroy();
            _pendingArrowCraftTimers.Remove(survivor.Character.Id);
        }

        if (_pendingBowCraftTimers.TryGetValue(survivor.Character.Id, out Timer pendingBowCraftTimer))
        {
            pendingBowCraftTimer.Destroy();
            _pendingBowCraftTimers.Remove(survivor.Character.Id);
        }

        if (_pendingPostCombatResumeTimers.TryGetValue(survivor.Character.Id, out Timer pendingPostCombatResumeTimer))
        {
            pendingPostCombatResumeTimer.Destroy();
            _pendingPostCombatResumeTimers.Remove(survivor.Character.Id);
        }

        _hasRolledPrimitiveGoal.Remove(survivor.Character.Id);

        // Resets per-life state for a survivor with no base yet, so it re-rolls a
        // home site instead of staying stuck near the shore.
        if (survivor.Character.Home == null)
        {
            _hasRolledHomeSiteStrategy.Remove(survivor.Character.Id);
            _homeSiteTarget.Remove(survivor.Character.Id);

            // Resets the monument-rush roll so the survivor gets a fresh roll next life.
            _hasRolledMonumentRush.Remove(survivor.Character.Id);
            _pursuingMonumentRushGoal.Remove(survivor.Character.Id);
            _monumentRushDeadline.Remove(survivor.Character.Id);

            // _pursuingBaseGatherGoal/_rolledBaseDesign are intentionally not cleared here, so
            // the survivor keeps working toward the same base design across deaths.
            _primitiveGoalRetryTime.Remove(survivor.Character.Id);
            _coastalProgressSnapshot.Remove(survivor.Character.Id);

            // Resets the animal-hunt quota/window for a fresh life.
            _animalHuntKillCount.Remove(survivor.Character.Id);
            _animalHuntPhaseStartTime.Remove(survivor.Character.Id);
            _activeAnimalHunt.Remove(survivor.Character.Id);

            foreach ((Guid CharacterId, string Ingredient) key in _ingredientSearchCooldownUntil.Keys.Where(k => k.CharacterId == survivor.Character.Id).ToList())
            {
                _ingredientSearchCooldownUntil.Remove(key);
            }
        }

        // Clears the door-crossing busy flag so the route isn't left disabled next life.
        _activeHomeDoorCrossings.Remove(survivor.Character.Id);
        _pursuingPrimitiveGoals.Remove(survivor.Character.Id);

        foreach ((Guid CharacterId, string Shortname) key in _pendingOneOffCraftTimers.Keys.Where(k => k.CharacterId == survivor.Character.Id).ToList())
        {
            _pendingOneOffCraftTimers[key].Destroy();
            _pendingOneOffCraftTimers.Remove(key);
        }

        // A dead bot has nothing to resume; not cleared inside CancelActiveCombat itself
        // since another call path also relies on that method after setting this entry.
        CancelPendingResumeCombatTarget(survivor.Character.Id);

        // Determines the death cause from recorded damage types. Unrecorded damage is
        // detected via Total() and reported separately, since GetMajorityDamageType()
        // would otherwise misreport it.
        string cause;

        if (info?.damageTypes == null)
        {
            cause = "Unknown (no HitInfo)";
        }
        else if (info.damageTypes.Total() <= 0f)
        {
            string initiatorDesc = info.Initiator != null
                ? $"{info.Initiator.GetType().Name} ('{info.Initiator.ShortPrefabName}')"
                : "no Initiator entity";

            cause = $"UNRECORDED damage (real GetMajorityDamageType() would misreport this as 'Cannon' - see this line's own doc comment; Initiator: {initiatorDesc})";
        }
        else
        {
            cause = info.damageTypes.GetMajorityDamageType().ToString();
        }

        Puts($"'{survivor.Character.Alias}' died ({cause}) at {player.transform.position}.");

        // Remembered so RespawnSurvivor can send this survivor back to scavenge its own death
        // bag (and whatever else - a killer's drops, other loose scraps) before resuming
        // whatever it was doing. See TryPursueDeathSiteLoot's own doc comment.
        _pendingDeathSiteLoot[survivor.Character.Id] = new DeathSiteLoot
        {
            Position = player.transform.position,
            ExpiresAt = Time.realtimeSinceStartup + DeathSiteLootLifetimeSeconds,
        };

        // Tracks the death-loop streak at the moment of death, not in RespawnSurvivor,
        // since its delay would distort how "quick" the succession looks.
        Guid deathStreakId = survivor.Character.Id;
        float deathNow = Time.realtimeSinceStartup;

        int streak = _lastDeathTime.TryGetValue(deathStreakId, out float lastDeathAt) && deathNow - lastDeathAt <= DeathLoopQuickSuccessionSeconds
            ? _deathStreak.GetValueOrDefault(deathStreakId) + 1
            : 1;

        _deathStreak[deathStreakId] = streak;
        _lastDeathTime[deathStreakId] = deathNow;

        if (streak > DeathLoopRescueThreshold)
        {
            Puts($"death-loop: '{survivor.Character.Alias}' has died {streak} times in a row in quick succession - next respawn will bypass its sleeping bag and use a beach spawn instead.");
        }

        survivor.Character.State = CharacterState.Dead;

        timer.Once(RespawnDelaySeconds, () => RespawnSurvivor(survivor));
    }

    /// <summary>
    /// Records the position of a kill made by one of our survivors, so the corpse stays
    /// reachable for looting. Only records kills where the initiator is one of our survivors.
    /// </summary>
    private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
    {
        if (entity == null || info?.InitiatorPlayer == null || _engine == null)
        {
            return;
        }

        Survivor killer = FindSurvivorByPlayer(info.InitiatorPlayer);

        if (killer == null)
        {
            return;
        }

        RememberOwnKill(killer.Character.Id, entity.transform.position);
        NotePotentialPriorityKill(killer, entity);
    }

    /// <summary>
    /// Diagnostic hook that logs when a tracked survivor's BasePlayer is destroyed through
    /// any path other than Die(). Read-only; does not modify any state.
    /// </summary>
    private void OnEntityKill(BaseNetworkable entity)
    {
        if (entity is SupplyDrop killedDrop)
        {
            OnAirdropEntityKilled(killedDrop);
            return;
        }

        if (entity is not BasePlayer player)
        {
            return;
        }

        Survivor survivor = FindSurvivorByPlayer(player);

        if (survivor == null)
        {
            return;
        }

        Puts($"diagnostic: '{survivor.Character.Alias}' (userID {survivor.Character.BotId}, instanceID {player.GetInstanceID()}) BasePlayer is being destroyed via OnEntityKill - CharacterState was already {survivor.Character.State} at this moment (Dead = normal Die() path already handled it; anything else = this destruction bypassed OnPlayerDeath entirely).");
    }

    /// <summary>
    /// Respawns a dead survivor as a new BasePlayer while keeping the same persistent
    /// Character, so unlocks and building privilege carry over. Spawns at an owned
    /// sleeping bag if available, otherwise falls back to a beach spawn.
    /// </summary>
    private void RespawnSurvivor(Survivor survivor)
    {
        Character character = survivor.Character;

        BasePlayer npc = GameManager.server.CreateEntity(
            "assets/prefabs/player/player.prefab",
            Vector3.zero,
            Quaternion.identity) as BasePlayer;

        if (npc == null)
        {
            Puts($"'{character.Alias}' failed to respawn - couldn't create a new BasePlayer.");
            return;
        }

        // userID must be set before Spawn() (see SpawnSurvivor).
        npc.userID = character.BotId;
        npc.displayName = character.Alias;

        // A survivor past the death-loop rescue threshold skips its owned bag and has every
        // bag it owns destroyed, so it can't be pulled back to the same spot again. Resets the streak.
        bool rescueFromDeathLoop = _deathStreak.TryGetValue(character.Id, out int deathStreak) && deathStreak > DeathLoopRescueThreshold;

        if (rescueFromDeathLoop)
        {
            int bagsDestroyed = DestroyOwnedSleepingBags(character.BotId);
            _deathStreak.Remove(character.Id);
            Puts($"death-loop: '{character.Alias}' is being rescued - destroyed {bagsDestroyed} owned sleeping bag(s), spawning at the beach instead.");
        }

        Vector3 spawnPos = default;
        Quaternion spawnRot = default;
        bool atOwnedBag = !rescueFromDeathLoop && TryFindOwnedBag(character.BotId, out spawnPos, out spawnRot);

        if (!atOwnedBag)
        {
            BasePlayer.SpawnPoint spawnPoint = ServerMgr.FindSpawnPoint(npc);
            spawnPos = spawnPoint.pos;
            spawnRot = spawnPoint.rot;
        }

        npc.transform.position = spawnPos;
        npc.transform.rotation = spawnRot;

        npc.Spawn();
        npc.InitializeHealth(100f, 100f);
        npc.SendNetworkUpdateImmediate();

        GiveStartingKit(npc);

        survivor.Player = npc;
        survivor.Position = spawnPos;
        character.Position = spawnPos;
        character.State = CharacterState.Alive;
        character.Spawned = true;
        survivor.Spawned = true;

        VerbosePuts($"'{character.Alias}' respawned at {spawnPos} ({(atOwnedBag ? "owned sleeping bag" : "beach")}).");

        // Resumes autonomous behaviour after respawn so the gear-weighted destination
        // roll runs fresh for the survivor's minimal starting gear.
        StartLootForResourcesTask(survivor);
    }

    /// <summary>
    /// Destroys every SleepingBag entity owned by botId, not just the one TryFindOwnedBag
    /// would pick.
    /// </summary>
    private int DestroyOwnedSleepingBags(ulong botId)
    {
        int destroyed = 0;

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities.ToArray())
        {
            if (entity is SleepingBag bag && bag.OwnerID == botId && !bag.IsDestroyed)
            {
                bag.Kill();
                destroyed++;
            }
        }

        return destroyed;
    }

    /// <summary>
    /// Searches every SleepingBag entity on the map for one owned by botId, preferring one
    /// off cooldown but falling back to one still on cooldown.
    /// </summary>
    private bool TryFindOwnedBag(ulong botId, out Vector3 position, out Quaternion rotation)
    {
        SleepingBag fallbackBag = null;

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is not SleepingBag bag || bag.OwnerID != botId || bag.IsDestroyed)
            {
                continue;
            }

            if (bag.ValidForPlayer(botId, false))
            {
                bag.GetSpawnPos(out position, out rotation);
                return true;
            }

            fallbackBag ??= bag;
        }

        if (fallbackBag != null)
        {
            fallbackBag.GetSpawnPos(out position, out rotation);
            return true;
        }

        position = default;
        rotation = default;
        return false;
    }

    /// <summary>
    /// Gives a freshly respawned survivor the same starting items as a new
    /// Rust character: a rock and a torch.
    /// </summary>
    private void GiveStartingKit(BasePlayer npc)
    {
        GiveItem(npc, "rock", 1);
        GiveItem(npc, "torch", 1);
    }

    /// <summary>
    /// Drops a survivor's torch once it starts a real task, since a bot doesn't need a
    /// light source to see. Safe to call repeatedly; no-ops if no torch is found.
    /// </summary>
    private void DropUnneededLightSource(Survivor survivor, BasePlayer npc)
    {
        Item torch = npc.inventory.FindItemByItemName("torch");

        if (torch == null)
        {
            return;
        }

        Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
        Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

        torch.Drop(dropPosition, dropVelocity);
    }

    private void GiveItem(BasePlayer npc, string shortname, int amount)
    {
        Item item = ItemManager.CreateByName(shortname, amount);

        if (item == null)
        {
            Puts($"WARNING: couldn't create starting-kit item '{shortname}' - unknown shortname?");
            return;
        }

        if (!npc.inventory.GiveItem(item))
        {
            item.Remove();
        }
    }
}