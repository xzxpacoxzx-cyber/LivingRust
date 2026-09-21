using System.Collections.Generic;
using LivingRust.Models;
using System.Linq;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // High-gear resource discipline (2026-09-21, Lucas's own explicit spec): a
    // survivor above HighGearResourceAvoidScore has better things to do
    // (airdrops, fights, loot from other players) than farming. It avoids
    // collectables and farm nodes entirely UNLESS (A) it already has a base
    // AND (B) it already carries that specific item (so it keeps topping up
    // cloth if it already has cloth, but won't start collecting sulfur ore,
    // metal ore, HQM ore, stones or wood it isn't already holding).
    private const int HighGearResourceAvoidScore = 35;

    private bool IsResourceAllowedForHighGear(Survivor survivor, BasePlayer npc, IEnumerable<ItemAmount> yields)
    {
        if (GetGearScore(npc) <= HighGearResourceAvoidScore)
        {
            return true;
        }

        if (survivor.Character.Home == null || yields == null)
        {
            return false;
        }

        return yields.Any(y => y?.itemDef != null && npc.inventory.GetAmount(y.itemDef.itemid) > 0);
    }

    private static bool HasAnyRawResourceInInventory(BasePlayer npc)
    {
        foreach (string shortname in new[] { "wood", "stones", "metal.ore", "sulfur.ore", "hq.metal.ore" })
        {
            ItemDefinition def = ItemManager.FindItemDefinition(shortname);

            if (def != null && npc.inventory.GetAmount(def.itemid) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<ItemAmount> WoodYieldForGate()
    {
        ItemDefinition wood = ItemManager.FindItemDefinition("wood");
        return wood == null ? null : new[] { new ItemAmount(wood, 1f) };
    }

    private static IEnumerable<ItemAmount> GetNodeYields(ResourceEntity node)
    {
        return node?.resourceDispenser?.containedItems;
    }
}
