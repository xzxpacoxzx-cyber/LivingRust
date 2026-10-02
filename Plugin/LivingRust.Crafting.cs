using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Handles crafting goals: primitive starter items (tools, sleeping bag, bow, arrows, bandages)
/// and basic base-building materials. Uses Rust's own crafting pipeline (ItemCrafter) directly,
/// and drives the craft queue manually since disconnected NPCs never trigger its normal update path.
/// </summary>
public partial class LivingRust
{
    private const string SleepingBagShortname = "sleepingbag";
    private const string ArrowShortname = "arrow.wooden";
    private const string ClothShortname = "cloth";

    // Arrow crafting: up to 15 batches per craft, retriggers once the survivor's stock hits zero.
    private const int ArrowMaxBatches = 15;
    private const int ArrowRecraftThreshold = 0;

    // Bandage crafting: 4x cloth -> 1x bandage, up to 5 batches, retriggers once stock hits zero.
    private const int BandageMaxBatches = 5;
    private const int BandageRecraftThreshold = 0;

    // Sleeping bag: owned exactly once per life, crafted then immediately placed.
    private const int SleepingBagTargetBatches = 1;

    // Bow: crafted once per life (200x wood, 50x cloth), checked before arrows since arrows are
    // useless without one.
    private const string BowShortname = "bow.hunting";
    private const int BowTargetBatches = 1;

    // Prefab name substrings for the wood/stone surface-pile collectibles, checked first when an
    // ingredient is missing since they're a faster top-up than a full tree/ore node.
    private const string WoodCollectablePrefabSubstring = "wood-collectable";
    private const string StoneCollectablePrefabSubstring = "stone-collectable";

    // Search radius for both the collectible-pile search and the tree/ore fallback.
    private const float CraftIngredientSearchRadius = 100f;

    private readonly HashSet<Guid> _hasPlacedSleepingBag = new();
    private readonly Dictionary<Guid, Timer> _pendingSleepingBagDeployTimers = new();

    // Tracks whether a survivor has rolled its once-per-life primitive starter checklist, and
    // whether it is currently pursuing that checklist. This is a priority reordering within the
    // normal LootForResources task, not a separate task type.
    private readonly HashSet<Guid> _hasRolledPrimitiveGoal = new();
    private readonly HashSet<Guid> _pursuingPrimitiveGoals = new();

    // Tracks when a timed-out checklist attempt should be retried from a different spot.
    private readonly Dictionary<Guid, float> _primitiveGoalRetryTime = new();

    /// <summary>
    /// Called from StartLootForResourcesTask, the single entry point every "resume looting" call
    /// site funnels through. Once-per-life gating happens here via _hasRolledPrimitiveGoal, which
    /// clears on death so every new life gets the checklist again.
    /// </summary>
    // Gear score above which the primitive checklist is skipped, mainly relevant right after a
    // plugin reload since the roll-tracking sets are in-memory only.
    private const float SkipPrimitiveChecklistGearScoreThreshold = 30f;

    /// <summary>
    /// Checks whether a survivor still needs the from-scratch checklist: skipped if it already
    /// has a base, its gear score is high enough, or it already owns both a pickaxe-family and a
    /// hatchet-family tool. Requires BOTH families, not either one - the checklist's own job covers
    /// sleeping bag, bandages, and a bow/arrows too, not just gather tools, so owning only one tool
    /// (e.g. a looted pickaxe with no hatchet) should still run the checklist to pick up everything
    /// else, not skip the whole thing.
    /// </summary>
    private bool ShouldSkipPrimitiveChecklist(Survivor survivor, BasePlayer npc)
    {
        if (survivor.Character.Home != null)
        {
            return true;
        }

        if (GetGearScore(npc) >= SkipPrimitiveChecklistGearScoreThreshold)
        {
            return true;
        }

        return HasAnyToolOfFamily(npc, PickaxeFamily) && HasAnyToolOfFamily(npc, HatchetFamily);
    }

    private void RollPrimitiveGoalIfFreshLife(Survivor survivor)
    {
        Guid characterId = survivor.Character.Id;

        if (_hasRolledPrimitiveGoal.Contains(characterId))
        {
            return;
        }

        _hasRolledPrimitiveGoal.Add(characterId);

        BasePlayer npc = survivor.Player;

        if (npc != null && !npc.IsDestroyed && ShouldSkipPrimitiveChecklist(survivor, npc))
        {
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' already has a base/real gather tool/GearScore {SkipPrimitiveChecklistGearScoreThreshold:F0}+ - skipping the primitive starter checklist this life.");

            // A skipped checklist still counts as done, so it goes straight to base-gathering
            // if it doesn't already have one.
            if (survivor.Character.Home == null)
            {
                _pursuingBaseGatherGoal.Add(characterId);
            }

            return;
        }

        _pursuingPrimitiveGoals.Add(characterId);
        VerbosePuts($"craft-task: '{survivor.Character.Alias}' is prioritizing its primitive starter checklist (stone tools, sleeping bag, bandages, bow, arrows) this life.");
    }

    // 1s tick interval; ItemCrafter's completion check is wall-clock based so this stays precise
    // enough for a background NPC.
    private const float CraftQueueDriverIntervalSeconds = 1f;

    private Timer _craftQueueDriverTimer;

