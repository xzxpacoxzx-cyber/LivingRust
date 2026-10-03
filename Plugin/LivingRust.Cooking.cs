using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Campfire cooking at the base (2026-10-04, Lucas's spec): a survivor that harvested raw meat keeps it,
/// puts a campfire inside its base (crafted for 100 wood by the workshop if it has none), loads it with
/// 100 wood and the meat, waits a minute for it to cook, then eats the cooked meat until its hunger is
/// full and banks the rest in storage.
/// </summary>
public partial class LivingRust
{
    private const string CampfireShortname = "campfire";
    private const int CampfireWoodFill = 100;
    private const float CookWaitSeconds = 60f;
    private static readonly Vector3 CampfireHalfExtents = new(0.45f, 0.25f, 0.45f);

    /// <summary>
    /// Raw cookable meat (and fish): a food item that becomes something else when cooked. Human meat is
    /// never kept. These are NOT eaten raw on pickup - they are carried home to be cooked.
    /// </summary>
    private static bool IsRawMeat(Item item)
    {
        return item?.info != null
            && item.info.category == ItemCategory.Food
            && item.info.shortname != "humanmeat.raw"
            && item.info.GetComponent<ItemModCookable>()?.becomeOnCooked != null;
    }

    private BaseOven FindOwnedCampfire(BasePlayer npc, Vector3 origin, float radius)
    {
        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is BaseOven oven && !oven.IsDestroyed && oven.ShortPrefabName == CampfireShortname
                && oven.OwnerID == npc.userID && Vector3.Distance(origin, oven.transform.position) <= radius)
            {
                return oven;
            }
        }

        return null;
    }

    private BaseOven PlaceCampfireAtBase(Survivor survivor, BasePlayer npc, Item campfireItem)
    {
        ItemModDeployable deployable = campfireItem.info.GetComponent<ItemModDeployable>();

        if (deployable == null
            || !TryFindFreeStorageSpot(survivor, npc, out Vector3 position, out Vector3 facing, CampfireHalfExtents)
            || IsInsideMonumentNoBuildZone(position, out string _))
        {
            return null;
        }

        BaseEntity entity = GameManager.server.CreateEntity(deployable.entityPrefab.resourcePath, position, Quaternion.LookRotation(facing));

        if (entity is not BaseOven oven)
        {
            entity?.Kill();
            return null;
        }

        oven.OwnerID = npc.userID;
        oven.Spawn();
        campfireItem.UseItem(1);

        Puts($"cooking: '{survivor.Character.Alias}' placed a campfire inside its base at {position}.");
        return oven;
    }

    /// <summary>
    /// Part of the base trip. A campfire that already holds finished food from an earlier trip is emptied
    /// the same way. Always calls onComplete.
    /// </summary>
    private void TryCookMeatAtBase(Survivor survivor, Action onComplete)
    {
        BasePlayer npc = survivor.Player;
        HomeBase home = survivor.Character.Home;

        if (npc == null || npc.IsDestroyed || home == null)
        {
            onComplete?.Invoke();
            return;
        }

        List<Item> rawMeat = npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList).Where(IsRawMeat).ToList();
        BaseOven campfire = FindOwnedCampfire(npc, home.Position, HomeStorageSearchRadius);
        bool leftovers = campfire != null && campfire.inventory != null && campfire.inventory.itemList.Any(i => i.info.category == ItemCategory.Food);

        if (rawMeat.Count == 0 && !leftovers)
        {
            onComplete?.Invoke();
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);

        if (campfire == null)
        {
            ItemDefinition campfireDef = ItemManager.FindItemDefinition(CampfireShortname);
            Item campfireItem = campfireDef != null ? npc.inventory.FindItemByItemID(campfireDef.itemid) : null;

            if (campfireItem == null && campfireDef != null && WithdrawUpToAmount(boxes, CampfireShortname, 1, npc.inventory.containerMain) > 0)
            {
                campfireItem = npc.inventory.FindItemByItemID(campfireDef.itemid);
            }

            campfire = campfireItem != null ? PlaceCampfireAtBase(survivor, npc, campfireItem) : null;

            if (campfire == null)
            {
                VerbosePuts($"cooking: '{survivor.Character.Alias}' is carrying raw meat but has no campfire at its base (and couldn't place one) - keeping the meat for a later trip.");
                onComplete?.Invoke();
                return;
            }
        }

        Vector3 away = npc.transform.position - campfire.transform.position;
        away.y = 0f;

        if (away.sqrMagnitude < 0.01f)
        {
            away = npc.transform.forward;
        }

        Vector3 standPoint = campfire.transform.position + away.normalized * 1.2f;
        standPoint.y = campfire.transform.position.y;

        BaseOven oven = campfire;

        StartPhasingToDestination(
            survivor,
            standPoint,
            onArrived: () => LoadCampfireAndWait(survivor, oven, boxes, onComplete),
            onFailed: () => LoadCampfireAndWait(survivor, oven, boxes, onComplete));
    }

    private void LoadCampfireAndWait(Survivor survivor, BaseOven campfire, List<StorageContainer> boxes, Action onComplete)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || campfire == null || campfire.IsDestroyed || campfire.inventory == null)
        {
            onComplete?.Invoke();
            return;
        }

        ItemDefinition woodDef = ItemManager.FindItemDefinition(WoodShortname);
        int woodNeeded = CampfireWoodFill - (woodDef != null ? campfire.inventory.GetAmount(woodDef.itemid) : 0);

        if (woodDef != null && woodNeeded > 0)
        {
            List<Item> carriedWood = new();
            npc.inventory.Take(carriedWood, woodDef.itemid, woodNeeded);

            foreach (Item wood in carriedWood)
            {
                int amount = wood.amount;

                if (wood.MoveToContainer(campfire.inventory))
                {
                    woodNeeded -= amount;
                }
                else
                {
                    wood.MoveToContainer(npc.inventory.containerMain);
                }
            }

            if (woodNeeded > 0)
            {
                WithdrawUpToAmount(boxes, WoodShortname, woodNeeded, campfire.inventory);
            }
        }

        int meatLoaded = 0;

        foreach (Item meat in npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList).Where(IsRawMeat).ToList())
        {
            if (meat.MoveToContainer(campfire.inventory))
            {
                meatLoaded++;
            }
        }

        if (meatLoaded > 0 || campfire.inventory.itemList.Any(i => i.info.GetComponent<ItemModCookable>() != null))
        {
            campfire.StartCooking();
        }

        // Standing by a campfire for a minute is not a stall, and its doors stay the sweeper's to leave alone.
        float exemptUntil = Time.realtimeSinceStartup + CookWaitSeconds + 45f;
        _workshopUntil[survivor.Character.Id] = Mathf.Max(_workshopUntil.GetValueOrDefault(survivor.Character.Id), exemptUntil);
        _baseTripUntil[survivor.Character.Id] = Mathf.Max(_baseTripUntil.GetValueOrDefault(survivor.Character.Id), exemptUntil);

        Puts($"cooking: '{survivor.Character.Alias}' loaded its campfire with {meatLoaded} stack(s) of raw meat and wood - waiting {CookWaitSeconds:F0}s for it to cook.");

        timer.Once(CookWaitSeconds, () => CollectAndEatCookedMeat(survivor, campfire, boxes, onComplete));
    }

    private void CollectAndEatCookedMeat(Survivor survivor, BaseOven campfire, List<StorageContainer> boxes, Action onComplete)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
        {
            return;
        }

        if (campfire == null || campfire.IsDestroyed || campfire.inventory == null)
        {
            onComplete?.Invoke();
            return;
        }

        List<Item> cooked = new();

        foreach (Item item in new List<Item>(campfire.inventory.itemList))
        {
            if (item.info.category != ItemCategory.Food)
            {
                // Charcoal from the burnt wood goes straight into storage; the unburnt wood stays put.
                if (item.info.shortname == "charcoal")
                {
                    foreach (StorageContainer box in boxes)
                    {
                        if (box != null && !box.IsDestroyed && box.inventory != null && item.MoveToContainer(box.inventory))
                        {
                            break;
                        }
                    }
                }

                continue;
            }

            if (item.MoveToContainer(npc.inventory.containerMain) && !IsRawMeat(item))
            {
                // Anything still raw is carried back and cooked on the next trip.
                cooked.Add(item);
            }
        }

        int eaten = 0;
        int banked = 0;

        foreach (Item food in cooked)
        {
            eaten += ConsumeViaItemModConsume(food, npc);

            // Full (or nothing more to consume): whatever remains is banked in storage.
            if (food.amount > 0)
            {
                foreach (StorageContainer box in boxes)
                {
                    if (box != null && !box.IsDestroyed && box.inventory != null && food.MoveToContainer(box.inventory))
                    {
                        banked++;
                        break;
                    }
                }
            }
        }

        campfire.StopCooking();

        Puts($"cooking: '{survivor.Character.Alias}' collected {cooked.Count} stack(s) of cooked meat - ate {eaten} portion(s), banked {banked} stack(s).");
        onComplete?.Invoke();
    }
}
