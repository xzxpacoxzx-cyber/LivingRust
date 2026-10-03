using LivingRust.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingRust.Models;
using LivingRust.Navigation;
using Oxide.Plugins;
using Rust;
using Rust.Ai.Gen2;
using UnityEngine;
using UnityEngine.AI;

namespace Carbon.Plugins;

public partial class LivingRust
{
    /// <summary>
    /// Radius around the survivor's current position used to search for the next loot container.
    /// </summary>
    private const float LootSearchRadius = 50f;

    /// <summary>
    /// Search radius for ambient corpse/dropped-bag discovery. Kept smaller than LootSearchRadius for realism.
    /// </summary>
    private const float CorpseAmbientAwarenessRadius = BotOnSightDetectionRange;

    /// <summary>
    /// How long a survivor's own kill stays remembered before it is treated as unknown again.
    /// </summary>
    private const float OwnKillMemoryDurationSeconds = 300f;

    /// <summary>
    /// Distance tolerance used to match a corpse's position to a remembered kill position.
    /// </summary>
    private const float OwnKillMemoryMatchRadius = 5f;

    /// <summary>
    /// Per-survivor list of recent kill positions and their expiry times, used by IsRememberedOwnKill.
    /// </summary>
    // Caps how many recent kills are remembered per survivor.
    private const int OwnKillMemoryMaxEntries = 8;
    private readonly Dictionary<Guid, List<(Vector3 Position, float ExpiresAt)>> _recentOwnKillPositions = new();

    private void RememberOwnKill(Guid characterId, Vector3 position)
    {
        if (!_recentOwnKillPositions.TryGetValue(characterId, out List<(Vector3 Position, float ExpiresAt)> kills))
        {
            kills = new List<(Vector3, float)>();
            _recentOwnKillPositions[characterId] = kills;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;
        kills.RemoveAll(k => k.ExpiresAt <= now);
        kills.Add((position, now + OwnKillMemoryDurationSeconds));

        if (kills.Count > OwnKillMemoryMaxEntries)
        {
            kills.RemoveAt(0);
        }
    }

    /// <summary>
    /// Checks whether a corpse position matches a remembered own kill for this survivor. Expired entries are pruned lazily.
    /// </summary>
    private bool IsRememberedOwnKill(Guid characterId, Vector3 corpsePosition)
    {
        if (!_recentOwnKillPositions.TryGetValue(characterId, out List<(Vector3 Position, float ExpiresAt)> kills))
        {
            return false;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;

        foreach ((Vector3 position, float expiresAt) in kills)
        {
            if (expiresAt > now && Vector3.Distance(corpsePosition, position) <= OwnKillMemoryMatchRadius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Outer search ceiling for collectible detection; per-type cutoffs are enforced separately in the search filter.
    /// </summary>
    private const float CollectibleSearchRadius = 15f;

    /// <summary>
    /// Any loot within this distance takes priority over normal type-tier ordering, regardless of type.
    /// </summary>
    private const float NearbyLootPriorityRadius = 30f;

    /// <summary>
    /// Poll interval used to check whether the equipped tool's attack cooldown has cleared.
    /// </summary>
    private const float AttackHitInterval = 0.1f;

    /// <summary>
    /// Fallback damage per hit when no melee tool is equipped. Normal damage comes from GetToolDamage instead.
    /// </summary>
    private const float AttackDamagePerHit = 6f;

    /// <summary>
    /// Delay before a direct loot (crates/boxes) completes, instead of transferring instantly on arrival.
    /// </summary>
    private const float DirectLootDelay = 2f;

    /// <summary>
    /// Max distance a survivor can be from a container and still loot or attack it.
    /// </summary>
    private const float LootInteractionRange = 3f;

    /// <summary>
    /// Safety cap on hits against a single container, in case its health never drops.
    /// </summary>
    private const int MaxHitsPerContainer = 20;

    /// <summary>
    /// Safety cap on hits against a door barricade, higher than MaxHitsPerContainer since barricades are sturdier.
    /// </summary>
    private const int MaxHitsPerBarricade = 40;

    /// <summary>
    /// Search radius EscalateStuckRecovery uses to look for a nearby Barricade blocking the survivor.
    /// </summary>
    private const float BarricadeAttackDetectionRange = 6f;

    // Detection range used to find a door to open when the survivor is stuck near one.
    private const float DoorOpenDetectionRange = 6f;

    // Distance past the door's center the survivor moves to clear the frame's collider before resuming normal movement.
    private const float DoorGhostClearanceDistance = 2.5f;

    // Height correction tolerance applied after closing a door, in case closing it physically displaced the survivor.
    private const float DoorCloseHeightCorrectionTolerance = 0.5f;

    // Max height a survivor may "jump" to reach a door on sloped terrain.
    private const float DoorJumpableHeight = 2f;

    /// <summary>
    /// Phases the survivor through a door it owns: moves to the door's position first, then clears the frame before resuming normal movement.
    /// </summary>
    private void GhostThroughOwnDoor(Survivor survivor, Door door, Vector3 destination, Action onArrived, Action onFailed, int recoveryTier)
    {
        BasePlayer npc = survivor.Player;
        Vector3 doorCenter = door.transform.position;

        Vector3 direction = destination - doorCenter;
        direction.y = 0f;

        if (direction.sqrMagnitude < 0.01f)
        {
            direction = npc.transform.forward;
        }

        Vector3 clearOfFrame = doorCenter + direction.normalized * DoorGhostClearanceDistance;
        clearOfFrame.y = doorCenter.y;

        // Closes the door again once the survivor is clear of the frame, restoring security either direction.
        void CloseDoorAndContinue()
        {
            if (!door.IsDestroyed && door.IsOpen())
            {
                door.SetOpen(false);
                door.SendNetworkUpdate();
            }

            // Corrects the survivor's height if closing the door physically displaced it.
            if (npc != null && !npc.IsDestroyed && Mathf.Abs(npc.transform.position.y - doorCenter.y) > DoorCloseHeightCorrectionTolerance)
            {
                Vector3 corrected = npc.transform.position;
                corrected.y = doorCenter.y;
                npc.transform.position = corrected;
                npc.MovePosition(corrected);
            }

            StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier);
        }

        StartLevelledPhasing(
            survivor,
            doorCenter,
            onArrived: () => StartPhasingToDestination(
                survivor,
                clearOfFrame,
                onArrived: CloseDoorAndContinue,
                onFailed: CloseDoorAndContinue),
            onFailed: CloseDoorAndContinue);
    }

    /// <summary>
    /// Computes a point squarely in front of the door, offset along whichever local axis best separates it from the survivor's position, staying on the survivor's current side.
    /// </summary>
    private Vector3 ComputeDoorApproachPoint(BasePlayer npc, Door door)
    {
        Vector3 doorCenter = door.transform.position;
        Vector3 toNpc = npc.transform.position - doorCenter;
        toNpc.y = 0f;

        Vector3 doorForward = door.transform.forward;
        doorForward.y = 0f;
        doorForward = doorForward.sqrMagnitude > 0.01f ? doorForward.normalized : Vector3.forward;

        // Picks whichever forward/back axis the survivor is more aligned with, to keep the approach point flush with the doorway.
        float alignment = toNpc.sqrMagnitude > 0.01f ? Vector3.Dot(toNpc.normalized, doorForward) : 1f;
        Vector3 approachDirection = alignment >= 0f ? doorForward : -doorForward;

        Vector3 approachPoint = doorCenter + approachDirection * DoorGhostClearanceDistance;
        approachPoint.y = doorCenter.y;
        return approachPoint;
    }

    private bool CanOpenDoor(BasePlayer npc, Door door)
    {
        BaseLock doorLock = door.GetSlot(BaseEntity.Slot.Lock) as BaseLock;

        if (doorLock == null || !doorLock.IsLocked())
        {
            return true;
        }

        return doorLock is CodeLock codeLock && codeLock.whitelistPlayers.Contains(npc.userID);
    }

    /// <summary>
    /// Checks nearby for a closed door blocking the survivor, using the same Construction layer mask as other construction checks.
    /// </summary>
    private bool TryFindBlockingClosedDoor(BasePlayer npc, out Door blockingDoor)
    {
        Collider[] hits = Physics.OverlapSphere(npc.transform.position, DoorOpenDetectionRange, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

        foreach (Collider hit in hits)
        {
            Door candidate = hit.GetComponentInParent<Door>();

            if (candidate == null || candidate.IsDestroyed || candidate.IsOpen())
            {
                continue;
            }

            if (CanOpenDoor(npc, candidate))
            {
                blockingDoor = candidate;
                return true;
            }
        }

        blockingDoor = null;
        return false;
    }

    /// <summary>
    /// Best-to-worst list of melee-capable tool/weapon shortnames used to pick a combat tool. Excludes mounted/siege weapons and power tools, which cannot be cast to BaseMelee.
    /// </summary>
    private static readonly string[] MeleeToolPriority =
    {
        "salvaged.sword",
        "longsword",
        "salvaged.cleaver",
        "machete",
        "mace",
        "mace.baseballbat",
        "knife.combat",
        "spear.stone",
        "spear.wooden",
        "spear.cny",
        "knife.butcher",
        "pitchfork",
        "pickaxe",
        "stone.pickaxe",
        "concretepickaxe",
        "diverpickaxe",
        "lumberjack.pickaxe",
        "hatchet",
        "stonehatchet",
        "concretehatchet",
        "diverhatchet",
        "lumberjack.hatchet",
        "frontier_hatchet",
        "axe.salvaged",
        "icepick.salvaged",
        "bone.club",
        "knife.bone",
        "knife.bone.obsidian",
        "knife.skinning",
        "candycaneclub",
        "vampire.stake",
        "sunken.knife",
        "boomerang",
        "paddle",
        "rock",
    };

    /// <summary>
    /// Validates every MeleeToolPriority shortname against the item database at startup and logs a warning for any that don't resolve.
    /// </summary>
    private void ValidateMeleeToolPriority()
    {
        List<string> unresolved = new();

        foreach (string shortname in MeleeToolPriority)
        {
            if (ItemManager.FindItemDefinition(shortname) == null)
            {
                unresolved.Add(shortname);
            }
        }

        if (unresolved.Count > 0)
        {
            Puts($"WARNING: MeleeToolPriority contains {unresolved.Count} shortname(s) that don't resolve to any real item - EquipBestMeleeTool will never recognize these: {string.Join(", ", unresolved)}.");
        }
        else
        {
            Puts($"MeleeToolPriority validated - all {MeleeToolPriority.Length} shortnames resolve to real items.");
        }
    }

    /// <summary>
    /// Best-to-worst weapon ranking used for belt slot 0 (main weapon). Separate from MeleeToolPriority since it only affects belt layout, not what gets equipped in combat. Excludes mounted/siege weapons, attachments, and thrown/deployed explosives.
    /// </summary>
    private static readonly string[] WeaponPriority =
    {
        "lmg.m249",
        "hmlmg",
        "minigun",
        "smg.thompson",
        "smg.mp5",
        "smg.2",
        "t1_smg",
        "rifle.ak",
        "rifle.lr300",
        "rifle.m39",
        "m16a2",
        "rifle.semiauto",
        "rifle.sks",
        "shotgun.m4",
        "krieg.shotgun",
        "rifle.l96",
        "rifle.bolt",
        "shotgun.spas12",
        "shotgun.pump",
        "shotgun.double",
        "shotgun.waterpipe",
        "pistol.m92",
        "revolver.hc",
        "pistol.python",
        "pistol.revolver",
        "pistol.semiauto",
        "pistol.semiauto.a.m15",
        "pistol.prototype17",
        "pistol.nailgun",
        "pistol.eoka",
        "bow.compound",
        "crossbow",
        "crossbowbowless",
        "bow.hunting",
        "minicrossbow",
        "speargun",
        "blowpipe",
        "salvaged.sword",
        "longsword",
        "salvaged.cleaver",
        "machete",
        "mace",
        "mace.baseballbat",
        "knife.combat",
        "spear.stone",
        "spear.wooden",
        "spear.cny",
        "knife.butcher",
        "pitchfork",
        "bone.club",
        "knife.bone",
        "knife.bone.obsidian",
        "knife.skinning",
        "candycaneclub",
        "vampire.stake",
        "sunken.knife",
        "boomerang",
        "paddle",
        "snowballgun",
        "paintballgun",
        "gun.water",
        "pistol.water",
    };

    /// <summary>
    /// Fully-automatic firearm shortnames, used to pick a non-automatic "offsider" weapon for belt slot 2 when slot 1 is automatic.
    /// </summary>
    private static readonly string[] AutomaticWeaponShortnames =
    {
        "lmg.m249",
        "hmlmg",
        "minigun",
        "smg.thompson",
        "smg.mp5",
        "smg.2",
        "t1_smg",
        "rifle.ak",
        "rifle.lr300",
    };

    /// <summary>
    /// Best-first gathering tool ranking for belt slot 5, kept separate from WeaponPriority. No per-task tool selection exists yet, so pickaxes rank slightly above hatchets as the more generally useful option.
    /// </summary>
    private static readonly string[] GatherToolPriority =
    {
        "jackhammer",
        "chainsaw",
        "pickaxe",
        "stone.pickaxe",
        "concretepickaxe",
        "diverpickaxe",
        "lumberjack.pickaxe",
        "hatchet",
        "stonehatchet",
        "concretehatchet",
        "diverhatchet",
        "lumberjack.hatchet",
        "frontier_hatchet",
        "axe.salvaged",
        "icepick.salvaged",
    };

    private const string MedicalSyringeShortname = "syringe.medical";
    private const string BandageShortname = "bandage";

    // Belt layout: slot 1 = best weapon (or best tool if no weapon owned); slot 2 = best offsider weapon (different category from slot 1);
    // slot 3 = medical; slot 4 = bandages; slot 5 = gathering tool; slot 6 = overflow.
    private const int BeltWeaponSlot = 0;
    private const int BeltOffsiderSlot = 1;
    private const int BeltMedicalSlot = 2;
    private const int BeltBandageSlot = 3;
    private const int BeltGatherToolSlot = 4;
    private static readonly int[] BeltOverflowSlots = { 5 };

    /// <summary>
    /// Validates every WeaponPriority shortname against the item database at startup and logs a warning for any that don't resolve.
    /// </summary>
    private void ValidateWeaponPriority()
    {
        List<string> unresolved = new();

        foreach (string shortname in WeaponPriority)
        {
            if (ItemManager.FindItemDefinition(shortname) == null)
            {
                unresolved.Add(shortname);
            }
        }

        if (unresolved.Count > 0)
        {
            Puts($"WARNING: WeaponPriority contains {unresolved.Count} shortname(s) that don't resolve to any real item - OrganizeBelt will never recognize these: {string.Join(", ", unresolved)}.");
        }
        else
        {
            Puts($"WeaponPriority validated - all {WeaponPriority.Length} shortnames resolve to real items.");
        }
    }

    /// <summary>
    /// Arranges the survivor's belt: slot 1 best weapon (or gather tool if none owned), slot 2 best offsider weapon, slot 3/4 medical/bandages, slot 5 gather tool, slot 6 overflow. Slot 1 is assigned once and kept unless combat forces a swap.
    /// </summary>
    private void OrganizeBelt(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        // Records the active item's slot before any moves below, to detect later whether it changed.
        Item activeItemBefore = npc.GetActiveItem();
        int? activeItemPositionBefore = activeItemBefore?.position;

        List<Item> allItems = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .ToList();

        Item currentSlotOne = npc.inventory.containerBelt.itemList.FirstOrDefault(item => item.position == BeltWeaponSlot);
        bool slotOneAlreadyCommitted = currentSlotOne != null
            && (Array.IndexOf(WeaponPriority, currentSlotOne.info.shortname) >= 0 || Array.IndexOf(GatherToolPriority, currentSlotOne.info.shortname) >= 0);

        Item primaryWeapon;
        Item primaryTool;
        Item primary;

        if (slotOneAlreadyCommitted)
        {
            primary = currentSlotOne;
            primaryWeapon = Array.IndexOf(WeaponPriority, currentSlotOne.info.shortname) >= 0 ? currentSlotOne : null;
            primaryTool = primaryWeapon == null ? currentSlotOne : null;
        }
        else
        {
            primaryWeapon = FindBestByPriority(allItems, WeaponPriority, exclude: null);

            // If the survivor owns no real weapon, the best tool takes the primary slot instead.
            primaryTool = primaryWeapon == null ? FindBestByPriority(allItems, GatherToolPriority, exclude: null) : null;

            primary = primaryWeapon ?? primaryTool;

            // Checks parent AND position, since position alone is only meaningful within an item's current container.
            bool primaryAlreadyPlaced = primary != null && primary.parent == npc.inventory.containerBelt && primary.position == BeltWeaponSlot;

            if (primary != null && !primaryAlreadyPlaced && !primary.MoveToContainer(npc.inventory.containerBelt, BeltWeaponSlot))
            {
                // Logs the failure so belt-layout issues can be diagnosed.
                Puts($"WARNING: '{survivor.Character.Alias}' - couldn't move '{primary.info.shortname}' into belt slot {BeltWeaponSlot + 1} (already at position {primary.position}, parent {(primary.parent == npc.inventory.containerBelt ? "belt" : primary.parent == npc.inventory.containerMain ? "main" : "other")}).");
            }
        }

        // Offsider only applies when slot 1 is a genuine weapon.
        if (primaryWeapon != null)
        {
            Item offsider = FindBestOffsider(allItems, primaryWeapon);

            if (offsider != null && offsider.position != BeltOffsiderSlot && !offsider.MoveToContainer(npc.inventory.containerBelt, BeltOffsiderSlot))
            {
                Puts($"WARNING: '{survivor.Character.Alias}' - couldn't move '{offsider.info.shortname}' into belt slot {BeltOffsiderSlot + 1} (already at position {offsider.position}, parent {(offsider.parent == npc.inventory.containerBelt ? "belt" : offsider.parent == npc.inventory.containerMain ? "main" : "other")}).");
            }
        }

        OrganizeMedicalAndExplosiveSlots(npc);

        // Excludes primaryTool so a second owned tool still gets its own dedicated gather-tool slot.
        Item gatherTool = FindBestByPriority(allItems, GatherToolPriority, exclude: primaryTool);

        if (gatherTool != null && gatherTool.position != BeltGatherToolSlot && !gatherTool.MoveToContainer(npc.inventory.containerBelt, BeltGatherToolSlot))
        {
            Puts($"WARNING: '{survivor.Character.Alias}' - couldn't move '{gatherTool.info.shortname}' into belt slot {BeltGatherToolSlot + 1} (already at position {gatherTool.position}, parent {(gatherTool.parent == npc.inventory.containerBelt ? "belt" : gatherTool.parent == npc.inventory.containerMain ? "main" : "other")}).");
        }

        DeclutterBeltOfWearables(npc);

        FillOverflowBeltSlots(npc);

        // Refreshes the client's held-item visual, but only if the active item's slot actually changed during this call.
        Item currentActive = npc.GetActiveItem();

        if (currentActive != null && currentActive.position != activeItemPositionBefore)
        {
            // Diagnostic log for tracking held-entity refreshes.
            VerbosePuts($"weapon-refresh-diag: '{survivor.Character.Alias}' active item '{currentActive.info.shortname}' moved from slot {activeItemPositionBefore} to slot {currentActive.position} during OrganizeBelt - forcing a held-entity refresh.");

            npc.UpdateActiveItem(currentActive.uid);
            ForceRefreshHeldEntity(npc);
        }
    }

    private static Item FindBestByPriority(List<Item> items, string[] priority, Item exclude)
    {
        Item best = null;
        int bestRank = int.MaxValue;

        foreach (Item item in items)
        {
            if (item == exclude)
            {
                continue;
            }

            int rank = Array.IndexOf(priority, item.info.shortname);

            if (rank >= 0 && rank < bestRank)
            {
                bestRank = rank;
                best = item;
            }
        }

        return best;
    }

    /// <summary>
    /// Picks the best belt-slot-2 "offsider" weapon for primaryWeapon: a non-automatic weapon if the primary is automatic, otherwise the next-best weapon overall.
    /// </summary>
    private static Item FindBestOffsider(List<Item> items, Item primaryWeapon)
    {
        bool primaryIsAutomatic = Array.IndexOf(AutomaticWeaponShortnames, primaryWeapon.info.shortname) >= 0;

        if (primaryIsAutomatic)
        {
            Item nonAutomaticOffsider = items
                .Where(item => item != primaryWeapon
                    && Array.IndexOf(WeaponPriority, item.info.shortname) >= 0
                    && Array.IndexOf(AutomaticWeaponShortnames, item.info.shortname) < 0)
                .OrderBy(item => Array.IndexOf(WeaponPriority, item.info.shortname))
                .FirstOrDefault();

            if (nonAutomaticOffsider != null)
            {
                return nonAutomaticOffsider;
            }
        }

        return items
            .Where(item => item != primaryWeapon && Array.IndexOf(WeaponPriority, item.info.shortname) >= 0)
            .OrderBy(item => Array.IndexOf(WeaponPriority, item.info.shortname))
            .FirstOrDefault();
    }

    private const string F1GrenadeShortname = "grenade.f1";
    private const string LargeMedkitShortname = "largemedkit";

    /// <summary>
    /// Fills BeltMedicalSlot and BeltBandageSlot by priority: F1 grenades then syringes then bandages for the medical slot, with a displaced syringe or large medkit falling to the bandage slot. Re-evaluated fresh each call based on current ownership.
    /// </summary>
    private void OrganizeMedicalAndExplosiveSlots(BasePlayer npc)
    {
        List<Item> allItems = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .ToList();

        Item grenade = allItems.FirstOrDefault(item => item.info.shortname == F1GrenadeShortname);
        Item syringe = allItems.FirstOrDefault(item => item.info.shortname == MedicalSyringeShortname);
        Item largeMedkit = allItems.FirstOrDefault(item => item.info.shortname == LargeMedkitShortname);
        Item bandage = allItems.FirstOrDefault(item => item.info.shortname == BandageShortname);

        Item medicalSlotItem = grenade ?? syringe ?? bandage;

        Item bandageSlotItem = medicalSlotItem == grenade && syringe != null
            ? syringe
            : largeMedkit ?? (bandage != medicalSlotItem ? bandage : null);

        if (medicalSlotItem != null && medicalSlotItem.position != BeltMedicalSlot)
        {
            medicalSlotItem.MoveToContainer(npc.inventory.containerBelt, BeltMedicalSlot);
        }

        if (bandageSlotItem != null && bandageSlotItem.position != BeltBandageSlot)
        {
            bandageSlotItem.MoveToContainer(npc.inventory.containerBelt, BeltBandageSlot);
        }
    }

    /// <summary>
    /// Moves any wearable armor/clothing left sitting in a belt slot back into main inventory, since the belt is reserved for weapons/medical/gather-tool roles.
    /// </summary>
    private void DeclutterBeltOfWearables(BasePlayer npc)
    {
        List<Item> wearablesOnBelt = npc.inventory.containerBelt.itemList
            .Where(item => item.info.GetComponent<ItemModWearable>() != null)
            .ToList();

        foreach (Item item in wearablesOnBelt)
        {
            item.MoveToContainer(npc.inventory.containerMain);
        }
    }

    /// <summary>
    /// Fills any empty overflow belt slots with spare medical stacks from main inventory. Never displaces an item already in a slot.
    /// </summary>
    private void FillOverflowBeltSlots(BasePlayer npc)
    {
        foreach (int slot in BeltOverflowSlots)
        {
            if (npc.inventory.containerBelt.GetSlot(slot) != null)
            {
                continue;
            }

            Item extraSyringe = npc.inventory.containerMain.itemList
                .FirstOrDefault(item => item.info.shortname == MedicalSyringeShortname);

            if (extraSyringe == null)
            {
                break;
            }

            extraSyringe.MoveToContainer(npc.inventory.containerBelt, slot, allowStack: true, ignoreStackLimit: false, sourcePlayer: null, allowSwap: false);
        }
    }

    /// <summary>
    /// Sorts the main inventory by item category, then by shortname within each category, so similar items sit together.
    /// </summary>
    private void TidyMainInventory(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        List<Item> sorted = npc.inventory.containerMain.itemList
            .OrderBy(item => (int)item.info.category)
            .ThenBy(item => item.info.shortname, StringComparer.Ordinal)
            .ToList();

        for (int targetPosition = 0; targetPosition < sorted.Count; targetPosition++)
        {
            Item item = sorted[targetPosition];

            if (item.position != targetPosition)
            {
                item.MoveToContainer(npc.inventory.containerMain, targetPosition);
            }
        }
    }

    /// <summary>
    /// Safety cap on how many times ConsumeFoodImmediately calls DoAction on a single item stack, to guard against a runaway loop.
    /// </summary>
    private const int MaxFoodConsumeActionsPerItem = 50;

    /// <summary>
    /// Eats food immediately on pickup instead of modeling hunger/thirst as a separate system, using the same consume action a player's Consume button triggers. Skips medical items, which are reserved for the belt instead.
    /// </summary>
    private void ConsumeFoodImmediately(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        // Water bottles are handled separately by DrinkWaterBottles since they're a reusable container, not a consumable stack.
        // Worms are excluded too since they're incidental clutter from gathering, not something deliberately eaten.
        List<Item> foodItems = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => item.info.category == ItemCategory.Food
                && item.info.shortname != WaterBottleShortname
                && item.info.shortname != WormShortname)
            .ToList();

        foreach (Item item in foodItems)
        {
            int actions = ConsumeViaItemModConsume(item, npc);

            if (actions > 0)
            {
                VerbosePuts($"'{survivor.Character.Alias}' ate '{item.info.shortname}' ({actions}x) on pickup.");
            }
        }
    }

    private const string WaterBottleShortname = "smallwaterbottle";
    private const string WormShortname = "worm";

    /// <summary>
    /// Repeatedly calls ItemModConsume's CanDoAction/DoAction to consume an item stack, stopping once the survivor is full. Returns how many times DoAction fired, or 0 if the item has no ItemModConsume component.
    /// </summary>
    private int ConsumeViaItemModConsume(Item item, BasePlayer npc)
    {
        ItemModConsume consume = item.info.GetComponent<ItemModConsume>();

        if (consume == null)
        {
            return 0;
        }

        int actions = 0;

        while (item.amount > 0 && actions < MaxFoodConsumeActionsPerItem && consume.CanDoAction(item, npc))
        {
            consume.DoAction(item, npc);
            actions++;
        }

        return actions;
    }

    /// <summary>
    /// Water bottles are drunk via ConsumeViaItemModConsume, then dropped once empty since the survivor cannot refill them.
    /// </summary>
    private const string WaterJugShortname = "waterjug";

    private void DrinkWaterBottles(Survivor survivor, BasePlayer npc)
    {
        List<Item> bottles = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => item.info.shortname == WaterBottleShortname)
            .ToList();

        // If the survivor owns a water jug, small bottles are pure clutter and get dropped outright instead of drunk.
        bool ownsJug = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Any(item => item.info.shortname == WaterJugShortname);

        if (ownsJug)
        {
            foreach (Item bottle in bottles)
            {
                Vector3 jugDropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
                Vector3 jugDropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

                bottle.Drop(jugDropPosition, jugDropVelocity);

                VerbosePuts($"'{survivor.Character.Alias}' dropped a water bottle - already carrying a water jug.");
            }

            return;
        }

        foreach (Item bottle in bottles)
        {
            int actions = ConsumeViaItemModConsume(bottle, npc);

            if (actions == 0)
            {
                continue;
            }

            // Only drops the bottle if it's still present (i.e. wasn't removed as a single-use item).
            Item stillHeld = npc.inventory.FindItemByUID(bottle.uid);

            if (stillHeld != null)
            {
                Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
                Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

                stillHeld.Drop(dropPosition, dropVelocity);
            }

            VerbosePuts($"'{survivor.Character.Alias}' drank a water bottle ({actions}x) and dropped it.");
        }
    }

    private const string RadiationPillsShortname = "antiradpills";

    /// <summary>
    /// Consumes radiation pills immediately on pickup, the same as food and water, via the standard ItemModConsume action.
    /// </summary>
    private void ConsumeRadiationPillsImmediately(Survivor survivor, BasePlayer npc)
    {
        List<Item> pillStacks = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => item.info.shortname == RadiationPillsShortname)
            .ToList();

        foreach (Item item in pillStacks)
        {
            int actions = ConsumeViaItemModConsume(item, npc);

            if (actions > 0)
            {
                VerbosePuts($"'{survivor.Character.Alias}' took {actions}x radiation pills on pickup.");
            }
        }
    }

    /// <summary>
    /// How many consecutive unreachable/no-line-of-sight loot attempts before the surrounding area gets treated as poisoned, rather than continuing to individually try each remaining candidate.
    /// </summary>
    private const int ConsecutiveFailuresBeforeAvoidingArea = 4;

    /// <summary>
    /// Radius poisoned around the survivor's position once ConsecutiveFailuresBeforeAvoidingArea is hit. Only affects which container is picked next, not pathing through the area.
    /// </summary>
    private const float PoisonedZoneRadius = 12f;

    /// <summary>
    /// How long a poisoned zone stays in effect. Time-limited rather than permanent so a briefly-struggled-in area isn't avoided forever.
    /// </summary>
    private const float PoisonedZoneDuration = 25f;

    /// <summary>
    /// A crate/barrel container more than this far above the survivor's standing height is excluded from search, as a cheap heuristic to avoid targeting unreachable elevated platform loot.
    /// </summary>
    private const float LootVerticalReachLimit = 3f;

    private static bool IsUnreachableCrateOrBarrel(Vector3 npcPosition, StorageContainer candidate)
    {
        if ((candidate.transform.position.y - npcPosition.y) <= LootVerticalReachLimit)
        {
            return false;
        }

        string name = candidate.ShortPrefabName;

        return name.IndexOf("crate", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("barrel", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Same vertical pre-filter as IsUnreachableCrateOrBarrel, extended to dropped items. Has no shortname restriction since a dropped item's shortname says nothing about reachability.
    /// </summary>
    private static bool IsUnreachableDroppedItem(Vector3 npcPosition, Vector3 candidatePosition)
    {
        return (candidatePosition.y - npcPosition.y) > LootVerticalReachLimit;
    }

    /// <summary>
    /// Separate poisoning mechanism from PoisonedZones, keyed per-survivor at the plugin level so it survives across loot-task instances, used to avoid re-walking into a spot a survivor was just attacked at.
    /// </summary>
    private const float ThreatFleePoisonRadius = 20f;

    /// <summary>
    /// How long a threat-flee poison zone lasts.
    /// </summary>
    private const float ThreatFleePoisonDuration = 30f;

    private readonly Dictionary<Guid, List<(Vector3 Center, float Radius, float ExpiresAt)>> _threatFleeZones = new();

    /// <summary>
    /// Marks the area around where a survivor was attacked as temporarily worth avoiding, so the loot search doesn't immediately walk back into the same danger after fleeing.
    /// </summary>
    private void PoisonAreaFromThreatFlee(Survivor survivor, Vector3 position, float radius = ThreatFleePoisonRadius, float duration = ThreatFleePoisonDuration)
    {
        Guid characterId = survivor.Character.Id;

        if (!_threatFleeZones.TryGetValue(characterId, out List<(Vector3 Center, float Radius, float ExpiresAt)> zones))
        {
            zones = new List<(Vector3, float, float)>();
            _threatFleeZones[characterId] = zones;
        }

        float expiresAt = UnityEngine.Time.realtimeSinceStartup + duration;
        zones.Add((position, radius, expiresAt));

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' will avoid the area around {position} ({radius:F0}m) for the next {duration:F0}s after fleeing.");
    }

    /// <summary>
    /// Entries are lazily expired on read rather than by a separate cleanup timer, since each survivor's list is small and short-lived.
    /// </summary>
    /// <summary>
    /// Safety filter that rejects loot candidates too far below sea level, as a guard against pathing into cave/tunnel systems overworld survivors aren't meant to enter.
    /// </summary>
    private const float SafeLootDepthBelowSeaLevel = -15f;

    private static bool IsBelowSafeLootDepth(Vector3 position)
    {
        return position.y < SafeLootDepthBelowSeaLevel;
    }

    private bool IsInThreatFleeZone(Guid characterId, Vector3 position)
    {
        if (!_threatFleeZones.TryGetValue(characterId, out List<(Vector3 Center, float Radius, float ExpiresAt)> zones))
        {
            return false;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;

        zones.RemoveAll(zone => zone.ExpiresAt <= now);

        foreach ((Vector3 center, float radius, float _) in zones)
        {
            if (Vector3.Distance(center, position) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Per-task-run scratch state threaded through the find-walk-loot chain: which containers are already handled, and which areas have proven unreachable for a while. Not persisted; purely runtime.
    /// </summary>
    private sealed class LootTaskState
    {
        public readonly HashSet<NetworkableId> Visited = new();
        public readonly List<(Vector3 Center, float Radius, float ExpiresAt)> PoisonedZones = new();
        public int ConsecutiveFailures;

        // A container that failed once stays excluded while this counts down, then becomes eligible again at zero.
        public readonly Dictionary<NetworkableId, int> PendingRetry = new();
        public readonly HashSet<NetworkableId> AlreadyRetried = new();

        // Counts total road-following hops across the task's lifetime, capping how many can be chained.
        public int RoadFollowAttempts;

        // Which monument zone indices this task has already searched, reset when the nearest monument changes.
        public string? CurrentMonumentZoneKey;
        public readonly HashSet<int> VisitedMonumentZoneIndices = new();

        // The monument this task has committed to working, and the deadline that commitment holds until.
        public string? CommittedMonumentName;
        public float CommittedMonumentDeadline;

        // Set only for a throwaway side-quest state (see LivingRust.TierZeroSideQuest.cs) - called
        // once EscalateSearchToMonumentZone has exhausted every zone, instead of its normal
        // EscalateSearchAlongRoad continuation, so a side-questing survivor resumes whatever it was
        // doing before rather than falling into ordinary opportunistic looting.
        public Action? OnMonumentZonesExhausted;

        // Caps this task to at most one long cross-country trip toward a distant known monument.
        public bool TraveledToDistantMonument;

        // The container/corpse/bag this survivor currently has a claim on, if any.
        public NetworkableId? ClaimedTargetId;

        // Which monument(s) this task has already run an authored ghost route through, capping it to one detour per monument per task.
        public readonly HashSet<string> GhostRouteVisitedMonuments = new();

        // Same one-attempt-per-monument-per-task cap as GhostRouteVisitedMonuments, for the card puzzle detour.
        public readonly HashSet<string> CardPuzzleVisitedMonuments = new();

        // Tracks genuine completion (not just an attempt) of ghost routes and card puzzles, used to decide monument handicap rewards.
        public readonly HashSet<string> GhostRouteCompletedMonuments = new();
        public readonly HashSet<string> CardPuzzleCompletedMonuments = new();
    }

    /// <summary>
    /// How many other containers must be successfully looted nearby before a previously-failed container gets exactly one retry.
    /// </summary>
    private const int RetrySuccessesBeforeRevisit = 3;

    /// <summary>
    /// Per-character active "breaking open a container" timer, letting an in-progress attack be cancelled cleanly.
    /// </summary>
    private readonly Dictionary<Guid, Timer> _activeAttacks = new();

    /// <summary>
    /// Registry of loot targets already claimed by another survivor, keyed by net ID and valued by claim expiry. Prevents two survivors from converging on the same container.
    /// </summary>
    private readonly Dictionary<NetworkableId, float> _lootClaims = new();

    /// <summary>
    /// Registry of road/trail destinations already claimed by a survivor, keyed by Character ID. Prevents multiple survivors converging on the same road-following hop.
    /// </summary>
    private readonly Dictionary<Guid, Vector3> _roadHopClaims = new();

    // Distance within which another survivor's claimed road-hop destination is considered contested.
    private const float RoadHopContentionRadius = 30f;

    // How far along the road to nudge a contested destination.
    private const float RoadHopContentionOffsetMin = 50f;
    private const float RoadHopContentionOffsetMax = 100f;

    /// <summary>
    /// Whether position is close enough to another survivor's claimed road-hop destination to count as contested.
    /// </summary>
    private bool IsRoadDestinationContested(Vector3 position, Guid selfCharacterId)
    {
        foreach (KeyValuePair<Guid, Vector3> claim in _roadHopClaims)
        {
            if (claim.Key != selfCharacterId && Vector3.Distance(position, claim.Value) <= RoadHopContentionRadius)
            {
                return true;
            }
        }

        return false;
    }

    // Upper bound on how long a claim should live; a safety net against a leaked claim rather than a normal expiry path.
    private const float LootClaimTtlSeconds = 60f;

    private bool IsLootTargetClaimed(NetworkableId id)
    {
        if (_lootClaims.TryGetValue(id, out float expiresAt))
        {
            if (expiresAt > UnityEngine.Time.realtimeSinceStartup)
            {
                return true;
            }

            _lootClaims.Remove(id);
        }

        return false;
    }

    private void ClaimLootTarget(LootTaskState state, NetworkableId id)
    {
        _lootClaims[id] = UnityEngine.Time.realtimeSinceStartup + LootClaimTtlSeconds;
        state.ClaimedTargetId = id;
    }

    private void ReleaseLootClaim(LootTaskState state)
    {
        if (state.ClaimedTargetId.HasValue)
        {
            _lootClaims.Remove(state.ClaimedTargetId.Value);
            state.ClaimedTargetId = null;
        }
    }

    /// <summary>
    /// Debug command that manually triggers the loot-for-resources task on a survivor, since there is no autonomous trigger yet.
    /// </summary>
    [ChatCommand("lr.debug.settask")]
    private void CmdDebugSetTask(BasePlayer player, string command, string[] args)
    {
        RunDebugSetTask(player, args.Length > 0 ? string.Join(" ", args) : null);
    }

    [ConsoleCommand("lr.debug.settask")]
    private void CmdDebugSetTaskConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player == null)
        {
            return;
        }

        string aliasFilter = arg.HasArgs()
            ? string.Join(" ", arg.Args.Select(a => a.ToString()))
            : null;

        RunDebugSetTask(player, aliasFilter);
    }

    private void RunDebugSetTask(BasePlayer player, string aliasFilter)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor survivor = string.IsNullOrWhiteSpace(aliasFilter)
            ? FindNearestSpawnedSurvivor(player.transform.position)
            : FindSpawnedSurvivorByAlias(aliasFilter);

        if (survivor == null)
        {
            string message = string.IsNullOrWhiteSpace(aliasFilter)
                ? "No spawned survivor nearby. Use /lr.spawn first."
                : $"No spawned survivor named '{aliasFilter}'.";

            player.ChatMessage($"[LivingRust] {message}");
            Puts($"settask: {message}");
            return;
        }

        StartLootForResourcesTask(survivor);

        string confirm = $"'{survivor.Character.Alias}' is now looting for resources.";
        player.ChatMessage($"[LivingRust] {confirm}");
    }

    /// <summary>
    /// Kicks off the scan-vicinity-then-loot-everything-reachable loop, driven by chained onArrived callbacks rather than a separate task-tick timer.
    /// </summary>
    private void StartLootForResourcesTask(Survivor survivor)
    {
        // Checked first and unconditionally, so an established survivor resuming its normal
        // gear-weighted destination roll (the common case for a death well past the checklist -
        // e.g. mid monument loot run) still detours to its own death spot first, not just a
        // survivor still on the checklist/base-gather path that happens to route through
        // ContinueLootTask below.
        BasePlayer earlyNpc = survivor.Player;

        if (earlyNpc != null && !earlyNpc.IsDestroyed && TryPursueDeathSiteLoot(survivor, earlyNpc, new LootTaskState()))
        {
            return;
        }

        if (ShouldHoldForAirdrop(survivor.Character.Id))
        {
            return;
        }

        survivor.Character.CurrentTask = TaskType.LootForResources;

        // Checked before either roll below - a rushing survivor skips the checklist and home-site walk entirely.
        if (RollMonumentRushIfFreshLife(survivor))
        {
            BasePlayer rushNpc = survivor.Player;

            if (rushNpc == null || rushNpc.IsDestroyed)
            {
                Puts($"loot-task: '{survivor.Character.Alias}' has no live BasePlayer, can't start.");
                survivor.Character.CurrentTask = TaskType.None;
                return;
            }

            DropUnneededLightSource(survivor, rushNpc);
            PerformInventoryCheck(survivor, rushNpc);

            // The immediate rush variant still needs its minimal hatchet+pickaxe kit first -
            // routes through the normal task loop so TryPursueImmediateRushTools (checked early
            // in ContinueLootTask) can gather/craft them before departing for the monument.
            if (_pursuingImmediateRushTools.Contains(survivor.Character.Id))
            {
                ContinueLootTask(survivor, new LootTaskState());
                return;
            }

            TryStartWithGearWeightedDestination(survivor, rushNpc);
            return;
        }

        // Internally gated to fire only once per life, since this method is called from many places, not just a fresh spawn.
        RollPrimitiveGoalIfFreshLife(survivor);

        // Same once-per-life gating. Takes a callback so the rest of this method only runs once the roll (and any resulting walk) completes.
        RollHomeSiteStrategyIfFreshLife(survivor, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                Puts($"loot-task: '{survivor.Character.Alias}' has no live BasePlayer, can't start.");
                survivor.Character.CurrentTask = TaskType.None;
                return;
            }

            DropUnneededLightSource(survivor, npc);

            // Confirms kit is in order before heading off, not just reactively after each pickup.
            PerformInventoryCheck(survivor, npc);

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' scanning a {LootSearchRadius:F0}m radius for containers to loot.");

            // Skips straight to a local scan for a survivor pursuing the primitive checklist, so that priority actually runs first
            // instead of potentially being preceded by a long gear-weighted destination walk.
            if (_pursuingPrimitiveGoals.Contains(survivor.Character.Id))
            {
                ContinueLootTask(survivor, new LootTaskState(), forceLocalScan: true);
                return;
            }

            // Same priority-before-destination-roll handling, extended to base-gathering, so a survivor resuming a base attempt
            // doesn't wander off toward a monument or road instead.
            if (_pursuingBaseGatherGoal.Contains(survivor.Character.Id))
            {
                ContinueLootTask(survivor, new LootTaskState(), forceLocalScan: true);
                return;
            }

            // Rolls a gear-weighted starting destination instead of always just searching locally, so spawns don't all converge on the same nearby loot.
            TryStartWithGearWeightedDestination(survivor, npc);
        });
    }

    private void ContinueLootTask(Survivor survivor, LootTaskState state, bool forceLocalScan = false)
    {
        // Combat takes over entirely once it starts. Checking here catches every loot-chain re-entry path, not just the cancel calls at combat start.
        if (_activeCombat.ContainsKey(survivor.Character.Id))
        {
            return;
        }

        // Releases whatever this survivor claimed last cycle, unconditionally, so a claim never outlives the survivor that held it.
        ReleaseLootClaim(state);

        // Same release-at-the-top pattern for road-hop destinations.
        _roadHopClaims.Remove(survivor.Character.Id);

        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.None;
            return;
        }

        // Proactive self-heal so a survivor bandages itself regardless of what it's doing, not just during a tactical combat retreat.
        // Fire-and-forget: no-ops instantly if health is fine, already mid-chain, on cooldown, or has nothing to heal with.
        TryUseMedicalItemIfHurt(survivor);

        // Top priority right after a respawn: go scavenge the survivor's own death spot first.
        if (TryPursueDeathSiteLoot(survivor, npc, state))
        {
            return;
        }

        // Top priority after a fight-won kill.
        if (TryPursuePriorityKillLoot(survivor, npc, state))
        {
            return;
        }

        // Airdrop priority.
        if (ShouldHoldForAirdrop(survivor.Character.Id))
        {
            return;
        }

        // Standing safety net so a survivor never goes fully toolless, checked every cycle ahead of the checklist/base-gather branches.
        // Cheap in the common case since HasAnyToolOfFamily short-circuits once a tool already exists.
        if (!HasAnyToolOfFamily(npc, HatchetFamily) && TryPursueOneOffToolGoal(survivor, npc, state, StoneHatchetShortname, "stone hatchet"))
        {
            return;
        }

        if (!HasAnyToolOfFamily(npc, PickaxeFamily) && TryPursueOneOffToolGoal(survivor, npc, state, StonePickaxeShortname, "stone pickaxe"))
        {
            return;
        }

        // If the survivor is hurt and has nothing left to heal with, prioritizes finding bandage supply at the same tier as the toolless safety net above.
        if (TryPursueBandageSupplyIfHurt(survivor, npc, state))
        {
            return;
        }

        // Standing survival-kit upkeep; see TryPursueSurvivalKitUpkeep's own doc comment.
        if (TryPursueSurvivalKitUpkeep(survivor, npc, state))
        {
            return;
        }

        // Prioritizes a deposit trip promptly when the survivor is carrying a spare firearm and has a base to deposit it at. Does nothing without a base.
        if (TryPursueSpareFirearmDeposit(survivor, npc, state))
        {
            return;
        }

        // Progression goal layer; see LivingRust.WipeGoals.cs's own doc comment. Falls through to normal looting when nothing is currently blocking progress.
        if (TryPursueWipeGoal(survivor, npc, state))
        {
            return;
        }

        // An immediate-monument-rush survivor gathering its minimal hatchet+pickaxe kit checks
        // before the full checklist below - it deliberately skips everything else in that
        // checklist until a genuine monument clear.
        if (TryPursueImmediateRushTools(survivor, npc, state))
        {
            return;
        }

        // A survivor pursuing the primitive starter checklist (bag, bow, arrows, stone tools) checks it before the normal loot search below.
        // Once every checklist goal is satisfied, this permanently stops checking for the rest of that life.
        if (_pursuingPrimitiveGoals.Contains(survivor.Character.Id))
        {
            // Redirects a coastal survivor making no gathering progress toward an inland site, before the 15-minute timeout below.
            if (TryRedirectStalledCoastalBotInland(survivor, npc))
            {
                return;
            }

            // Gives up on the checklist after 15 minutes and switches to base-gathering, checked before trying another craft cycle.
            if (UnityEngine.Time.realtimeSinceStartup >= GetOrSetPrimitiveGoalDeadline(survivor.Character.Id))
            {
                // Moves straight to base-gathering instead of retrying the checklist later, since wood/stone are more reliably available than whatever checklist item was the blocker.
                _pursuingPrimitiveGoals.Remove(survivor.Character.Id);
                _pursuingBaseGatherGoal.Add(survivor.Character.Id);
                Puts($"craft-task: '{survivor.Character.Alias}' didn't finish its primitive checklist within 15 minutes - moving straight to gathering for a base instead.");
            }
            else if (TryStartCraftingFallback(survivor, npc, state))
            {
                return;
            }
            // A false return here does not mean the checklist is done. Only clears the flag once every item is confirmed owned;
            // otherwise falls through to normal looting for this cycle while staying in the priority set to retry next cycle.
            else if (HasCompletedPrimitiveGoals(survivor, npc))
            {
                _pursuingPrimitiveGoals.Remove(survivor.Character.Id);

                // A finished checklist leads into gathering for a base next, so loot progress isn't lost with nowhere to store it.
                if (survivor.Character.Home == null)
                {
                    _pursuingBaseGatherGoal.Add(survivor.Character.Id);
                    Puts($"craft-task: '{survivor.Character.Alias}' has everything from its primitive starter checklist - moving on to gather for a base.");
                }
                else
                {
                    Puts($"craft-task: '{survivor.Character.Alias}' has everything from its primitive starter checklist - back to normal looting.");
                }
            }
        }

        // Gather-for-a-base fallback, driving the design roll -> gather -> build pipeline.
        if (_pursuingBaseGatherGoal.Contains(survivor.Character.Id))
        {
            if (TryPursueBaseGatherGoal(survivor, npc, state))
            {
                return;
            }
        }

        // Checks main inventory alone rather than main+belt, since new loot always lands in main first.
        // Falls back to a recycler trip instead of idling forever when the inventory is full.
        if (IsMainInventoryFullIncludingBackpack(npc))
        {
            if (!TryStartRecyclingTask(survivor))
            {
                // A based survivor with nothing worth recycling nearby heads home to deposit instead of idling.
                if (survivor.Character.Home != null)
                {
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' is full up with nothing worth recycling nearby - heading home to deposit.");
                    GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor));
                }
                // A base-less survivor with a full inventory and nothing worth recycling drops its least valuable item to keep moving,
                // rather than idling and getting stuck in a relocate loop.
                // Cooldown-gated so a survivor stuck on an unrelated movement problem doesn't keep dropping items every retry.
                else if (Time.realtimeSinceStartup - _lastFullInventoryDropTime.GetValueOrDefault(survivor.Character.Id) >= FullInventoryDropCooldownSeconds
                    && DropLowerPriorityItem(npc, (LootPriorityTier)(-1)))
                {
                    _lastFullInventoryDropTime[survivor.Character.Id] = Time.realtimeSinceStartup;
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' is full up with nothing worth recycling and no base yet - dropped its least valuable item to keep moving.");
                    StartLootForResourcesTask(survivor);
                }
                else
                {
                    // Genuinely nothing evictable, or still on cooldown - left idle; the life-stall watchdog picks this up as usual.
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' is full up - done looting.");
                    survivor.Character.CurrentTask = TaskType.None;
                }
            }

            return;
        }

        // Restores the display weapon before every new search/walk cycle. Covers the case where a failed container attempt
        // never called OnLootObtained, so nothing else would have swapped the melee tool back.
        RunLootHookSafely(survivor, nameof(EquipBestWeaponForDisplay), () => EquipBestWeaponForDisplay(survivor));

        // A bare-handed survivor only considers containers that don't require destruction, since swinging with empty hands is unproductive.
        bool hasMeleeTool = HasAnyMeleeTool(npc);

        // Only computed when actually needed (roadsign candidates), since HasNonRockMeleeTool does its own inventory scan.
        bool? hasNonRockMeleeTool = null;

        // Coin flip between a local radius search and heading to a known monument loot zone, so bots spread out instead of lingering on exhausted ground.
        // forceLocalScan forces the local scan this cycle; near a real monument (checked against both the nearby-monument and wider committed-monument radius) also forces it, so containers aren't skipped mid-monument.
        bool nearRealMonument = (state.CommittedMonumentName != null && TryGetCommittedMonument(npc.transform.position, state, out _))
            || (TryGetNearestMonument(npc.transform.position, MonumentLootZoneDetectionRadius, out MonumentInfo nearbyMonument)
                && !IsMonumentExcludedFromAutonomy(nearbyMonument));

        if (!forceLocalScan && !nearRealMonument && UnityEngine.Random.value < 0.5f)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' skipped the local scan this cycle (dice roll) - checking known loot zones instead.");
            EscalateSearchToMonumentZone(survivor, state);
            return;
        }

        // Computed once per cycle and reused across all searches below; see GetOccupiedPowerlineTowers' own doc comment.
        List<Vector3> occupiedPowerlineTowers = GetOccupiedPowerlineTowers(survivor, npc.transform.position);

        // Computed once per cycle and reused below; see GetNearbyVisibleHostileScientistPositions' own doc comment (LivingRust.Combat.cs). Avoids looting near a visible hostile scientist unless armed with ranged ammo.
        List<Vector3> nearbyVisibleHostileScientists = GetNearbyVisibleHostileScientistPositions(npc);

        // Direct-loot containers (crates, lockers, ...) rank above anything requiring destruction (barrels, roadsigns) as two separate search tiers.
        bool foundContainer = _engine.NavigationManager.TryFindNearestLootContainer(
            npc.transform.position,
            LootSearchRadius,
            out StorageContainer container,
            candidate => !state.Visited.Contains(candidate.net.ID)
                && !IsLootTargetClaimed(candidate.net.ID)
                && candidate.inventory != null
                && candidate.inventory.itemList.Count > 0
                // Excludes containers made entirely of never-loot items, since they'd never empty and would keep getting re-picked. See TryFindEnRouteLootCandidate's identical check.
                && candidate.inventory.itemList.Any(item => !IsNeverLootItem(item.info.shortname))
                && !IsNearJunkpileJVan(candidate.transform.position)
                && !IsInPoisonedZone(candidate.transform.position, state)
                && !IsUnreachableCrateOrBarrel(npc.transform.position, candidate)
                && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)
                && !IsVehicleFuelStorage(candidate)
                && !IsHotAirBalloonStorage(candidate)
                && !IsRowboatStorage(candidate)
                && !IsMailbox(candidate)
                && !IsNearCardReader(candidate.transform.position)
                && !IsNearOccupiedPowerlineTower(candidate.transform.position, occupiedPowerlineTowers)
                && !IsNearVisibleHostileScientist(candidate.transform.position, nearbyVisibleHostileScientists)
                && !RequiresDestructionToLoot(candidate));

        bool foundBarrel = _engine.NavigationManager.TryFindNearestLootContainer(
            npc.transform.position,
            LootSearchRadius,
            out StorageContainer barrel,
            candidate => !state.Visited.Contains(candidate.net.ID)
                && !IsLootTargetClaimed(candidate.net.ID)
                && candidate.inventory != null
                && candidate.inventory.itemList.Count > 0
                // Excludes containers made entirely of never-loot items, since they'd never empty and would keep getting re-picked. See TryFindEnRouteLootCandidate's identical check.
                && candidate.inventory.itemList.Any(item => !IsNeverLootItem(item.info.shortname))
                && !IsNearJunkpileJVan(candidate.transform.position)
                && !IsInPoisonedZone(candidate.transform.position, state)
                && !IsUnreachableCrateOrBarrel(npc.transform.position, candidate)
                && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)
                && !IsVehicleFuelStorage(candidate)
                && !IsHotAirBalloonStorage(candidate)
                && !IsRowboatStorage(candidate)
                && !IsMailbox(candidate)
                && !IsNearCardReader(candidate.transform.position)
                && !IsNearOccupiedPowerlineTower(candidate.transform.position, occupiedPowerlineTowers)
                && !IsNearVisibleHostileScientist(candidate.transform.position, nearbyVisibleHostileScientists)
                && RequiresDestructionToLoot(candidate)
                && (IsRoadsign(candidate)
                    ? (hasNonRockMeleeTool ??= HasNonRockMeleeTool(npc))
                    : hasMeleeTool));

        // Corpses (player, scientist/NPC, animal) are searched separately from containers, excluding safe-zone-protected corpses the survivor doesn't own. Picks the highest-value corpse among candidates, not just the nearest.
        bool foundCorpse = _engine.NavigationManager.TryFindBestLootableCorpse(
            npc.transform.position,
            LootSearchRadius,
            scorer: candidate => GetContentsGearScore(candidate.containers?.Where(c => c != null).SelectMany(c => c.itemList) ?? Enumerable.Empty<Item>()),
            out LootableCorpse corpse,
            candidate => !state.Visited.Contains(candidate.net.ID)
                && !IsLootTargetClaimed(candidate.net.ID)
                && candidate.containers != null
                && candidate.containers.Any(c => c != null && c.itemList.Count > 0)
                && !IsInPoisonedZone(candidate.transform.position, state)
                && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)
                && !IsNearCardReader(candidate.transform.position)
                && !IsNearOccupiedPowerlineTower(candidate.transform.position, occupiedPowerlineTowers)
                && !IsNearVisibleHostileScientist(candidate.transform.position, nearbyVisibleHostileScientists)
                && (candidate is not PlayerCorpse || candidate.playerSteamID == survivor.Character.BotId || !candidate.InSafeZone())
                // A corpse the survivor genuinely killed stays reachable beyond the tightened ambient range; anything else must be nearby.
                && (Vector3.Distance(npc.transform.position, candidate.transform.position) <= CorpseAmbientAwarenessRadius
                    || IsRememberedOwnKill(survivor.Character.Id, candidate.transform.position)));

        // Despawned bodies convert into a lootable bag, a separate entity class searched with the same loot-value weighting as corpses.
        bool foundBag = _engine.NavigationManager.TryFindBestDroppedItemContainer(
            npc.transform.position,
            LootSearchRadius,
            scorer: candidate => GetContentsGearScore(candidate.inventory?.itemList ?? Enumerable.Empty<Item>()),
            out DroppedItemContainer bag,
            candidate => !state.Visited.Contains(candidate.net.ID)
                && !IsLootTargetClaimed(candidate.net.ID)
                && candidate.inventory != null
                && candidate.inventory.itemList.Count > 0
                && !IsInPoisonedZone(candidate.transform.position, state)
                && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)
                && !IsNearCardReader(candidate.transform.position)
                && !IsNearOccupiedPowerlineTower(candidate.transform.position, occupiedPowerlineTowers)
                && !IsNearVisibleHostileScientist(candidate.transform.position, nearbyVisibleHostileScientists)
                && (candidate.playerSteamID == 0 || candidate.playerSteamID == survivor.Character.BotId || !candidate.InSafeZone())
                // Same ambient-awareness gate the corpse search above uses.
                && (Vector3.Distance(npc.transform.position, candidate.transform.position) <= CorpseAmbientAwarenessRadius
                    || IsRememberedOwnKill(survivor.Character.Id, candidate.transform.position)));

        // A standalone loose item, a distinct entity type from a dropped-item container. IsNeverLootItem pre-filters here
        // so a survivor doesn't waste a walk toward something it will refuse to pick up on arrival anyway.
        bool foundDroppedItem = _engine.NavigationManager.TryFindNearestDroppedItem(
            npc.transform.position,
            LootSearchRadius,
            out DroppedItem droppedItem,
            candidate => !state.Visited.Contains(candidate.net.ID)
                && !IsLootTargetClaimed(candidate.net.ID)
                && !IsNeverLootItem(candidate.item.info.shortname)
                && !IsInPoisonedZone(candidate.transform.position, state)
                && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)
                && !IsNearCardReader(candidate.transform.position)
                && !IsNearOccupiedPowerlineTower(candidate.transform.position, occupiedPowerlineTowers)
                && !IsNearVisibleHostileScientist(candidate.transform.position, nearbyVisibleHostileScientists)
                && !IsUnreachableDroppedItem(npc.transform.position, candidate.transform.position));

        // Always searched within the tighter CollectibleSearchRadius, so collectibles can participate in the proximity-priority
        // check below alongside everything else, rather than being starved whenever a container exists anywhere in the wider radius.
        bool foundCollectible = _engine.NavigationManager.TryFindNearestCollectible(
            npc.transform.position,
            ApplyFreshWipeCollectibleBoost(CollectibleSearchRadius),
            out CollectibleEntity collectible,
            candidate => !state.Visited.Contains(candidate.net.ID)
                && !IsLootTargetClaimed(candidate.net.ID)
                && candidate.itemList != null
                && candidate.itemList.Length > 0
                && !IsInPoisonedZone(candidate.transform.position, state)
                && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)
                && !IsNearVisibleHostileScientist(candidate.transform.position, nearbyVisibleHostileScientists)
                && !IsExcludedCollectibleType(candidate)
                && IsResourceAllowedForHighGear(survivor, npc, candidate.itemList)
                && IsFarmedResourceWanted(survivor, candidate.itemList)
                && Vector3.Distance(npc.transform.position, candidate.transform.position) <= GetCollectibleDivertRadius(candidate));

        if (!foundContainer && !foundBarrel && !foundCorpse && !foundBag && !foundDroppedItem && !foundCollectible)
        {
            // Checked before active resource gathering, since crafting is effectively free when enough raw material is already held.
            if (TryStartCraftingFallback(survivor, npc, state))
            {
                return;
            }

            // Checked before falling back to monument-zone road-following.
            if (TryStartResourceGatheringFallback(survivor, npc, state))
            {
                return;
            }

            // Checked before road-following, since a monument's interior often has no road running through it, and a known
            // loot cluster elsewhere in the same monument is a more targeted next step.
            EscalateSearchToMonumentZone(survivor, state);
            return;
        }

        float containerDistance = foundContainer ? Vector3.Distance(npc.transform.position, container.transform.position) : float.MaxValue;
        float barrelDistance = foundBarrel ? Vector3.Distance(npc.transform.position, barrel.transform.position) : float.MaxValue;
        float bagDistance = foundBag ? Vector3.Distance(npc.transform.position, bag.transform.position) : float.MaxValue;
        float corpseDistance = foundCorpse ? Vector3.Distance(npc.transform.position, corpse.transform.position) : float.MaxValue;
        float droppedItemDistance = foundDroppedItem ? Vector3.Distance(npc.transform.position, droppedItem.transform.position) : float.MaxValue;
        float collectibleDistance = foundCollectible ? Vector3.Distance(npc.transform.position, collectible.transform.position) : float.MaxValue;

        // Proximity override: anything within NearbyLootPriorityRadius wins outright, whichever's closest regardless of type,
        // so a survivor sweeps everything genuinely close before moving on rather than beelining past it for a "higher tier" find
        // farther away. Only when nothing is close enough does the strict type-tier order below apply.
        float bestNearbyDistance = float.MaxValue;

        if (foundContainer && containerDistance <= NearbyLootPriorityRadius) bestNearbyDistance = Mathf.Min(bestNearbyDistance, containerDistance);
        if (foundBarrel && barrelDistance <= NearbyLootPriorityRadius) bestNearbyDistance = Mathf.Min(bestNearbyDistance, barrelDistance);
        if (foundCorpse && corpseDistance <= NearbyLootPriorityRadius) bestNearbyDistance = Mathf.Min(bestNearbyDistance, corpseDistance);
        if (foundBag && bagDistance <= NearbyLootPriorityRadius) bestNearbyDistance = Mathf.Min(bestNearbyDistance, bagDistance);
        if (foundDroppedItem && droppedItemDistance <= NearbyLootPriorityRadius) bestNearbyDistance = Mathf.Min(bestNearbyDistance, droppedItemDistance);
        // No extra radius check needed here - the search predicate above already enforces the per-type cutoff, so any
        // foundCollectible is already guaranteed close enough to matter.
        if (foundCollectible) bestNearbyDistance = Mathf.Min(bestNearbyDistance, collectibleDistance);

        bool proximityOverrideActive = bestNearbyDistance < float.MaxValue;

        // Strict priority tiers, fallback whenever nothing triggered the proximity override above: a standalone dropped item
        // outranks everything else regardless of distance; corpse-vs-bag ties break on better loot value rather than distance.
        bool preferDroppedItemOverOthers = proximityOverrideActive ? droppedItemDistance == bestNearbyDistance : foundDroppedItem;
        bool preferCorpseOverOthers = proximityOverrideActive
            ? corpseDistance == bestNearbyDistance
            : !preferDroppedItemOverOthers && foundCorpse
                && (!foundBag || GetContentsGearScore(corpse.containers?.Where(c => c != null).SelectMany(c => c.itemList) ?? Enumerable.Empty<Item>())
                    >= GetContentsGearScore(bag.inventory?.itemList ?? Enumerable.Empty<Item>()));
        bool preferBagOverOthers = proximityOverrideActive
            ? bagDistance == bestNearbyDistance
            : !preferDroppedItemOverOthers && !preferCorpseOverOthers && foundBag;
        bool preferContainerOverOthers = proximityOverrideActive
            ? containerDistance == bestNearbyDistance
            : !preferDroppedItemOverOthers && !preferCorpseOverOthers && !preferBagOverOthers && foundContainer;
        bool preferBarrelOverOthers = proximityOverrideActive
            ? barrelDistance == bestNearbyDistance
            : !preferDroppedItemOverOthers && !preferCorpseOverOthers && !preferBagOverOthers && !preferContainerOverOthers && foundBarrel;
        bool preferCollectibleOverOthers = proximityOverrideActive && collectibleDistance == bestNearbyDistance;

        // No shouldWalkCarefully needed here - StartWalking sprints by default for every walk and eases off automatically
        // near the destination, so no container-specific gating is required.
        if (preferCorpseOverOthers)
        {
            state.Visited.Add(corpse.net.ID);
            ClaimLootTarget(state, corpse.net.ID);

            Vector3 corpseApproachPoint = GetApproachPoint(corpse, npc);

            StartWalkingWithRecovery(
                survivor,
                corpseApproachPoint,
                onArrived: () => LootCorpseAndContinue(survivor, corpse, state),
                onFailed: () =>
                {
                    // Re-fetches the player fresh since it may have died or despawned during the recovery chain.
                    BasePlayer liveNpc = survivor.Player;

                    if (liveNpc == null || liveNpc.IsDestroyed)
                    {
                        ContinueLootTask(survivor, state);
                        return;
                    }

                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach a corpse even after trying to recover - skipping it.");
                    PoisonAreaNow(survivor, state, liveNpc.transform.position);
                    ContinueLootTask(survivor, state);
                });

            return;
        }

        if (preferBagOverOthers)
        {
            // Reached when bag wins the corpse-vs-bag comparison, or no corpse was found. Bags and corpses are treated as peer-tier "fresh loot" sources, both outranking a plain container.
            state.Visited.Add(bag.net.ID);
            ClaimLootTarget(state, bag.net.ID);

            Vector3 bagApproachPoint = GetApproachPoint(bag, npc);

            StartWalkingWithRecovery(
                survivor,
                bagApproachPoint,
                onArrived: () => LootDroppedItemContainerAndContinue(survivor, bag, state),
                onFailed: () =>
                {
                    // See the corpse onFailed above for why the outer npc
                    // local can't be trusted here - re-fetch fresh.
                    BasePlayer liveNpc = survivor.Player;

                    if (liveNpc == null || liveNpc.IsDestroyed)
                    {
                        ContinueLootTask(survivor, state);
                        return;
                    }

                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach a dropped bag even after trying to recover - skipping it.");
                    PoisonAreaNow(survivor, state, liveNpc.transform.position);
                    ContinueLootTask(survivor, state);
                });

            return;
        }

        if (preferDroppedItemOverOthers)
        {
            // A standalone loose item, not a container, so it uses the simpler single-pickup flow instead of the paced multi-item loot flow.
            state.Visited.Add(droppedItem.net.ID);
            ClaimLootTarget(state, droppedItem.net.ID);

            Vector3 droppedItemApproachPoint = GetApproachPoint(droppedItem, npc);

            StartWalkingWithRecovery(
                survivor,
                droppedItemApproachPoint,
                onArrived: () => PickupDroppedItemAndContinue(survivor, droppedItem, state),
                onFailed: () =>
                {
                    // Re-fetches npc fresh since the outer local can't be trusted here.
                    BasePlayer liveNpc = survivor.Player;

                    if (liveNpc == null || liveNpc.IsDestroyed)
                    {
                        ContinueLootTask(survivor, state);
                        return;
                    }

                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach a dropped item even after trying to recover - skipping it.");
                    PoisonAreaNow(survivor, state, liveNpc.transform.position);
                    ContinueLootTask(survivor, state);
                });

            return;
        }

        if (preferContainerOverOthers)
        {
            state.Visited.Add(container.net.ID);
            ClaimLootTarget(state, container.net.ID);

            Vector3 approachPoint = GetApproachPoint(container, npc);

            StartWalkingWithRecovery(
                survivor,
                approachPoint,
                onArrived: () => LootContainerAndContinue(survivor, container, state),
                onFailed: () =>
                {
                    // container is already in Visited, so moving to the next nearest one won't retry this unreachable spot.
                    // Poisons immediately since StartWalkingWithRecovery has already exhausted its full recovery ladder by this point.
                    BasePlayer liveNpc = survivor.Player;

                    if (liveNpc == null || liveNpc.IsDestroyed)
                    {
                        ContinueLootTask(survivor, state);
                        return;
                    }

                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach '{container.ShortPrefabName}' even after trying to recover - skipping it.");
                    PoisonAreaNow(survivor, state, liveNpc.transform.position, container);
                    ContinueLootTask(survivor, state);
                });

            return;
        }

        if (preferBarrelOverOthers)
        {
            // Reached either via the proximity-priority check, or as the original fallback once every higher tier came up empty.
            // Shares LootContainerAndContinue with the direct-loot tier; only which tier is preferred first differs.
            state.Visited.Add(barrel.net.ID);
            ClaimLootTarget(state, barrel.net.ID);

            Vector3 barrelApproachPoint = GetApproachPoint(barrel, npc);

            StartWalkingWithRecovery(
                survivor,
                barrelApproachPoint,
                onArrived: () => LootContainerAndContinue(survivor, barrel, state),
                onFailed: () =>
                {
                    // See the container onFailed above for the same
                    // reasoning - re-fetch fresh, poison immediately.
                    BasePlayer liveNpc = survivor.Player;

                    if (liveNpc == null || liveNpc.IsDestroyed)
                    {
                        ContinueLootTask(survivor, state);
                        return;
                    }

                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach '{barrel.ShortPrefabName}' even after trying to recover - skipping it.");
                    PoisonAreaNow(survivor, state, liveNpc.transform.position, barrel);
                    ContinueLootTask(survivor, state);
                });

            return;
        }

        // Reached either because it won the proximity-priority check, or as the last resort when nothing else was found.
        state.Visited.Add(collectible.net.ID);
        ClaimLootTarget(state, collectible.net.ID);

        Vector3 collectibleApproachPoint = GetApproachPoint(collectible, npc);

        // Uses plain StartWalking with a short timeout instead of the full stuck-recovery ladder, since a collectible is low-value and not worth much recovery effort.
        StartWalking(
            survivor,
            collectibleApproachPoint,
            onArrived: () => PickupCollectibleAndContinue(survivor, collectible, state),
            onFailed: () =>
            {
                BasePlayer liveNpc = survivor.Player;

                if (liveNpc == null || liveNpc.IsDestroyed)
                {
                    ContinueLootTask(survivor, state);
                    return;
                }

                VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach a collectible within {CollectibleWalkTimeoutSeconds:F0}s - skipping it.");
                PoisonAreaNow(survivor, state, liveNpc.transform.position);
                ContinueLootTask(survivor, state);
            },
            maxSeconds: CollectibleWalkTimeoutSeconds);
    }

    // Deliberately much shorter than the default 200s walk timeout, matching the plain StartWalking call above.
    private const float CollectibleWalkTimeoutSeconds = 2f;

    /// <summary>
    /// How far to look for any real road when the immediate area has nothing left to loot.
    /// </summary>
    private const float RoadSearchDetectionRadius = 60f;

    /// <summary>
    /// Wider last-resort road search radius, tried only once RoadSearchDetectionRadius finds nothing, so a survivor isn't left permanently idle.
    /// </summary>
    private const float RoadSearchFallbackRadius = 800f;

    /// <summary>
    /// Cumulative distances (metres) tried, one per road-following escalation, before giving up. ContinueLootTask's normal radius scan still runs at each new point, on or off the road.
    /// </summary>
    private static readonly float[] RoadFollowDistances = { 20f, 50f, 75f, 150f };

    /// <summary>
    /// Called once ContinueLootTask's normal radius scan finds nothing nearby. Checks for a nearby road and walks further along it before giving up, re-finding the nearest road point and direction fresh each call.
    /// </summary>
    private void EscalateSearchAlongRoad(Survivor survivor, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.None;
            return;
        }

        if (state.RoadFollowAttempts >= RoadFollowDistances.Length)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' followed the road as far as it's going to ({RoadFollowDistances[^1]:F0}m total) and found nothing more.");
            EscalateSearchToKnownMonument(survivor, state);
            return;
        }

        if (!_engine.NavigationManager.TryFindNearestRoadPoint(npc.transform.position, RoadSearchDetectionRadius, out _, out PathInterpolator road, out float distanceAlongRoad))
        {
            // Last resort before giving up: walks to the nearest point on any road this wider search finds, then resumes the ordinary loot search from there.
            if (!_engine.NavigationManager.TryFindNearestRoadPoint(npc.transform.position, RoadSearchFallbackRadius, out Vector3 fallbackPoint, out PathInterpolator fallbackRoad, out float fallbackDistanceAlongRoad))
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' found nothing left to loot nearby, and no road within {RoadSearchFallbackRadius:F0}m to follow either.");
                EscalateSearchToKnownMonument(survivor, state);
                return;
            }

            // Same contention nudge as the normal hop path below - see
            // _roadHopClaims' own doc comment.
            if (IsRoadDestinationContested(fallbackPoint, survivor.Character.Id))
            {
                bool fallbackForward = fallbackRoad.Length - fallbackDistanceAlongRoad >= fallbackDistanceAlongRoad;
                float fallbackOffset = UnityEngine.Random.Range(RoadHopContentionOffsetMin, RoadHopContentionOffsetMax);
                float nudgedDistance = Mathf.Clamp(
                    fallbackForward ? fallbackDistanceAlongRoad + fallbackOffset : fallbackDistanceAlongRoad - fallbackOffset,
                    0f,
                    fallbackRoad.Length);
                fallbackPoint = fallbackRoad.GetPoint(nudgedDistance);
            }

            _roadHopClaims[survivor.Character.Id] = fallbackPoint;

            Vector3 fallbackDestination = fallbackPoint;

            if (_engine.NavigationManager.TryFindGroundBelow(fallbackPoint + Vector3.up * 4f, 4f, 6f, out float fallbackGroundY, out _))
            {
                fallbackDestination.y = fallbackGroundY;
            }

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' found nothing left to loot nearby, and no road within the normal {RoadSearchDetectionRadius:F0}m range - heading for the nearest road it can find at all ({Vector3.Distance(npc.transform.position, fallbackPoint):F0}m away).");

            // StartLongDistanceWalkDirect, not plain StartWalkingWithRecovery
            // (2026-08-15, same fix as the normal hop branch below) - see
            // that branch's own comment for why.
            StartLongDistanceWalkDirect(
                survivor,
                fallbackDestination,
                onArrived: () => ContinueLootTask(survivor, state),
                onFailed: () =>
                {
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach the nearest distant road either.");
                    EscalateSearchToKnownMonument(survivor, state);
                });

            return;
        }

        bool forward = road.Length - distanceAlongRoad >= distanceAlongRoad;
        float hopDistance = RoadFollowDistances[state.RoadFollowAttempts];
        state.RoadFollowAttempts++;

        float targetDistance = Mathf.Clamp(
            forward ? distanceAlongRoad + hopDistance : distanceAlongRoad - hopDistance,
            0f,
            road.Length);

        Vector3 roadPoint = road.GetPoint(targetDistance);

        // If another survivor's already heading for roughly this same
        // spot, nudge further along the road instead of converging on it
        // together - stopgap per Lucas's explicit request, deliberately
        // NOT meant to be the permanent design (see _roadHopClaims' own
        // doc comment). Only tries once, not a loop - good enough to
        // break up the common "two bots ran out of loot near each other"
        // case without turning this into its own escalation chain.
        if (IsRoadDestinationContested(roadPoint, survivor.Character.Id))
        {
            float contentionOffset = UnityEngine.Random.Range(RoadHopContentionOffsetMin, RoadHopContentionOffsetMax);
            targetDistance = Mathf.Clamp(
                forward ? targetDistance + contentionOffset : targetDistance - contentionOffset,
                0f,
                road.Length);
            roadPoint = road.GetPoint(targetDistance);
        }

        _roadHopClaims[survivor.Character.Id] = roadPoint;

        // Real ground height at the road point via a downward raycast,
        // not the raw spline Y - same reasoning ComputeApproachPoint's
        // own doc comment gives for containers on elevated platforms;
        // roads are usually already close to real ground, but this is
        // the established pattern for "trust a computed point's Y",
        // not this project's own guess.
        Vector3 destination = roadPoint;

        if (_engine.NavigationManager.TryFindGroundBelow(roadPoint + Vector3.up * 4f, 4f, 6f, out float groundY, out _))
        {
            destination.y = groundY;
        }

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' found nothing left to loot nearby - following the road {hopDistance:F0}m further to look for more ({state.RoadFollowAttempts}/{RoadFollowDistances.Length}).");

        // Uses StartLongDistanceWalkDirect so en-route loot is scanned for during this long road-hop travel, same as the other escalation steps.
        StartLongDistanceWalkDirect(
            survivor,
            destination,
            onArrived: () => ContinueLootTask(survivor, state),
            onFailed: () =>
            {
                // The attempt still counts as used; re-running the scan from wherever the survivor ended up either finds something or escalates to the next hop.
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach the next point along the road - trying the next stretch.");
                ContinueLootTask(survivor, state);
            });
    }

    /// <summary>
    /// Counts a failed loot attempt toward the current streak, poisoning the area once ConsecutiveFailuresBeforeAvoidingArea is hit. Used for lighter failures that skip the full stuck-recovery escalation. container is optional and registers a retry attempt when given.
    /// </summary>
    private void RegisterLootFailure(Survivor survivor, LootTaskState state, Vector3 position, StorageContainer container = null)
    {
        RegisterContainerRetry(state, container);

        state.ConsecutiveFailures++;

        if (state.ConsecutiveFailures < ConsecutiveFailuresBeforeAvoidingArea)
        {
            return;
        }

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' had {ConsecutiveFailuresBeforeAvoidingArea} loot failures in a row.");
        PoisonAreaNow(survivor, state, position);
    }

    /// <summary>
    /// Poisons the area immediately, no streak needed, since a walk that already exhausted the full stuck-recovery escalation is strong evidence the area is bad. container is optional and registers a retry attempt when given.
    /// </summary>
    private void PoisonAreaNow(Survivor survivor, LootTaskState state, Vector3 position, StorageContainer container = null)
    {
        RegisterContainerRetry(state, container);

        float expiresAt = UnityEngine.Time.realtimeSinceStartup + PoisonedZoneDuration;

        state.PoisonedZones.Add((position, PoisonedZoneRadius, expiresAt));
        state.ConsecutiveFailures = 0;

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' avoiding the area around {position} ({PoisonedZoneRadius:F0}m) for the next {PoisonedZoneDuration:F0}s.");

        // Also feeds this into the permanent, monument-relative avoid-zone system, which is remembered for every survivor and never expires (LivingRust.MonumentAvoidZones.cs).
        RecordPotentialAvoidZone(position);
    }

    /// <summary>
    /// Marks container as eligible for exactly one retry once RetrySuccessesBeforeRevisit other containers have been successfully looted (see AdvanceRetryCountdown). A container that already used its one retry is left permanently skipped. No-op if container is null.
    /// </summary>
    private void RegisterContainerRetry(LootTaskState state, StorageContainer container)
    {
        if (container == null)
        {
            return;
        }

        if (state.AlreadyRetried.Contains(container.net.ID))
        {
            // The one retry already failed too; re-add to Visited for permanent exclusion this time.
            state.Visited.Add(container.net.ID);
            return;
        }

        state.PendingRetry[container.net.ID] = RetrySuccessesBeforeRevisit;
        state.AlreadyRetried.Add(container.net.ID);
    }

    /// <summary>
    /// Called after every successfully completed container. Counts down containers waiting on a retry and un-skips (removes from Visited) any that reach zero, so the next search can pick them up again.
    /// </summary>
    private void AdvanceRetryCountdown(LootTaskState state)
    {
        if (state.PendingRetry.Count == 0)
        {
            return;
        }

        List<NetworkableId> ready = null;

        foreach (NetworkableId id in new List<NetworkableId>(state.PendingRetry.Keys))
        {
            int remaining = --state.PendingRetry[id];

            if (remaining <= 0)
            {
                (ready ??= new List<NetworkableId>()).Add(id);
            }
        }

        if (ready == null)
        {
            return;
        }

        foreach (NetworkableId id in ready)
        {
            state.PendingRetry.Remove(id);
            state.Visited.Remove(id);
        }
    }

    private bool IsInPoisonedZone(Vector3 position, LootTaskState state)
    {
        float now = UnityEngine.Time.realtimeSinceStartup;

        foreach ((Vector3 center, float radius, float expiresAt) in state.PoisonedZones)
        {
            if (expiresAt > now && Vector3.Distance(position, center) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How close a candidate container needs to be to junkpile_j's van collider to count as inside/right next to it and get excluded.
    /// </summary>
    private const float JunkpileJVanAvoidRadius = 6f;

    /// <summary>
    /// Rejects any container physically near junkpile_j's van, since that variant's barrels/crates sit inside the van model rather than on the ground, causing bots to get stuck reaching them. Checks physical proximity to the van's collider rather than its parent chain, since spawned loot isn't parented to the junkpile.
    /// </summary>
    private bool IsNearJunkpileJVan(Vector3 position)
    {
        Collider[] hits = Physics.OverlapSphere(position, JunkpileJVanAvoidRadius, ~0, QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            if (hit.gameObject.name.IndexOf("van_d_white", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How close a candidate needs to be to real powerline-tower geometry to count as physically at/on this tower.
    /// </summary>
    private const float PowerlineTowerProximityRadius = 8f;

    /// <summary>
    /// Once a powerline tower is confirmed occupied by another survivor, how wide an area around its root position gets excluded from every other survivor's loot search.
    /// </summary>
    private const float PowerlineOccupancyRadius = 20f;

    /// <summary>
    /// Finds the real powerline-tower structure (if any) physically at position, returning its stable per-instance root position. Powerline towers aren't registered Rust monuments, so this matches by "powerline" substring on the collider's root GameObject name instead.
    /// </summary>
    private bool TryFindPowerlineTowerRoot(Vector3 position, out Vector3 towerRootPosition)
    {
        Collider[] hits = Physics.OverlapSphere(position, PowerlineTowerProximityRadius, ~0, QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            Transform root = hit.transform.root;

            if (root.name.IndexOf("powerline", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                towerRootPosition = root.position;
                return true;
            }
        }

        towerRootPosition = default;
        return false;
    }

    /// <summary>
    /// Radius for the cheap "is anything powerline-related nearby" gate. Kept smaller than LootSearchRadius, since a full-radius unfiltered physics sweep near a dense powerline tower was expensive enough to stall a bot's first search cycle.
    /// </summary>
    private const float PowerlineGateRadius = 25f;

    /// <summary>
    /// Cheap gate for GetOccupiedPowerlineTowers: whether any real powerline-tower geometry exists within radius at all. A single OverlapSphere avoids paying for the more expensive per-survivor occupancy scan when nothing is nearby.
    /// </summary>
    private bool IsAnyPowerlineTowerWithinRadius(Vector3 position, float radius)
    {
        Collider[] hits = Physics.OverlapSphere(position, radius, ~0, QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            if (hit.transform.root.name.IndexOf("powerline", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly List<Vector3> NoOccupiedPowerlineTowers = new();

    /// <summary>
    /// Every distinct powerline tower another currently-spawned survivor is physically at right now, computed once per cycle and reused across candidate filters. Prevents multiple bots converging on the same tower. Uses a cheap gate to skip the per-survivor scan when no tower is nearby, avoiding an O(bots^2) cost.
    /// </summary>
    private List<Vector3> GetOccupiedPowerlineTowers(Survivor self, Vector3 selfPosition)
    {
        if (!IsAnyPowerlineTowerWithinRadius(selfPosition, PowerlineGateRadius))
        {
            return NoOccupiedPowerlineTowers;
        }

        List<Vector3> occupied = new();

        foreach (Survivor other in _engine.SurvivorManager.GetAll())
        {
            if (other == self)
            {
                continue;
            }

            BasePlayer otherNpc = other.Player;

            if (otherNpc == null || otherNpc.IsDestroyed)
            {
                continue;
            }

            if (TryFindPowerlineTowerRoot(otherNpc.transform.position, out Vector3 towerRootPosition)
                && !occupied.Any(existing => Vector3.Distance(existing, towerRootPosition) < 1f))
            {
                occupied.Add(towerRootPosition);
            }
        }

        return occupied;
    }

    private bool IsNearOccupiedPowerlineTower(Vector3 position, List<Vector3> occupiedPowerlineTowers)
    {
        return occupiedPowerlineTowers.Any(towerPosition => Vector3.Distance(position, towerPosition) <= PowerlineOccupancyRadius);
    }

    /// <summary>
    /// How far out to avoid any loot near a real CardReader (green/blue/
    /// red keycard swipe) - a stopgap per Lucas's explicit request until
    /// real puzzle-solving exists (see the Monuments roadmap entry: a
    /// bot can't yet check its own keycard tier, walk to the reader, and
    /// swipe it). Without this, a bot repeatedly walks toward loot sitting
    /// behind a locked puzzle door it structurally cannot ever open right
    /// now, fails, and either gets stuck cycling through the normal stuck-
    /// recovery chain or burns through the ordinary (temporary)
    /// PoisonAreaNow cycle over and over on the same permanently-
    /// unreachable spot. 20m is Lucas's own number - generous enough to
    /// cover a whole puzzle room, not just the door itself.
    /// </summary>
    private const float CardReaderAvoidRadius = 20f;

    // CardReader's own collider sits on the "World" layer (confirmed via
    // a live /lr.debug.scan at the Ferry Terminal monument's puzzle room).
    private static readonly int CardReaderLayerMask = LayerMask.GetMask("World");

    /// <summary>
    /// Whether position is within CardReaderAvoidRadius of a real
    /// CardReader - deliberately NOT the temporary PoisonedZone mechanism
    /// (LootTaskState.PoisonedZones, which expires after
    /// PoisonedZoneDuration and is per-survivor memory only). This is a
    /// live check against real CardReader entities every single search
    /// cycle, for every survivor - functionally permanent ("indefinitely,
    /// for now" per Lucas's own framing) without needing any expiry or
    /// state to track at all, and shared automatically across every bot
    /// rather than each one having to independently rediscover the same
    /// unreachable puzzle room.
    /// </summary>
    private bool IsNearCardReader(Vector3 position)
    {
        Collider[] hits = Physics.OverlapSphere(position, CardReaderAvoidRadius, CardReaderLayerMask, QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            if (hit.GetComponentInParent<CardReader>() != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Alternate angles (degrees, around the container's own center) to
    /// try when the closest-side approach looks obstructed - 0 is the
    /// closest side itself (already tried before this list is consulted),
    /// the rest fan out to cover the remaining sides roughly evenly.
    /// </summary>
    private static readonly float[] AlternateApproachAngles = { 90f, -90f, 45f, -45f, 135f, -135f, 180f };

    /// <summary>
    /// A point just outside a container's own bounds, on a side that
    /// looks clear of solid obstruction - not always the side closest to
    /// fromPosition. The closest side is tried first (cheap, usually
    /// fine), but a live report showed bots repeatedly oscillating in
    /// place for over a minute near junkpile-style clutter, unable to
    /// close the final 1-2m of a walk - the closest side was often
    /// pointed straight into a rock/gravel/junk pile the survivor's
    /// current position happened to be on the wrong side of. Checking a
    /// handful of other angles around the container first (a cheap
    /// Linecast each, not a real path-planning search) and picking one
    /// with a clear line from the survivor's current position meaningfully
    /// reduces the odds of committing to a walk that dead-ends in mess,
    /// without needing genuine route-around-obstacles pathfinding - that
    /// remains real future work if this still isn't enough in practice.
    /// StartWalking's own stuck-detection is still the final safety net
    /// either way.
    /// </summary>
    // Real melee reach is short - BaseMelee.maxDistance defaults to 1.5f
    // and AttackEntity.effectiveRange to 1f (confirmed via decompiling
    // Assembly-CSharp.dll) - a real player stands close to swing, not
    // 0.6m+ back from the container's own bounds. Combined with
    // WaypointArriveDistance's own tolerance on top, the old 0.6f read as
    // roughly half a metre too far in a live test. Still enough standoff
    // for the bot's own collision radius, just not the extra padding a
    // real swing never needed.
    private const float ContainerApproachStandoffDistance = 0.25f;

    private Vector3 GetApproachPoint(BaseEntity entity, BasePlayer npc, float standoffDistance = ContainerApproachStandoffDistance)
    {
        Vector3 fromPosition = npc.transform.position;
        OBB bounds = entity.WorldSpaceBounds();

        Vector3 closestSide = ComputeApproachPoint(entity, bounds, fromPosition, standoffDistance);

        if (IsPathClear(fromPosition, closestSide))
        {
            return SnapApproachPointToNavMesh(npc, closestSide);
        }

        foreach (float angle in AlternateApproachAngles)
        {
            Vector3 rotatedReference = RotatePointAround(fromPosition, bounds.position, angle);
            Vector3 candidate = ComputeApproachPoint(entity, bounds, rotatedReference, standoffDistance);

            if (IsPathClear(fromPosition, candidate))
            {
                return SnapApproachPointToNavMesh(npc, candidate);
            }
        }

        // Nothing looked clear from any angle - fall back to the closest
        // side anyway rather than refusing to try at all. StartWalking's
        // stuck-detection (onFailed) still catches a genuinely bad pick.
        return SnapApproachPointToNavMesh(npc, closestSide);
    }

    // Tight-then-generous two-tier radius, exactly matching real Scientist2
    // AI's own destination-picking pattern (confirmed via decompiling
    // Assembly-CSharp.dll's State_ScientistRush.GetMoveDestination).
    private const float ApproachPointNavMeshSnapTightRadius = 3.5f;
    private const float ApproachPointNavMeshSnapWideRadius = 20f;

    // Guards against a wide-radius navmesh sample landing on a different vertical layer (e.g. a cave/tunnel below a surface point). Anything beyond this vertical delta is treated as a different layer, not a ground-height correction.
    private const float ApproachPointMaxVerticalSnapDelta = 6f;

    /// <summary>
    /// Snaps a computed approach point onto the baked navmesh, since a geometrically valid point can still land somewhere the navmesh doesn't cover. Includes a vertical sanity check so it doesn't snap to a different layer. Falls back to the original unsnapped point if nothing suitable is found nearby.
    /// </summary>
    private Vector3 SnapApproachPointToNavMesh(BasePlayer npc, Vector3 worldPosition)
    {
        EnsureNativeNavAgent(npc, out RustNavMeshAgent agent, out _);

        Vector3 positionNS = npc.WorldToNavMeshSpace.MultiplyPoint(worldPosition);

        if (TryGetSameLevelNavMeshSnap(npc, agent, positionNS, worldPosition, ApproachPointNavMeshSnapTightRadius, out Vector3 tightSnap))
        {
            return tightSnap;
        }

        if (TryGetSameLevelNavMeshSnap(npc, agent, positionNS, worldPosition, ApproachPointNavMeshSnapWideRadius, out Vector3 wideSnap))
        {
            return wideSnap;
        }

        return worldPosition;
    }

    /// <summary>
    /// One radius attempt for SnapApproachPointToNavMesh, applying the vertical-layer rejection to both the tight and wide radius passes.
    /// </summary>
    private bool TryGetSameLevelNavMeshSnap(BasePlayer npc, RustNavMeshAgent agent, Vector3 positionNS, Vector3 originalWorldPosition, float radius, out Vector3 result)
    {
        result = default;

        if (!agent.SamplePosition(positionNS, out NavMeshHit hitNS, radius))
        {
            return false;
        }

        Vector3 candidateWorldPosition = npc.NavMeshToWorldSpace.MultiplyPoint(hitNS.position);

        if (Mathf.Abs(candidateWorldPosition.y - originalWorldPosition.y) > ApproachPointMaxVerticalSnapDelta)
        {
            return false;
        }

        result = candidateWorldPosition;
        return true;
    }

    /// <summary>
    /// Computes closest-point-on-bounds plus standoff plus real ground height, parameterized by the reference position "closest" is measured from. GetApproachPoint calls this for the survivor's position and each rotated candidate angle.
    /// </summary>
    private Vector3 ComputeApproachPoint(BaseEntity entity, OBB bounds, Vector3 referencePosition, float standoffDistance)
    {
        Vector3 closest = bounds.ClosestPoint(referencePosition);

        Vector3 outward = closest - bounds.position;
        outward.y = 0f;

        if (outward.sqrMagnitude < 0.01f)
        {
            outward = referencePosition - entity.transform.position;
            outward.y = 0f;
        }

        if (outward.sqrMagnitude < 0.01f)
        {
            outward = Vector3.forward;
        }

        Vector3 approach = closest + outward.normalized * standoffDistance;

        // Uses a downward raycast for real ground/floor height, since the coarse terrain heightmap would misplace this on an elevated platform.
        if (_engine.NavigationManager.TryFindGroundBelow(approach, 4f, 6f, out float groundY, out _))
        {
            approach.y = groundY;
        }
        else
        {
            approach.y = closest.y;
        }

        return approach;
    }

    private static Vector3 RotatePointAround(Vector3 point, Vector3 pivot, float angleDegrees)
    {
        return pivot + Quaternion.Euler(0f, angleDegrees, 0f) * (point - pivot);
    }

    /// <summary>
    /// Cheap sanity check for whether a straight line between two points is free of solid obstruction, used to pre-screen candidate approach angles before committing a walk to one.
    /// </summary>
    private bool IsPathClear(Vector3 from, Vector3 to)
    {
        return !Physics.Linecast(from + Vector3.up * 0.5f, to + Vector3.up * 0.5f, LineOfSightBlockingMask, QueryTriggerInteraction.Ignore);
    }

    private void LootContainerAndContinue(Survivor survivor, StorageContainer container, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.None;
            return;
        }

        if (container == null || container.IsDestroyed || container.inventory == null)
        {
            ContinueLootTask(survivor, state);
            return;
        }

        if (!RequiresDestructionToLoot(container))
        {
            LootContainerDirectly(survivor, container, state);
            return;
        }

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' is breaking open '{container.ShortPrefabName}'.");

        StartAttackingContainerWithReposition(
            survivor,
            container,
            onSuccess: () =>
            {
                state.ConsecutiveFailures = 0;
                AdvanceRetryCountdown(state);
                ContinueLootTask(survivor, state);
            },
            onFailed: () =>
            {
                // Re-fetches the player fresh, since it may have died or despawned during repositioning.
                BasePlayer liveNpc = survivor.Player;

                if (liveNpc == null || liveNpc.IsDestroyed)
                {
                    ContinueLootTask(survivor, state);
                    return;
                }

                RegisterLootFailure(survivor, state, liveNpc.transform.position, container);
                ContinueLootTask(survivor, state);
            });
    }

    /// <summary>
    /// Whether this container needs to be destroyed to get at its contents. Barrels and roadsigns must be broken open; crates are opened and looted intact. Matched by shortname substring rather than an exhaustive list.
    /// </summary>
    private bool RequiresDestructionToLoot(StorageContainer container)
    {
        string name = container.ShortPrefabName;

        return name.IndexOf("barrel", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("roadsign", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Roadsign HP is high enough that mining one with just the starting rock takes too long, leaving a bot exposed. Requires a real tool for roadsigns specifically; barrels stay rock-eligible.
    /// </summary>
    private bool IsRoadsign(StorageContainer container)
    {
        return container.ShortPrefabName.IndexOf("roadsign", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// A vehicle or deployable's own fuel tank. Excluded since it's a real StorageContainer that would otherwise pass every loot filter, but isn't free-standing world loot. Matched by shortname substring.
    /// </summary>
    private bool IsVehicleFuelStorage(StorageContainer container)
    {
        string name = container.ShortPrefabName;

        return name.IndexOf("fuelstorage", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("fuel_storage", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// The hot air balloon's own attached loot container, excluded since it sits inside a dense collider cluster that can wedge a bot in place. IsBlockedByHotAirBalloon below is a separate general safety net.
    /// </summary>
    private bool IsHotAirBalloonStorage(StorageContainer container)
    {
        return container.ShortPrefabName.IndexOf("hab_storage", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// A rowboat's cargo storage, shared across every skin variant including beached wrecks. Excluded the same way as the other vehicle-storage exclusions, since it would otherwise pass every loot filter.
    /// </summary>
    private bool IsRowboatStorage(StorageContainer container)
    {
        return container.ShortPrefabName.IndexOf("rowboat_storage", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// A player-owned mailbox, excluded since it's base furniture tied to a specific player's ownership, not scavengeable loot.
    /// </summary>
    private bool IsMailbox(StorageContainer container)
    {
        return container.ShortPrefabName.IndexOf("mailbox", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Loots a container by opening it directly, no combat or destruction. Since no real loot panel is opened, this calls Kill() itself to replicate the auto-cleanup a real player closing the panel would trigger. Delayed by DirectLootDelay instead of completing instantly, so it doesn't read as unnaturally fast.
    /// </summary>
    private void LootContainerDirectly(Survivor survivor, StorageContainer container, LootTaskState state, Action onDone = null)
    {
        Action resume = onDone ?? (() => ContinueLootTask(survivor, state));

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' is looting '{container.ShortPrefabName}'.");

        timer.Once(DirectLootDelay, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || container == null || container.IsDestroyed || container.inventory == null)
            {
                resume();
                return;
            }

            if (!IsWithinLootRange(npc, container))
            {
                // Verifies actual proximity before looting, not just that a walk reported "arrived". See LootInteractionRange's own doc comment.
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' can't reach '{container.ShortPrefabName}' - too far away ({Vector3.Distance(npc.transform.position, container.transform.position):F1}m, obstacle in the way?). Skipping it.");
                RegisterLootFailure(survivor, state, npc.transform.position, container);
                resume();
                return;
            }

            int moved = TransferAllItems(container.inventory, npc.inventory, npc, out List<string> movedShortnames);

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' looted {moved} item stack(s) from '{container.ShortPrefabName}' (opened, not destroyed): {string.Join(", ", movedShortnames)}.");

            if (moved > 0)
            {
                OnLootObtained(survivor, npc, movedShortnames);
            }

            if (container is LootContainer lootContainer && lootContainer.destroyOnEmpty && container.inventory.itemList.Count == 0)
            {
                container.Kill();
            }

            state.ConsecutiveFailures = 0;
            AdvanceRetryCountdown(state);
            resume();
        });
    }

    /// <summary>
    /// Loots a real corpse (player, scientist/NPC, or animal), paced one item at a time across up to 3 containers. See LootMultiContainerEntityAndContinue for the shared pacing logic.
    /// </summary>
    private void LootCorpseAndContinue(Survivor survivor, LootableCorpse corpse, LootTaskState state)
    {
        List<ItemContainer> containers = corpse.containers != null
            ? new List<ItemContainer>(corpse.containers)
            : new List<ItemContainer>();

        LootMultiContainerEntityAndContinue(survivor, corpse, containers, "a corpse", state);
    }

    /// <summary>
    /// Loots a dropped bag, which a destroyed or despawned body converts into. A separate entity class from StorageContainer and LootableCorpse, but shares the same paced per-item loot logic with a single container.
    /// </summary>
    private void LootDroppedItemContainerAndContinue(Survivor survivor, DroppedItemContainer bag, LootTaskState state)
    {
        LootMultiContainerEntityAndContinue(survivor, bag, new List<ItemContainer> { bag.inventory }, "a dropped bag", state);
    }

    /// <summary>
    /// A standalone loose item (a fifth real loot-source class, see
    /// TryFindNearestDroppedItem's own doc comment) - Lucas's own explicit
    /// framing, 2026-08-14: "pickup everything... except a few specific
    /// items... prioritise them in the inventory," meaning this should use
    /// exactly the same real decision TryTransferSingleItem already makes
    /// for every item pulled from a corpse or bag (IsNeverLootItem/
    /// ShouldSkipDuplicateItem/ShouldSkipInferiorArmor), not a separate
    /// bespoke rule - and OnLootObtained afterward is the same real
    /// equip-priority pass that already decides whether a looted weapon/
    /// armor piece actually gets worn/wielded. No per-item pacing timer
    /// needed here (unlike LootMultiContainerEntityAndContinue) since
    /// there's only ever the one real Item to consider.
    /// </summary>
    private void PickupDroppedItemAndContinue(Survivor survivor, DroppedItem droppedItem, LootTaskState state, Action onDone = null)
    {
        Guid characterId = survivor.Character.Id;
        Action resume = onDone ?? (() => ContinueLootTask(survivor, state));

        CancelActiveAttack(characterId);

        float pickupDelay = DirectLootDelay;

        if (droppedItem?.item != null && IsBackpackItem(droppedItem.item))
        {
            // A backpack off the ground is a timed (3-5s) pickup, same as a
            // real player's (2026-09-21).
            pickupDelay += UnityEngine.Random.Range(3f, 5f);
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' is picking up a dropped backpack (takes a few seconds).");
        }

        timer.Once(pickupDelay, () =>
        {
            // See LootMultiContainerEntityAndContinue's own identical guard
            // for why this exact gap needs its own check - combat starting
            // during this delay has nothing registered yet to cancel it.
            if (_activeCombat.ContainsKey(characterId))
            {
                return;
            }

            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || droppedItem == null || droppedItem.IsDestroyed || droppedItem.item == null)
            {
                resume();
                return;
            }

            if (!IsWithinLootRange(npc, droppedItem))
            {
                // Real live gap found 2026-08-15 - this line never
                // identified WHAT the dropped item actually was or where,
                // so a live report of "it tried to loot something and
                // failed" was impossible to verify against what was
                // actually visible in-game. Now logs the real shortname
                // and position.
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' can't reach a dropped item ('{droppedItem.item.info.shortname}' at {droppedItem.transform.position}) - too far away ({Vector3.Distance(npc.transform.position, droppedItem.transform.position):F1}m, obstacle in the way?). Skipping it.");
                RegisterLootFailure(survivor, state, npc.transform.position);
                resume();
                return;
            }

            Item item = droppedItem.item;
            string shortname = item.info.shortname;

            if (TryTransferSingleItem(item, npc.inventory, npc))
            {
                // The real Item now belongs to the survivor's inventory -
                // RemoveItem() just clears the world entity's own reference
                // to it (doesn't touch the Item itself, matching the real
                // Pickup(RPCMessage) flow this mirrors), then the now-empty
                // world prop is cleaned up explicitly rather than left
                // behind as inert clutter.
                droppedItem.RemoveItem();
                droppedItem.Kill();

                VerbosePuts($"loot-task: '{survivor.Character.Alias}' picked up a dropped '{shortname}'.");
                OnLootObtained(survivor, npc, new List<string> { shortname });
            }
            else
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' left a dropped '{shortname}' - not worth picking up.");

                // Real live infinite loop (2026-08-28, Lucas's own report:
                // 'ScrappyWeasel' stuck endlessly re-detouring to the same
                // dropped pickaxe on a long walk). TryFindEnRouteLootCandidate
                // (below) already excludes anything RecentlyFailedEnRouteLoot,
                // and MarkEnRouteLootFailure already exists for exactly this
                // purpose - but was only ever called from the walk's own
                // onFailed (couldn't physically reach it). Reaching the item
                // and then rejecting it (TryTransferSingleItem returning
                // false - already holding something better, inventory full,
                // etc.) was never treated as a failure at all, so the exact
                // same dropped item stayed a valid en-route candidate
                // forever: detour to it, reject it, resume the original
                // walk, immediately re-spot the same item still sitting
                // right there, detour again. Marking it here breaks the
                // loop the same way an unreachable item already does - the
                // per-item state/detourState this fires from gets discarded
                // either way (see StartLongDistanceWalkDirect's own doc
                // comment on why detourState is throwaway), so this is the
                // only place a rejection can actually be remembered.
                MarkEnRouteLootFailure(droppedItem.net.ID);
            }

            state.ConsecutiveFailures = 0;
            AdvanceRetryCountdown(state);
            resume();
        });
    }

    /// <summary>
    /// Hemp, corn, pumpkin, mushroom, small stone/metal/sulfur surface
    /// deposits, fallen wood - all the same real CollectibleEntity class
    /// (see TryFindNearestCollectible's own doc comment), lowest priority
    /// of every loot source this project searches. DoPickup is CollectibleEntity's
    /// own real, public method (confirmed via decompile) - it already
    /// creates the right items, gives them to the receiver, plays the real
    /// pickup effect, and kills itself, so there's no manual item-transfer
    /// logic needed here the way DroppedItem's own pickup needed (no
    /// never-loot/duplicate/inferior-armor concept applies to raw gathered
    /// materials the way it does for a found weapon or armor piece).
    /// Shortnames captured before DoPickup runs since it nils out itemList
    /// as part of its own real cleanup.
    /// </summary>
    private void PickupCollectibleAndContinue(Survivor survivor, CollectibleEntity collectible, LootTaskState state, Action onDone = null)
    {
        Guid characterId = survivor.Character.Id;
        Action resume = onDone ?? (() => ContinueLootTask(survivor, state));

        CancelActiveAttack(characterId);

        timer.Once(DirectLootDelay, () =>
        {
            if (_activeCombat.ContainsKey(characterId))
            {
                return;
            }

            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || collectible == null || collectible.IsDestroyed || collectible.itemList == null || collectible.itemList.Length == 0)
            {
                resume();
                return;
            }

            if (!IsWithinLootRange(npc, collectible))
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' can't reach a collectible - too far away ({Vector3.Distance(npc.transform.position, collectible.transform.position):F1}m, obstacle in the way?). Skipping it.");
                RegisterLootFailure(survivor, state, npc.transform.position);
                resume();
                return;
            }

            List<string> shortnames = collectible.itemList
                .Where(itemAmount => itemAmount.itemDef != null)
                .Select(itemAmount => itemAmount.itemDef.shortname)
                .ToList();

            collectible.DoPickup(npc);

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' gathered from a collectible: {string.Join(", ", shortnames)}.");

            if (shortnames.Count > 0)
            {
                OnLootObtained(survivor, npc, shortnames);
            }

            state.ConsecutiveFailures = 0;
            AdvanceRetryCountdown(state);
            resume();
        });
    }

    // Detection radius for real loot found basically on the way during a long-distance walk. Smaller than LootSearchRadius since this isn't a detour search.
    private const float EnRouteLootDetectionRadius = 25f;

    // Collectibles get a tighter en-route radius than containers/dropped items, since they're low-value enough to only be worth a detour when genuinely close to the path. This is the outer ceiling; per-type cutoffs are enforced in the filter (see GetCollectibleDivertRadius).
    private const float EnRouteCollectibleDetectionRadius = 15f;

    // How often a long walk re-checks for something worth grabbing nearby. A periodic sweep instead of every tick keeps physics query cost down at scale.
    private const float EnRouteLootScanIntervalSeconds = 4f;

    // Short and skips the full stuck-recovery ladder, since an awkward en-route detour isn't worth fighting the geometry over.
    private const float EnRouteLootWalkTimeoutSeconds = 6f;

    // Detection radius for en-route tree/ore gathering, tighter than EnRouteLootDetectionRadius since a full gather is a bigger time investment than grabbing a container.
    private const float EnRouteResourceNodeDetectionRadius = 20f;

    // Caps stops per walk so a survivor doesn't stop at every stone ore or tree along the way. Reset in StartLongDistanceWalk, not StartLongDistanceWalkDirect, since the latter is also called by each detour's own resume.
    private const int EnRouteResourceNodeMaxStopsPerWalk = 2;

    private readonly Dictionary<Guid, int> _enRouteTreeStopsThisWalk = new();
    private readonly Dictionary<Guid, int> _enRouteOreStopsThisWalk = new();

    /// <summary>
    /// How long a failed en-route detour target stays excluded from re-selection, preventing the scan from repeatedly re-picking the same unreachable candidate.
    /// </summary>
    private const float EnRouteLootFailureCooldownSeconds = 90f;

    private readonly Dictionary<NetworkableId, float> _enRouteLootFailures = new();

    private bool RecentlyFailedEnRouteLoot(NetworkableId id)
    {
        if (_enRouteLootFailures.TryGetValue(id, out float expiresAt))
        {
            if (expiresAt > UnityEngine.Time.realtimeSinceStartup)
            {
                return true;
            }

            _enRouteLootFailures.Remove(id);
        }

        return false;
    }

    private void MarkEnRouteLootFailure(NetworkableId id)
    {
        _enRouteLootFailures[id] = UnityEngine.Time.realtimeSinceStartup + EnRouteLootFailureCooldownSeconds;
    }

    private readonly Dictionary<Guid, Timer> _enRouteLootScanTimers = new();

    private void StopEnRouteLootScan(Guid characterId)
    {
        if (_enRouteLootScanTimers.TryGetValue(characterId, out Timer scanTimer))
        {
            scanTimer?.Destroy();
            _enRouteLootScanTimers.Remove(characterId);
        }
    }

    // Number of sample points along the straight line to the destination, checking each for water. Coarse by design, just enough to catch a real crossing.
    private const int WaterCrossingSampleCount = 12;

    /// <summary>
    /// Whether the straight line from -> to passes through water at any sampled point, the trigger for routing via roads instead of walking direct. A cheap straight-line heuristic, not real water-body geometry.
    /// </summary>
    private bool DoesPathCrossWater(Vector3 from, Vector3 to)
    {
        for (int i = 1; i < WaterCrossingSampleCount; i++)
        {
            Vector3 sample = Vector3.Lerp(from, to, i / (float)WaterCrossingSampleCount);

            if (_engine.NavigationManager.IsWater(sample))
            {
                return true;
            }
        }

        return false;
    }

    // How far to search for a road to route via once a water crossing is detected. Generous, for real cross-map travel, and separate from the tighter RoadSearchDetectionRadius used for loot-search escalation.
    private const float RoadRouteDetectionRadius = 300f;

    // How far to advance along the road spline per hop while routing around water.
    private const float RoadRouteHopDistance = 80f;

    // Hard cap on road hops before accepting the direct route anyway, as a termination guarantee for a destination that can't be reached by hopping this road network.
    private const int RoadRouteMaxHops = 15;

    /// <summary>
    /// Routes a survivor around a water crossing by hopping along the nearest road network, re-checking each hop whether the direct line to finalDestination has cleared, and breaking off onto it once it has.
    /// </summary>
    private void StartRoadRouteToward(Survivor survivor, Vector3 finalDestination, string destinationLabel, PathInterpolator road, float distanceAlongRoad, Action onArrived, Action onFailed, int hopsRemaining)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            onFailed?.Invoke();
            return;
        }

        if (hopsRemaining <= 0)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' exhausted its road-routing hops trying to get around water toward {destinationLabel} - continuing the direct route regardless.");
            StartLongDistanceWalkDirect(survivor, finalDestination, onArrived, onFailed);
            return;
        }

        if (!DoesPathCrossWater(npc.transform.position, finalDestination))
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' has a clear line to {destinationLabel} now - breaking off the road route for the direct approach.");
            StartLongDistanceWalkDirect(survivor, finalDestination, onArrived, onFailed);
            return;
        }

        float forwardDistance = Mathf.Min(distanceAlongRoad + RoadRouteHopDistance, road.Length);
        float backwardDistance = Mathf.Max(distanceAlongRoad - RoadRouteHopDistance, 0f);

        Vector3 forwardPoint = road.GetPoint(forwardDistance);
        Vector3 backwardPoint = road.GetPoint(backwardDistance);

        bool goForward = Vector3.Distance(forwardPoint, finalDestination) <= Vector3.Distance(backwardPoint, finalDestination);
        Vector3 nextPoint = goForward ? forwardPoint : backwardPoint;
        float nextDistanceAlongRoad = goForward ? forwardDistance : backwardDistance;

        StartWalkingWithRecovery(
            survivor,
            nextPoint,
            onArrived: () => StartRoadRouteToward(survivor, finalDestination, destinationLabel, road, nextDistanceAlongRoad, onArrived, onFailed, hopsRemaining - 1),
            onFailed: () =>
            {
                // Couldn't reach this hop; falls through to the direct route from wherever the survivor currently is.
                BasePlayer liveNpc = survivor.Player;

                if (liveNpc == null || liveNpc.IsDestroyed)
                {
                    onFailed?.Invoke();
                    return;
                }

                StartLongDistanceWalkDirect(survivor, finalDestination, onArrived, onFailed);
            });
    }

    /// <summary>
    /// Entry point for every long-distance walk in the project. The road-routing detour around water (StartRoadRouteToward, DoesPathCrossWater) is currently disabled due to a hop-selection bug; water crossings are walked through directly instead.
    /// </summary>
    private void StartLongDistanceWalk(Survivor survivor, Vector3 destination, string destinationLabel, Action onArrived, Action onFailed)
    {
        // Resets the per-walk stop counters here, not in StartLongDistanceWalkDirect; see EnRouteResourceNodeMaxStopsPerWalk's own doc comment.
        Guid characterId = survivor.Character.Id;
        _enRouteTreeStopsThisWalk[characterId] = 0;
        _enRouteOreStopsThisWalk[characterId] = 0;

        StartLongDistanceWalkDirect(survivor, destination, onArrived, onFailed);
    }

    /// <summary>
    /// Drop-in replacement for StartWalkingWithRecovery at long-distance call sites. The walk itself is unchanged; this also runs a periodic en-route loot scan alongside it, since short local hops don't need one.
    /// </summary>
    private void StartLongDistanceWalkDirect(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed)
    {
        Guid characterId = survivor.Character.Id;

        StartWalkingWithRecovery(
            survivor,
            destination,
            onArrived: () =>
            {
                StopEnRouteLootScan(characterId);
                onArrived?.Invoke();
            },
            onFailed: () =>
            {
                StopEnRouteLootScan(characterId);
                onFailed?.Invoke();
            });

        StopEnRouteLootScan(characterId);

        Timer scanTimer = null;

        scanTimer = timer.Every(EnRouteLootScanIntervalSeconds, () =>
        {
            BasePlayer npc = survivor.Player;

            // No longer actually walking, so nothing left to scan alongside.
            if (npc == null || npc.IsDestroyed || !_activeMovement.ContainsKey(characterId)
                || _activeCombat.ContainsKey(characterId) || _activeFlee.ContainsKey(characterId))
            {
                StopEnRouteLootScan(characterId);
                return;
            }

            // Checked before the normal small-item en-route detour below, since this is a bigger
            // one - resumes this exact walk once the side quest concludes.
            if (TryPursueTierZeroMonumentSideQuest(survivor, npc, new LootTaskState(),
                    () => StartLongDistanceWalkDirect(survivor, destination, onArrived, onFailed)))
            {
                StopEnRouteLootScan(characterId);
                return;
            }

            if (!TryFindEnRouteLootCandidate(survivor, npc, out BaseEntity candidate, out EnRouteLootKind kind))
            {
                return;
            }

            StopEnRouteLootScan(characterId);

            // Resumes via the direct walk rather than re-checking water crossing, and deliberately does not reset the tree/ore stop counters since this resumes the same walk.
            Action resumeOriginalWalk = () => StartLongDistanceWalkDirect(survivor, destination, onArrived, onFailed);

            // Tree/ore get their own dedicated gather-and-resume path; see GatherEnRouteTreeAndResume/GatherEnRouteOreAndResume's own doc comments.
            if (kind == EnRouteLootKind.Tree)
            {
                _enRouteTreeStopsThisWalk[characterId] = _enRouteTreeStopsThisWalk.GetValueOrDefault(characterId) + 1;
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' spotted a tree on the way - stopping to gather wood ({_enRouteTreeStopsThisWalk[characterId]}/{EnRouteResourceNodeMaxStopsPerWalk} this trip).");
                GatherEnRouteTreeAndResume(survivor, (TreeEntity)candidate, resumeOriginalWalk);
                return;
            }

            if (kind == EnRouteLootKind.OreNode)
            {
                _enRouteOreStopsThisWalk[characterId] = _enRouteOreStopsThisWalk.GetValueOrDefault(characterId) + 1;
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' spotted a stone ore node on the way - stopping to mine it ({_enRouteOreStopsThisWalk[characterId]}/{EnRouteResourceNodeMaxStopsPerWalk} this trip).");
                GatherEnRouteOreAndResume(survivor, (OreResourceEntity)candidate, resumeOriginalWalk);
                return;
            }

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' spotted something worth grabbing on the way - detouring.");

            // Throwaway state so the existing Loot*AndContinue machinery has somewhere to record a failed detour attempt; discarded once the detour resolves.
            LootTaskState detourState = new();

            StartWalking(
                survivor,
                GetApproachPoint(candidate, npc),
                onArrived: () =>
                {
                    switch (kind)
                    {
                        case EnRouteLootKind.Container:
                            LootContainerDirectly(survivor, (StorageContainer)candidate, detourState, resumeOriginalWalk);
                            break;
                        case EnRouteLootKind.DroppedItem:
                            PickupDroppedItemAndContinue(survivor, (DroppedItem)candidate, detourState, resumeOriginalWalk);
                            break;
                        case EnRouteLootKind.Collectible:
                            PickupCollectibleAndContinue(survivor, (CollectibleEntity)candidate, detourState, resumeOriginalWalk);
                            break;
                    }
                },
                onFailed: () =>
                {
                    // candidate can be destroyed/despawned (looted by another survivor, expired)
                    // between being spotted and this detour timing out, well before the walk
                    // itself reports failure - candidate.net is null once that happens.
                    if (candidate != null && !candidate.IsDestroyed)
                    {
                        MarkEnRouteLootFailure(candidate.net.ID);
                    }

                    resumeOriginalWalk();
                },
                maxSeconds: EnRouteLootWalkTimeoutSeconds);
        });

        _enRouteLootScanTimers[characterId] = scanTimer;
    }

    private enum EnRouteLootKind
    {
        Container,
        DroppedItem,
        Tree,
        OreNode,
        Collectible,
    }

    /// <summary>
    /// Real collectable prefabs (confirmed via Bundles\AssetSceneManifest.
    /// json - Rose-Collectable, Orchid-Collectable, Sunflower-Collectable,
    /// Wheat-Collectable, all under autospawn/collectable) Lucas asked to
    /// skip entirely (2026-08-15): "avoid picking flowers of any kind, and
    /// sunflowers... avoid picking up wheat collectable also," no reasoning
    /// given. Matched by ShortPrefabName substring, case-insensitive, same
    /// approach RequiresDestructionToLoot/IsRoadsign already use for their
    /// own shortname matching - "rose"/"orchid" cover "flowers of any
    /// kind" as currently modeled (those are the two real flower
    /// collectable types that exist).
    /// </summary>
    private static bool IsExcludedCollectibleType(CollectibleEntity candidate)
    {
        string name = candidate.ShortPrefabName;

        return name.IndexOf("rose", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("orchid", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("sunflower", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("wheat", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// How far a specific collectible TYPE is worth diverting for
    /// (2026-08-18, Lucas's own explicit figures) - berries are common
    /// enough/low-value enough to only matter within 3m, mushrooms 5m,
    /// and the real gathering-tier resources (hemp/wood/stone/metal ore/
    /// sulfur ore) get a wider 15m since they're worth a small detour.
    /// Matched by ShortPrefabName substring, same approach
    /// IsExcludedCollectibleType already uses - real confirmed prefab
    /// names via AssetSceneManifest.json: Berry-Red/Blue/Green/Black/
    /// White/Yellow-Collectable, Mushroom-Cluster-5/6, Hemp-Collectable,
    /// Wood-Collectable, Metal-Collectable, Stone-Collectable,
    /// Sulfur-Collectable. Anything not explicitly named (diesel fuel
    /// included - not covered by Lucas's own list) defaults to the wider
    /// 15m tier rather than a narrow one, on the assumption an
    /// unclassified collectible is more likely a genuine resource than a
    /// low-value berry - flag if that default is wrong for something
    /// specific.
    /// </summary>
    private float GetCollectibleDivertRadius(CollectibleEntity candidate)
    {
        string name = candidate.ShortPrefabName;

        if (name.IndexOf("berry", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 3f;
        }

        if (name.IndexOf("mushroom", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 5f;
        }

        // Real early-game building-block resources (hemp/wood/stone/metal ore/sulfur ore, plus
        // anything unclassified) - doubled for a window right after a fresh wipe, unlike the
        // berry/mushroom tiers above.
        return ApplyFreshWipeCollectibleBoost(15f);
    }

    // How much wood a survivor is allowed to passively stockpile via
    // en-route gathering BEFORE it has rolled a real base design - once a
    // design IS rolled, the design's own real cost (+BaseGatherResourceBuffer,
    // the identical target TryPursueBaseGatherGoal itself uses) becomes the
    // real ceiling instead, so this flat number only matters pre-design
    // (still on the primitive checklist, or between lives). 2026-09-19,
    // Lucas's own explicit number-free framing ("quite overkill") - chosen
    // as a generous-but-real cap for whatever the checklist/early crafting
    // might still need, not a hard gameplay requirement.
    private const int EnRoutePreDesignWoodCap = 1500;

    /// <summary>
    /// See EnRouteTreeStopsThisWalk's own call site doc comment
    /// (TryFindEnRouteLootCandidate) - stops an en-route tree stop from
    /// padding wood indefinitely once the survivor already has enough for
    /// whatever it's actually working toward.
    /// </summary>
    private bool HasEnoughWoodAlready(Survivor survivor, BasePlayer npc)
    {
        int have = ItemManager.FindItemDefinition(WoodShortname) is ItemDefinition woodDef
            ? npc.inventory.GetAmount(woodDef.itemid)
            : 0;

        if (_rolledBaseDesign.TryGetValue(survivor.Character.Id, out (string Tier, string DesignPath, Dictionary<string, int> Cost) rolled)
            && rolled.Cost != null
            && rolled.Cost.TryGetValue(WoodShortname, out int designWoodCost))
        {
            return have >= designWoodCost + BaseGatherResourceBuffer;
        }

        return have >= EnRoutePreDesignWoodCap;
    }

    /// <summary>
    /// Deliberately narrow compared to the main local search (ContinueLootTask): only non-destructible containers, standalone dropped items, and collectibles. Barrels/roadsigns and corpses are excluded on purpose, since a quick en-route pickup shouldn't become a full detour project.
    /// </summary>
    private bool TryFindEnRouteLootCandidate(Survivor survivor, BasePlayer npc, out BaseEntity candidate, out EnRouteLootKind kind)
    {
        if (_engine.NavigationManager.TryFindNearestLootContainer(
            npc.transform.position,
            EnRouteLootDetectionRadius,
            out StorageContainer container,
            c => !IsLootTargetClaimed(c.net.ID)
                && !RecentlyFailedEnRouteLoot(c.net.ID)
                && c.inventory != null
                && c.inventory.itemList.Count > 0
                // Requires at least one item that would actually transfer, since a container made entirely of never-loot items would never empty and would keep re-qualifying as a candidate.
                && c.inventory.itemList.Any(item => !IsNeverLootItem(item.info.shortname))
                && !RequiresDestructionToLoot(c)
                && !IsNearJunkpileJVan(c.transform.position)
                && !IsUnreachableCrateOrBarrel(npc.transform.position, c)
                && !IsInMonumentAvoidZone(c.transform.position)
                && !IsBelowSafeLootDepth(c.transform.position)
                && !IsVehicleFuelStorage(c)
                && !IsHotAirBalloonStorage(c)
                && !IsRowboatStorage(c)
                && !IsMailbox(c)
                && !IsNearCardReader(c.transform.position)
                && HasLineOfSight(npc, c)))
        {
            candidate = container;
            kind = EnRouteLootKind.Container;
            return true;
        }

        if (_engine.NavigationManager.TryFindNearestDroppedItem(
            npc.transform.position,
            EnRouteLootDetectionRadius,
            out DroppedItem droppedItem,
            c => !IsLootTargetClaimed(c.net.ID)
                && !RecentlyFailedEnRouteLoot(c.net.ID)
                && !IsNeverLootItem(c.item.info.shortname)
                && !IsInMonumentAvoidZone(c.transform.position)
                && !IsBelowSafeLootDepth(c.transform.position)
                && !IsNearCardReader(c.transform.position)
                && !IsUnreachableDroppedItem(npc.transform.position, c.transform.position)
                && HasLineOfSight(npc, c)))
        {
            candidate = droppedItem;
            kind = EnRouteLootKind.DroppedItem;
            return true;
        }

        Guid enRouteCharacterId = survivor.Character.Id;

        // Trees checked before ore, and both before collectibles, so a farmable node takes priority when both are nearby. Each is gated on the per-walk cap and on owning a gather-capable tool.
        // Also skips the tree stop once the survivor already has enough wood, so it doesn't keep stacking more than it needs.
        if (_enRouteTreeStopsThisWalk.GetValueOrDefault(enRouteCharacterId) < EnRouteResourceNodeMaxStopsPerWalk
            && !HasEnoughWoodAlready(survivor, npc)
            && HasAnyGatherCapableTool(npc, TreeGatherToolPriority)
            && IsResourceAllowedForHighGear(survivor, npc, WoodYieldForGate())
            && IsFarmedResourceWanted(survivor, WoodYieldForGate())
            && _engine.NavigationManager.TryFindNearestTreeEntity(
                npc.transform.position,
                EnRouteResourceNodeDetectionRadius,
                out TreeEntity tree,
                c => !IsLootTargetClaimed(c.net.ID)
                    && !RecentlyFailedEnRouteLoot(c.net.ID)
                    && !IsInMonumentAvoidZone(c.transform.position)
                    && !IsBelowSafeLootDepth(c.transform.position)
                    && !IsResourceNodePoisoned(c)))
        {
            candidate = tree;
            kind = EnRouteLootKind.Tree;
            return true;
        }

        if (_enRouteOreStopsThisWalk.GetValueOrDefault(enRouteCharacterId) < EnRouteResourceNodeMaxStopsPerWalk
            && HasAnyGatherCapableTool(npc, OreGatherToolPriority)
            && _engine.NavigationManager.TryFindNearestOreResourceEntity(
                npc.transform.position,
                EnRouteResourceNodeDetectionRadius,
                out OreResourceEntity ore,
                c => !IsLootTargetClaimed(c.net.ID)
                    && IsResourceAllowedForHighGear(survivor, npc, GetNodeYields(c))
                    && IsFarmedResourceWanted(survivor, GetNodeYields(c))
                    && !RecentlyFailedEnRouteLoot(c.net.ID)
                    && !IsInMonumentAvoidZone(c.transform.position)
                    && !IsBelowSafeLootDepth(c.transform.position)
                    && !IsResourceNodePoisoned(c)))
        {
            candidate = ore;
            kind = EnRouteLootKind.OreNode;
            return true;
        }

        if (_engine.NavigationManager.TryFindNearestCollectible(
            npc.transform.position,
            ApplyFreshWipeCollectibleBoost(EnRouteCollectibleDetectionRadius),
            out CollectibleEntity collectible,
            c => !IsLootTargetClaimed(c.net.ID)
                && !RecentlyFailedEnRouteLoot(c.net.ID)
                && c.itemList != null
                && c.itemList.Length > 0
                && IsResourceAllowedForHighGear(survivor, npc, c.itemList)
                && IsFarmedResourceWanted(survivor, c.itemList)
                && !IsInMonumentAvoidZone(c.transform.position)
                && !IsBelowSafeLootDepth(c.transform.position)
                && HasLineOfSight(npc, c)
                && !IsExcludedCollectibleType(c)
                && Vector3.Distance(npc.transform.position, c.transform.position) <= GetCollectibleDivertRadius(c)))
        {
            candidate = collectible;
            kind = EnRouteLootKind.Collectible;
            return true;
        }

        candidate = null;
        kind = default;
        return false;
    }

    /// <summary>
    /// How long each individual item takes to loot from a corpse or bag, so an instant "everything transfers at once" doesn't read as unrealistic.
    /// </summary>
    private const float CorpseLootPerItemDelay = 0.2f;

    /// <summary>
    /// A corpse/bag find that multiplies the survivor's pre-loot gear score by at least this much triggers an immediate interrupt to protect the find (base-gathering or a deposit trip). See the bigGearJump check below.
    /// </summary>
    private const float BigLootGearJumpMultiplier = 1.75f;

    /// <summary>
    /// Shared paced multi-container loot logic behind LootCorpseAndContinue and LootDroppedItemContainerAndContinue, generic over any entity and its ItemContainer list. Registered in _activeAttacks so the timer is torn down by the existing cleanup path.
    /// </summary>
    private void LootMultiContainerEntityAndContinue(Survivor survivor, BaseEntity entity, List<ItemContainer> containers, string entityLabel, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveAttack(characterId);

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' is looting {entityLabel}.");

        timer.Once(DirectLootDelay, () =>
        {
            // The per-item lootTimer isn't registered in _activeAttacks until it's created below, so this bails on combat starting during the delay too.
            if (_activeCombat.ContainsKey(characterId))
            {
                return;
            }

            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || entity == null || entity.IsDestroyed)
            {
                ContinueLootTask(survivor, state);
                return;
            }

            // Snapshotted before looting, compared against the survivor's gear score once looting finishes, to detect a jackpot find worth protecting immediately.
            int gearScoreBeforeLoot = GetGearScore(npc);

            if (!IsWithinLootRange(npc, entity))
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' can't reach {entityLabel} - too far away ({Vector3.Distance(npc.transform.position, entity.transform.position):F1}m, obstacle in the way?). Skipping it.");
                RegisterLootFailure(survivor, state, npc.transform.position);
                ContinueLootTask(survivor, state);
                return;
            }

            // Snapshots every item across every container up front, since mutating a container's itemList while iterating it would skip items.
            List<Item> pending = new();

            foreach (ItemContainer container in containers)
            {
                if (container != null)
                {
                    pending.AddRange(container.itemList);
                }
            }

            if (pending.Count == 0)
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' found {entityLabel} already empty - nothing to loot.");
                state.ConsecutiveFailures = 0;
                AdvanceRetryCountdown(state);
                ContinueLootTask(survivor, state);
                return;
            }

            int index = 0;
            int moved = 0;
            List<string> movedShortnames = new();
            Timer lootTimer = null;

            lootTimer = timer.Every(CorpseLootPerItemDelay, () =>
            {
                // See RunLootHookSafely's own doc comment - the exact
                // class of bug that fix addresses (an uncaught exception
                // silently freezing a survivor's whole task forever)
                // applies equally here: a repeating timer holding
                // references across real ticks, same shape as the
                // confirmed StartAttackingContainerWithReposition crash.
                try
                {
                    BasePlayer currentNpc = survivor.Player;

                    // Not gated on IsInventoryFull, since a full inventory should only skip the specific item that can't fit, not end the whole loot session early.
                    if (currentNpc == null || currentNpc.IsDestroyed || index >= pending.Count)
                    {
                        lootTimer.Destroy();
                        _activeAttacks.Remove(characterId);

                        if (currentNpc == null || currentNpc.IsDestroyed)
                        {
                            return;
                        }

                        VerbosePuts($"loot-task: '{survivor.Character.Alias}' looted {moved} item stack(s) from {entityLabel}: {string.Join(", ", movedShortnames)}.");

                        if (moved > 0)
                        {
                            OnLootObtained(survivor, currentNpc, movedShortnames);
                        }

                        state.ConsecutiveFailures = 0;
                        AdvanceRetryCountdown(state);

                        // A corpse/bag find that jumps gear score by the threshold interrupts current behavior to protect it: base-gathering if no base yet, or a deposit trip if one exists. Only applies to corpse/bag loot (moved > 0), not container/barrel loot.
                        int gearScoreAfterLoot = GetGearScore(currentNpc);
                        bool bigGearJump = moved > 0 && (gearScoreBeforeLoot == 0
                            ? gearScoreAfterLoot > 0
                            : gearScoreAfterLoot >= gearScoreBeforeLoot * BigLootGearJumpMultiplier);

                        if (bigGearJump)
                        {
                            Puts($"loot-task: '{survivor.Character.Alias}' hit a big gear jump from {entityLabel} (gear {gearScoreBeforeLoot} -> {gearScoreAfterLoot}) - cashing in instead of continuing to loot.");

                            if (survivor.Character.Home == null)
                            {
                                // Forces the base-gather deadline into the past immediately instead of the normal randomized window, so the existing deadline-pass-through mechanism grants any still-missing base resources on the next cycle.
                                _pursuingBaseGatherGoal.Add(characterId);
                                _baseGatherDeadline[characterId] = UnityEngine.Time.realtimeSinceStartup - 1f;
                                ContinueLootTask(survivor, state);
                            }
                            else
                            {
                                GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor));
                            }

                            return;
                        }

                        ContinueLootTask(survivor, state);
                        return;
                    }

                    Item item = pending[index];
                    index++;

                    if (EnsureRoomFor(currentNpc, item) && TryTransferSingleItem(item, currentNpc.inventory, currentNpc))
                    {
                        moved++;
                        movedShortnames.Add(item.info.shortname);
                    }
                }
                catch (Exception exception)
                {
                    lootTimer.Destroy();
                    _activeAttacks.Remove(characterId);

                    // Logs the full exception, not just its message, since a bare message alone doesn't identify which line threw.
                    Puts($"WARNING: '{survivor.Character.Alias}' - {entityLabel} loot tick threw and was aborted: {exception}");

                    ContinueLootTask(survivor, state);
                }
            });

            _activeAttacks[characterId] = lootTimer;
        });
    }

    /// <summary>
    /// Repeatedly swings the survivor's equipped melee tool at a container via real BaseMelee.ServerUse() calls, so the swing animation, sound, and hit-test all behave like a real player's attack. Paces hits using the tool's own attack cooldown so a better tool swings faster.
    /// </summary>
    private void StartAttackingContainer(Survivor survivor, StorageContainer container, Action onSuccess, Action onFailed)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveAttack(characterId);

        EquipBestMeleeTool(survivor);

        int hits = 0;
        Timer attackTimer = null;

        attackTimer = timer.Every(AttackHitInterval, () =>
        {
            // Same combat-preemption guard as ContinueLootTask's own entry.
            if (_activeCombat.ContainsKey(characterId))
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                return;
            }

            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || container == null || container.IsDestroyed || container.inventory == null)
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onFailed?.Invoke();
                return;
            }

            if (!HasLineOfSight(npc, container))
            {
                // Gives up on this container rather than letting a swing land through a wall; it's already in Visited so the task moves on.
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' can't reach '{container.ShortPrefabName}' - no clear line of sight (wall in the way?). Skipping it.");

                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onFailed?.Invoke();
                return;
            }

            if (!IsWithinLootRange(npc, container))
            {
                // Line of sight alone isn't proximity; see LootInteractionRange's own doc comment.
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' can't reach '{container.ShortPrefabName}' - too far away ({Vector3.Distance(npc.transform.position, container.transform.position):F1}m, obstacle in the way?). Skipping it.");

                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onFailed?.Invoke();
                return;
            }

            int moved = TransferAllItems(container.inventory, npc.inventory, npc, out List<string> movedShortnames);

            if (moved > 0)
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' looted {moved} item stack(s) from '{container.ShortPrefabName}': {string.Join(", ", movedShortnames)}.");

                OnLootObtained(survivor, npc, movedShortnames);

                // OnLootObtained's reorganization pass equips the best weapon for display, not the melee tool; re-asserting the melee tool here restores it before the swing logic below runs.
                EquipBestMeleeTool(survivor);
            }

            BaseMelee melee = npc.GetHeldEntity() as BaseMelee;

            if (melee != null && melee.HasAttackCooldown())
            {
                // Still mid-swing/reset for this tool's real repeatDelay, same as a real player can't click faster than their held tool allows.
                return;
            }

            hits++;

            // ServerUse_Strike's hit-test raycasts from the wielder's eyes forward, so the survivor needs to be looking at the container each tick.
            AimAtContainer(npc, container);

            // ServerUse() is called purely for its swing animation/sound; actual damage is applied manually below. Its own delayed hit-test (ServerUse_Strike) is cancelled immediately after to avoid double-applying damage.
            if (melee != null)
            {
                melee.ServerUse();
                melee.CancelInvoke(melee.ServerUse_Strike);
            }

            // Cancelling ServerUse_Strike also cancels Rust's hit impact FX/sound, so it's replayed manually here to restore it without reintroducing double damage.
            PlayMeleeImpactEffect(npc, melee, container);

            container.Hurt(GetToolDamage(melee), DamageType.Blunt, npc, useProtection: false);

            bool destroyed = container.IsDestroyed || container.Health() <= 0f;

            if (destroyed || hits >= MaxHitsPerContainer)
            {
                string message = destroyed
                    ? $"loot-task: '{survivor.Character.Alias}' broke open '{container.ShortPrefabName}'."
                    : $"loot-task: '{survivor.Character.Alias}' gave up trying to destroy '{container.ShortPrefabName}' after {MaxHitsPerContainer} hits (already looted what it had).";

                VerbosePuts(message);

                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onSuccess?.Invoke();
                return;
            }
        });

        _activeAttacks[characterId] = attackTimer;
    }

    /// <summary>
    /// Hits a melee-destructible wood door barricade blocking a bot's path, exactly like a real player has to. Modeled on StartAttackingContainer's swing loop, minus the inventory-transfer step since a barricade has nothing to loot.
    /// </summary>
    private void StartAttackingBarricade(Survivor survivor, Barricade barricade, Action onSuccess, Action onFailed)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveAttack(characterId);

        EquipBestMeleeTool(survivor);

        int hits = 0;
        Timer attackTimer = null;

        attackTimer = timer.Every(AttackHitInterval, () =>
        {
            if (_activeCombat.ContainsKey(characterId))
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                return;
            }

            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || barricade == null || barricade.IsDestroyed)
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onSuccess?.Invoke();
                return;
            }

            if (!HasLineOfSight(npc, barricade) || !IsWithinLootRange(npc, barricade))
            {
                VerbosePuts($"'{survivor.Character.Alias}' lost line of sight/range on the barricade blocking its way - giving up on breaking it.");

                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onFailed?.Invoke();
                return;
            }

            BaseMelee melee = npc.GetHeldEntity() as BaseMelee;

            if (melee != null && melee.HasAttackCooldown())
            {
                return;
            }

            hits++;

            AimAtContainer(npc, barricade);

            if (melee != null)
            {
                melee.ServerUse();
                melee.CancelInvoke(melee.ServerUse_Strike);
            }

            PlayMeleeImpactEffect(npc, melee, barricade);

            barricade.Hurt(GetToolDamage(melee), DamageType.Blunt, npc, useProtection: false);

            bool destroyed = barricade.IsDestroyed || barricade.Health() <= 0f;

            if (destroyed || hits >= MaxHitsPerBarricade)
            {
                string message = destroyed
                    ? $"'{survivor.Character.Alias}' broke through a barricade blocking its way."
                    : $"'{survivor.Character.Alias}' gave up trying to break a barricade after {MaxHitsPerBarricade} hits.";

                Puts(message);

                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);

                if (destroyed)
                {
                    onSuccess?.Invoke();
                }
                else
                {
                    onFailed?.Invoke();
                }

                return;
            }
        });

        _activeAttacks[characterId] = attackTimer;
    }

    /// <summary>
    /// Angles (degrees off the straight line to the container) tried in turn once StartAttackingContainer gives up on the current approach. This is a bad-angle problem, not the physically-stuck case EscalateStuckRecovery handles.
    /// </summary>
    private static readonly float[] ContainerRepositionAngles = { 45f, -45f, 90f, -90f };

    /// <summary>
    /// How far along the rotated direction to walk for each reposition attempt.
    /// </summary>
    private const float ContainerRepositionDistance = 5.5f;

    /// <summary>
    /// Wraps StartAttackingContainer with the angled-reposition retries described by ContainerRepositionAngles before giving up on this container. Does not fall through to EscalateStuckRecovery, since the survivor isn't physically stuck.
    /// </summary>
    private void StartAttackingContainerWithReposition(Survivor survivor, StorageContainer container, Action onSuccess, Action onFailed, int repositionAttempt = 0)
    {
        StartAttackingContainer(survivor, container, onSuccess, onFailed: () =>
        {
            BasePlayer npc = survivor.Player;

            // Checked first, before touching container: it can go stale mid-callback if destroyed between StartAttackingContainer's onFailed call and this closure running.
            if (npc == null || npc.IsDestroyed || container == null || container.IsDestroyed)
            {
                onFailed?.Invoke();
                return;
            }

            if (repositionAttempt >= ContainerRepositionAngles.Length)
            {
                VerbosePuts($"'{survivor.Character.Alias}' tried {ContainerRepositionAngles.Length} different angles on '{container.ShortPrefabName}' and still can't reach/see it - not physically stuck, just genuinely unreachable from here. Giving up on it.");
                onFailed?.Invoke();
                return;
            }

            Vector3 towardContainer = container.transform.position - npc.transform.position;
            towardContainer.y = 0f;
            towardContainer = towardContainer.sqrMagnitude > 0.01f ? towardContainer.normalized : npc.transform.forward;

            Vector3 rotatedDirection = Quaternion.Euler(0f, ContainerRepositionAngles[repositionAttempt], 0f) * towardContainer;
            Vector3 candidate = npc.transform.position + rotatedDirection * ContainerRepositionDistance;

            // See StuckReassessPause's own doc comment for the brief pause before the next attempt.
            FaceDirection(npc, rotatedDirection);

            VerbosePuts($"'{survivor.Character.Alias}' couldn't reach/see '{container.ShortPrefabName}' from here - repositioning ({repositionAttempt + 1}/{ContainerRepositionAngles.Length}, {ContainerRepositionAngles[repositionAttempt]:F0}°) to try a different angle.");

            timer.Once(StuckReassessPause, () =>
            {
                BasePlayer reassessNpc = survivor.Player;

                if (reassessNpc == null || reassessNpc.IsDestroyed)
                {
                    onFailed?.Invoke();
                    return;
                }

                StartWalking(
                    survivor,
                    candidate,
                    onArrived: () => StartAttackingContainerWithReposition(survivor, container, onSuccess, onFailed, repositionAttempt + 1),
                    onFailed: () => StartAttackingContainerWithReposition(survivor, container, onSuccess, onFailed, repositionAttempt + 1));
            });
        });
    }

    /// <summary>
    /// Per-hit damage from whatever tool is equipped, read directly from BaseMelee.damageTypes rather than the weapon's scaled damage, so a better tool deals more damage here too. Falls back to AttackDamagePerHit if there's no melee tool.
    /// </summary>
    private float GetToolDamage(BaseMelee melee)
    {
        if (melee == null || melee.damageTypes == null)
        {
            return AttackDamagePerHit;
        }

        float total = 0f;

        foreach (DamageTypeEntry entry in melee.damageTypes)
        {
            total += entry.amount;
        }

        return total > 0f ? total : AttackDamagePerHit;
    }

    /// <summary>
    /// Aims npc's eyes and body-facing rotation at container's real collision center, not its transform position. Uses full 3D pitch for the eye aim so the raycast hits low targets, while keeping body rotation horizontal-only; also forces an eyes.NetworkUpdate since disconnected bots never get it refreshed automatically.
    /// </summary>
    private void AimAtContainer(BasePlayer npc, BaseEntity container)
    {
        Vector3 targetPoint = container.WorldSpaceBounds().position;
        Vector3 direction = targetPoint - npc.eyes.position;

        if (direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Quaternion aimRotation = Quaternion.LookRotation(direction);

        npc.transform.rotation = Quaternion.Euler(0f, aimRotation.eulerAngles.y, 0f);
        npc.OverrideViewAngles(aimRotation.eulerAngles);
        npc.eyes.NetworkUpdate(aimRotation);
    }

    /// <summary>
    /// Everything that should happen once any loot transfer actually moves
    /// items - regardless of source (destroyed barrel, opened crate, or a
    /// looted corpse/bag - see LootMultiContainerEntityAndContinue).
    /// Food/water consumption always runs (cheap, self-gating - see
    /// ConsumeFoodImmediately's own doc comment), but the heavier
    /// reorganization pass (equip/armor/belt/tidy) only runs if
    /// movedShortnames actually contains something worth reacting to -
    /// see ReactiveLootCategories' own doc comment for why. Lucas's own
    /// explicit request: with many survivors all looting simultaneously,
    /// re-running a full belt/inventory reorganization after literally
    /// every pickup (including a single stack of scrap) was wasted work
    /// and could shuffle item positions for no functional reason.
    /// </summary>
    private void OnLootObtained(Survivor survivor, BasePlayer npc, List<string> movedShortnames)
    {
        PlayPickupGesture(npc);

        // Eat any food/drink any water before bothering to react further -
        // see ConsumeFoodImmediately/DrinkWaterBottles' own doc comments.
        // Always run regardless of category - genuinely cheap (each just
        // scans for its own specific item type and no-ops if none exist),
        // and doesn't reorganize/shuffle anything else on its own.
        RunLootHookSafely(survivor, nameof(ConsumeFoodImmediately), () => ConsumeFoodImmediately(survivor));
        RunLootHookSafely(survivor, nameof(DrinkWaterBottles), () => DrinkWaterBottles(survivor, npc));
        RunLootHookSafely(survivor, nameof(ConsumeRadiationPillsImmediately), () => ConsumeRadiationPillsImmediately(survivor, npc));
        RunLootHookSafely(survivor, nameof(DropOwnedSeeds), () => DropOwnedSeeds(survivor, npc));
        RunLootHookSafely(survivor, nameof(DropBowKitIfArmed), () => DropBowKitIfArmed(survivor, npc));
        RunLootHookSafely(survivor, nameof(EquipBestBackpack), () => EquipBestBackpack(survivor));
        RunLootHookSafely(survivor, nameof(TryUnlockAmmoTypesFromWeapons), () => TryUnlockAmmoTypesFromWeapons(survivor, npc));

        if (ContainsReactiveLootCategory(movedShortnames))
        {
            PerformReorganizationCheck(survivor, npc);
        }
    }

    /// <summary>
    /// Real item categories worth actually reacting to with a full
    /// equip/armor/belt/tidy pass - Weapon/Attire/Tool/Medical/Ammunition
    /// are the only categories that could conceivably change what's
    /// equipped, worn, or how the belt should be laid out. Plain
    /// resources/components/junk (scrap, wood, stone, gears, ...) never
    /// could be an upgrade to anything this project tracks, so reacting
    /// to them is pure wasted work - and with many survivors all looting
    /// at once, real, worth avoiding.
    /// </summary>
    private static readonly ItemCategory[] ReactiveLootCategories =
    {
        ItemCategory.Weapon,
        ItemCategory.Attire,
        ItemCategory.Tool,
        ItemCategory.Medical,
        ItemCategory.Ammunition,
    };

    private bool ContainsReactiveLootCategory(List<string> movedShortnames)
    {
        foreach (string shortname in movedShortnames)
        {
            ItemDefinition definition = ItemManager.FindItemDefinition(shortname);

            if (definition != null && Array.IndexOf(ReactiveLootCategories, definition.category) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The full "am I set up properly" self-check, run unconditionally
    /// once before a task even starts (StartLootForResourcesTask) per
    /// Lucas's explicit request: a survivor shouldn't only tidy up
    /// reactively as loot comes in - it should confirm its own kit is in
    /// order before heading off too. OnLootObtained's own per-pickup
    /// version is conditional (see ReactiveLootCategories' own doc
    /// comment) since it runs far more often; this one-time pre-task
    /// check always runs the full thing regardless, since it only ever
    /// happens once per task, not once per item.
    /// </summary>
    private void PerformInventoryCheck(Survivor survivor, BasePlayer npc)
    {
        RunLootHookSafely(survivor, nameof(ConsumeFoodImmediately), () => ConsumeFoodImmediately(survivor));
        RunLootHookSafely(survivor, nameof(DrinkWaterBottles), () => DrinkWaterBottles(survivor, npc));
        RunLootHookSafely(survivor, nameof(ConsumeRadiationPillsImmediately), () => ConsumeRadiationPillsImmediately(survivor, npc));
        RunLootHookSafely(survivor, nameof(DropOwnedSeeds), () => DropOwnedSeeds(survivor, npc));
        RunLootHookSafely(survivor, nameof(DropBowKitIfArmed), () => DropBowKitIfArmed(survivor, npc));
        RunLootHookSafely(survivor, nameof(EquipBestBackpack), () => EquipBestBackpack(survivor));

        PerformReorganizationCheck(survivor, npc);
    }

    /// <summary>
    /// Real Rust confirmed live (Lucas, 2026-08-11): eating raw pumpkin,
    /// corn, or any berry colour actually yields a real seed item back
    /// (genuine vanilla ItemModConsume byproduct, not something this plugin
    /// creates) - the pre-existing seed.* never-loot rule only stops a
    /// survivor picking a seed up from elsewhere, it does nothing about one
    /// that appears in inventory as a side effect of eating. Same "grade A
    /// dead weight, no use until hemp/cloth gathering exists" reasoning
    /// IsNeverLootItem already applies to seeds, so any owned seed just gets
    /// dropped outright here - same shape as DropUnneededLightSource's
    /// torch drop, just triggered from the reactive inventory-check pass
    /// instead of task-start.
    /// </summary>
    private void DropOwnedSeeds(Survivor survivor, BasePlayer npc)
    {
        List<Item> seeds = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => item.info.shortname.StartsWith(SeedShortnamePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (Item seed in seeds)
        {
            Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
            Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

            seed.Drop(dropPosition, dropVelocity);

            VerbosePuts($"'{survivor.Character.Alias}' dropped '{seed.info.shortname}' - no use for seeds yet.");
        }
    }

    /// <summary>
    /// The actual equip/armor/belt/tidy reorganization steps, shared by
    /// both OnLootObtained (conditionally) and PerformInventoryCheck
    /// (always, for the once-per-task pre-departure check).
    /// </summary>
    private void PerformReorganizationCheck(Survivor survivor, BasePlayer npc)
    {
        // A better weapon than whatever's currently displayed might have
        // just come out of this - see EquipBestWeaponForDisplay's own doc
        // comment for why this (not EquipBestMeleeTool) is what governs
        // the survivor's normal, everyday active item. EquipBestMeleeTool
        // itself is only ever called directly by StartAttackingContainer,
        // for the brief real window of actually swinging at a barrel.
        RunLootHookSafely(survivor, nameof(EquipBestWeaponForDisplay), () => EquipBestWeaponForDisplay(survivor));

        // Same idea for armor - see EvaluateAndUpgradeArmor's own doc
        // comment.
        RunLootHookSafely(survivor, nameof(EvaluateAndUpgradeArmor), () => EvaluateAndUpgradeArmor(survivor));

        // Catches redundant armor EvaluateAndUpgradeArmor's own drop-on-
        // upgrade doesn't (duplicates that never actually competed against
        // each other in a single upgrade event) - see its own doc comment.
        RunLootHookSafely(survivor, nameof(DropRedundantArmor), () => DropRedundantArmor(survivor, npc));

        // Doesn't need the starting rock anymore once something better's
        // actually in hand - see DropRockIfUpgraded's own doc comment.
        RunLootHookSafely(survivor, nameof(DropRockIfUpgraded), () => DropRockIfUpgraded(survivor, npc));

        // Keep the belt laid out like a real player would, and main
        // inventory tidy - see OrganizeBelt/TidyMainInventory's own doc
        // comments.
        RunLootHookSafely(survivor, nameof(OrganizeBelt), () => OrganizeBelt(survivor));
        RunLootHookSafely(survivor, nameof(TidyMainInventory), () => TidyMainInventory(survivor));
    }

    /// <summary>
    /// Isolates each post-loot hook above from the others. A live crash
    /// (a NullReferenceException in a different but structurally similar
    /// method - see StartAttackingContainerWithReposition's own doc
    /// comment) confirmed an uncaught exception in one of these can
    /// silently break the ENTIRE loot-task callback chain:
    /// LootCorpseAndContinue/LootContainerDirectly both call
    /// OnLootObtained BEFORE their own trailing ContinueLootTask call, so
    /// an exception partway through this sequence aborts before that line
    /// ever runs - a survivor that freezes permanently right after its
    /// next loot pickup, matching a live report ("stops and doesn't know
    /// what to do next" after looting a corpse). Catching and logging
    /// per-hook means one broken hook (several of these - armor eval,
    /// food, water, belt layout - are all brand new and not yet fully
    /// live-tested) can't take the whole survivor down with it, and any
    /// future failure gets a real, specific log line to work from instead
    /// of a silent freeze.
    /// </summary>
    private void RunLootHookSafely(Survivor survivor, string hookName, Action hook)
    {
        try
        {
            hook();
        }
        catch (Exception exception)
        {
            Puts($"WARNING: '{survivor.Character.Alias}' - loot hook '{hookName}' threw and was skipped: {exception.Message}");
        }
    }

    /// <summary>
    /// Best-effort real "reaching in and grabbing something" cue, fired
    /// whenever a loot transfer actually moves items - Lucas's own
    /// observation that a real player has a visible pickup/grab animation,
    /// while this bot's loot transfers were a silent, instant inventory
    /// swap. Used BaseEntity.Signal.Gesture, the same real client RPC
    /// mechanism StartAttackingContainer's swing animation relies on
    /// (BaseMelee.ServerUse's SignalBroadcast(Signal.Attack, ...),
    /// confirmed via decompiling Assembly-CSharp.dll) - Signal is a small,
    /// fixed enum (Attack, Reload, Throw, Gesture, Eat, ...; no dedicated
    /// "Pickup" entry exists), and Gesture was the closest generic "play a
    /// body animation" option in it.
    ///
    /// DISABLED 2026-08-16 (Lucas's own live bug report: a bot's held
    /// weapon going invisible - "still holding its hands as if it were
    /// holding it" - right after picking something up, no equip/swap event
    /// anywhere near it in the log). This function's own doc comment
    /// always flagged Signal.Gesture as "not yet visually confirmed" - a
    /// real connected player's own client naturally animates OUT of a
    /// gesture pose once it finishes, but these bots never process a real
    /// client tick, so nothing ever tells the animator the gesture is
    /// over; getting stuck IN the gesture pose (hands up, held item
    /// hidden) forever is a very plausible real explanation, and matches
    /// the report far better than the equip-churn theory this same session
    /// already fixed separately. No-op for now to conclusively test
    /// whether this was the actual cause - re-enable (and this time pace
    /// it with a delayed ForceRefreshHeldEntity call to force the weapon
    /// back once the gesture would have finished) only once that's
    /// confirmed either way.
    /// </summary>
    private void PlayPickupGesture(BasePlayer npc)
    {
    }

    /// <summary>
    /// Manually replays the hit impact FX/sound that BaseMelee.ServerUse_Strike
    /// would normally trigger via Effect.server.ImpactEffect - needed because
    /// StartAttackingContainer cancels ServerUse_Strike entirely (to avoid a
    /// confirmed double-damage bug, see its own call site comment), which
    /// also silently cancels the only place that impact FX/sound gets fired.
    /// Builds its own HitInfo instead of relying on a real physics raycast
    /// (unlike the native code) since the target container is already known
    /// - container.ClosestPoint gives a reasonable stand-in for where a real
    /// swing would have connected. HitMaterial deliberately left at Rust's
    /// "generic" fallback (the same fallback ServerUse_Strike itself uses
    /// when a collider's real material can't be determined) rather than
    /// guessing a specific material - EffectDictionary and
    /// BaseMelee.GetStrikeEffectPath both degrade gracefully for it.
    /// </summary>
    private void PlayMeleeImpactEffect(BasePlayer npc, BaseMelee melee, BaseEntity container)
    {
        if (melee == null)
        {
            return;
        }

        Vector3 hitPositionWorld = container.ClosestPoint(npc.eyes.position);
        Vector3 hitNormalWorld = (npc.eyes.position - hitPositionWorld).normalized;

        HitInfo hitInfo = new HitInfo
        {
            Initiator = npc,
            WeaponPrefab = melee,
            // Effect.server.ImpactEffect (decompiled) branches on
            // "info.WeaponPrefab is AttackEntity" - true for a BaseMelee -
            // but then calls info.Weapon.GetImpactEffectNumberValue(info)
            // and info.Weapon.GetEffectType(info), reading the SEPARATE
            // Weapon field, not WeaponPrefab. Never setting it here meant
            // that branch always dereferenced a null Weapon and threw -
            // confirmed by a live server error: "Object reference not set
            // to an instance of an object" inside Effect+server.ImpactEffect,
            // called from this exact method. Both fields need to point at
            // the same real melee tool.
            Weapon = melee,
            HitEntity = container,
            HitPositionWorld = hitPositionWorld,
            HitPositionLocal = container.transform.InverseTransformPoint(hitPositionWorld),
            HitNormalWorld = hitNormalWorld,
            HitNormalLocal = container.transform.InverseTransformDirection(hitNormalWorld),
            HitMaterial = StringPool.Get("generic"),
        };

        Effect.server.ImpactEffect(hitInfo);
    }

    /// <summary>
    /// Whether the survivor is carrying any known melee tool anywhere (main or belt), regardless of what's currently equipped. Used to decide whether barrels/roadsigns are worth considering as a target at all.
    /// </summary>
    private bool HasAnyMeleeTool(BasePlayer npc)
    {
        foreach (Item item in npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList))
        {
            if (Array.IndexOf(MeleeToolPriority, item.info.shortname) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Same as HasAnyMeleeTool but excludes the starting rock. See IsRoadsign's own doc comment for why roadsigns need this stricter gate.
    /// </summary>
    private bool HasNonRockMeleeTool(BasePlayer npc)
    {
        foreach (Item item in npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList))
        {
            if (item.info.shortname != "rock" && Array.IndexOf(MeleeToolPriority, item.info.shortname) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Stricter than HasNonRockMeleeTool: also excludes the plain bone knife, since neither it nor the rock is a meaningful combat weapon. knife.bone.obsidian still counts.
    /// </summary>
    private bool HasCombatReadyMeleeWeapon(BasePlayer npc)
    {
        foreach (Item item in npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList))
        {
            if (item.info.shortname != "rock" && item.info.shortname != "knife.bone"
                && Array.IndexOf(MeleeToolPriority, item.info.shortname) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Equips once and stops: the first real tool a survivor picks up gets equipped and kept for the rest of that life, even if something ranked higher turns up later.
    /// </summary>
    private void EquipBestMeleeTool(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        Item currentlyEquipped = npc.GetActiveItem();

        if (currentlyEquipped != null && currentlyEquipped.info.shortname != "rock"
            && Array.IndexOf(MeleeToolPriority, currentlyEquipped.info.shortname) >= 0)
        {
            return;
        }

        // Prefers whatever's already on the belt (usually the gather tool) before reaching into main inventory for a higher-ranked but harder-to-swap-in melee weapon.
        Item best = FindBestByPriority(npc.inventory.containerBelt.itemList.ToList(), MeleeToolPriority, exclude: null);

        if (best == null)
        {
            int bestRank = int.MaxValue;

            foreach (Item item in npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList))
            {
                int rank = Array.IndexOf(MeleeToolPriority, item.info.shortname);

                if (rank >= 0 && rank < bestRank)
                {
                    bestRank = rank;
                    best = item;
                }
            }
        }

        if (best == null || (currentlyEquipped != null && currentlyEquipped.uid == best.uid))
        {
            return;
        }

        int bestRankForLog = Array.IndexOf(MeleeToolPriority, best.info.shortname);

        if (!npc.inventory.containerBelt.itemList.Contains(best))
        {
            // Targets the currently-equipped item's own belt position directly (allowSwap), since OrganizeBelt claims all 6 belt slots and an auto-position move would find no free slot.
            int targetPosition = currentlyEquipped != null && currentlyEquipped.parent == npc.inventory.containerBelt
                ? currentlyEquipped.position
                : -1;

            if (!best.MoveToContainer(npc.inventory.containerBelt, targetPosition))
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' has a better tool ('{best.info.shortname}') but no free belt slot to equip it - keeping '{(currentlyEquipped != null ? currentlyEquipped.info.shortname : "nothing")}'.");
                return;
            }
        }

        npc.UpdateActiveItem(best.uid);

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' equipped '{best.info.shortname}' (priority rank {bestRankForLog}).");
    }

    /// <summary>
    /// Equips the survivor's best weapon as the displayed item for ordinary exploring/looting, so it isn't visibly holding a tool while a gun sits unused. Falls back to the best gathering tool if no real weapon is owned. Never swaps away from an already-equipped real weapon; restores whatever's already in the committed weapon slot otherwise.
    /// </summary>
    private void EquipBestWeaponForDisplay(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        Item currentlyEquipped = npc.GetActiveItem();

        List<Item> allItems = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .ToList();

        // Best weapon the survivor can actually use right now; a firearm with no ammo doesn't count.
        Item bestUsableWeapon = FindBestByPriority(allItems.Where(item => IsWeaponUsableNow(npc, item)).ToList(), WeaponPriority, exclude: null);
        int bestUsableRank = bestUsableWeapon != null ? Array.IndexOf(WeaponPriority, bestUsableWeapon.info.shortname) : int.MaxValue;

        if (currentlyEquipped != null && Array.IndexOf(WeaponPriority, currentlyEquipped.info.shortname) >= 0)
        {
            // Already holding a listed weapon: only carries on if a usable owned weapon is actually better.
            int currentRank = Array.IndexOf(WeaponPriority, currentlyEquipped.info.shortname);

            if (bestUsableWeapon == null || bestUsableRank >= currentRank)
            {
                return;
            }
        }

        Item committedPrimary = npc.inventory.containerBelt.itemList.FirstOrDefault(item =>
            item.position == BeltWeaponSlot
            && (Array.IndexOf(WeaponPriority, item.info.shortname) >= 0 || Array.IndexOf(GatherToolPriority, item.info.shortname) >= 0));

        // The "committed" slot weapon only wins if it's at least as good as
        // the best usable one owned.
        if (committedPrimary != null && bestUsableWeapon != null
            && Array.IndexOf(WeaponPriority, committedPrimary.info.shortname) is int committedRank
            && (committedRank < 0 || bestUsableRank < committedRank))
        {
            committedPrimary = null;
        }

        Item best = committedPrimary
            ?? bestUsableWeapon
            ?? FindBestByPriority(allItems, WeaponPriority, exclude: null)
            ?? FindBestByPriority(allItems, GatherToolPriority, exclude: null);

        if (best == null || (currentlyEquipped != null && currentlyEquipped.uid == best.uid))
        {
            return;
        }

        // Checks parent AND position, not just belt membership, so a weapon sitting in the wrong belt slot still gets moved into BeltWeaponSlot rather than OrganizeBelt finding a surprise later.
        bool alreadyInWeaponSlot = best.parent == npc.inventory.containerBelt && best.position == BeltWeaponSlot;

        if (!alreadyInWeaponSlot && !best.MoveToContainer(npc.inventory.containerBelt, BeltWeaponSlot))
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' has a better weapon ('{best.info.shortname}') to display but no free belt slot for it - keeping '{(currentlyEquipped != null ? currentlyEquipped.info.shortname : "nothing")}'.");
            return;
        }

        npc.UpdateActiveItem(best.uid);
        ForceRefreshHeldEntity(npc);

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' switched back to displaying '{best.info.shortname}'.");
    }

    /// <summary>
    /// Forces a fresh network snapshot of whatever's currently held, right after UpdateActiveItem. Connectionless bots never send a real tick RPC, so nothing else broadcasts the state change on its own. No-ops if nothing is held.
    /// </summary>
    private void ForceRefreshHeldEntity(BasePlayer npc)
    {
        npc.GetHeldEntity()?.SendNetworkUpdateImmediate();
    }

    /// <summary>
    /// Drops the starting rock once the survivor has a genuinely better melee/gather tool anywhere in inventory, not just currently equipped.
    /// </summary>
    private void DropRockIfUpgraded(Survivor survivor, BasePlayer npc)
    {
        Item rock = npc.inventory.FindItemByItemName("rock");

        if (rock == null)
        {
            return;
        }

        bool hasBetterTool = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Any(item => item.info.shortname != "rock" && Array.IndexOf(MeleeToolPriority, item.info.shortname) >= 0);

        if (!hasBetterTool)
        {
            return;
        }

        // Keeps the rock until the survivor owns both a hatchet and a pickaxe, since the rock is the only tool that can gather both wood and stone.
        if (!HasAnyToolOfFamily(npc, HatchetFamily) || !HasAnyToolOfFamily(npc, PickaxeFamily))
        {
            return;
        }

        Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
        Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

        rock.Drop(dropPosition, dropVelocity);

        VerbosePuts($"'{survivor.Character.Alias}' dropped its rock now that it has a real hatchet and pickaxe.");
    }

    /// <summary>
    /// Damage types folded into a single overall armor score. Bullet/Slash/Blunt cover the combat cases that matter for survivability; Cold is included for future exposure needs. Deliberately excludes rarer types like Radiation/Explosion.
    /// </summary>
    private static readonly DamageType[] ArmorEvaluationDamageTypes =
    {
        DamageType.Bullet,
        DamageType.Slash,
        DamageType.Blunt,
        DamageType.Cold,
    };

    /// <summary>
    /// Bullet gets weighted far above the others - a live report and its
    /// log evidence confirmed a real scoring bug: a flat unweighted sum
    /// let wood.armor.jacket (broad-but-shallow protection across all
    /// four types) outscore metal.plate.torso.icevest outright (0.90 vs
    /// 0.65), so the bot equipped wood over metal and dropped the metal
    /// plate it had just looted - backwards from any real player's
    /// judgement. Rust's own real armor design deliberately concentrates
    /// top-tier pieces (metal plate, roadsign) into strong bullet
    /// resistance specifically, since gunfights are the dominant real
    /// threat/cause of death - a flat sum structurally can't reflect that,
    /// since it lets several mediocre secondary stats numerically outweigh
    /// one excellent primary one. Slash/Blunt/Cold are still real
    /// secondary factors (a tiebreaker between two similarly bullet-
    /// resistant pieces), just no longer able to overrule a clear bullet-
    /// protection gap the way they just did.
    /// </summary>
    private const float BulletProtectionWeight = 4f;

    /// <summary>
    /// How much higher a candidate's score has to be than whatever it
    /// would displace before it's worth the swap - without this, two
    /// pieces with near-identical protection (e.g. both roughly "tier 1
    /// cloth") would thrash back and forth every time a new one is picked
    /// up, never settling.
    /// </summary>
    private const float ArmorUpgradeMinimumScoreGain = 0.05f;

    /// <summary>
    /// Real-world armor tiers, low to high, per Lucas's explicit ranking
    /// (2026-08-09): basic clothing (burlap, pants, hoodies - no special
    /// handling needed, their real protection stats are low enough to
    /// naturally fall at the bottom already) &lt; wood armor &lt; radiation/
    /// hazmat suits (deliberately ranked low but explicitly ABOVE wood -
    /// "however it trumps wooden armour") &lt; roadsign &lt; metal plate &lt;
    /// ballistic/heavy plate (highest). This exists as an explicit tier
    /// list rather than trusting the raw weighted-damage-type score to
    /// happen to land in this order on its own - hazmat suits are the
    /// clearest reason why: their real value is radiation resistance, a
    /// damage type this project doesn't even track (see
    /// ArmorEvaluationDamageTypes' own doc comment for why), so their raw
    /// combat-protection score alone would likely rank them at or below
    /// wood, contradicting Lucas's explicit real-world ranking. Real
    /// shortnames confirmed via a full item-database scan.
    /// </summary>
    private enum ArmorTier
    {
        Basic = 0,
        Wood = 1,
        Hazmat = 2,
        Roadsign = 3,
        MetalPlate = 4,
        TopTier = 5,
    }

    private static readonly string[] WoodArmorShortnames = { "wood.armor.jacket", "wood.armor.pants", "wood.armor.helmet" };
    // coffeecan.helmet added 2026-08-15 (Lucas's own explicit request,
    // "have coffee can helmet added as medium, same as road sign") - real
    // shortname confirmed via Bundles\items\coffeecan.helmet.json.
    private static readonly string[] RoadsignArmorShortnames = { "roadsign.jacket", "roadsign.gloves", "roadsign.kilt", "coffeecan.helmet" };
    private static readonly string[] MetalPlateArmorShortnames = { "metal.plate.torso", "metal.plate.torso.icevest", "metal.facemask", "metal.facemask.hockey", "metal.facemask.icemask" };

    /// <summary>
    /// Ballistic (the current real top tier: vest/helmet/leg armor) and
    /// heavy plate (the older top tier: jacket/pants/helmet) are both
    /// genuinely top-of-game armor - Lucas named "ballistic armour"
    /// specifically as highest but didn't separately place heavy plate,
    /// so both share this tier; GetArmorProtectionScore's own raw
    /// weighted score still breaks ties between them.
    /// </summary>
    private static readonly string[] TopTierArmorShortnames = { "ballistic.vest", "ballistic.helmet", "ballistic.legarmor", "heavy.plate.jacket", "heavy.plate.pants", "heavy.plate.helmet" };

    /// <summary>
    /// Every real radiation/hazmat suit shortname shares this exact
    /// prefix (base hazmatsuit plus every real skinned variant -
    /// arcticsuit, diver, frontier, lumberjack, nomadsuit, pilot,
    /// spacesuit, the scientist variants, ...) - matched by prefix so a
    /// future new skin is covered automatically, same reasoning
    /// SeedShortnamePrefix's own doc comment gives. Deliberately NOT a
    /// bare "hazmat" prefix - that would also catch hazmat.krieg and
    /// hazmat.plushy (real novelty/non-armor items) and
    /// pilot.hazmat.box.wooden (a deployable, not wearable at all).
    /// </summary>
    private const string HazmatSuitShortnamePrefix = "hazmatsuit";

    private static ArmorTier GetArmorTier(string shortname)
    {
        if (Array.IndexOf(TopTierArmorShortnames, shortname) >= 0)
        {
            return ArmorTier.TopTier;
        }

        if (Array.IndexOf(MetalPlateArmorShortnames, shortname) >= 0)
        {
            return ArmorTier.MetalPlate;
        }

        if (Array.IndexOf(RoadsignArmorShortnames, shortname) >= 0)
        {
            return ArmorTier.Roadsign;
        }

        if (shortname.StartsWith(HazmatSuitShortnamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ArmorTier.Hazmat;
        }

        if (Array.IndexOf(WoodArmorShortnames, shortname) >= 0)
        {
            return ArmorTier.Wood;
        }

        return ArmorTier.Basic;
    }

    /// <summary>
    /// Basic and Wood tier clothing/armor is cheap and low-value enough to be deduplicated on sight, unlike higher-tier spares which are worth keeping regardless of duplicates.
    /// </summary>
    private static bool IsLowTierArmor(string shortname)
    {
        return GetArmorTier(shortname) <= ArmorTier.Wood;
    }

    /// <summary>
    /// Per-item protection score built from two layers: ArmorTier dominates the comparison, and the weighted-damage-type score only breaks ties within the same tier. Item condition is already folded in by GetProtection.
    /// </summary>
    private float GetArmorProtectionScore(Item item)
    {
        ItemModWearable wearable = item.info.GetComponent<ItemModWearable>();

        if (wearable == null || !wearable.HasProtections())
        {
            return 0f;
        }

        float rawScore = 0f;

        foreach (DamageType damageType in ArmorEvaluationDamageTypes)
        {
            float protection = wearable.GetProtection(item, damageType);
            rawScore += damageType == DamageType.Bullet ? protection * BulletProtectionWeight : protection;
        }

        return (float)GetArmorTier(item.info.shortname) * 100f + rawScore;
    }

    /// <summary>
    /// Evaluates every wearable sitting unworn in inventory against whatever it would actually displace, and equips it if it's a real upgrade. What a piece conflicts with is computed from CanExistWith rather than a hardcoded slot layout, since Rust armor doesn't use fixed slots.
    /// </summary>
    private void EvaluateAndUpgradeArmor(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        EquipBestBackpack(survivor);

        // Also scans containerBelt, not just containerMain, since a wearable can land directly on the belt if main was fuller at that moment.
        List<Item> candidates = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => item.info.GetComponent<ItemModWearable>() != null && !IsBackpackItem(item))
            .ToList();

        foreach (Item candidate in candidates)
        {
            ItemModWearable candidateWearable = candidate.info.GetComponent<ItemModWearable>();

            List<Item> conflicting = npc.inventory.containerWear.itemList
                .Where(worn => !candidateWearable.CanExistWith(worn.info.GetComponent<ItemModWearable>()))
                .ToList();

            float candidateScore = GetArmorProtectionScore(candidate);
            float conflictingScore = conflicting.Sum(GetArmorProtectionScore);

            // Only requires a score improvement when something is actually being displaced; an empty slot is always worth filling regardless of score.
            if (conflicting.Count > 0 && candidateScore < conflictingScore + ArmorUpgradeMinimumScoreGain)
            {
                continue;
            }

            if (candidate.MoveToContainer(npc.inventory.containerWear))
            {
                string replacedNote = conflicting.Count > 0
                    ? string.Join(", ", conflicting.Select(item => item.info.shortname))
                    : "nothing (empty slot)";

                VerbosePuts($"loot-task: '{survivor.Character.Alias}' equipped '{candidate.info.shortname}' (protection {candidateScore:F2} vs {conflictingScore:F2}) - replacing: {replacedNote}.");

                // Drops the outclassed pieces for real instead of letting them pile up as clutter in main inventory.
                foreach (Item displaced in conflicting)
                {
                    Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
                    Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

                    displaced.Drop(dropPosition, dropVelocity);
                }
            }
        }
    }

    /// <summary>
    /// Declutters armor/clothing that's strictly outclassed by what's currently worn. Only compares against worn items, not other unworn spares, since owning equal-or-better backup sets is deliberate, not clutter.
    /// </summary>
    private void DropRedundantArmor(Survivor survivor, BasePlayer npc)
    {
        List<Item> worn = npc.inventory.containerWear.itemList;

        List<Item> unwornCandidates = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => item.info.GetComponent<ItemModWearable>() != null && !IsBackpackItem(item))
            .ToList();

        foreach (Item item in unwornCandidates)
        {
            ItemModWearable wearable = item.info.GetComponent<ItemModWearable>();
            float score = GetArmorProtectionScore(item);

            // Low-tier exact duplicate of something already worn; see IsLowTierArmor's own doc comment.
            bool lowTierDuplicateOfWorn = IsLowTierArmor(item.info.shortname)
                && worn.Any(wornItem => wornItem.info.shortname == item.info.shortname);

            bool outclassedByWorn = lowTierDuplicateOfWorn || worn.Any(wornItem =>
            {
                ItemModWearable wornWearable = wornItem.info.GetComponent<ItemModWearable>();

                if (wornWearable == null || wearable.CanExistWith(wornWearable))
                {
                    return false;
                }

                return GetArmorProtectionScore(wornItem) > score;
            });

            if (!outclassedByWorn)
            {
                continue;
            }

            Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
            Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

            item.Drop(dropPosition, dropVelocity);

            string reason = lowTierDuplicateOfWorn ? "a low-tier duplicate of what's already worn" : "worse than what's already worn";
            VerbosePuts($"'{survivor.Character.Alias}' dropped redundant '{item.info.shortname}' - {reason}.");
        }
    }

    /// <summary>
    /// Solid layers a wall/floor/rock would sit on, deliberately narrower than NavigationManager's ObstacleLayerMask so a fallen log or corpse doesn't incorrectly block a container attack. Combat uses a separate mask below.
    /// </summary>
    private static readonly int LineOfSightBlockingMask = LayerMask.GetMask("Terrain", "World", "Construction");

    /// <summary>
    /// Combat's own LOS mask, which includes Tree and Default unlike LineOfSightBlockingMask, since a bullet is stopped by tree trunks and ore nodes and combat LOS needs to account for that.
    /// </summary>
    private static readonly int CombatLineOfSightBlockingMask = LayerMask.GetMask("Terrain", "World", "Construction", "Tree", "Default");

    /// <summary>
    /// Combat-specific sibling of HasLineOfSight(BasePlayer, BaseEntity), using CombatLineOfSightBlockingMask so a tree trunk correctly counts as blocking.
    /// </summary>
    private bool HasCombatLineOfSight(BasePlayer npc, BaseCombatEntity target)
    {
        Vector3 origin = npc.eyes.position;
        Vector3 targetPoint = GetAimPoint(target);

        if (!Physics.Linecast(origin, targetPoint, out RaycastHit hit, CombatLineOfSightBlockingMask, QueryTriggerInteraction.Ignore))
        {
            return true;
        }

        BaseEntity hitEntity = hit.collider.GetComponentInParent<BaseEntity>();

        return hitEntity != null && hitEntity.EqualNetID(target);
    }

    /// <summary>
    /// Whether npc is actually close enough to container to loot/attack it. See LootInteractionRange's own doc comment for why this is checked independently of arrival or line of sight.
    /// </summary>
    private bool IsWithinLootRange(BasePlayer npc, BaseEntity container)
    {
        Vector3 closestPoint = container.WorldSpaceBounds().ClosestPoint(npc.transform.position);

        return Vector3.Distance(npc.transform.position, closestPoint) <= LootInteractionRange;
    }

    /// <summary>
    /// Whether npc has a clear line to container - a Linecast that hits
    /// anything other than the container itself first means something
    /// solid (a wall, most likely) sits between them.
    /// </summary>
    private bool HasLineOfSight(BasePlayer npc, BaseEntity container)
    {
        Vector3 origin = npc.eyes.position;
        Vector3 targetPoint = container.WorldSpaceBounds().ClosestPoint(origin);

        if (!Physics.Linecast(origin, targetPoint, out RaycastHit hit, LineOfSightBlockingMask, QueryTriggerInteraction.Ignore))
        {
            return true;
        }

        BaseEntity hitEntity = hit.collider.GetComponentInParent<BaseEntity>();

        return hitEntity != null && hitEntity.EqualNetID(container);
    }

    private void CancelActiveAttack(Guid characterId)
    {
        if (_activeAttacks.TryGetValue(characterId, out Timer attackTimer))
        {
            attackTimer.Destroy();
            _activeAttacks.Remove(characterId);
        }
    }

    /// <summary>
    /// Moves every item from a container into the survivor's inventory, preferring main but falling back to GiveItem when main won't take it. Only stops once both main and belt are full.
    /// </summary>
    private int TransferAllItems(ItemContainer from, PlayerInventory to, BasePlayer npc)
    {
        return TransferAllItems(from, to, npc, out _);
    }

    /// <summary>
    /// movedShortnames lets callers log what was actually picked up, not just how many stacks, for diagnosing whether a specific item was ever looted.
    /// </summary>
    private int TransferAllItems(ItemContainer from, PlayerInventory to, BasePlayer npc, out List<string> movedShortnames)
    {
        int moved = 0;
        movedShortnames = new List<string>();

        // Copies first, since MoveToContainer mutates from.itemList as it goes.
        var items = new List<Item>(from.itemList);

        foreach (Item item in items)
        {
            // continue, not break: a full inventory should only skip this item, not abandon the rest of the container's contents.
            if (!EnsureRoomFor(npc, item))
            {
                continue;
            }

            if (TryTransferSingleItem(item, to, npc))
            {
                moved++;
                movedShortnames.Add(item.info.shortname);
            }
        }

        return moved;
    }

    /// <summary>
    /// Tool/weapon families where owning just one is genuinely enough, since a second pickaxe/hatchet/bow doesn't gather or perform any better and is pure clutter. Firearms are deliberately not covered, since spares there are genuine equipment upgrades.
    /// </summary>
    private static readonly string[] PickaxeFamily = { "pickaxe", "stone.pickaxe", "concretepickaxe", "diverpickaxe", "lumberjack.pickaxe", "icepick.salvaged" };
    private static readonly string[] HatchetFamily = { "hatchet", "stonehatchet", "concretehatchet", "diverhatchet", "lumberjack.hatchet", "frontier_hatchet", "axe.salvaged" };
    private static readonly string[] BowFamily = { "bow.compound", "bow.hunting", "crossbow", "crossbowbowless", "minicrossbow" };

    /// <summary>
    /// mace and mace.baseballbat are functionally the same melee role, so a second one is as redundant as a second hatchet.
    /// </summary>
    private static readonly string[] MaceFamily = { "mace", "mace.baseballbat" };

    /// <summary>
    /// Bots avoid looting spare rocks off corpses/bags while already owning one, since a rock is the single worst
    /// tool in the game (the only reason a survivor ever holds one at all
    /// is GiveStartingKit's default before anything better turns up) and
    /// a second one adds nothing.
    /// </summary>
    private static readonly string[] RockFamily = { "rock" };

    /// <summary>
    /// Added 2026-08-10, Lucas's explicit request: "avoid looting
    /// multiple water bottle fillable containers." Normally a bottle gets
    /// drunk and dropped the instant it's picked up (DrinkWaterBottles),
    /// so this rarely matters - but if the survivor's already fully
    /// hydrated, ConsumeViaItemModConsume's CanDoAction correctly refuses
    /// to drink it (0 actions), and per DrinkWaterBottles' own doc
    /// comment nothing drops an UNdrunk bottle. Without this, that one
    /// still-full bottle would just sit in inventory while the bot kept
    /// picking up MORE of them from every corpse/bag it passes - this
    /// closes that gap at the pickup filter itself.
    /// </summary>
    private static readonly string[] WaterBottleFamily = { "smallwaterbottle" };

    // Any door counts as "the one door" (2026-09-21, Lucas's own explicit ask:
    // never carry more than one door or one tool cupboard - dead weight).
    private static readonly string[] DoorFamily =
    {
        "door.hinged.wood", "door.hinged.metal", "door.hinged.toptier",
        "door.double.hinged.wood", "door.double.hinged.metal", "door.double.hinged.toptier",
    };

    private static readonly string[][] SingleOwnershipFamilies = { PickaxeFamily, HatchetFamily, BowFamily, MaceFamily, RockFamily, WaterBottleFamily, new[] { HammerShortname }, new[] { BuildingPlannerShortname }, DoorFamily, new[] { ToolCupboardShortname } };

    /// <summary>
    /// Real items never worth picking up at all, regardless of what the
    /// survivor already owns - Lucas's own explicit examples: torches
    /// ("grade A dead weight" - the bot doesn't need light, see
    /// DropUnneededLightSource's own doc comment for why it doesn't even
    /// keep its OWN starting one) and seeds of any kind ("for noting not
    /// for implementing" - hemp/cloth gathering is real future work, but
    /// picking up seeds has no use at all until that exists). Exact
    /// shortnames for the real light-source torches only (confirmed via a
    /// scan of the bundled item database) - deliberately NOT
    /// industrial.torch (a real welding/repair TOOL, not a light source)
    /// or torchholder (a wall-mounted deployable, not a carried item).
    ///
    /// bone.fragments/humanmeat.raw/skull.human/grub/worm added 2026-08-14
    /// (Lucas's own explicit follow-up, "add... to the avoid list for
    /// eating and picking up") - all real, confirmed shortnames (Bundles\
    /// items\*.json). Both grub AND worm included even though Lucas only
    /// said "grubs (worms)" - two genuinely distinct real items ("Grub" and
    /// "Worm"), not two names for the same one, so both are covered rather
    /// than guessing which single one he meant. This same list already
    /// gates BOTH pickup paths Lucas asked about - TryTransferSingleItem
    /// (corpses/bags/containers) and TryFindNearestDroppedItem's own search
    /// filter (standalone dropped items) both check IsNeverLootItem, which
    /// reads straight from this array - no separate change needed for
    /// either.
    ///
    /// heavy.plate.helmet/jacket/pants added 2026-08-14 (Lucas's own
    /// explicit request: "have the bot avoid any heavy plate X armour
    /// (pants, chest, helmet, etc") - real, confirmed shortnames (Bundles\
    /// items\*.json, no separate boots variant exists). Unlike every other
    /// entry here this is actually the single BEST armor in the game
    /// defensively - excluded anyway per explicit request, presumably for
    /// its real, severe movement-speed penalty rather than because it's
    /// useless. Skipped at pickup entirely (not just "never worn") since
    /// there's nowhere for a bot to deposit/sell excess gear yet - same
    /// choke point (TryTransferSingleItem/TryFindNearestDroppedItem) as
    /// every other never-loot entry, so it's never carried at all, not
    /// just never equipped.
    ///
    /// rock added 2026-08-14 (real, confirmed bug report, not a fresh
    /// request) - RockFamily/SingleOwnershipFamilies (ShouldSkipDuplicateItem)
    /// only ever skipped a SECOND rock while the survivor still owned one;
    /// it said nothing about a rock the survivor no longer owns at all. A
    /// bot that drops its own starting rock the instant a real gathering
    /// tool displaces it (see EquipBest*/DropRedundant* elsewhere) leaves
    /// that exact rock sitting on the ground as a genuinely ownerless
    /// DroppedItem - the very next loot cycle's TryFindNearestDroppedItem
    /// search had nothing left excluding it (zero owned rocks = not a
    /// "duplicate"), so it walked back, picked it up, immediately re-
    /// evaluated as dead weight, dropped it again, and repeated forever.
    /// Full IsNeverLootItem exclusion fixes this the same way every other
    /// entry here does - a rock is now never picked up at all, regardless
    /// of current ownership, so there's nothing left to thrash between.
    /// RockFamily/SingleOwnershipFamilies is left in place - dead code for
    /// rock specifically now (IsNeverLootItem is checked first at the same
    /// choke point and already refuses it), but harmless, and still the
    /// live mechanism for the other real duplicate families (pickaxe/
    /// hatchet/bow/mace).
    /// </summary>
    private static readonly string[] NeverLootShortnames =
    {
        "torch", "torch.torch.skull", "divertorch",
        "bone.fragments", "humanmeat.raw", "skull.human", "grub", "worm",
        "heavy.plate.helmet", "heavy.plate.jacket", "heavy.plate.pants",
        "rock", "binoculars", "smallwaterbottle", "egg",
    };

    /// <summary>
    /// Real engine-component items dropped by the "vehicle_parts" loot
    /// crate (confirmed shortnames, Bundles\items\*.json) - Lucas's own
    /// explicit correction 2026-08-14: the whole container used to be
    /// excluded from the loot search entirely (IsVehiclePartsContainer,
    /// now removed), but it also drops real scrap/components a bot DOES
    /// want, so that threw those out along with the parts. Now the
    /// container itself is a normal candidate again (see the two
    /// TryFindNearestLootContainer filters above, IsVehiclePartsContainer
    /// no longer referenced there) and only these specific items are
    /// skipped at the same TryTransferSingleItem/TryFindNearestDroppedItem
    /// choke point every other NeverLootShortnames entry already uses -
    /// scrap and everything else in the crate still gets picked up
    /// normally. All three real Component-tier variants included for each
    /// part (carburetor/crankshaft/piston/sparkplug/valve all go 1-3),
    /// smallengine isn't tiered. "gears" removed from this list
    /// (2026-08-16, Lucas's own explicit request) - still no bot-crafting/
    /// vehicle-repair use case for it, but it's a real inventory item worth
    /// having on hand regardless. Still "grade A dead weight for now"
    /// reasoning for the rest of this list.
    /// </summary>
    private static readonly string[] VehiclePartShortnames =
    {
        "carburetor1", "carburetor2", "carburetor3",
        "crankshaft1", "crankshaft2", "crankshaft3",
        "piston1", "piston2", "piston3",
        "sparkplug1", "sparkplug2", "sparkplug3",
        "valve1", "valve2", "valve3",
        "smallengine",
    };

    /// <summary>
    /// Every real seed shortname shares this exact prefix, matched by prefix rather than an exhaustive list so a future new seed type is covered automatically.
    /// </summary>
    private const string SeedShortnamePrefix = "seed.";

    /// <summary>
    /// Scientist-exclusive suit shortname prefixes. These suits aren't obtainable by a real player in Rust, since they're NPC-exclusive gear.
    /// </summary>
    private static readonly string[] ScientistExclusiveSuitPrefixes = { "hazmatsuit_scientist", "scientistsuit" };

    /// <summary>
    /// Decorative/furniture/junk-tier items that are dead weight with no real use to a bot: deployable decor, scrap-tier clutter, low-value misc items, and base-defense props not yet meaningful without base-building. Validated against the live item database at boot by ValidateNeverLootShortnames below.
    /// </summary>
    private static readonly string[] JunkDecorationShortnames =
    {
        "table", "clantable", "rug.bear", "bbq", "spinner.wheel",
        "bone.fragments", "plantfiber", "can.tuna.empty", "can.beans.empty", "electric.igniter",
        "fun.guitar",
        "flashlight.held", "flare", "handcuffs", "blood", "boomerang",
        "knife.butcher", "pistol.eoka", "bone.club",
        "wall.window.bars.wood", "shutter.wood.a", "barricade.sandbags", "barricade.stone", "spikes.floor", "spikes.trap",
        "sign.wooden.small", "sign.wooden.medium", "sign.wooden.large", "sign.wooden.huge",
        "tunalight", "bucket.water",
        // Distinct from IsMailbox's own container-prefab exclusion, which stops bots looting FROM a placed mailbox; this stops them picking one up as loot.
        "mailbox",
        // Loot-only exclusion, deliberately not also a physical movement obstacle.
        "trap.bear",
    };

    /// <summary>
    /// Prefix-matched siblings of JunkDecorationShortnames, for item families with several size/variant suffixes rather than one exact shortname. Not validated against the live item database, since a prefix has no single resolve check.
    /// </summary>
    private static readonly string[] JunkDecorationPrefixes =
    {
        "sign.post", "sign.pictureframe", "planter", "rug", "spear.", "shopfront",
    };

    /// <summary>
    /// Checks every JunkDecorationShortnames entry against the live item database at startup, since this list is best-effort and unvalidated by an offline scan.
    /// </summary>
    private void ValidateNeverLootShortnames()
    {
        List<string> unresolved = new();

        foreach (string shortname in JunkDecorationShortnames)
        {
            if (ItemManager.FindItemDefinition(shortname) == null)
            {
                unresolved.Add(shortname);
            }
        }

        if (unresolved.Count > 0)
        {
            Puts($"WARNING: JunkDecorationShortnames contains {unresolved.Count} shortname(s) that don't resolve to any real item - these will never be excluded from looting since they don't match anything: {string.Join(", ", unresolved)}.");
        }
        else
        {
            Puts($"JunkDecorationShortnames validated - all {JunkDecorationShortnames.Length} shortnames resolve to real items.");
        }
    }

    /// <summary>
    /// Whether item is on the "never worth picking up" list. Independent of ownership, unlike ShouldSkipDuplicateItem, since these items are never useful regardless of what's already owned.
    /// </summary>
    private static bool IsNeverLootItem(string shortname)
    {
        return Array.IndexOf(NeverLootShortnames, shortname) >= 0
            || shortname.StartsWith(SeedShortnamePrefix, StringComparison.OrdinalIgnoreCase)
            || Array.Exists(ScientistExclusiveSuitPrefixes, prefix => shortname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || Array.IndexOf(JunkDecorationShortnames, shortname) >= 0
            || Array.Exists(JunkDecorationPrefixes, prefix => shortname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || Array.IndexOf(VehiclePartShortnames, shortname) >= 0;
    }

    /// <summary>
    /// Whether npc should skip picking up shortname because it already owns something from the same SingleOwnershipFamilies entry. Checked against the whole inventory (main + belt), not just what's equipped.
    /// </summary>
    private bool ShouldSkipDuplicateItem(BasePlayer npc, string shortname)
    {
        // A survivor with a ready firearm has no use for a bow or arrows.
        if ((Array.IndexOf(NonCombatCapableRangedWeaponShortnames, shortname) >= 0 || shortname.StartsWith("arrow.", StringComparison.Ordinal))
            && HasReadyFirearm(npc))
        {
            return true;
        }

        foreach (string[] family in SingleOwnershipFamilies)
        {
            if (Array.IndexOf(family, shortname) < 0)
            {
                continue;
            }

            return npc.inventory.containerMain.itemList
                .Concat(npc.inventory.containerBelt.itemList)
                .Any(item => Array.IndexOf(family, item.info.shortname) >= 0);
        }

        return false;
    }

    /// <summary>
    /// Whether npc should skip picking up a wearable candidate because it
    /// already owns something (worn OR unworn - a spare in the bag is
    /// just as much "already have this" as one on the body) that
    /// genuinely conflicts with it (real CanExistWith slot check) and
    /// scores as good or better. Lucas's own framing, applied at the
    /// moment of picking the item up rather than after: "do they have
    /// wooden armour and I have road sign jacket? yes? I don't take the
    /// armour." The inverse (a real upgrade) is deliberately NOT decided
    /// here - EvaluateAndUpgradeArmor still does that full evaluation
    /// (and the actual equip + drop-the-old-one) once the item is
    /// actually in inventory; this is purely the "don't even bother
    /// carrying this, it's already outclassed" pre-filter.
    /// </summary>
    private bool ShouldSkipInferiorArmor(BasePlayer npc, Item candidate)
    {
        // A backpack has its own slot and its own rule (2026-09-21) - skip it
        // only if the worn one is at least as big.
        if (IsBackpackItem(candidate))
        {
            Item wornBackpack = GetWornBackpack(npc);

            return wornBackpack != null && GetBackpackCapacity(wornBackpack) >= GetBackpackCapacity(candidate);
        }

        ItemModWearable candidateWearable = candidate.info.GetComponent<ItemModWearable>();

        if (candidateWearable == null)
        {
            return false;
        }

        // Low-tier exact duplicates are skipped outright, regardless of
        // score - see IsLowTierArmor's own doc comment. Checked against
        // everything already owned (worn AND unworn in main/belt), unlike
        // the tier/score comparison below which is worn-only - a spare
        // balaclava already in the bag is just as pointless as a second
        // one on the body.
        if (IsLowTierArmor(candidate.info.shortname)
            && npc.inventory.containerWear.itemList
                .Concat(npc.inventory.containerMain.itemList)
                .Concat(npc.inventory.containerBelt.itemList)
                .Any(owned => owned.info.shortname == candidate.info.shortname))
        {
            return true;
        }

        float candidateScore = GetArmorProtectionScore(candidate);

        // Compared against WORN items only, not everything owned - a live
        // correction: "a player with multiple sets of better armour is
        // good, not bad." Owning a second (or third) piece equal to or
        // better than what's currently worn is a real, deliberate backup/
        // upgrade reserve, not clutter - only something genuinely WORSE
        // than what's already on the body is worth skipping. Strict ">"
        // (not ">=") specifically so an equal-tier piece still gets
        // picked up as a spare, per the same correction - this general
        // rule is what the low-tier exact-duplicate check above
        // deliberately overrides for cheap gear specifically.
        return npc.inventory.containerWear.itemList.Any(worn =>
        {
            ItemModWearable wornWearable = worn.info.GetComponent<ItemModWearable>();

            if (wornWearable == null || candidateWearable.CanExistWith(wornWearable))
            {
                return false;
            }

            return GetArmorProtectionScore(worn) > candidateScore;
        });
    }

    /// <summary>
    /// Full real-world looting priority order, Lucas's own explicit
    /// ranking (2026-08-09), highest to lowest: weapons -&gt; ammunition OR
    /// explosives (grenades, rocket ammo, timed charges/satchels) -&gt;
    /// medical -&gt; armor (handled by its own dedicated logic - see
    /// ShouldSkipInferiorArmor/DropRedundantArmor - so anything that
    /// actually reaches this tier already cleared that bar) -&gt; deployable
    /// workbenches -&gt; scrap metal specifically (not components) -&gt; sulfur
    /// ore/refined sulfur -&gt; metal ore/metal fragments -&gt; wood OR stone -&gt;
    /// everything else real Resources/Component items (sheet metal,
    /// propane tank, metal pipes, ...). Used to decide what gets evicted
    /// to make room for what - Lucas's own example: "I just killed this
    /// player but my inventory is full... drop the excess components or
    /// scrap to replace them with higher tier items."
    /// </summary>
    private enum LootPriorityTier
    {
        Weapon = 0,
        AmmoOrExplosive = 1,
        Medical = 2,
        Armor = 3,
        DeployableWorkbench = 4,
        ScrapMetal = 5,
        Sulfur = 6,
        MetalOreOrFragments = 7,
        WoodOrStone = 8,
        Components = 9,

        /// <summary>
        /// Anything not otherwise classified - foodstuffs, tools (their
        /// own dedicated single-ownership logic already governs whether
        /// they're worth having at all), misc junk. Never evicted FOR
        /// (nothing "unimportant enough" to make room for), but can still
        /// be evicted BY anything above it.
        /// </summary>
        Other = 10,
    }

    private static readonly string[] DeployableWorkbenchShortnames = { "workbench1", "workbench2", "workbench3" };

    /// <summary>
    /// Real per-item tier lookup. Grenades/rocket-ammo/raid charges are
    /// deliberately pulled OUT of their raw ItemCategory (grenades are
    /// real ItemCategory.Weapon; C4/satchel charges are real
    /// ItemCategory.Tool, confirmed via the bundled item database) and
    /// into AmmoOrExplosive specifically - Lucas's own tier list groups
    /// consumable ordnance with ammunition, separate from the reusable
    /// weapons tier above it, which real Rust's own category field
    /// doesn't distinguish on its own.
    /// </summary>
    private static LootPriorityTier GetLootPriorityTier(Item item)
    {
        if (IsBackpackItem(item))
        {
            return LootPriorityTier.Armor;
        }

        string shortname = item.info.shortname;
        ItemCategory category = item.info.category;

        bool isConsumableOrdnance = shortname.StartsWith("grenade.", StringComparison.OrdinalIgnoreCase)
            || shortname == "explosive.timed"
            || shortname == "explosive.satchel";

        if (category == ItemCategory.Weapon && !isConsumableOrdnance)
        {
            return LootPriorityTier.Weapon;
        }

        if (category == ItemCategory.Ammunition || isConsumableOrdnance)
        {
            return LootPriorityTier.AmmoOrExplosive;
        }

        if (category == ItemCategory.Medical)
        {
            return LootPriorityTier.Medical;
        }

        if (item.info.GetComponent<ItemModWearable>() != null)
        {
            return LootPriorityTier.Armor;
        }

        // Blueprint fragments always rank above ores of any kind and components
        // (2026-09-21, Lucas's own spec) - same tier as deployable workbenches.
        if (shortname == "basicblueprintfragment" || shortname == "advancedblueprintfragment")
        {
            return LootPriorityTier.DeployableWorkbench;
        }

        if (Array.IndexOf(DeployableWorkbenchShortnames, shortname) >= 0)
        {
            return LootPriorityTier.DeployableWorkbench;
        }

        if (shortname == "scrap")
        {
            return LootPriorityTier.ScrapMetal;
        }

        if (shortname == "sulfur.ore" || shortname == "sulfur")
        {
            return LootPriorityTier.Sulfur;
        }

        if (shortname == "metal.ore" || shortname == "metal.fragments")
        {
            return LootPriorityTier.MetalOreOrFragments;
        }

        if (shortname == "wood" || shortname == "stones")
        {
            return LootPriorityTier.WoodOrStone;
        }

        if (category == ItemCategory.Resources || category == ItemCategory.Component)
        {
            return LootPriorityTier.Components;
        }

        return LootPriorityTier.Other;
    }

    // Cooldown for ContinueLootTask's own base-less full-inventory drop
    // fallback (2026-09-19) - see its call site's own doc comment.
    private const float FullInventoryDropCooldownSeconds = 10f;

    private readonly Dictionary<Guid, float> _lastFullInventoryDropTime = new();

    /// <summary>
    /// True immediately if there's already room; for a genuinely full
    /// inventory, only tries to make room (by dropping the single lowest-
    /// priority item the survivor owns that's ranked BELOW item's own
    /// tier - DropLowerPriorityItem) if item is high-priority enough to
    /// be worth evicting for at all (anything above LootPriorityTier.Other).
    /// A tier-9 component doesn't evict another tier-9 component just
    /// because inventory's full - only a genuine step up the priority
    /// list earns a swap.
    /// </summary>
    private bool EnsureRoomFor(BasePlayer npc, Item item)
    {
        if (!IsInventoryFull(npc.inventory) || HasBackpackRoom(npc))
        {
            return true;
        }

        LootPriorityTier candidateTier = GetLootPriorityTier(item);

        if (candidateTier == LootPriorityTier.Other)
        {
            return false;
        }

        return DropLowerPriorityItem(npc, candidateTier);
    }

    /// <summary>
    /// Drops exactly one real item ranked below candidateTier (the single
    /// LOWEST-priority one owned, so the least valuable thing goes first)
    /// to free a slot - the actual real Item.Drop mechanism, same as
    /// every other "doesn't need this anymore" case in this project
    /// (torch, rock, outclassed armor), not a delete. Returns false
    /// (nothing dropped, no room made) if the survivor owns nothing
    /// ranked lower than candidateTier at all - a full inventory of
    /// nothing but higher-or-equal-priority items is left alone rather
    /// than sacrificing something that actually matters just as much.
    /// </summary>
    private bool DropLowerPriorityItem(BasePlayer npc, LootPriorityTier candidateTier)
    {
        // Keycards protected from eviction (2026-08-21, Lucas's own
        // explicit request: "bots should never drop keycards, like ever") -
        // real live bug this fixes: keycards have no dedicated
        // LootPriorityTier of their own, so they fell into the catch-all
        // Other tier (lowest priority, first evicted whenever inventory's
        // full) - a live trace showed a survivor repeatedly drop-then-
        // immediately-re-pick-up its own keycard_green/blue/red as
        // inventory oscillated between full and not-full (15+ "picked up a
        // dropped keycard_X" lines in about 10 real seconds), burning
        // nothing functionally but reading as a genuinely broken loop and
        // risking the bot walking off without a keycard a puzzle route
        // still needs.
        //
        // Green and blue are both exceptions (Lucas's own same-day follow-
        // ups - green first, then blue "if a bot does however get multiple
        // blue cards, a max of 3 should be carried with them getting
        // evicted in favour of the same rules applied for green
        // keycards"): ShouldSkipExcessKeycard already caps normal PICKUP at
        // each one's own MaxOwned, but a survivor can still end up owning
        // more (spawncardtest, an admin give, a save from before that cap
        // existed) - anything beyond the real cap is a genuine spare
        // duplicate, not a protected puzzle tool, so it stays evictable in
        // favor of something Lucas explicitly called out as more valuable
        // (a weapon or medium/high-tier armor - both already rank above
        // Other in LootPriorityTier, so they'd win this comparison
        // naturally once the excess copies stop being blanket-protected).
        // Red keycards get NO such exception at all (Lucas's own explicit
        // instruction: "no exception, it never drops them or avoids them")
        // - no cap, no eviction, period, regardless of how many are owned.
        int ownedGreenKeycards = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Count(owned => owned.info.shortname == GreenKeycardShortname);

        int ownedBlueKeycards = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Count(owned => owned.info.shortname == BlueKeycardShortname);

        Item deadWeight = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(owned => GetDropRank(npc, owned) > (int)candidateTier
                && (!KeycardShortnames.ContainsValue(owned.info.shortname)
                    || (owned.info.shortname == GreenKeycardShortname && ownedGreenKeycards > GreenKeycardMaxOwned)
                    || (owned.info.shortname == BlueKeycardShortname && ownedBlueKeycards > BlueKeycardMaxOwned)))
            .OrderByDescending(owned => GetDropRank(npc, owned))
            .FirstOrDefault();

        if (deadWeight == null)
        {
            return false;
        }

        Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
        Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

        deadWeight.Drop(dropPosition, dropVelocity);

        return true;
    }

    /// <summary>
    /// Real green/blue keycard shortnames (Rust's own item database) -
    /// each capped at its own MaxOwned rather than folded into
    /// SingleOwnershipFamilies/ShouldSkipDuplicateItem (2026-08-16, Lucas's
    /// own explicit request for green: "have the bot only pickup 2 green
    /// cards in total, if it has more than 2, avoid them"; extended
    /// 2026-08-21 to blue at a cap of 3, same reasoning, Lucas's own
    /// explicit follow-up) since that system only ever supports a cap of
    /// exactly 1 (any owned = skip), not a genuine numeric limit. Red
    /// deliberately has NO cap/entry here at all (Lucas's own explicit
    /// instruction: "Red keycards, no exception, it never drops them or
    /// avoids them") - red stays fully protected and always pickable,
    /// unlike green/blue which both get sacrificed past their own cap in
    /// favor of something more valuable (see DropLowerPriorityItem's own
    /// doc comment).
    /// </summary>
    private const string GreenKeycardShortname = "keycard_green";
    private const int GreenKeycardMaxOwned = 2;
    private const string BlueKeycardShortname = "keycard_blue";
    private const int BlueKeycardMaxOwned = 3;

    /// <summary>
    /// Whether npc should skip picking up shortname because it already owns
    /// that keycard's own cap (GreenKeycardMaxOwned/BlueKeycardMaxOwned) or
    /// more - checked against the survivor's whole inventory (main + belt),
    /// same scope ShouldSkipDuplicateItem already uses for its own
    /// duplicate checks. Only ever true for green/blue; every other item
    /// (including red, which has no cap) is untouched.
    /// </summary>
    private bool ShouldSkipExcessKeycard(BasePlayer npc, string shortname)
    {
        int maxOwned;

        if (shortname == GreenKeycardShortname)
        {
            maxOwned = GreenKeycardMaxOwned;
        }
        else if (shortname == BlueKeycardShortname)
        {
            maxOwned = BlueKeycardMaxOwned;
        }
        else
        {
            return false;
        }

        int owned = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Count(item => item.info.shortname == shortname);

        return owned >= maxOwned;
    }

    /// <summary>
    /// The actual real per-item transfer both TransferAllItems (bulk) and
    /// LootCorpseAndContinue's paced per-item loop use - pulled out once
    /// the corpse loot pacing needed the exact same "try main inventory,
    /// fall back to GiveItem's own smarter placement" logic one item at a
    /// time instead of all at once. Also where IsNeverLootItem/
    /// ShouldSkipDuplicateItem/ShouldSkipInferiorArmor/
    /// ShouldSkipExcessKeycard are all enforced - a single choke point
    /// every caller shares, so a container full of loot correctly leaves a
    /// torch, a seed packet, a redundant 3rd hatchet, an already-outclassed
    /// armor piece, or a 3rd green keycard behind while still taking
    /// everything else in the same container - applies identically to
    /// barrels/crates, corpses, and dropped bags, since they all funnel
    /// through here.
    /// </summary>
    private bool TryTransferSingleItem(Item item, PlayerInventory to, BasePlayer npc)
    {
        if (IsNeverLootItem(item.info.shortname)
            || ShouldSkipDuplicateItem(npc, item.info.shortname)
            || ShouldSkipInferiorArmor(npc, item)
            || ShouldSkipExcessKeycard(npc, item.info.shortname)
            || ShouldSkipExcessWood(npc, item))
        {
            return false;
        }

        Item wornBackpack = GetWornBackpack(npc);

        // Bulk goods and spares go in the backpack FIRST (2026-09-21, Lucas's
        // own spec: extra weapons, spare armour/clothing, ores, wood, stones
        // etc fill the backpack), leaving the main slots for what the
        // survivor actively uses. Anything better than what it already
        // owns still goes to main so the equip logic (which only looks at
        // main/belt) can see it.
        if (wornBackpack?.contents != null && ShouldPreferBackpack(npc, item) && item.MoveToContainer(wornBackpack.contents))
        {
            return true;
        }

        if (item.MoveToContainer(to.containerMain) || to.GiveItem(item))
        {
            return true;
        }

        return wornBackpack?.contents != null && item.MoveToContainer(wornBackpack.contents);
    }

    // Real cap on wood picked up from containers/corpses/dropped bags
    // (2026-09-21, live report: '51RustyBuzzard' carrying 10,000 wood).
    // HasEnoughWoodAlready only ever gated the ways a survivor FELLS wood;
    // wood also arrives by looting other survivors' corpses and dropped
    // bags (a killed bot's whole stockpile), which had no cap at all, so
    // stockpiles simply snowballed from bot to bot. The biggest real base
    // design needs ~2,100 wood (+1,000 upkeep buffer), so this leaves room.
    private const int WoodPickupCap = 3500;

    private bool ShouldSkipExcessWood(BasePlayer npc, Item item)
    {
        if (item.info.shortname != WoodShortname)
        {
            return false;
        }

        return npc.inventory.GetAmount(item.info.itemid) >= WoodPickupCap;
    }

    private bool IsContainerFull(ItemContainer container)
    {
        return container.itemList.Count >= container.capacity;
    }

    /// <summary>
    /// Whether both main and belt are full - wear isn't checked, since
    /// that's for worn clothing/armor slots specifically, not general
    /// carry capacity the way "toolbelt/inventory" was meant.
    /// </summary>
    private bool IsInventoryFull(PlayerInventory inventory)
    {
        return IsContainerFull(inventory.containerMain) && IsContainerFull(inventory.containerBelt);
    }

    // ---------------------------------------------------------------
    // Stuck recovery - an escalating "try everything before giving up"
    // sequence for when StartWalking's own onFailed fires (genuine
    // Stuck/NoPath, or the no-real-progress give-up), built specifically
    // for the two failure modes actually seen in live traces this
    // session: a bot standing on a small navmesh island disconnected
    // from the rest of the map (confirmed via decompiled-evidence NoPath
    // results at every distance tried), and a bot fighting local
    // obstruction near cluttered terrain (junkpile scatter) for the
    // final meter or two of an otherwise-real path. Currently wired into
    // the loot task only (ContinueLootTask calls StartWalkingWithRecovery
    // instead of StartWalking directly) - kept scoped here rather than
    // promoted to a general Commands.cs primitive until another caller
    // actually needs it.
    //
    // Tier 0 - wiggle: a few short local steps (back, left, right,
    // forward, in that order - backing away first reads as the most
    // natural "oops, blocked" reaction, forward is literally the
    // direction that got it stuck so it's tried last) using the exact
    // same local-stepping primitives ordinary walking already uses
    // (NavigationManager.TryGetNextStep + ApplyMovementStep), spread
    // over real ticks like a normal walk rather than an instant snap -
    // the whole point is to look like a real player trying a different
    // direction, not a teleport in disguise.
    // Tier 1 - navmesh nudge: NavMesh.SamplePosition to find the nearest
    // point actually on Rust's baked navmesh within a growing radius,
    // then a real StartWalking there - aimed specifically at the
    // "standing on a disconnected island" failure mode, since that's a
    // real fix for the actual problem rather than a guess.
    // Tier 2 - emergency teleport (deliberately last resort): a short
    // (3-5m), validated relocation - checked against real ground and a
    // clear landing spot before committing, so it can't relocate a
    // survivor into an equally bad spot. Only reached after both cheaper
    // tiers already failed.
    // Tier 3 - genuine give-up: every option exhausted, hand back to
    // whatever the original caller wanted to happen on failure.
    //
    // Each tier retries the ORIGINAL destination after succeeding
    // locally - the goal at every stage is "become unstuck enough for
    // the real walk to work," not to replace it.
    // ---------------------------------------------------------------

    // Real pause before each escalation tier/wiggle-direction change - a
    // live report noted that when a survivor is genuinely surrounded
    // (every wiggle direction blocked on its very first probe tick, the
    // navmesh nudge unreachable), the WHOLE wiggle -> nudge -> teleport
    // chain could resolve in well under a second - functionally correct,
    // but reads as an instant glitch/warp to a real player watching,
    // rather than a bot visibly trying something. Facing toward the next
    // thing being attempted (the next wiggle direction, or the
    // destination before a tier change) during this pause is what turns
    // it into a believable "looking around, reconsidering" beat instead
    // of a dead, silent delay.
    private const float StuckReassessPause = 0.5f;

    /// <summary>
    /// Angles (degrees, off the straight line from npc to destination)
    /// tried in turn when tier 0 wiggling. Previously a fixed
    /// facing-relative Vector3 set (back/left/right/forward) - a live
    /// report caught the "back" entry doing real damage: it's a pure 180°
    /// reversal of whatever direction the survivor was already facing
    /// (i.e. toward destination), which almost always succeeds (it's
    /// retracing the exact path just walked), reports wiggled=true, and
    /// immediately retries the SAME destination - walking straight back
    /// into the identical block. An angled offset off the destination
    /// direction actually changes the approach line instead of just
    /// retreating and re-approaching the same one. Same fix, same
    /// reasoning as ContainerRepositionAngles - see its own doc comment.
    /// </summary>
    private static readonly float[] WiggleAngles = { 45f, -45f, 90f, -90f, 135f, -135f };
    private const float WiggleProbeDistance = 2f;
    private const int WiggleMaxStepsPerDirection = 20;
    private const float WiggleMinProgressDistance = 1f;

    private static readonly float[] NavMeshNudgeRadii = { 5f, 10f, 15f };

    private const int EmergencyTeleportAttempts = 6;
    private const float EmergencyTeleportMinDistance = 3f;
    private const float EmergencyTeleportMaxDistance = 5f;

    /// <summary>
    /// Character.Id -> the real-time timestamp this survivor entered
    /// stuck-recovery (EscalateStuckRecovery, tier 0) and hasn't yet
    /// resolved - backs /lr.tp.stuck (2026-08-15) so an admin can jump
    /// straight to whichever bot is actually bugged out right now instead
    /// of eyeballing 200 bots on the map. Set/cleared only from
    /// EscalateStuckRecovery itself (see its own doc comment) - a survivor
    /// present here has been stuck continuously since the stored timestamp.
    /// </summary>
    private readonly Dictionary<Guid, float> _stuckSince = new();

    private void MarkStuck(Survivor survivor)
    {
        if (!_stuckSince.ContainsKey(survivor.Character.Id))
        {
            _stuckSince[survivor.Character.Id] = Time.realtimeSinceStartup;
        }
    }

    private void ClearStuck(Survivor survivor)
    {
        _stuckSince.Remove(survivor.Character.Id);
    }

    /// <summary>
    /// Drop-in replacement for StartWalking that runs the full stuck-
    /// recovery escalation (see the section comment above) before
    /// finally surfacing onFailed to the caller.
    /// </summary>
    // How close to home even bothers considering a door crossing at all
    // (2026-08-29) - just a coarse "is this even worth checking" gate for
    // ExitHomeIfInside, wide enough that a survivor anywhere near its own
    // base is covered.
    private const float HomeCrossingRadius = 20f;

    // Real minimum pause after opening a recorded door route's door, before
    // playback starts (2026-09-01, live report: "instant inside->outside in
    // a split second... doors didn't even open"). These recorded routes
    // only span a few metres (the recording itself was slow/careful), and
    // StartGhostRoute always plays back at fixed RunSpeed regardless of the
    // original recorded pacing - covering a few metres at running speed
    // takes well under a second, not enough time for the door's own real
    // swing animation to even be visible before the survivor's already
    // past it. This is a deliberate, isolated fix at the two door-route
    // call sites only - NOT a change to StartGhostRoute's own shared
    // playback speed, which every monument route also depends on and has
    // its own long history of carefully live-tuned fixes.
    private const float DoorRouteOpenAnimationDelay = 0.6f;

    // Real fix for the false-positive door-hijack (2026-09-01, live report:
    // "constantly phasing up and down through the ceiling and opening the
    // door/closing it non-stop" while the bot was actually just walking to
    // an ordinary outdoor task destination - an ore node/fuel point ~8m
    // from the cupboard, well outside the base's actual walls). The ORIGINAL
    // 20m HomeCrossingRadius was being reused as the inside-vs-outside test
    // for BOTH TryGetHomeDoorCrossing and TryStartHomeDoorRouteCrossing -
    // correct for "starting deep inside and heading well outside" as the
    // doc comment above says, but 20m is far wider than any real tier0
    // footprint (doors sit ~4-5m from the cupboard in every recorded base
    // so far), so ANY ordinary task destination that merely happens to
    // land within 20m of the cupboard - not actually inside the walls -
    // got misclassified as "entering home" and forced through the
    // recorded/computed door route, which has no idea the real destination
    // was never past the door at all. Tuned specifically to tier0 (the
    // only tier currently in scope) - comfortably past every recorded
    // tier0 door distance, comfortably short of the false-trigger distance
    // actually observed (~8.3m). Revisit if/when tier1+ bases are added -
    // a larger design could need a bigger value here.
    private const float HomeInteriorRadius = 6f;

    /// <summary>
    /// Real proactive door-crossing detection (2026-08-29, second round -
    /// Lucas's own explicit ask: "is it possible to have a ghostroute
    /// added for the different bases... so it knows how to get in and
    /// out?"). Reactive stuck-detection (EscalateStuckRecovery's own
    /// closed-door branch) turned out not reliable enough on its own -
    /// this catches the crossing BEFORE the survivor ever gets close
    /// enough to trigger a stuck episode at all, using the real computed
    /// route ComputeHomeDoorRoute already built (LivingRust.BaseBuilding.cs)
    /// the moment this specific base finished building. A plain distance-
    /// from-cupboard test on both ends of the walk (not a real geometric
    /// "which side of the door plane" check) - simple, and correct for
    /// exactly the shape of walk this needs to catch: starting deep inside
    /// a base and heading well outside it, or the reverse, not a walk that
    /// merely passes near the perimeter without crossing through.
    /// </summary>
    private bool TryGetHomeDoorCrossing(Survivor survivor, Vector3 destination, out Vector3 nearPoint, out Vector3 farPoint)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || home.DoorPosition == Vector3.zero || npc == null)
        {
            nearPoint = default;
            farPoint = default;
            return false;
        }

        // Real live bug (2026-08-29, second round - Lucas's own report:
        // right after settask, the bot ran the WHOLE crossing dance 2-3
        // times in a row before finally heading off to loot). Root cause:
        // a normal loot decision loop re-evaluates/re-issues a fresh
        // top-level walk request (recoveryTier 0) more than once in quick
        // succession as it settles on a real target - each one
        // independently re-triggered the FULL near-point -> door -> far-
        // point sequence from scratch while a previous one was still
        // running. A DISTANCE-based "already near the door" guard was
        // tried here first and made things WORSE (2026-08-29, third round -
        // Lucas's own report: "just phases through the walls, doesn't
        // even go near the doors... straight through the walls") - a
        // survivor that just finished BUILDING its own door is almost
        // always already standing close to it, so that guard was
        // disabling the crossing on the very first, correct attempt too,
        // silently falling through to the general last-resort phase
        // fallback instead (which has no door awareness at all - hence
        // straight through the nearest wall). A real per-Character busy
        // flag is the correct fix instead - skip only while a crossing
        // for THIS survivor is actually still in flight, never based on
        // where it happens to be standing.
        if (_activeHomeDoorCrossings.Contains(survivor.Character.Id))
        {
            nearPoint = default;
            farPoint = default;
            return false;
        }

        float thresholdSqr = HomeInteriorRadius * HomeInteriorRadius;
        bool currentlyInside = (npc.transform.position - home.Position).sqrMagnitude < thresholdSqr;
        bool destinationInside = (destination - home.Position).sqrMagnitude < thresholdSqr;

        if (currentlyInside == destinationInside)
        {
            nearPoint = default;
            farPoint = default;
            return false;
        }

        // Real live bug (2026-08-29, fourth round - Lucas's own report:
        // "as soon as I do settask they instantly teleport out of the
        // foundation"). This was backwards: nearPoint is meant to be the
        // point reachable via ordinary collision-respecting StartWalking
        // WITHOUT crossing the door (same side the survivor is already
        // on), with CrossHomeDoor only taking over once actually there.
        // Swapped, a currently-inside survivor got handed OutsidePoint -
        // a point on the FAR side of its own closed door/wall - as a
        // plain walk target, which StartWalking's real collision has no
        // way to reach, immediately failing onto a raw walk straight at
        // the real final destination instead (skipping CrossHomeDoor's
        // open/phase/close sequence entirely) and racing up the general
        // stuck-recovery ladder toward its own last-resort teleport/phase
        // tiers almost immediately - reading exactly as "instantly
        // teleports out."
        nearPoint = currentlyInside ? home.InsidePoint : home.OutsidePoint;
        farPoint = currentlyInside ? home.OutsidePoint : home.InsidePoint;

        VerbosePuts($"home-door-crossing: '{survivor.Character.Alias}' computed-geometry crossing triggered (currentlyInside={currentlyInside}) toward {destination}.");

        return true;
    }

    // Real per-Character busy flag (2026-08-29) - see
    // TryGetHomeDoorCrossing's own doc comment for the full story on why
    // this replaced an earlier, broken distance-based guard. Set the
    // moment a crossing starts, cleared once the whole sequence (open,
    // cross, close) genuinely finishes or gives up - never left set on a
    // path that doesn't clear it.
    private readonly HashSet<Guid> _activeHomeDoorCrossings = new();

    /// <summary>
    /// Real precomputed crossing (2026-08-29, second round - Lucas's own
    /// explicit ask: "the bot will have to open and close the door upon
    /// leaving the base and also when entering... if the doors are left
    /// open it defeats the purpose of having doors with codelocks").
    /// Finds the real live Door entity nearest the base's own known
    /// DoorPosition (found fresh each time rather than stored by
    /// NetworkableId, so this self-heals if the door entity ever gets
    /// recreated), opens it if closed, phases straight through the door's
    /// own center and out the far side - the same real "known point, no
    /// pathfinding needed" approach GhostThroughOwnDoor already uses, just
    /// anchored to the design's own precomputed route - then closes it
    /// again once safely clear of the frame, real security restored
    /// either direction.
    /// </summary>
    /// <summary>
    /// Real isolated fallback (2026-09-01, Lucas's own explicit call after
    /// a long session fighting collision/navmesh/network-sync issues on
    /// the "realistic" door crossing: "resort to purely ghostroute into
    /// the base... deposit loot... craft items"). Deliberately touches
    /// NONE of tonight's other crossing machinery - no _activeHomeDoorCrossings
    /// guard, no collision walk, no navmesh dependency, no busy-flag races
    /// with ExitHomeIfInside/TryStartHomeDoorRouteCrossing. Just chains
    /// plain StartPhasingToDestination (the same proven primitive
    /// StartGhostRoute itself is built on, and monument routes have used
    /// reliably all along) across every recorded door route's waypoints in
    /// nearest-first order, ending at the cupboard. Doors still open/close
    /// for real (Lucas's own explicit ask, 2026-09-01: "we still need
    /// doors to open/close once the bot enters and passes them") using the
    /// exact SetOpen+SendNetworkUpdate pair already proven working on
    /// video this session - only the MOVEMENT itself is simplified to
    /// guaranteed-reliable phasing, not the door visuals.
    /// </summary>
    // Real hard ceiling on the WHOLE ghost-enter sequence (2026-09-01,
    // Lucas's own explicit bar: "as long as the bot doesn't just stay
    // there indefinitely... grab tools, deposit loot, head off on a new
    // task... probably a 10-15 second task"). Doesn't matter if a leg is
    // still visually imperfect (see this function's own Y-correction doc
    // comment) - what matters now is the survivor is GUARANTEED to
    // actually finish and move on with its life within a bounded window,
    // never stuck oscillating for 90+ real seconds the way the live trace
    // caught it doing.
    private const float GhostEnterMaxSeconds = 15f;

    private void GhostEnterHomeForDeposit(Survivor survivor, Action onCompleteRaw)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed || home.DoorRoutes.Count == 0)
        {
            onCompleteRaw?.Invoke();
            return;
        }

        bool completed = false;

        void onComplete()
        {
            if (completed)
            {
                return;
            }

            completed = true;
            onCompleteRaw?.Invoke();
        }

        timer.Once(GhostEnterMaxSeconds, () =>
        {
            if (!completed)
            {
                VerbosePuts($"ghost-enter: '{survivor.Character.Alias}' hit the {GhostEnterMaxSeconds:F0}s hard ceiling - forcing completion regardless of where it currently is.");
                onComplete();
            }
        });

        List<HomeDoorRoute> remainingRoutes = new(home.DoorRoutes);

        Door ResolveDoorNear(Vector3 anchor)
        {
            Collider[] hits = Physics.OverlapSphere(anchor, 1f, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

            foreach (Collider hit in hits)
            {
                Door candidate = hit.GetComponentInParent<Door>();

                if (candidate != null && !candidate.IsDestroyed)
                {
                    return candidate;
                }
            }

            return null;
        }

        void PlayGhostWaypoints(List<Vector3> waypoints, int index, Action onRouteComplete)
        {
            if (index >= waypoints.Count)
            {
                onRouteComplete?.Invoke();
                return;
            }

            StartPhasingToDestination(
                survivor,
                waypoints[index],
                onArrived: () => PlayGhostWaypoints(waypoints, index + 1, onRouteComplete),
                onFailed: () => PlayGhostWaypoints(waypoints, index + 1, onRouteComplete));
        }

        // Real fix (2026-09-01, live report: "first door opens ~10m away",
        // "second door doesn't open at all", "ends up on the roof" - trace
        // evidence showed sustained Y oscillation between two floor
        // heights for 90+ real seconds, not a one-time miscalculation).
        // Don't try to correct the WHOLE route's height from wherever the
        // survivor happens to be standing when this starts (unreliable -
        // they might not be anywhere near the route's own start yet, so
        // the "correction" was really just measuring an unrelated height
        // difference and applying it everywhere, compounding across two
        // routes). Instead: phase to the route's own (uncorrected)
        // near-end FIRST - a short hop, low risk even if slightly off -
        // THEN measure the real correction from where the survivor
        // genuinely landed, THEN open the door (only once actually close,
        // fixing the "opens 10m away" report) and play the rest of the
        // route with that correction applied.
        const float DepositApproachDistance = 1.5f;

        void PlayNextRoute()
        {
            if (remainingRoutes.Count == 0)
            {
                // Real fix for "ends up on the roof" (2026-09-01, live
                // report + Lucas's own diagnosis: "is the bot just trying
                // to get to the centre of the toolcupboard? ... offset by
                // 1 metre, it doesn't need to be hugging it"). Phasing
                // exactly to home.Position (the cupboard's own real
                // transform center, a solid object) overlaps its collider;
                // approaching from the survivor's own current direction
                // instead keeps a real gap.
                Vector3 approachDirection = (npc.transform.position - home.Position);
                approachDirection.y = 0f;

                if (approachDirection.sqrMagnitude < 0.01f)
                {
                    approachDirection = npc.transform.forward;
                }

                Vector3 approachPoint = home.Position + approachDirection.normalized * DepositApproachDistance;
                approachPoint.y = home.Position.y;

                StartPhasingToDestination(survivor, approachPoint, onArrived: onComplete, onFailed: onComplete);
                return;
            }

            HomeDoorRoute bestRoute = null;
            bool reversed = false;
            float bestDistSqr = float.MaxValue;
            Vector3 current = npc.transform.position;

            foreach (HomeDoorRoute route in remainingRoutes)
            {
                if (route.Waypoints.Count == 0)
                {
                    continue;
                }

                float distToStart = (route.Waypoints[0] - current).sqrMagnitude;
                float distToEnd = (route.Waypoints[^1] - current).sqrMagnitude;
                float minDist = Mathf.Min(distToStart, distToEnd);

                if (minDist < bestDistSqr)
                {
                    bestDistSqr = minDist;
                    bestRoute = route;
                    reversed = distToEnd < distToStart;
                }
            }

            remainingRoutes.Remove(bestRoute);

            List<Vector3> orderedWaypoints = reversed ? Enumerable.Reverse(bestRoute.Waypoints).ToList() : bestRoute.Waypoints;

            // Real fix (2026-09-01, live report + log-confirmed: "applying
            // 2.99m Y-correction" printed AFTER the survivor had already
            // jumped to Y=29.17/the roof approaching this route's own
            // near-end). The approach hop above used to phase straight to
            // orderedWaypoints[0] AT ITS OWN recorded (possibly wrong,
            // same per-instance mismatch as always) height - so the
            // correction was being measured AFTER already landing
            // somewhere bad, locking in the wrong height as the new
            // "correct" baseline instead of fixing it. Approaching
            // HORIZONTALLY ONLY first - lining up the X/Z while holding
            // the survivor's own CURRENT (known-good) height - means the
            // correction below is always measured from a position that
            // was never touched, not one already corrupted by this hop.
            Vector3 horizontalNearEnd = orderedWaypoints[0];
            horizontalNearEnd.y = current.y;

            StartPhasingToDestination(survivor, horizontalNearEnd, onArrived: () =>
            {
                // Real per-instance height correction, now measured from
                // the survivor's own real height (untouched by the hop
                // above) against the route's recorded near-end height. See
                // this function's own doc comment for why a base (rebuilt
                // per-instance against real terrain) can't reuse
                // monument-style rigid reprojection here.
                float yCorrection = npc.transform.position.y - orderedWaypoints[0].y;
                List<Vector3> waypoints = orderedWaypoints.Select(w => w + new Vector3(0f, yCorrection, 0f)).ToList();

                VerbosePuts($"ghost-enter: '{survivor.Character.Alias}' applying {yCorrection:F2}m Y-correction to this route's {waypoints.Count} waypoint(s).");

                Door routeDoor = ResolveDoorNear(bestRoute.DoorAnchorPosition);

                if (routeDoor != null && !routeDoor.IsOpen())
                {
                    routeDoor.SetOpen(true);
                    routeDoor.SendNetworkUpdate();
                }

                PlayGhostWaypoints(waypoints, 0, () =>
                {
                    Door doorToClose = ResolveDoorNear(bestRoute.DoorAnchorPosition);

                    if (doorToClose != null && doorToClose.IsOpen())
                    {
                        doorToClose.SetOpen(false);
                        doorToClose.SendNetworkUpdate();
                    }

                    // Real safety-net height correction (2026-09-01, live
                    // report + log-confirmed: position jumped 3m UP while
                    // still mid-flight toward a lower destination, right
                    // around when this route's door closed - the exact
                    // signature this project already has a name for,
                    // DoorCloseHeightCorrectionTolerance: Unity physics
                    // shoving a survivor still overlapping a door's
                    // collider onto whatever's above, most often the floor
                    // slab of the story overhead. Applied everywhere else
                    // doors close in this project already - this new
                    // function just never had it. waypoints[^1].y is the
                    // one height already known correct here (the survivor
                    // was just phased to it).
                    BasePlayer closingNpc = survivor.Player;

                    if (closingNpc != null && !closingNpc.IsDestroyed && waypoints.Count > 0 && Mathf.Abs(closingNpc.transform.position.y - waypoints[^1].y) > DoorCloseHeightCorrectionTolerance)
                    {
                        Vector3 corrected = closingNpc.transform.position;
                        corrected.y = waypoints[^1].y;
                        closingNpc.transform.position = corrected;
                        closingNpc.MovePosition(corrected);
                        survivor.Position = corrected;
                        survivor.Character.Position = corrected;

                        VerbosePuts($"ghost-enter: '{survivor.Character.Alias}' corrected a {Mathf.Abs(closingNpc.transform.position.y - waypoints[^1].y):F2}m post-door-close height push back to {waypoints[^1].y:F2}.");
                    }

                    PlayNextRoute();
                });
            },
            onFailed: PlayNextRoute);
        }

        PlayNextRoute();
    }

    // How far from home.Position to look for a survivor's OWN placed
    // storage boxes/furnaces (2026-09-01) - same order of magnitude as
    // CheckUpgradesSearchRadius (LivingRust.Debug.cs), generous enough to
    // cover a whole base interior without picking up a neighbouring base's
    // own storage.
    private const float HomeStorageSearchRadius = 40f;

    // Same 1m stand-off proven live on both the box and furnace walk-
    // deposit test rigs (2026-09-01, Lucas's own ask) - avoids phasing
    // into the container's own collider.
    private const float HomeStorageStandOffDistance = 1f;

    // Real trigger threshold (2026-09-01, Lucas's own explicit numbers):
    // "2000+ wood in storage (not including tool cupboard)" before a
    // furnace trip is worth making, pulling exactly 1000 out per trip.
    private const int HomeFurnaceWoodTrigger = 300;
    private const int HomeFurnaceWoodFillAmount = 500;

    private static readonly string[] SmeltableOreShortnames = { "metal.ore", "sulfur.ore" };

    /// <summary>
    /// Real "what survives the trip home" filter (2026-09-01, Lucas's own
    /// explicit spec): "any components, metal fragments, scrap metal,
    /// cloth etc will go back into the bases chests... not ammo, not med
    /// syringes etc." Reuses GetLootPriorityTier - the same real
    /// categorization already used to decide what a full inventory drops -
    /// rather than a second hand-rolled category check that could quietly
    /// drift out of sync with it. Also keeps a gather tool that's the
    /// survivor's ONLY one of its kind (2026-09-01, Lucas's own follow-up:
    /// "keep those primitive tools on them and not deposit them IF they
    /// have no other tool on them... that way it isn't stuck with a rock")
    /// - deliberately checks the WHOLE pickaxe/hatchet family, not just the
    /// stone tier specifically, so this stays correct if a survivor's only
    /// pickaxe/hatchet ever happens to be a better one instead.
    ///
    /// Weapons/armor were originally excluded entirely too ("not weapons...
    /// not armour IF it intends to go outside again which is 99% of the
    /// time") - revised same session, Lucas's own explicit follow-up: "any
    /// ADDITIONAL looted weapons (ranged or melee) or additional armour/
    /// clothing gets deposited into their chests." "Additional" means a
    /// genuine spare - DepositFilteredItems only ever iterates main+belt
    /// (never containerWear, LivingRust.Debug.cs's own doc comment), so any
    /// Armor-tier item reaching this check is ALREADY unworn by
    /// construction; a Weapon-tier item is kept only if it's the real
    /// currently-active held item.
    /// </summary>
    private static bool ShouldDepositAtBase(Item item, BasePlayer npc)
    {
        // Checked before any tier logic - if the rock ever lands in a tier
        // other than Weapon it would otherwise fall through to the final
        // "deposit everything else" return below.
        if (item.info.shortname == "rock")
        {
            return false;
        }

        LootPriorityTier tier = GetLootPriorityTier(item);

        if (tier == LootPriorityTier.AmmoOrExplosive || tier == LootPriorityTier.Medical)
        {
            return false;
        }

        if (tier == LootPriorityTier.Weapon)
        {
            // The starting rock never gets deposited (2026-09-24, Lucas's
            // own live report: bots depositing rocks into their own
            // chests). Rock is real ItemCategory.Weapon in Rust, so
            // without this it hits the exact same "deposit unless it's my
            // currently active item" rule below every real firearm/melee
            // weapon does - but unlike a spare firearm (genuinely fine to
            // bank since only one is ever needed at a time), a survivor's
            // rock is its LAST-RESORT fallback tool/weapon, not a spare -
            // it's only ever not "active" because the survivor is
            // temporarily holding something else for an unrelated task,
            // not because it owns a better melee option. DropRockIfUpgraded
            // (PerformReorganizationCheck, called from every OnLootObtained)
            // is the one real mechanism that should ever remove it - once a
            // genuine upgrade exists, that drops it on the spot; this
            // method should never bank it into storage in the meantime.
            if (item.info.shortname == "rock")
            {
                return false;
            }

            // A survivor's only bow stays with it (2026-09-21) - otherwise
            // every deposit trip stripped it (a bow isn't the "active" item
            // while a hatchet is in hand) and the survival-kit upkeep
            // crafted a fresh one right after, forever.
            if (Array.IndexOf(NonCombatCapableRangedWeaponShortnames, item.info.shortname) >= 0
                && npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList)
                    .Count(i => Array.IndexOf(NonCombatCapableRangedWeaponShortnames, i.info.shortname) >= 0) <= 1)
            {
                return false;
            }

            Item activeItem = npc.GetActiveItem();
            return activeItem == null || activeItem.uid != item.uid;
        }

        if (tier == LootPriorityTier.Armor)
        {
            return true;
        }

        if (IsOnlyGatherToolOfItsFamily(item, npc))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// How many REAL firearms (WeaponGearScore-listed, excluding the bow/
    /// crossbow family - same real-firearm-vs-bow distinction
    /// ShouldDepositAtBase's own bow carve-out already draws) a survivor is
    /// currently carrying across main+belt (2026-09-23). Backs
    /// TryPursueSpareFirearmDeposit below - counts everything regardless of
    /// which one is actively held, since the point is "how many guns does
    /// this survivor own right now," not just what's in its hands.
    /// </summary>
    private static int CountRealFirearms(BasePlayer npc)
    {
        return npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Count(item => WeaponGearScore.ContainsKey(item.info.shortname)
                && Array.IndexOf(NonCombatCapableRangedWeaponShortnames, item.info.shortname) < 0);
    }

    /// <summary>
    /// See this method's own call site in ContinueLootTask for the full
    /// reasoning (2026-09-23 "don't hoard spare firearms" ask). Only ever
    /// triggers a trip once a real base exists - ShouldDepositAtBase does
    /// the actual work of deciding which specific firearm stays (the active
    /// one) versus gets deposited, this just decides WHEN that trip should
    /// happen.
    /// </summary>
    private bool TryPursueSpareFirearmDeposit(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        if (survivor.Character.Home == null || CountRealFirearms(npc) < 2)
        {
            return false;
        }

        Puts($"loot-task: '{survivor.Character.Alias}' is carrying a spare firearm - heading home to deposit it.");
        GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor));
        return true;
    }

    private static bool IsOnlyGatherToolOfItsFamily(Item item, BasePlayer npc)
    {
        string shortname = item.info.shortname;
        string[] family = Array.IndexOf(PickaxeFamily, shortname) >= 0 ? PickaxeFamily
            : Array.IndexOf(HatchetFamily, shortname) >= 0 ? HatchetFamily
            : null;

        if (family == null)
        {
            return false;
        }

        int count = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Count(i => Array.IndexOf(family, i.info.shortname) >= 0);

        return count <= 1;
    }

    /// <summary>
    /// Real "does the survivor own ANY tool from this family at all"
    /// check (2026-09-01) - used both by ShouldSkipPrimitiveChecklist
    /// (LivingRust.Crafting.cs, skip the from-scratch checklist if already
    /// tooled up) and the standing "never let a survivor go fully toolless"
    /// safety net (ContinueLootTask, LivingRust.Looting.cs).
    /// </summary>
    private bool HasAnyToolOfFamily(BasePlayer npc, string[] family)
    {
        return npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Any(i => Array.IndexOf(family, i.info.shortname) >= 0);
    }

    /// <summary>
    /// Real owned-storage lookup (2026-09-01) - "owned" via OwnerID, the
    /// same real field a placed deployable is stamped with at build time
    /// (see IsTooCloseToAnotherCupboard's own doc comment, LivingRust.
    /// BaseBuilding.cs, for the same OwnerID pattern applied to cupboards).
    /// Deliberately box.wooden.large only for now - Lucas's own explicit
    /// framing was "boxes it has made inside of its base" - not every
    /// StorageContainer within range (that would also catch a neighbour's
    /// storage sitting just inside this radius). Sorted nearest-to-home
    /// first so the deposit loop visits the most central boxes first.
    /// </summary>
    private List<StorageContainer> FindOwnedStorageBoxesNear(BasePlayer npc, Vector3 origin, float radius)
    {
        List<StorageContainer> results = new();

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is not StorageContainer container || container.IsDestroyed || container.ShortPrefabName != "box.wooden.large")
            {
                continue;
            }

            if (container.OwnerID != npc.userID || Vector3.Distance(origin, container.transform.position) > radius)
            {
                continue;
            }

            results.Add(container);
        }

        results.Sort((a, b) => Vector3.Distance(origin, a.transform.position).CompareTo(Vector3.Distance(origin, b.transform.position)));
        return results;
    }

    /// <summary>Same idea as FindOwnedStorageBoxesNear, for the survivor's own furnace(s).</summary>
    private List<BaseOven> FindOwnedFurnacesNear(BasePlayer npc, Vector3 origin, float radius)
    {
        List<BaseOven> results = new();

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is not BaseOven oven || oven.IsDestroyed || oven.ShortPrefabName != "furnace")
            {
                continue;
            }

            if (oven.OwnerID != npc.userID || Vector3.Distance(origin, oven.transform.position) > radius)
            {
                continue;
            }

            results.Add(oven);
        }

        results.Sort((a, b) => Vector3.Distance(origin, a.transform.position).CompareTo(Vector3.Distance(origin, b.transform.position)));
        return results;
    }

    /// <summary>
    /// Generalizes DepositAllItems (LivingRust.Debug.cs, built for the box
    /// deposit test) with a predicate - DepositAllItems now just calls this
    /// with an always-true filter, so the two test rigs and this real
    /// production path share one real transfer loop instead of two that
    /// could drift apart.
    /// </summary>
    private int DepositFilteredItems(BasePlayer npc, ItemContainer target, Func<Item, bool> shouldDeposit)
    {
        List<Item> items = new();

        if (npc.inventory.containerMain != null)
        {
            items.AddRange(npc.inventory.containerMain.itemList);
        }

        if (npc.inventory.containerBelt != null)
        {
            items.AddRange(npc.inventory.containerBelt.itemList);
        }

        // Backpack contents are part of what a survivor carries (2026-09-21) -
        // otherwise spares and bulk resources stored there would never be
        // banked at base.
        Item depositBackpack = GetWornBackpack(npc);

        if (depositBackpack?.contents != null)
        {
            items.AddRange(depositBackpack.contents.itemList);
        }

        int moved = 0;

        foreach (Item item in items)
        {
            if (shouldDeposit(item) && item.MoveToContainer(target))
            {
                moved++;
            }
        }

        return moved;
    }

    /// <summary>
    /// Real production deposit trip (2026-09-01, Lucas's own explicit
    /// ask) - phases (zero-collision, same as every other in-base movement
    /// this project uses) to each of the survivor's own boxes in turn,
    /// approaching with the same 1m stand-off proven on the walk-deposit
    /// test rig, and stops early the moment nothing left in inventory
    /// still passes shouldDeposit (no point visiting a second box empty-
    /// handed). Assumes the survivor is ALREADY inside (called after
    /// GhostEnterHomeForDeposit) - doesn't itself cross any door.
    /// </summary>
    private void GhostDepositIntoOwnedBoxes(Survivor survivor, Func<Item, bool> shouldDeposit, Action<int> onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            onComplete?.Invoke(0);
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);

        if (boxes.Count == 0)
        {
            onComplete?.Invoke(0);
            return;
        }

        int totalMoved = 0;

        void DepositIntoNext(int index)
        {
            BasePlayer liveNpc = survivor.Player;

            bool anyLeft = liveNpc != null && !liveNpc.IsDestroyed
                && liveNpc.inventory.containerMain.itemList.Concat(liveNpc.inventory.containerBelt.itemList).Any(shouldDeposit);

            if (index >= boxes.Count || !anyLeft)
            {
                onComplete?.Invoke(totalMoved);
                return;
            }

            StorageContainer box = boxes[index];

            if (box == null || box.IsDestroyed || box.inventory == null)
            {
                DepositIntoNext(index + 1);
                return;
            }

            Vector3 approachDirection = liveNpc.transform.position - box.transform.position;
            approachDirection.y = 0f;

            if (approachDirection.sqrMagnitude < 0.01f)
            {
                approachDirection = liveNpc.transform.forward;
            }

            Vector3 approachPoint = box.transform.position + approachDirection.normalized * HomeStorageStandOffDistance;
            approachPoint.y = box.transform.position.y;

            StartPhasingToDestination(survivor, approachPoint, onArrived: () =>
            {
                BasePlayer arrivedNpc = survivor.Player;

                if (arrivedNpc != null && !arrivedNpc.IsDestroyed && !box.IsDestroyed && box.inventory != null)
                {
                    totalMoved += DepositFilteredItems(arrivedNpc, box.inventory, shouldDeposit);
                }

                DepositIntoNext(index + 1);
            },
            onFailed: () => DepositIntoNext(index + 1));
        }

        DepositIntoNext(0);
    }

    /// <summary>
    /// Tier-upgrade pity grant (2026-09-24, Lucas's own explicit spec) -
    /// creates amount of shortname and drops it straight into the first
    /// owned box with room, since the survivor is already standing at home
    /// for this check (TryPursueTierUpgrade's own calling context). Falls
    /// back to a ground drop at home if every owned box is genuinely full,
    /// same last-resort WithdrawUpToAmount's own split-remainder handling
    /// uses rather than silently discarding the item.
    /// </summary>
    private void GrantItemIntoOwnedBoxes(List<StorageContainer> boxes, string shortname, int amount, Vector3 fallbackDropPosition)
    {
        ItemDefinition def = ItemManager.FindItemDefinition(shortname);

        if (def == null)
        {
            Puts($"WARNING: tier-upgrade pity grant couldn't create '{shortname}' - unknown shortname?");
            return;
        }

        // Real stack-sized chunks (2026-10-03): the single oversized Item this used to create is
        // what put impossible 6,639-stone "stacks" into base boxes.
        foreach (Item item in CreateStackSizedItems(def, amount))
        {
            bool placed = false;

            foreach (StorageContainer box in boxes)
            {
                if (box != null && !box.IsDestroyed && box.inventory != null && item.MoveToContainer(box.inventory))
                {
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                item.Drop(fallbackDropPosition, Vector3.zero);
            }
        }
    }

    /// <summary>
    /// Withdraws up to amount total of shortname from boxes directly into
    /// destination - a genuine container-to-container transfer (Item.
    /// MoveToContainer doesn't require passing through a player inventory
    /// in between), splitting a stack via Item.SplitItem when a single
    /// box's stack holds more than what's still needed. On a failed move
    /// (destination full/rejects it), the split remainder is merged back
    /// into its source box rather than left orphaned.
    /// </summary>
    private int WithdrawUpToAmount(List<StorageContainer> boxes, string shortname, int amount, ItemContainer destination)
    {
        int remaining = amount;

        foreach (StorageContainer box in boxes)
        {
            if (remaining <= 0)
            {
                break;
            }

            if (box == null || box.IsDestroyed || box.inventory == null)
            {
                continue;
            }

            foreach (Item item in new List<Item>(box.inventory.itemList))
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (item.info.shortname != shortname)
                {
                    continue;
                }

                if (item.amount <= remaining)
                {
                    int amt = item.amount;

                    if (item.MoveToContainer(destination))
                    {
                        remaining -= amt;
                    }
                }
                else
                {
                    Item split = item.SplitItem(remaining);

                    if (split == null)
                    {
                        continue;
                    }

                    if (split.MoveToContainer(destination))
                    {
                        remaining -= split.amount;
                    }
                    else if (!split.MoveToContainer(box.inventory))
                    {
                        split.Drop(box.transform.position, Vector3.zero);
                    }
                }
            }
        }

        return amount - remaining;
    }

    /// <summary>
    /// Moves up to maxStacks whole stacks of shortname out of boxes into
    /// destination (2026-09-01, live-confirmed correction: a real furnace
    /// only has 2 real ore input slots, not unlimited - "can only put 2
    /// stacks... 1000 of two different ores OR 2 stacks of the same
    /// ore... if it has more than this it should just re-deposit excess
    /// ores into a crate it owns"). Deliberately doesn't split stacks the
    /// way WithdrawUpToAmount does for wood - ore stacks are already
    /// capped at 1000 by the server's own stack size, so a whole-stack
    /// move is always exactly one furnace slot. Any stack left over simply
    /// stays in its box untouched (MoveToContainer is never even attempted
    /// once maxStacks is hit) - already exactly "re-deposited" since it
    /// was already sitting there from GhostDepositIntoOwnedBoxes, no
    /// separate action needed.
    /// </summary>
    private (int Amount, int Stacks) WithdrawUpToStackCount(List<StorageContainer> boxes, string shortname, int maxStacks, ItemContainer destination)
    {
        int amount = 0;
        int stacks = 0;

        foreach (StorageContainer box in boxes)
        {
            if (stacks >= maxStacks)
            {
                break;
            }

            if (box == null || box.IsDestroyed || box.inventory == null)
            {
                continue;
            }

            foreach (Item item in new List<Item>(box.inventory.itemList))
            {
                if (stacks >= maxStacks)
                {
                    break;
                }

                if (item.info.shortname != shortname)
                {
                    continue;
                }

                int amt = item.amount;

                if (item.MoveToContainer(destination))
                {
                    amount += amt;
                    stacks++;
                }
            }
        }

        return (amount, stacks);
    }

    /// <summary>
    /// Real furnace-fill trip (2026-09-01, Lucas's own explicit spec):
    /// only worth making once base storage genuinely has 2000+ wood AND
    /// some ore sitting in it - a single trip pulls exactly 1000 wood plus
    /// every ore stack found straight out of the survivor's own boxes
    /// (container-to-container, matching the walk-smelt test rig's own
    /// proven deposit-then-StartCooking order) and ignites. No-ops
    /// (onComplete straight away) if the trigger isn't met or there's no
    /// owned furnace to fill. Assumes the survivor is already inside (same
    /// assumption as GhostDepositIntoOwnedBoxes).
    /// </summary>
    private void TryFillOwnedFurnaces(Survivor survivor, Action onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            onComplete?.Invoke();
            return;
        }

        List<BaseOven> furnaces = FindOwnedFurnacesNear(npc, home.Position, HomeStorageSearchRadius);

        if (furnaces.Count == 0)
        {
            onComplete?.Invoke();
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);
        FillFurnaceAtIndex(survivor, furnaces, boxes, 0, onComplete);
    }

    // Works through every owned furnace in turn (2026-09-21, Lucas's own
    // explicit spec: keep ore + fuel loaded for a steady sulfur/metal
    // fragment supply) - this used to only ever service furnaces[0], so a
    // base's second furnace never smelted anything.
    private void FillFurnaceAtIndex(Survivor survivor, List<BaseOven> furnaces, List<StorageContainer> boxes, int index, Action afterAll)
    {
        BasePlayer npc = survivor.Player;

        if (index >= furnaces.Count || npc == null || npc.IsDestroyed)
        {
            afterAll?.Invoke();
            return;
        }

        BaseOven furnace = furnaces[index];
        Action onComplete = () => FillFurnaceAtIndex(survivor, furnaces, boxes, index + 1, afterAll);

        if (furnace == null || furnace.IsDestroyed)
        {
            onComplete();
            return;
        }

        Vector3 approachDirection = npc.transform.position - furnace.transform.position;
        approachDirection.y = 0f;

        if (approachDirection.sqrMagnitude < 0.01f)
        {
            approachDirection = npc.transform.forward;
        }

        Vector3 approachPoint = furnace.transform.position + approachDirection.normalized * HomeStorageStandOffDistance;
        approachPoint.y = furnace.transform.position.y;

        // Real B step (2026-09-19, Lucas's own explicit spec: "check
        // furnaces and remove loot from smelted ores (metal fragments,
        // charcoal etc) and top up with wood if owned"). The walk-to-
        // furnace step below used to only fire if THIS trip's own
        // wood/ore trigger was met, which meant real finished output from
        // an EARLIER trip's batch could sit uncollected in the furnace
        // indefinitely if storage never again happened to have 2000+ wood
        // AND ore at the same moment. Now visits the furnace unconditionally
        // whenever one's owned, collects anything real ALREADY smelted
        // first (regardless of this trip's own trigger), then still only
        // starts a brand new batch if the trigger's actually met.
        StartPhasingToDestination(survivor, approachPoint, onArrived: () =>
        {
            if (furnace == null || furnace.IsDestroyed || furnace.inventory == null)
            {
                onComplete?.Invoke();
                return;
            }

            int outputCollected = 0;

            foreach (Item furnaceItem in new List<Item>(furnace.inventory.itemList))
            {
                if (furnaceItem.info.shortname == "wood" || Array.IndexOf(SmeltableOreShortnames, furnaceItem.info.shortname) >= 0)
                {
                    // Still-raw fuel/ore, not finished output - leave it to
                    // keep cooking.
                    continue;
                }

                StorageContainer target = boxes.FirstOrDefault(b => b != null && !b.IsDestroyed && b.inventory != null && !IsContainerFull(b.inventory));

                if (target != null && furnaceItem.MoveToContainer(target.inventory))
                {
                    outputCollected++;
                }
            }

            Puts($"home-storage: '{survivor.Character.Alias}' visited its furnace - collected {outputCollected} smelted item stack(s), {furnace.inventory.itemList.Count} item(s) still inside.");

            int totalWood = boxes.Where(b => b != null && !b.IsDestroyed && b.inventory != null)
                .SelectMany(b => b.inventory.itemList)
                .Where(i => i.info.shortname == "wood")
                .Sum(i => i.amount);

            bool hasOre = boxes.Where(b => b != null && !b.IsDestroyed && b.inventory != null)
                .SelectMany(b => b.inventory.itemList)
                .Any(i => SmeltableOreShortnames.Contains(i.info.shortname));

            if (totalWood < HomeFurnaceWoodTrigger || !hasOre)
            {
                Puts($"home-storage: '{survivor.Character.Alias}' skipped refilling its furnace (stored wood {totalWood}/{HomeFurnaceWoodTrigger}, has ore: {hasOre}).");
                onComplete?.Invoke();
                return;
            }

            int woodMoved = WithdrawUpToAmount(boxes, "wood", HomeFurnaceWoodFillAmount, furnace.inventory);
            int oreMoved = 0;
            int oreStacksMoved = 0;

            // Real live inputSlots on the actually-spawned furnace (2026-09-01,
            // live-confirmed: "can only put 2 stacks... if it has more than
            // this it should just re-deposit excess ores into a crate it
            // owns") - reads the furnace's own real component value rather
            // than hardcoding "2", so this stays correct even if a
            // different furnace tier/config is ever used. Any ore beyond
            // this cap is left untouched in its box - already
            // "re-deposited" there from the earlier GhostDepositIntoOwnedBoxes
            // step, no separate action needed.
            foreach (string oreShortname in SmeltableOreShortnames)
            {
                if (oreStacksMoved >= furnace.inputSlots)
                {
                    break;
                }

                (int amount, int stacks) = WithdrawUpToStackCount(boxes, oreShortname, furnace.inputSlots - oreStacksMoved, furnace.inventory);
                oreMoved += amount;
                oreStacksMoved += stacks;
            }

            if (woodMoved > 0)
            {
                furnace.StartCooking();
            }

            Puts($"home-storage: '{survivor.Character.Alias}' filled its furnace with {woodMoved}x wood and {oreMoved} ore ({oreStacksMoved}/{furnace.inputSlots} ore slot(s)), IsOn={furnace.IsOn()}.");
            onComplete?.Invoke();
        },
        onFailed: onComplete);
    }

    // How much cloth a survivor pulls from its own storage to top up
    // bandages before heading back out (2026-09-19) - enough for
    // BandageMaxBatches' own real 4x-cloth-per-bandage recipe with room to
    // spare, not a bulk strip-the-base amount.
    private const int BandageStockClothWithdrawAmount = 40;

    /// <summary>
    /// Real D step (2026-09-19, Lucas's own explicit spec: "make sure they
    /// have additional bandages to go out and roam in case they get
    /// attacked... if they run out, find more hemp in the wildlife"). Pulls
    /// cloth from storage first (a real player grabs spare cloth from their
    /// own base before heading out), then reuses TryPursueBandageGoal - the
    /// exact same real craft-from-owned-cloth logic the primitive checklist
    /// already relies on - rather than re-deriving bandage-crafting from
    /// scratch. allowGather deliberately false here: sending the survivor
    /// off to find real hemp mid-base-visit would skip the door-exit step
    /// still to come (ExitHomeIfInside) further up this chain. A genuinely
    /// empty cloth supply is already handled the normal way once the
    /// survivor's back out roaming (TryPursueBandageSupplyIfHurt/
    /// TryUseMedicalItemIfHurt, both unconditional every cycle) - this is
    /// purely an opportunistic top-up from whatever's already sitting at
    /// home for free.
    /// </summary>
    private void TryStockBandagesBeforeLeaving(Survivor survivor, Action onComplete)
    {
        BasePlayer npc = survivor.Player;
        HomeBase home = survivor.Character.Home;

        if (npc == null || npc.IsDestroyed || home == null)
        {
            onComplete?.Invoke();
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);
        int clothWithdrawn = WithdrawUpToAmount(boxes, ClothShortname, BandageStockClothWithdrawAmount, npc.inventory.containerMain);

        if (clothWithdrawn > 0)
        {
            TryPursueBandageGoal(survivor, npc, new LootTaskState(), allowGather: false);
        }

        onComplete?.Invoke();
    }

    /// <summary>
    /// Real C step (2026-09-19, Lucas's own explicit spec: "upgrade
    /// gearscore (if possible) with items/clothing/tools/weapons in
    /// storage containers"). Armor reuses the exact same score/conflict
    /// comparison EvaluateAndUpgradeArmor already applies to a freshly-
    /// looted candidate (GetArmorProtectionScore + real CanExistWith slot
    /// check + ArmorUpgradeMinimumScoreGain), just sourced from owned
    /// storage instead of "just picked up" - only genuine upgrades are
    /// ever moved, so a spare/equal set sitting in storage is correctly
    /// left alone rather than churned every visit. A displaced WORN piece
    /// goes back into storage (not dropped the way EvaluateAndUpgradeArmor
    /// does for a fresh field pickup) - it's already home, keeping the
    /// spare costs nothing. Weapon uses the same WeaponPriority ranking
    /// GetWeaponGearScore/TryEquipBestArmedWeapon already use, just
    /// comparing storage's own best against whatever's already owned.
    /// </summary>
    private void TryUpgradeGearFromStorage(Survivor survivor, Action onComplete)
    {
        BasePlayer npc = survivor.Player;
        HomeBase home = survivor.Character.Home;

        if (npc == null || npc.IsDestroyed || home == null)
        {
            onComplete?.Invoke();
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);
        int armorUpgraded = 0;

        foreach (StorageContainer box in boxes)
        {
            if (box == null || box.IsDestroyed || box.inventory == null)
            {
                continue;
            }

            foreach (Item candidate in new List<Item>(box.inventory.itemList))
            {
                ItemModWearable candidateWearable = candidate.info.GetComponent<ItemModWearable>();

                if (candidateWearable == null)
                {
                    continue;
                }

                List<Item> conflicting = npc.inventory.containerWear.itemList
                    .Where(worn => !candidateWearable.CanExistWith(worn.info.GetComponent<ItemModWearable>()))
                    .ToList();

                float candidateScore = GetArmorProtectionScore(candidate);
                float conflictingScore = conflicting.Sum(GetArmorProtectionScore);

                if (conflicting.Count > 0 && candidateScore < conflictingScore + ArmorUpgradeMinimumScoreGain)
                {
                    continue;
                }

                if (!candidate.MoveToContainer(npc.inventory.containerWear))
                {
                    continue;
                }

                armorUpgraded++;
                VerbosePuts($"home-storage: '{survivor.Character.Alias}' equipped '{candidate.info.shortname}' from its own storage (protection {candidateScore:F2} vs {conflictingScore:F2}).");

                foreach (Item displaced in conflicting)
                {
                    StorageContainer target = boxes.FirstOrDefault(b => b != null && !b.IsDestroyed && b.inventory != null && !IsContainerFull(b.inventory));

                    if (target == null || !displaced.MoveToContainer(target.inventory))
                    {
                        displaced.Drop(npc.transform.position, Vector3.zero);
                    }
                }
            }
        }

        List<Item> allStoredItems = boxes.Where(b => b != null && !b.IsDestroyed && b.inventory != null)
            .SelectMany(b => b.inventory.itemList)
            .ToList();

        Item bestStoredWeapon = FindBestByPriority(allStoredItems, WeaponPriority, exclude: null);
        Item bestOwnedWeapon = FindBestByPriority(
            npc.inventory.containerBelt.itemList.Concat(npc.inventory.containerMain.itemList).ToList(),
            WeaponPriority,
            exclude: null);

        bool weaponUpgraded = false;

        if (bestStoredWeapon != null
            && (bestOwnedWeapon == null || Array.IndexOf(WeaponPriority, bestStoredWeapon.info.shortname) < Array.IndexOf(WeaponPriority, bestOwnedWeapon.info.shortname)))
        {
            if (bestStoredWeapon.MoveToContainer(npc.inventory.containerMain))
            {
                weaponUpgraded = true;
                VerbosePuts($"home-storage: '{survivor.Character.Alias}' grabbed '{bestStoredWeapon.info.shortname}' from its own storage - a real upgrade over what it's carrying.");
            }
        }

        if (armorUpgraded > 0 || weaponUpgraded)
        {
            EquipBestWeaponForDisplay(survivor);
        }

        onComplete?.Invoke();
    }

    /// <summary>
    /// Finds the survivor's own real tool cupboard by the NetID recorded
    /// at build time (HomeBase.CupboardNetId) - more precise than the
    /// OwnerID+proximity scan boxes/furnaces use, and there's only ever
    /// one real cupboard per base anyway.
    /// </summary>
    private BuildingPrivlidge FindOwnedCupboard(HomeBase home)
    {
        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is BuildingPrivlidge cupboard && !cupboard.IsDestroyed && cupboard.net != null && cupboard.net.ID.Value == home.CupboardNetId)
            {
                return cupboard;
            }
        }

        return null;
    }

    /// <summary>
    /// Real tool cupboard deposit (2026-09-01, Lucas's own explicit
    /// correction: "Tool cupboards accept arbitrary tools (building plans,
    /// hammer)... it has a separate area within the tool cupboard to store
    /// them" - confirmed via decompiling BuildingPrivlidge: it's a real
    /// StorageContainer with a general 24-slot area (slots 0-23, accepts
    /// anything - hammer/building.planner land here) PLUS reserved upkeep-
    /// only slots (24-28, gated to whatever allowedConstructionItems the
    /// live prefab is actually configured with - real wood/stone/metal.
    /// fragments/HQM). Reads allowedConstructionItems live off the real
    /// spawned cupboard rather than hardcoding a guessed shortname list, so
    /// this stays correct even if that set is ever reconfigured. Runs
    /// BEFORE the general box deposit in GhostReturnHomeAndDeposit -
    /// whatever lands here physically leaves the survivor's inventory, so
    /// the box step naturally never sees it again, no exclusion logic
    /// needed on that side.
    /// </summary>
    private void GhostDepositIntoOwnedCupboard(Survivor survivor, Action<int> onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            onComplete?.Invoke(0);
            return;
        }

        BuildingPrivlidge cupboard = FindOwnedCupboard(home);

        if (cupboard == null || cupboard.inventory == null)
        {
            onComplete?.Invoke(0);
            return;
        }

        Vector3 approachDirection = npc.transform.position - cupboard.transform.position;
        approachDirection.y = 0f;

        if (approachDirection.sqrMagnitude < 0.01f)
        {
            approachDirection = npc.transform.forward;
        }

        Vector3 approachPoint = cupboard.transform.position + approachDirection.normalized * HomeStorageStandOffDistance;
        approachPoint.y = cupboard.transform.position.y;

        StartPhasingToDestination(survivor, approachPoint, onArrived: () =>
        {
            BasePlayer arrivedNpc = survivor.Player;

            if (arrivedNpc == null || arrivedNpc.IsDestroyed || cupboard.IsDestroyed || cupboard.inventory == null)
            {
                onComplete?.Invoke(0);
                return;
            }

            // Hammer/planner still deposit in FULL (a whole tool, not a
            // percentage) - real upkeep resources (wood/stone/metal.
            // fragments/HQM) now only trickle 10% (DepositUpkeepPortion's
            // own doc comment) rather than depositing the whole stack the
            // way this used to.
            int moved = DepositFilteredItems(arrivedNpc, cupboard.inventory, item =>
                item.info.shortname == HammerShortname
                || item.info.shortname == BuildingPlannerShortname);

            moved += DepositUpkeepPortion(arrivedNpc, cupboard);

            onComplete?.Invoke(moved);
        },
        onFailed: () => onComplete?.Invoke(0));
    }

    // Real upkeep-trickle fraction (2026-09-01, Lucas's own explicit spec:
    // "have the bot deposit 10% of its stones/ores/metal/wood everytime it
    // returns from a task outside of the base. That way it has upkeep and
    // is slowly progressing its base hoarding"). Deliberately only a SLICE,
    // not the whole stack - the other 90% keeps flowing into the normal box
    // deposit right after this (GhostDepositIntoOwnedBoxes), so the hoard
    // TryPursueTierUpgrade checks against keeps growing at the same time
    // the cupboard's own real decay-prevention timer gets fed.
    private const float HomeUpkeepDepositFraction = 0.1f;

    private int DepositUpkeepPortion(BasePlayer npc, BuildingPrivlidge cupboard)
    {
        List<Item> items = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => cupboard.allowedConstructionItems.Contains(item.info))
            .ToList();

        int moved = 0;

        foreach (Item item in items)
        {
            int portion = Mathf.Clamp(Mathf.RoundToInt(item.amount * HomeUpkeepDepositFraction), 1, item.amount);

            if (portion >= item.amount)
            {
                if (item.MoveToContainer(cupboard.inventory))
                {
                    moved++;
                }

                continue;
            }

            ItemContainer source = item.parent;
            Item split = item.SplitItem(portion);

            if (split == null)
            {
                continue;
            }

            if (split.MoveToContainer(cupboard.inventory))
            {
                moved++;
            }
            else if (source == null || !split.MoveToContainer(source))
            {
                split.Drop(npc.transform.position, Vector3.zero);
            }
        }

        return moved;
    }

    /// <summary>
    /// Top-level real "go home and deposit" trip (2026-09-01, Lucas's own
    /// explicit roadmap ask, points A and B) - ghost-enters the base (same
    /// door-route chaining as every other GhostEnterHomeForDeposit caller),
    /// deposits hammer/building plan/real upkeep materials into the tool
    /// cupboard first (GhostDepositIntoOwnedCupboard), then everything else
    /// except the active weapon/worn armor/ammo/medical into the survivor's
    /// own boxes, then - in the SAME trip, rather than a separate
    /// standalone check - tops up an owned furnace if the
    /// 2000+-wood/has-ore trigger is met. Both A ("recycle then deposit")
    /// and B ("excess wood fills the furnace") share this one trip: A
    /// triggers it (see FinishRecycling, LivingRust.Recycling.cs), and B's
    /// own condition is just checked as a natural part of every trip this
    /// causes, rather than needing its own separate periodic trigger.
    /// </summary>
    private void GhostReturnHomeAndDeposit(Survivor survivor, Action onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;
        MarkBaseVisit(survivor.Character.Id);

        if (home == null || npc == null || npc.IsDestroyed)
        {
            onComplete?.Invoke();
            return;
        }

        // Keeps the door sweeper's hands off this survivor's doors for the length of the trip, and
        // repairs impossible stacks / destroys torches before anything is deposited.
        _baseTripUntil[survivor.Character.Id] = Time.realtimeSinceStartup + 120f;
        SanitizeBaseStorage(survivor);

        GhostEnterHomeForDeposit(survivor, () =>
        {
            GhostDepositIntoOwnedCupboard(survivor, cupboardMoved =>
            {
                if (cupboardMoved > 0)
                {
                    survivor.Character.HasDepositedInitialLoot = true;
                }

                VerbosePuts($"home-storage: '{survivor.Character.Alias}' deposited {cupboardMoved} item stack(s) into its own tool cupboard.");

                // Re-reads survivor.Player fresh via the closure (not the
                // captured npc above) - matches this project's general
                // pattern of never trusting a BasePlayer reference across a
                // multi-tick operation, and lets IsOnlyGatherToolOfItsFamily
                // see the survivor's real current inventory at each deposit
                // decision.
                GhostDepositIntoOwnedBoxes(survivor, item => ShouldDepositAtBaseFor(survivor, item), deposited =>
                {
                    if (deposited > 0)
                    {
                        survivor.Character.HasDepositedInitialLoot = true;
                    }

                    VerbosePuts($"home-storage: '{survivor.Character.Alias}' deposited {deposited} item stack(s) into its own base storage.");

                    TryFillOwnedFurnaces(survivor, () =>
                    {
                        // C then D (2026-09-19, Lucas's own explicit
                        // A-through-F spec for this whole trip) - gear
                        // upgrade from storage, then a bandage top-up,
                        // both right here between the furnace step (B)
                        // and the existing sleeping-bag/tier-upgrade/exit
                        // tail.
                        TryUpgradeGearFromStorage(survivor, () =>
                        {
                            // The in-base workshop (2026-10-03): free metal tools, clothing,
                            // gunpowder, ammo, firearms from researched blueprints, and the
                            // sheet-metal base upgrade - all standing at the workbench.
                            TryRunBaseWorkshop(survivor, () =>
                            TryStockBandagesBeforeLeaving(survivor, () =>
                            {
                                TryPlaceSleepingBagAtBase(survivor, () =>
                                {
                                    // Real fix (2026-09-19, Lucas's own live
                                    // report: "ensure that they close their doors
                                    // when they leave otherwise their base is wide
                                    // open"). TryPursueTierUpgrade's own common
                            // case (no upgrade available/affordable - the
                            // vast majority of deposit trips) just called
                            // onComplete directly with no exit step at all
                            // - GhostEnterHomeForDeposit opens whatever
                            // doors it needs to get IN, but nothing on this
                            // path ever closed them again before the
                            // survivor left for its next task. The ONE
                            // place doors reliably got closed was
                            // AdvanceBuildReplay's own one-time build-
                            // completion settle-in (ExitHomeIfInside) -
                            // every ROUTINE deposit trip afterward (which
                            // happens far more often than that one-time
                            // event) had no matching exit. Wrapping
                            // onComplete here instead of passing it through
                            // directly means ExitHomeIfInside always runs
                            // first regardless of why TryPursueTierUpgrade
                            // finished - if it actually started a real
                            // upgrade build instead, it deliberately never
                            // calls this callback at all (that whole
                            // separate replay already closes the door via
                            // its own AdvanceBuildReplay completion), so
                            // this wrapper is a no-op for that case.
                            BasePlayer keycardCheckNpc = survivor.Player;

                            if (keycardCheckNpc != null && !keycardCheckNpc.IsDestroyed)
                            {
                                TryWithdrawBestKeycardFromStorage(survivor, keycardCheckNpc, home);
                                TryPlaceAdditionalStorageBoxAtHome(survivor, keycardCheckNpc);
                            }

                            TryPursueTierUpgrade(survivor, () => TeleportOutsideHomeIfInside(survivor, home, onComplete));
                                });
                            }));
                        });
                    });
                });
            });
        });
    }

    /// <summary>
    /// Real "respawn point at base" placement (2026-09-01, Lucas's own
    /// explicit spec: "have the bots place an additional sleeping bag
    /// inside or near their base... that way if they die they can go
    /// 'cool, let me check my storage crates for items, re-equip, go
    /// continue'"). Deliberately a SEPARATE bag from the primitive
    /// checklist's own one (placed wherever the survivor happened to be
    /// early in life, likely nowhere near the eventual base) - a real
    /// player owns both simultaneously the same way (Rust's own respawn
    /// UI lets you pick among every bag you own), so this doesn't remove
    /// or replace that one. Gated on real proximity to an owned SleepingBag
    /// entity (HasSleepingBagNearHome) rather than a HashSet flag - a
    /// HashSet would need its own persistence/reload story the way
    /// _hasPlacedSleepingBag already quietly doesn't have (see
    /// ShouldSkipPrimitiveChecklist's own doc comment on that exact class
    /// of bug); checking the real world state instead means this is
    /// naturally correct across reloads with no extra bookkeeping. Reuses
    /// the exact same real deploy mechanism DeploySleepingBagAndAssign
    /// already uses (LivingRust.Crafting.cs), just triggered here instead
    /// (this trip already has the survivor standing right at its base,
    /// which is exactly where this bag needs to land) and without
    /// touching _hasPlacedSleepingBag (a genuinely separate one-off flag,
    /// conflating the two would incorrectly satisfy HasCompletedPrimitiveGoals'
    /// own bag check for a survivor that skipped the checklist entirely).
    /// Opportunistic - if there's not enough cloth on hand right now, just
    /// tries again on the next trip home rather than blocking this one.
    /// </summary>
    // Tight, dedicated radius for "does a base already have its own bag" - 2026-10-03, Lucas's own
    // explicit spec ("a sleeping bag is placed within 5 metres of their base"). Deliberately
    // narrower than HomeStorageSearchRadius (40m, used for general storage-box lookups) - a bag
    // 40m away would still pass the old check and silently block a genuinely base-local one from
    // ever being placed, which defeats the actual point (dying far from base and respawning 40m
    // away still means a real walk back, not "respawn at the base").
    private const float SleepingBagNearHomeRadius = 5f;

    private bool HasSleepingBagNearHome(BasePlayer npc, HomeBase home)
    {
        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is SleepingBag bag && !bag.IsDestroyed && bag.OwnerID == npc.userID
                && Vector3.Distance(bag.transform.position, home.Position) <= SleepingBagNearHomeRadius)
            {
                return true;
            }
        }

        return false;
    }


    private void DeployBaseSleepingBag(Survivor survivor, BasePlayer npc, ItemDefinition bagDef, Item bagItem)
    {
        ItemModDeployable modDeployable = bagDef.GetComponent<ItemModDeployable>();

        if (modDeployable == null)
        {
            return;
        }

        // The survivor is standing on its own base floor (it was just moved to the cupboard), so its
        // own height is the right one - the terrain height below a raised foundation would bury the bag.
        Vector3 position = npc.transform.position;
        Quaternion rotation = Quaternion.LookRotation(Vector3.up, npc.eyes.BodyForward()) * Quaternion.Euler(90f, 0f, 0f);

        BaseEntity bagEntity = GameManager.server.CreateEntity(modDeployable.entityPrefab.resourcePath, position, rotation);

        if (bagEntity == null)
        {
            Puts($"home-storage: '{survivor.Character.Alias}' failed to create a base sleeping bag entity ('{modDeployable.entityPrefab.resourcePath}').");
            return;
        }

        bagEntity.skinID = bagItem.skin;
        bagEntity.SendMessage("SetDeployedBy", npc, SendMessageOptions.DontRequireReceiver);
        bagEntity.OwnerID = npc.userID;
        bagEntity.Spawn();
        bagItem.UseItem(1);

        _hasPlacedSleepingBag.Add(survivor.Character.Id);

        Puts($"home-storage: '{survivor.Character.Alias}' placed a sleeping bag at its base.");
    }

    /// <summary>
    /// Real continuous tier-progression check (2026-09-01, Lucas's own
    /// explicit spec: "constantly farming or attempting to progress to the
    /// next tier of base level... go back to base, check its boxes and go
    /// 'do I have stuff to make the next base tier?' no? go back to
    /// another monument or farm roads/paths... or farm more stone/ore").
    /// Piggybacks on the SAME real return-home trip every recycling
    /// completion already triggers (GhostReturnHomeAndDeposit) - the
    /// natural "loot/recycle, come home, check" cycle the spec describes
    /// already happens for free once this is added at the END of that
    /// trip, no separate periodic scheduler needed. Checks affordability
    /// against real BASE STORAGE totals (owned boxes), not carried
    /// inventory - inventory is usually near-empty right after a deposit
    /// trip, the real wealth sits in the boxes. If NOT yet affordable, this
    /// is a genuine no-op - the survivor just resumes whatever it was
    /// doing (normal looting, which already includes monument farming and
    /// opportunistic recycling - see ContinueLootTask's own six-category
    /// search and TryStartRecyclingTask), satisfying "go get more" without
    /// needing new dedicated farming logic. If affordable, withdraws
    /// exactly what's needed straight out of the boxes (WithdrawUpToAmount,
    /// the same container-to-container transfer TryFillOwnedFurnaces
    /// already uses) and starts a real new build via the same
    /// TryStartAutonomousBaseBuild pipeline the very first base used -
    /// this builds a genuinely NEW structure (a new cupboard, new site) at
    /// wherever the survivor currently is (right by its old base, and
    /// IsTooCloseToAnotherCupboard's own site-selection check will
    /// relocate it if needed) rather than upgrading the existing one in
    /// place piece-by-piece - this project's CSV-replay system has no
    /// concept of an in-place tier conversion between two different
    /// literal floor plans, so a fresh higher-tier structure is the
    /// closest real equivalent. The old base (and its own boxes/furnace/
    /// cupboard) is left behind, still owned, once Character.Home
    /// reassigns to the new one on build completion (ReplayBuildTrace's
    /// own existing behavior, unchanged).
    /// </summary>
    // Consecutive-failure pity counter, keyed per survivor (2026-09-24,
    // Lucas's own explicit spec). See TryPursueTierUpgrade's own doc
    // comment just below for the full mechanism.
    private readonly Dictionary<Guid, int> _tierUpgradeStruggleCount = new();

    private void TryPursueTierUpgrade(Survivor survivor, Action onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            onComplete?.Invoke();
            return;
        }

        int nextTierRank = home.TierRank + 1;
        string nextTierFolder = $"tier{nextTierRank}";
        string nextTierDirectory = $"{BaseDesignsDirectory}/{nextTierFolder}";

        if (!Directory.Exists(nextTierDirectory))
        {
            onComplete?.Invoke();
            return;
        }

        string[] designs = Directory.GetFiles(nextTierDirectory, "*.csv");

        if (designs.Length == 0)
        {
            onComplete?.Invoke();
            return;
        }

        if (nextTierRank >= 2 && !IsTierUnlocked(survivor, npc, nextTierRank, out string _))
        {
            onComplete?.Invoke();
            return;
        }

        string designPath = designs[UnityEngine.Random.Range(0, designs.Length)];

        // Real "no more free lunch after the first base" fix (2026-09-23,
        // Lucas's own explicit ask: "subsequent base tiers after the
        // initial build require legitimate resources... the bots should
        // already have X amount of metal fragments, resources etc to build
        // said base tier OR at a minimum be working towards it"). This
        // function is reached for EVERY tier upgrade past the first base
        // (tier0->tier1 included, not just the tier2+ IsTierUnlocked-gated
        // ones) - previously passed freeStarterEssentials: true here too,
        // which incorrectly gave every upgrade the same free door/lock/
        // furnace treatment the FIRST base alone is meant to get
        // (CalculateTraceResourceRequirements' own doc comment). Real
        // affordability (canAfford below, against actual base storage) is
        // what already implements "or at a minimum be working towards it" -
        // a survivor short on the genuine cost simply doesn't upgrade yet
        // and keeps farming/looting normally until it can.
        Dictionary<string, int> cost = CalculateTraceResourceRequirements(designPath);
        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);

        Dictionary<string, int> shortfalls = new();

        foreach (KeyValuePair<string, int> requirement in cost)
        {
            int itemId = ItemManager.FindItemDefinition(requirement.Key)?.itemid ?? 0;

            int have = itemId != 0
                ? boxes.Where(b => b != null && !b.IsDestroyed && b.inventory != null)
                    .SelectMany(b => b.inventory.itemList)
                    .Where(i => i.info.itemid == itemId)
                    .Sum(i => i.amount)
                : 0;

            if (have < requirement.Value)
            {
                shortfalls[requirement.Key] = requirement.Value - have;
            }
        }

        bool canAfford = cost.Count > 0 && shortfalls.Count == 0;

        if (!canAfford)
        {
            // Real "pity" catch-up (2026-09-24, Lucas's own explicit spec,
            // walking back an earlier same-day active-fetch attempt he
            // decided was too aggressive a priority). Otherwise this stays
            // purely passive - a survivor never goes out of its way to
            // gather toward the next tier, it just keeps re-checking
            // affordability opportunistically on whatever deposit trip
            // happens next, same as before this whole tier-upgrade feature
            // existed. Once a survivor has genuinely failed this real
            // affordability check 10 times in a row, it starts getting
            // handed its biggest shortfall ingredient, fully covered,
            // deposited straight into its own boxes (it's already home for
            // this check) rather than anything carried out and fetched. An
            // 11th straight failure covers two ingredients, a 12th covers
            // three, and so on - a survivor stuck a long time converges on
            // the upgrade fast instead of staying stuck indefinitely.
            Guid characterId = survivor.Character.Id;

            int struggleCount = _tierUpgradeStruggleCount.TryGetValue(characterId, out int existingStruggleCount)
                ? existingStruggleCount + 1
                : 1;
            _tierUpgradeStruggleCount[characterId] = struggleCount;

            if (struggleCount >= 10)
            {
                int grantTypeCount = Math.Min(shortfalls.Count, struggleCount - 9);

                List<string> grantOrder = shortfalls
                    .OrderByDescending(kvp => kvp.Value)
                    .Select(kvp => kvp.Key)
                    .Take(grantTypeCount)
                    .ToList();

                foreach (string shortname in grantOrder)
                {
                    GrantItemIntoOwnedBoxes(boxes, shortname, shortfalls[shortname], home.Position);
                }

                string grantSummary = string.Join(", ", grantOrder.Select(s => $"{shortfalls[s]}x {s}"));
                Puts($"tier-upgrade: '{survivor.Character.Alias}' has failed the affordability check for '{nextTierFolder}/{Path.GetFileNameWithoutExtension(designPath)}' {struggleCount} times in a row - granting {grantSummary} straight into storage to help it catch up.");
            }

            onComplete?.Invoke();
            return;
        }

        _tierUpgradeStruggleCount.Remove(survivor.Character.Id);
        Puts($"tier-upgrade: '{survivor.Character.Alias}' has enough stored for '{nextTierFolder}/{Path.GetFileNameWithoutExtension(designPath)}' - withdrawing and starting the upgrade.");

        foreach (KeyValuePair<string, int> requirement in cost)
        {
            WithdrawUpToAmount(boxes, requirement.Key, requirement.Value, npc.inventory.containerMain);
        }

        // Deliberately does NOT call onComplete - building the upgrade IS
        // the survivor's next task now, same contract TryPursueBaseGatherGoal
        // itself already uses (readiness replaces "resume the old task,"
        // it doesn't chain after it). oldHomeToMigrate (2026-09-01, Lucas's
        // own explicit caveat) - passing the CURRENT home (about to be
        // overwritten the instant the new base finishes building) is what
        // lets TryStartAutonomousBaseBuild's own completion route back to
        // collect everything from it afterward.
        TryStartAutonomousBaseBuild(survivor, npc, designPath, survivor.Character.Id, oldHomeToMigrate: home);
    }

    private void CrossHomeDoor(Survivor survivor, HomeBase home, Vector3 farPoint, Vector3 finalDestination, Action onArrived, Action onFailed)
    {
        _activeHomeDoorCrossings.Add(survivor.Character.Id);

        Door ResolveDoor()
        {
            Collider[] hits = Physics.OverlapSphere(home.DoorPosition, 1f, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

            foreach (Collider hit in hits)
            {
                Door candidate = hit.GetComponentInParent<Door>();

                if (candidate != null && !candidate.IsDestroyed)
                {
                    return candidate;
                }
            }

            return null;
        }

        Door door = ResolveDoor();

        if (door != null && !door.IsOpen())
        {
            door.SetOpen(true);
            door.SendNetworkUpdate();
        }

        void FinishCrossing()
        {
            // Real re-resolve, not the captured reference above - the
            // whole crossing takes a couple of real seconds
            // (StartPhasingToDestination steps at running speed, not
            // instantly), long enough that the original Door reference
            // could theoretically have been destroyed/replaced in the
            // meantime (an upgrade, a raid) - closing whatever's actually
            // there now is more robust than trusting a stale reference.
            Door doorToClose = ResolveDoor();

            if (doorToClose != null && doorToClose.IsOpen())
            {
                doorToClose.SetOpen(false);
                doorToClose.SendNetworkUpdate();
            }

            // See DoorCloseHeightCorrectionTolerance's own doc comment
            // (GhostThroughOwnDoor) - same real fix here: closing the
            // door can physically shove the survivor if it's still
            // overlapping the door's own collider, most often straight
            // up onto an overhead floor slab on a multi-story design.
            // home.DoorPosition.y is the one height already known for
            // certain to be correct immediately after this crossing.
            BasePlayer crossingNpc = survivor.Player;

            if (crossingNpc != null && !crossingNpc.IsDestroyed && Mathf.Abs(crossingNpc.transform.position.y - home.DoorPosition.y) > DoorCloseHeightCorrectionTolerance)
            {
                Vector3 corrected = crossingNpc.transform.position;
                corrected.y = home.DoorPosition.y;
                crossingNpc.transform.position = corrected;
                crossingNpc.MovePosition(corrected);
            }

            _activeHomeDoorCrossings.Remove(survivor.Character.Id);
            StartWalkingWithRecovery(survivor, finalDestination, onArrived, onFailed, recoveryTier: 1);
        }

        StartLevelledPhasing(
            survivor,
            home.DoorPosition,
            onArrived: () => StartPhasingToDestination(
                survivor,
                farPoint,
                onArrived: FinishCrossing,
                onFailed: FinishCrossing),
            onFailed: FinishCrossing);
    }

    /// <summary>
    /// Real explicit "leave home before doing anything else" gate
    /// (2026-08-29, eighth round - Lucas's own proposed fix: "I have
    /// finished task -> am I inside of the base I built? Yes? -> exit
    /// base using ghostroute in reverse... re-roll task for whatever I
    /// want to do next"). See AdvanceBuildReplay's own call site doc
    /// comment (BaseBuilding.cs) for the specific bug this closes - a
    /// survivor's very next task decision kicking off a normal walk
    /// whose native-movement setup silently Warps it outside before any
    /// of this project's own door-crossing checks ever get a chance to
    /// run. Deliberately synchronous with build completion rather than
    /// folded into the general walk system - the one moment a survivor
    /// is GUARANTEED to be standing inside its own home, handled before
    /// anything else touches its movement. Same route-vs-computed-
    /// geometry priority as TryStartHomeDoorRouteCrossing below, just
    /// with no further destination to continue toward afterward - the
    /// caller's own onComplete is exactly "now go decide what to do
    /// next," same as if nothing needed leaving at all.
    /// </summary>
    // How far outside HomeCrossingRadius a random teleport-exit point can
    // land (2026-09-27, Lucas's own explicit ask: "don't loiter inside
    // their base constantly opening/closing doors... teleport randomly
    // outside their base and continue on"). Deliberately still fairly
    // close - this is meant to skip the door-walk animation/pathing, not
    // relocate the survivor across the map.
    private const float TeleportOutsideHomeExtraRange = 15f;
    private const int TeleportOutsideHomeMaxAttempts = 8;

    /// <summary>
    /// Replaces ExitHomeIfInside at the tail of the real "return to base,
    /// deposit, refuel furnaces" trip (GhostReturnHomeAndDeposit's own
    /// call chain) - once those actual duties are done, a survivor has no
    /// reason left to be standing inside its base at all, so this skips
    /// the hardcoded door-route walk entirely and just relocates it
    /// straight to a random valid point just outside HomeCrossingRadius
    /// (BasePlayer.Teleport, same real API this project's own debug
    /// teleport commands already use). Every candidate is height-snapped,
    /// checked against water level, and validated on real navmesh
    /// (SnapApproachPointToNavMesh) the same way any other real destination
    /// in this project is - a candidate that still resolves back inside
    /// HomeInteriorRadius after snapping (e.g. the navmesh snap pulled it
    /// back through a wall) is rejected and re-rolled. Falls back to the
    /// original door-walk exit (ExitHomeIfInside) if no valid point is
    /// found after TeleportOutsideHomeMaxAttempts - a survivor should never
    /// end up permanently stuck inside just because this shortcut failed.
    /// </summary>
    private void TeleportOutsideHomeIfInside(Survivor survivor, HomeBase home, Action onComplete)
    {
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            onComplete?.Invoke();
            return;
        }

        float thresholdSqr = HomeInteriorRadius * HomeInteriorRadius;

        if ((npc.transform.position - home.Position).sqrMagnitude < thresholdSqr)
        {
            for (int attempt = 0; attempt < TeleportOutsideHomeMaxAttempts; attempt++)
            {
                float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float distance = UnityEngine.Random.Range(HomeCrossingRadius, HomeCrossingRadius + TeleportOutsideHomeExtraRange);
                Vector3 candidate = home.Position + new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
                candidate.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(candidate) : candidate.y;

                if (WaterLevel.GetWaterLevel(candidate, waves: false) > candidate.y + 0.5f)
                {
                    continue;
                }

                Vector3 snapped = SnapApproachPointToNavMesh(npc, candidate);

                if ((snapped - home.Position).sqrMagnitude < thresholdSqr)
                {
                    continue;
                }

                npc.Teleport(snapped);
                VerbosePuts($"home-exit: '{survivor.Character.Alias}' teleported outside its base to {snapped} instead of walking the door route out.");
                onComplete?.Invoke();
                return;
            }

            Puts($"home-exit: WARNING - '{survivor.Character.Alias}' couldn't find a valid teleport-exit point after {TeleportOutsideHomeMaxAttempts} attempts - falling back to the normal door-walk exit.");
        }

        ExitHomeIfInside(survivor, home, onComplete);
    }

    private void ExitHomeIfInside(Survivor survivor, HomeBase home, Action onCompleteRaw)
    {
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            VerbosePuts($"exit-home-gate: '{survivor?.Character?.Alias}' skipping - home or npc missing.");
            onCompleteRaw?.Invoke();
            return;
        }

        // Real shared busy-guard (2026-09-01, live report + phase-diag
        // proof: 'GhostTorch' oscillated between TWO different phase
        // targets forever, the logged destination alternating every single
        // line, because ExitHomeIfInside and TryStartHomeDoorRouteCrossing
        // had SEPARATE notions of "busy" - TryStartHomeDoorRouteCrossing
        // checks/sets _activeHomeDoorCrossings, this function checked/set
        // nothing at all, so both could genuinely run concurrently on the
        // same survivor, each call's own CancelActiveMovement wiping out
        // whatever phase timer the OTHER one had in flight before it could
        // ever finish - neither could make real progress, forever.
        // Sharing the same guard means whichever one starts first
        // genuinely owns the survivor's movement until it's actually done.
        if (_activeHomeDoorCrossings.Contains(survivor.Character.Id))
        {
            VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' skipping - a door crossing is already in flight for this survivor.");
            onCompleteRaw?.Invoke();
            return;
        }

        _activeHomeDoorCrossings.Add(survivor.Character.Id);

        // Every onComplete?.Invoke() below this point (there are several
        // exit paths through this function) now goes through this wrapper,
        // so the busy flag is always released exactly once, regardless of
        // which path was taken - no need to touch every individual call
        // site.
        Action onComplete = () =>
        {
            _activeHomeDoorCrossings.Remove(survivor.Character.Id);
            onCompleteRaw?.Invoke();
        };

        // HomeInteriorRadius, not the wider HomeCrossingRadius - this gate
        // means "am I actually inside my base right now," not "merely
        // somewhere near it" (2026-09-01, same fix/reasoning as
        // TryStartHomeDoorRouteCrossing's own doc comment above). Using
        // the 20m radius here meant a survivor that finished its build
        // task already standing outside (e.g. stuck 8m away failing to
        // path to a placement point) still "passed" this check and tried
        // to walk to a route's near end and play it OUTWARD again, which
        // never made sense for a survivor that was never inside to begin
        // with.
        float thresholdSqr = HomeInteriorRadius * HomeInteriorRadius;
        float distSqr = (npc.transform.position - home.Position).sqrMagnitude;

        if (distSqr >= thresholdSqr)
        {
            VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' not actually inside home (dist={Mathf.Sqrt(distSqr):F1}m, radius={HomeInteriorRadius:F1}m) - nothing to exit.");
            onComplete?.Invoke();
            return;
        }

        VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' is inside home (dist={Mathf.Sqrt(distSqr):F1}m) - {home.DoorRoutes.Count} hardcoded route(s) available.");

        if (home.DoorRoutes.Count > 0)
        {
            HomeDoorRoute bestRoute = null;
            bool reversed = false;
            float bestDistSqr = float.MaxValue;

            foreach (HomeDoorRoute route in home.DoorRoutes)
            {
                if (route.Waypoints.Count == 0)
                {
                    continue;
                }

                float distToStart = (route.Waypoints[0] - npc.transform.position).sqrMagnitude;
                float distToEnd = (route.Waypoints[^1] - npc.transform.position).sqrMagnitude;
                float minDist = Mathf.Min(distToStart, distToEnd);

                if (minDist < bestDistSqr)
                {
                    bestDistSqr = minDist;
                    bestRoute = route;
                    reversed = distToEnd < distToStart;
                }
            }

            if (bestRoute != null)
            {
                List<Vector3> orderedWaypoints = reversed ? Enumerable.Reverse(bestRoute.Waypoints).ToList() : bestRoute.Waypoints;
                Vector3 nearEnd = orderedWaypoints[0];
                Vector3 doorAnchor = bestRoute.DoorAnchorPosition;

                VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' phasing to route near-end {nearEnd} (reversed={reversed}) before playing the hardcoded route out.");

                Door ResolveRouteDoor()
                {
                    Collider[] hits = Physics.OverlapSphere(doorAnchor, 1f, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

                    foreach (Collider hit in hits)
                    {
                        Door candidate = hit.GetComponentInParent<Door>();

                        if (candidate != null && !candidate.IsDestroyed)
                        {
                            return candidate;
                        }
                    }

                    return null;
                }

                // StartLevelledPhasing, not StartWalking (2026-09-01, live
                // test: every single test bot failed this leg - "destination
                // 4.68m from nearest navmesh point" - because nearEnd sits
                // inside the player-built base, which the static navmesh has
                // zero knowledge of, same root cause as this whole session's
                // NavMeshAgent.Warp fix. Ordinary collision-based pathing can
                // never reach an interior point; phasing (already used for
                // every other interior leg in this system) sidesteps the
                // navmesh dependency entirely, and nearEnd is always close
                // by construction (picked as the closest route endpoint,
                // gated by HomeInteriorRadius), so this is always a short
                // hop, not a long warp.
                StartLevelledPhasing(
                    survivor,
                    nearEnd,
                    onArrived: () =>
                    {
                        Door routeDoor = ResolveRouteDoor();

                        VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' reached route near-end - door at anchor {doorAnchor} {(routeDoor != null ? $"FOUND (open={routeDoor.IsOpen()})" : "NOT FOUND")}, playing the route.");

                        if (routeDoor != null && !routeDoor.IsOpen())
                        {
                            routeDoor.SetOpen(true);

                            // Real fix for "door never visibly opens" (2026-
                            // 09-01, live report + frame-by-frame video
                            // review: across the ENTIRE crossing, both doors
                            // showed as visually closed on Lucas's own
                            // client the whole time, despite Door.IsOpen()
                            // correctly reporting true server-side the whole
                            // time too). SetOpen() alone doesn't guarantee
                            // an immediate network broadcast of the state
                            // change - a well-documented Rust/Oxide modding
                            // gotcha (entity state changes made server-side,
                            // outside the normal player-interaction RPC
                            // path, need an explicit SendNetworkUpdate to
                            // actually reach observing clients). This was
                            // very likely the real root cause behind EVERY
                            // "teleports through a closed door" report this
                            // whole session, regardless of which movement
                            // mechanism was used - the door itself was never
                            // visibly opening for anyone watching.
                            routeDoor.SendNetworkUpdate();
                        }

                        // Real delay before moving, then a real COLLISION walk
                        // (2026-09-01, live report: "instant inside->outside
                        // in a split second... doors didn't even open" -
                        // still true even once the door was confirmed
                        // genuinely toggling open server-side, because
                        // StartGhostRoute's phase movement never actually
                        // depends on collision or the door's state at all -
                        // see StartCollisionWalkRoute's own doc comment).
                        // Giving the door a moment to actually swing open
                        // before the walk starts, then walking the recorded
                        // path with real collision, makes the crossing look
                        // like a real player using the door instead of a
                        // glide that happens to have a door animation next
                        // to it.
                        timer.Once(DoorRouteOpenAnimationDelay, () => StartCollisionWalkRoute(survivor, orderedWaypoints, 0, () =>
                        {
                            Door doorToClose = ResolveRouteDoor();

                            VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' finished the route - door at anchor {doorAnchor} {(doorToClose != null ? $"FOUND (open={doorToClose.IsOpen()})" : "NOT FOUND")}.");

                            if (doorToClose != null && doorToClose.IsOpen())
                            {
                                doorToClose.SetOpen(false);
                                doorToClose.SendNetworkUpdate();
                            }

                            onComplete?.Invoke();
                        }));
                    },
                    onFailed: () =>
                    {
                        VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' failed to phase to route near-end {nearEnd} - giving up on the hardcoded route this time.");
                        onComplete?.Invoke();
                    });

                return;
            }
        }

        if (home.DoorPosition == Vector3.zero)
        {
            VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' has no hardcoded routes and no computed DoorPosition either - nothing to exit through.");
            onComplete?.Invoke();
            return;
        }

        VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' falling back to computed-geometry exit (no hardcoded route matched).");

        Door ResolveHomeDoor()
        {
            Collider[] hits = Physics.OverlapSphere(home.DoorPosition, 1f, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

            foreach (Collider hit in hits)
            {
                Door candidate = hit.GetComponentInParent<Door>();

                if (candidate != null && !candidate.IsDestroyed)
                {
                    return candidate;
                }
            }

            return null;
        }

        // StartLevelledPhasing, not StartWalking - same off-static-navmesh
        // reasoning as the DoorRoutes branch above's own doc comment
        // (InsidePoint is just as much an interior point as any recorded
        // route waypoint).
        StartLevelledPhasing(
            survivor,
            home.InsidePoint,
            onArrived: () =>
            {
                Door door = ResolveHomeDoor();

                if (door != null && !door.IsOpen())
                {
                    door.SetOpen(true);
                    door.SendNetworkUpdate();
                }

                StartLevelledPhasing(
                    survivor,
                    home.DoorPosition,
                    onArrived: () => StartPhasingToDestination(
                        survivor,
                        home.OutsidePoint,
                        onArrived: () =>
                        {
                            Door doorToClose = ResolveHomeDoor();

                            if (doorToClose != null && doorToClose.IsOpen())
                            {
                                doorToClose.SetOpen(false);
                                doorToClose.SendNetworkUpdate();
                            }

                            onComplete?.Invoke();
                        },
                        onFailed: onComplete),
                    onFailed: onComplete);
            },
            onFailed: onComplete);
    }

    /// <summary>
    /// Real hardcoded door-route crossing (2026-08-29, seventh round) -
    /// checked BEFORE the computed-geometry TryGetHomeDoorCrossing below,
    /// since an authored recording (see HomeBase.DoorRoutes' own doc
    /// comment) is the whole point of the fallback - once one exists for
    /// this design, it should always win over guessing from geometry.
    /// Same "is this walk actually crossing between inside and outside"
    /// gate and per-Character busy flag as TryGetHomeDoorCrossing (a
    /// crossing already in flight must never be re-triggered mid-route).
    /// Walks normally (real collision) to whichever end of the chosen
    /// route is closer, then plays the recorded path itself via
    /// StartGhostRoute - the same proven waypoint-phase engine already
    /// used for every monument ghost route, chosen specifically because
    /// it phases (no collision) rather than trusting the door's own
    /// collider/physics through the crossing itself, sidestepping every
    /// one of this session's door/physics failures entirely. Direction-
    /// agnostic: a route recorded walking OUT works equally well for
    /// walking IN, just played in reverse.
    /// </summary>
    private bool TryStartHomeDoorRouteCrossing(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed, Func<BasePlayer, bool> shouldWalkCarefully)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || home.DoorRoutes.Count == 0 || npc == null)
        {
            return false;
        }

        if (_activeHomeDoorCrossings.Contains(survivor.Character.Id))
        {
            return false;
        }

        float thresholdSqr = HomeInteriorRadius * HomeInteriorRadius;
        bool currentlyInside = (npc.transform.position - home.Position).sqrMagnitude < thresholdSqr;
        bool destinationInside = (destination - home.Position).sqrMagnitude < thresholdSqr;

        if (currentlyInside == destinationInside)
        {
            return false;
        }

        VerbosePuts($"home-door-route: '{survivor.Character.Alias}' hardcoded-route crossing triggered (currentlyInside={currentlyInside}) toward {destination}.");

        HomeDoorRoute bestRoute = null;
        bool reversed = false;
        float bestDistSqr = float.MaxValue;

        foreach (HomeDoorRoute route in home.DoorRoutes)
        {
            if (route.Waypoints.Count == 0)
            {
                continue;
            }

            float distToStart = (route.Waypoints[0] - npc.transform.position).sqrMagnitude;
            float distToEnd = (route.Waypoints[^1] - npc.transform.position).sqrMagnitude;
            float minDist = Mathf.Min(distToStart, distToEnd);

            if (minDist < bestDistSqr)
            {
                bestDistSqr = minDist;
                bestRoute = route;
                reversed = distToEnd < distToStart;
            }
        }

        if (bestRoute == null)
        {
            return false;
        }

        List<Vector3> orderedWaypoints = reversed ? Enumerable.Reverse(bestRoute.Waypoints).ToList() : bestRoute.Waypoints;
        Vector3 nearEnd = orderedWaypoints[0];

        Door ResolveRouteDoor()
        {
            Collider[] hits = Physics.OverlapSphere(bestRoute.DoorAnchorPosition, 1f, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

            foreach (Collider hit in hits)
            {
                Door candidate = hit.GetComponentInParent<Door>();

                if (candidate != null && !candidate.IsDestroyed)
                {
                    return candidate;
                }
            }

            return null;
        }

        void PlayRoute()
        {
            Door routeDoor = ResolveRouteDoor();

            VerbosePuts($"home-door-route: '{survivor.Character.Alias}' door at anchor {bestRoute.DoorAnchorPosition} {(routeDoor != null ? $"FOUND (open={routeDoor.IsOpen()})" : "NOT FOUND")}, playing the route.");

            if (routeDoor != null && !routeDoor.IsOpen())
            {
                routeDoor.SetOpen(true);

                // Real fix for "door never visibly opens" - see
                // ExitHomeIfInside's identical fix/doc comment (2026-09-01)
                // for the full reasoning (frame-by-frame video review
                // confirmed the door stayed visually closed the whole
                // crossing despite IsOpen() correctly reporting true
                // server-side).
                routeDoor.SendNetworkUpdate();
            }

            // Real delay before moving, then a real COLLISION walk - see
            // ExitHomeIfInside's identical fix/doc comment (2026-09-01) for
            // the full reasoning: StartGhostRoute's phase movement never
            // actually depends on collision or the door's state, so it
            // looked like a teleport even with the door genuinely open.
            timer.Once(DoorRouteOpenAnimationDelay, () => StartCollisionWalkRoute(survivor, orderedWaypoints, 0, () =>
            {
                Door doorToClose = ResolveRouteDoor();

                if (doorToClose != null && doorToClose.IsOpen())
                {
                    doorToClose.SetOpen(false);
                    doorToClose.SendNetworkUpdate();
                }

                _activeHomeDoorCrossings.Remove(survivor.Character.Id);

                // Real "last mile" fix (2026-09-01, live report: door route
                // plays cleanly - doors genuinely open now - but the survivor
                // then endlessly re-triggers the SAME crossing every ~10s,
                // "phasing to spots randomly around the base, rinse and
                // repeat"). Root cause: the route's own recorded endpoint
                // isn't necessarily the task's real destination (e.g. a
                // step further toward the cupboard) - this handoff used to
                // ALWAYS fall through to StartWalkingWithRecovery's ordinary
                // recoveryTier:1 path, which deliberately skips the door-
                // route system entirely and uses plain navmesh-based
                // walking - the exact "destination off the static navmesh"
                // wall this whole session has been fighting, for whatever
                // interior distance remains. That failure was escalating
                // into the old generic stuck-recovery ladder, which most
                // likely snapped the survivor back outside, so the task
                // loop just re-triggered the whole crossing from scratch
                // forever. If the real destination is STILL an interior
                // point (within HomeInteriorRadius of the cupboard), phase
                // the rest of the way directly instead of handing off to a
                // system that can't reach it.
                if (home != null && (destination - home.Position).sqrMagnitude < HomeInteriorRadius * HomeInteriorRadius)
                {
                    VerbosePuts($"home-door-route: '{survivor.Character.Alias}' route done, destination {destination} is still interior - phasing the rest of the way instead of handing off to ordinary walking.");
                    StartLevelledPhasing(survivor, destination, onArrived, onFailed);
                }
                else
                {
                    StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 1);
                }
            }));
        }

        _activeHomeDoorCrossings.Add(survivor.Character.Id);

        VerbosePuts($"home-door-route: '{survivor.Character.Alias}' phasing to route near-end {nearEnd} (reversed={reversed}) before playing the hardcoded route.");

        // StartLevelledPhasing, not StartWalking - same off-static-navmesh
        // reasoning as ExitHomeIfInside's own doc comment above (nearEnd is
        // an interior/route point the static navmesh doesn't know about
        // whenever the survivor starts out currently inside).
        StartLevelledPhasing(
            survivor,
            nearEnd,
            onArrived: () =>
            {
                VerbosePuts($"home-door-route: '{survivor.Character.Alias}' reached route near-end - opening door and playing the route.");
                PlayRoute();
            },
            onFailed: () =>
            {
                // Deliberately NOT falling through to a plain StartWalking/
                // EscalateStuckRecovery chain here (2026-09-01, live report:
                // "teleports from ground level to inside the ceiling and
                // back... doesn't properly know how to integrate with
                // player-built objects"). That generic ladder's own last-
                // resort tiers (wiggle/navmesh-nudge/emergency-teleport)
                // have zero concept of ghost routes or player-built
                // interiors - handing it a destination this deep inside a
                // private structure is exactly what produced the wild
                // vertical teleport chaos. If the crossing itself couldn't
                // even reach the route's own near-end, just admit this
                // attempt failed and let the caller's own task loop retry
                // later, rather than escalating into a system that was
                // never built to handle this case.
                VerbosePuts($"home-door-route: '{survivor.Character.Alias}' failed to phase to route near-end {nearEnd} - giving up on this crossing attempt (not escalating to generic stuck-recovery).");
                _activeHomeDoorCrossings.Remove(survivor.Character.Id);
                onFailed?.Invoke();
            });

        return true;
    }

    private void StartWalkingWithRecovery(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed, int recoveryTier = 0, Func<BasePlayer, bool> shouldWalkCarefully = null)
    {
        // Checked only on a genuinely fresh walk request (tier 0), never
        // on a stuck-recovery continuation - see TryGetHomeDoorCrossing's
        // own doc comment for the full reasoning.
        if (recoveryTier == 0 && TryStartHomeDoorRouteCrossing(survivor, destination, onArrived, onFailed, shouldWalkCarefully))
        {
            return;
        }

        if (recoveryTier == 0 && TryGetHomeDoorCrossing(survivor, destination, out Vector3 nearPoint, out Vector3 farPoint))
        {
            HomeBase home = survivor.Character.Home;

            // StartLevelledPhasing, not StartWalking - nearPoint is
            // home.InsidePoint whenever the survivor starts out currently
            // inside, an interior point the static navmesh doesn't know
            // about (same off-navmesh reasoning as TryStartHomeDoorRoute
            // Crossing's own doc comment above).
            // Deliberately NOT escalating to EscalateStuckRecovery on
            // failure here either - same reasoning as TryStartHomeDoorRoute
            // Crossing's own doc comment (2026-09-01): that ladder's real
            // teleport fallback has no concept of player-built interiors,
            // and nearPoint can be an interior point (home.InsidePoint)
            // just like a route's near-end.
            StartLevelledPhasing(
                survivor,
                nearPoint,
                onArrived: () => CrossHomeDoor(survivor, home, farPoint, destination, onArrived, onFailed),
                onFailed: onFailed);

            return;
        }

        StartWalking(survivor, destination, onArrived, onFailed: () => EscalateStuckRecovery(survivor, destination, onArrived, onFailed, recoveryTier), shouldWalkCarefully);
    }

    private void EscalateStuckRecovery(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed, int recoveryTier)
    {
        // Single chokepoint every stuck-recovery path (walk, follow, loot)
        // already funnels through - see /lr.tp.stuck's own doc comment for
        // why this is where the stuck flag gets set/cleared rather than at
        // each individual "got stuck"/"wiggled free" log line scattered
        // across callers.
        if (recoveryTier == 0)
        {
            MarkStuck(survivor);

            Action originalOnArrived = onArrived;
            Action originalOnFailed = onFailed;

            onArrived = () => { ClearStuck(survivor); originalOnArrived?.Invoke(); };
            onFailed = () => { ClearStuck(survivor); originalOnFailed?.Invoke(); };
        }

        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            onFailed?.Invoke();
            return;
        }

        // Wounded/downed - pause entirely rather than fight Rust's own
        // incapacitated state machine, same rule as the main walk timer's
        // own IsWounded() check (see StartWalking's doc comment). A live
        // report caught this whole chain missing that check: wiggle/
        // navmesh-nudge/emergency-teleport could all still fire on a downed
        // survivor, dragging or flat-out teleporting a wounded ragdoll's
        // transform.position around mid-bleedout - fighting Rust's own
        // incapacitated handling badly enough to leave the survivor stuck
        // unkillable, and (via TryEmergencyTeleport's direct position
        // write) visibly flung to wherever the recovery chain landed once
        // despawnall finally killed it. Re-polls on the same cadence until
        // the survivor either gets back up (resumes this exact tier) or
        // dies (OnPlayerDeath's CancelActiveMovement stops this chain the
        // next time it checks npc.IsDestroyed above).
        if (npc.IsWounded())
        {
            timer.Once(StuckReassessPause, () => EscalateStuckRecovery(survivor, destination, onArrived, onFailed, recoveryTier));
            return;
        }

        // Checked on EVERY stuck episode regardless of tier (2026-08-16,
        // was "only at tier 0" - real live bug: a monument doorway can
        // have MORE THAN ONE real barricade in sequence, and gating this
        // to tier 0 only meant that once a survivor broke through the
        // FIRST one and resumed at tier 1+ (see below), it permanently
        // lost the ability to ever detect a SECOND one for the rest of
        // that walk - confirmed live, 'NumbJackal89' broke barricade #1
        // cleanly, then spent 3+ full 200s timeout cycles endlessly
        // nudging/retrying against barricade #2 a few metres further in,
        // since every subsequent stuck episode re-entered at tier 1+ and
        // never re-checked. No infinite-loop risk from checking every
        // time - TryFindBlockingBarricade's own IsDestroyed/Health()<=0
        // filter means an already-broken barricade simply stops being
        // found, same as any other now-cleared obstacle. See
        // StartAttackingBarricade's own doc comment for the full story on
        // why this is a genuine, correctly-reported Blocked in the first
        // place, not a bug in any of the navmesh/ground-probe fixes from
        // the previous session.
        //
        // Continuation tier: advances 0 -> 1 (so a later episode doesn't
        // re-wrap MarkStuck/ClearStuck a second time - see the "wiggled
        // free" continuation just below for the identical convention),
        // but otherwise preserves whatever tier this episode was ALREADY
        // at rather than resetting backward - breaking barricade #2 while
        // already escalated shouldn't un-escalate the survivor.
        // Real "the actual obstruction is my own closed door" check
        // (2026-08-29, Lucas's own explicit ask: "how the bot gets in and
        // out of the base without teleporting"). Checked on every stuck
        // episode, same reasoning as the barricade check right below - a
        // closed door genuinely blocking a survivor's step is a totally
        // legitimate, common, and TRIVIAL fix (no swing/attack loop
        // needed, just SetOpen) compared to everything else this ladder
        // exists for, so it's worth resolving before any of the heavier
        // wiggle/nudge/teleport machinery ever gets a chance to fire.
        // Without this, a survivor standing inside the base it just
        // finished building had no way out except phasing through its
        // own walls the instant normal tasks resumed and pathed it
        // straight into its own closed front door.
        if (TryFindBlockingClosedDoor(npc, out Door blockingDoor))
        {
            blockingDoor.SetOpen(true);
            blockingDoor.SendNetworkUpdate();

            int doorContinuationTier = recoveryTier == 0 ? 1 : recoveryTier;

            // Real live bug (2026-08-29, second round - Lucas's own
            // report: "sometimes able to walk through... very
            // temperamental... when I walk outside, the bot also
            // teleports out"). SetOpen alone fixes the door's own COLLIDER
            // state, but the doorway threshold's real ground-probe/local-
            // stepping unreliability (the same class of bug
            // IsNonSteppableCollider's own doc comment documents
            // repeatedly for other geometry) can still misjudge the
            // handful of steps actually crossing the threshold even once
            // it's open - resuming through the normal ladder afterward
            // still risked eventually exhausting it and phasing, exactly
            // what's meant to be reserved for genuinely unreachable
            // destinations, not a door the survivor legitimately owns.
            //
            // Lucas's own proposed fix: treat crossing a door it BUILT
            // ITSELF like this project's existing authored ghost routes
            // (StartGhostRoute/MonumentRoutes.cs) - a short, fully-known
            // hop doesn't need general pathfinding's uncertainty at all,
            // since the survivor is already walking directly at the
            // door and doorways are flush with the floor either side (no
            // real elevation change to get wrong). Explicitly reserved for
            // OwnerID matching this survivor - a door it didn't place
            // still only gets the plain SetOpen+resume treatment above,
            // never this guaranteed hop, so this can never become a way
            // to bypass another base's real security (Lucas's own
            // explicit boundary: "we don't want the bot potentially
            // phasing through doors that it never placed, defeats the
            // purpose of building a base to secure your loot").
            if (blockingDoor.OwnerID == npc.userID)
            {
                // Real hardcoded door-route preference (2026-08-29,
                // seventh round) - see TryStartHomeDoorRouteCrossing's own
                // doc comment. Tried FIRST here too, same as the proactive
                // StartWalkingWithRecovery entry point - an authored
                // recording should win over the geometry-based
                // GhostThroughOwnDoor fallback below whenever one exists
                // for this design, not just on a fresh top-level walk.
                if (TryStartHomeDoorRouteCrossing(survivor, destination, onArrived, onFailed, null))
                {
                    return;
                }

                VerbosePuts($"'{survivor.Character.Alias}' opened its own door and is stepping straight through it.");
                GhostThroughOwnDoor(survivor, blockingDoor, destination, onArrived, onFailed, doorContinuationTier);
                return;
            }

            // Real live bug (2026-08-29, fourth round - Lucas's own
            // report: approaching a closed door it doesn't own "side on"
            // instead of square-on left it oscillating rather than ever
            // cleanly stepping through, even once opened - a real player
            // walks up to face a door before opening it, never sidesteps
            // through at an angle). Square up to the door FIRST, on
            // whichever side the survivor is already standing (a plain
            // real walk, no phasing - this never crosses the door's own
            // plane, so it stays well inside the "never for a door it
            // didn't place" boundary above), then open it, then hand back
            // to the normal ladder for the rest of the real journey -
            // approaching flush with the doorway's own axis is exactly
            // what already works reliably for GhostThroughOwnDoor's own
            // door-center anchoring above.
            Vector3 doorApproachPoint = ComputeDoorApproachPoint(npc, blockingDoor);

            void OpenDoorAndContinue()
            {
                if (!blockingDoor.IsDestroyed && !blockingDoor.IsOpen())
                {
                    blockingDoor.SetOpen(true);
                    blockingDoor.SendNetworkUpdate();
                }

                VerbosePuts($"'{survivor.Character.Alias}' opened a closed door that was blocking its path.");
                StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: doorContinuationTier);
            }

            // Real live bug (2026-08-29, fifth round - Lucas's own
            // explicit framing: a flat foundation built on sloped ground
            // always ends up with ITS door sitting some real height above
            // the ground on whichever edge happened to land on the
            // downhill side - not a build mistake, just unavoidable given
            // flat foundations on uneven terrain, and "hard to tell when
            // they will build it which way" since that depends on the
            // site's own slope, not a choice this project controls for.
            // "A real player could still technically make it through the
            // door by jumping up into the frame" - a plain collision-
            // respecting StartWalking has no equivalent (real per-step
            // climb height is far smaller than a real player's jump), so
            // it just oscillates at the base of the gap forever, same
            // failure StartLevelledPhasing already solves for
            // GhostThroughOwnDoor's own OWNED-door case. Reused here too,
            // capped to DoorJumpableHeight so this never becomes a way to
            // silently no-clip past a genuinely broken/unreachable height
            // difference - only ever a real player's own realistic jump.
            float approachHeightGap = Mathf.Abs(npc.transform.position.y - doorApproachPoint.y);

            if (approachHeightGap > DoorVerticalAlignmentThreshold && approachHeightGap <= DoorJumpableHeight)
            {
                StartLevelledPhasing(survivor, doorApproachPoint, onArrived: OpenDoorAndContinue, onFailed: OpenDoorAndContinue);
            }
            else
            {
                StartWalking(survivor, doorApproachPoint, onArrived: OpenDoorAndContinue, onFailed: OpenDoorAndContinue);
            }

            return;
        }

        if (_engine.NavigationManager.TryFindBlockingBarricade(npc.transform.position, BarricadeAttackDetectionRange, out Barricade blockingBarricade))
        {
            VerbosePuts($"'{survivor.Character.Alias}' found a real barricade nearby - approaching it to break through instead of trying to route around.");

            int continuationTier = recoveryTier == 0 ? 1 : recoveryTier;

            // StartAttackingBarricade assumes it's ALREADY in range (same
            // contract as StartAttackingContainer) - it doesn't walk
            // closer itself. Given TryFindBlockingBarricade's detection
            // radius (6m) is now deliberately wider than melee range
            // (2026-08-16, widened after the barricade wasn't being found
            // at the old 3m directional-raycast range), the survivor needs
            // a real approach leg first, same as any other container/
            // corpse interaction, before the swing loop can assume
            // IsWithinLootRange will actually pass on its first tick.
            Vector3 approachPoint = GetApproachPoint(blockingBarricade, npc);

            StartWalking(
                survivor,
                approachPoint,
                onArrived: () => StartAttackingBarricade(survivor, blockingBarricade,
                    onSuccess: () => StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: continuationTier),
                    onFailed: () => EscalateStuckRecovery(survivor, destination, onArrived, onFailed, continuationTier)),
                onFailed: () => EscalateStuckRecovery(survivor, destination, onArrived, onFailed, continuationTier));

            return;
        }

        // Real "am I stuck ON a tree/ore node itself" check (2026-08-28,
        // Lucas's own live report: bots caught oscillating in place within
        // ~1m of a resource node rather than being genuinely blocked by
        // anything else). Checked on every stuck episode, same reasoning
        // as the barricade check just above (a survivor can clear one
        // obstruction only to catch a second later in the same walk). If a
        // gather-capable survivor is standing this close to a live tree/
        // ore node, the node itself is almost certainly what's actually
        // blocking the path rather than a real navmesh/geometry problem -
        // gather it, then resume the original destination at an advanced
        // tier exactly like the barricade branch does. Trees checked
        // before ore, same priority order TryStartResourceGatheringFallback
        // already uses.
        if (_engine.NavigationManager.TryFindNearestTreeEntity(npc.transform.position, StuckResourceNodeDetectionRadius, out TreeEntity blockingTree, candidate => !IsInMonumentAvoidZone(candidate.transform.position) && !IsResourceNodePoisoned(candidate))
            && HasAnyGatherCapableTool(npc, TreeGatherToolPriority))
        {
            VerbosePuts($"'{survivor.Character.Alias}' is stuck right next to a live tree ('{blockingTree.ShortPrefabName}') - gathering it before continuing.");

            int nodeContinuationTier = recoveryTier == 0 ? 1 : recoveryTier;

            StartGatheringResourceNode(
                survivor,
                blockingTree,
                TreeGatherToolPriority,
                onSuccess: () => StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: nodeContinuationTier),
                onFailed: () => StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: nodeContinuationTier));

            return;
        }

        if (_engine.NavigationManager.TryFindNearestOreResourceEntity(npc.transform.position, StuckResourceNodeDetectionRadius, out OreResourceEntity blockingOre, candidate => !IsInMonumentAvoidZone(candidate.transform.position) && !IsResourceNodePoisoned(candidate))
            && HasAnyGatherCapableTool(npc, OreGatherToolPriority))
        {
            VerbosePuts($"'{survivor.Character.Alias}' is stuck right next to a live ore node ('{blockingOre.ShortPrefabName}') - gathering it before continuing.");

            int nodeContinuationTier = recoveryTier == 0 ? 1 : recoveryTier;

            StartGatheringResourceNode(
                survivor,
                blockingOre,
                OreGatherToolPriority,
                onSuccess: () => StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: nodeContinuationTier),
                onFailed: () => StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: nodeContinuationTier));

            return;
        }

        if (recoveryTier == 0)
        {
            VerbosePuts($"'{survivor.Character.Alias}' got stuck - trying to wiggle free before giving up.");

            // See StuckReassessPause's own doc comment - a believable
            // "notice I'm stuck, look around" beat before the wiggle
            // itself starts, rather than instantly snapping into motion
            // the same tick the block was detected.
            FaceDirection(npc, destination - npc.transform.position);

            timer.Once(StuckReassessPause, () =>
            {
                BasePlayer reassessNpc = survivor.Player;

                if (reassessNpc == null || reassessNpc.IsDestroyed)
                {
                    onFailed?.Invoke();
                    return;
                }

                if (reassessNpc.IsWounded())
                {
                    EscalateStuckRecovery(survivor, destination, onArrived, onFailed, recoveryTier);
                    return;
                }

                TryWiggleFree(survivor, destination, wiggled =>
                {
                    if (wiggled)
                    {
                        VerbosePuts($"'{survivor.Character.Alias}' wiggled free - retrying its destination.");
                    }

                    StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 1);
                });
            });

            return;
        }

        if (recoveryTier == 1)
        {
            FaceDirection(npc, destination - npc.transform.position);

            timer.Once(StuckReassessPause, () =>
            {
                BasePlayer reassessNpc = survivor.Player;

                if (reassessNpc == null || reassessNpc.IsDestroyed)
                {
                    onFailed?.Invoke();
                    return;
                }

                if (reassessNpc.IsWounded())
                {
                    EscalateStuckRecovery(survivor, destination, onArrived, onFailed, recoveryTier);
                    return;
                }

                // NavMesh.SamplePosition only checks proximity to A
                // navmesh surface, not whether it's actually reachable
                // from where the survivor currently is - a genuinely
                // isolated position (the whole point of this tier) can
                // have a "nearby" navmesh point that's just as
                // unreachable, for the identical underlying reason. A
                // live trace showed exactly this: every nudge attempt
                // immediately failed with the same NoPath, wasting a full
                // real-time walk attempt each time. Checking
                // TryCalculatePath first - the real pathing engine, not
                // distance-based sampling - skips straight past a doomed
                // attempt instead of pretending it might work.
                if (TryFindNavMeshNudgePoint(reassessNpc.transform.position, out Vector3 navMeshPoint)
                    && _engine.NavigationManager.TryCalculatePath(reassessNpc.transform.position, navMeshPoint, new RustNavMeshPath(), out _))
                {
                    VerbosePuts($"'{survivor.Character.Alias}' still stuck - trying to reach the nearest real, reachable navmesh point at {navMeshPoint}.");

                    StartWalking(
                        survivor,
                        navMeshPoint,
                        onArrived: () =>
                        {
                            VerbosePuts($"'{survivor.Character.Alias}' reached solid navmesh - retrying its original destination.");
                            StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 2);
                        },
                        onFailed: () => StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 2));

                    return;
                }

                StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 2);
            });

            return;
        }

        if (recoveryTier == 2)
        {
            FaceDirection(npc, destination - npc.transform.position);

            timer.Once(StuckReassessPause, () =>
            {
                BasePlayer reassessNpc = survivor.Player;

                if (reassessNpc == null || reassessNpc.IsDestroyed)
                {
                    onFailed?.Invoke();
                    return;
                }

                if (reassessNpc.IsWounded())
                {
                    EscalateStuckRecovery(survivor, destination, onArrived, onFailed, recoveryTier);
                    return;
                }

                if (TryEmergencyTeleport(survivor))
                {
                    VerbosePuts($"'{survivor.Character.Alias}' exhausted wiggling and a navmesh nudge - emergency-relocated a short distance to recover.");
                    StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 3);
                    return;
                }

                StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 3);
            });

            return;
        }

        // Real "the actual obstruction is a cactus, not the destination
        // itself" check (2026-08-28, Lucas's own live report: two smaller-
        // form-factor cactus variants - cactus-3/cactus-7, confirmed via
        // his own /lr.debug.scan - wedged a survivor exactly the way the
        // original single-trunk cactus already proved capable of
        // (IsBlockedByCactus's own doc comment), which then invoked this
        // exact phase-through as a "last resort" and walked it straight
        // through solid ground. The RecordPotentialAvoidZone(destination)
        // call below this branch doesn't help here - destination is
        // whatever this survivor was ORIGINALLY trying to reach (confirmed
        // live: 'BluntRunner655' got wedged against 'DE_Cactus_Part_04',
        // then phased 75m to a completely unrelated destination), not the
        // cactus's own position, so nothing about the actual bad spot ever
        // got learned. Checked here instead - IsBlockedByCactus's own 2m
        // radius easily covers being "wedged against" one - and gives up
        // cleanly rather than phasing, same as any other genuinely
        // unreachable case, while feeding the survivor's OWN current
        // position (where the cactus actually is) into the avoid-zone
        // system so this exact spot stops trapping every survivor that
        // ever walks near it.
        if (IsBlockedByCactus(npc.transform.position))
        {
            VerbosePuts($"'{survivor.Character.Alias}' is wedged against a cactus - giving up on this destination rather than phasing through the ground to reach it.");
            RecordPotentialAvoidZone(npc.transform.position);
            onFailed?.Invoke();
            return;
        }

        VerbosePuts($"'{survivor.Character.Alias}' exhausted every real-movement recovery option (wiggle, navmesh nudge, emergency teleport) - phasing directly to the destination as a genuine last resort.");

        // Feeds the same self-learning monument-avoid-zone system
        // PoisonAreaNow already uses (LivingRust.MonumentAvoidZones.cs) -
        // but keyed to destination here, not the survivor's own give-up
        // position. PoisonAreaNow only ever fires from an onFailed branch,
        // yet phasing calls onArrived on success (it always "succeeds" by
        // walking straight through geometry) - so a destination that
        // genuinely has no legitimate path (sealed room, isolated navmesh
        // island, a fragment below the map) was never being recorded at
        // all, letting the exact same bad destination get phased-to over
        // and over by every survivor that ever targets it. destination
        // itself is the fixed anchor (derived from the same container/node's
        // position each retry), unlike the stuck position which drifts
        // attempt to attempt - so repeat offenders now merge into one
        // confirmed zone after AvoidZoneConfirmThreshold hits, same as any
        // other avoid zone, and every existing container/tree/ore candidate
        // filter already checks IsInMonumentAvoidZone, so a confirmed spot
        // is excluded everywhere for free.
        RecordPotentialAvoidZone(destination);
        StartPhasingToDestination(survivor, destination, onArrived, onFailed);
    }

    /// <summary>
    /// True last resort (2026-08-16, Lucas's own explicit request) once
    /// wiggle/navmesh-nudge/emergency-teleport have ALL failed - moves the
    /// survivor directly toward destination via a real, raw
    /// transform.position write (the same mechanism TryEmergencyTeleport
    /// already uses for its own instant relocation, confirmed the actual
    /// way to bypass collision - npc.MovePosition() ALONE is NOT a true
    /// bypass, it's still collision-resolved, confirmed live earlier this
    /// session when an early noclip debug tool using MovePosition alone
    /// got physically caught on top of a car mid-flight), stepped at
    /// normal running speed instead of one big teleport jump - so it reads
    /// as ordinary movement to anyone watching, not a warp. Completely
    /// bypasses TryGetNextStep/IsBodyOverlapping/CalculatePath - by design,
    /// this is the option for when a destination is real and reachable in
    /// principle (a real player can walk there) but every one of this
    /// project's own collision/pathing systems has already been given a
    /// fair, repeated chance and still can't find a way, most likely
    /// because of real, dense interior clutter (the Abandoned Supermarket
    /// keycard room: barricade -> desk -> nearby crates all in one tight
    /// space) rather than a further bug worth chasing. Damage/combat
    /// hitboxes are completely untouched - this only ever writes position,
    /// nothing about the survivor's actual collider/hurtbox changes.
    /// </summary>
    // Below this height difference, StartPhasingToDestination's own
    // single diagonal leg is fine as-is (real steps/thresholds/small
    // terrain noise) - StartLevelledPhasing only kicks in for a genuine
    // floor-height mismatch, matching Lucas's own "if it isn't the same
    // Y axis as the bot" phrasing (2026-08-29, third round).
    private const float DoorVerticalAlignmentThreshold = 0.3f;

    /// <summary>
    /// Real fix for "phase up and ontop of the building" (2026-08-29,
    /// third round - Lucas's own live report after his own traceme: a
    /// real player walking in/out of a doorway holds essentially flat Y
    /// the whole way, only ever a normal step's worth of vertical change).
    /// GhostThroughOwnDoor/CrossHomeDoor's own single-leg
    /// StartPhasingToDestination call moves both axes at once - straight
    /// diagonally toward the door's exact transform.position - and
    /// because phasing has zero collision, if the survivor's own current
    /// Y is off from the door's real floor height (stuck below/above its
    /// own foundation, a stale precomputed DoorPosition, a multi-level
    /// base), that diagonal climbs/dives straight through the roof/floor
    /// instead of being blocked by it, and can end WaypointArriveDistance-
    /// close to the target while still well off the real floor, with nothing
    /// nearby for SnapToGround to catch. Splitting into a vertical-only
    /// leg (stationary horizontally, snapping to the door's own foundation
    /// height first) then a horizontal-only leg locked to that same height
    /// removes the diagonal entirely - matches Lucas's own proposed fix
    /// exactly ("gradually incline to the door... then lock the Y axis of
    /// whatever the current foundation is"). Skipped entirely (falls
    /// straight through to a plain StartPhasingToDestination) when the
    /// two Ys already roughly match, since real doors on a single-level
    /// base never need this at all.
    /// </summary>
    private void StartLevelledPhasing(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || Mathf.Abs(npc.transform.position.y - destination.y) < DoorVerticalAlignmentThreshold)
        {
            StartPhasingToDestination(survivor, destination, onArrived, onFailed);
            return;
        }

        Vector3 verticalAlignPoint = npc.transform.position;
        verticalAlignPoint.y = destination.y;

        StartPhasingToDestination(
            survivor,
            verticalAlignPoint,
            onArrived: () => StartPhasingToDestination(survivor, destination, onArrived, onFailed),
            onFailed: () => StartPhasingToDestination(survivor, destination, onArrived, onFailed));
    }

    // Real safety-valve timeout (2026-09-01, live report: a door-route
    // crossing's phase-to-near-end leg silently hung for 14 real seconds
    // with zero log output - no error, no arrival, no failure - before
    // whatever eventually noticed fell back to the old generic stuck-
    // recovery ladder, producing wild Y-axis teleporting). Prime suspect:
    // `if (npc.IsWounded()) { return; }` below has no time limit at all -
    // if the survivor takes damage mid-phase (a real live risk here, since
    // Lucas tests right next to these bots and this session's own memory
    // notes confirm he deliberately shoots bots to end broken tests), this
    // loop just spins forever doing nothing the whole time it's wounded,
    // with no escape. This applies everywhere StartPhasingToDestination is
    // used, not just door routes, but door routes are the one place that
    // previously had NO bound at all downstream either. 8s is generous for
    // any real phase leg (all observed ones finish in 1-3s) while still
    // being far short of "hangs indefinitely."
    private const float PhaseToDestinationMaxSeconds = 8f;

    private void StartPhasingToDestination(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveMovement(survivor);

        Timer phaseTimer = null;
        float deadline = UnityEngine.Time.realtimeSinceStartup + PhaseToDestinationMaxSeconds;
        float nextDiagLog = UnityEngine.Time.realtimeSinceStartup;

        phaseTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                phaseTimer.Destroy();
                _activeMovement.Remove(characterId);
                onFailed?.Invoke();
                return;
            }

            // Real concurrency-detection safety net (2026-09-01, live
            // report + log-confirmed: position jumped ~3.8m in a single
            // tick while GhostEnterHomeForDeposit's own step logic never
            // writes anything but small incremental steps - something ELSE
            // was concurrently touching this same survivor's position,
            // most likely its normal background task loop deciding to
            // move it for an unrelated reason mid-ghost-route, since that
            // new isolated function deliberately doesn't register in any
            // of the guards the rest of this project's movement respects).
            // _activeMovement[characterId] always points at whichever
            // timer currently "owns" this survivor's movement - if it no
            // longer points at THIS phaseTimer, someone else has already
            // taken over without our knowledge, so stop fighting for
            // control immediately rather than continuing to write
            // positions on top of whatever that other system is doing.
            if (!_activeMovement.TryGetValue(characterId, out Timer registeredTimer) || !ReferenceEquals(registeredTimer, phaseTimer))
            {
                phaseTimer.Destroy();
                VerbosePuts($"'{survivor.Character.Alias}' phase-to-destination lost ownership of its own movement mid-flight (another system took over) - bailing out.");
                onFailed?.Invoke();
                return;
            }

            // Real diagnostic-only throttled trajectory log (2026-09-01,
            // live report: a phase hangs completely for a full 8s timeout,
            // confirmed NOT wounded - damage-diag shows zero damage during
            // the actual hang window). One line/second is enough to see
            // whether the real position is genuinely stuck at one spot
            // (something resetting the write), drifting the WRONG way, or
            // just never getting network-close enough - can't tell which
            // from the timeout message alone.
            if (UnityEngine.Time.realtimeSinceStartup >= nextDiagLog)
            {
                nextDiagLog = UnityEngine.Time.realtimeSinceStartup + 1f;
                VerbosePuts($"phase-diag: '{survivor.Character.Alias}' pos={npc.transform.position}, destination={destination}, remaining={Vector3.Distance(npc.transform.position, destination):F2}m, wounded={npc.IsWounded()}.");
            }

            if (UnityEngine.Time.realtimeSinceStartup >= deadline)
            {
                phaseTimer.Destroy();
                _activeMovement.Remove(characterId);
                VerbosePuts($"'{survivor.Character.Alias}' phase-to-destination timed out after {PhaseToDestinationMaxSeconds:F0}s (likely wounded/stuck the whole time) - giving up.");
                onFailed?.Invoke();
                return;
            }

            if (npc.IsWounded())
            {
                return;
            }

            Vector3 current = npc.transform.position;
            Vector3 toDestination = destination - current;
            float remaining = toDestination.magnitude;

            if (remaining < WaypointArriveDistance)
            {
                phaseTimer.Destroy();
                _activeMovement.Remove(characterId);
                SnapToGround(npc);

                VerbosePuts($"'{survivor.Character.Alias}' reached its destination (phased through).");
                onArrived?.Invoke();
                return;
            }

            float stepDistance = RunSpeed * WalkTickInterval;
            Vector3 next = remaining <= stepDistance ? destination : current + toDestination.normalized * stepDistance;

            // Field set/order matches the native StartFollowing branch
            // exactly now - see StartGhostRoute's identical fix/doc comment
            // for the full investigation notes (2026-08-16): modelState
            // parity with native has been ruled out as the actual cause,
            // most likely structural (native's real velocity vs a discrete
            // position teleport), not fixed yet.
            npc.modelState.sprinting = true;
            npc.modelState.ducked = false;
            npc.modelState.ducking = 0f;
            npc.modelState.waterLevel = npc.WaterFactor();
            npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
            npc.SendModelState(true);

            FaceDirection(npc, toDestination);

            // No SendNetworkUpdateImmediate here (2026-08-16 fix) - unlike
            // TryEmergencyTeleport, where an instant visible snap is the
            // whole point, this is supposed to read as ordinary walking.
            // Confirmed live: including it made every step look like a
            // sudden pop/disappear-reappear instead of smooth motion -
            // every OTHER hand-built movement step in this project (see
            // ApplyMovementStep) only ever calls plain MovePosition each
            // tick and lets Rust's own normal interpolation carry it
            // smoothly between updates, which is what this needs too.
            npc.transform.position = next;
            npc.MovePosition(next);

            // Real diagnostic-only check (2026-09-01, live report + math
            // proof: logged Y sat frozen at 24.34 for over a minute
            // straight despite writing a clearly lower next.y every single
            // tick - remaining never shrank at all). This confirms WITHIN
            // THE SAME TICK whether npc.MovePosition(next) itself is
            // immediately rejecting/overriding the write we just made
            // (Rust's own collision resolution snapping back up onto a
            // floor slab above), or whether the position holds here and
            // gets reset later, between ticks, by something outside our
            // control entirely.
            if (Vector3.Distance(npc.transform.position, next) > 0.05f)
            {
                VerbosePuts($"phase-diag: '{survivor.Character.Alias}' MovePosition rejected the write THIS TICK - wrote {next}, actual is now {npc.transform.position} (diff {Vector3.Distance(npc.transform.position, next):F2}m).");
            }

            survivor.Position = next;
            survivor.Character.Position = next;
        });

        _activeMovement[characterId] = phaseTimer;
    }

    /// <summary>
    /// Authored ghost routes per monument, keyed by a case-insensitive
    /// substring match against the real monument name (same matching style
    /// as AutonomyExcludedMonumentSubstrings) against a FOLDER under
    /// TraceDirectory rather than individual filenames (2026-08-16, Lucas's
    /// own explicit refinement of the original per-file list design):
    /// "Make a overarching folder called Supermarket_A that the bots refer
    /// to each time it rolls for Supermarket." Every .csv directly inside
    /// that folder counts as one candidate route for this monument - one is
    /// picked at random each time it's triggered (see
    /// TryGetGhostRouteForMonument). Adding a further recorded route (up to
    /// Lucas's planned 5 total for Supermarket_A) is then just dropping a
    /// new .csv into the folder - no code change needed. "_A" distinguishes
    /// this specific supermarket instance's name/layout if a second,
    /// differently-laid-out Abandoned Supermarket ever needs its own "_B"
    /// folder.
    /// </summary>
    private static readonly Dictionary<string, string> MonumentGhostRouteFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["supermarket"] = "Supermarket_A",

        // Scaffolded 2026-08-16 for Lucas's next tracing pass - folder
        // exists under LivingRust/traces/ already but starts empty
        // (TryGetGhostRouteForMonument returns false for an empty folder,
        // so this is a silent no-op until real .csv routes land in it).
        // "lighthouse" is ALSO still in AutonomyExcludedMonumentSubstrings
        // (LivingRust.GearScore.cs) - that's deliberate, not an oversight:
        // it's excluded for a genuine navigation reason (sits on its own
        // island, no land route, and this project has no ocean/water
        // movement yet), not the "safezone, no loot" reason most of that
        // list's other entries have. Remove it from
        // AutonomyExcludedMonumentSubstrings only once this folder actually
        // has traces in it AND ocean/water movement exists to get a
        // survivor there in the first place - a ghost route alone can't
        // solve the "how does it even reach the island" half of this one.
        //
        // Ranch/Barn/Compound/Bandit Camp/Fishing Village were removed from
        // this registry 2026-08-16 (originally scaffolded here by mistake) -
        // see AutonomyExcludedMonumentSubstrings' own doc comment: all five
        // are real safezones with no lootable containers at all, so a ghost
        // route through them would have nothing to actually accomplish.
        // Underwater Lab and Apartments Complex skipped by Lucas's own
        // explicit request (underwater entry, and no stated reason
        // respectively) - not registered here for now either.
        ["lighthouse"] = "Lighthouse_A",

        // Scaffolded 2026-08-16 - Medium/High loot-tier monuments
        // (GetMonumentTier's own tier lists, LivingRust.GearScore.cs), all
        // currently open to autonomy already (none of these are in
        // AutonomyExcludedMonumentSubstrings), so unlike lighthouse above,
        // a populated folder here takes effect immediately - no exclusion-
        // list change needed to activate one. Folders start empty and are
        // silent no-ops until Lucas records real traces into them. Unlike
        // Abandoned Supermarket, none of these have confirmed navigation
        // problems yet - they're candidates by loot value, not by hard
        // evidence of being broken, so it's worth treating each one as
        // "record a trace, see if bots actually needed it" rather than
        // assuming every one of these folders will end up used.
        ["military_tunnel"] = "MilitaryTunnel_A",
        ["launch_site"] = "LaunchSite_A",
        // Traced 2026-08-21 - Nuclear Missile Silo, Tier 3/High monument.
        // Single way in and out, so this one trace covers both looting and
        // the card puzzle (see CardPuzzleRouteFolders' own
        // "nuclear_missile_silo" entry, red keycard only, no fuse). 1
        // path. Heavy elevator use throughout - not yet handled, flagged
        // for a dedicated pass.
        ["nuclear_missile_silo"] = "NuclearMissileSilo_A",
        // Traced 2026-08-20 - Airfield. Initially assumed loot-less (no
        // crates visible at the time), corrected same day once a real loot
        // fill-group refresh spawned 3 crates - all on one side of the
        // monument, so the route is deliberately linear with minimal
        // variance between its 2 paths rather than the usual 5, matching
        // what's actually there. Also has its own card puzzle - see
        // CardPuzzleRouteFolders' "airfield" entry (Cardreader_K,
        // green+blue+2 fuses), LivingRust.CardPuzzles.cs.
        ["airfield"] = "Airfield_A",
        ["military_base"] = "MilitaryBase_A",
        // Traced 2026-08-21 - Trainyard, Tier 2/Medium monument. 4 loot
        // paths. Also has its own card puzzle - see CardPuzzleRouteFolders'
        // "trainyard" entry (Cardreader_M, green+blue+1 fuse),
        // LivingRust.CardPuzzles.cs.
        ["trainyard"] = "TrainYard_A",
        // Traced 2026-08-19 - Powerplant, Tier 2/Medium monument. Reuses
        // the same confirmed-good trace as the puzzle route
        // (CardPuzzleRouteFolders' own "powerplant" entry, Cardreader_H) -
        // same reasoning as Military Tunnel's identical repoint: the
        // recorded path already covers the real loot along the way, so no
        // separate recording is needed. 1 path.
        ["powerplant"] = "PowerPlant_A",
        // Traced 2026-08-20 - Water Treatment Plant, Tier 2/Medium
        // monument. Puzzle needs blue keycard + 1 fuse only, no green -
        // includes a real WheelSwitch-driven roller door and a PressButton
        // door-release (see CardPuzzleRouteFolders' own "water_treatment_plant"
        // entry, Cardreader_L, LivingRust.CardPuzzles.cs). 4 loot paths
        // recorded by Lucas.
        ["water_treatment_plant"] = "WaterTreatmentPlant_A",

        // Traced 2026-08-19 - Arctic Research Base, Tier 2/Medium monument.
        // Puzzle needs blue keycard ONLY, no fuse (CardPuzzleRouteFolders'
        // own "arctic_research_base" entry, Cardreader_J - the first
        // RequiresFuse: false exception). 1 dedicated loot path recorded by
        // Lucas.
        ["arctic_research_base"] = "ArcticResearchBase_A",
        // Traced 2026-08-19 - Sewer Branch, reclassified from Medium to
        // Low/Tier 1 same day (see MediumTierMonumentSubstrings' own doc
        // comment). 1 dedicated loot path plus its puzzle trace
        // (CardPuzzleRouteFolders' "radtown_small" entry, Cardreader_I)
        // reused here too - same reasoning as Military Tunnel/Powerplant's
        // identical repoint. 2 paths total.
        ["radtown_small"] = "SewerBranch_A",

        // Traced 2026-08-17 - Oxum's Gas Station (roadside/gas_station_1.prefab,
        // confirmed via scanmonumentloot log output), 5 paths recorded by Lucas.
        ["gas_station"] = "GasStation_A",

        // Traced 2026-08-17 - Harbor (specifically harbor_2, NOT harbor_1
        // or ferry_terminal_1 - all three are real distinct monuments
        // sharing the same "harbor" folder in the asset path, confirmed via
        // scanmonumentloot log output, so the substring here is
        // deliberately "harbor_2" and not the broader "harbor" to avoid
        // misrouting the other two onto this specific layout's trace). Only
        // 3 paths recorded (not the usual 5) - Lucas's own explicit call:
        // larger monuments have less loot-position randomness and a more
        // fixed traversal path in practice, so fewer variants are needed to
        // cover the real spread.
        ["harbor_2"] = "Harbor2_A",

        // Scaffolded 2026-08-18 - Harbor (specifically harbor_1, a distinct
        // real monument from harbor_2/ferry_terminal_1 - same "harbor"
        // asset-folder caveat as Harbor2_A above). Folder starts empty -
        // silent no-op until Lucas records real traces into it.
        ["harbor_1"] = "Harbor1_A",

        // Scaffolded 2026-08-17 - Mining Outpost, a Low/Tier-1 monument
        // (unlisted in MediumTierMonumentSubstrings/HighTierMonumentSubstrings,
        // LivingRust.GearScore.cs, so it defaults to Low) and already open
        // to autonomy (not in AutonomyExcludedMonumentSubstrings). Folder
        // starts empty - silent no-op (TryGetGhostRouteForMonument returns
        // false for an empty folder) until Lucas records real traces into
        // it, same pattern as every other scaffolded entry above.
        ["mining_outpost"] = "MiningOutpost_A",

        // Scaffolded 2026-08-18 - Satellite Dish, a Low/Tier-1 monument
        // (unlisted in MediumTierMonumentSubstrings/HighTierMonumentSubstrings)
        // and already open to autonomy. Folder starts empty - silent no-op
        // until Lucas records real traces into it.
        ["satellite_dish"] = "SatelliteDish_A",

        // Scaffolded 2026-08-18 - Radtown (real prefab "radtown_1", distinct
        // from "radtown_small"/Sewer Branch, a Medium-tier monument).
        // Folder starts empty - silent no-op until Lucas records traces.
        ["radtown_1"] = "Radtown_A",

        // Traced 2026-08-18 - Sphere Tank ("Dome", real prefab
        // monument/small/sphere_tank.prefab), 2 paths recorded by Lucas -
        // small monument, just one way up and a couple of ways down, no
        // need for more variety than that.
        ["sphere_tank"] = "SphereTank_A",

        // Traced 2026-08-18 - Junkyard (real prefab
        // monument/medium/junkyard_1.prefab - sits in the "medium" asset
        // folder, but Lucas's own explicit call: keep it Tier 1, rolled
        // just as easily as Harbor/Supermarket, NOT added to
        // MediumTierMonumentSubstrings). 5 paths recorded by Lucas. No
        // card-reader puzzle at this monument - deliberately no
        // Cardreader_X folder/registry entry for it.
        ["junkyard"] = "Junkyard_A",

        // Traced 2026-08-19 - Ferry Terminal (specifically ferry_terminal_1,
        // a distinct real monument from harbor_1/harbor_2 - same "harbor"
        // asset-folder caveat as Harbor2_A above), 4 paths recorded by
        // Lucas. Also has its own card puzzle - see CardPuzzleRouteFolders'
        // ferry_terminal entry (Cardreader_G, green tier), LivingRust.CardPuzzles.cs.
        ["ferry_terminal"] = "FerryTerminal_A",

        // Scaffolded 2026-08-19 - Oil Rig (real prefab substrings
        // "oilrig_1" = small, "oilrig_2" = large, confirmed via
        // monument_loot_zones.json's own scanned monument names -
        // deliberately registered by exact substring, not the broader
        // "oilrig" HighTierMonumentSubstrings already uses for gear-score
        // tiering, so small/large get their own separate trace folders).
        // Both parked in AutonomyExcludedMonumentSubstrings for now, Lucas's
        // own explicit call - real prerequisites (boat travel, fending off
        // RHIB scientist NPCs) don't exist yet, and getting there will be
        // its own dedicated test->fix->test->fix pass, deliberately saved
        // for later rather than half-built now. Folders start empty -
        // silent no-op either way until both the prerequisites AND real
        // traces exist.
        ["oilrig_1"] = "OilRigSmall_A",
        ["oilrig_2"] = "OilRigLarge_A",
    };

    /// <summary>
    /// Picks a random authored ghost route .csv for monumentName out of its
    /// registered folder (see MonumentGhostRouteFolders), if one's
    /// registered and the folder actually has at least one .csv in it right
    /// now. Returns false with no output for any monument without a
    /// registered/populated folder - callers fall back to the fully generic
    /// search/movement path exactly as before this existed. traceFilePath
    /// is a full relative path (TraceDirectory/folder/file.csv), ready to
    /// pass straight to TryLoadTraceWaypoints.
    /// </summary>
    // Real ghost-route exclusivity (2026-09-24, Lucas's own explicit ask:
    // "multiple bots can loot the same monument simultaneously but only 1
    // bot is able to do a ghostroute" - normal ambient/opportunistic
    // looting at a monument stays completely unrestricted, this only gates
    // the special scripted route). Keyed by monument NAME, same
    // established (if imprecise across multiple same-named instances)
    // convention _monumentOccupants already uses, for consistency rather
    // than introducing a second keying scheme. Time-expiring rather than
    // requiring an explicit release on every real exit path (success,
    // failure, death, watchdog rescue) - simpler and more robust than
    // trying to wire cleanup into all of them; GhostRouteSlotLifetimeSeconds
    // is generous enough to cover a real multi-stop route, short enough
    // that a stuck/killed bot doesn't lock a monument out for long.
    // Per-ROUTE exclusivity (2026-09-27, Lucas's own revision of the above):
    // every bot at a monument can run a ghost route, but each individual
    // route file is held by at most one bot at a time (an in-progress claim,
    // time-expiring for the same robustness reason), and once a route is
    // completed it is exhausted for GhostRouteExhaustSeconds before anyone
    // can run that same route again. A bot that finds every route of its
    // monument claimed/exhausted falls back to ordinary natural looting.
    private readonly Dictionary<string, (Guid CharacterId, float ExpiresAt)> _ghostRouteClaims = new();
    private readonly Dictionary<string, float> _ghostRouteExhaustedUntil = new();
    private const float GhostRouteSlotLifetimeSeconds = 300f;
    private const float GhostRouteExhaustSeconds = 600f;

    private bool TryClaimGhostRouteForMonument(string monumentName, Guid characterId, out string traceFilePath)
    {
        traceFilePath = null;

        if (string.IsNullOrEmpty(monumentName))
        {
            return false;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;

        foreach (KeyValuePair<string, string> entry in MonumentGhostRouteFolders)
        {
            if (monumentName.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string folderPath = $"{TraceDirectory}/{entry.Value}";

            if (!Directory.Exists(folderPath))
            {
                return false;
            }

            List<string> available = new();

            foreach (string candidate in Directory.GetFiles(folderPath, "*.csv"))
            {
                string route = candidate.Replace('\\', '/');

                if (!IsGhostRouteClaimable(route, monumentName, characterId, now))
                {
                    continue;
                }

                available.Add(route);
            }

            if (available.Count == 0)
            {
                return false;
            }

            traceFilePath = available[UnityEngine.Random.Range(0, available.Count)];
            _ghostRouteClaims[traceFilePath] = (characterId, now + GhostRouteSlotLifetimeSeconds);
            return true;
        }

        return false;
    }

    private void CompleteGhostRoute(string traceFilePath)
    {
        _ghostRouteClaims.Remove(traceFilePath);
        _ghostRouteExhaustedUntil[traceFilePath] = UnityEngine.Time.realtimeSinceStartup + GhostRouteExhaustSeconds;
    }

    private bool TryGetGhostRouteForMonument(string monumentName, out string traceFilePath)
    {
        traceFilePath = null;

        if (string.IsNullOrEmpty(monumentName))
        {
            return false;
        }

        foreach (KeyValuePair<string, string> entry in MonumentGhostRouteFolders)
        {
            if (monumentName.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string folderPath = $"{TraceDirectory}/{entry.Value}";

            if (!Directory.Exists(folderPath))
            {
                return false;
            }

            string[] candidates = Directory.GetFiles(folderPath, "*.csv");

            if (candidates.Length == 0)
            {
                return false;
            }

            string chosen = candidates[UnityEngine.Random.Range(0, candidates.Length)];
            traceFilePath = chosen.Replace('\\', '/');
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the nearest real monument to origin that ACTUALLY has a
    /// registered, populated ghost route - not just the nearest monument of
    /// any type (2026-08-16, Lucas's own explicit fix: "make ghostroute
    /// specifically use the closest monument and then roll the dice to
    /// whatever path it should take at that monument"). Standing near two
    /// different monuments, where the CLOSER one has no registered route
    /// and a slightly farther one does, previously fell all the way back to
    /// the hardcoded DefaultGhostRouteTraceFile instead of finding the
    /// farther-but-actually-usable one - this searches every monument
    /// matching ANY MonumentGhostRouteFolders key, not just whichever one
    /// happens to be nearest overall, and rolls the dice only once it's
    /// found the nearest one that qualifies.
    /// </summary>
    private bool TryGetGhostRouteForNearestMonument(Vector3 origin, float maxDistance, out string traceFilePath, out MonumentInfo monument)
    {
        traceFilePath = null;
        monument = null;

        if (MonumentAccess.GetAllMonuments().Count == 0)
        {
            return false;
        }

        float bestDistanceSqr = maxDistance * maxDistance;
        MonumentInfo nearestCandidate = null;

        foreach (MonumentInfo candidate in MonumentAccess.GetAllMonuments())
        {
            if (candidate == null
                || !MonumentGhostRouteFolders.Keys.Any(substring => candidate.name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                continue;
            }

            float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

            if (distanceSqr < bestDistanceSqr)
            {
                bestDistanceSqr = distanceSqr;
                nearestCandidate = candidate;
            }
        }

        if (nearestCandidate == null || !TryGetGhostRouteForMonument(nearestCandidate.name, out string rolledFilePath))
        {
            // Either nothing registered is within range, or the nearest
            // registered monument's own folder is currently empty (a
            // monument can be "registered" here before it has any real
            // traces yet - see MonumentGhostRouteFolders' own doc comment).
            return false;
        }

        traceFilePath = rolledFilePath;
        monument = nearestCandidate;
        return true;
    }

    /// <summary>
    /// How close two consecutive rows of a real /lr.debug.traceme CSV need
    /// to be to collapse into a single ghost-route waypoint - a real trace
    /// samples every WalkTickInterval (0.05s) regardless of whether the
    /// admin was moving or standing still looting something, so a genuine
    /// multi-second stop (the crate, the keycard) shows up as dozens of
    /// near-identical rows in a row. Collapsing those down to one waypoint
    /// keeps the route exactly as fine-grained as the real walk during
    /// actual movement, without the phase-through logic wasting ticks
    /// "arriving" at the same point over and over.
    ///
    /// Widened from 0.15m to 0.4m (2026-08-16, real live bug: "bots still
    /// definitely aren't running during the ghostroute... moving at
    /// walking speed") - at real running speed, consecutive raw trace rows
    /// land about RunSpeed * WalkTickInterval = 0.275m apart, which is
    /// ABOVE the old 0.15m threshold, so almost none of them were actually
    /// collapsing during real forward movement - nearly every single raw
    /// sample became its own waypoint. That first fix undershot its own
    /// target though (2026-08-19, symptom recurred: "only sort of jog...
    /// quicker than walk but slower than run") - it aimed to beat the
    /// natural per-tick step (0.275m) but never checked it against
    /// WaypointArriveDistance (0.5m), the actual arrival threshold
    /// StartGhostRoute tests against. 0.4m is BELOW that 0.5m threshold, so
    /// most collapsed waypoints still sat within arrival range of each
    /// other from the very first tick toward them - the phase-through loop
    /// kept counting itself as "arrived" before taking a real step, and an
    /// arrival tick does real work (LootWhateverIsHereNow's physics scans,
    /// tearing down and rebuilding the movement timer) INSTEAD OF stepping
    /// that tick, not in addition to it. Raised to 0.6m - safely above
    /// WaypointArriveDistance itself, not just the per-tick step size - so
    /// a waypoint can no longer be pre-satisfied by arrival distance before
    /// the bot has actually covered real ground toward it.
    /// </summary>
    private const float GhostRouteWaypointCollapseDistance = 0.6f;

    /// <summary>
    /// One collapsed ghost-route stop - the real recorded position, plus
    /// how long the admin actually stood there in the source trace
    /// (DwellSeconds, summed from every consecutive raw row collapsed into
    /// this waypoint - see TryLoadTraceWaypoints). Added 2026-08-16, Lucas's
    /// own explicit request after confirming the route itself was "literally
    /// perfect": the bot "seamlessly walk[ing] and loot[ing] with no issues"
    /// read as inhuman precisely because it skipped every real pause a human
    /// naturally takes while looting/reading a container - replaying those
    /// same pauses (see StartGhostRoute) closes that gap.
    /// </summary>
    private readonly struct GhostRouteWaypoint
    {
        public readonly Vector3 Position;
        public readonly float DwellSeconds;

        public GhostRouteWaypoint(Vector3 position, float dwellSeconds)
        {
            Position = position;
            DwellSeconds = dwellSeconds;
        }

        public GhostRouteWaypoint WithPosition(Vector3 position) => new(position, DwellSeconds);
    }

    /// <summary>
    /// How far from a trace's own first recorded point (or, for the manual
    /// /lr.debug.ghostroute command and TryGetGhostRouteForNearestMonument,
    /// the caller's own position) to search for a real registered monument.
    /// Widened from 60m to 150m (2026-08-16, real live bug: testing at
    /// Military Tunnels - a "large" monument, confirmed via
    /// /lr.debug.scanmonumentloot as
    /// 'assets/bundled/prefabs/autospawn/monument/large/military_tunnel_1.prefab' -
    /// found nothing within 60m and silently fell back to the hardcoded
    /// Supermarket default, even though the registered "military_tunnel"
    /// substring itself was correct). Large monuments can have their
    /// registered transform origin genuinely far from wherever a player or
    /// bot is actually standing inside/around them - matches
    /// CommittedMonumentRangeRadius (LivingRust.MonumentLootZones.cs),
    /// the same wider radius already established elsewhere in this project
    /// specifically for "is this position still meaningfully inside a large
    /// monument."
    /// </summary>
    private const float GhostRouteRecordingMonumentSearchRadius = 150f;

    /// <summary>
    /// Parses a real /lr.debug.traceme CSV (elapsed_s,x,y,z,... - see
    /// RunDebugTraceMe's own writer) into a clean waypoint list - the
    /// actual real path an admin walked, used verbatim rather than a
    /// hand-picked approximation of it. Lucas's own explicit framing
    /// (2026-08-16): "I want the bot to take this EXACT path... to avoid
    /// jittering, oscillating etc" - since this is real recorded ground
    /// truth through a monument's own worst navigation trouble spot, using
    /// it directly sidesteps needing to guess at waypoints by hand.
    ///
    /// Returned waypoints are MONUMENT-RELATIVE, not raw world coordinates,
    /// whenever the trace was recorded near a real monument (recordedAtMonument
    /// comes back non-null) - 2026-08-16, Lucas's own explicit question:
    /// "these traces... are hardcoded to specific coordinates on THIS map,
    /// how do we alleviate this for other maps? and what if there are
    /// multiple of these supermarkets on the map... there are 2?" Same fix
    /// for both: every position gets converted via
    /// recordedAtMonument.transform.InverseTransformPoint into an offset
    /// relative to the monument's OWN transform (position + rotation),
    /// exactly the way MonumentLootZone.LocalOffset already works for
    /// container clusters. A monument's interior layout is a fixed prefab -
    /// only its placement/rotation on the terrain differs per seed/instance -
    /// so a route recorded at one instance re-projects correctly onto ANY
    /// instance of the same monument type (ProjectGhostRouteToMonument),
    /// whether that's a second supermarket on this same map or the only one
    /// on a completely different map. recordedAtMonument comes back null
    /// (waypoints stay raw world coordinates, unchanged from before this
    /// existed) only if the trace genuinely wasn't recorded near any real
    /// monument at all.
    /// </summary>
    /// <summary>
    /// requiredMonumentName (2026-08-18, real live bug fix): when provided,
    /// the anchor search below only considers monuments whose .name EXACTLY
    /// matches it (the same real monument TYPE the trace is being loaded
    /// for, e.g. every instance of "harbor/harbor_2.prefab" specifically) -
    /// not literally whichever MonumentInfo happens to be nearest the
    /// trace's first waypoint. Confirmed live: Harbor2_A was recorded
    /// standing right next to a small power substation that happens to sit
    /// closer to the recording's own first waypoint than Harbor2's own
    /// (much larger) MonumentInfo origin - the old unfiltered
    /// TryGetNearestMonument call anchored the WHOLE route to that
    /// substation instead, so reprojecting onto the real Harbor2 monument
    /// sent survivors ~130m off and ~4m below the real floor (confirmed via
    /// the navmesh's own nearest-point probe reporting real ground level at
    /// that X/Z), which the pathfinder correctly refused to route to -
    /// exhausting every stuck-recovery tier and ending in the last-resort
    /// phase-through-geometry recovery cutting straight down through the
    /// floor toward that bad point (JumpyTorch4/CrazyTorch, live-observed).
    /// null (the debug command's own manual-filename path, which has no
    /// specific monument type to require) falls back to the old unfiltered
    /// "just find something nearby" behaviour.
    /// </summary>
    private bool TryLoadTraceWaypoints(string filePath, out List<GhostRouteWaypoint> waypoints, out MonumentInfo recordedAtMonument, string requiredMonumentName = null)
    {
        waypoints = new List<GhostRouteWaypoint>();
        recordedAtMonument = null;

        if (!File.Exists(filePath))
        {
            return false;
        }

        string[] lines = File.ReadAllLines(filePath);

        for (int i = 1; i < lines.Length; i++)
        {
            string[] columns = lines[i].Split(',');

            if (columns.Length < 4
                || !float.TryParse(columns[1], out float x)
                || !float.TryParse(columns[2], out float y)
                || !float.TryParse(columns[3], out float z))
            {
                continue;
            }

            Vector3 point = new Vector3(x, y, z);

            if (waypoints.Count == 0 || Vector3.Distance(waypoints[^1].Position, point) >= GhostRouteWaypointCollapseDistance)
            {
                waypoints.Add(new GhostRouteWaypoint(point, 0f));
            }
            else
            {
                // Close enough to the last kept waypoint to collapse into it
                // rather than becoming a waypoint of its own - but every raw
                // row still spent here is real, live time the admin stood
                // at this exact spot (the trace samples every WalkTickInterval
                // regardless of movement), so it accumulates as that
                // waypoint's own dwell time instead of just being discarded.
                GhostRouteWaypoint last = waypoints[^1];
                waypoints[^1] = new GhostRouteWaypoint(last.Position, last.DwellSeconds + WalkTickInterval);
            }
        }

        // Y-only smoothing pass, MEDIAN not average (2026-08-16, second
        // iteration - Lucas's own framing: "physically lock the bot's Y
        // axis when it goes through these friction points"). A moving
        // AVERAGE still lets one real outlier sample pull the result
        // partway, which is exactly why the first version still visibly
        // flickered at doorways - a real admin's recorded Y naturally
        // wobbles a few cm from foot placement/camera bob/brushing clutter,
        // invisible on a real player since their own animation absorbs it,
        // but confirmed live to still play back as an up/down flicker after
        // averaging. A median is a much closer match to "lock unless it's
        // real" - it completely ignores brief spikes that don't make up a
        // majority of the window, and only moves once a change is
        // genuinely sustained across most of it (a real staircase climbs
        // steadily across many consecutive waypoints, so it still comes
        // through untouched). X/Z are left exactly as recorded - only
        // vertical noise caused the visible flicker.
        if (waypoints.Count > GhostRouteSmoothingWindow)
        {
            float[] smoothedY = new float[waypoints.Count];
            float[] windowBuffer = new float[GhostRouteSmoothingWindow + 1];

            for (int i = 0; i < waypoints.Count; i++)
            {
                int windowStart = Mathf.Max(0, i - GhostRouteSmoothingWindow / 2);
                int windowEnd = Mathf.Min(waypoints.Count - 1, i + GhostRouteSmoothingWindow / 2);
                int count = 0;

                for (int j = windowStart; j <= windowEnd; j++)
                {
                    windowBuffer[count] = waypoints[j].Position.y;
                    count++;
                }

                Array.Sort(windowBuffer, 0, count);
                smoothedY[i] = windowBuffer[count / 2];
            }

            for (int i = 0; i < waypoints.Count; i++)
            {
                Vector3 position = waypoints[i].Position;
                waypoints[i] = waypoints[i].WithPosition(new Vector3(position.x, smoothedY[i], position.z));
            }
        }

        // Convert from raw world coordinates to monument-relative offsets -
        // see this method's own doc comment for why. Done last, after
        // collapsing/smoothing (which both only care about relative
        // distances between points, so operating in world space first
        // doesn't change their result), against whichever real monument
        // sits nearest the trace's own first waypoint.
        if (waypoints.Count > 0 && TryGetNearestMonumentOfType(waypoints[0].Position, GhostRouteRecordingMonumentSearchRadius, requiredMonumentName, out MonumentInfo nearestToRecording))
        {
            recordedAtMonument = nearestToRecording;

            for (int i = 0; i < waypoints.Count; i++)
            {
                Vector3 localOffset = nearestToRecording.transform.InverseTransformPoint(waypoints[i].Position);
                waypoints[i] = waypoints[i].WithPosition(localOffset);
            }
        }

        return waypoints.Count > 0;
    }

    /// <summary>
    /// Re-projects a monument-relative waypoint list (see
    /// TryLoadTraceWaypoints' own doc comment) onto a SPECIFIC real
    /// monument instance's actual world transform - the other half of the
    /// map/instance-portability fix. Safe to call even on a waypoint list
    /// that was never localized to begin with (recordedAtMonument came back
    /// null) - callers just skip calling this in that case and use the raw
    /// world-space waypoints as-is.
    /// </summary>
    private List<GhostRouteWaypoint> ProjectGhostRouteToMonument(List<GhostRouteWaypoint> localWaypoints, MonumentInfo targetMonument)
    {
        List<GhostRouteWaypoint> projected = new(localWaypoints.Count);

        foreach (GhostRouteWaypoint waypoint in localWaypoints)
        {
            Vector3 worldPosition = targetMonument.transform.TransformPoint(waypoint.Position);
            projected.Add(waypoint.WithPosition(worldPosition));
        }

        return projected;
    }

    /// <summary>
    /// Nearest real monument to origin whose own name EXACTLY matches
    /// monumentName (not a substring match like TryGetNearestMonument/
    /// AutonomyExcludedMonumentSubstrings use) - for re-projecting a ghost
    /// route recorded at one specific monument instance onto whichever
    /// instance of that SAME exact monument type is actually nearest a
    /// given position (e.g. the manual /lr.debug.ghostroute command,
    /// testing near a different supermarket instance than the one the
    /// trace was recorded at).
    /// </summary>
    private bool TryGetNearestMonumentByExactName(Vector3 origin, string monumentName, float maxDistance, out MonumentInfo monument)
    {
        monument = null;

        if (string.IsNullOrEmpty(monumentName) || MonumentAccess.GetAllMonuments().Count == 0)
        {
            return false;
        }

        float bestDistanceSqr = maxDistance * maxDistance;

        foreach (MonumentInfo candidate in MonumentAccess.GetAllMonuments())
        {
            if (candidate == null || candidate.name != monumentName)
            {
                continue;
            }

            float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

            if (distanceSqr < bestDistanceSqr)
            {
                bestDistanceSqr = distanceSqr;
                monument = candidate;
            }
        }

        return monument != null;
    }

    /// <summary>
    /// Centred moving-average window size for the Y-smoothing pass above -
    /// small enough (1s either side at the real 0.05s trace sample rate)
    /// to not blur a genuine staircase's own real slope into something
    /// mushy, large enough to actually average out the kind of rapid
    /// multi-sample noise confirmed live at the doorway/counter.
    /// </summary>
    private const int GhostRouteSmoothingWindow = 20;

    /// <summary>
    /// Caps how many corpses LootWhateverIsHereNow will empty in one call -
    /// pure safety net against a pathological loop (there's no real
    /// scenario with more than a couple of fresh kills piled at one exact
    /// spot), not an expected real limit.
    /// </summary>
    private const int GhostRouteMaxCorpsesPerStop = 5;

    /// <summary>
    /// Direct "loot whatever's within real reach right now, no travel
    /// needed" pass for a ghost-route stop - deliberately NOT the full
    /// ContinueLootTask search/filter/claim machinery (avoid-zones, safe-
    /// zone ownership, other survivors' claims, card-reader exclusion) -
    /// none of that applies here, the whole point of a ghost route is a
    /// short, pre-validated, already-known-safe real path, not organic
    /// search. Containers here are looted directly (TransferAllItems) with
    /// no destruction/approach-angle step, matching what's already been
    /// confirmed live for this specific room's containers (they loot
    /// cleanly with no "breaking open" needed once actually in range).
    ///
    /// Corpses added 2026-08-16 (Lucas's own explicit request: "if it kills
    /// scientists, it should still loot the bodies, but afterwards, go back
    /// to ghostrouting") - a scientist killed mid-combat right on or near
    /// the route counts as exactly the kind of "whatever's here" this
    /// method already exists for. Uses the same direct bulk transfer as
    /// containers (TransferAllItems per real container on the corpse, not
    /// the paced per-item animation LootCorpseAndContinue uses for organic
    /// search) - same "instant, pre-validated path" reasoning the rest of
    /// this method already follows. Looped rather than a single check since
    /// a corpse becomes exempt from TryFindNearestLootableCorpse's own
    /// filter the moment it's actually empty, so this naturally stops on
    /// its own once nothing's left - GhostRouteMaxCorpsesPerStop is purely
    /// a safety cap, not an expected real limit.
    /// </summary>
    private void LootWhateverIsHereNow(Survivor survivor, BasePlayer npc)
    {
        List<StorageContainer> containers = new();

        _engine.NavigationManager.GetAllLootContainersInRange(
            npc.transform.position,
            LootInteractionRange,
            candidate => candidate.inventory != null && candidate.inventory.itemList.Count > 0,
            containers);

        foreach (StorageContainer container in containers)
        {
            if (container == null || container.IsDestroyed || container.inventory == null)
            {
                continue;
            }

            int moved = TransferAllItems(container.inventory, npc.inventory, npc, out List<string> movedShortnames);

            if (moved > 0)
            {
                VerbosePuts($"ghost-route: '{survivor.Character.Alias}' looted {moved} item stack(s) from '{container.ShortPrefabName}': {string.Join(", ", movedShortnames)}.");
                OnLootObtained(survivor, npc, movedShortnames);
            }

            // Same fix as LootContainerDirectly's own doc comment
            // (2026-08-18) - nothing here ever opens a real loot panel, so
            // LootContainer.PlayerStoppedLooting's native destroyOnEmpty
            // cleanup (what normally makes an emptied crate/barrel
            // disappear) never fires on its own. Confirmed live: a bot
            // emptying a container mid-ghost-route left it sitting there
            // visibly empty until an admin manually opened and closed it
            // themselves.
            if (container is LootContainer lootContainer && lootContainer.destroyOnEmpty && container.inventory.itemList.Count == 0)
            {
                container.Kill();
            }
        }

        for (int i = 0; i < GhostRouteMaxCorpsesPerStop; i++)
        {
            bool foundCorpse = _engine.NavigationManager.TryFindNearestLootableCorpse(
                npc.transform.position,
                LootInteractionRange,
                out LootableCorpse corpse,
                candidate => candidate.containers != null && candidate.containers.Any(c => c.itemList.Count > 0));

            if (!foundCorpse)
            {
                break;
            }

            List<string> movedFromCorpse = new();
            int movedCount = 0;

            foreach (ItemContainer container in corpse.containers)
            {
                if (container == null)
                {
                    continue;
                }

                movedCount += TransferAllItems(container, npc.inventory, npc, out List<string> containerShortnames);
                movedFromCorpse.AddRange(containerShortnames);
            }

            if (movedCount > 0)
            {
                VerbosePuts($"ghost-route: '{survivor.Character.Alias}' looted {movedCount} item stack(s) from a corpse: {string.Join(", ", movedFromCorpse)}.");
                OnLootObtained(survivor, npc, movedFromCorpse);
            }
            else
            {
                // Genuinely nothing transferable (e.g. every item on it is
                // IsNeverLootItem-excluded) - break rather than looping
                // GhostRouteMaxCorpsesPerStop times against the same corpse,
                // since it'll never pass the filter differently next time.
                break;
            }
        }

        if (_engine.NavigationManager.TryFindNearestDroppedItem(npc.transform.position, LootInteractionRange, out DroppedItem droppedItem, candidate => !IsNeverLootItem(candidate.item.info.shortname)))
        {
            string shortname = droppedItem.item.info.shortname;

            if (TryTransferSingleItem(droppedItem.item, npc.inventory, npc))
            {
                droppedItem.RemoveItem();
                droppedItem.Kill();

                VerbosePuts($"ghost-route: '{survivor.Character.Alias}' picked up a dropped '{shortname}'.");
                OnLootObtained(survivor, npc, new List<string> { shortname });
            }
        }
    }

    /// <summary>
    /// Real dwell time recorded at a waypoint (GhostRouteWaypoint.DwellSeconds)
    /// below this is treated as incidental - a real player's momentary slow-
    /// down while turning a corner or eyeing a doorway, not a genuine stop -
    /// and doesn't pause the route at all.
    /// </summary>
    private const float GhostRoutePauseMinSeconds = 0.35f;

    /// <summary>
    /// Caps how long the bot will ever hold at one waypoint, regardless of
    /// how long the admin's own recorded dwell was there (e.g. genuinely
    /// got distracted mid-recording) - a real pause for looting/reading a
    /// container should read as human, not become its own new stuck-bot
    /// complaint.
    /// </summary>
    private const float GhostRoutePauseMaxSeconds = 4f;

    /// <summary>
    /// Safety valve for a wheel-driven ProgressDoor wait (StartGhostRoute's
    /// own arrival branch, 2026-08-20) - NOT the real completion signal
    /// (that's door.openProgress reaching 1, checked every 0.5s regardless
    /// of how long it takes). This only exists so a genuinely stuck wheel
    /// (its own internal RotateProgress timer silently stopped for some
    /// reason without the door ever finishing) falls back to moving the
    /// route on instead of hanging a survivor at that waypoint forever.
    /// </summary>
    private const float WheelWaitMaxSeconds = 60f;

    /// <summary>
    /// How far a ghost-route survivor will detour to loot a real corpse or
    /// dropped-item bag it notices mid-route (2026-08-21, Lucas's own
    /// explicit request - "20 metres") - see StartGhostRouteLootScan's own
    /// corpse/bag search. Deliberately wider than GhostRouteLootDetourRadius
    /// (the SAME scan's existing 10m container/dropped-item radius) and
    /// LootInteractionRange/LootWhateverIsHereNow's much tighter in-place
    /// pickup radius - Lucas's own specific figure for this case.
    /// </summary>
    private const float GhostRouteCorpseInterruptRadius = 20f;

    /// <summary>
    /// How many raycast HasLineOfSight checks a spotted corpse/bag gets
    /// before TryInterruptGhostRouteForNearbyLoot gives up on it - 2026-08-21,
    /// Lucas's own explicit request/spec ("spread the 3 checks out across
    /// 15, 30, and 45 seconds"). Guards against committing a real walk-
    /// detour (with its own NoPath/Stuck recovery ladder) toward a corpse
    /// that's genuinely behind a wall from the survivor's current position -
    /// spread out rather than checked once, since LOS from far away can
    /// change as the survivor's own ghost route naturally carries it
    /// closer/around a corner in the meantime, without ever actually
    /// interrupting the route to find out.
    /// </summary>
    private const int GhostRouteLosMaxChecks = 3;

    /// <summary>
    /// Real spacing between each of GhostRouteLosMaxChecks' checks - first
    /// check lands at +15s after a corpse/bag is first spotted, second at
    /// +30s, third at +45s (Lucas's own explicit numbers).
    /// </summary>
    private const float GhostRouteLosCheckIntervalSeconds = 15f;

    /// <summary>
    /// How long a target that failed all GhostRouteLosMaxChecks stays
    /// excluded from being re-picked as a FRESH detour candidate - purely a
    /// "don't immediately re-run the same doomed 45s check cycle against
    /// the same still-blocked corpse every tick" throttle, NOT a permanent
    /// blacklist. Deliberately short and deliberately separate from
    /// anything LootWhateverIsHereNow reads - giving up on the ACTIVE
    /// detour never stops the survivor from looting that same corpse for
    /// free the ordinary way if its own route later genuinely carries it
    /// within LootWhateverIsHereNow's own much tighter LootInteractionRange
    /// (a real live question Lucas asked: a corpse that fails LOS 3 times
    /// is NOT nulled out forever - it's just skipped for THIS detour
    /// mechanism for a while, walking directly over it still loots it via
    /// the completely independent close-range pass).
    /// </summary>
    private const float GhostRouteLosBlockedCooldownSeconds = 60f;

    /// <summary>
    /// Per-survivor in-progress LOS recheck state for
    /// TryInterruptGhostRouteForNearbyLoot - which target it's watching,
    /// how many checks have already failed, and when the next one is due.
    /// Only one target tracked at a time per survivor (mirrors
    /// _pendingGhostRouteToResume's own one-at-a-time pattern) - a closer/
    /// different candidate replacing the currently-watched one restarts the
    /// count fresh against the new target rather than carrying over stale
    /// failures against an unrelated entity.
    /// </summary>
    private readonly Dictionary<Guid, (NetworkableId TargetId, int ChecksSoFar, float NextCheckTime)> _pendingGhostRouteLosRecheck = new();

    /// <summary>
    /// Targets that failed all GhostRouteLosMaxChecks recently, keyed by
    /// the corpse/bag's own NetworkableId, valued by when the exclusion
    /// expires - see GhostRouteLosBlockedCooldownSeconds' own doc comment
    /// for why this is short and non-permanent. Global (not per-survivor)
    /// since a target genuinely blocked by geometry is blocked the same way
    /// regardless of which survivor's ghost route notices it next.
    /// </summary>
    private readonly Dictionary<NetworkableId, float> _ghostRouteLosBlockedUntil = new();

    /// <summary>
    /// Which waypoint index a survivor was last heading toward on an
    /// in-progress ghost route (2026-08-16, Lucas's own explicit request:
    /// "if the bot gets engaged in combat, after it disengages... it
    /// returns to its last known position of the ghost route and resumes
    /// the ghostrouting until the route is fully finished"). Updated every
    /// time StartGhostRoute begins moving toward a waypoint, so combat
    /// externally destroying the phase timer mid-flight (StartCombat's own
    /// CancelActiveMovement, same as every other movement type) still
    /// leaves this pointing at the right index to resume from - see
    /// TryResumeGhostRoute, called from ResumeOrStartLootTask
    /// (LivingRust.Recycling.cs) ahead of the normal fresh-task fallback.
    /// Removed once a route genuinely finishes (nothing left to resume) or
    /// once a resume attempt actually starts (avoids a stale double-resume
    /// if somehow triggered twice) - also cleared on death/full reset, see
    /// OnPlayerDeath (LivingRust.Hooks.cs).
    /// </summary>
    private readonly Dictionary<Guid, (List<GhostRouteWaypoint> Waypoints, int Index, Action OnComplete)> _pendingGhostRouteToResume = new();

    /// <summary>
    /// How many times TryResumeGhostRoute has resumed after a combat/loot
    /// detour since the last time the survivor was ACTUALLY back moving on
    /// the route - 2026-08-16, Lucas's own explicit request: "it should
    /// only engage in a maximum 8 of these extra steps per disengagement...
    /// this way the bot can't get overloaded by multiple random containers
    /// etc if it gets dragged off course." Past MaxGhostRouteDetourAttempts,
    /// TryResumeGhostRoute stops looting at the fight-end spot and just
    /// forces its way straight back - real self-defense (StartCombat) is
    /// NOT blocked by this, a bot still fights back if attacked again while
    /// forcing its way back, it just stops rewarding itself with more loot
    /// each time. Reset to 0 the instant it's genuinely back on the route
    /// (StartGhostRoute actually resumes) - "if the bot gets instantly
    /// engaged again after it is on the ghostroute, its event counter is
    /// reset," so a fresh disengagement always gets its own clean budget
    /// rather than accumulating across unrelated interruptions.
    /// </summary>
    private readonly Dictionary<Guid, int> _ghostRouteDetourAttempts = new();

    private const int MaxGhostRouteDetourAttempts = 8;

    /// <summary>
    /// Per-survivor periodic scan timer for real nearby loot while a ghost
    /// route is active - 2026-08-16, Lucas's own explicit request: "have
    /// the bot exit the ghost route upon lootable containers or dropped
    /// items within 10 metres, let it escalate through 8 events of
    /// looting then hardcode back to the ghostroute." Started once per
    /// route commit (StartGhostRouteLootScan, called alongside the initial
    /// _pendingGhostRouteToResume registration - see
    /// EscalateSearchToMonumentZone/RunDebugGhostRoute), not re-created per
    /// waypoint. Stopped the moment the route genuinely finishes, fails, or
    /// the survivor dies/resets - see StopGhostRouteLootScan.
    /// </summary>
    private readonly Dictionary<Guid, Timer> _ghostRouteLootScanTimers = new();

    /// <summary>
    /// How far a real container or dropped item can be from the survivor
    /// before the periodic scan below detours to it - Lucas's own explicit
    /// figure ("within 10 metres").
    /// </summary>
    private const float GhostRouteLootDetourRadius = 10f;

    /// <summary>
    /// How often the ghost-route loot scan checks for nearby loot - same
    /// cadence as the established EnRouteLootScanIntervalSeconds pattern
    /// normal long-distance walks already use for the identical "don't
    /// walk straight past real loot" concern.
    /// </summary>
    private const float GhostRouteLootScanIntervalSeconds = 4f;

    /// <summary>
    /// Monuments where a mid-ghost-route recycler detour is deliberately
    /// suppressed entirely (2026-08-21, Lucas's own explicit request,
    /// after a real live death: HazyCamper74 broke off its Nuclear Missile
    /// Silo route to recycle 205m away, and by the time it walked all the
    /// way back the door it had swiped through had already re-closed
    /// (real ~10-15s open window) - it walked straight into the closed
    /// door's own kill-barrier without any way to know it was there,
    /// "died (Blunt)"). Both of these monuments already have real,
    /// confirmed-rough navmesh (the same tunnel this whole session's
    /// splice/telegraph work has been fighting), so a full inventory here
    /// is better left alone entirely until the route itself finishes,
    /// rather than risking a long, real round-trip through geometry that's
    /// already known to be unreliable - LootWhateverIsHereNow still keeps
    /// picking up whatever's within real reach at every stop regardless,
    /// same as always, only the RECYCLER DETOUR specifically is skipped.
    /// </summary>
    private static readonly string[] RecyclerRiskyMonumentSubstrings = { "nuclear_missile_silo", "launch_site" };

    /// <summary>
    /// How close counts as "near" a RecyclerRiskyMonumentSubstrings entry -
    /// generous enough to cover a monument's whole real footprint (both are
    /// large, multi-level structures), not a precise boundary check.
    /// </summary>
    private const float RecyclerRiskyMonumentCheckRadius = 150f;

    private static bool IsNearRecyclerRiskyMonument(Vector3 position)
    {
        if (MonumentAccess.GetAllMonuments().Count == 0)
        {
            return false;
        }

        foreach (MonumentInfo monument in MonumentAccess.GetAllMonuments())
        {
            if (monument == null)
            {
                continue;
            }

            bool isRisky = RecyclerRiskyMonumentSubstrings.Any(substring => monument.name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0);

            if (isRisky && Vector3.Distance(position, monument.transform.position) <= RecyclerRiskyMonumentCheckRadius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts (if not already running) a periodic scan that detours a
    /// ghost-routing survivor to any real container or dropped item within
    /// GhostRouteLootDetourRadius, shares the exact same
    /// _ghostRouteDetourAttempts/MaxGhostRouteDetourAttempts budget combat
    /// detours already use (looting a barrel on the way and getting
    /// dragged into 5 fights both count against the same "how far off
    /// course has this excursion gone" total - Lucas's own framing), and
    /// stops offering detours once that budget is spent, same as
    /// TryResumeGhostRoute already does for combat. Skips entirely while
    /// combat owns the survivor (that has its own, separate interruption
    /// path already). Requires no destruction to loot (RequiresDestructionToLoot)
    /// since LootWhateverIsHereNow itself has no barrel-breaking step -
    /// only containers/items it can actually empty instantly are worth
    /// detouring for.
    /// </summary>
    private void StartGhostRouteLootScan(Survivor survivor, Action onRouteComplete)
    {
        Guid characterId = survivor.Character.Id;

        if (_ghostRouteLootScanTimers.ContainsKey(characterId))
        {
            return;
        }

        Timer scanTimer = null;

        scanTimer = timer.Every(GhostRouteLootScanIntervalSeconds, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || !_pendingGhostRouteToResume.TryGetValue(characterId, out var resumeState))
            {
                // Route no longer active (finished/abandoned/survivor gone) -
                // nothing left to scan for.
                scanTimer.Destroy();
                _ghostRouteLootScanTimers.Remove(characterId);
                return;
            }

            if (_activeCombat.ContainsKey(characterId))
            {
                // Combat already owns this survivor's movement right now -
                // its own resume path (TryResumeGhostRoute) handles getting
                // back afterward, this scan just stays quiet until then.
                return;
            }

            // Bradley check (2026-08-22, Lucas's explicit request: "Bradley
            // = Death") - takes priority over every other detour below
            // (recycler, corpse/bag, container) since none of that matters
            // if the survivor gets caught in Bradley's engagement range.
            // Reuses StartFleeingFromThreat (the same mechanism unarmed-vs-
            // scientist/animal encounters already use) rather than new
            // movement code - it already runs away, reassesses on a timer,
            // and resumes via ResumeOrStartLootTask -> TryResumeGhostRoute
            // once clear, exactly the "break from the ghost route, seek
            // clear ground, then pick the route back up" behaviour asked
            // for. Deliberately scoped to the ghost-route scan only for now
            // (matches the literal request) - doesn't yet interrupt an
            // already-active scientist fight to flee a Bradley too.
            if (TryFindNearbyBradley(npc.transform.position, out BradleyAPC bradley))
            {
                VerbosePuts($"ghost-route: '{survivor.Character.Alias}' spotted a Bradley APC within {BradleyDangerRadius:F0}m - breaking the route to get clear.");
                StartFleeingFromThreat(survivor, bradley, $"'{survivor.Character.Alias}' spotted a Bradley APC - getting clear.", safeDistanceOverride: BradleyDangerRadius + 20f);
                return;
            }

            if (survivor.Character.CurrentTask != TaskType.Recycling
                && IsContainerFull(npc.inventory.containerMain)
                && !IsNearRecyclerRiskyMonument(npc.transform.position))
            {
                // Same "main inventory alone, not both main AND belt" rule
                // ContinueLootTask's own full-inventory check already uses
                // (see its doc comment) - 2026-08-17, Lucas's own explicit
                // request: "if the bot has a full inventory... it breaks
                // the ghostroute, does the WHOLE Recycler task... then goes
                // back to the ghost route... the same way that Combat
                // interrupts it." TryStartRecyclingTask's own StartWalking
                // call cancels this route's phase timer for us (every
                // movement-start function does), so there's no separate
                // CancelActiveMovement needed here - _pendingGhostRouteToResume
                // stays untouched throughout, exactly like a combat
                // interruption, so TryResumeGhostRoute (via
                // FinishRecyclerDetourOrIdle, LivingRust.Recycling.cs) can
                // pick the route back up once recycling genuinely finishes.
                // If there's no fodder worth recycling or no recycler in
                // range, TryStartRecyclingTask just returns false and the
                // route carries on as if this tick never happened.
                if (TryStartRecyclingTask(survivor))
                {
                    _ghostRouteRecyclerDetour.Add(characterId);
                    VerbosePuts($"ghost-route: '{survivor.Character.Alias}' is full up - breaking the route to recycle, will resume once done.");
                }

                return;
            }

            int attempts = _ghostRouteDetourAttempts.TryGetValue(characterId, out int existingAttempts) ? existingAttempts : 0;

            if (attempts >= MaxGhostRouteDetourAttempts)
            {
                return;
            }

            // 2026-08-18, Lucas's own explicit request: no opportunistic
            // CONTAINER/DROPPED-ITEM detours while a card-puzzle detour is
            // active ("remove the found loot nearby entirely... it only
            // loots once it's within reach") - a puzzle attempt is spending
            // a genuinely limited-use keycard/fuse, not worth risking on a
            // detour to loot that might sit somewhere the navmesh can't
            // actually reach (see IsUnreachableCrateOrBarrel/
            // IsUnreachableDroppedItem below - real live incident: a
            // dropped item near harbor_2's puzzle room repeatedly pulled
            // every test survivor 20-30m off the route into the same
            // broken navmesh pocket). LootWhateverIsHereNow still picks up
            // anything genuinely within reach at every real stop
            // regardless - only the DETOUR is skipped.
            //
            // Corpses/bags are a deliberate EXCEPTION (2026-08-21, Lucas's
            // own explicit follow-up) - TryHandleGhostRouteCorpseOrBagDetour
            // has its own real HasLineOfSight retry/cooldown safety net
            // this original container rule never had, making the same
            // "dragged into an unreachable navmesh pocket" risk far less
            // likely, so a corpse/bag mid-puzzle is allowed to interrupt
            // same as it would on an ordinary loot-run ghost route.
            if (_activeCardPuzzleSurvivors.Contains(characterId))
            {
                TryHandleGhostRouteCorpseOrBagDetour(survivor, npc, resumeState, attempts);
                return;
            }

            bool foundContainer = _engine.NavigationManager.TryFindNearestLootContainer(
                npc.transform.position,
                GhostRouteLootDetourRadius,
                out StorageContainer container,
                candidate => candidate.inventory != null && candidate.inventory.itemList.Count > 0
                    && !RequiresDestructionToLoot(candidate)
                    && !IsUnreachableCrateOrBarrel(npc.transform.position, candidate));

            DroppedItem droppedItem = null;
            bool foundDropped = !foundContainer && _engine.NavigationManager.TryFindNearestDroppedItem(
                npc.transform.position,
                GhostRouteLootDetourRadius,
                out droppedItem,
                candidate => !IsNeverLootItem(candidate.item.info.shortname)
                    && !IsUnreachableDroppedItem(npc.transform.position, candidate.transform.position));

            if (!foundContainer && !foundDropped)
            {
                TryHandleGhostRouteCorpseOrBagDetour(survivor, npc, resumeState, attempts);
                return;
            }

            Vector3 lootPosition = foundContainer ? container.transform.position : droppedItem.transform.position;
            int thisAttempt = attempts + 1;
            _ghostRouteDetourAttempts[characterId] = thisAttempt;

            VerbosePuts($"ghost-route: '{survivor.Character.Alias}' spotted real loot {Vector3.Distance(npc.transform.position, lootPosition):F0}m away - detouring ({thisAttempt}/{MaxGhostRouteDetourAttempts}).");

            StartLongDistanceWalk(
                survivor,
                lootPosition,
                "ghost route side-loot",
                onArrived: () =>
                {
                    BasePlayer arrivedNpc = survivor.Player;

                    if (arrivedNpc != null && !arrivedNpc.IsDestroyed)
                    {
                        LootWhateverIsHereNow(survivor, arrivedNpc);
                    }

                    StartGhostRoute(survivor, resumeState.Waypoints, resumeState.Index, resumeState.OnComplete);
                },
                onFailed: () => StartGhostRoute(survivor, resumeState.Waypoints, resumeState.Index, resumeState.OnComplete));
        });

        _ghostRouteLootScanTimers[characterId] = scanTimer;
    }

    /// <summary>
    /// Corpse/dropped-item-bag sibling of StartGhostRouteLootScan's own
    /// container/dropped-item detour above - 2026-08-21, Lucas's own
    /// explicit request ("a bot will interrupt a ghost route IF a corpse
    /// lootable container (bag or body) is within 20 metres"). Called from
    /// that same scan tick in two cases: immediately, while a card-puzzle
    /// detour is active (the ONLY opportunistic detour still allowed
    /// during a puzzle attempt - see its own call site's doc comment for
    /// why corpses/bags are exempt from the container/dropped-item
    /// puzzle-safety rule), or otherwise only once NEITHER a container nor
    /// a dropped item was found this cycle - a real container within
    /// GhostRouteLootDetourRadius still wins first outside a puzzle, same
    /// detour budget either way.
    ///
    /// Reuses the same TryFindNearestLootableCorpse/
    /// TryFindNearestDroppedItemContainer searches (and the same
    /// PlayerCorpse safe-zone/ownership exclusion) the ordinary loot-for-
    /// resources task already relies on, and the same shared _lootClaims
    /// registry so two survivors can't converge on the same corpse. Claims
    /// directly (bypassing ClaimLootTarget's LootTaskState parameter, which
    /// a ghost route doesn't have).
    ///
    /// Real HasLineOfSight raycast gate (Lucas's own explicit follow-up,
    /// after asking what happens if a spotted corpse turns out to be behind
    /// a wall) - a freshly-spotted target gets up to GhostRouteLosMaxChecks
    /// (3) checks, spaced ~GhostRouteLosCheckIntervalSeconds (15s) apart via
    /// _pendingGhostRouteLosRecheck, landing at roughly +15s/+30s/+45s after
    /// first being noticed (rounded to this scan's own 4s cadence). Only a
    /// target that PASSES a check actually triggers the real walk-detour
    /// below - failing all 3 adds it to _ghostRouteLosBlockedUntil for
    /// GhostRouteLosBlockedCooldownSeconds (60s) and gives up on it for
    /// THIS mechanism only. That cooldown is deliberately short and
    /// deliberately doesn't touch anything LootWhateverIsHereNow reads - a
    /// corpse that fails all 3 LOS checks is NOT permanently blacklisted:
    /// if the survivor's own route later genuinely carries it within
    /// LootWhateverIsHereNow's own much tighter LootInteractionRange (e.g.
    /// walking directly past/over it), that completely independent
    /// close-range pass still loots it for free, same as it always would.
    /// </summary>
    private void TryHandleGhostRouteCorpseOrBagDetour(Survivor survivor, BasePlayer npc, (List<GhostRouteWaypoint> Waypoints, int Index, Action OnComplete) resumeState, int attempts)
    {
        Guid characterId = survivor.Character.Id;
        float now = UnityEngine.Time.realtimeSinceStartup;

        // Real gap fix (2026-09-01, live report + screenshots: 20-40 bots
        // still piling up at Ranch even after the monument-exclusion fix
        // landed) - a bot trace confirmed one survivor genuinely stuck
        // oscillating within a ~6m box at Ranch's own coordinates well
        // AFTER that fix's reload, meaning the pileup wasn't (only) coming
        // from the main scan below (TryFindNearestLootableCorpse in
        // ContinueLootTask, already checks !IsInMonumentAvoidZone) - this
        // SEPARATE ghost-route corpse/bag interrupt had no monument check
        // at all. Once corpses exist at an excluded monument for any
        // reason (players/bots dying there), every OTHER bot merely
        // ghost-routing PAST it within GhostRouteCorpseInterruptRadius
        // would detour in via this completely different, unfiltered path,
        // regardless of whether it would ever have chosen to travel there
        // deliberately - a real self-sustaining snowball this fixes.
        bool foundCorpse = _engine.NavigationManager.TryFindNearestLootableCorpse(
            npc.transform.position,
            GhostRouteCorpseInterruptRadius,
            out LootableCorpse corpse,
            candidate => !IsLootTargetClaimed(candidate.net.ID)
                && (!_ghostRouteLosBlockedUntil.TryGetValue(candidate.net.ID, out float blockedUntil) || blockedUntil <= now)
                && candidate.containers != null
                && candidate.containers.Any(c => c != null && c.itemList.Count > 0)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && (candidate is not PlayerCorpse || candidate.playerSteamID == survivor.Character.BotId || !candidate.InSafeZone()));

        bool foundBag = _engine.NavigationManager.TryFindNearestDroppedItemContainer(
            npc.transform.position,
            GhostRouteCorpseInterruptRadius,
            out DroppedItemContainer bag,
            candidate => !IsLootTargetClaimed(candidate.net.ID)
                && (!_ghostRouteLosBlockedUntil.TryGetValue(candidate.net.ID, out float blockedUntil) || blockedUntil <= now)
                && candidate.inventory != null
                && candidate.inventory.itemList.Count > 0
                && !IsInMonumentAvoidZone(candidate.transform.position));

        if (!foundCorpse && !foundBag)
        {
            _pendingGhostRouteLosRecheck.Remove(characterId);
            return;
        }

        float corpseDistance = foundCorpse ? Vector3.Distance(npc.transform.position, corpse.transform.position) : float.MaxValue;
        float bagDistance = foundBag ? Vector3.Distance(npc.transform.position, bag.transform.position) : float.MaxValue;
        bool useCorpse = foundCorpse && corpseDistance <= bagDistance;

        NetworkableId targetId = useCorpse ? corpse.net.ID : bag.net.ID;
        BaseEntity targetEntity = useCorpse ? corpse : bag;

        if (!_pendingGhostRouteLosRecheck.TryGetValue(characterId, out var pending) || pending.TargetId != targetId)
        {
            // Freshly spotted (or the previously-watched target is gone/
            // replaced by a closer one) - first check scheduled for
            // +GhostRouteLosCheckIntervalSeconds from now, not immediately
            // (Lucas's own explicit spec: "15, 30, and 45 seconds").
            _pendingGhostRouteLosRecheck[characterId] = (targetId, 0, now + GhostRouteLosCheckIntervalSeconds);
            return;
        }

        if (now < pending.NextCheckTime)
        {
            // Still waiting for the next scheduled check.
            return;
        }

        if (!HasLineOfSight(npc, targetEntity))
        {
            int failedChecks = pending.ChecksSoFar + 1;

            if (failedChecks >= GhostRouteLosMaxChecks)
            {
                VerbosePuts($"ghost-route: '{survivor.Character.Alias}' gave up on a {(useCorpse ? "corpse" : "bag")} after {failedChecks} failed line-of-sight checks - likely behind a wall, skipping for now.");
                _pendingGhostRouteLosRecheck.Remove(characterId);
                _ghostRouteLosBlockedUntil[targetId] = now + GhostRouteLosBlockedCooldownSeconds;
            }
            else
            {
                _pendingGhostRouteLosRecheck[characterId] = (targetId, failedChecks, now + GhostRouteLosCheckIntervalSeconds);
            }

            return;
        }

        // Line of sight confirmed - commit to the real detour, same pattern
        // (attempt budget, StartLongDistanceWalk, LootWhateverIsHereNow on
        // arrival) as the container/dropped-item branch above.
        _pendingGhostRouteLosRecheck.Remove(characterId);
        _lootClaims[targetId] = now + LootClaimTtlSeconds;

        int thisAttempt = attempts + 1;
        _ghostRouteDetourAttempts[characterId] = thisAttempt;

        VerbosePuts($"ghost-route: '{survivor.Character.Alias}' spotted a {(useCorpse ? "corpse" : "bag")} {(useCorpse ? corpseDistance : bagDistance):F0}m away - detouring ({thisAttempt}/{MaxGhostRouteDetourAttempts}).");

        StartLongDistanceWalk(
            survivor,
            GetApproachPoint(targetEntity, npc),
            "ghost route corpse/bag side-loot",
            onArrived: () =>
            {
                BasePlayer arrivedNpc = survivor.Player;

                if (arrivedNpc != null && !arrivedNpc.IsDestroyed)
                {
                    LootWhateverIsHereNow(survivor, arrivedNpc);
                }

                _lootClaims.Remove(targetId);
                StartGhostRoute(survivor, resumeState.Waypoints, resumeState.Index, resumeState.OnComplete);
            },
            onFailed: () =>
            {
                _lootClaims.Remove(targetId);
                StartGhostRoute(survivor, resumeState.Waypoints, resumeState.Index, resumeState.OnComplete);
            });
    }

    /// <summary>
    /// Stops this survivor's ghost-route loot scan, if one's running - see
    /// StartGhostRouteLootScan's own doc comment. Called once the route
    /// genuinely finishes/fails and from the same death/full-reset cleanup
    /// sites _pendingGhostRouteToResume already uses.
    /// </summary>
    private void StopGhostRouteLootScan(Guid characterId)
    {
        if (_ghostRouteLootScanTimers.TryGetValue(characterId, out Timer scanTimer))
        {
            scanTimer.Destroy();
            _ghostRouteLootScanTimers.Remove(characterId);
        }
    }

    /// <summary>
    /// Walks a survivor along a real recorded route (see
    /// TryLoadTraceWaypoints) using the same collision-bypassing
    /// phase-through movement as StartPhasingToDestination, looting
    /// whatever's within reach and pausing for a moment (mirroring the
    /// admin's own real recorded dwell time - see GhostRouteWaypoint,
    /// 2026-08-16 Lucas's own explicit request: "the bot seems to
    /// seamlessly walk and loot with no issues (not really humane)") at
    /// every waypoint along the way. Once the whole route is complete, hands
    /// control straight back to the normal autonomous loot loop
    /// (onComplete) - this is a scripted DETOUR through one specific
    /// known-hard stretch, not a replacement for normal behaviour either
    /// side of it.
    /// </summary>
    // How many stuck ticks (no real movement despite MovePosition being
    // called) before giving up on a waypoint and skipping to the next one -
    // see StartCollisionWalkRoute's own doc comment. 20 ticks * WalkTick
    // Interval (0.05s) = 1s, matching this project's general "never hang
    // forever" precedent without needing a long wait to notice a genuine
    // block.
    private const int CollisionWalkRouteStuckTicksBeforeSkip = 20;

    /// <summary>
    /// Real collision-respecting waypoint walk (2026-09-01, Lucas's own
    /// explicit choice after live testing: StartGhostRoute's phase-based
    /// movement made a door-route crossing look like an instant teleport
    /// up close, even once the door was confirmed genuinely toggling open
    /// server-side - phasing (zero collision, a direct transform.position
    /// write) never actually depends on collision or the door's own open
    /// state at all, so the door opening was purely cosmetic dressing on a
    /// glide that looked identical whether it opened or not). This walks
    /// the SAME recorded waypoints - a real, once-human-walked, provably
    /// collision-valid path - but via real MovePosition-based collision
    /// instead of a direct position write, so the survivor genuinely walks
    /// through the now-open door rather than sliding through it regardless.
    /// Deliberately does NOT use CalculatePath/native pathing at all - the
    /// whole point of a recorded route is we already HAVE the path, we're
    /// not asking pathfinding to find one (which is exactly what failed for
    /// these off-static-navmesh interior points in the first place, see
    /// ExitHomeIfInside's own doc comment). If real collision blocks
    /// forward progress for a full second straight (the door didn't
    /// actually finish opening in time, a stray prop, whatever), skips
    /// ahead to the next waypoint rather than hanging forever.
    /// </summary>
    private void StartCollisionWalkRoute(Survivor survivor, List<Vector3> waypoints, int index, Action onComplete)
    {
        Guid characterId = survivor.Character.Id;

        if (index >= waypoints.Count)
        {
            onComplete?.Invoke();
            return;
        }

        CancelActiveMovement(survivor);

        Vector3 target = waypoints[index];
        Timer walkTimer = null;
        int stuckTicks = 0;

        walkTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                walkTimer.Destroy();
                _activeMovement.Remove(characterId);
                onComplete?.Invoke();
                return;
            }

            if (npc.IsWounded())
            {
                return;
            }

            // Real, proven door-open mechanism (2026-09-01) - the same one
            // ApplyMovementStep already calls every tick for ordinary
            // hand-built walking, NPCDoorTriggerBox.TryOpenDoorFor via the
            // survivor's own CURRENT position, not Door.SetOpen against a
            // fixed pre-recorded anchor (which is what this crossing used
            // to do, and could easily open a door that's no longer - or
            // never truly was - the one actually in the survivor's real
            // path, matching the live report: "doors are opening/closing,
            // the bot isn't physically and visually next to the door").
            TryOpenNearbyDoors(npc);

            Vector3 current = npc.transform.position;
            Vector3 toTarget = target - current;
            float remaining = toTarget.magnitude;

            if (remaining < WaypointArriveDistance)
            {
                walkTimer.Destroy();
                _activeMovement.Remove(characterId);
                StartCollisionWalkRoute(survivor, waypoints, index + 1, onComplete);
                return;
            }

            float stepDistance = RunSpeed * WalkTickInterval;
            Vector3 next = remaining <= stepDistance ? target : current + toTarget.normalized * stepDistance;

            npc.modelState.sprinting = true;
            npc.modelState.ducked = false;
            npc.modelState.ducking = 0f;
            npc.modelState.waterLevel = npc.WaterFactor();
            npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
            npc.SendModelState(true);

            FaceDirection(npc, toTarget);

            // Real collision - MovePosition only, no direct transform.
            // position write (that write is what makes StartGhostRoute a
            // true collision bypass; omitting it here is the entire point
            // of this function).
            npc.MovePosition(next);
            survivor.Position = npc.transform.position;
            survivor.Character.Position = npc.transform.position;

            if (Vector3.Distance(npc.transform.position, current) < 0.01f)
            {
                stuckTicks++;
            }
            else
            {
                stuckTicks = 0;
            }

            if (stuckTicks >= CollisionWalkRouteStuckTicksBeforeSkip)
            {
                walkTimer.Destroy();
                _activeMovement.Remove(characterId);
                StartCollisionWalkRoute(survivor, waypoints, index + 1, onComplete);
            }
        });

        _activeMovement[characterId] = walkTimer;
    }

    private void StartGhostRoute(Survivor survivor, List<GhostRouteWaypoint> waypoints, int index, Action onComplete)
    {
        Guid characterId = survivor.Character.Id;

        // Real combat takes over entirely, exactly like ContinueLootTask's
        // own combat gate (see its doc comment) - StartCombat's
        // CancelActiveMovement already destroys this route's phase timer
        // the instant a fight starts (it shares _activeMovement with every
        // other movement type), so mid-step this check is mostly a no-op
        // safety net. It matters most for the gap BETWEEN waypoints (the
        // real recorded-pause timer.Once below) - nothing is registered in
        // _activeMovement during that window, so combat starting there
        // can't cancel anything, and without this check the pause's own
        // callback would blindly resume phasing on top of whatever
        // StartCombat/StartFollowing is now doing with this same survivor.
        if (_activeCombat.ContainsKey(characterId))
        {
            return;
        }

        if (index >= waypoints.Count)
        {
            _pendingGhostRouteToResume.Remove(characterId);
            StopGhostRouteLootScan(characterId);
            VerbosePuts($"'{survivor.Character.Alias}' finished the ghost route - resuming normal behaviour.");
            onComplete?.Invoke();
            return;
        }

        // Recorded every time movement toward a waypoint actually begins,
        // not just once at the start of the whole route - see
        // _pendingGhostRouteToResume's own doc comment. Combat killing this
        // method's phase timer from outside (StartCombat's
        // CancelActiveMovement) leaves this pointing at whichever waypoint
        // was actually in progress when that happened.
        _pendingGhostRouteToResume[characterId] = (waypoints, index, onComplete);

        CancelActiveMovement(survivor);

        Vector3 target = waypoints[index].Position;
        Timer phaseTimer = null;

        phaseTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                phaseTimer.Destroy();
                _activeMovement.Remove(characterId);
                return;
            }

            if (npc.IsWounded())
            {
                return;
            }

            Vector3 current = npc.transform.position;
            Vector3 toTarget = target - current;
            float remaining = toTarget.magnitude;

            // Arrival is now checked AFTER stepping (below), using the
            // POST-step position, not before - 2026-08-19, real live bug:
            // "bot is still stuck in a weird jog pace... I can physically
            // outrun the bot." Widening GhostRouteWaypointCollapseDistance
            // to 0.6m (same day, earlier fix this session) turned out not
            // to be enough on its own: the arrival check used to run FIRST,
            // against the position from BEFORE this tick's step, so any
            // tick where the PREVIOUS tick's movement had already closed
            // the gap to under WaypointArriveDistance (0.5m) did ZERO
            // movement of its own - it just ran arrival housekeeping
            // (LootWhateverIsHereNow, TryHandleCardPuzzleInteractions,
            // pause/next-waypoint scheduling) and returned. Since collapsed
            // waypoints sit only just above the 0.6m collapse threshold,
            // ONE real movement step (RunSpeed * WalkTickInterval ~=
            // 0.275m) was consistently enough to cross under the 0.5m
            // threshold, meaning every waypoint cost exactly 2 ticks - 1
            // that moved, 1 that didn't - for a hard 50% speed tax
            // regardless of how far collapse distance gets raised. Moving
            // the check to after the step means every single tick now
            // contributes real movement; arrival is just "did that step
            // land us close enough," never "skip stepping because we're
            // already close."
            float stepDistance = RunSpeed * WalkTickInterval;
            Vector3 next = remaining <= stepDistance ? target : current + toTarget.normalized * stepDistance;

            // Field set matches the native StartFollowing branch exactly
            // (2026-08-16) - combat movement defaults to NATIVE
            // NavMeshAgent-driven movement, not the hand-built
            // ApplyMovementStep fallback an earlier fix here was compared
            // against; native's own order is SendModelState BEFORE
            // FaceDirection, restored here after briefly trying the
            // opposite order. sprinting/ducked/ducking/waterLevel/onground
            // now match native's own field set exactly either way - see
            // this method's own investigation notes for why modelState
            // parity alone has NOT fixed this (ghost route and native are
            // now essentially identical here), meaning the real cause is
            // most likely structural: native's real Unity NavMeshAgent
            // produces genuine continuous velocity every frame, while this
            // (and every hand-built movement type) is a discrete position
            // teleport (MovePosition) with no real velocity component at
            // all - plausibly what Rust's client animator actually keys
            // off, not the modelState flags alone. Not yet confirmed or
            // fixed - needs real decompile access to pin down what field
            // (if any) carries that signal, which isn't available this
            // session.
            npc.modelState.sprinting = true;
            npc.modelState.ducked = false;
            npc.modelState.ducking = 0f;
            npc.modelState.waterLevel = npc.WaterFactor();
            npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
            npc.SendModelState(true);

            FaceDirection(npc, toTarget);

            // No SendNetworkUpdateImmediate - see StartPhasingToDestination's
            // identical fix/doc comment for why plain MovePosition alone is
            // what reads as smooth ordinary walking to observers.
            npc.transform.position = next;
            npc.MovePosition(next);
            survivor.Position = next;
            survivor.Character.Position = next;

            if (Vector3.Distance(next, target) >= WaypointArriveDistance)
            {
                return;
            }

            phaseTimer.Destroy();
            _activeMovement.Remove(characterId);

            // Deliberately NOT SnapToGround here (2026-08-16, second
            // flicker-fix iteration) - it re-queries the same real
            // ground-height probe every single waypoint arrival (dense
            // near doorways, since that's where the recorded trace has
            // many close samples) and overrides Y with whatever THAT
            // computes, independent of the recorded/smoothed trace Y -
            // confirmed live as the actual flicker source, not sample
            // noise (Y-smoothing alone didn't fix it, because this was
            // never about the trace data at all). The whole point of a
            // ghost route is that the real recorded Y IS the ground
            // truth here - it shouldn't get second-guessed by the same
            // probe system this whole session has been fighting.
            LootWhateverIsHereNow(survivor, npc);

            // Fuse/switch/reader interactions - no-op for every normal
            // ghost route (gated entirely behind _activeCardPuzzleSurvivors,
            // see its own doc comment for why - those consume real fuse/
            // card durability, so only a committed puzzle attempt should
            // ever touch them).
            TryHandleCardPuzzleInteractions(survivor, npc);

            // Wheel/button/elevator interactions - called UNCONDITIONALLY
            // on every ghost route, puzzle or plain loot, since none of
            // them cost an item (2026-08-20, Lucas's own explicit spec:
            // Water Treatment Plant's wheel-driven door needs no keycard/
            // fuse at all, and the buttons that let a bot escape a
            // monument if the doors auto-close are "concurrent throughout
            // almost every monument with a card reader," not puzzle-
            // specific; elevators added 2026-08-21 for Nuclear Missile
            // Silo, same "costs nothing" reasoning). Returns true if
            // anything handled here is still actively pending (an
            // in-progress wheel-turn whose door hasn't reached full
            // openProgress yet, or an elevator still IsBusy() moving) -
            // NOT the specific entity itself, since WheelSwitch.
            // rotateProgress turned out to be an uncapped local animation
            // value with no real ceiling (see
            // TryHandleFreeMonumentInteractions' own doc comment for the
            // live bug this fixed, 2026-08-20).
            bool hasPendingInteraction = TryHandleFreeMonumentInteractions(survivor, npc);

            if (hasPendingInteraction)
            {
                // Real live bug (2026-08-20): the normal recorded-dwell
                // pause below is clamped to GhostRoutePauseMaxSeconds (a
                // flat 4s), but a real wheel hold can genuinely take
                // longer (Lucas's own confirmed 8.86s for one specific
                // wheel) - and a bigger hardcoded cap would just be wrong
                // in the other direction for a faster-tuned wheel
                // elsewhere. Poll the door's own real openProgress instead
                // of trusting a duration at all - advance the instant it's
                // actually open, however long that genuinely takes.
                // WheelWaitMaxSeconds is a safety valve, not the real
                // completion signal - guards against a genuinely stuck
                // wheel hanging the route forever rather than falling back
                // to moving on. Registered in _activeMovement so combat
                // starting mid-turn correctly cancels this the same way it
                // cancels the phase timer above.
                //
                // Re-calls TryHandleFreeMonumentInteractions every poll tick
                // and uses ITS FRESH return value, not the stale
                // hasPendingInteraction captured when the wait started -
                // 2026-08-20, real live bug (two parts). First: a bot
                // arriving 2-3m from the wheel started turning, immediately
                // self-cancelled (real 2m distance check), and this loop
                // just watched the same dead, never-retried timer for the
                // full 60s before giving up - re-invoking each tick means
                // WheelSwitchMaxRotateDistance2D's own pre-check
                // (TryStartWheelTurn) gets a fresh chance every 0.5s.
                // Second, worse bug: even after TryStartWheelTurn/
                // _handledWheelsThisPuzzle correctly finished the real 8s
                // hold and gave up for good, this loop kept checking the
                // ORIGINAL captured door's openProgress - which
                // live-confirmed never actually reaches 1 (stayed flat at
                // 0.00 across every real 8s hold, even ones that visibly
                // lifted the door) - so it just sat frozen until the full
                // 60s WheelWaitMaxSeconds safety valve, not the real "the
                // wheel gave up, move on" signal at all. Re-invoking and
                // reassigning currentlyPending each tick means a false
                // return (nothing left pending - handled, destroyed, or
                // never actually started) is treated as done immediately.
                float wheelWaitDeadline = UnityEngine.Time.realtimeSinceStartup + WheelWaitMaxSeconds;
                Timer wheelWaitTimer = null;
                bool currentlyPending = hasPendingInteraction;

                wheelWaitTimer = timer.Every(0.5f, () =>
                {
                    if (survivor.Player == null || survivor.Player.IsDestroyed)
                    {
                        wheelWaitTimer.Destroy();
                        _activeMovement.Remove(characterId);
                        return;
                    }

                    currentlyPending = TryHandleFreeMonumentInteractions(survivor, survivor.Player);

                    bool doneOrStuck = !currentlyPending
                        || UnityEngine.Time.realtimeSinceStartup >= wheelWaitDeadline;

                    if (doneOrStuck)
                    {
                        wheelWaitTimer.Destroy();
                        _activeMovement.Remove(characterId);
                        StartGhostRoute(survivor, waypoints, index + 1, onComplete);
                    }
                });

                _activeMovement[characterId] = wheelWaitTimer;
                return;
            }

            float pauseSeconds = Mathf.Clamp(waypoints[index].DwellSeconds, 0f, GhostRoutePauseMaxSeconds);

            if (pauseSeconds >= GhostRoutePauseMinSeconds)
            {
                VerbosePuts($"ghost-route: '{survivor.Character.Alias}' pausing {pauseSeconds:F1}s at waypoint {index} - matching the real recorded stop here.");

                timer.Once(pauseSeconds, () =>
                {
                    if (survivor.Player == null || survivor.Player.IsDestroyed)
                    {
                        return;
                    }

                    StartGhostRoute(survivor, waypoints, index + 1, onComplete);
                });
            }
            else
            {
                StartGhostRoute(survivor, waypoints, index + 1, onComplete);
            }
        });

        _activeMovement[characterId] = phaseTimer;
    }

    /// <summary>
    /// Resumes an in-progress ghost route after combat interrupted it, if
    /// this survivor has one recorded (see _pendingGhostRouteToResume's own
    /// doc comment) - 2026-08-16, Lucas's own explicit request. Called from
    /// ResumeOrStartLootTask (LivingRust.Recycling.cs) ahead of its normal
    /// fresh-task fallback, same priority pattern that method already gives
    /// a pending recycler.
    ///
    /// Loots whatever's within reach right where combat just ended first
    /// (LootWhateverIsHereNow, which now includes corpses - "if it kills
    /// scientists, it should still loot the bodies, but afterwards, go back
    /// to ghostrouting") - the thing it just killed is almost always right
    /// there. Then walks back (a real, collision-respecting walk via
    /// StartLongDistanceWalk, same as the original approach to the route -
    /// combat can genuinely drag a survivor off the recorded line) to
    /// exactly the waypoint it was heading toward when interrupted, and
    /// resumes phasing through the route from there to completion. Returns
    /// true the instant it commits to a resume attempt - false, doing
    /// nothing, if there's nothing to resume, letting the caller fall
    /// through to its own normal fresh-task start.
    ///
    /// Deliberately does NOT remove the pending entry here (2026-08-16,
    /// real live bug: multiple scientists near the route - the SECOND
    /// fight interrupted this method's own walk-back before it ever
    /// reached StartGhostRoute again, and since the entry had already been
    /// deleted the instant this method committed to the FIRST resume
    /// attempt, the state was gone for good - the survivor wandered off to
    /// an unrelated fresh task instead of ever finishing the route).
    /// ResumeOrStartLootTask only ever calls this once per genuine
    /// disengage event, so there's no real double-fire risk to guard
    /// against - StartGhostRoute itself keeps this entry current (or
    /// clears it on genuine completion/death), so leaving it alone here
    /// means a resume attempt that gets interrupted YET AGAIN just tries
    /// again from the same still-recorded waypoint next time, instead of
    /// silently losing the route.
    /// </summary>
    private bool TryResumeGhostRoute(Survivor survivor)
    {
        Guid characterId = survivor.Character.Id;

        if (!_pendingGhostRouteToResume.TryGetValue(characterId, out var resumeState))
        {
            return false;
        }

        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return false;
        }

        // See _ghostRouteDetourAttempts' own doc comment - past the cap,
        // this stops looting at the fight-end spot and just forces its way
        // straight back instead, no more side-loot rewards for getting
        // dragged further off course.
        int attempts = _ghostRouteDetourAttempts.TryGetValue(characterId, out int previousAttempts) ? previousAttempts + 1 : 1;
        _ghostRouteDetourAttempts[characterId] = attempts;

        bool forcedBack = attempts > MaxGhostRouteDetourAttempts;

        if (forcedBack)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' has been dragged off its ghost route through {attempts - 1} combat/loot detours already - forcing its way straight back now, no more side-loot.");
        }
        else
        {
            LootWhateverIsHereNow(survivor, npc);
        }

        Vector3 resumePosition = resumeState.Waypoints[resumeState.Index].Position;

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' is heading back to its ghost route ({Vector3.Distance(npc.transform.position, resumePosition):F0}m away) after the fight.");
        Puts($"ghostroute: '{survivor.Character.Alias}' resuming its ghost route at waypoint {resumeState.Index} after combat (detour {attempts}/{MaxGhostRouteDetourAttempts}{(forcedBack ? ", forced" : "")}).");

        StartLongDistanceWalk(
            survivor,
            resumePosition,
            "ghost route resume point",
            onArrived: () =>
            {
                // Genuinely back on the route now - a fresh disengagement
                // from here on gets its own clean budget rather than
                // accumulating across unrelated interruptions (see
                // _ghostRouteDetourAttempts' own doc comment).
                _ghostRouteDetourAttempts.Remove(characterId);
                StartGhostRoute(survivor, resumeState.Waypoints, resumeState.Index, resumeState.OnComplete);
            },
            onFailed: () =>
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't get back to its ghost route - starting a fresh search instead.");
                _ghostRouteDetourAttempts.Remove(characterId);
                _pendingGhostRouteToResume.Remove(characterId);
                StopGhostRouteLootScan(characterId);
                StartLootForResourcesTask(survivor);
            });

        return true;
    }

    /// <summary>
    /// Tier 0. Tries each angle in WiggleAngles in turn, spread
    /// over real ticks (not an instant snap), stopping as soon as one
    /// produces real cumulative displacement (WiggleMinProgressDistance) -
    /// the theory being a fragmented-navmesh dead spot is often only a
    /// meter or two wide, so a short step in almost any direction is
    /// enough to clear it.
    /// </summary>
    private void TryWiggleFree(Survivor survivor, Vector3 destination, Action<bool> onComplete)
    {
        TryWiggleDirection(survivor, destination, 0, onComplete);
    }

    /// <summary>
    /// Real toward-destination direction, recomputed fresh each call
    /// (rather than reusing whatever the survivor happened to be facing)
    /// so WiggleAngles' angles are always relative to the actual
    /// destination, not stale facing left over from before this wiggle
    /// episode started.
    /// </summary>
    private static Vector3 GetWiggleBaseDirection(BasePlayer npc, Vector3 destination)
    {
        Vector3 toward = destination - npc.transform.position;
        toward.y = 0f;

        return toward.sqrMagnitude > 0.01f ? toward.normalized : npc.transform.forward;
    }

    private void TryWiggleDirection(Survivor survivor, Vector3 destination, int directionIndex, Action<bool> onComplete)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            onComplete(false);
            return;
        }

        if (directionIndex >= WiggleAngles.Length)
        {
            onComplete(false);
            return;
        }

        Vector3 start = npc.transform.position;
        Vector3 baseDirection = GetWiggleBaseDirection(npc, destination);
        Vector3 worldDirection = (Quaternion.Euler(0f, WiggleAngles[directionIndex], 0f) * baseDirection).normalized;
        Vector3 probeTarget = start + worldDirection * WiggleProbeDistance;
        float stepDistance = WalkSpeed * WalkTickInterval;

        int stepsTaken = 0;
        Timer wiggleTimer = null;

        wiggleTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer currentNpc = survivor.Player;

            if (currentNpc == null || currentNpc.IsDestroyed || stepsTaken >= WiggleMaxStepsPerDirection)
            {
                wiggleTimer.Destroy();
                FinishWiggleDirection(survivor, destination, start, directionIndex, onComplete);
                return;
            }

            // Wounded/downed - pause entirely rather than fight Rust's own
            // incapacitated state machine, same rule as EscalateStuckRecovery
            // and the main walk timer. This tick loop calls ApplyMovementStep
            // directly, so without this check it would keep dragging a
            // downed survivor's transform around every tick.
            if (currentNpc.IsWounded())
            {
                return;
            }

            StepResult step = _engine.NavigationManager.TryGetNextStep(currentNpc.transform.position, probeTarget, stepDistance, out Vector3 nextStep, out _);

            if (step == StepResult.Blocked)
            {
                wiggleTimer.Destroy();
                FinishWiggleDirection(survivor, destination, start, directionIndex, onComplete);
                return;
            }

            ApplyMovementStep(survivor, currentNpc, currentNpc.transform.position, nextStep, step == StepResult.SteppedUp, sprinting: false, stepDistance);
            stepsTaken++;
        });
    }

    private void FinishWiggleDirection(Survivor survivor, Vector3 destination, Vector3 start, int directionIndex, Action<bool> onComplete)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            onComplete(false);
            return;
        }

        ReleaseMovementState(npc);

        if (Vector3.Distance(start, npc.transform.position) >= WiggleMinProgressDistance)
        {
            onComplete(true);
            return;
        }

        int nextDirectionIndex = directionIndex + 1;

        if (nextDirectionIndex >= WiggleAngles.Length)
        {
            onComplete(false);
            return;
        }

        // See StuckReassessPause's own doc comment - a genuinely stuck
        // survivor (every direction blocked on its very first probe tick)
        // could otherwise blow through all six wiggle angles in a
        // fraction of a second. Turning to actually face the next
        // direction before trying it reads as reconsidering, not
        // flickering.
        Vector3 nextWorldDirection = (Quaternion.Euler(0f, WiggleAngles[nextDirectionIndex], 0f) * GetWiggleBaseDirection(npc, destination)).normalized;
        FaceDirection(npc, nextWorldDirection);

        timer.Once(StuckReassessPause, () => TryWiggleDirection(survivor, destination, nextDirectionIndex, onComplete));
    }

    /// <summary>
    /// Tier 1. Finds the nearest point actually on Rust's baked navmesh
    /// (not just anywhere - the real, walkable, connected surface),
    /// trying a growing radius in case the survivor is standing well
    /// clear of the nearest real navmesh polygon.
    /// </summary>
    private bool TryFindNavMeshNudgePoint(Vector3 origin, out Vector3 navMeshPoint)
    {
        foreach (float radius in NavMeshNudgeRadii)
        {
            if (NavMesh.SamplePosition(origin, out NavMeshHit hit, radius, NavMesh.AllAreas))
            {
                navMeshPoint = hit.position;
                return true;
            }
        }

        navMeshPoint = default;
        return false;
    }

    /// <summary>
    /// Tier 2, deliberately last resort. Tries Survivor.LastKnownGoodPosition
    /// first - a live report showed the old random-direction-only version
    /// could relocate a survivor into an equally bad new spot (e.g. a
    /// tight trash-bag/AC-unit corner) instead of anywhere better, since a
    /// random guess has no idea whether the landing spot is actually any
    /// good. A position the survivor has already really stood on and
    /// walked away from is a much safer bet than a blind guess. Falls back
    /// to the original random-direction search (unchanged) if that known-
    /// good position isn't set, is too close to bother with, or itself
    /// fails validation (something could have changed - a moved player,
    /// new debris). Every candidate, known-good or random, goes through
    /// the same TryValidateEmergencyTeleportCandidate check - real ground
    /// beneath it (TryFindGroundBelow) and a clear landing area (no solid
    /// geometry, no other player) - so this can never relocate a survivor
    /// into a spot it hasn't actually verified. Unlike ApplyMovementStep's
    /// step-by-step movement, this is a deliberate one-shot relocation
    /// (matching how SpawnSurvivor/RespawnSurvivor already position a
    /// fresh BasePlayer directly) - not something that should trip the
    /// "moved too far in one tick" anomaly warning that's meant to catch
    /// bugs in ordinary stepped movement, not intentional repositioning.
    /// </summary>
    private bool TryEmergencyTeleport(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (survivor.LastKnownGoodPosition.HasValue)
        {
            Vector3 knownGood = survivor.LastKnownGoodPosition.Value;

            if (Vector3.Distance(knownGood, npc.transform.position) >= EmergencyTeleportMinDistance
                && TryValidateEmergencyTeleportCandidate(npc, knownGood, out Vector3 validatedKnownGood))
            {
                npc.transform.position = validatedKnownGood;
                npc.MovePosition(validatedKnownGood);
                npc.SendNetworkUpdateImmediate();

                return true;
            }
        }

        for (int attempt = 0; attempt < EmergencyTeleportAttempts; attempt++)
        {
            float angle = UnityEngine.Random.Range(0f, 360f);
            float distance = UnityEngine.Random.Range(EmergencyTeleportMinDistance, EmergencyTeleportMaxDistance);
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            Vector3 candidate = npc.transform.position + direction * distance;

            if (!TryValidateEmergencyTeleportCandidate(npc, candidate, out Vector3 validated))
            {
                continue;
            }

            npc.transform.position = validated;
            npc.MovePosition(validated);
            npc.SendNetworkUpdateImmediate();

            return true;
        }

        return false;
    }

    // Live report + a live /lr.debug.scan cross-reference (Lucas, 2026-08-11):
    // SlyWanderer ended up standing on an unreachable warehouse rooftop with
    // no stairs/ladder anywhere nearby - the scan confirmed NavMesh.
    // SamplePosition found nothing within 3m of that exact spot, only
    // picking up a real point 5.9m away, meaning the roof carries its own
    // disconnected "island" of baked navmesh with no real walkable
    // connection to the ground. TryValidateEmergencyTeleportCandidate only
    // ever checked "is there solid ground below" and "is the landing clear" -
    // never whether the candidate was actually reachable on foot from where
    // the survivor currently stands, so a random probe landing near that
    // roof island passed both checks and got teleported straight up there.
    // Two guards close this, per Lucas's own proposed fix: (1) a cheap
    // elevation-delta gate first - if a candidate's real ground height
    // differs from the survivor's current height by more than
    // EmergencyTeleportMaxElevationChange, it's rejected outright before
    // ever touching pathfinding, since roofs/upper floors are almost always
    // a big elevation jump from ground level; (2) TryCalculatePath - the
    // real pathing engine, same "don't trust proximity alone" fix already
    // used for the stuck-recovery chain's navmesh-nudge tier - confirms a
    // genuine walkable route exists from the survivor's current position to
    // the candidate before ever relocating it there.
    private const float EmergencyTeleportMaxElevationChange = 3f;

    // maxElevationChange/groundProbeHeight/groundProbeDistance all default
    // to the original tight-radius (3-5m) TryEmergencyTeleport numbers -
    // see TryForceRelocateIgnoringCollision's own doc comment
    // (LivingRust.Commands.cs) for why its own much wider 30-80m search
    // passes larger values here instead: real terrain naturally varies
    // more than 3m of elevation over that distance even on completely
    // ordinary, walkable ground, so reusing the tight default would reject
    // most otherwise-legitimate distant candidates outright. The real
    // safety guarantee either way is TryCalculatePath below, not this
    // elevation gate - it's a cheap pre-filter, not the thing actually
    // proving reachability.
    //
    // requireRealPath (2026-09-07, real regression fix - Lucas's own live
    // report: 270 distinct bots stuck in an unresolved rescue loop, ZERO
    // successful force-relocations, after TryForceRelocateIgnoringCollision
    // started reusing this function's own TryCalculatePath check). That
    // check is exactly right for TryEmergencyTeleport's own short-range use
    // (a survivor that's otherwise navigating fine, just needs a small
    // nudge) - it's actively self-defeating for the wide-search last-resort
    // fallback specifically, since a survivor only ever reaches THAT
    // fallback because it's already stuck somewhere real pathfinding
    // doesn't work. Demanding a real calculated path FROM that same broken
    // origin before ever relocating it is close to guaranteeing failure -
    // if a real path from there worked, it wouldn't be stuck in the first
    // place. Defaults to true (TryEmergencyTeleport's own call is
    // unaffected) - TryForceRelocateIgnoringCollision passes false instead,
    // keeping the ground/elevation/obstruction checks (still real, still
    // what stops it landing on a rock formation's summit) while dropping
    // just the one check that made the whole fallback nearly always fail.
    private bool TryValidateEmergencyTeleportCandidate(BasePlayer npc, Vector3 candidate, out Vector3 validated, float maxElevationChange = EmergencyTeleportMaxElevationChange, float groundProbeHeight = 4f, float groundProbeDistance = 6f, bool requireRealPath = true)
    {
        return TryValidateEmergencyTeleportCandidate(npc, candidate, out validated, out _, maxElevationChange, groundProbeHeight, groundProbeDistance, requireRealPath);
    }

    // Real diagnostic overload (2026-09-07, live report: 270 distinct bots
    // stuck with the wide-search fallback failing 100% of the time even
    // after dropping the connectivity requirement - failureReason exists
    // to find out WHICH of the three remaining checks is actually the
    // culprit instead of guessing through further blind deploy cycles).
    private bool TryValidateEmergencyTeleportCandidate(BasePlayer npc, Vector3 candidate, out Vector3 validated, out string failureReason, float maxElevationChange = EmergencyTeleportMaxElevationChange, float groundProbeHeight = 4f, float groundProbeDistance = 6f, bool requireRealPath = true)
    {
        validated = candidate;

        // Real fix (2026-09-21, live log audit: 287 of 291 stall-rescue
        // wide-search failures were "no real ground found"). The probe
        // itself already lifts its rays ProbeStartHeight (4m) above whatever
        // origin it's given and only reaches ProbeSearchDistance (6m) down -
        // lifting the candidate by groundProbeHeight (4m) on top of that
        // started rays 8m above the candidate and stopped them 2m ABOVE it,
        // so open ground could never be hit (only tall props, which is why
        // this "worked" on rock summits). Passing the candidate as-is puts
        // the surface inside the ray's reach (start +4m, end -2m).
        if (!_engine.NavigationManager.TryFindGroundBelow(candidate, groundProbeHeight, groundProbeDistance, out float groundY, out _))
        {
            failureReason = "no real ground found within the probe window";
            return false;
        }

        float elevationDelta = Mathf.Abs(groundY - npc.transform.position.y);

        if (elevationDelta > maxElevationChange)
        {
            failureReason = $"elevation delta {elevationDelta:F1}m > {maxElevationChange:F1}m cap";
            return false;
        }

        validated.y = groundY;

        bool obstructed = Physics.CheckSphere(validated + Vector3.up * 0.9f, 0.4f, LineOfSightBlockingMask, QueryTriggerInteraction.Ignore)
            || IsBlockedByOtherPlayer(validated, npc, exempt: null);

        if (obstructed)
        {
            failureReason = "candidate position obstructed";
            return false;
        }

        if (requireRealPath && !_engine.NavigationManager.TryCalculatePath(npc.transform.position, validated, new RustNavMeshPath(), out _))
        {
            failureReason = "no real calculated path from current position";
            return false;
        }

        failureReason = null;
        return true;
    }
}