    /// <summary>
    /// Started from OnServerInitialized, stopped from Unload. Manually drives every survivor's
    /// ItemCrafter queue forward, since disconnected NPCs never trigger the normal update path.
    /// </summary>
    private void StartCraftQueueDriver()
    {
        _craftQueueDriverTimer?.Destroy();
        _craftQueueDriverTimer = timer.Every(CraftQueueDriverIntervalSeconds, () =>
        {
            foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
            {
                BasePlayer npc = survivor.Player;

                if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
                {
                    continue;
                }

                ItemCrafter crafter = npc.inventory?.crafting;

                if (crafter == null || crafter.queue.Count == 0)
                {
                    continue;
                }

                crafter.ServerUpdate(CraftQueueDriverIntervalSeconds);
            }
        });
    }

    private void StopCraftQueueDriver()
    {
        _craftQueueDriverTimer?.Destroy();
        _craftQueueDriverTimer = null;
    }

    /// <summary>
    /// Checks whether a survivor has finished its primitive checklist: sleeping bag placed, bow
    /// owned, and arrows/bandages/stone tools owned. Arrows and bandages only need to be above
    /// zero, since they are ongoing consumables rather than one-time goals.
    /// </summary>
    private bool HasCompletedPrimitiveGoals(Survivor survivor, BasePlayer npc)
    {
        if (!_hasPlacedSleepingBag.Contains(survivor.Character.Id))
        {
            return false;
        }

        ItemDefinition bowDef = ItemManager.FindItemDefinition(BowShortname);

        if (bowDef == null || npc.inventory.GetAmount(bowDef.itemid) < 1)
        {
            return false;
        }

        ItemDefinition arrowDef = ItemManager.FindItemDefinition(ArrowShortname);

        if (arrowDef == null || npc.inventory.GetAmount(arrowDef.itemid) <= 0)
        {
            return false;
        }

        // Same "owns more than zero" check as arrows: bandages are an ongoing consumable too.
        ItemDefinition bandageDef = ItemManager.FindItemDefinition(BandageShortname);

        if (bandageDef == null || npc.inventory.GetAmount(bandageDef.itemid) <= 0)
        {
            return false;
        }

        ItemDefinition hatchetDef = ItemManager.FindItemDefinition(StoneHatchetShortname);

        if (hatchetDef == null || npc.inventory.GetAmount(hatchetDef.itemid) < 1)
        {
            return false;
        }

        ItemDefinition pickaxeDef = ItemManager.FindItemDefinition(StonePickaxeShortname);

        if (pickaxeDef == null || npc.inventory.GetAmount(pickaxeDef.itemid) < 1)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checked from ContinueLootTask before falling back to resource gathering, since crafting is
    /// free when the survivor already holds enough material. Tries the sleeping bag, then bow,
    /// then arrows, in priority order.
    /// </summary>
    private bool TryStartCraftingFallback(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        // Only one real craft may be in flight at a time; if the queue is already busy, wait for
        // it rather than stacking a second craft on top.
        ItemCrafter activeCrafter = npc.inventory?.crafting;

        if (activeCrafter != null && activeCrafter.queue.Count > 0)
        {
            return false;
        }

        // Two-pass materials-driven checklist: pass 1 only crafts goals that are already
        // affordable with what's on hand (allowGather:false), so goals can complete in any order.
        // Pass 2 falls back to gathering the missing ingredient for the first goal in priority order.
        if (TryPursueOneOffToolGoal(survivor, npc, state, StoneHatchetShortname, "stone hatchet", allowGather: false)) return true;
        if (TryPursueOneOffToolGoal(survivor, npc, state, StonePickaxeShortname, "stone pickaxe", allowGather: false)) return true;
        if (TryPursueSleepingBagGoal(survivor, npc, state, allowGather: false)) return true;
        if (TryPursueBandageGoal(survivor, npc, state, allowGather: false)) return true;
        if (TryPursueBowGoal(survivor, npc, state, allowGather: false)) return true;
        if (TryPursueArrowGoal(survivor, npc, state, allowGather: false)) return true;

        if (TryPursueOneOffToolGoal(survivor, npc, state, StoneHatchetShortname, "stone hatchet")) return true;
        if (TryPursueOneOffToolGoal(survivor, npc, state, StonePickaxeShortname, "stone pickaxe")) return true;
        if (TryPursueSleepingBagGoal(survivor, npc, state)) return true;
        if (TryPursueBandageGoal(survivor, npc, state)) return true;
        if (TryPursueBowGoal(survivor, npc, state)) return true;
        if (TryPursueArrowGoal(survivor, npc, state)) return true;

        // Basic base-building materials, a lower-priority tier opportunistically pursued by every
        // survivor regardless of checklist status. metal.fragments has no active gathering source,
        // so these items only get crafted once enough fragments are passively looted.
        if (TryPursueOneOffToolGoal(survivor, npc, state, CodeLockShortname, "code lock"))
        {
            return true;
        }

        if (TryPursueOneOffToolGoal(survivor, npc, state, SheetMetalDoorShortname, "sheet metal door"))
        {
            return true;
        }

        if (TryPursueOneOffToolGoal(survivor, npc, state, ToolCupboardShortname, "tool cupboard"))
        {
            return true;
        }

        if (TryPursueOneOffToolGoal(survivor, npc, state, LargeWoodBoxShortname, "large wood box"))
        {
            return true;
        }

        if (TryPursueOneOffToolGoal(survivor, npc, state, Workbench1Shortname, "workbench"))
        {
            return true;
        }

        return false;
    }

    private const string StoneHatchetShortname = "stonehatchet";
    private const string StonePickaxeShortname = "stone.pickaxe";

    private const string WoodenDoorShortname = "door.hinged.wood";
    private const string CodeLockShortname = "lock.code";
    private const string SheetMetalDoorShortname = "door.hinged.metal";
    private const string ToolCupboardShortname = "cupboard.tool";
    private const string LargeWoodBoxShortname = "box.wooden.large";
    private const string Workbench1Shortname = "workbench1";

    private readonly Dictionary<(Guid CharacterId, string Shortname), Timer> _pendingOneOffCraftTimers = new();

    // Safety cap only, same reasoning as SleepingBagDeployWaitMaxTicks/
    // ArrowCraftWaitMaxTicks/BowCraftWaitMaxTicks.
    private const int OneOffCraftWaitMaxTicks = 60;

    /// <summary>
    /// Generic "own exactly one of this" goal: crafts it if affordable, gathers a missing
    /// ingredient if not, and waits for the queue to finish. Used by both stone tools.
    /// </summary>
    private bool TryPursueOneOffToolGoal(Survivor survivor, BasePlayer npc, LootTaskState state, string shortname, string logLabel, bool allowGather = true)
    {
        ItemDefinition def = ItemManager.FindItemDefinition(shortname);
        ItemBlueprint bp = def?.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bp == null || crafter == null)
        {
            return false;
        }

        if (npc.inventory.GetAmount(def.itemid) >= 1)
        {
            return false;
        }

        (Guid, string) key = (survivor.Character.Id, shortname);

        // Already mid-craft: doesn't claim this cycle, so the survivor keeps acting rather than
        // standing still while the craft finishes in the background.
        if (crafter.queue.Any(task => !task.cancelled && task.blueprint == bp))
        {
            if (!_pendingOneOffCraftTimers.ContainsKey(key))
            {
                WaitForOneOffCraftThenContinue(survivor, state, key);
            }

            return false;
        }

        if (crafter.CanCraft(bp, 1, free: false))
        {
            crafter.CraftItem(bp, npc, amount: 1);
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' is crafting a {logLabel} ({bp.time:F0}s) - continuing other tasks while it finishes.");
            WaitForOneOffCraftThenContinue(survivor, state, key);
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return true;
        }

        ItemAmount missing = bp.GetIngredients()
            .FirstOrDefault(ingredient => npc.inventory.GetAmount(ingredient.itemid) < (int)ingredient.amount);

        if (missing?.itemDef == null)
        {
            return false;
        }

        return allowGather && TryGatherCraftIngredient(survivor, npc, state, missing.itemDef.shortname);
    }

