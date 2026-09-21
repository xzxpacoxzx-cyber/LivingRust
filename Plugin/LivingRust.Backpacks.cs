using System.Linq;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Backpacks (2026-09-21, Lucas's own spec + screenshot): a backpack is NOT
    // clothing - it goes in its own dedicated slot (the wear container at
    // ItemContainer.BackpackSlotIndex) and opens extra slots (28 for the
    // biggest). Found in a crate it can be equipped instantly; only one
    // dropped by a dead/downed player is a timed (3-5s) world pickup. Bots
    // now equip the best backpack they own, upgrade small -> large, treat one
    // as a top-tier pickup, use its extra room, and take the same 3-5s to
    // pick one up off the ground.
    private static bool IsBackpackItem(Item item)
    {
        return item?.info != null && item.info.GetComponent<ItemModBackpack>() != null;
    }

    private static int GetBackpackCapacity(Item backpack)
    {
        return backpack?.contents?.capacity ?? 0;
    }

    private static Item GetWornBackpack(BasePlayer npc)
    {
        Item slotItem = npc.inventory.containerWear.GetSlot(ItemContainer.BackpackSlotIndex);
        return slotItem != null && IsBackpackItem(slotItem) ? slotItem : null;
    }

    private void EquipBestBackpack(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        Item worn = GetWornBackpack(npc);
        int wornCapacity = GetBackpackCapacity(worn);

        Item best = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(IsBackpackItem)
            .OrderByDescending(GetBackpackCapacity)
            .FirstOrDefault();

        if (best == null || GetBackpackCapacity(best) <= wornCapacity)
        {
            return;
        }

        if (worn != null && !worn.MoveToContainer(npc.inventory.containerMain))
        {
            return;
        }

        if (best.MoveToContainer(npc.inventory.containerWear, ItemContainer.BackpackSlotIndex))
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' put on a '{best.info.shortname}' ({GetBackpackCapacity(best)} slots).");
        }
    }

    // Free room in the worn backpack, if any (used as overflow after the main
    // inventory fills, and so "inventory full" doesn't fire while there's
    // still backpack space left).
    private static bool HasBackpackRoom(BasePlayer npc)
    {
        Item worn = GetWornBackpack(npc);

        return worn?.contents != null && worn.contents.itemList.Count < worn.contents.capacity;
    }

    private static bool IsMainInventoryFullIncludingBackpack(BasePlayer npc)
    {
        return npc.inventory.containerMain.itemList.Count >= npc.inventory.containerMain.capacity && !HasBackpackRoom(npc);
    }

    // What belongs in the backpack rather than the main slots (Lucas's own
    // spec): bulk raw goods (wood, stone, ores, sulfur, metal fragments,
    // components, scrap), spare weapons (anything not better than the best
    // weapon already owned) and spare clothing/armour (anything that isn't an
    // upgrade over what's worn). Ammo, medical, tools, keycards, the active
    // kit stay in main/belt. Weapons and armour that WOULD be an upgrade go
    // to main so EquipBest*/EvaluateAndUpgradeArmor (which only scan
    // main/belt) can see and equip them.
    private bool ShouldPreferBackpack(BasePlayer npc, Item item)
    {
        if (IsBackpackItem(item))
        {
            return false;
        }

        LootPriorityTier tier = GetLootPriorityTier(item);

        switch (tier)
        {
            case LootPriorityTier.ScrapMetal:
            case LootPriorityTier.Sulfur:
            case LootPriorityTier.MetalOreOrFragments:
            case LootPriorityTier.WoodOrStone:
            case LootPriorityTier.Components:
                return true;

            case LootPriorityTier.Weapon:
            {
                int candidateRank = System.Array.IndexOf(WeaponPriority, item.info.shortname);
                Item bestOwned = FindBestByPriority(
                    npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList).ToList(),
                    WeaponPriority,
                    exclude: null);

                if (bestOwned == null)
                {
                    return false;
                }

                int ownedRank = System.Array.IndexOf(WeaponPriority, bestOwned.info.shortname);

                // Lower index = better. Unknown weapons (-1) aren't upgrades.
                return candidateRank < 0 || (ownedRank >= 0 && candidateRank >= ownedRank);
            }

            case LootPriorityTier.Armor:
            {
                ItemModWearable wearable = item.info.GetComponent<ItemModWearable>();

                if (wearable == null)
                {
                    return false;
                }

                float candidateScore = GetArmorProtectionScore(item);
                var conflicting = npc.inventory.containerWear.itemList
                    .Where(worn => worn.info.GetComponent<ItemModWearable>() is ItemModWearable w && !wearable.CanExistWith(w))
                    .ToList();

                return conflicting.Count > 0 && candidateScore < conflicting.Sum(GetArmorProtectionScore) + ArmorUpgradeMinimumScoreGain;
            }

            default:
                return false;
        }
    }
}
