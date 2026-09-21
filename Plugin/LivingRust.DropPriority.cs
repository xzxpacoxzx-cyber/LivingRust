using System.Linq;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Value-aware drop order (2026-09-21, Lucas's own spec). Higher rank =
    // dropped sooner. Starts from the loot-priority tier, then adjusts:
    //  - bandages are cheap to make, so they go first (medical syringes and
    //    large medkits stay protected as Medical);
    //  - a survivor with a ready firearm has no use for a bow or arrows;
    //  - a stack at or under 30% of its maximum (300 of 1000 stones, 30
    //    pistol rounds, a handful of components) is worth less than any
    //    weapon, clothing or armour piece, so it ranks below them.
    private const float SmallStackFraction = 0.3f;

    private int GetDropRank(BasePlayer npc, Item item)
    {
        string shortname = item.info.shortname;
        LootPriorityTier tier = GetLootPriorityTier(item);

        if (shortname == "bandage")
        {
            return 12;
        }

        if (tier == LootPriorityTier.Medical)
        {
            return (int)tier;
        }

        if ((System.Array.IndexOf(NonCombatCapableRangedWeaponShortnames, shortname) >= 0 || shortname.StartsWith("arrow.", System.StringComparison.Ordinal))
            && HasReadyFirearm(npc))
        {
            return 13;
        }

        int rank = (int)tier;

        bool smallStackTier = tier == LootPriorityTier.AmmoOrExplosive
            || tier == LootPriorityTier.ScrapMetal
            || tier == LootPriorityTier.Sulfur
            || tier == LootPriorityTier.MetalOreOrFragments
            || tier == LootPriorityTier.WoodOrStone
            || tier == LootPriorityTier.Components;

        if (smallStackTier && item.info.stackable > 1 && item.amount <= item.info.stackable * SmallStackFraction)
        {
            rank = System.Math.Max(rank, 11);
        }

        return rank;
    }

    // A survivor that holds a ready firearm sheds its bow and arrows
    // outright rather than waiting for inventory pressure.
    private void DropBowKitIfArmed(Survivor survivor, BasePlayer npc)
    {
        if (!HasReadyFirearm(npc))
        {
            return;
        }

        foreach (Item item in npc.inventory.containerMain.itemList
                     .Concat(npc.inventory.containerBelt.itemList)
                     .Where(i => System.Array.IndexOf(NonCombatCapableRangedWeaponShortnames, i.info.shortname) >= 0
                         || i.info.shortname.StartsWith("arrow.", System.StringComparison.Ordinal))
                     .ToList())
        {
            item.Drop(npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f, npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f);
        }
    }

    // A weapon is usable now if it isn't a firearm, or it's a firearm with
    // rounds in the magazine or matching ammo somewhere in the inventory.
    private static bool IsWeaponUsableNow(BasePlayer npc, Item item)
    {
        if (item.GetHeldEntity() is not BaseProjectile projectile)
        {
            return true;
        }

        if (projectile.primaryMagazine == null)
        {
            return true;
        }

        if (projectile.primaryMagazine.contents > 0)
        {
            return true;
        }

        ItemDefinition ammo = projectile.primaryMagazine.ammoType;

        return ammo != null && npc.inventory.GetAmount(ammo.itemid) > 0;
    }
}
