using System.Collections.Generic;
using LivingRust.Models;
using System.Linq;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // High-gear resource discipline: a survivor above HighGearResourceAvoidScore has
    // better things to do than farming. It avoids collectables and farm nodes
    // entirely unless it already has a base and already carries that specific item.
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
        // ResourceEntity.resourceDispenser became protected in Rust's October 2026
        // update; GetComponent<ResourceDispenser>() is the same lookup the field's own
        // initializer uses internally (confirmed via decompile), so it's a direct,
        // non-reflection replacement.
        return node?.GetComponent<ResourceDispenser>()?.containedItems;
    }
}
