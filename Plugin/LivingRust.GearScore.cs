using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Gear-based monument tiering (2026-08-15) - the decision layer behind
/// "which monument should this survivor head toward first," built across
/// several rounds of explicit spec with Lucas: a 0-100 gear score
/// (weapon tier + armor tier, normalized), three monument tiers each with
/// their own real, named monuments, and a WEIGHTED RANDOM roll (not a
/// deterministic "always pick nearest") driving the actual choice - the
/// same "roll a range, don't hardcode one answer" philosophy already used
/// for burst-fire counts in LivingRust.Combat.cs. Lucas's own framing:
/// "randomized visit monument will alleviate contested container issues"
/// and stops 200 beach spawns all converging on whatever's nearest.
/// </summary>
public partial class LivingRust
{
    /// <summary>
    /// Best-owned-weapon score, 0-85 - Lucas's own explicit numbers,
    /// refined across two rounds of live discussion (LR300 raised to match
    /// AK, M16A2 dropped below both, Python raised above HCR, MP5 pulled
    /// below the full-rifle tier). Deliberately a flat lookup, not derived
    /// from WeaponPriority's own rank order - that list is about "which
    /// weapon looks best on the belt," a different, finer-grained ranking
    /// than "how dangerous is this weapon for monument-difficulty
    /// purposes," which only needs Lucas's own tier buckets. Anything not
    /// listed (thrown weapons, melee) scores 0 here - this is specifically
    /// a RANGED-combat-readiness score.
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
    /// Best owned weapon's score (0 if unarmed or the best owned weapon
    /// isn't in WeaponGearScore at all - krieg.shotgun/shotgun.waterpipe/
    /// pistol.semiauto.a.m15/t1_smg/crossbowbowless/speargun/blowpipe/
    /// pistol.eoka weren't given explicit numbers by Lucas, so they fall
    /// back to 0 rather than a guessed value - worth naming explicitly if
    /// any of those turn out to matter in practice). shotgun.spas12 = 40,
    /// added 2026-08-15 after being missed across the earlier revision
    /// rounds - a real /lr.spawn.spas12 kit was silently scoring as if
    /// unarmed until this.
    /// Reuses WeaponPriority's own ordering (already-established "best
    /// weapon this survivor owns" ranking) purely to PICK which single
    /// weapon to score, not for the score itself.
    /// </summary>
    private static int GetWeaponGearScore(BasePlayer npc)
    {
        // Belt only, not main inventory (2026-08-15, Lucas's own explicit
        // correction) - a spare AK sitting unbelted in the main inventory
        // isn't actually "carried" the way a real player's readily-
        // accessible loadout is; only what's equipped/on the toolbelt
        // should count.
        Item best = FindBestByPriority(npc.inventory.containerBelt.itemList, WeaponPriority, exclude: null);

        if (best == null)
        {
            return 0;
        }

        return WeaponGearScore.TryGetValue(best.info.shortname, out int score) ? score : 0;
    }

    /// <summary>
    /// Worn-armor score, 0-100 - average of the BEST TWO worn pieces'
    /// GetArmorTier×20 values (missing pieces below 2 count as 0, so one
    /// great item and nothing else still gets penalized for real
    /// incompleteness). Changed from "sum over all worn, divide by an
    /// assumed 3-piece kit" (2026-08-15, live report: "the armour is
    /// dropping the overall scores") - the real root cause: MetalPlate
    /// tier has NO real legwear item at all (only metal.facemask/
    /// metal.plate.torso exist), so a maxed-out metal-plate bot could
    /// only ever fill 2 of the /3 formula's assumed 3 slots, capping it
    /// around 53 regardless of how well-geared it actually was. Averaging
    /// over the best 2 instead means a tier that only offers 2 real
    /// pieces (MetalPlate) isn't structurally penalized for a 3rd slot
    /// that doesn't exist, while a tier that DOES offer 3 (Roadsign:
    /// jacket/gloves/kilt) still only gets credit for its best 2 either
    /// way - matches Lucas's own worked examples: full metal plate (2
    /// pieces) + AK/LR300/MP5 landing ~80-90 overall, Thompson + partial
    /// roadsign (gloves+kilt, 2 pieces) landing ~high 60s.
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

