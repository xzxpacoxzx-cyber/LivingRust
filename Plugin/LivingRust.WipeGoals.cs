using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Progression goals a survivor actively pursues beyond ordinary looting, such as unlocking and
/// crafting the right ammo type for its weapons and retrieving keycards from base storage.
/// Checked from ContinueLootTask as its own priority tier, alongside every other TryPursueX check.
/// </summary>
public partial class LivingRust
{
    // Maps each firearm to the ammo type it fires. Excludes the bow/crossbow family, which has
    // its own dedicated arrow-crafting pipeline.
    private static readonly Dictionary<string, string> WeaponAmmoType = new()
    {
        ["rifle.ak"] = "ammo.rifle",
        ["rifle.lr300"] = "ammo.rifle",
        ["rifle.bolt"] = "ammo.rifle",
        ["rifle.l96"] = "ammo.rifle",
        ["lmg.m249"] = "ammo.rifle",
        ["m16a2"] = "ammo.rifle",
        ["hmlmg"] = "ammo.rifle",
        ["rifle.m39"] = "ammo.rifle",
        ["rifle.sks"] = "ammo.rifle",
        ["rifle.semiauto"] = "ammo.rifle",
        ["minigun"] = "ammo.rifle",
        ["smg.mp5"] = "ammo.pistol",
        ["smg.thompson"] = "ammo.pistol",
        ["smg.2"] = "ammo.pistol",
        ["smg.handmade"] = "ammo.pistol",
        ["pistol.python"] = "ammo.pistol",
        ["pistol.m92"] = "ammo.pistol",
        ["pistol.prototype17"] = "ammo.pistol",
        ["revolver.hc"] = "ammo.pistol",
        ["pistol.semiauto"] = "ammo.pistol",
        ["pistol.revolver"] = "ammo.pistol",
        ["pistol.nailgun"] = "ammo.pistol",
        ["shotgun.m4"] = "ammo.shotgun",
        ["shotgun.spas12"] = "ammo.shotgun",
        ["shotgun.pump"] = "ammo.shotgun",
        ["shotgun.double"] = "ammo.shotgun",
    };

