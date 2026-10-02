using System.Collections.Generic;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Stagger window for RestoreSpawnedSurvivors' post-reload task-pipeline resume, so a
    // large population doesn't all hit resource-gathering scans in the same frame.
    private const float RestoreResumeStaggerWindowSeconds = 15f;

    /// <summary>
    /// Captures every spawned survivor's live position, rotation, health, and inventory
    /// into its persistent Character record. Called from Unload before SaveManager writes
    /// to disk, so RestoreSpawnedSurvivors can bring survivors back on reload.
    /// </summary>
    private void CaptureAllLiveState()
    {
        if (_engine == null)
        {
            return;
        }

        int captured = 0;

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            BasePlayer npc = survivor.Player;
            Character character = survivor.Character;

            // Home-site/base-gather progress is captured regardless of whether npc is still
            // alive, since these in-memory-only dictionaries would otherwise be wiped on reload.
            character.HasRolledHomeSiteStrategy = _hasRolledHomeSiteStrategy.Contains(character.Id);
            character.PursuingBaseGatherGoal = _pursuingBaseGatherGoal.Contains(character.Id);
            character.HasRolledMonumentRush = _hasRolledMonumentRush.Contains(character.Id);
            character.PursuingMonumentRushGoal = _pursuingMonumentRushGoal.Contains(character.Id);
            character.MonumentRushDeadline = _monumentRushDeadline.TryGetValue(character.Id, out float monumentRushDeadline) ? monumentRushDeadline : 0f;
            character.TierUpgradeStruggleCount = _tierUpgradeStruggleCount.TryGetValue(character.Id, out int tierUpgradeStruggleCount) ? tierUpgradeStruggleCount : 0;

            if (_rolledBaseDesign.TryGetValue(character.Id, out (string Tier, string DesignPath, Dictionary<string, int> Cost) rolled))
            {
                character.RolledBaseTier = rolled.Tier;
                character.RolledBaseDesignPath = rolled.DesignPath;
                character.RolledBaseCost = rolled.Cost;
            }

            if (npc == null || npc.IsDestroyed)
            {
                character.Spawned = false;
                continue;
            }

            character.Position = npc.transform.position;
            character.Rotation = npc.transform.rotation;
            character.Health = npc.health;
            character.Inventory = CaptureInventory(npc.inventory);
            character.Spawned = true;

            captured++;
        }

        Puts($"Captured live state for {captured} spawned survivor(s).");
    }

    private List<SavedItem> CaptureInventory(PlayerInventory inventory)
    {
        var saved = new List<SavedItem>();

        CaptureContainer(inventory.containerMain, InventorySlot.Main, saved);
        CaptureContainer(inventory.containerBelt, InventorySlot.Belt, saved);
        CaptureContainer(inventory.containerWear, InventorySlot.Wear, saved);

        return saved;
    }

    private void CaptureContainer(ItemContainer container, InventorySlot slot, List<SavedItem> into)
    {
        if (container == null)
        {
            return;
        }

        foreach (Item item in container.itemList)
        {
            into.Add(new SavedItem
            {
                ItemId = item.info.itemid,
                Amount = item.amount,
                Condition = item.condition,
                SkinId = item.skin,
                Position = item.position,
                Container = slot,
            });
        }
    }

    /// <summary>
    /// Brings back every survivor with a live BasePlayer still in the world, plus every
    /// survivor that was spawned last time state was captured. Called after
    /// _engine.Start() loads the persistent roster.
    /// </summary>
    private void RestoreSpawnedSurvivors()
    {
        if (_engine == null)
        {
            return;
        }

        int relinked = 0;
        int respawned = 0;

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            Character character = survivor.Character;

            // Re-seeds home-site/base-gather progress unconditionally, regardless of
            // whether the survivor turns out to be spawned, dead, or freshly respawned.
            if (character.HasRolledHomeSiteStrategy)
            {
                _hasRolledHomeSiteStrategy.Add(character.Id);
            }

            if (character.PursuingBaseGatherGoal)
            {
                _pursuingBaseGatherGoal.Add(character.Id);
            }

            if (character.TierUpgradeStruggleCount > 0)
            {
                _tierUpgradeStruggleCount[character.Id] = character.TierUpgradeStruggleCount;
            }

            if (character.HasRolledMonumentRush)
            {
                _hasRolledMonumentRush.Add(character.Id);
            }

            if (character.PursuingMonumentRushGoal)
            {
                _pursuingMonumentRushGoal.Add(character.Id);
                _monumentRushDeadline[character.Id] = character.MonumentRushDeadline;
            }

            if (!string.IsNullOrEmpty(character.RolledBaseTier))
            {
                _rolledBaseDesign[character.Id] = (character.RolledBaseTier, character.RolledBaseDesignPath, character.RolledBaseCost ?? new Dictionary<string, int>());
            }

            // Checked before trusting the persisted Spawned flag, since a live entity is
            // authoritative over whatever got persisted.
            BasePlayer existing = FindExistingBotEntity(character.BotId);

            if (existing == null && !character.Spawned)
            {
                // A death's RespawnSurvivor timer never survives an Unload/restart, so a bot
                // killed just before a reload would otherwise stay Dead forever; this revives it.
                if (character.State == CharacterState.Dead)
                {
                    float respawnDelay = UnityEngine.Random.Range(1f, RestoreResumeStaggerWindowSeconds);
                    timer.Once(respawnDelay, () =>
                    {
                        if (survivor.Player == null || survivor.Player.IsDestroyed)
                        {
                            RespawnSurvivor(survivor);
                        }
                    });
                }

                continue;
            }

            if (existing != null)
            {
                // A live, non-destroyed entity is direct proof this character is alive now,
                // regardless of whatever State got persisted.
                character.State = CharacterState.Alive;

                // A restart's world-load calls StartSleeping() for every saved player entity.
                // Bots have no reconnect handshake to wake them up, so EndSleeping() is called explicitly.
                if (existing.IsSleeping())
                {
                    existing.EndSleeping();

                    // EndSleeping() only flips server-side state; the client's ragdoll visual
                    // needs a per-tick path bots don't run, so it's pushed explicitly too.
                    existing.SendNetworkUpdateImmediate();
                }

                survivor.Player = existing;
                survivor.Position = existing.transform.position;
                survivor.Spawned = true;
                character.Spawned = true;
                relinked++;

                // Re-linking Survivor.Player does not resume prior behaviour, since the task
                // pipeline runs on timers Unload destroys. This re-enters it, staggered
                // across RestoreResumeStaggerWindowSeconds.
                if (character.State == CharacterState.Alive)
                {
                    float restartDelay = UnityEngine.Random.Range(0f, RestoreResumeStaggerWindowSeconds);

                    timer.Once(restartDelay, () =>
                    {
                        if (survivor.Player != null && !survivor.Player.IsDestroyed && survivor.Character.State == CharacterState.Alive)
                        {
                            StartLootForResourcesTask(survivor);
                        }
                    });
                }

                continue;
            }

            BasePlayer npc = SpawnSurvivor(survivor, character.Position, character.Rotation, character.Health, character.Inventory);

            if (npc != null)
            {
                respawned++;
            }
        }

        Puts($"Restored {relinked} still-existing and {respawned} freshly respawned survivor(s) from the persistent roster.");
    }

    /// <summary>
    /// Looks for a live BasePlayer already in the world with this character's userID.
    /// Excludes vanilla NPCs, since Rust's NPC userID counter can occasionally collide
    /// numerically with this plugin's bot IDs.
    /// </summary>
    private BasePlayer FindExistingBotEntity(ulong botId)
    {
        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is BasePlayer player && player.userID == botId && !player.IsDestroyed && !player.IsNpc)
            {
                return player;
            }
        }

        return null;
    }

    /// <summary>
    /// Recreates each saved item and puts it back in the same container/slot it was
    /// captured from, falling back to any free slot, and dropping the item if neither works.
    /// </summary>
    private void RestoreInventory(BasePlayer npc, List<SavedItem> items)
    {
        foreach (SavedItem saved in items)
        {
            Item item = ItemManager.CreateByItemID(saved.ItemId, saved.Amount, saved.SkinId);

            if (item == null)
            {
                Puts($"WARNING: couldn't restore item ID {saved.ItemId} (x{saved.Amount}) for '{npc.displayName}' - unknown item ID?");
                continue;
            }

            item.condition = saved.Condition;

            ItemContainer targetContainer = saved.Container switch
            {
                InventorySlot.Belt => npc.inventory.containerBelt,
                InventorySlot.Wear => npc.inventory.containerWear,
                _ => npc.inventory.containerMain,
            };

            if (!item.MoveToContainer(targetContainer, saved.Position) && !npc.inventory.GiveItem(item))
            {
                Puts($"WARNING: couldn't restore item ID {saved.ItemId} (x{saved.Amount}) for '{npc.displayName}' - no room in inventory?");
                item.Remove();
            }
        }
    }
}
