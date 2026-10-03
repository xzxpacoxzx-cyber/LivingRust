using LivingRust.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Gear-based monument tiering: the decision layer behind which monument a survivor should head
/// toward, using a 0-100 gear score (weapon + armor, normalized) across monument tiers with a
/// weighted random roll rather than always picking the nearest, so bots don't all converge on the
/// same destination.
/// </summary>
public partial class LivingRust
{
    /// <summary>
    /// Best-owned-weapon score, 0-85. A flat lookup rather than derived from WeaponPriority's own
    /// rank order, since that list ranks belt preference, not monument-difficulty readiness.
    /// Anything not listed (thrown weapons, melee) scores 0, since this is a ranged-combat
    /// readiness score.
    /// </summary>
    private static readonly Dictionary<string, int> WeaponGearScore = new()
    {
        ["minigun"] = 85,
        ["lmg.m249"] = 75,
        ["rifle.l96"] = 75,
        ["rifle.ak"] = 60,
        ["rifle.lr300"] = 60,
        ["rifle.bolt"] = 60,
        ["m16a2"] = 50,
        ["hmlmg"] = 50,
        ["rifle.m39"] = 50,
        ["shotgun.m4"] = 50,
        ["smg.mp5"] = 45,
        ["rifle.sks"] = 45,
        ["smg.thompson"] = 40,
        ["shotgun.spas12"] = 40,
        ["pistol.python"] = 35,
        ["rifle.semiauto"] = 35,
        ["pistol.m92"] = 35,
        ["pistol.prototype17"] = 35,
        ["revolver.hc"] = 30,
        ["smg.2"] = 30,
        ["shotgun.pump"] = 30,
        ["pistol.semiauto"] = 25,
        ["smg.handmade"] = 20,
        ["pistol.revolver"] = 15,
        ["shotgun.double"] = 15,
        ["crossbow"] = 10,
        ["minicrossbow"] = 10,
        ["pistol.nailgun"] = 10,
        ["bow.compound"] = 5,
        ["bow.hunting"] = 5,
    };

    /// <summary>
    /// Best owned weapon's score, 0 if unarmed or the weapon isn't in WeaponGearScore. Reuses
    /// WeaponPriority's ordering purely to pick which weapon to score, not for the score itself.
    /// </summary>
    private static int GetWeaponGearScore(BasePlayer npc)
    {
        // Belt only, not main inventory - a spare weapon sitting unbelted isn't actually carried
        // the way a readily-accessible loadout is.
        Item best = FindBestByPriority(npc.inventory.containerBelt.itemList, WeaponPriority, exclude: null);

        if (best == null)
        {
            return 0;
        }

        return WeaponGearScore.TryGetValue(best.info.shortname, out int score) ? score : 0;
    }

    /// <summary>
    /// Worn-armor score, 0-100: average of the best two worn pieces' GetArmorTier×20 values
    /// (missing pieces below 2 count as 0). Averages over the best 2 rather than dividing by an
    /// assumed 3-piece kit, so an armor tier that only offers 2 real pieces isn't structurally
    /// penalized for a 3rd slot that doesn't exist.
    /// </summary>
    private static int GetArmorGearScore(BasePlayer npc)
    {
        List<Item> worn = npc.inventory.containerWear.itemList;

        List<int> pieceScores = worn
            .Select(item => (int)GetArmorTier(item.info.shortname) * 20)
            .OrderByDescending(score => score)
            .Take(2)
            .ToList();

        while (pieceScores.Count < 2)
        {
            pieceScores.Add(0);
        }

        return Mathf.Clamp(Mathf.RoundToInt((float)pieceScores.Average()), 0, 100);
    }

    private static readonly string[] ScoredAmmoShortnames =
    {
        "ammo.rifle", "ammo.rifle.explosive", "ammo.rifle.hv", "ammo.rifle.incendiary",
        "ammo.pistol", "ammo.pistol.fire", "ammo.pistol.hv",
    };

    // Bows/crossbows score in WeaponGearScore but combat only supports BaseProjectile firearms, so
    // owning one of these doesn't mean the bot can actually fight back; HasReadyRangedWeapon
    // excludes them.
    private static readonly string[] NonCombatCapableRangedWeaponShortnames =
    {
        "bow.compound", "bow.hunting", "crossbow", "minicrossbow",
    };

    /// <summary>
    /// Whether npc owns a firearm and has at least one round of matching ammo anywhere in its
    /// inventory. Backs the scientist-loot-avoidance check, which only skips looting near a
    /// hostile scientist while genuinely unable to fight back.
    /// </summary>
    private bool HasReadyRangedWeapon(BasePlayer npc)
    {
        // Bows count once they have arrows, since an archer with arrows shouldn't avoid scientist
        // areas as if unarmed.
        return HasReadyFirearm(npc) || (HasBowFamilyWeapon(npc) && HasAnyArrows(npc));
    }

