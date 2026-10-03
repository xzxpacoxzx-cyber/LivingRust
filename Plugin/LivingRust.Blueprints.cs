using System;
using LivingRust.Models;
using System.Collections.Generic;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Instant blueprint unlock on pickup: a survivor that picks up a weapon, ammo,
    // tool, clothing or armor of any tier learns its blueprint on the spot, avoiding
    // a hard research-table/scrap gate for every item. Limited to items that matter
    // for roaming/fighting/surviving, and only ever a real, user-craftable blueprint.
    private readonly Dictionary<ulong, bool> _blueprintHookBotCache = new();

    private static bool IsBlueprintWorthyCategory(ItemCategory category)
    {
        return category == ItemCategory.Weapon
            || category == ItemCategory.Ammunition
            || category == ItemCategory.Attire
            || category == ItemCategory.Tool
            || category == ItemCategory.Medical;
    }

    private void OnItemAddedToContainer(ItemContainer container, Item item)
    {
        BasePlayer owner = container?.playerOwner;

        if (owner == null || item?.info == null || _engine == null)
        {
            return;
        }

        if (IsUnholdableItem(item) && FindSurvivorByPlayer(owner) != null)
        {
            // Removed shortly after - destroying an item from inside its own add hook is unsafe.
            timer.Once(0.1f, () =>
            {
                if (item != null && item.parent == container)
                {
                    item.Remove();
                }
            });
            return;
        }

        ItemBlueprint blueprint = item.info.Blueprint;

        if (blueprint == null
            || !blueprint.userCraftable
            || blueprint.defaultBlueprint
            || !IsBlueprintWorthyCategory(item.info.category))
        {
            return;
        }

        // Per-userID cache, since FindSurvivorByPlayer is a linear scan and this
        // hook fires for every item any player anywhere receives.
        if (!_blueprintHookBotCache.TryGetValue(owner.userID, out bool isBot))
        {
            isBot = FindSurvivorByPlayer(owner) != null;

            // Only caches a "no" for things that can never be a survivor, since a
            // survivor's spawn kit can land before its Survivor.Player link exists.
            if (isBot || owner.IsNpc || owner.IsConnected)
            {
                _blueprintHookBotCache[owner.userID] = isBot;
            }
        }

        if (!isBot || owner.blueprints == null || owner.blueprints.IsUnlocked(item.info))
        {
            return;
        }

        try
        {
            owner.blueprints.Unlock(item.info);
            Puts($"blueprint: '{owner.displayName}' learned the '{item.info.shortname}' blueprint on pickup (workbench level {blueprint.workbenchLevelRequired}).");
            TryUpgradeBaseTierForLearnedBlueprint(owner, blueprint);

            Survivor learner = FindSurvivorByPlayer(owner);

            if (learner != null)
            {
                NoteBlueprintLearned(learner, item.info);
            }
        }
        catch (Exception ex)
        {
            Puts($"blueprint: failed to unlock '{item.info.shortname}' for '{owner.displayName}' ({ex.Message}).");
        }
    }
}