    /// <summary>
    /// Discovers ammo types from firearms a survivor is currently holding and adds any new ones
    /// to Character.KnownAmmoTypes. Called whenever a survivor picks up a firearm.
    /// </summary>
    private void TryUnlockAmmoTypesFromWeapons(Survivor survivor, BasePlayer npc)
    {
        List<string> known = survivor.Character.KnownAmmoTypes ??= new List<string>();

        foreach (Item item in npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList))
        {
            // Defensive null-check: item.info can be null for a malformed/orphaned Item.
            if (item?.info == null || !WeaponAmmoType.TryGetValue(item.info.shortname, out string ammoShortname) || known.Contains(ammoShortname))
            {
                continue;
            }

            known.Add(ammoShortname);
            Puts($"wipe-goal: '{survivor.Character.Alias}' learned it needs to keep '{ammoShortname}' stocked after picking up a '{item.info.shortname}'.");
        }
    }

    // "Running low" threshold, roughly two magazines' worth for the smaller magazine sizes.
    private const int AmmoRestockThreshold = 60;
    private const int AmmoCraftBatchSize = 30;

    // Gunpowder's craft batch is larger than the ammo batch that consumes it, since it's
    // consumed fast.
    private const int GunpowderCraftBatchSize = 60;

    /// <summary>
    /// Top-level ammo-restock goal. Iterates every ammo type the survivor has unlocked and, for
    /// the first one running low, pursues restocking it via the recursive craft-or-gather chain.
    /// Acts on only one type per call.
    /// </summary>
    private bool TryPursueAmmoCraftGoal(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        List<string> known = survivor.Character.KnownAmmoTypes;

        if (known == null || known.Count == 0)
        {
            return false;
        }

        // Readiness gate: a fresh spawn shouldn't be pulled into ammo crafting before it has a
        // base down and has completed at least one deposit trip. Before that point, a looted
        // weapon just rides along under-stocked until normal progression catches up.
        if (!HasCompletedEarlyGameMilestones(survivor))
        {
            return false;
        }

        foreach (string ammoShortname in known)
        {
            ItemDefinition ammoDef = ItemManager.FindItemDefinition(ammoShortname);

            if (ammoDef == null)
            {
                continue;
            }

            if (npc.inventory.GetAmount(ammoDef.itemid) >= AmmoRestockThreshold)
            {
                continue;
            }

            if (TryPursueCraftableItem(survivor, npc, state, ammoDef, AmmoCraftBatchSize, ammoShortname))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recursively crafts an item, or figures out what ingredient is blocking that and pursues
    /// it instead. Uses the game's own recipe data via ItemCrafter/ItemBlueprint, crafts in a
    /// batch, and recurses into an ingredient's own blueprint before falling back to gathering
    /// raw materials.
    /// </summary>
    private bool TryPursueCraftableItem(Survivor survivor, BasePlayer npc, LootTaskState state, ItemDefinition def, int batchAmount, string logLabel)
    {
        ItemBlueprint bp = def.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bp == null || crafter == null)
        {
            return false;
        }

        (Guid, string) key = (survivor.Character.Id, def.shortname);

        if (crafter.queue.Any(task => !task.cancelled && task.blueprint == bp))
        {
            if (!_pendingOneOffCraftTimers.ContainsKey(key))
            {
                WaitForOneOffCraftThenContinue(survivor, state, key);
            }

            return false;
        }

        if (crafter.CanCraft(bp, batchAmount, free: false))
        {
            crafter.CraftItem(bp, npc, amount: batchAmount);
            Puts($"wipe-goal: '{survivor.Character.Alias}' is crafting {batchAmount}x {logLabel} ({bp.time:F0}s).");
            WaitForOneOffCraftThenContinue(survivor, state, key);
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return true;
        }

        ItemAmount missing = bp.GetIngredients()
            .FirstOrDefault(ingredient => npc.inventory.GetAmount(ingredient.itemid) < (int)ingredient.amount * batchAmount);

        if (missing?.itemDef == null)
        {
            return false;
        }

        string missingShortname = missing.itemDef.shortname;

        // Charcoal is a furnace-smelting byproduct, not directly craftable or minable, so just
        // wait on the existing furnace cycle to produce more.
        if (missingShortname == "charcoal")
        {
            VerbosePuts($"wipe-goal: '{survivor.Character.Alias}' is short on charcoal for {logLabel} - waiting on the furnace cycle to produce more.");
            return false;
        }

        // Recurse into the missing ingredient's own recipe if it has one. Raw materials have no
        // blueprint at all, so those naturally fall through to TryGatherCraftIngredient.
        if (missing.itemDef.Blueprint != null
            && TryPursueCraftableItem(survivor, npc, state, missing.itemDef, GunpowderCraftBatchSize, missingShortname))
        {
            return true;
        }

        return TryGatherCraftIngredient(survivor, npc, state, missingShortname);
    }

    /// <summary>
    /// Single entry point for this file's goals, checked from ContinueLootTask.
    /// </summary>
    private bool TryPursueWipeGoal(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        return TryPursueAmmoCraftGoal(survivor, npc, state) || TryPursueAdditionalStorageGoal(survivor, npc, state);
    }

    // How few free slots an owned box can have left before it counts as needing a spare, so
    // deposits don't start failing to find room.
    private const int AdditionalStorageFreeSlotThreshold = 2;

    /// <summary>
    /// Standing "add more storage as needed" goal. Crafts a spare storage box once owned boxes
    /// near home are nearly full, then heads home once a spare is being carried so it can be
    /// placed. Does not touch the build design files; this is a separate, autonomous placement.
    /// </summary>
    private bool TryPursueAdditionalStorageGoal(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        HomeBase home = survivor.Character.Home;

        if (home == null)
        {
            return false;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);

        bool needsMoreStorage = boxes.Count == 0
            || boxes.All(b => b.inventory != null && b.inventory.capacity - b.inventory.itemList.Count <= AdditionalStorageFreeSlotThreshold);

        if (!needsMoreStorage)
        {
            return false;
        }

        ItemDefinition boxDef = ItemManager.FindItemDefinition(LargeWoodBoxShortname);

        if (boxDef == null)
        {
            return false;
        }

        // Already carrying a spare - go home so the deposit trip's end-of-chain step can place it.
        if (npc.inventory.GetAmount(boxDef.itemid) >= 1)
        {
            Puts($"wipe-goal: '{survivor.Character.Alias}' is carrying a spare storage box - heading home to place it.");
            GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor));
            return true;
        }

        return TryPursueCraftableItem(survivor, npc, state, boxDef, 1, "storage box");
    }

    /// <summary>
    /// Places a spare storage box at the survivor's current position, using the same
    /// CreateEntity/SetDeployedBy/OwnerID/Spawn pattern used for other base deployables. Called
    /// once the survivor is genuinely home, so no separate travel logic is needed.
    /// </summary>
    // A survivor that finishes a loot run at a monument without picking up a single firearm gets
    // a small, tier-scaled chance at a starter weapon plus some matching ammo.
    private readonly struct HandicapReward
    {
        public readonly string WeaponShortname;
        public readonly string AmmoShortname;
        public readonly int AmmoAmount;

        public HandicapReward(string weaponShortname, string ammoShortname, int ammoAmount)
        {
            WeaponShortname = weaponShortname;
            AmmoShortname = ammoShortname;
            AmmoAmount = ammoAmount;
        }
    }

    // Handmade shotguns use ammo.handmade.shell, not the workbench-tier ammo.shotgun the
    // pump/SPAS use - a distinct ammo type from the WeaponAmmoType table above.
    private static readonly HandicapReward[] Tier1HandicapPool =
    {
        new("shotgun.double", "ammo.handmade.shell", 20),
        new("shotgun.waterpipe", "ammo.handmade.shell", 20),
        new("pistol.revolver", "ammo.pistol", 32),
        new("smg.handmade", "ammo.pistol", 32),
    };

    private static readonly HandicapReward Tier1SemiAutoPistolExtra = new("pistol.semiauto", "ammo.pistol", 40);
    private static readonly HandicapReward Tier1PythonExtra = new("pistol.python", "ammo.pistol", 40);

    private static readonly HandicapReward[] Tier2HandicapPool =
    {
        new("pistol.semiauto", "ammo.pistol", 40),
        new("rifle.semiauto", "ammo.rifle", 48),
        new("smg.thompson", "ammo.pistol", 60),
        new("shotgun.pump", "ammo.shotgun", 24),
        new("smg.2", "ammo.pistol", 48),
    };

    private static readonly HandicapReward[] Tier3HandicapPool =
    {
        new("smg.mp5", "ammo.pistol", 90),
        new("pistol.m92", "ammo.pistol", 60),
        new("rifle.sks", "ammo.rifle", 80),
        new("rifle.lr300", "ammo.rifle", 120),
        new("shotgun.spas12", "ammo.shotgun", 40),
    };

    private static readonly HandicapReward[] Tier4ExtraPool =
    {
        new("rifle.ak", "ammo.rifle", 120),
        new("rifle.bolt", "ammo.rifle", 40),
        new("pistol.prototype17", "ammo.pistol", 90),
    };

    // Randomized, deliberately not full durability - a handicap weapon should feel like a lucky
    // battlefield find, not a pristine gift.
    private const float HandicapDurabilityMin = 0.2f;
    private const float HandicapDurabilityMax = 0.8f;

    /// <summary>
    /// One weighted roll: chance out of 100 to hit any entry in the pool at all, evenly split
    /// among the pool's entries once it does. Returns null on a miss. The stated percentage is
    /// the combined odds of getting something from the pool, not each entry's individual odds.
    /// </summary>
    private static HandicapReward? RollHandicapPool(HandicapReward[] pool, float chancePercent)
    {
        if (pool.Length == 0 || UnityEngine.Random.Range(0f, 100f) >= chancePercent)
        {
            return null;
        }

        return pool[UnityEngine.Random.Range(0, pool.Length)];
    }

    private void GrantHandicapReward(Survivor survivor, BasePlayer npc, HandicapReward reward)
    {
        Item weapon = ItemManager.CreateByName(reward.WeaponShortname, 1);

        if (weapon == null)
        {
            Puts($"WARNING: handicap reward couldn't create weapon '{reward.WeaponShortname}' - unknown shortname?");
            return;
        }

        weapon.condition = weapon.info.condition.max * UnityEngine.Random.Range(HandicapDurabilityMin, HandicapDurabilityMax);

        if (!npc.inventory.GiveItem(weapon))
        {
            weapon.Remove();
            return;
        }

        // 2026-10-03, Lucas's own explicit spec: the stated amount is now a ceiling, not a fixed
        // grant - rolled 0 to that amount inclusive, so sometimes the weapon arrives with no ammo
        // at all.
        int ammoAmount = UnityEngine.Random.Range(0, reward.AmmoAmount + 1);

        if (ammoAmount > 0)
        {
            GiveItem(npc, reward.AmmoShortname, ammoAmount);
        }

        Puts($"wipe-goal: '{survivor.Character.Alias}' got a handicap reward - '{reward.WeaponShortname}' ({weapon.conditionNormalized:P0} durability) + {ammoAmount}x {reward.AmmoShortname}.");
    }

    // Already-armed penalty: an armed survivor can still roll a handicap weapon, just at a
    // reduced rate. An unarmed survivor is unaffected (multiplier stays 1).
    private const float ArmedHandicapRewardMultiplier = 0.66f;

    /// <summary>
    /// Entry point, called once a survivor finishes with a monument. Odds are gated on how the
    /// visit was spent: a survivor that completed a real ghost route (loot or puzzle) gets full
    /// odds, otherwise every roll is halved. Solving the card puzzle specifically adds a flat
    /// bonus on top. An already-armed survivor's odds are further scaled down rather than
    /// excluded outright.
    /// </summary>
    private void TryGrantMonumentHandicapReward(Survivor survivor, BasePlayer npc, MonumentTier tier, bool didRealGhostRoute, bool didCardPuzzle)
    {
        float baseMultiplier = didRealGhostRoute ? 1f : 0.5f;
        float puzzleBonus = didCardPuzzle ? 5f : 0f;
        float armedMultiplier = CountRealFirearms(npc) > 0 ? ArmedHandicapRewardMultiplier : 1f;

        float Chance(float basePercent) => ((basePercent * baseMultiplier) + puzzleBonus) * armedMultiplier;

        HandicapReward? reward = tier switch
        {
            // Tier1: main 10% pool split 4 ways, plus two separate 5% rolls stacked on top
            // (semi-auto pistol, python). First hit wins, so only one handicap gun per roll.
            MonumentTier.TierZero => RollHandicapPool(Tier1HandicapPool, Chance(10f))
                ?? RollHandicapPool(new[] { Tier1SemiAutoPistolExtra }, Chance(5f))
                ?? RollHandicapPool(new[] { Tier1PythonExtra }, Chance(5f)),

            // Tier2: single 10% pool, 5 ways.
            MonumentTier.TierOne => RollHandicapPool(Tier2HandicapPool, Chance(10f)),

            // Tier3: 20% main pool (5 ways) plus a separate 30% chance at any Tier2 reward.
            MonumentTier.TierTwo => RollHandicapPool(Tier3HandicapPool, Chance(20f))
                ?? RollHandicapPool(Tier2HandicapPool, Chance(30f)),

            // Tier4: three independent pools (Tier3 reward, Tier4-exclusive, Tier2 reward),
            // first hit wins.
            MonumentTier.TierThree => RollHandicapPool(Tier3HandicapPool, Chance(30f))
                ?? RollHandicapPool(Tier4ExtraPool, Chance(10f))
                ?? RollHandicapPool(Tier2HandicapPool, Chance(30f)),

            _ => null,
        };

        if (reward != null)
        {
            GrantHandicapReward(survivor, npc, reward.Value);
        }
    }

    // Clothing/armor bonus reward (2026-10-03, Lucas's own explicit spec), independent of and
    // stacked on top of the weapon handicap reward above - one item, not all, cascaded the same
    // "tier's own pool, then a boosted shot at the tier below" shape. Shortnames verified live
    // against wiki.rustclash.com's own "Short Name" field for each item, not guessed.
    private static readonly string[] Tier0ClothingPool =
    {
        "hazmatsuit", "tshirt", "shirt.tanktop", "wood.armor.helmet", "wood.armor.pants",
        "wood.armor.jacket", "woodarmor.gloves", "jacket.snow", "pants", "bucket.helmet",
        "riot.helmet", "smallbackpack", "diving.wetsuit", "pants.shorts",
        "attire.hide.boots", "attire.hide.pants", "attire.hide.helterneck", "attire.hide.poncho",
        "attire.hide.skirt", "attire.hide.vest",
    };

    private static readonly string[] Tier1ClothingPool =
    {
        "shoes.boots", "hat.wolf", "shirt.collared", "tshirt.long", "jacket", "hoodie",
        "coffeecan.helmet", "roadsign.kilt", "roadsign.jacket", "hat.cap", "hat.boonie",
        "roadsign.gloves",
    };

    private static readonly string[] Tier2ClothingPool =
    {
        "roadsign.kilt", "roadsign.jacket", "coffeecan.helmet", "hat.wolf", "roadsign.gloves",
    };

    private static readonly string[] Tier3ClothingPool =
    {
        "metal.facemask", "metal.plate.torso", "largebackpack",
    };

    private static string RollClothingPool(string[] pool, float chancePercent)
    {
        if (pool.Length == 0 || UnityEngine.Random.Range(0f, 100f) >= chancePercent)
        {
            return null;
        }

        return pool[UnityEngine.Random.Range(0, pool.Length)];
    }

    /// <summary>
    /// Bonus clothing/armor roll on top of the weapon handicap reward, same "finished a loot run
    /// at this monument" trigger. Tier0: 10% at the Tier0 pool. Tier1: 15% at Tier1, else 20% at
    /// Tier0. Tier2: 20% at Tier2, else 30% at Tier1. Tier3: 20% at Tier3, else 30% at Tier2 -
    /// first hit wins, so only ever one item. Random not-full condition, same "lucky battlefield
    /// find" reasoning as GrantHandicapReward, skipped for items with no durability.
    /// </summary>
    private void TryGrantMonumentClothingReward(Survivor survivor, BasePlayer npc, MonumentTier tier)
    {
        string shortname = tier switch
        {
            MonumentTier.TierZero => RollClothingPool(Tier0ClothingPool, 10f),
            MonumentTier.TierOne => RollClothingPool(Tier1ClothingPool, 15f) ?? RollClothingPool(Tier0ClothingPool, 20f),
            MonumentTier.TierTwo => RollClothingPool(Tier2ClothingPool, 20f) ?? RollClothingPool(Tier1ClothingPool, 30f),
            MonumentTier.TierThree => RollClothingPool(Tier3ClothingPool, 20f) ?? RollClothingPool(Tier2ClothingPool, 30f),
            _ => null,
        };

        if (shortname == null)
        {
            return;
        }

        Item item = ItemManager.CreateByName(shortname, 1);

        if (item == null)
        {
            Puts($"WARNING: monument clothing reward couldn't create '{shortname}' - unknown shortname?");
            return;
        }

        if (item.hasCondition)
        {
            item.condition = item.info.condition.max * UnityEngine.Random.Range(HandicapDurabilityMin, HandicapDurabilityMax);
        }

        if (!npc.inventory.GiveItem(item))
        {
            item.Remove();
            return;
        }

        Puts($"wipe-goal: '{survivor.Character.Alias}' got a bonus clothing reward from a monument clear - '{shortname}'.");
    }

    // Medical bonus reward (2026-10-03, Lucas's own explicit spec), independent of the other
    // monument-clear rewards above. Tier0 gets bandages (1-3), Tier1/2/3 get medical syringes
    // (1-3) - both 20% flat, no tier scaling.
    private const float MonumentMedicalRewardChance = 20f;

    private void TryGrantMonumentMedicalReward(Survivor survivor, BasePlayer npc, MonumentTier tier)
    {
        if (UnityEngine.Random.Range(0f, 100f) >= MonumentMedicalRewardChance)
        {
            return;
        }

        string shortname = tier == MonumentTier.TierZero ? BandageShortname : MedicalSyringeShortname;
        int amount = UnityEngine.Random.Range(1, 4); // 1-3 inclusive

        GiveItem(npc, shortname, amount);
        Puts($"wipe-goal: '{survivor.Character.Alias}' got a bonus medical reward from a monument clear - {amount}x {shortname}.");
    }

    // Tool/weapon bonus reward (2026-10-03, Lucas's own explicit spec), independent of every
    // other monument-clear reward above - one item, not all. Tier3 isn't covered by this one;
    // Lucas's spec only named Tier0 through Tier2.
    private static readonly string[] Tier0ToolBonusPool = { "pickaxe", "hatchet", "crossbow" };
    private static readonly string[] Tier1ToolBonusPool = { "icepick.salvaged", "axe.salvaged", "jackhammer" };

    private const float Tier0ToolBonusChance = 40f;
    private const float Tier1ToolBonusChance = 40f;

    // Tier2 reuses the Tier1 pool at a boosted chance - Lucas's own framing: "Tier2 Monuments
    // additionally reward at Tier1 monument rewards however with a 60% chance."
    private const float Tier2ToolBonusChance = 60f;

    private void TryGrantMonumentToolBonusReward(Survivor survivor, BasePlayer npc, MonumentTier tier)
    {
        string[] pool;
        float chance;

        switch (tier)
        {
            case MonumentTier.TierZero:
                pool = Tier0ToolBonusPool;
                chance = Tier0ToolBonusChance;
                break;
            case MonumentTier.TierOne:
                pool = Tier1ToolBonusPool;
                chance = Tier1ToolBonusChance;
                break;
            case MonumentTier.TierTwo:
                pool = Tier1ToolBonusPool;
                chance = Tier2ToolBonusChance;
                break;
            default:
                return;
        }

        if (UnityEngine.Random.Range(0f, 100f) >= chance)
        {
            return;
        }

        string shortname = pool[UnityEngine.Random.Range(0, pool.Length)];
        Item item = ItemManager.CreateByName(shortname, 1);

        if (item == null)
        {
            Puts($"WARNING: monument tool bonus reward couldn't create '{shortname}' - unknown shortname?");
            return;
        }

        if (item.hasCondition)
        {
            item.condition = item.info.condition.max * UnityEngine.Random.Range(HandicapDurabilityMin, HandicapDurabilityMax);
        }

        if (!npc.inventory.GiveItem(item))
        {
            item.Remove();
            return;
        }

        Puts($"wipe-goal: '{survivor.Character.Alias}' got a bonus tool reward from a monument clear - '{shortname}'.");
    }

    // Flat override chance for ConcludeMonumentRush's bonus roll: one flat roll against the
    // tier's primary pool, replacing the normal cascaded/stacked roll for this specific event.
    private const float MonumentRushCompletionRewardChance = 75f;

    /// <summary>
    /// Called only from ConcludeMonumentRush on a successful rush. Uses the same reduced-not-
    /// excluded armed handling as TryGrantMonumentHandicapReward, but with one flat 75% roll
    /// against the tier's main pool instead of the normal tiered/stacked odds.
    /// </summary>
    private void GrantMonumentRushCompletionReward(Survivor survivor, BasePlayer npc, MonumentTier tier)
    {
        HandicapReward[] pool = tier switch
        {
            MonumentTier.TierZero => Tier1HandicapPool,
            MonumentTier.TierOne => Tier2HandicapPool,
            MonumentTier.TierTwo => Tier3HandicapPool,
            MonumentTier.TierThree => Tier3HandicapPool,
            _ => Array.Empty<HandicapReward>(),
        };

        float armedMultiplier = CountRealFirearms(npc) > 0 ? ArmedHandicapRewardMultiplier : 1f;
        HandicapReward? reward = RollHandicapPool(pool, MonumentRushCompletionRewardChance * armedMultiplier);

        if (reward != null)
        {
            GrantHandicapReward(survivor, npc, reward.Value);
        }
    }

    // Which keycard tier is a given monument tier's reward card. TierZero monuments have no card
    // reader at all (open loot only, no puzzle room), so they get no keycard reward.
    private static KeycardTier? KeycardTierForMonumentTier(MonumentTier tier)
    {
        return tier switch
        {
            MonumentTier.TierOne => KeycardTier.Green,
            MonumentTier.TierTwo => KeycardTier.Blue,
            MonumentTier.TierThree => KeycardTier.Red,
            _ => null,
        };
    }

    // Per-monument "first clear" reward cycle. Keyed by monument name. Value is the wall-clock
    // moment the first bot to clear that monument this cycle got its 100% reward.
    private readonly Dictionary<string, float> _monumentFirstClearGrantedAt = new();

    // Full cycle length: a monument's "first clear" slot is up for grabs again once this much
    // time has passed since the last one was claimed.
    private const float KeycardFirstClearCycleSeconds = 900f;

    // Blackout window right after the first-clear grant where no reward is given at all,
    // distinct from the reduced-odds window that follows it until the cycle ends. Kept at the
    // same 85% of the cycle as before (was 1530/1800s), so the reduced-odds window is still the
    // last ~15% of the cycle, just proportionally shorter (135s instead of 270s).
    private const float KeycardBlackoutWindowSeconds = 765f;

    private const float KeycardFirstClearFuseAmount = 2;
    private const float KeycardRewardChanceGeneralLooting = 30f;

    /// <summary>
    /// Grants a keycard reward for finishing a monument. The first survivor to clear a monument
    /// each cycle gets a guaranteed keycard and fuses; after a blackout window, later clears get
    /// a flat chance at a keycard only.
    /// </summary>
    private void TryGrantMonumentKeycardReward(Survivor survivor, BasePlayer npc, MonumentTier tier, string monumentName)
    {
        KeycardTier? keycardTier = KeycardTierForMonumentTier(tier);

        if (keycardTier == null)
        {
            return;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;

        if (!_monumentFirstClearGrantedAt.TryGetValue(monumentName, out float grantedAt)
            || now - grantedAt >= KeycardFirstClearCycleSeconds)
        {
            _monumentFirstClearGrantedAt[monumentName] = now;

            GiveItem(npc, KeycardShortnames[keycardTier.Value], 1);
            GiveItem(npc, "fuse", (int)KeycardFirstClearFuseAmount);
            Puts($"wipe-goal: '{survivor.Character.Alias}' is the first to clear '{monumentName}' this cycle - got a guaranteed '{KeycardShortnames[keycardTier.Value]}' + {KeycardFirstClearFuseAmount:F0}x fuse.");
            return;
        }

        float elapsedSinceFirstClear = now - grantedAt;

        if (elapsedSinceFirstClear < KeycardBlackoutWindowSeconds)
        {
            return;
        }

        if (UnityEngine.Random.Range(0f, 100f) >= KeycardRewardChanceGeneralLooting)
        {
            return;
        }

        GiveItem(npc, KeycardShortnames[keycardTier.Value], 1);
        Puts($"wipe-goal: '{survivor.Character.Alias}' got a keycard reward - '{KeycardShortnames[keycardTier.Value]}' from finishing up at '{monumentName}' (outside the first-clear window).");
    }

    private void TryPlaceAdditionalStorageBoxAtHome(Survivor survivor, BasePlayer npc)
    {
        ItemDefinition boxDef = ItemManager.FindItemDefinition(LargeWoodBoxShortname);

        if (boxDef == null)
        {
            return;
        }

        Item boxItem = npc.inventory.FindItemByItemID(boxDef.itemid);

        if (boxItem == null)
        {
            return;
        }

        ItemModDeployable modDeployable = boxDef.GetComponent<ItemModDeployable>();

        if (modDeployable == null || !_engine.NavigationManager.TryGetGroundHeight(npc.transform.position, out float groundHeight))
        {
            return;
        }

        Vector3 position = npc.transform.position;
        position.y = groundHeight;
        Quaternion rotation = Quaternion.LookRotation(Vector3.up, npc.eyes.BodyForward()) * Quaternion.Euler(90f, 0f, 0f);

        BaseEntity boxEntity = GameManager.server.CreateEntity(modDeployable.entityPrefab.resourcePath, position, rotation);

        if (boxEntity == null)
        {
            Puts($"wipe-goal: '{survivor.Character.Alias}' failed to create an additional storage box entity ('{modDeployable.entityPrefab.resourcePath}').");
            return;
        }

        boxEntity.skinID = boxItem.skin;
        boxEntity.SendMessage("SetDeployedBy", npc, SendMessageOptions.DontRequireReceiver);
        boxEntity.OwnerID = npc.userID;
        boxEntity.Spawn();
        boxItem.UseItem(1);

        Puts($"wipe-goal: '{survivor.Character.Alias}' placed an additional storage box at its base.");
    }

    // Reuses HomeStorageSearchRadius for the base-storage keycard search radius.
    private static readonly Dictionary<KeycardTier, int> KeycardTierRank = new()
    {
        [KeycardTier.Green] = 1,
        [KeycardTier.Blue] = 2,
        [KeycardTier.Red] = 3,
    };

    /// <summary>
    /// Withdraws the highest-tier keycard from base storage if it beats whatever the survivor is
    /// already carrying. Does nothing if storage has nothing better.
    /// </summary>
    private void TryWithdrawBestKeycardFromStorage(Survivor survivor, BasePlayer npc, HomeBase home)
    {
        KeycardTier? carriedBest = null;

        foreach (KeycardTier tier in KeycardTierRank.Keys)
        {
            if (HasUsableKeycard(npc, tier) && (carriedBest == null || KeycardTierRank[tier] > KeycardTierRank[carriedBest.Value]))
            {
                carriedBest = tier;
            }
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);

        KeycardTier? storageBest = null;

        foreach (KeycardTier tier in KeycardTierRank.Keys)
        {
            bool hasInStorage = boxes.Any(b => b != null && !b.IsDestroyed && b.inventory != null
                && b.inventory.itemList.Any(i => i.info.shortname == KeycardShortnames[tier] && i.condition > 0f));

            if (hasInStorage && (storageBest == null || KeycardTierRank[tier] > KeycardTierRank[storageBest.Value]))
            {
                storageBest = tier;
            }
        }

        if (storageBest == null || (carriedBest != null && KeycardTierRank[storageBest.Value] <= KeycardTierRank[carriedBest.Value]))
        {
            return;
        }

        if (WithdrawUpToAmount(boxes, KeycardShortnames[storageBest.Value], 1, npc.inventory.containerMain) > 0)
        {
            Puts($"wipe-goal: '{survivor.Character.Alias}' grabbed a {storageBest.Value} keycard from base storage before heading out.");
        }
    }
}
