using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Random home-made outfits (2026-10-04, Lucas's spec): a survivor makes clothing out of whatever it has the
/// materials for - cloth, burlap, hide (leather), wood armour, bone armour - instead of the same ranked
/// piece every time, and keeps upgrading whatever it wears. Each survivor rolls a favourite style per life
/// (so a bot reads as a "bone guy" or a "hide guy") but still mixes pieces, so the population looks varied.
/// Works at the base workshop (boxes included) and out in the field (carried materials only).
/// </summary>
public partial class LivingRust
{
    private const string OutfitJobReason = "outfit piece";

    // Rank decides what counts as an upgrade: a piece is only made when it outranks everything it would
    // replace. Wood/bone sit in ArmorTier.Wood, the rest in Basic, so rank is tier * 10 + style.
    private static readonly (string Shortname, string Style, int Rank)[] OutfitCatalogue =
    {
        // Plain cloth.
        ("hat.cap", "cloth", 1), ("hat.boonie", "cloth", 1), ("hat.beenie", "cloth", 1), ("mask.bandana", "cloth", 1),
        ("shirt.tanktop", "cloth", 1), ("pants.shorts", "cloth", 1),

        // Burlap (cloth).
        ("burlap.headwrap", "burlap", 2), ("burlap.shirt", "burlap", 2), ("burlap.trousers", "burlap", 2),
        ("burlap.shoes", "burlap", 2), ("burlap.gloves.new", "burlap", 2),

        // Hide (leather).
        ("attire.hide.boots", "hide", 3), ("attire.hide.pants", "hide", 3), ("attire.hide.skirt", "hide", 3),
        ("attire.hide.vest", "hide", 3), ("attire.hide.helterneck", "hide", 3), ("attire.hide.poncho", "hide", 3),
        ("burlap.gloves", "hide", 3),

        // Wood armour (cloth + wood).
        ("wood.armor.helmet", "wood", 10), ("wood.armor.jacket", "wood", 10), ("wood.armor.pants", "wood", 10),
        ("woodarmor.gloves", "wood", 10),

        // Bone armour (cloth + bone fragments).
        ("bone.armor.suit", "bone", 10), ("deer.skull.mask", "bone", 10),
    };

    private static readonly string[] OutfitStyles = { "cloth", "burlap", "hide", "wood", "bone" };

    // A survivor's favourite style this life; cleared on death.
    private readonly Dictionary<Guid, string> _outfitStyle = new();

    private const float OutfitFavouriteStyleWeight = 4f;
    private const int OutfitWoodArmourStockReserve = 1000;
    private const int OutfitFieldClothReserve = 10;

    private static int GetWornOutfitRank(Item worn)
    {
        foreach ((string shortname, string _, int rank) in OutfitCatalogue)
        {
            if (shortname == worn.info.shortname)
            {
                return rank;
            }
        }

        // Anything else (looted or bench-made) is ranked by its armour tier; ordinary clothes sit above hide.
        ArmorTier tier = GetArmorTier(worn.info.shortname);
        return (int)tier * 10 + (tier == ArmorTier.Basic ? 4 : 0);
    }

    private static bool OutfitUsesIngredient(ItemDefinition def, string ingredientShortname)
    {
        return def.Blueprint.GetIngredients().Any(i => i?.itemDef != null && i.itemDef.shortname == ingredientShortname);
    }

    /// <summary>
    /// Picks one piece to make, at random, from every garment it can afford that would be a strict upgrade
    /// on what it wears (or fills an empty slot). Pieces of the survivor's favourite style are weighted up.
    /// </summary>
    private string PickOutfitPiece(Survivor survivor, BasePlayer npc, int workbenchLevel, Func<ItemDefinition, bool> canAfford, HashSet<string> failed)
    {
        Guid id = survivor.Character.Id;

        if (!_outfitStyle.TryGetValue(id, out string favourite))
        {
            favourite = OutfitStyles[UnityEngine.Random.Range(0, OutfitStyles.Length)];
            _outfitStyle[id] = favourite;
        }

        List<(string Shortname, float Weight)> options = new();

        foreach ((string shortname, string style, int rank) in OutfitCatalogue)
        {
            if (failed != null && failed.Contains(shortname))
            {
                continue;
            }

            ItemDefinition def = ItemManager.FindItemDefinition(shortname);
            ItemModWearable wearable = def?.GetComponent<ItemModWearable>();

            if (def?.Blueprint == null || wearable == null || !def.Blueprint.userCraftable || def.Blueprint.workbenchLevelRequired > workbenchLevel)
            {
                continue;
            }

            // Already holding one (crafted or looted, just not worn yet): wearing it is the sweep's job.
            if (npc.inventory.GetAmount(def.itemid) > 0)
            {
                continue;
            }

            bool upgrade = true;

            foreach (Item worn in npc.inventory.containerWear.itemList)
            {
                ItemModWearable wornWearable = worn.info.GetComponent<ItemModWearable>();

                if (wornWearable == null || wearable.CanExistWith(wornWearable))
                {
                    continue;
                }

                if (GetWornOutfitRank(worn) >= rank)
                {
                    upgrade = false;
                    break;
                }
            }

            if (upgrade && canAfford(def))
            {
                options.Add((shortname, style == favourite ? OutfitFavouriteStyleWeight : 1f));
            }
        }

        if (options.Count == 0)
        {
            return null;
        }

        float roll = UnityEngine.Random.Range(0f, options.Sum(o => o.Weight));

        foreach ((string shortname, float weight) in options)
        {
            if ((roll -= weight) <= 0f)
            {
                return shortname;
            }
        }

        return options[options.Count - 1].Shortname;
    }

    /// <summary>
    /// Puts on every catalogue garment sitting in the inventory that outranks what it would replace. The
    /// displaced clothing is banked or dropped rather than carried around.
    /// </summary>
    private void WearOutfitFromInventory(BasePlayer npc)
    {
        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        foreach (Item carried in npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList).ToList())
        {
            int rank = OutfitCatalogue.Where(c => c.Shortname == carried.info.shortname).Select(c => c.Rank).FirstOrDefault();
            ItemModWearable wearable = carried.info.GetComponent<ItemModWearable>();

            if (rank == 0 || wearable == null)
            {
                continue;
            }

            List<Item> conflicting = npc.inventory.containerWear.itemList
                .Where(w => w.info.GetComponent<ItemModWearable>() is ItemModWearable ww && !wearable.CanExistWith(ww))
                .ToList();

            if (conflicting.Any(w => GetWornOutfitRank(w) >= rank))
            {
                continue;
            }

            foreach (Item displaced in conflicting)
            {
                DisposeOfSpareClothing(npc, displaced);
            }

            carried.MoveToContainer(npc.inventory.containerWear);
        }
    }

    /// <summary>
    /// A spare garment: banked in a storage box when the survivor is at its base (and one has room), dropped on the
    /// ground when it is out in the field (2026-10-04, Lucas's spec).
    /// </summary>
    private void DisposeOfSpareClothing(BasePlayer npc, Item item)
    {
        if (npc == null || npc.IsDestroyed || item == null)
        {
            return;
        }

        HomeBase home = FindSurvivorByPlayer(npc)?.Character.Home;

        if (home != null && Vector3.Distance(npc.transform.position, home.Position) <= HomeStorageSearchRadius)
        {
            foreach (StorageContainer box in FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius))
            {
                if (box != null && !box.IsDestroyed && box.inventory != null && item.MoveToContainer(box.inventory))
                {
                    return;
                }
            }
        }

        item.Drop(npc.transform.position + Vector3.up + npc.eyes.BodyForward() * 0.5f, npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f);
    }

    /// <summary>
    /// Puts a freshly crafted garment straight onto the survivor when it outranks everything it would replace
    /// (displaced cheap clothing is destroyed). False when it is not an upgrade - the caller then keeps it in the
    /// inventory for the normal armour evaluation.
    /// </summary>
    private bool TryWearCraftedGarment(BasePlayer npc, Item crafted)
    {
        ItemModWearable wearable = crafted.info.GetComponent<ItemModWearable>();

        if (wearable == null)
        {
            return false;
        }

        int rank = OutfitCatalogue.Where(c => c.Shortname == crafted.info.shortname).Select(c => c.Rank).FirstOrDefault();

        if (rank == 0)
        {
            ArmorTier tier = GetArmorTier(crafted.info.shortname);
            rank = (int)tier * 10 + (tier == ArmorTier.Basic ? 4 : 0);
        }

        List<Item> conflicting = npc.inventory.containerWear.itemList
            .Where(w => w.info.GetComponent<ItemModWearable>() is ItemModWearable ww && !wearable.CanExistWith(ww))
            .ToList();

        if (conflicting.Any(w => GetWornOutfitRank(w) >= rank || !IsLowTierArmor(w.info.shortname)))
        {
            return false;
        }

        foreach (Item displaced in conflicting)
        {
            DisposeOfSpareClothing(npc, displaced);
        }

        return crafted.MoveToContainer(npc.inventory.containerWear);
    }

    /// <summary>
    /// Field crafting for a survivor away from its workbench: only garments with no bench requirement and
    /// only from carried materials - it never goes gathering for them. Wood armour is left to survivors with
    /// a base (it would eat the wood a base build needs); cloth keeps a small reserve for bandages.
    /// </summary>
    private bool TryPursueOutfitCraft(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        ItemCrafter crafter = npc.inventory.crafting;

        if (crafter == null || crafter.queue.Count > 0)
        {
            return false;
        }

        bool hasBase = survivor.Character.Home != null;

        string pick = PickOutfitPiece(survivor, npc, 0, def =>
        {
            if (!hasBase && OutfitUsesIngredient(def, WoodShortname))
            {
                return false;
            }

            return def.Blueprint.GetIngredients().All(i =>
            {
                if (i?.itemDef == null)
                {
                    return true;
                }

                int reserve = i.itemDef.shortname == "cloth" ? OutfitFieldClothReserve : 0;
                return npc.inventory.GetAmount(i.itemid) >= (int)i.amount + reserve;
            });
        }, null);

        if (pick == null)
        {
            return false;
        }

        ItemDefinition pickDef = ItemManager.FindItemDefinition(pick);

        if (!npc.blueprints.IsUnlocked(pickDef))
        {
            FreeUnlock(npc, pickDef, "random outfit piece", survivor.Character.Alias);
        }

        if (!npc.blueprints.IsUnlocked(pickDef) || !crafter.CanCraft(pickDef.Blueprint, 1, free: false))
        {
            return false;
        }

        crafter.CraftItem(pickDef.Blueprint, npc, amount: 1);
        Puts($"outfit: '{survivor.Character.Alias}' is making '{pick}' from what it is carrying (favours {_outfitStyle.GetValueOrDefault(survivor.Character.Id)}).");
        WaitForOneOffCraftThenContinue(survivor, state, (survivor.Character.Id, pick));
        ContinueLootTask(survivor, state, forceLocalScan: true);
        return true;
    }
}