    private void WaitForOneOffCraftThenContinue(Survivor survivor, LootTaskState state, (Guid CharacterId, string Shortname) key)
    {
        if (_pendingOneOffCraftTimers.TryGetValue(key, out Timer existing))
        {
            existing.Destroy();
        }

        int ticks = 0;
        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                _pendingOneOffCraftTimers.Remove(key);
                return;
            }

            ItemCrafter crafter = npc.inventory.crafting;

            if (crafter == null || crafter.queue.Count == 0)
            {
                pollTimer.Destroy();
                _pendingOneOffCraftTimers.Remove(key);

                // Drops the rock now that a stone tool exists, or otherwise reorganizes gear after
                // a craft lands. Harmless no-op for the non-tool one-off goals sharing this function.
                RunLootHookSafely(survivor, nameof(PerformReorganizationCheck), () => PerformReorganizationCheck(survivor, npc));

                ContinueLootTask(survivor, state, forceLocalScan: true);
                return;
            }

            if (++ticks >= OneOffCraftWaitMaxTicks)
            {
                pollTimer.Destroy();
                _pendingOneOffCraftTimers.Remove(key);
                Puts($"craft-task: '{survivor.Character.Alias}' gave up waiting for its {key.Shortname} craft to finish after {OneOffCraftWaitMaxTicks}s - resuming normally.");
                ContinueLootTask(survivor, state, forceLocalScan: true);
            }
        });

        _pendingOneOffCraftTimers[key] = pollTimer;
    }

    private bool TryPursueSleepingBagGoal(Survivor survivor, BasePlayer npc, LootTaskState state, bool allowGather = true)
    {
        Guid characterId = survivor.Character.Id;

        if (_hasPlacedSleepingBag.Contains(characterId))
        {
            return false;
        }

        ItemDefinition bagDef = ItemManager.FindItemDefinition(SleepingBagShortname);
        ItemBlueprint bp = bagDef?.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bp == null || crafter == null)
        {
            return false;
        }

        // Already holding a finished bag (crafted but not yet placed, e.g. CanBuild failed last
        // try) - just make sure a deploy watcher is pending for it, without claiming this cycle.
        if (npc.inventory.GetAmount(bagDef.itemid) > 0)
        {
            if (!_pendingSleepingBagDeployTimers.ContainsKey(characterId))
            {
                WaitForSleepingBagThenDeploy(survivor, bagDef, state);
            }

            return false;
        }

        // Already mid-craft: just wait for it rather than issuing a second craft.
        if (crafter.queue.Any(task => !task.cancelled && task.blueprint == bp))
        {
            if (!_pendingSleepingBagDeployTimers.ContainsKey(characterId))
            {
                WaitForSleepingBagThenDeploy(survivor, bagDef, state);
            }

            return false;
        }

        if (crafter.CanCraft(bp, SleepingBagTargetBatches, free: false))
        {
            crafter.CraftItem(bp, npc, amount: SleepingBagTargetBatches);
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' is crafting a sleeping bag ({bp.time:F0}s) - continuing other tasks while it finishes.");
            WaitForSleepingBagThenDeploy(survivor, bagDef, state);
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return true;
        }

        ItemAmount missing = bp.GetIngredients()
            .FirstOrDefault(ingredient => npc.inventory.GetAmount(ingredient.itemid) < (int)ingredient.amount);

        if (missing?.itemDef == null)
        {
            return false;
        }

        return allowGather && TryGatherCraftIngredient(survivor, npc, state, missing.itemDef.shortname);
    }

    // Safety cap so an orphaned poll doesn't run forever if the bag item drops or despawns mid-wait.
    private const int SleepingBagDeployWaitMaxTicks = 45;

    private void WaitForSleepingBagThenDeploy(Survivor survivor, ItemDefinition bagDef, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (_pendingSleepingBagDeployTimers.TryGetValue(characterId, out Timer existing))
        {
            existing.Destroy();
        }

        int ticks = 0;
        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                _pendingSleepingBagDeployTimers.Remove(characterId);
                return;
            }

            if (npc.inventory.GetAmount(bagDef.itemid) > 0)
            {
                pollTimer.Destroy();
                _pendingSleepingBagDeployTimers.Remove(characterId);
                DeploySleepingBagAndAssign(survivor, bagDef, state);
                return;
            }

            if (++ticks >= SleepingBagDeployWaitMaxTicks)
            {
                pollTimer.Destroy();
                _pendingSleepingBagDeployTimers.Remove(characterId);

                // Logs loudly rather than failing silently, so a stuck wait is easy to notice.
                Puts($"craft-task: '{survivor.Character.Alias}' gave up waiting for its sleeping bag craft to finish after {SleepingBagDeployWaitMaxTicks}s - resuming normally.");
                ContinueLootTask(survivor, state, forceLocalScan: true);
            }
        });

        _pendingSleepingBagDeployTimers[characterId] = pollTimer;
    }

    /// <summary>
    /// Places a sleeping bag at the survivor's current ground-snapped position, mirroring the
    /// game's deploy logic server-side. Placement also assigns it as the survivor's respawn point.
    /// </summary>
    private void DeploySleepingBagAndAssign(Survivor survivor, ItemDefinition bagDef, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        // Every return path below resumes the survivor's normal loop, since this call was the
        // last thing keeping it busy and it would otherwise sit idle on any failure/retry path.
        Item bagItem = npc.inventory.FindItemByItemID(bagDef.itemid);

        if (bagItem == null)
        {
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return;
        }

        ItemModDeployable modDeployable = bagDef.GetComponent<ItemModDeployable>();

        if (modDeployable == null)
        {
            Puts($"craft-task: '{survivor.Character.Alias}' has a sleeping bag but it has no ItemModDeployable - can't place it.");
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return;
        }

        if (!npc.CanBuild())
        {
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' can't place a sleeping bag here (no building permission) - will retry once it moves on.");
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return;
        }

        if (!_engine.NavigationManager.TryGetGroundHeight(npc.transform.position, out float groundHeight))
        {
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return;
        }

        Vector3 position = npc.transform.position;
        position.y = groundHeight;

        // Forward is the surface normal (flat ground = Vector3.up); placeDir hints the facing.
        Quaternion rotation = Quaternion.LookRotation(Vector3.up, npc.eyes.BodyForward()) * Quaternion.Euler(90f, 0f, 0f);

        BaseEntity bagEntity = GameManager.server.CreateEntity(modDeployable.entityPrefab.resourcePath, position, rotation);

        if (bagEntity == null)
        {
            Puts($"craft-task: '{survivor.Character.Alias}' failed to create a sleeping bag entity ('{modDeployable.entityPrefab.resourcePath}').");
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return;
        }

        bagEntity.skinID = bagItem.skin;
        bagEntity.SendMessage("SetDeployedBy", npc, SendMessageOptions.DontRequireReceiver);
        bagEntity.OwnerID = npc.userID;
        bagEntity.Spawn();

        bagItem.UseItem(1);

        _hasPlacedSleepingBag.Add(survivor.Character.Id);

        Puts($"craft-task: '{survivor.Character.Alias}' placed a sleeping bag - will respawn there on death.");
        ContinueLootTask(survivor, state, forceLocalScan: true);
    }

    private readonly Dictionary<Guid, Timer> _pendingBowCraftTimers = new();

    private bool TryPursueBowGoal(Survivor survivor, BasePlayer npc, LootTaskState state, bool allowGather = true)
    {
        Guid characterId = survivor.Character.Id;
        ItemDefinition bowDef = ItemManager.FindItemDefinition(BowShortname);
        ItemBlueprint bp = bowDef?.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bp == null || crafter == null)
        {
            return false;
        }

        if (npc.inventory.GetAmount(bowDef.itemid) >= BowTargetBatches)
        {
            return false;
        }

        // Already mid-craft: doesn't claim this cycle so the survivor keeps acting while it finishes.
        if (crafter.queue.Any(task => !task.cancelled && task.blueprint == bp))
        {
            if (!_pendingBowCraftTimers.ContainsKey(characterId))
            {
                WaitForBowCraftThenContinue(survivor, state);
            }

            return false;
        }

        if (crafter.CanCraft(bp, BowTargetBatches, free: false))
        {
            crafter.CraftItem(bp, npc, amount: BowTargetBatches);
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' is crafting a bow.hunting ({bp.time:F0}s) - continuing other tasks while it finishes.");
            WaitForBowCraftThenContinue(survivor, state);
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return true;
        }

        ItemAmount missing = bp.GetIngredients()
            .FirstOrDefault(ingredient => npc.inventory.GetAmount(ingredient.itemid) < (int)ingredient.amount);

        if (missing?.itemDef == null)
        {
            return false;
        }

        return allowGather && TryGatherCraftIngredient(survivor, npc, state, missing.itemDef.shortname);
    }

    // Safety cap only, generous rather than exact.
    private const int BowCraftWaitMaxTicks = 60;

    private void WaitForBowCraftThenContinue(Survivor survivor, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (_pendingBowCraftTimers.TryGetValue(characterId, out Timer existing))
        {
            existing.Destroy();
        }

        int ticks = 0;
        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                _pendingBowCraftTimers.Remove(characterId);
                return;
            }

            ItemCrafter crafter = npc.inventory.crafting;

            if (crafter == null || crafter.queue.Count == 0)
            {
                pollTimer.Destroy();
                _pendingBowCraftTimers.Remove(characterId);
                ContinueLootTask(survivor, state, forceLocalScan: true);
                return;
            }

            if (++ticks >= BowCraftWaitMaxTicks)
            {
                pollTimer.Destroy();
                _pendingBowCraftTimers.Remove(characterId);
                Puts($"craft-task: '{survivor.Character.Alias}' gave up waiting for its bow craft to finish after {BowCraftWaitMaxTicks}s - resuming normally.");
                ContinueLootTask(survivor, state, forceLocalScan: true);
            }
        });

        _pendingBowCraftTimers[characterId] = pollTimer;
    }

    private readonly Dictionary<Guid, Timer> _pendingArrowCraftTimers = new();

    private bool TryPursueArrowGoal(Survivor survivor, BasePlayer npc, LootTaskState state, bool allowGather = true, int recraftThreshold = ArrowRecraftThreshold)
    {
        Guid characterId = survivor.Character.Id;
        ItemDefinition arrowDef = ItemManager.FindItemDefinition(ArrowShortname);
        ItemBlueprint bp = arrowDef?.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bp == null || crafter == null)
        {
            return false;
        }

        if (npc.inventory.GetAmount(arrowDef.itemid) > recraftThreshold)
        {
            return false;
        }

        // Already mid-craft: let it finish rather than stacking a second run, and make sure a
        // resume waiter is running so the finished arrows don't just sit unused in inventory.
        if (crafter.queue.Any(task => !task.cancelled && task.blueprint == bp))
        {
            if (!_pendingArrowCraftTimers.ContainsKey(characterId))
            {
                WaitForArrowCraftThenContinue(survivor, state);
            }

            return false;
        }

        int arrowBatches = GetAffordableBatches(crafter, bp, ArrowMaxBatches);

        if (arrowBatches >= 1)
        {
            crafter.CraftItem(bp, npc, amount: arrowBatches);
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' is crafting {arrowBatches}x arrow.wooden batches ({bp.time * arrowBatches:F0}s) - continuing other tasks while it finishes.");
            WaitForArrowCraftThenContinue(survivor, state);
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return true;
        }

        ItemAmount missing = bp.GetIngredients()
            .Where(ingredient => npc.inventory.GetAmount(ingredient.itemid) < (int)ingredient.amount * ArrowMaxBatches)
            .OrderBy(ingredient => (float)npc.inventory.GetAmount(ingredient.itemid) / ((int)ingredient.amount * ArrowMaxBatches))
            .FirstOrDefault();

        if (missing?.itemDef == null)
        {
            return false;
        }

        return allowGather && TryGatherCraftIngredient(survivor, npc, state, missing.itemDef.shortname);
    }

    // Safety cap covering the max craft time plus a generous buffer.
    private const int ArrowCraftWaitMaxTicks = 70;

    private void WaitForArrowCraftThenContinue(Survivor survivor, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (_pendingArrowCraftTimers.TryGetValue(characterId, out Timer existing))
        {
            existing.Destroy();
        }

        int ticks = 0;
        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                _pendingArrowCraftTimers.Remove(characterId);
                return;
            }

            ItemCrafter crafter = npc.inventory.crafting;

            if (crafter == null || crafter.queue.Count == 0)
            {
                pollTimer.Destroy();
                _pendingArrowCraftTimers.Remove(characterId);
                ContinueLootTask(survivor, state, forceLocalScan: true);
                return;
            }

            if (++ticks >= ArrowCraftWaitMaxTicks)
            {
                pollTimer.Destroy();
                _pendingArrowCraftTimers.Remove(characterId);
                Puts($"craft-task: '{survivor.Character.Alias}' gave up waiting for its arrow craft to finish after {ArrowCraftWaitMaxTicks}s - resuming normally.");
                ContinueLootTask(survivor, state, forceLocalScan: true);
            }
        });

        _pendingArrowCraftTimers[characterId] = pollTimer;
    }

    // Largest batch count (up to maxBatches) the survivor can afford right now, so a partial
    // stock of ingredients still crafts something rather than nothing. 0 = can't afford even one.
    private static int GetAffordableBatches(ItemCrafter crafter, ItemBlueprint bp, int maxBatches)
    {
        for (int batches = maxBatches; batches >= 1; batches--)
        {
            if (crafter.CanCraft(bp, batches, free: false))
            {
                return batches;
            }
        }

        return 0;
    }

    private readonly Dictionary<Guid, Timer> _pendingBandageCraftTimers = new();

    // Health threshold below which a survivor with no heal item pursues a bandage restock.
    private const float HurtBandageSupplyHealthThreshold = 80f;

    /// <summary>
    /// Restocks bandages for a survivor that is hurt and has nothing left to heal with, reachable
    /// from ContinueLootTask's universal priority chain regardless of task phase.
    /// </summary>
    // Keeps a bow/arrow/bandage survival kit topped up for primitive-level survivors that don't
    // already carry a firearm. Claims the cycle until the kit is complete.
    private const int SurvivalKitMinBandages = 3;
    private const int SurvivalKitMinArrows = 10;

    private bool TryPursueSurvivalKitUpkeep(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        if (_pursuingPrimitiveGoals.Contains(survivor.Character.Id))
        {
            return false;
        }

        // Counts every heal item the survivor would actually use, not just bandages, so a
        // survivor already carrying syringes doesn't go crafting bandages too.
        int healSupply = 0;

        foreach (string healShortname in CombatHealItemPriority)
        {
            ItemDefinition healDef = ItemManager.FindItemDefinition(healShortname);

            if (healDef != null)
            {
                healSupply += npc.inventory.GetAmount(healDef.itemid);
            }
        }

        ItemDefinition bandageDef = ItemManager.FindItemDefinition(BandageShortname);

        if (bandageDef != null
            && healSupply < SurvivalKitMinBandages
            && TryPursueBandageGoal(survivor, npc, state, allowGather: true, recraftThreshold: SurvivalKitMinBandages - 1))
        {
            return true;
        }

        if (HasReadyFirearm(npc))
        {
            return false;
        }

        if (!HasBowFamilyWeapon(npc))
        {
            return TryPursueBowGoal(survivor, npc, state);
        }

        ItemDefinition arrowDef = ItemManager.FindItemDefinition(ArrowShortname);

        return arrowDef != null
            && npc.inventory.GetAmount(arrowDef.itemid) < SurvivalKitMinArrows
            && TryPursueArrowGoal(survivor, npc, state, allowGather: true, recraftThreshold: SurvivalKitMinArrows - 1);
    }

    private bool TryPursueBandageSupplyIfHurt(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        if (npc.health >= HurtBandageSupplyHealthThreshold)
        {
            return false;
        }

        if (FindBestHealItem(npc) != null)
        {
            return false;
        }

        return TryPursueBandageGoal(survivor, npc, state);
    }

    /// <summary>
    /// Bandage crafting goal, same recraft-when-empty batch shape as TryPursueArrowGoal since
    /// bandages are consumed the same ongoing way.
    /// </summary>
    private bool TryPursueBandageGoal(Survivor survivor, BasePlayer npc, LootTaskState state, bool allowGather = true, int recraftThreshold = BandageRecraftThreshold)
    {
        Guid characterId = survivor.Character.Id;
        ItemDefinition bandageDef = ItemManager.FindItemDefinition(BandageShortname);
        ItemBlueprint bp = bandageDef?.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bp == null || crafter == null)
        {
            return false;
        }

        if (npc.inventory.GetAmount(bandageDef.itemid) > recraftThreshold)
        {
            return false;
        }

        if (crafter.queue.Any(task => !task.cancelled && task.blueprint == bp))
        {
            if (!_pendingBandageCraftTimers.ContainsKey(characterId))
            {
                WaitForBandageCraftThenContinue(survivor, state);
            }

            return false;
        }

        int bandageBatches = GetAffordableBatches(crafter, bp, BandageMaxBatches);

        if (bandageBatches >= 1)
        {
            crafter.CraftItem(bp, npc, amount: bandageBatches);
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' is crafting {bandageBatches}x bandage batches ({bp.time * bandageBatches:F0}s) - continuing other tasks while it finishes.");
            WaitForBandageCraftThenContinue(survivor, state);
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return true;
        }

        ItemAmount missing = bp.GetIngredients()
            .Where(ingredient => npc.inventory.GetAmount(ingredient.itemid) < (int)ingredient.amount * BandageMaxBatches)
            .OrderBy(ingredient => (float)npc.inventory.GetAmount(ingredient.itemid) / ((int)ingredient.amount * BandageMaxBatches))
            .FirstOrDefault();

        if (missing?.itemDef == null)
        {
            return false;
        }

        return allowGather && TryGatherCraftIngredient(survivor, npc, state, missing.itemDef.shortname);
    }

    // Safety cap covering the max craft time plus a generous buffer.
    private const int BandageCraftWaitMaxTicks = 45;

    private void WaitForBandageCraftThenContinue(Survivor survivor, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (_pendingBandageCraftTimers.TryGetValue(characterId, out Timer existing))
        {
            existing.Destroy();
        }

        int ticks = 0;
        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                _pendingBandageCraftTimers.Remove(characterId);
                return;
            }

            ItemCrafter crafter = npc.inventory.crafting;

            if (crafter == null || crafter.queue.Count == 0)
            {
                pollTimer.Destroy();
                _pendingBandageCraftTimers.Remove(characterId);
                ContinueLootTask(survivor, state, forceLocalScan: true);
                return;
            }

            if (++ticks >= BandageCraftWaitMaxTicks)
            {
                pollTimer.Destroy();
                _pendingBandageCraftTimers.Remove(characterId);
                Puts($"craft-task: '{survivor.Character.Alias}' gave up waiting for its bandage craft to finish after {BandageCraftWaitMaxTicks}s - resuming normally.");
                ContinueLootTask(survivor, state, forceLocalScan: true);
            }
        });

        _pendingBandageCraftTimers[characterId] = pollTimer;
    }

    /// <summary>
    /// Dispatches a gather trip for one missing recipe ingredient. Wood/stone check a nearby
    /// surface-pile collectible first, falling back to normal tree/ore gathering. Cloth uses a
    /// hemp-collectible detour. Bypasses the sticky tree-vs-ore gather-type lock since a specific
    /// recipe's ingredient need is a deliberate reason to switch resource type.
    /// </summary>
    // Once a search for a specific ingredient finds nothing, that (survivor, ingredient) pair is
    // cooled down so other goals needing it fail fast instead of repeating the same search, and
    // the checklist can move on to a goal that doesn't need the blocked ingredient.
    private const float IngredientSearchCooldownSeconds = 120f;

    private readonly Dictionary<(Guid CharacterId, string Ingredient), float> _ingredientSearchCooldownUntil = new();

    private bool TryGatherCraftIngredient(Survivor survivor, BasePlayer npc, LootTaskState state, string ingredientShortname)
    {
        (Guid, string) cooldownKey = (survivor.Character.Id, ingredientShortname);

        if (_ingredientSearchCooldownUntil.TryGetValue(cooldownKey, out float cooldownUntil) && UnityEngine.Time.realtimeSinceStartup < cooldownUntil)
        {
            return false;
        }

        bool found = ingredientShortname switch
        {
            // Caps committing to a full tree once the survivor already has enough wood for the
            // checklist's modest needs; collectible pickup stays uncapped since it's much lighter.
            WoodShortname => TryGatherViaCollectible(survivor, npc, state, WoodCollectablePrefabSubstring, "wood")
                || (!HasEnoughWoodAlready(survivor, npc) && TryGatherViaTree(survivor, npc, state)),
            StoneShortname => TryGatherViaCollectible(survivor, npc, state, StoneCollectablePrefabSubstring, "stone")
                || TryGatherViaOre(survivor, npc, state),
            "metal.fragments" => TryGatherViaOre(survivor, npc, state, preferredYieldShortname: "metal.ore"),
            ClothShortname => TryGatherViaCollectible(survivor, npc, state, HempCollectablePrefabSubstring, "cloth")
                || TryPursueAnimalHuntForCloth(survivor, npc, state),

            // Mines the matching ore node for metal/sulfur ore.
            "metal.ore" => TryGatherViaOre(survivor, npc, state, preferredYieldShortname: "metal.ore"),
            "sulfur.ore" => TryGatherViaOre(survivor, npc, state, preferredYieldShortname: "sulfur.ore"),
            // Refined sulfur is a furnace output, not minable directly - redirects to raw ore,
            // which the existing furnace cycle then smelts.
            "sulfur" => TryGatherViaOre(survivor, npc, state, preferredYieldShortname: "sulfur.ore"),

            _ => false,
        };

        // Logs and cools down when no source is found nearby, so this case is visible and isn't
        // confused with "goal complete."
        if (!found)
        {
            _ingredientSearchCooldownUntil[cooldownKey] = UnityEngine.Time.realtimeSinceStartup + IngredientSearchCooldownSeconds;
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' needs more {ingredientShortname} to craft but found no source within {CraftIngredientSearchRadius:F0}m - skipping ahead to other goals for {IngredientSearchCooldownSeconds:F0}s.");
        }
        else
        {
            _ingredientSearchCooldownUntil.Remove(cooldownKey);
        }

        return found;
    }

    private const string HempCollectablePrefabSubstring = "hemp-collectable";

    private bool TryGatherViaCollectible(Survivor survivor, BasePlayer npc, LootTaskState state, string prefabSubstring, string logLabel)
    {
        if (!_engine.NavigationManager.TryFindNearestCollectible(
                npc.transform.position,
                CraftIngredientSearchRadius,
                out CollectibleEntity collectible,
                candidate => !state.Visited.Contains(candidate.net.ID)
                    && !IsLootTargetClaimed(candidate.net.ID)
                    && !IsInPoisonedZone(candidate.transform.position, state)
                    && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                    && !IsInMonumentAvoidZone(candidate.transform.position)
                    && !IsBelowSafeLootDepth(candidate.transform.position)
                    && candidate.itemList != null
                    && candidate.itemList.Length > 0
                    && IsResourceAllowedForHighGear(survivor, npc, candidate.itemList)
                    && candidate.ShortPrefabName.IndexOf(prefabSubstring, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return false;
        }

        state.Visited.Add(collectible.net.ID);
        ClaimLootTarget(state, collectible.net.ID);
        VerbosePuts($"craft-task: '{survivor.Character.Alias}' needs more {logLabel} to craft - heading to a nearby {logLabel} pile.");
        GatherCraftCollectibleAndContinue(survivor, collectible, state);
        return true;
    }

    private bool TryGatherViaTree(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        if (!HasAnyGatherCapableTool(npc, TreeGatherToolPriority))
        {
            return false;
        }

        if (!_engine.NavigationManager.TryFindNearestTreeEntity(
                npc.transform.position,
                CraftIngredientSearchRadius,
                out TreeEntity tree,
                candidate => !state.Visited.Contains(candidate.net.ID)
                    && !IsLootTargetClaimed(candidate.net.ID)
                    && !IsInPoisonedZone(candidate.transform.position, state)
                    && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                    && !IsInMonumentAvoidZone(candidate.transform.position)
                    && !IsBelowSafeLootDepth(candidate.transform.position)
                    && !IsResourceNodePoisoned(candidate)))
        {
            return false;
        }

        state.Visited.Add(tree.net.ID);
        ClaimLootTarget(state, tree.net.ID);
        VerbosePuts($"craft-task: '{survivor.Character.Alias}' needs more wood to craft - heading to a nearby tree.");
        GatherTreeAndContinue(survivor, tree, state);
        return true;
    }

    private bool TryGatherViaOre(Survivor survivor, BasePlayer npc, LootTaskState state, string preferredYieldShortname = null)
    {
        if (!HasAnyGatherCapableTool(npc, OreGatherToolPriority))
        {
            return false;
        }

        // Tries a node that actually yields the preferred ore type first, falling back to any
        // ore node if nothing matching is nearby.
        if (preferredYieldShortname != null
            && _engine.NavigationManager.TryFindNearestOreResourceEntity(
                npc.transform.position,
                CraftIngredientSearchRadius,
                out OreResourceEntity preferredOre,
                candidate => !state.Visited.Contains(candidate.net.ID)
                    && !IsLootTargetClaimed(candidate.net.ID)
                    && !IsInPoisonedZone(candidate.transform.position, state)
                    && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                    && !IsInMonumentAvoidZone(candidate.transform.position)
                    && !IsBelowSafeLootDepth(candidate.transform.position)
                    && !IsResourceNodePoisoned(candidate)
                    && GetNodeYields(candidate)?.Any(y => y?.itemDef?.shortname == preferredYieldShortname) == true))
        {
            state.Visited.Add(preferredOre.net.ID);
            ClaimLootTarget(state, preferredOre.net.ID);
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' needs {preferredYieldShortname} - heading to a matching ore node.");
            GatherOreAndContinue(survivor, preferredOre, state);
            return true;
        }

        // A specific preferred type (metal.fragments/sulfur callers above) that couldn't be found
        // nearby never falls back to an unrelated ore type - substituting sulfur for a metal.ore
        // request (or vice versa) would silently ignore the early-game ore restriction and gather
        // something the survivor has no actual use for. Only the type-agnostic "need more stone"
        // caller (no preferredYieldShortname) reaches this generic any-node search.
        if (preferredYieldShortname != null)
        {
            return false;
        }

        if (!_engine.NavigationManager.TryFindNearestOreResourceEntity(
                npc.transform.position,
                CraftIngredientSearchRadius,
                out OreResourceEntity ore,
                candidate => !state.Visited.Contains(candidate.net.ID)
                    && !IsLootTargetClaimed(candidate.net.ID)
                    && !IsInPoisonedZone(candidate.transform.position, state)
                    && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                    && !IsInMonumentAvoidZone(candidate.transform.position)
                    && !IsBelowSafeLootDepth(candidate.transform.position)
                    && !IsResourceNodePoisoned(candidate)))
        {
            return false;
        }

        state.Visited.Add(ore.net.ID);
        ClaimLootTarget(state, ore.net.ID);
        VerbosePuts($"craft-task: '{survivor.Character.Alias}' needs more stone to craft - heading to a nearby ore node.");
        GatherOreAndContinue(survivor, ore, state);
        return true;
    }

    /// <summary>
    /// Walks to and picks up a collectible pile (hemp/wood/stone), same walk-then-gather shape
    /// as the tree/ore gather wrappers.
    /// </summary>
    private void GatherCraftCollectibleAndContinue(Survivor survivor, CollectibleEntity collectible, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        Vector3 approachPoint = GetApproachPoint(collectible, npc);

        StartWalkingWithRecovery(
            survivor,
            approachPoint,
            onArrived: () => PickupCollectibleAndContinue(survivor, collectible, state, onDone: () => ContinueLootTask(survivor, state, forceLocalScan: true)),
            onFailed: () =>
            {
                BasePlayer laterNpc = survivor.Player;

                if (laterNpc == null || laterNpc.IsDestroyed)
                {
                    return;
                }

                ContinueLootTask(survivor, state, forceLocalScan: true);
            });
    }
}
