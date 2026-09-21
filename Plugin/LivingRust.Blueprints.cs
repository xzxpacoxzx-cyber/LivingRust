using System;
using System.Collections.Generic;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Instant blueprint unlock on pickup (2026-09-21, Lucas's own explicit
    // spec): a survivor that picks up a weapon, ammo, tool, clothing or
    // armor of ANY tier learns its blueprint on the spot - the handicap
    // that stops bots being hard-locked behind "find a research table,
    // scrap, and the item" for every single thing. Deliberately limited to
    // items that actually matter for roaming/fighting/surviving (a found
    // ladder hatch or barricade is not worth a blueprint), and only ever a
    // real, user-craftable blueprint.
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

        ItemBlueprint blueprint = item.info.Blueprint;

        if (blueprint == null
            || !blueprint.userCraftable
            || blueprint.defaultBlueprint
            || !IsBlueprintWorthyCategory(item.info.category))
        {
            return;
        }

        // Cheap per-userID cache - FindSurvivorByPlayer is a linear scan and
        // this hook fires for every item any player anywhere receives.
        if (!_blueprintHookBotCache.TryGetValue(owner.userID, out bool isBot))
        {
            isBot = FindSurvivorByPlayer(owner) != null;

            // Only remember a "no" for things that can never be a survivor
            // (real connected players, NPC scientists) - a survivor's own
            // spawn kit can land before its Survivor.Player link exists, and
            // caching that first miss would lock it out permanently.
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
        }
        catch (Exception ex)
        {
            Puts($"blueprint: failed to unlock '{item.info.shortname}' for '{owner.displayName}' ({ex.Message}).");
        }
    }
}
