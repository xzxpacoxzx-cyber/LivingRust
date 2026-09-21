using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Priority loot after a "fight up" kill (2026-09-21, Lucas's own explicit
    // ask): when a survivor kills a player or bot with a HIGHER gear score
    // than its own at the moment of the kill, that body - and whatever
    // weapon it dropped - become the survivor's top priority, above every
    // other task, especially when the survivor only had a bow and just
    // killed someone carrying a firearm. Set by OnEntityDeath
    // (LivingRust.Hooks.cs), consumed from ContinueLootTask's priority chain.
    private const float PriorityKillLootLifetimeSeconds = 120f;
    private const float PriorityKillLootSearchRadius = 15f;
    private const int PriorityKillLootMaxSteps = 10;

    private sealed class PriorityKillLoot
    {
        public Vector3 Position;
        public float ExpiresAt;
        public int Steps;
        public int Waits;
    }

    private readonly Dictionary<Guid, PriorityKillLoot> _priorityKillLoot = new();

    private void NotePotentialPriorityKill(Survivor killer, BaseCombatEntity victim)
    {
        if (victim is not BasePlayer victimPlayer || killer.Player == null)
        {
            return;
        }

        int victimGear = GetGearScore(victimPlayer);
        int killerGear = GetGearScore(killer.Player);

        if (victimGear <= killerGear)
        {
            return;
        }

        _priorityKillLoot[killer.Character.Id] = new PriorityKillLoot
        {
            Position = victim.transform.position,
            ExpiresAt = Time.realtimeSinceStartup + PriorityKillLootLifetimeSeconds,
        };

        Puts($"kill-loot: '{killer.Character.Alias}' (gear {killerGear}) just killed a higher-geared target (gear {victimGear}) - going straight for the body and whatever it dropped.");
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

        if (Time.realtimeSinceStartup >= entry.ExpiresAt || entry.Steps >= PriorityKillLootMaxSteps)
        {
            _priorityKillLoot.Remove(characterId);
            return false;
        }

        // Judgement call (2026-09-21, Lucas's own follow-up: only loot right
        // away once out of combat, and only if it looks safe - other bots or
        // players still around the body change the answer). Unsafe = someone
        // else is standing near the body who is clearly better geared, or
        // two or more others are around at all, or the survivor is badly
        // hurt with anyone nearby. Unsafe means hold and watch for a few
        // seconds (the normal on-sight rules deal with anyone who attacks),
        // then re-assess; after PriorityKillLootMaxWaits it gives up.
        if (_activeCombat.ContainsKey(characterId))
        {
            return false;
        }

        if (!IsPriorityKillLootSafe(survivor, npc, entry, out string unsafeReason))
        {
            if (++entry.Waits > PriorityKillLootMaxWaits)
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
}