    // Bows/crossbows score in WeaponGearScore but real combat only supports
    // BaseProjectile firearms right now (StartCombat's own
    // GetHeldEntity() is BaseProjectile check) - melee/bow combat is still
    // unimplemented (Lucas's own explicit framing, 2026-08-15). Owning one
    // of these doesn't mean the bot can actually fight back yet, so
    // HasReadyRangedWeapon below excludes them.
    private static readonly string[] NonCombatCapableRangedWeaponShortnames =
    {
        "bow.compound", "bow.hunting", "crossbow", "minicrossbow",
    };

    /// <summary>
    /// Whether npc owns a real firearm (belt, same WeaponPriority ordering
    /// GetWeaponGearScore already uses to pick "the" weapon) AND has at
    /// least one round of matching ammo anywhere in its inventory. Backs
    /// the scientist-loot-avoidance check (LivingRust.Looting.cs) - Lucas's
    /// own explicit spec (2026-08-15): only skip looting near a visible
    /// hostile scientist while genuinely unable to fight back.
    /// </summary>
    private bool HasReadyRangedWeapon(BasePlayer npc)
    {
        // Bows count once they have arrows (2026-09-21) - the comment above
        // predates working bow combat (bots fire bow.hunting live now), and
        // an archer with arrows shouldn't avoid scientist areas as if
        // unarmed.
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
    /// Real melee weapons only - Lucas's own explicit examples "cleaver,
    /// mace, sword, paddle, etc" (2026-08-15), kept deliberately separate
    /// from pickaxes/hatchets (their own +2 bonus below) even though both
    /// live in the same pre-existing MeleeToolPriority list. Built by
    /// excluding PickaxeFamily/HatchetFamily/rock from that list rather
    /// than a fresh hand-typed one, so it can't silently drift out of sync
    /// with the real, already-validated melee item roster. A computed
    /// property, not a static-initialized field - MeleeToolPriority/
    /// PickaxeFamily/HatchetFamily live in a DIFFERENT partial-class file
    /// (LivingRust.Looting.cs), and C# doesn't guarantee static field
    /// initializer order across files of the same partial class, so a
    /// field initializer here could run before those are populated.
    /// </summary>
    private static string[] MeleeWeaponBonusShortnames => MeleeToolPriority
        .Except(PickaxeFamily)
        .Except(HatchetFamily)
        .Except(new[] { "rock" })
        .ToArray();

    /// <summary>
    /// Ammo/meds/tools sustain bonus (2026-08-15, Lucas's own explicit
    /// numbers) - added directly on top of the weapon+armor average, not
    /// blended into it, since this represents a genuinely separate signal
    /// ("can this bot actually sustain a fight/trip," not "how good is its
    /// current loadout"). Counts across the WHOLE inventory (main + belt),
    /// unlike GetWeaponGearScore's belt-only scope - reserves in the main
    /// inventory are exactly the point here, not something to exclude the
    /// way an unbelted spare weapon is.
    /// syringe.medical: +2 each. bandage: +0.5 each. largemedkit: +1 each.
    /// Ammo (5.56/pistol, any real variant): +2 per 64 rounds
    /// (fractional - 32 rounds is +1, not rounded away). Pickaxe/hatchet
    /// (any family variant, salvaged included): flat +2 if at least one is
    /// owned, not scaled by count. Real melee weapon: flat +2 if at least
    /// one is owned, same non-scaling reasoning.
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

        // Keycards (2026-08-15, Lucas's own explicit numbers) - flat per
        // colour owned, same non-scaling-by-quantity reasoning as the
        // pickaxe/hatchet/melee bonuses above (a second green card doesn't
        // mean anything a first one didn't already). Real shortnames
        // confirmed via Bundles\items\*.json. Owning multiple colours
        // stacks (a red card implies genuine progress, but doesn't
        // preclude also holding a green/blue one), so this can add up to
        // +30 total.
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
    /// Combined 0-100 gear score - Lucas's own explicit example shape,
    /// 2026-08-15: "50 is medium tier floor... scales medium up until 75,
    /// high tier begins at 76." Weapon and armor components (both already
    /// independently 0-100) are simple-averaged, then the sustain bonus
    /// (ammo/meds/tools, GetSustainGearScore) is added on top and the
    /// whole thing clamped back to 0-100 - the sustain bonus is
    /// deliberately uncapped on its own (a bot with huge stockpiles can
    /// genuinely push the total up meaningfully), but the final score
    /// still needs to stay inside the gear-score bands' own 0-100 range or
    /// GetTierWeights' band-matching would silently stop matching High
    /// entirely above 100.
    /// </summary>
    private static int GetGearScore(BasePlayer npc)
    {
        float baseScore = (GetWeaponGearScore(npc) + GetArmorGearScore(npc)) / 2f;
        float sustainBonus = GetSustainGearScore(npc);

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
    /// Real monument name substrings per Lucas's own explicit tier
    /// restructure, 2026-08-19 - tiers are now defined by real, CONFIRMED
    /// keycard/puzzle requirements rather than name-based guesses:
    ///   Tier 0 - no keycard/puzzle at all (confirmed live: Oxum's Gas
    ///     Station, Abandoned Supermarket, Mining Outpost genuinely have no
    ///     locked room; Junkyard confirmed the same 2026-08-19 - moved here
    ///     from its old "stays Tier 1 as a deliberate exception" spot now
    ///     that an explicit no-puzzle tier actually exists; Lighthouse
    ///     included for whenever its own AutonomyExcludedMonumentSubstrings
    ///     exclusion lifts).
    ///   Tier 1 - requires a green keycard (accessLevel 1) + fuse. This is
    ///     also the fallback default for anything NOT listed in any tier
    ///     list below (warehouse, radtown_1, water_well_a,
    ///     jungle_ziggurat_a, power substations, or any monument type not
    ///     yet scanned/known) - unconfirmed monuments default here rather
    ///     than to Tier 0, since "no puzzle at all" is the narrower, more
    ///     specific claim and should only apply to monuments actually
    ///     confirmed that way.
    ///   Tier 2 - requires accessLevel 2 (blue), with real per-monument
    ///     variation Lucas confirmed 2026-08-19: Powerplant needs
    ///     green+blue, Water Treatment Plant needs ONLY blue+fuse (no
    ///     green), Arctic Research Base needs ONLY blue (no fuse at all).
    ///     Airfield/Trainyard classified here on Lucas's own word ahead of
    ///     their own puzzles being traced/confirmed - correct later if a
    ///     live scan proves otherwise, same pattern Powerplant's own blue
    ///     confirmation followed.
    ///   Tier 3 - unchanged (Nuclear Missile Silo, Launch Site, Oil Rig,
    ///     Military Tunnel's green+blue+red).
    /// Matched against MonumentInfo.name (a full prefab path, e.g.
    /// ".../monument/medium/nuclear_missile_silo.prefab") via substring,
    /// same approach RequiresDestructionToLoot/IsRoadsign/etc already use.
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
        "underwater_lab", // added 2026-08-15
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
    /// Monument types autonomous survivors should never scan/select as a
    /// loot destination, matched the same substring-against-MonumentInfo.
    /// name way as the tier lists above. Lighthouse sits on its own small
    /// island (no land route) and Underwater Lab's entrance is submerged -
    /// both require real ocean/water movement this project doesn't have
    /// yet, parked for later (2026-08-15, Lucas's own explicit scoping:
    /// "avoid... for now, we can implement water/ocean movement later").
    /// apartments_complex added same session, no reason given - Lucas's
    /// own explicit request, parked pending whatever prompted it. Ranch,
    /// barn ("barn"), Outpost (real internal name is "compound"), Bandit
    /// Camp ("bandit"), and Fishing Village small/large (both share
    /// "fishing_village") added 2026-08-15 - Lucas's own explicit reason:
    /// these are all safezones, genuinely unlootable, so there's no point a
    /// survivor ever heading there for loot (this is also what was causing
    /// the Ranch bot pileup traced the same session - the fix there stays
    /// in place, but excluding these monuments from autonomy entirely is
    /// the real, correct fix since bots had no business going there at
    /// all). Purely an autonomy exclusion - debug/admin lookup commands
    /// (/lr.monument.where, /lr.debug.scanmonumentloot, etc.) deliberately
    /// still work against any of these by name, since an admin asking for
    /// one by name isn't the same as a bot wandering there on its own.
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

        // launch_site added 2026-09-01 (live report: 233 of 248 recent
        // deaths were "Cannon" - Bradley APC, which patrols Launch Site -
        // and the only real Bradley-flee check in the whole project is
        // scoped to ghost-route monument scanning specifically, so a bot
        // just doing normal looting/checklist/base-gathering had ZERO
        // protection walking in. Lucas's own explicit call: "hard avoid
        // launch site as a pre-rolled check... they shouldn't be going
        // into launch site anyways realistically" - simpler and more
        // direct than teaching every other task type about Bradley
        // detection too. A real tier3 monument (HighTierMonumentSubstrings
        // still classifies it that way for whatever else reads tier), just
        // never chosen as an autonomous destination or wandered into
        // opportunistically. Deliberately NOT also added to
        // FullyAvoidedMonumentNameSubstrings (LivingRust.MonumentAvoidZones.cs) -
        // Lucas's own explicit narrowing: "only avoid it during the initial
        // roll the dice 'go inland'... if they venture there and die, it's
        // on them." A bot that later wanders near/into Launch Site
        // opportunistically (chasing loot, fleeing, etc) takes its chances,
        // unlike the other entries in this list which ARE also hard-avoided
        // everywhere.
        "launch_site",

        // Added 2026-08-18, Lucas's own explicit call after reviewing the
        // full scanned monument list for this map - "stables" is another
        // real safezone (same reasoning as ranch/barn/compound above), and
        // the rest are terrain/scenery features with no real loot worth a
        // survivor detouring for: swamp_a/b/c AND ue_jungle_swamp_a (both
        // caught by the single "swamp" substring), ice_lake_1/4, ue_lake_a,
        // ue_oasis_a, and water_well_a-e (just a water source, no
        // containers at all). Cave variants deliberately NOT added here -
        // Lucas chose to just not pursue them for now (no scaffolding/
        // registry work happened for them either), not to actively exclude
        // them from autonomy the way these are.
        "stables",
        "swamp",
        "ice_lake",
        "ue_lake",
        "oasis",
        "water_well",

        // Added 2026-08-19, Lucas's own explicit call - Oil Rig (small
        // "oilrig_1" and large "oilrig_2", both caught by "oilrig") is
        // registered (MonumentGhostRouteFolders, LivingRust.Looting.cs) and
        // already Tier 3 (HighTierMonumentSubstrings above), but real
        // prerequisites this project doesn't have yet - boat travel to
        // reach it at all, fending off RHIB-mounted scientist NPCs once
        // there - mean it's deliberately parked out of autonomy for now.
        // Lucas's own framing: "a whole lot of test -> fix -> test -> fix
        // back and forth" better tackled as its own dedicated pass later,
        // not half-built alongside everything else. Same debug/admin
        // lookup exception as every other entry here - only autonomous
        // rolling is blocked.
        "oilrig",
    };

    private static bool IsMonumentExcludedFromAutonomy(string monumentName)
    {
        return !string.IsNullOrEmpty(monumentName)
            && AutonomyExcludedMonumentSubstrings.Any(substring => monumentName.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// Real fix (2026-09-01, live report: bots STILL piling up 20-40 deep
    /// at "Ranch" despite it already being listed in
    /// AutonomyExcludedMonumentSubstrings) - confirmed via the game's own
    /// full monument prefab manifest that there is NO "ranch"/"barn"/"farm"
    /// prefab anywhere in it, so the plain-string overload above (matched
    /// only against MonumentInfo.name, the internal prefab path) almost
    /// certainly never had anything to match in the first place - same
    /// display-name-vs-internal-name gap Bandit Camp ("bandit_town") and
    /// Outpost ("compound") already needed their real internal names
    /// reverse-engineered for, except whoever added "ranch" never
    /// confirmed its real internal name the same way. Rather than guess
    /// again, this checks the real player-facing display name too
    /// (MonumentInfo.displayPhrase.english) - the same dual-check
    /// FindAllMonumentMatches (/lr.monument.where's own real lookup,
    /// NavigationManager.cs) already uses - so "Ranch" matches correctly
    /// regardless of whatever its actual internal prefab name turns out to
    /// be.
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
    /// Deliberately OVERLAPPING gear-score bands. Restructured 2026-08-19
    /// (Lucas's own explicit spec, alongside the Tier 0-3 monument
    /// restructure above) to a 4-tier shape: Tier 0 0-20, Tier 1 10-33,
    /// Tier 2 25-65, Tier 3 58-100 unchanged. A score sitting in an overlap
    /// window (0-20 spans into Tier 1, 10-20; 25-33 spans Tier 1/Tier 2;
    /// 58-65 spans Tier 2/Tier 3) matches multiple bands at once, and
    /// GetTierWeights below blends every matching band's weight profile
    /// together via the same continuous falloff curve rather than picking
    /// just one - Lucas's own original framing (2026-08-15, preserved
    /// through this restructure): "a bot can variably still roll a higher
    /// tier, or a tier below it but has a smaller percentage to... adds
    /// even more randomness."
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
    /// Integrates the discrete bands above with the continuous-falloff
    /// idea discussed earlier (2026-08-15, Lucas's own explicit request:
    /// "can we integrate both?") - each tier's weight is now a smooth
    /// triangular falloff from its own band-derived center, floor + (peak
    /// - floor) * (1 - distance/TierFalloffRadius), clamped so it never
    /// goes negative past the radius. Replaces the old flat "which bands
    /// does this score match, average their weights" step-function
    /// version - same overlap BEHAVIOUR (a score between two tiers'
    /// centers gets meaningful weight in both) but genuinely continuous
    /// now instead of a hard 0/1 per band, and every gear score - not just
    /// ones inside an overlap window - gets its own uniquely graduated
    /// weight rather than one of only 3 possible flat outcomes. Floors
    /// keep every tier reachable at ANY gear score (never truly zero,
    /// matching "still visitable, just lower priority") - Tier2's floor is
    /// higher than Tier1/Tier3's since Medium is the real middle ground,
    /// plausible from either direction.
    ///
    /// Floors lowered 2026-08-15 (8/15/5 -> 4/4/2) - live report: a
    /// completely naked gear-score-0 survivor rolled Medium tier (real log
    /// evidence: '(gear score 0) rolled a Medium tier start - heading to
    /// radtown_small_3... (1710m)') and headed to powerplant on a separate
    /// occasion. At the old floor=15, Medium's share of a naked bot's total
    /// weight was ~19% - not the rare exception "still visitable, just
    /// lower priority" was meant to describe. The new floors keep every
    /// tier reachable (never truly zero) but at a genuinely small share
    /// (~5-7%) for a badly-mismatched score, matching what "still
    /// possible, but should be uncommon" actually implies.
    /// </summary>
    private static (float TierZero, float Tier1, float Tier2, float Tier3, float Local) GetTierWeights(int gearScore)
    {
        // TierZero's peak/floor deliberately mirror Tier1's (2026-08-19,
        // no explicit numbers given for this brand-new tier) - it's the
        // immediately adjacent, equally low-commitment bracket, so the same
        // shape that was already tuned for Tier1 is the safest starting
        // point rather than inventing new untested numbers.
        // Gear-independent, monument-favouring (2026-09-21, Lucas's own
        // explicit spec): loot density at monuments is far higher than on
        // roads, and gear score should only gate the card PUZZLE itself
        // (see TryStartCardPuzzleDetour) - a bot can still walk a
        // monument's ghost-route loot path regardless of gear. gearScore is
        // kept as a parameter for the existing call sites. Tier0/Tier1 are
        // the road-heavy tiers, so they're the ones cut back.
        return (TierZero: 10f, Tier1: 30f, Tier2: 40f, Tier3: 40f, Local: 12f);
    }

    // Soft monument occupancy cap (2026-09-21, Lucas's own explicit
    // request: "monuments should have occupancy"). Bots register when they
    // commit to a monument destination; a registration lapses after
    // MonumentOccupancyLifetimeSeconds (a realistic trip length) or when the
    // bot dies. Each current occupant lowers that monument's pick weight
    // (never to zero - real players avoid a crowd, but still sometimes
    // collide, which is where the good bot-vs-bot fights happen).
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

    // How wide a net to cast for a Tier 1 "road" candidate - deliberately
    // much wider than RoadSearchDetectionRadius (60m, that one's a
    // desperate last-resort check), since this is a genuine deliberate
    // choice to go explore a road, not a fallback from having nothing else
    // left. Wide enough that a beach spawn has real candidates without
    // being an unbounded map-wide search.
    private const float TierOneRoadSearchRadius = 300f;

    /// <summary>
    /// Beyond this distance, a rolled destination isn't automatically
    /// accepted - see the coin-flip in TryStartWithGearWeightedDestination.
    /// Real players decide "do I want to travel halfway across the map for
    /// this" consciously, not automatically - Lucas's own framing,
    /// 2026-08-15.
    /// </summary>
    private const float LongDistanceCoinFlipThreshold = 1000f;

    /// <summary>
    /// Safety cap on how many times the whole decision (tier roll AND
    /// destination pick) can be re-rolled from scratch on a coin-flip
    /// "tails" - purely a termination guarantee (a genuinely infinite loop
    /// is only a statistical near-impossibility, not actually impossible),
    /// not a real gameplay number Lucas asked for.
    /// </summary>
    private const int MaxDistanceDecisionRerolls = 20;

    /// <summary>
    /// Rolls the weighted Local/Tier1/Tier2/Tier3 choice (GetTierWeights)
    /// and, if a tier other than Local wins, walks there BEFORE the normal
    /// find-container loop starts - a deliberate "here's where I'm
    /// starting my search today" decision, made once per fresh loot task,
    /// not an escalation reacting to failure the way
    /// EscalateSearchToMonumentZone/EscalateSearchToKnownMonument are.
    ///
    /// Long-distance coin flip (2026-08-15, Lucas's own explicit spec,
    /// replacing an earlier hard per-tier distance cap that turned out to
    /// be the wrong shape): any rolled destination beyond
    /// LongDistanceCoinFlipThreshold (1000m) isn't accepted automatically -
    /// flip a 100-sided "coin" (even = heads = commit to the trip, odd =
    /// tails = throw the ENTIRE decision away and re-roll a fresh tier +
    /// destination from scratch, not just a new destination within the
    /// same tier). Lucas's own framing: this replicates a real player
    /// consciously weighing "do I want to travel halfway across the map
    /// for this" rather than a hardcoded "go to whatever's within X
    /// distance" rule - the randomness genuinely has no ceiling, a 3000m
    /// trip is always possible, it just needs to survive real (repeated)
    /// chance rather than being silently excluded from candidacy the way
    /// the old hard cap did.
    ///
    /// Falls back to starting locally if the rolled tier has no real
    /// candidate available at all (e.g. Tier 3 rolled but no high-tier
    /// monument has been scanned on this map), or if MaxDistanceDecisionRerolls
    /// is exhausted (an extreme, near-impossible run of consecutive tails).
    /// </summary>
    /// <summary>
    /// Cheap existence check (2026-08-15, real live bug: 'FilthyMarauder'
    /// spawned a gear score of 87 right next to real corpses at Launch
    /// Site, rolled a High-tier destination, and walked ~40s across the
    /// SAME monument to a different zone while the corpses it started next
    /// to sat untouched) - the gear-weighted roll had zero awareness of
    /// what's already within reach before committing to a destination.
    /// Deliberately lightweight, not the real full-filtered search
    /// ContinueLootTask itself runs (no avoid-zone/hostile-scientist/
    /// poisoned-zone checks) - this only answers "is it obviously worth
    /// looting right here instead of walking somewhere else," the actual
    /// loot decision still goes through the real filtered search once
    /// ContinueLootTask takes over.
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

    // Keycard-tier destination bias (2026-09-15, Lucas's own explicit ask:
    // "if a bot... holds a tiered keycard... it then decides based off
    // keycard tier what monument to favour when rolling its dice"). Flat
    // add-on to whichever tier's weight GetTierWeights already computed
    // from gear score - large enough to meaningfully favour actually using
    // the keycard it's carrying (comparable to that tier's own peak weight,
    // 50f) without making it deterministic; gear score, the Local option,
    // and every other tier's own weight are untouched. Stacks naturally if
    // a survivor holds more than one tier at once (e.g. green+blue for
    // Airfield) - each owned tier gets its own independent boost.
    private const float KeycardTierDestinationWeightBoost = 40f;

    /// <summary>
    /// Green->Tier1/Low, Blue->Tier2/Medium, Red->Tier3/High - matches
    /// CardPuzzleRouteFolders' own real keycard-tier-to-monument mapping
    /// (LivingRust.CardPuzzles.cs): every Low-tier puzzle monument
    /// (Harbor/Satellite Dish/Radtown) needs Green, every Medium-tier one
    /// (Powerplant/Water Treatment/Arctic Research Base/Trainyard) needs
    /// Blue, every High-tier one (Launch Site/Nuclear Missile Silo/Airfield)
    /// needs Red somewhere along it. TierZero deliberately has no
    /// corresponding keycard tier - it's the "barely committing, nearly
    /// Local" bracket, no real puzzle content lives there.
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

        if (HasNearbyLootWorthStartingLocally(npc))
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' (gear score {gearScore}) already has real loot within {LootSearchRadius:F0}m - starting right here instead of rolling a destination.");
            ContinueLootTask(survivor, new LootTaskState(), forceLocalScan: true);
            return;
        }

        for (int attempt = 0; attempt < MaxDistanceDecisionRerolls; attempt++)
        {
            (float tierZero, float tier1, float tier2, float tier3, float local) = GetTierWeights(gearScore);
            (tierZero, tier1, tier2, tier3) = ApplyKeycardTierDestinationBias(npc, tierZero, tier1, tier2, tier3);

            float total = tierZero + tier1 + tier2 + tier3 + local;
            float roll = UnityEngine.Random.Range(0f, total);

            MonumentTier? chosenTier;

            if (roll < local)
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

            if (chosenTier == null || !TryPickRandomTierDestination(npc, chosenTier.Value, out Vector3 destination, out string destinationLabel))
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
                    // Same durable monument avoid-zone memory
                    // EscalateSearchToMonumentZone's own onFailed feeds -
                    // without this, a doomed gear-weighted pick (e.g. a
                    // loot zone sitting right at an unreachable shoreline)
                    // would just get rolled again on the bot's very next
                    // fresh task, looping forever (confirmed live:
                    // 'SilentHunter' at power_sub_small_2).
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
    /// Uniform random pick among every real candidate belonging to tier -
    /// for TierZero/Tier1 (the two lowest-commitment brackets), a nearby
    /// road counts as one candidate alongside every known monument of that
    /// tier (Lucas's own explicit "pooled together as equal-weight
    /// candidates" framing, not roads ranked separately below monuments) -
    /// extended from Tier1-only to also cover the new TierZero 2026-08-19,
    /// since TierZero now fills the exact "nothing much going on, just a
    /// low-commitment wander" role Tier1/Low originally had alone. Each
    /// monument contributes exactly one of its own known zones (itself
    /// randomly picked), not one candidate per zone - a monument that
    /// happens to have more detected zones shouldn't dominate the random
    /// pool just for that reason. No distance cap here at all (an earlier
    /// per-tier radius was tried and removed the same day) - long-distance
    /// picks are handled entirely by the coin flip in
    /// TryStartWithGearWeightedDestination instead, not by excluding far
    /// candidates from ever being considered.
    /// </summary>
    private bool TryPickRandomTierDestination(BasePlayer npc, MonumentTier tier, out Vector3 destination, out string label)
    {
        List<(Vector3 Position, string Label)> candidates = new();

        if ((tier == MonumentTier.TierZero || tier == MonumentTier.TierOne)
            && _engine.NavigationManager.TryFindNearestRoadPoint(npc.transform.position, TierOneRoadSearchRadius, out Vector3 roadPoint, out _, out _))
        {
            candidates.Add((roadPoint, "a nearby road"));
        }

        if (TerrainMeta.Path != null && TerrainMeta.Path.Monuments != null)
        {
            foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
            {
                if (monument == null || IsMonumentExcludedFromAutonomy(monument) || GetMonumentTier(monument.name) != tier)
                {
                    continue;
                }

                if (!_monumentLootZones.TryGetValue(monument.name, out List<MonumentLootZone> zones) || zones.Count == 0)
                {
                    continue;
                }

                // Skip any zone the avoid-zone system has confirmed
                // unreachable (see EscalateSearchToMonumentZone's own doc
                // comment on this same check) - otherwise a fresh roll
                // could still land right back on a known-doomed zone.
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

        // Occupancy-weighted pick (see GetMonumentOccupancyCap): a crowded
        // monument's weight shrinks per occupant but never reaches zero;
        // a road (no monument name) is never crowded.
        int occupancyCap = GetMonumentOccupancyCap(tier);
        float[] weights = new float[candidates.Count];
        float totalWeight = 0f;

        for (int i = 0; i < candidates.Count; i++)
        {
            int occupants = candidates[i].Label == "a nearby road" ? 0 : GetMonumentOccupancy(candidates[i].Label);
            weights[i] = Mathf.Max(0.05f, 1f - occupants / (float)occupancyCap);
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