    private bool HasBowFamilyWeapon(BasePlayer npc)
    {
        return npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Any(item => Array.IndexOf(NonCombatCapableRangedWeaponShortnames, item.info.shortname) >= 0);
    }

    private bool HasAnyArrows(BasePlayer npc)
    {
        return npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Any(item => item.amount > 0 && item.info.shortname.StartsWith("arrow.", StringComparison.Ordinal));
    }

    private bool HasReadyFirearm(BasePlayer npc)
    {
        Item best = FindBestByPriority(npc.inventory.containerBelt.itemList, WeaponPriority, exclude: null);

        if (best == null || Array.IndexOf(NonCombatCapableRangedWeaponShortnames, best.info.shortname) >= 0)
        {
            return false;
        }

        return npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Any(item => Array.IndexOf(ScoredAmmoShortnames, item.info.shortname) >= 0 && item.amount > 0);
    }

    /// <summary>
    /// Melee weapons only, kept separate from pickaxes/hatchets (their own bonus below) by
    /// excluding those from MeleeToolPriority. A computed property rather than a static field,
    /// since C# doesn't guarantee static initializer order across files of the same partial class.
    /// </summary>
    private static string[] MeleeWeaponBonusShortnames => MeleeToolPriority
        .Except(PickaxeFamily)
        .Except(HatchetFamily)
        .Except(new[] { "rock" })
        .ToArray();

    /// <summary>
    /// Ammo/meds/tools sustain bonus, added on top of the weapon+armor average rather than blended
    /// into it, since it's a separate "can this bot sustain a fight/trip" signal. Counts across
    /// the whole inventory, unlike GetWeaponGearScore's belt-only scope.
    /// </summary>
    private static float GetSustainGearScore(BasePlayer npc)
    {
        List<Item> allItems = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .ToList();

        float score = 0f;

        score += allItems.Where(item => item.info.shortname == "syringe.medical").Sum(item => item.amount) * 2f;
        score += allItems.Where(item => item.info.shortname == "bandage").Sum(item => item.amount) * 0.5f;
        score += allItems.Where(item => item.info.shortname == "largemedkit").Sum(item => item.amount) * 1f;

        int ammoCount = allItems.Where(item => Array.IndexOf(ScoredAmmoShortnames, item.info.shortname) >= 0).Sum(item => item.amount);
        score += ammoCount / 64f * 2f;

        if (allItems.Any(item => Array.IndexOf(PickaxeFamily, item.info.shortname) >= 0 || Array.IndexOf(HatchetFamily, item.info.shortname) >= 0))
        {
            score += 2f;
        }

        if (allItems.Any(item => Array.IndexOf(MeleeWeaponBonusShortnames, item.info.shortname) >= 0))
        {
            score += 2f;
        }

        // Keycards: flat bonus per colour owned, not scaled by quantity. Owning multiple colours
        // stacks, so this can add up to +30 total.
        if (allItems.Any(item => item.info.shortname == "keycard_green"))
        {
            score += 5f;
        }

        if (allItems.Any(item => item.info.shortname == "keycard_blue"))
        {
            score += 10f;
        }

        if (allItems.Any(item => item.info.shortname == "keycard_red"))
        {
            score += 15f;
        }

        return score;
    }

    /// <summary>
    /// Combined 0-100 gear score: weapon and armor components are simple-averaged, then the
    /// sustain bonus is added on top and the whole thing clamped back to 0-100 so it stays inside
    /// the gear-score bands' range.
    /// </summary>
    private static int GetGearScore(BasePlayer npc)
    {
        float baseScore = (GetWeaponGearScore(npc) + GetArmorGearScore(npc)) / 2f;
        float sustainBonus = GetSustainGearScore(npc);

        return Mathf.Clamp(Mathf.RoundToInt(baseScore + sustainBonus), 0, 100);
    }

