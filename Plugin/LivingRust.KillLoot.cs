using System;
using System.Collections.Generic;
using System.Linq;
using Facepunch;
using LivingRust.Models;
using Oxide.Plugins;
using Rust;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Priority loot after a "fight up" kill: when a survivor kills a player or bot
    // with a higher gear score than its own, that body and whatever weapon it
    // dropped become the survivor's top priority, above every other task.
    private const float PriorityKillLootLifetimeSeconds = 120f;
    private const float PriorityKillLootSearchRadius = 15f;
    private const int PriorityKillLootMaxSteps = 10;

    private sealed class PriorityKillLoot
    {
        public Vector3 Position;
        public float ExpiresAt;
        public int Steps;
        public int Waits;
        public bool HighValue;
    }

    // A victim above this gear score is a body worth chasing hard: longer to get to it, more patience, more steps.
    private const int KillLootHighValueGearScore = 20;

    private readonly Dictionary<Guid, PriorityKillLoot> _priorityKillLoot = new();

    private void NotePotentialPriorityKill(Survivor killer, BaseCombatEntity victim)
    {
        if (killer.Player == null)
        {
            return;
        }

        if (victim is BasePlayer victimPlayer)
        {
            int victimGear = GetGearScore(victimPlayer);
            int killerGear = GetGearScore(killer.Player);

            // Every kill of a player or bot is worth a look at the body, whatever its gear (2026-10-04, Lucas: a
            // same-gear AK bot's corpse was ignored). Above gear 20 the survivor wants it badly.
            bool highValue = victimGear > KillLootHighValueGearScore;

            _priorityKillLoot[killer.Character.Id] = new PriorityKillLoot
            {
                Position = victim.transform.position,
                ExpiresAt = Time.realtimeSinceStartup + PriorityKillLootLifetimeSeconds * (highValue ? 2f : 1f),
                HighValue = highValue,
            };

            Puts($"kill-loot: '{killer.Character.Alias}' (gear {killerGear}) just killed a {(highValue ? "well-geared " : string.Empty)}target (gear {victimGear}) - going straight for the body and whatever it dropped.");
            return;
        }

        // An animal kill is worth a special trip too, purely for the harvest (cloth/meat for
        // bandages) - an animal corpse is the same entity, not a LootableCorpse, so it's only
        // worth the trip when the survivor actually owns a tool that can harvest it.
        if (IsAnyAnimal(victim) && HasAnimalHarvestTool(killer.Player))
        {
            _priorityKillLoot[killer.Character.Id] = new PriorityKillLoot
            {
                Position = victim.transform.position,
                ExpiresAt = Time.realtimeSinceStartup + PriorityKillLootLifetimeSeconds,
            };

            VerbosePuts($"kill-loot: '{killer.Character.Alias}' just killed a '{victim.ShortPrefabName}' - going to harvest it.");
        }
    }

    /// <summary>
    /// Any wild animal (2026-10-04, Lucas's spec: harvest EVERY animal it kills, not just predators): the
    /// older BaseAnimalNPC family (chicken, boar, stag, wolf, bear, ...) and the Gen2 animals.
    /// </summary>
    private static bool IsAnyAnimal(BaseEntity entity)
    {
        return entity is BaseAnimalNPC
            || entity is Rust.Ai.Gen2.Bear
            || entity is Rust.Ai.Gen2.PolarBear
            || entity is Rust.Ai.Gen2.Boar
            || entity is Rust.Ai.Gen2.Stag
            || entity is Rust.Ai.Gen2.Wolf2
            || entity is Rust.Ai.Gen2.Crocodile
            || entity is Rust.Ai.Gen2.Panther
            || entity is Rust.Ai.Gen2.Tiger;
    }

    /// <summary>
    /// A hatchet, jackhammer, bone knife or combat knife (any of them, none preferred). A rock alone only
    /// counts for a naked, base-less survivor - the one that most needs the cloth, meat and fat, and is
    /// allowed to gather with a rock anyway (HasAnyGatherCapableTool). Everyone else crafts a real tool.
    /// </summary>
    private bool HasAnimalHarvestTool(BasePlayer npc)
    {
        return HasAnyGatherCapableTool(npc, AnimalHarvestToolPriority);
    }

    /// <summary>
    /// Older-style animals (chicken, boar, stag...) leave a harvestable BaseCorpse, not a lootable body.
    /// Player and scientist corpses are LootableCorpse and are excluded here.
    /// </summary>
    private bool TryFindNearestAnimalBaseCorpse(Vector3 position, float radius, out BaseCorpse corpse)
    {
        List<BaseCorpse> candidates = Pool.Get<List<BaseCorpse>>();
        corpse = null;
        float nearestDistanceSqr = float.MaxValue;

        try
        {
            Vis.Entities(position, radius, candidates);

            foreach (BaseCorpse candidate in candidates)
            {
                if (candidate == null || candidate.IsDestroyed || candidate is LootableCorpse)
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - position).sqrMagnitude;

                if (distanceSqr < nearestDistanceSqr)
                {
                    nearestDistanceSqr = distanceSqr;
                    corpse = candidate;
                }
            }
        }
        finally
        {
            Pool.FreeUnmanaged(ref candidates);
        }

        return corpse != null;
    }

    /// <summary>
    /// Finds the nearest dead-but-not-destroyed huntable animal near position. An animal
    /// corpse isn't a LootableCorpse - it's the same BaseCombatEntity, now harvestable in
    /// place with a bladed tool for cloth/meat, same mechanic as a tree/ore node.
    /// </summary>
    private bool TryFindNearestDeadHuntableAnimal(Vector3 position, float radius, out BaseCombatEntity animal)
    {
        List<BaseCombatEntity> candidates = Pool.Get<List<BaseCombatEntity>>();
        animal = null;
        float nearestDistanceSqr = float.MaxValue;

        try
        {
            CollectEntitiesInRange<Rust.Ai.Gen2.Bear>(position, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.PolarBear>(position, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Boar>(position, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Stag>(position, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Wolf2>(position, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Crocodile>(position, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Panther>(position, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Tiger>(position, radius, candidates);

            foreach (BaseCombatEntity candidate in candidates)
            {
                if (candidate == null || candidate.IsDestroyed || candidate.IsAlive())
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - position).sqrMagnitude;

                if (distanceSqr < nearestDistanceSqr)
                {
                    nearestDistanceSqr = distanceSqr;
                    animal = candidate;
                }
            }
        }
        finally
        {
            Pool.FreeUnmanaged(ref candidates);
        }

        return animal != null;
    }

    // Capped low since a corpse harvest is a bonus, not a full gather session.
    private const int MaxHitsPerAnimalCorpse = 10;

    /// <summary>
    /// Swing loop against a just-killed animal's corpse, same DoAttackShared pipeline as
    /// StartMeleeCombat's live swings and StartGatheringResourceNode's tree/ore swings.
    /// </summary>
    private void StartHarvestingAnimalCorpse(Survivor survivor, BaseCombatEntity corpse, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || corpse == null || corpse.IsDestroyed || _activeCombat.ContainsKey(characterId))
        {
            _priorityKillLoot.Remove(characterId);
            ContinueLootTask(survivor, state);
            return;
        }

        BaseMelee melee = EquipBestGatherToolForType(survivor, AnimalHarvestToolPriority);

        if (melee == null)
        {
            _priorityKillLoot.Remove(characterId);
            ContinueLootTask(survivor, state);
            return;
        }

        CancelActiveAttack(characterId);

        int hits = 0;
        Timer harvestTimer = null;

        VerbosePuts($"kill-loot: '{survivor.Character.Alias}' is harvesting the '{corpse.ShortPrefabName}' it just killed.");

        harvestTimer = timer.Every(AttackHitInterval, () =>
        {
            BasePlayer currentNpc = survivor.Player;

            void FinishHarvest()
            {
                harvestTimer.Destroy();
                _activeAttacks.Remove(characterId);
                _priorityKillLoot.Remove(characterId);
                ContinueLootTask(survivor, state);
            }

            if (currentNpc == null || currentNpc.IsDestroyed || _activeCombat.ContainsKey(characterId)
                || corpse == null || corpse.IsDestroyed || hits >= MaxHitsPerAnimalCorpse)
            {
                FinishHarvest();
                return;
            }

            if (Vector3.Distance(currentNpc.transform.position, corpse.transform.position) > MeleeEngagementRange + 1f)
            {
                FinishHarvest();
                return;
            }

            if (melee.HasAttackCooldown())
            {
                return;
            }

            AimAtPlayer(currentNpc, corpse);

            hits++;

            if (melee is Jackhammer harvestJackhammer)
            {
                harvestJackhammer.SetEngineStatus(true);
            }

            melee.ServerUse();
            melee.CancelInvoke(melee.ServerUse_Strike);

            HitInfo info = Pool.Get<HitInfo>();
            info.Init(currentNpc, corpse, DamageType.Generic, 0f, corpse.transform.position);
            info.Weapon = melee;
            info.WeaponPrefab = melee;
            info.PointStart = currentNpc.eyes.position;
            info.PointEnd = corpse.transform.position;

            melee.DoAttackShared(info);

            Pool.Free(ref info);

            if (hits >= MaxHitsPerAnimalCorpse || corpse.IsDestroyed)
            {
                VerbosePuts($"kill-loot: '{survivor.Character.Alias}' finished harvesting the '{corpse.ShortPrefabName}' ({hits} hit(s)).");
                FinishHarvest();
            }
        });

        _activeAttacks[characterId] = harvestTimer;
    }

    private const int PriorityKillLootMaxWaits = 8;
    private const float PriorityKillLootWaitSeconds = 5f;
    private const float PriorityKillLootThreatRadius = 40f;

    private bool IsPriorityKillLootSafe(Survivor survivor, BasePlayer npc, PriorityKillLoot entry, out string reason)
    {
        int myGear = GetGearScore(npc);
        int others = 0;
        int strongestOtherGear = int.MinValue;

        void Consider(BasePlayer other)
        {
            if (other == null || other == npc || other.IsDestroyed || !other.IsAlive() || other.IsSleeping())
            {
                return;
            }

            if (Vector3.Distance(other.transform.position, entry.Position) > PriorityKillLootThreatRadius
                && Vector3.Distance(other.transform.position, npc.transform.position) > PriorityKillLootThreatRadius)
            {
                return;
            }

            others++;
            strongestOtherGear = Math.Max(strongestOtherGear, GetGearScore(other));
        }

        foreach (Survivor other in _engine.SurvivorManager.GetAll())
        {
            if (other != survivor && other.Character.State != CharacterState.Dead)
            {
                Consider(other.Player);
            }
        }

        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            if (player != null && player.IsConnected)
            {
                Consider(player);
            }
        }

        if (others == 0)
        {
            reason = null;
            return true;
        }

        if (others >= 2)
        {
            reason = $"{others} others are still around";
            return false;
        }

        if (npc.health < 40f)
        {
            reason = "it's badly hurt with someone nearby";
            return false;
        }

        if (strongestOtherGear > myGear + 10)
        {
            reason = $"the one nearby is better geared ({strongestOtherGear} vs {myGear})";
            return false;
        }

        reason = null;
        return true;
    }

    private bool TryPursuePriorityKillLoot(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (!_priorityKillLoot.TryGetValue(characterId, out PriorityKillLoot entry))
        {
            return false;
        }

        if (Time.realtimeSinceStartup >= entry.ExpiresAt || entry.Steps >= PriorityKillLootMaxSteps * (entry.HighValue ? 2 : 1))
        {
            _priorityKillLoot.Remove(characterId);
            return false;
        }

        // Only loots right away once out of combat and if it looks safe. Unsafe
        // means someone better-geared is near the body, two or more others are
        // around, or the survivor is badly hurt with anyone nearby; in that case it
        // holds and re-assesses a few times before giving up.
        if (_activeCombat.ContainsKey(characterId))
        {
            // The want survives the fight: it is not allowed to run out while the survivor is busy, so the body is
            // still gone for as soon as the fight is over.
            entry.ExpiresAt = Mathf.Max(entry.ExpiresAt, Time.realtimeSinceStartup + 30f);
            return false;
        }

        if (!IsPriorityKillLootSafe(survivor, npc, entry, out string unsafeReason))
        {
            if (++entry.Waits > PriorityKillLootMaxWaits * (entry.HighValue ? 2 : 1))
            {
                Puts($"kill-loot: '{survivor.Character.Alias}' gave up waiting to loot the body ({unsafeReason}).");
                _priorityKillLoot.Remove(characterId);
                return false;
            }

            VerbosePuts($"kill-loot: '{survivor.Character.Alias}' is holding off looting the body - {unsafeReason} (wait {entry.Waits}/{PriorityKillLootMaxWaits}).");
            timer.Once(PriorityKillLootWaitSeconds, () =>
            {
                if (survivor.Player != null && !survivor.Player.IsDestroyed && survivor.Character.State != CharacterState.Dead
                    && !_activeCombat.ContainsKey(characterId))
                {
                    ContinueLootTask(survivor, state);
                }
            });

            return true;
        }

        entry.Steps++;

        // 0. A dead animal - not a LootableCorpse, the same entity, now harvestable in place.
        if (TryFindNearestDeadHuntableAnimal(entry.Position, PriorityKillLootSearchRadius, out BaseCombatEntity deadAnimal))
        {
            StartWalkingWithRecovery(
                survivor,
                GetApproachPoint(deadAnimal, npc, standoffDistance: MeleeEngagementRange),
                onArrived: () => StartHarvestingAnimalCorpse(survivor, deadAnimal, state),
                onFailed: () => ContinueLootTask(survivor, state));
            return true;
        }

        // 0b. A dead older-style animal (chicken, boar, stag...) - a harvestable BaseCorpse - hit with the
        // same melee pipeline: cloth, raw meat, leather, bone fragments and animal fat.
        if (HasAnimalHarvestTool(npc) && TryFindNearestAnimalBaseCorpse(entry.Position, PriorityKillLootSearchRadius, out BaseCorpse animalCorpse))
        {
            StartWalkingWithRecovery(
                survivor,
                GetApproachPoint(animalCorpse, npc),
                onArrived: () => StartHarvestingCorpse(survivor, animalCorpse, () =>
                {
                    _priorityKillLoot.Remove(characterId);
                    ContinueLootTask(survivor, state);
                }),
                onFailed: () => ContinueLootTask(survivor, state));
            return true;
        }

        // 1. The body itself.
        if (_engine.NavigationManager.TryFindNearestLootableCorpse(
                entry.Position,
                PriorityKillLootSearchRadius,
                out LootableCorpse corpse,
                candidate => candidate.containers != null && candidate.containers.Any(c => c != null && c.itemList.Count > 0)))
        {
            StartWalkingWithRecovery(
                survivor,
                corpse.transform.position,
                onArrived: () => LootCorpseAndContinue(survivor, corpse, state),
                onFailed: () => ContinueLootTask(survivor, state));
            return true;
        }

        // 2. A dropped bag it left behind.
        if (_engine.NavigationManager.TryFindNearestDroppedItemContainer(
                entry.Position,
                PriorityKillLootSearchRadius,
                out DroppedItemContainer bag,
                candidate => candidate.inventory != null && candidate.inventory.itemList.Count > 0))
        {
            StartWalkingWithRecovery(
                survivor,
                bag.transform.position,
                onArrived: () => LootDroppedItemContainerAndContinue(survivor, bag, state),
                onFailed: () => ContinueLootTask(survivor, state));
            return true;
        }

        // 3. Loose dropped items - the weapon it was holding lands here.
        if (_engine.NavigationManager.TryFindNearestDroppedItem(
                entry.Position,
                PriorityKillLootSearchRadius,
                out DroppedItem dropped,
                filter: candidate => candidate.item != null && !IsNeverLootItem(candidate.item.info.shortname)))
        {
            StartWalkingWithRecovery(
                survivor,
                dropped.transform.position,
                onArrived: () => PickupDroppedItemAndContinue(survivor, dropped, state),
                onFailed: () => ContinueLootTask(survivor, state));
            return true;
        }

        // Nothing left at the spot - done; make sure it's holding the best
        // weapon it now has.
        _priorityKillLoot.Remove(characterId);
        EquipBestWeaponForDisplay(survivor);
        return false;
    }

    private const int DeathSiteLootMaxSteps = 6;
    private const float DeathSiteLootSearchRadius = 15f;

    /// <summary>
    /// Sends a freshly-respawned survivor back to where it died to scavenge its own death bag
    /// and whatever else is lying around there (a killer's own drops, loose scraps) before
    /// resuming whatever it was doing. Self-expires via DeathSiteLootLifetimeSeconds/
    /// DeathSiteLootMaxSteps so an unreachable or already-looted spot doesn't block the survivor
    /// forever - it just falls through to normal task resumption once exhausted.
    /// </summary>
    private bool TryPursueDeathSiteLoot(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (!_pendingDeathSiteLoot.TryGetValue(characterId, out DeathSiteLoot entry))
        {
            return false;
        }

        // A death spot that has since been poisoned (died there the same way twice, a pile-up of
        // bodies, or inside a ghost-route-only monument) is never walked back to.
        if (UnityEngine.Time.realtimeSinceStartup >= entry.ExpiresAt || entry.Steps >= DeathSiteLootMaxSteps || IsInMonumentAvoidZone(entry.Position))
        {
            _pendingDeathSiteLoot.Remove(characterId);
            return false;
        }

        if (_activeCombat.ContainsKey(characterId))
        {
            return false;
        }

        entry.Steps++;

        // 1. The survivor's own death bag, or anyone else's dropped bag left at the same spot.
        if (_engine.NavigationManager.TryFindNearestDroppedItemContainer(
                entry.Position,
                DeathSiteLootSearchRadius,
                out DroppedItemContainer bag,
                candidate => candidate.inventory != null && candidate.inventory.itemList.Count > 0))
        {
            StartWalkingWithRecovery(
                survivor,
                bag.transform.position,
                onArrived: () => LootDroppedItemContainerAndContinue(survivor, bag, state),
                onFailed: () => ContinueLootTask(survivor, state));
            return true;
        }

        // 2. Loose dropped items - ammo, a dropped weapon, whatever else ended up on the ground.
        if (_engine.NavigationManager.TryFindNearestDroppedItem(
                entry.Position,
                DeathSiteLootSearchRadius,
                out DroppedItem dropped,
                filter: candidate => candidate.item != null && !IsNeverLootItem(candidate.item.info.shortname)))
        {
            StartWalkingWithRecovery(
                survivor,
                dropped.transform.position,
                onArrived: () => PickupDroppedItemAndContinue(survivor, dropped, state),
                onFailed: () => ContinueLootTask(survivor, state));
            return true;
        }

        // Nothing left at the spot - done.
        _pendingDeathSiteLoot.Remove(characterId);
        return false;
    }
}