    /// <summary>
    /// "How good is this loot" score for a raw item collection, such as a corpse's containers or a
    /// bag's inventory, used to weight which corpse/bag a survivor prioritizes by relative value
    /// rather than just proximity. Scans every item flat, unlike GetGearScore's
    /// belt/worn/main split.
    /// </summary>
    private static int GetContentsGearScore(IEnumerable<Item> items)
    {
        List<Item> all = items.Where(item => item != null).ToList();

        int bestWeaponScore = all
            .Where(item => WeaponGearScore.ContainsKey(item.info.shortname))
            .Select(item => WeaponGearScore[item.info.shortname])
            .DefaultIfEmpty(0)
            .Max();

        List<int> armorPieceScores = all
            .Select(item => (int)GetArmorTier(item.info.shortname) * 20)
            .OrderByDescending(score => score)
            .Take(2)
            .ToList();

        while (armorPieceScores.Count < 2)
        {
            armorPieceScores.Add(0);
        }

        float baseScore = (bestWeaponScore + (float)armorPieceScores.Average()) / 2f;

        float sustainBonus = all.Where(item => item.info.shortname == "syringe.medical").Sum(item => item.amount) * 2f;
        sustainBonus += all.Where(item => item.info.shortname == "bandage").Sum(item => item.amount) * 0.5f;
        sustainBonus += all.Where(item => item.info.shortname == "largemedkit").Sum(item => item.amount) * 1f;

        int ammoCount = all.Where(item => Array.IndexOf(ScoredAmmoShortnames, item.info.shortname) >= 0).Sum(item => item.amount);
        sustainBonus += ammoCount / 64f * 2f;

        if (all.Any(item => item.info.shortname == "keycard_green")) sustainBonus += 5f;
        if (all.Any(item => item.info.shortname == "keycard_blue")) sustainBonus += 10f;
        if (all.Any(item => item.info.shortname == "keycard_red")) sustainBonus += 15f;

        return Mathf.Clamp(Mathf.RoundToInt(baseScore + sustainBonus), 0, 100);
    }

    private enum MonumentTier
    {
        TierZero = 0,
        TierOne = 1,
        TierTwo = 2,
        TierThree = 3,
    }

    /// <summary>
    /// Monument name substrings grouped by tier, defined by keycard/puzzle requirements:
    /// Tier 0 has no keycard/puzzle at all; Tier 1 requires a green keycard and fuse, and is also
    /// the fallback default for any unlisted monument type; Tier 2 requires a blue keycard (with
    /// some per-monument variation in whether green/fuse is also needed); Tier 3 requires green,
    /// blue, and red. Matched against MonumentInfo.name via substring.
    /// </summary>
    private static readonly string[] TierZeroMonumentSubstrings =
    {
        "gas_station",
        "supermarket",
        "mining_outpost",
        "junkyard",
        "lighthouse", // still excluded from autonomy entirely - see AutonomyExcludedMonumentSubstrings
    };

    private static readonly string[] MediumTierMonumentSubstrings =
    {
        "powerplant",
        "water_treatment_plant",
        "trainyard",
        "airfield",
        "arctic_research_base",
        "military_base", // abandoned military base (_a/_b/_c/_d biome variants)
        "underwater_lab",
    };

    private static readonly string[] HighTierMonumentSubstrings =
    {
        "nuclear_missile_silo",
        "launch_site",
        "oilrig",
        "military_tunnel",
    };

    private static MonumentTier GetMonumentTier(string monumentName)
    {
        if (HighTierMonumentSubstrings.Any(substring => monumentName.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return MonumentTier.TierThree;
        }

        if (MediumTierMonumentSubstrings.Any(substring => monumentName.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return MonumentTier.TierTwo;
        }

        if (TierZeroMonumentSubstrings.Any(substring => monumentName.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return MonumentTier.TierZero;
        }

        return MonumentTier.TierOne;
    }

    /// <summary>
    /// Monument types autonomous survivors should never scan/select as a loot destination.
    /// Lighthouse and Underwater Lab require water movement this project doesn't have yet; Ranch,
    /// barn, Outpost, Bandit Camp, and Fishing Village are safezones with nothing to loot. Purely
    /// an autonomy exclusion - debug/admin lookup commands still work against any of these by name.
    /// </summary>
    private static readonly string[] AutonomyExcludedMonumentSubstrings =
    {
        "lighthouse",
        "underwater_lab",
        "apartments_complex",
        "ranch",
        "barn",
        "compound",
        "bandit",
        "fishing_village",

        // military_tunnel is a flat exclusion from autonomous rolling regardless of gear, unlike
        // Launch Site which has a gear-score carve-out instead.
        "military_tunnel",

        // "stables" is another safezone; the rest are terrain/scenery features with no real loot
        // worth detouring for (swamp variants, ice lakes, oasis, water wells).
        "stables",
        "swamp",
        "ice_lake",
        "ue_lake",
        "oasis",
        "water_well",

        // Oil Rig is registered and already Tier 3, but needs boat travel and RHIB-mounted
        // scientist handling this project doesn't have yet, so it's parked out of autonomy.
        "oilrig",
    };

    private static bool IsMonumentExcludedFromAutonomy(string monumentName)
    {
        return !string.IsNullOrEmpty(monumentName)
            && AutonomyExcludedMonumentSubstrings.Any(substring => monumentName.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// Also checks the player-facing display name (MonumentInfo.displayPhrase.english), not just
    /// the internal prefab name, since some monuments' internal names don't contain an obvious
    /// matching substring.
    /// </summary>
    private static bool IsMonumentExcludedFromAutonomy(MonumentInfo monument)
    {
        if (monument == null)
        {
            return false;
        }

        if (IsMonumentExcludedFromAutonomy(monument.name))
        {
            return true;
        }

        string displayName = monument.displayPhrase.IsValid() ? monument.displayPhrase.english : null;
        return IsMonumentExcludedFromAutonomy(displayName);
    }

    /// <summary>
    /// Deliberately overlapping gear-score bands across four tiers. A score in an overlap window
    /// matches multiple bands at once, and GetTierWeights blends every matching band's weight via
    /// a continuous falloff curve rather than picking just one, so a bot can still roll a
    /// higher or lower tier at reduced odds.
    /// </summary>
    private const int TierZeroBandMin = 0;
    private const int TierZeroBandMax = 20;
    private const int LowTierBandMin = 10;
    private const int LowTierBandMax = 33;
    private const int MediumTierBandMin = 25;
    private const int MediumTierBandMax = 65;
    private const int HighTierBandMin = 58;
    private const int HighTierBandMax = 100;

    // Each band's own midpoint becomes the curve's anchor - Tier 0 centers
    // on 10, Tier 1 on 21.5, Tier 2 on 45, Tier 3 on 79. The bands
    // themselves stay exactly as tuned, they're just no longer read as flat
    // on/off ranges - they inform WHERE each tier's weight peaks.
    private const float TierZeroCenter = (TierZeroBandMin + TierZeroBandMax) / 2f;
    private const float LowTierCenter = (LowTierBandMin + LowTierBandMax) / 2f;
    private const float MediumTierCenter = (MediumTierBandMin + MediumTierBandMax) / 2f;
    private const float HighTierCenter = (HighTierBandMin + HighTierBandMax) / 2f;

    // How far (in gear-score points) a tier's weight takes to fall from
    // its peak down to its floor - shared across all four tiers for
    // simplicity. Roughly matches each band's own half-width, so the
    // curve's "real reach" stays close to what the discrete bands imply.
    private const float TierFalloffRadius = 25f;

    /// <summary>
    /// Each tier's weight is a smooth triangular falloff from its own band-derived center,
    /// clamped so it never goes negative past the falloff radius. Floors keep every tier reachable
    /// at any gear score, but at a small share for a badly-mismatched score.
    /// </summary>
    private static (float TierZero, float Tier1, float Tier2, float Tier3, float Local) GetTierWeights(int gearScore)
    {
        // Gear-independent and monument-favouring: loot density at monuments is far higher than on
        // roads, and gear score only gates the card puzzle itself, so a bot can still walk a
        // monument's ghost-route loot path regardless of gear. Tier0/Tier1 are the road-heavy
        // tiers, so they're weighted lower. gearScore is kept as a parameter for existing call
        // sites.
        return (TierZero: 10f, Tier1: 30f, Tier2: 40f, Tier3: 40f, Local: 12f);
    }

    // Soft monument occupancy cap: bots register when they commit to a monument destination, and
    // a registration lapses after MonumentOccupancyLifetimeSeconds or when the bot dies. Each
    // current occupant lowers that monument's pick weight, but never to zero.
    private const float MonumentOccupancyLifetimeSeconds = 480f;
    private readonly Dictionary<string, Dictionary<Guid, float>> _monumentOccupants = new();

    private static int GetMonumentOccupancyCap(MonumentTier tier)
    {
        return tier switch
        {
            MonumentTier.TierZero => 3,
            MonumentTier.TierOne => 4,
            MonumentTier.TierTwo => 6,
            _ => 8,
        };
    }

    private int GetMonumentOccupancy(string monumentName)
    {
        if (!_monumentOccupants.TryGetValue(monumentName, out Dictionary<Guid, float> occupants))
        {
            return 0;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;
        int count = 0;

        foreach (float expiry in occupants.Values)
        {
            if (expiry > now)
            {
                count++;
            }
        }

        return count;
    }

    private void RegisterMonumentOccupancy(Guid characterId, string monumentName)
    {
        if (string.IsNullOrEmpty(monumentName) || monumentName == "a nearby road")
        {
            return;
        }

        ReleaseMonumentOccupancy(characterId);

        if (!_monumentOccupants.TryGetValue(monumentName, out Dictionary<Guid, float> occupants))
        {
            occupants = new Dictionary<Guid, float>();
            _monumentOccupants[monumentName] = occupants;
        }

        occupants[characterId] = UnityEngine.Time.realtimeSinceStartup + MonumentOccupancyLifetimeSeconds;
    }

    private void ReleaseMonumentOccupancy(Guid characterId)
    {
        foreach (Dictionary<Guid, float> occupants in _monumentOccupants.Values)
        {
            occupants.Remove(characterId);
        }
    }

    private static float TierFalloffWeight(int gearScore, float center, float peak, float floor)
    {
        float distance = Mathf.Abs(gearScore - center);
        float falloff = Mathf.Clamp01(1f - distance / TierFalloffRadius);

        return floor + (peak - floor) * falloff;
    }

    // How wide a net to cast for a Tier 1 "road" candidate, wider than RoadSearchDetectionRadius
    // since this is a deliberate choice to explore a road, not a desperate last resort.
    private const float TierOneRoadSearchRadius = 300f;

    // Beyond this distance, a rolled destination isn't automatically accepted - see the coin-flip
    // in TryStartWithGearWeightedDestination.
    private const float LongDistanceCoinFlipThreshold = 1000f;

    // Safety cap on how many times the whole decision can be re-rolled from scratch on a
    // coin-flip "tails", purely a termination guarantee.
    private const int MaxDistanceDecisionRerolls = 20;

    /// <summary>
    /// Rolls the weighted Local/Tier1/Tier2/Tier3 choice and, if a tier other than Local wins,
    /// walks there before the normal find-container loop starts. Any rolled destination beyond
    /// LongDistanceCoinFlipThreshold isn't accepted automatically - a coin flip either commits to
    /// the trip or re-rolls the entire decision from scratch. Falls back to starting locally if
    /// the rolled tier has no candidate available, or if MaxDistanceDecisionRerolls is exhausted.
    /// </summary>
    /// <summary>
    /// Cheap existence check so the gear-weighted roll doesn't walk a survivor past loot that's
    /// already within reach. Deliberately lightweight, not the full filtered search
    /// ContinueLootTask runs - this only answers whether it's obviously worth starting right here.
    /// </summary>
    private bool HasNearbyLootWorthStartingLocally(BasePlayer npc)
    {
        return _engine.NavigationManager.TryFindNearestLootContainer(
                npc.transform.position,
                LootSearchRadius,
                out StorageContainer _,
                candidate => !IsLootTargetClaimed(candidate.net.ID)
                    && candidate.inventory != null
                    && candidate.inventory.itemList.Count > 0
                    && candidate.inventory.itemList.Any(item => !IsNeverLootItem(item.info.shortname)))
            || _engine.NavigationManager.TryFindNearestLootableCorpse(
                npc.transform.position,
                LootSearchRadius,
                out LootableCorpse _,
                candidate => !IsLootTargetClaimed(candidate.net.ID)
                    && candidate.containers != null
                    && candidate.containers.Any(c => c != null && c.itemList.Count > 0))
            || _engine.NavigationManager.TryFindNearestDroppedItemContainer(
                npc.transform.position,
                LootSearchRadius,
                out DroppedItemContainer _,
                candidate => !IsLootTargetClaimed(candidate.net.ID)
                    && candidate.inventory != null
                    && candidate.inventory.itemList.Count > 0)
            || _engine.NavigationManager.TryFindNearestDroppedItem(
                npc.transform.position,
                LootSearchRadius,
                out DroppedItem _,
                candidate => !IsLootTargetClaimed(candidate.net.ID)
                    && !IsNeverLootItem(candidate.item.info.shortname));
    }

    // Keycard-tier destination bias: a flat add-on to whichever tier's weight GetTierWeights
    // computed from gear score, favouring a monument matching a keycard the survivor already
    // holds. Stacks if a survivor holds more than one tier at once.
    private const float KeycardTierDestinationWeightBoost = 40f;

    /// <summary>
    /// Green favours Tier1, Blue favours Tier2, Red favours Tier3, matching each tier's puzzle
    /// keycard requirement. TierZero has no corresponding keycard tier, since no puzzle content
    /// lives there.
    /// </summary>
    private static (float TierZero, float Tier1, float Tier2, float Tier3) ApplyKeycardTierDestinationBias(BasePlayer npc, float tierZero, float tier1, float tier2, float tier3)
    {
        if (HasUsableKeycard(npc, KeycardTier.Green))
        {
            tier1 += KeycardTierDestinationWeightBoost;
        }

        if (HasUsableKeycard(npc, KeycardTier.Blue))
        {
            tier2 += KeycardTierDestinationWeightBoost;
        }

        if (HasUsableKeycard(npc, KeycardTier.Red))
        {
            tier3 += KeycardTierDestinationWeightBoost;
        }

        return (tierZero, tier1, tier2, tier3);
    }

    private void TryStartWithGearWeightedDestination(Survivor survivor, BasePlayer npc)
    {
        int gearScore = GetGearScore(npc);

        // This local-loot shortcut is skipped entirely once a base exists, since a based survivor
        // should be actively rolling for monuments every cycle rather than defaulting to whatever
        // roadside scrap is underfoot - the original "avoid pointless travel" reasoning for this
        // shortcut is specifically an early-spawn concern.
        if (survivor.Character.Home == null && HasNearbyLootWorthStartingLocally(npc))
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' (gear score {gearScore}) already has real loot within {LootSearchRadius:F0}m - starting right here instead of rolling a destination.");
            ContinueLootTask(survivor, new LootTaskState(), forceLocalScan: true);
            return;
        }

        for (int attempt = 0; attempt < MaxDistanceDecisionRerolls; attempt++)
        {
            (float tierZero, float tier1, float tier2, float tier3, float local) = GetTierWeights(gearScore);

            // A survivor with a base is a monument farmer, not a roadside scavenger or a local
            // wanderer (2026-10-03): its scrap/component income is what funds research, crafting
            // and base upgrades, so the low-value tiers and "just search here" are heavily damped.
            if (survivor.Character.Home != null)
            {
                tierZero *= 0.3f;
                tier1 *= 0.7f;
                local *= 0.25f;

                // Still missing a weapon / ammunition blueprint its craft goal needs: those only
                // come from looting, so monuments become priority #1 - no roadside scavenging, no
                // "search right here", and the bigger monuments (richest loot) lean heavier.
                if (HasWantedBlueprints(survivor))
                {
                    tierZero = 0f;
                    local = 0f;
                    tier1 *= 0.5f;
                    tier2 *= 1.5f;
                    tier3 *= 1.5f;
                }
                else if (IsRoamingSaturated(survivor, npc))
                {
                    // Full wood + stone: monument looting only - no roads, no "search right here".
                    tierZero = 0f;
                    local = 0f;
                }
            }
            (tierZero, tier1, tier2, tier3) = ApplyKeycardTierDestinationBias(npc, tierZero, tier1, tier2, tier3);

            float total = tierZero + tier1 + tier2 + tier3 + local;
            float roll = UnityEngine.Random.Range(0f, total);

            MonumentTier? chosenTier;

            // A survivor with a base often just heads for the monument nearest it (a higher weighted
            // roll than any other single monument gets), so bots aren't sent 3000m away at random
            // when something comparable lives close to home. Skipped while it is hunting a blueprint
            // unless that nearby monument is a high-tier one, and falls back to the normal roll when
            // the nearest monument can't give it a destination.
            Vector3 destination = default;
            string destinationLabel = null;
            bool homeMonumentPicked = false;
            MonumentTier homeMonumentTier = MonumentTier.TierZero;

            if (survivor.Character.Home != null
                && UnityEngine.Random.value < HomeMonumentRollChance
                && TryGetNearestMonumentToHome(survivor, npc, out MonumentInfo homeMonument))
            {
                homeMonumentTier = GetMonumentTier(homeMonument.name);

                if ((homeMonumentTier >= MonumentTier.TierTwo || !HasWantedBlueprints(survivor))
                    && TryPickRandomTierDestination(npc, homeMonumentTier, out destination, out destinationLabel, homeMonument))
                {
                    homeMonumentPicked = true;
                }
            }

            if (homeMonumentPicked)
            {
                chosenTier = homeMonumentTier;
            }
            else if (roll < local)
            {
                chosenTier = null;
            }
            else if ((roll -= local) < tierZero)
            {
                chosenTier = MonumentTier.TierZero;
            }
            else if ((roll -= tierZero) < tier1)
            {
                chosenTier = MonumentTier.TierOne;
            }
            else if ((roll -= tier1) < tier2)
            {
                chosenTier = MonumentTier.TierTwo;
            }
            else
            {
                chosenTier = MonumentTier.TierThree;
            }

            if (!homeMonumentPicked && (chosenTier == null || !TryPickRandomTierDestination(npc, chosenTier.Value, out destination, out destinationLabel)))
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' (gear score {gearScore}) starting its search right here.");
                ContinueLootTask(survivor, new LootTaskState(), forceLocalScan: true);
                return;
            }

            float distance = Vector3.Distance(npc.transform.position, destination);

            if (distance > LongDistanceCoinFlipThreshold)
            {
                int coinFlip = UnityEngine.Random.Range(1, 101);
                bool heads = coinFlip % 2 == 0;

                if (!heads)
                {
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' (gear score {gearScore}) rolled a {chosenTier} tier start at {destinationLabel} ({distance:F0}m) but the long-distance coin flip came up tails ({coinFlip}) - deciding again.");
                    continue;
                }

                Puts($"loot-task: '{survivor.Character.Alias}' (gear score {gearScore}) rolled a {chosenTier} tier start and WON the long-distance coin flip ({coinFlip}) - committing to {destinationLabel} ({distance:F0}m away).");
            }
            else
            {
                Puts($"loot-task: '{survivor.Character.Alias}' (gear score {gearScore}) rolled a {chosenTier} tier start - heading to {destinationLabel} ({distance:F0}m).");
            }

            RegisterMonumentOccupancy(survivor.Character.Id, destinationLabel);

            StartLongDistanceWalk(
                survivor,
                destination,
                destinationLabel,
                onArrived: () => ContinueLootTask(survivor, new LootTaskState()),
                onFailed: () =>
                {
                    // Feeds the same durable monument avoid-zone memory EscalateSearchToMonumentZone
                    // uses, so a doomed gear-weighted pick doesn't just get rolled again next task.
                    RecordPotentialAvoidZone(destination);
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach {destinationLabel} - starting its search right here instead.");
                    ContinueLootTask(survivor, new LootTaskState(), forceLocalScan: true);
                });
            return;
        }

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' (gear score {gearScore}) hit {MaxDistanceDecisionRerolls} consecutive long-distance coin-flip tails - starting its search right here instead.");
        ContinueLootTask(survivor, new LootTaskState(), forceLocalScan: true);
    }

    /// <summary>
    /// Uniform random pick among every candidate belonging to tier. For TierZero/Tier1, a nearby
    /// road counts as one candidate alongside every known monument of that tier, pooled as
    /// equal-weight candidates. Each monument contributes exactly one randomly picked zone, so a
    /// monument with more detected zones doesn't dominate the pool. No distance cap here - that's
    /// handled by the coin flip in TryStartWithGearWeightedDestination.
    /// </summary>
    /// <summary>
    /// Per-monument gear floor: Missile Silo, Military Base, and Arctic Research Base are avoided
    /// by the autonomous destination roll unless the survivor meets the required gear score. A
    /// gated-out monument can still be reached opportunistically or via a card-puzzle/keycard
    /// chain; this only removes it from the roll.
    /// </summary>
    // Explicit gear floor for Arctic Research Base, given its own specific number rather than
    // reusing the general Tier2 band minimum.
    private const int ArcticResearchBaseGearScoreMin = 35;

    private bool IsBelowRequiredGearScoreForMonument(BasePlayer npc, string monumentName)
    {
        if (monumentName.IndexOf("nuclear_missile_silo", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return GetGearScore(npc) < HighTierBandMin;
        }

        if (monumentName.IndexOf("military_base", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return GetGearScore(npc) < MediumTierBandMin;
        }

        // Launch Site had no floor at all (2026-10-03): the local server log shows gear-score 0-14
        // survivors rolling it over a hundred times, and the hosted server piled up 50+ corpses
        // there. Ghost-route-only now (see IsGhostRouteOnlyMonument), plus a real gear floor.
        if (monumentName.IndexOf("launch_site", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return GetGearScore(npc) < MediumTierBandMin;
        }

        if (monumentName.IndexOf("arctic_research_base", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return GetGearScore(npc) < ArcticResearchBaseGearScoreMin;
        }

        return false;
    }

    // How likely a survivor with a base is to simply head for the monument nearest that base on any given
    // destination roll (2026-10-03, Lucas's spec) - and, on top of that, every monument's pick weight now
    // falls off with its distance from the BASE, so bots stop running 3000m for a monument when a
    // comparable one sits close to home.
    private const float HomeMonumentRollChance = 0.35f;
    private const float HomeDistanceWeightScale = 2000f;
    private const float HomeMonumentWeightBoost = 4f;

    /// <summary>
    /// The closest eligible monument to the survivor's base: not excluded from autonomy, passes the gear
    /// floor, and actually has something to do there (loot zones, or a free ghost route for a
    /// ghost-route-only monument).
    /// </summary>
    private bool TryGetNearestMonumentToHome(Survivor survivor, BasePlayer npc, out MonumentInfo nearest)
    {
        nearest = null;
        HomeBase home = survivor.Character.Home;

        if (home == null)
        {
            return false;
        }

        float bestDistance = float.MaxValue;

        foreach (MonumentInfo monument in MonumentAccess.GetAllMonuments())
        {
            if (monument == null || IsMonumentExcludedFromAutonomy(monument) || IsBelowRequiredGearScoreForMonument(npc, monument.name))
            {
                continue;
            }

            bool usable = IsGhostRouteOnlyMonument(monument)
                ? TryGetAvailableGhostRouteStart(monument, survivor.Character.Id, out Vector3 _)
                : _monumentLootZones.TryGetValue(monument.name, out List<MonumentLootZone> zones) && zones.Count > 0;

            if (!usable)
            {
                continue;
            }

            float distance = Vector3.Distance(monument.transform.position, home.Position);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = monument;
            }
        }

        return nearest != null;
    }

    private bool TryPickRandomTierDestination(BasePlayer npc, MonumentTier tier, out Vector3 destination, out string label, MonumentInfo onlyMonument = null)
    {
        List<(Vector3 Position, string Label)> candidates = new();

        if (onlyMonument == null && (tier == MonumentTier.TierZero || tier == MonumentTier.TierOne)
            && _engine.NavigationManager.TryFindNearestRoadPoint(npc.transform.position, TierOneRoadSearchRadius, out Vector3 roadPoint, out _, out _))
        {
            candidates.Add((roadPoint, "a nearby road"));
        }

        if (MonumentAccess.GetAllMonuments().Count > 0)
        {
            foreach (MonumentInfo monument in MonumentAccess.GetAllMonuments())
            {
                if (monument == null || IsMonumentExcludedFromAutonomy(monument)
                    || (onlyMonument != null ? monument != onlyMonument : GetMonumentTier(monument.name) != tier))
                {
                    continue;
                }

                if (IsBelowRequiredGearScoreForMonument(npc, monument.name))
                {
                    continue;
                }

                // Ghost-route-only monuments (Launch Site): the destination IS a free authored
                // route's start point, and the monument drops out of the roll entirely when every
                // route is held or recently run - never a loot zone to wander into.
                if (IsGhostRouteOnlyMonument(monument))
                {
                    Survivor rollingSurvivor = FindSurvivorByPlayer(npc);

                    if (rollingSurvivor != null && TryGetAvailableGhostRouteStart(monument, rollingSurvivor.Character.Id, out Vector3 routeStart))
                    {
                        candidates.Add((routeStart, monument.name));
                    }

                    continue;
                }

                if (!_monumentLootZones.TryGetValue(monument.name, out List<MonumentLootZone> zones) || zones.Count == 0)
                {
                    continue;
                }

                // Skips any zone the avoid-zone system has confirmed unreachable, otherwise a
                // fresh roll could land right back on a known-doomed zone.
                List<MonumentLootZone> reachableZones = zones
                    .Where(candidate => !IsInMonumentAvoidZone(monument.transform.TransformPoint(candidate.LocalOffset)))
                    .ToList();

                if (reachableZones.Count == 0)
                {
                    continue;
                }

                MonumentLootZone zone = reachableZones[UnityEngine.Random.Range(0, reachableZones.Count)];
                Vector3 zoneWorldPosition = monument.transform.TransformPoint(zone.LocalOffset);

                candidates.Add((zoneWorldPosition, monument.name));
            }
        }

        if (candidates.Count == 0)
        {
            destination = Vector3.zero;
            label = null;
            return false;
        }

        // Occupancy-weighted pick: a crowded monument's weight shrinks per occupant but never
        // reaches zero; a road is never crowded.
        int occupancyCap = GetMonumentOccupancyCap(tier);
        float[] weights = new float[candidates.Count];
        float totalWeight = 0f;

        // A survivor with a base prefers monuments close to it: weight falls off with the candidate's
        // distance from the BASE, and the monument nearest the base gets a boost on top.
        Survivor pickingSurvivor = FindSurvivorByPlayer(npc);
        HomeBase pickingHome = pickingSurvivor?.Character.Home;
        string nearestHomeMonumentName = pickingHome != null && TryGetNearestMonumentToHome(pickingSurvivor, npc, out MonumentInfo nearestToHome)
            ? nearestToHome.name
            : null;

        for (int i = 0; i < candidates.Count; i++)
        {
            int occupants = candidates[i].Label == "a nearby road" ? 0 : GetMonumentOccupancy(candidates[i].Label);
            weights[i] = Mathf.Max(0.05f, 1f - occupants / (float)occupancyCap);

            if (pickingHome != null && candidates[i].Label != "a nearby road")
            {
                weights[i] /= 1f + Vector3.Distance(candidates[i].Position, pickingHome.Position) / HomeDistanceWeightScale;

                if (candidates[i].Label == nearestHomeMonumentName)
                {
                    weights[i] *= HomeMonumentWeightBoost;
                }
            }

            totalWeight += weights[i];
        }

        float roll = UnityEngine.Random.Range(0f, totalWeight);
        int chosenIndex = candidates.Count - 1;

        for (int i = 0; i < candidates.Count; i++)
        {
            if ((roll -= weights[i]) <= 0f)
            {
                chosenIndex = i;
                break;
            }
        }

        (Vector3 Position, string Label) chosen = candidates[chosenIndex];
        destination = chosen.Position;
        label = chosen.Label;
        return true;
    }
}
