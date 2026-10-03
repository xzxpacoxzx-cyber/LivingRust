using LivingRust.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Facepunch;
using LivingRust.Models;
using Oxide.Plugins;
using Rust;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Coordinates home-site selection and base-building strategy: a fresh survivor rolls Coastal or
/// Inland, runs the primitive checklist, then gathers toward and builds a rolled base design
/// before settling in.
/// </summary>
public partial class LivingRust
{
    // Time limit for the primitive checklist before priority switches to base-gathering.
    private const float PrimitiveGoalTimeLimitSeconds = 900f;

    // Chance a fresh survivor rolls to head inland instead of staying coastal.
    private const float HomeSiteInlandRollChance = 0.75f;

    // Chance a fresh survivor rushes a monument first, before its checklist/home-site rolls,
    // routing it into the existing gear-weighted destination roll early. See
    // RollMonumentRushIfFreshLife for how the rush concludes.
    private const float MonumentRushChance = 0.25f;

    // Safety-net deadline for a monument rush that never naturally concludes (e.g. the roll landed
    // on a road or local search instead of an actual monument), so a survivor isn't stuck rushing
    // indefinitely.
    private const float MonumentRushTimeLimitSeconds = 900f;

    private readonly HashSet<Guid> _hasRolledMonumentRush = new();
    private readonly HashSet<Guid> _pursuingMonumentRushGoal = new();
    private readonly Dictionary<Guid, float> _monumentRushDeadline = new();

    // Chance, GIVEN a fresh life already rolled into a monument rush, that it's the more extreme
    // "immediate" variant: skip the full primitive checklist (sleeping bag/bow/arrows/bandages) in
    // favor of gathering only a stone hatchet + stone pickaxe first, then go straight for the
    // monument - 2026-10-03, Lucas's own explicit spec. 10% of the 25% that roll a rush at all,
    // so 2.5% of all fresh lives.
    private const float ImmediateMonumentRushChance = 0.10f;

    // This-life flag: true once a rushing survivor's nested roll lands on the immediate variant.
    // Reset on death along with the other once-per-life rush flags (see LivingRust.Hooks.cs) -
    // a death during the attempt is handled separately via _disqualifiedFromMonumentRush below,
    // not by this flag surviving the reset.
    private readonly HashSet<Guid> _isImmediateMonumentRush = new();

    // Still gathering its minimal hatchet+pickaxe kit before departing for the monument.
    private readonly HashSet<Guid> _pursuingImmediateRushTools = new();

    // Permanent - never reset on death. A survivor that dies anywhere during an immediate rush
    // attempt (tool-gathering, travel, or at the monument itself) never rolls ANY monument rush
    // again for the rest of its existence and instead always runs the normal full checklist and
    // priority ladder, matching Lucas's own explicit spec: "it respawns and joins the rest of the
    // 75% of bots trying to do everything else."
    private readonly HashSet<Guid> _disqualifiedFromMonumentRush = new();

    /// <summary>
    /// Once-per-life roll deciding whether a survivor should rush a monument before its checklist
    /// and home-site rolls. Returns true while the rush is still in progress (including the
    /// immediate variant's tool-gathering lead-in), and clears either on a genuine monument
    /// completion or once the safety-net deadline expires.
    /// </summary>
    private bool RollMonumentRushIfFreshLife(Survivor survivor)
    {
        Guid characterId = survivor.Character.Id;

        if (_disqualifiedFromMonumentRush.Contains(characterId))
        {
            return false;
        }

        if (_pursuingImmediateRushTools.Contains(characterId))
        {
            return true;
        }

        if (_pursuingMonumentRushGoal.Contains(characterId))
        {
            if (UnityEngine.Time.realtimeSinceStartup < _monumentRushDeadline.GetValueOrDefault(characterId))
            {
                return true;
            }

            _pursuingMonumentRushGoal.Remove(characterId);
            _monumentRushDeadline.Remove(characterId);
            Puts($"monument-rush: '{survivor.Character.Alias}' hit its {MonumentRushTimeLimitSeconds:F0}s safety-net deadline without finishing a real monument - falling back to its normal checklist/base-building priority.");
            return false;
        }

        if (_hasRolledMonumentRush.Contains(characterId))
        {
            return false;
        }

        _hasRolledMonumentRush.Add(characterId);

        if (UnityEngine.Random.value >= MonumentRushChance)
        {
            return false;
        }

        if (UnityEngine.Random.value < ImmediateMonumentRushChance)
        {
            _isImmediateMonumentRush.Add(characterId);
            _pursuingImmediateRushTools.Add(characterId);
            Puts($"monument-rush: '{survivor.Character.Alias}' rolled the immediate variant - gathering just a stone hatchet and pickaxe before rushing a monument, skipping the rest of its checklist until a genuine clear.");
            return true;
        }

        _pursuingMonumentRushGoal.Add(characterId);
        _monumentRushDeadline[characterId] = UnityEngine.Time.realtimeSinceStartup + MonumentRushTimeLimitSeconds;
        Puts($"monument-rush: '{survivor.Character.Alias}' rolled to rush a monument before starting its checklist/base-building this life.");
        return true;
    }

    /// <summary>
    /// Pursues the immediate-rush variant's minimal tool kit (stone hatchet + pickaxe only - no
    /// sleeping bag/bow/arrows/bandages). Once both are owned, hands off into the normal
    /// gear-weighted destination roll the same way the non-immediate rush already does.
    /// </summary>
    private bool TryPursueImmediateRushTools(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (!_pursuingImmediateRushTools.Contains(characterId))
        {
            return false;
        }

        if (TryStartCraftingFallbackForImmediateRushTools(survivor, npc, state))
        {
            return true;
        }

        ItemDefinition hatchetDef = ItemManager.FindItemDefinition(StoneHatchetShortname);
        ItemDefinition pickaxeDef = ItemManager.FindItemDefinition(StonePickaxeShortname);

        if ((hatchetDef == null || npc.inventory.GetAmount(hatchetDef.itemid) < 1)
            || (pickaxeDef == null || npc.inventory.GetAmount(pickaxeDef.itemid) < 1))
        {
            // Not done yet, but nothing to craft/gather this cycle either - fall through to
            // normal looting so the survivor keeps acting while waiting on materials.
            return false;
        }

        _pursuingImmediateRushTools.Remove(characterId);
        _pursuingMonumentRushGoal.Add(characterId);
        _monumentRushDeadline[characterId] = UnityEngine.Time.realtimeSinceStartup + MonumentRushTimeLimitSeconds;
        Puts($"monument-rush: '{survivor.Character.Alias}' has its hatchet and pickaxe - heading out to rush a monument now.");

        TryStartWithGearWeightedDestination(survivor, npc);
        return true;
    }

    /// <summary>
    /// Same shape as TryStartCraftingFallback but scoped to only the two tools the immediate rush
    /// variant needs, so it never drifts into crafting a bow/bag/bandages while "everything else"
    /// is supposed to stay disregarded until a genuine monument clear.
    /// </summary>
    private bool TryStartCraftingFallbackForImmediateRushTools(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        ItemCrafter activeCrafter = npc.inventory?.crafting;

        if (activeCrafter != null && activeCrafter.queue.Count > 0)
        {
            return false;
        }

        if (TryPursueOneOffToolGoal(survivor, npc, state, StoneHatchetShortname, "stone hatchet", allowGather: false)) return true;
        if (TryPursueOneOffToolGoal(survivor, npc, state, StonePickaxeShortname, "stone pickaxe", allowGather: false)) return true;
        if (TryPursueOneOffToolGoal(survivor, npc, state, StoneHatchetShortname, "stone hatchet")) return true;
        if (TryPursueOneOffToolGoal(survivor, npc, state, StonePickaxeShortname, "stone pickaxe")) return true;

        return false;
    }

    // Window with no gathering progress before a coastal survivor gives up and heads inland
    // instead. Only checked for a survivor that rolled Coastal and is still on the checklist.
    private const float CoastalStallCheckWindowSeconds = 150f;

    private readonly Dictionary<Guid, (float Time, int Wood, int Stone, int Cloth)> _coastalProgressSnapshot = new();

    /// <summary>
    /// Compares carried wood/stone/cloth against a snapshot from CoastalStallCheckWindowSeconds
    /// ago. Any growth just refreshes the baseline; a full window with no growth redirects the
    /// survivor to a fresh random inland site, converting it from Coastal to Inland retroactively.
    /// </summary>
    private bool TryRedirectStalledCoastalBotInland(Survivor survivor, BasePlayer npc)
    {
        Guid characterId = survivor.Character.Id;

        if (_homeSiteTarget.ContainsKey(characterId) || survivor.Character.Home != null)
        {
            return false;
        }

        int currentWood = npc.inventory.GetAmount(ItemManager.FindItemDefinition(WoodShortname)?.itemid ?? 0);
        int currentStone = npc.inventory.GetAmount(ItemManager.FindItemDefinition(StoneShortname)?.itemid ?? 0);
        int currentCloth = npc.inventory.GetAmount(ItemManager.FindItemDefinition(ClothShortname)?.itemid ?? 0);

        if (!_coastalProgressSnapshot.TryGetValue(characterId, out (float Time, int Wood, int Stone, int Cloth) snapshot))
        {
            _coastalProgressSnapshot[characterId] = (UnityEngine.Time.realtimeSinceStartup, currentWood, currentStone, currentCloth);
            return false;
        }

        if (UnityEngine.Time.realtimeSinceStartup - snapshot.Time < CoastalStallCheckWindowSeconds)
        {
            return false;
        }

        bool madeProgress = currentWood > snapshot.Wood || currentStone > snapshot.Stone || currentCloth > snapshot.Cloth;

        // Refreshes the baseline either way: a new starting point after progress, or a fresh
        // baseline for wherever the survivor ends up next after redirecting.
        _coastalProgressSnapshot[characterId] = (UnityEngine.Time.realtimeSinceStartup, currentWood, currentStone, currentCloth);

        if (madeProgress || !TryFindRandomInlandSite(out Vector3 site))
        {
            return false;
        }

        _homeSiteTarget[characterId] = site;
        Puts($"home-site: '{survivor.Character.Alias}' made no gathering progress in {CoastalStallCheckWindowSeconds:F0}s near the coast - giving up and heading inland to {site} instead.");

        StartWalkingWithRecovery(survivor, site, onArrived: () =>
        {
            Puts($"home-site: '{survivor.Character.Alias}' arrived at its new inland site.");
            ContinueLootTask(survivor, new LootTaskState(), forceLocalScan: true);
        },
        onFailed: () => ContinueLootTask(survivor, new LootTaskState(), forceLocalScan: true));

        return true;
    }

    // Keeps a random map coordinate roll away from the very edge of the map (often ocean/border
    // terrain).
    private const float MapEdgeMarginFraction = 0.1f;
    private const int InlandSiteSearchMaxAttempts = 20;

    // Buffer gathered on top of a design's real construction cost, so the survivor arrives with
    // enough left over to also seed the new tool cupboard's upkeep.
    // (The old flat BaseGatherResourceBuffer of 1000 is gone: targets are the design cost + 20%, see GetBaseGatherTarget.)

    // HammerShortname itself already exists (LivingRust.SpawnKits.cs) - reused, not redeclared.
    private const string BuildingPlannerShortname = "building.planner";

    private readonly HashSet<Guid> _hasRolledHomeSiteStrategy = new();
    private readonly Dictionary<Guid, float> _primitiveGoalDeadline = new();

    // Only ever set for an "Inland" roll - absent means Coastal (build
    // wherever the survivor happens to end up once it's ready).
    private readonly Dictionary<Guid, Vector3> _homeSiteTarget = new();

    private readonly HashSet<Guid> _pursuingBaseGatherGoal = new();

    // Guards against the life-stall watchdog re-entering the task loop mid-replay and starting a
    // second build on top of an unfinished one. Only the replay phase is guarded (the walk there
    // stays rescuable), and it expires so a genuinely hung replay can't block forever.
    private readonly Dictionary<Guid, float> _baseBuildStartedAt = new();
    private const float BaseBuildInFlightMaxSeconds = 600f;

    private bool IsBaseBuildInFlight(Guid characterId)
    {
        return _baseBuildStartedAt.TryGetValue(characterId, out float startedAt)
            && UnityEngine.Time.realtimeSinceStartup - startedAt < BaseBuildInFlightMaxSeconds;
    }
    private readonly Dictionary<Guid, (string Tier, string DesignPath, Dictionary<string, int> Cost)> _rolledBaseDesign = new();

    // Randomized window added on top of when a survivor's base-gather goal starts, so bots don't
    // all try to build at the same moment.
    private const float BaseGatherDeadlineWindowSeconds = 900f;

    private readonly Dictionary<Guid, float> _baseGatherDeadline = new();

    /// <summary>
    /// Lazily rolls and caches a random build-by deadline the first time it's needed, so a
    /// survivor with no entry yet still gets a fair 0-15 minute window instead of an instant
    /// catch-up.
    /// </summary>
    private float GetOrSetBaseGatherDeadline(Guid characterId)
    {
        if (_baseGatherDeadline.TryGetValue(characterId, out float deadline))
        {
            return deadline;
        }

        deadline = UnityEngine.Time.realtimeSinceStartup + UnityEngine.Random.Range(0f, BaseGatherDeadlineWindowSeconds);
        _baseGatherDeadline[characterId] = deadline;
        return deadline;
    }

    /// <summary>
    /// Once-per-life roll for coastal vs inland home-site strategy. An inland roll walks the
    /// survivor there via StartWalkingWithRecovery before onReady fires, so bots disperse from the
    /// coast instead of gathering wherever they already are; a coastal roll calls onReady
    /// immediately.
    /// </summary>
    private void RollHomeSiteStrategyIfFreshLife(Survivor survivor, Action onReady)
    {
        Guid characterId = survivor.Character.Id;

        if (_hasRolledHomeSiteStrategy.Contains(characterId))
        {
            onReady?.Invoke();
            return;
        }

        _hasRolledHomeSiteStrategy.Add(characterId);
        _primitiveGoalDeadline[characterId] = UnityEngine.Time.realtimeSinceStartup + PrimitiveGoalTimeLimitSeconds;

        BasePlayer npc = survivor.Player;

        if (npc != null && !npc.IsDestroyed && UnityEngine.Random.value < HomeSiteInlandRollChance && TryFindRandomInlandSite(out Vector3 site))
        {
            _homeSiteTarget[characterId] = site;

            // Pre-rolls a base design and build-by deadline for inland survivors so both start
            // ticking from spawn instead of from checklist-completion, giving them a head start
            // once they reach base-gather. Doesn't change checklist priority itself.
            if (!_rolledBaseDesign.ContainsKey(characterId)
                && TryRollBaseDesignIgnoringAffordability(out string headstartTier, out string headstartDesignPath, out Dictionary<string, int> headstartCost))
            {
                _rolledBaseDesign[characterId] = (headstartTier, headstartDesignPath, headstartCost);
                GetOrSetBaseGatherDeadline(characterId);
                Puts($"home-site: '{survivor.Character.Alias}' also pre-rolled '{headstartTier}/{Path.GetFileNameWithoutExtension(headstartDesignPath)}' for a head start on base-gathering once its checklist is done.");
            }

            VerbosePuts($"home-site: '{survivor.Character.Alias}' rolled INLAND - heading to {site} before starting its primitive checklist.");

            StartWalkingWithRecovery(survivor, site, onArrived: () =>
            {
                VerbosePuts($"home-site: '{survivor.Character.Alias}' arrived at its inland site - starting its checklist here.");
                onReady?.Invoke();
            },
            onFailed: () =>
            {
                Puts($"home-site: '{survivor.Character.Alias}' couldn't reach its inland site - starting its checklist wherever it ended up.");
                onReady?.Invoke();
            });

            return;
        }

        VerbosePuts($"home-site: '{survivor.Character.Alias}' rolled COASTAL - will build near wherever it ends up.");
        onReady?.Invoke();
    }

    private float GetOrSetPrimitiveGoalDeadline(Guid characterId)
    {
        if (_primitiveGoalDeadline.TryGetValue(characterId, out float deadline))
        {
            return deadline;
        }

        deadline = UnityEngine.Time.realtimeSinceStartup + PrimitiveGoalTimeLimitSeconds;
        _primitiveGoalDeadline[characterId] = deadline;
        return deadline;
    }

    /// <summary>
    /// Picks a random valid map coordinate, avoiding water and monument no-build zones.
    /// Approximates "inland" as a point well off the map edge rather than a true
    /// distance-from-coastline check.
    /// </summary>
    private bool TryFindRandomInlandSite(out Vector3 site)
    {
        float worldSize = ConVar.Server.worldsize;
        float half = worldSize / 2f;
        float margin = worldSize * MapEdgeMarginFraction;

        for (int attempt = 0; attempt < InlandSiteSearchMaxAttempts; attempt++)
        {
            Vector3 candidate = new Vector3(
                UnityEngine.Random.Range(-half + margin, half - margin),
                0f,
                UnityEngine.Random.Range(-half + margin, half - margin));

            candidate.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(candidate) : 0f;

            float waterHeight = WaterLevel.GetWaterLevel(candidate, waves: false);

            if (waterHeight > candidate.y + 0.5f)
            {
                continue;
            }

            if (IsInsideMonumentNoBuildZone(candidate, out _))
            {
                continue;
            }

            site = candidate;
            return true;
        }

        site = Vector3.zero;
        return false;
    }

    // How many times TryStartAutonomousBaseBuild retries the SAME target before giving up and
    // rerolling an entirely new one. A genuinely bad spot (e.g. wedged against a cliff face)
    // never becomes buildable no matter how far "terrain tolerance loosens as failures build
    // up" relaxes the thresholds at that one location, so retrying forever there is pure waste.
    private const int BuildSiteRerollFailureThreshold = 4;

    // How far outside a monument's no-build buffer the reroll site lands: far enough to not
    // immediately fail IsInsideMonumentNoBuildZone again, close enough to still read as "near"
    // the monument rather than a random point on the map.
    private const float MonumentAdjacentSiteMinDistance = MonumentNoBuildBuffer + 10f;
    private const float MonumentAdjacentSiteMaxDistance = MonumentNoBuildBuffer + 80f;

    // How far from the survivor's current position a candidate monument counts as "nearby" for
    // a build-site reroll, so a stuck survivor isn't sent clear across the map for this.
    private const float BuildSiteRerollMonumentSearchRadius = 1500f;

    private const int MonumentAdjacentSiteMaxAttempts = 20;

    /// <summary>
    /// Picks a random valid site just outside a nearby monument's no-build zone, for a survivor
    /// giving up on its current build target after BuildSiteRerollFailureThreshold failures.
    /// Prefers a random monument within BuildSiteRerollMonumentSearchRadius of origin; falls
    /// back to the single nearest monument on the map if none are that close.
    /// </summary>
    private bool TryFindRandomSiteNearMonument(Vector3 origin, out Vector3 site)
    {
        site = Vector3.zero;

        if (MonumentAccess.GetAllMonuments().Count == 0)
        {
            return false;
        }

        List<MonumentInfo> nearby = MonumentAccess.GetAllMonuments()
            .Where(m => m != null && (m.transform.position - origin).sqrMagnitude <= BuildSiteRerollMonumentSearchRadius * BuildSiteRerollMonumentSearchRadius)
            .ToList();

        MonumentInfo monument = nearby.Count > 0
            ? nearby[UnityEngine.Random.Range(0, nearby.Count)]
            : MonumentAccess.GetAllMonuments()
                .Where(m => m != null)
                .OrderBy(m => (m.transform.position - origin).sqrMagnitude)
                .FirstOrDefault();

        if (monument == null)
        {
            return false;
        }

        for (int attempt = 0; attempt < MonumentAdjacentSiteMaxAttempts; attempt++)
        {
            float angle = UnityEngine.Random.Range(0f, 360f);
            float distance = UnityEngine.Random.Range(MonumentAdjacentSiteMinDistance, MonumentAdjacentSiteMaxDistance);
            Vector3 offset = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * distance;
            Vector3 candidate = monument.transform.position + offset;

            candidate.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(candidate) : 0f;

            float waterHeight = WaterLevel.GetWaterLevel(candidate, waves: false);

            if (waterHeight > candidate.y + 0.5f)
            {
                continue;
            }

            if (IsInsideMonumentNoBuildZone(candidate, out _))
            {
                continue;
            }

            site = candidate;
            return true;
        }

        return false;
    }

    // Per-design roll weighting so bots naturally spread across the whole design pool instead of
    // clustering on whichever design keeps winning the dice. Keyed by full design path, so tiers
    // never collide. First selection costs a design 10 percentage points of weight; every
    // selection after that costs another 20 (so a design picked 3 times in a row sits at
    // -50%: 10 + 20 + 20). Every design NOT picked on a given roll recovers 5 points back toward
    // its normal weight, floored at 0 - a design that recovers fully to 0 is treated as fresh
    // again on its next pick. Weight is never allowed below BaseDesignMinWeightPercent, so a
    // heavily-penalized design stays pickable, just unlikely.
    private readonly Dictionary<string, float> _baseDesignRollPenalty = new();
    private const float BaseDesignFirstRollPenalty = 10f;
    private const float BaseDesignRepeatRollPenalty = 20f;
    private const float BaseDesignRecoveryPerNonRoll = 5f;
    private const float BaseDesignMinWeightPercent = 5f;

    /// <summary>
    /// Weighted pick among designs - see _baseDesignRollPenalty's own doc comment for the
    /// weighting rule. Also applies the roll-adjustment (penalize the winner, recover everyone
    /// else) as a side effect of picking, so callers never need to remember to do it separately.
    /// </summary>
    private string PickWeightedBaseDesign(string[] designs)
    {
        float[] weights = new float[designs.Length];
        float totalWeight = 0f;

        for (int i = 0; i < designs.Length; i++)
        {
            float penalty = _baseDesignRollPenalty.GetValueOrDefault(designs[i]);
            weights[i] = Mathf.Max(BaseDesignMinWeightPercent, 100f - penalty);
            totalWeight += weights[i];
        }

        float roll = UnityEngine.Random.Range(0f, totalWeight);
        string chosen = designs[designs.Length - 1];

        for (int i = 0; i < designs.Length; i++)
        {
            if ((roll -= weights[i]) <= 0f)
            {
                chosen = designs[i];
                break;
            }
        }

        foreach (string design in designs)
        {
            float currentPenalty = _baseDesignRollPenalty.GetValueOrDefault(design);

            if (design == chosen)
            {
                _baseDesignRollPenalty[design] = currentPenalty + (currentPenalty <= 0f ? BaseDesignFirstRollPenalty : BaseDesignRepeatRollPenalty);
            }
            else
            {
                _baseDesignRollPenalty[design] = Mathf.Max(0f, currentPenalty - BaseDesignRecoveryPerNonRoll);
            }
        }

        return chosen;
    }

    /// <summary>
    /// Rolls a random tier0/tier1 design regardless of current affordability, unlike
    /// TryChooseAffordableBaseDesign which only picks something already affordable.
    /// </summary>
    private bool TryRollBaseDesignIgnoringAffordability(out string tier, out string designPath, out Dictionary<string, int> cost)
    {
        string[] candidateTiers = { "tier0", "tier1" };
        List<string> availableTiers = candidateTiers
            .Where(t => Directory.Exists($"{BaseDesignsDirectory}/{t}") && Directory.GetFiles($"{BaseDesignsDirectory}/{t}", "*.csv").Length > 0)
            .ToList();

        if (availableTiers.Count == 0)
        {
            tier = null;
            designPath = null;
            cost = null;
            return false;
        }

        tier = availableTiers[UnityEngine.Random.Range(0, availableTiers.Count)];
        string[] designs = Directory.GetFiles($"{BaseDesignsDirectory}/{tier}", "*.csv");
        designPath = PickWeightedBaseDesign(designs);

        // First-base-only path: waives starter essentials so the survivor isn't stuck idling
        // mid-build.
        cost = CalculateTraceResourceRequirements(designPath, freeStarterEssentials: true);
        return true;
    }

    /// <summary>
    /// Rolls a random tier2 design at full cost, used as the monument-rush completion reward.
    /// </summary>
    /// <summary>
    /// Called when a survivor learns a blueprint (LivingRust.Blueprints.cs) that needs a higher
    /// workbench than its currently targeted base design provides - 2026-10-03, Lucas's own
    /// explicit spec: "I picked up a rifle.ak but I only have a tier2 base design built, I need
    /// to now build a tier3 base design." Only ever raises the target, never lowers it, and only
    /// while the survivor hasn't already built that tier (a real built Home's own tier isn't
    /// tracked here - this just re-rolls the still-pending target design, the same thing
    /// TryPursueTierUpgrade already walks toward once gathering/building resumes).
    /// </summary>
    private void TryUpgradeBaseTierForLearnedBlueprint(BasePlayer npc, ItemBlueprint blueprint)
    {
        int neededTier = blueprint.workbenchLevelRequired;

        if (neededTier <= 0)
        {
            return;
        }

        Survivor survivor = FindSurvivorByPlayer(npc);

        if (survivor == null)
        {
            return;
        }

        Guid characterId = survivor.Character.Id;

        int currentTier = _rolledBaseDesign.TryGetValue(characterId, out (string Tier, string DesignPath, Dictionary<string, int> Cost) rolled)
            && int.TryParse(rolled.Tier.Replace("tier", ""), out int parsedTier)
                ? parsedTier
                : 0;

        if (neededTier <= currentTier || !TryRollBaseDesignForTier(neededTier, out string designPath, out Dictionary<string, int> cost))
        {
            return;
        }

        _rolledBaseDesign[characterId] = ($"tier{neededTier}", designPath, cost);
        Puts($"blueprint: '{survivor.Character.Alias}' learned a blueprint needing a tier{neededTier} workbench - upgrading its target base design from tier{currentTier} to tier{neededTier}.");
    }

    private bool TryRollBaseDesignForTier(int tier, out string designPath, out Dictionary<string, int> cost)
    {
        string tierDirectory = $"{BaseDesignsDirectory}/tier{tier}";

        if (!Directory.Exists(tierDirectory))
        {
            designPath = null;
            cost = null;
            return false;
        }

        string[] designs = Directory.GetFiles(tierDirectory, "*.csv");

        if (designs.Length == 0)
        {
            designPath = null;
            cost = null;
            return false;
        }

        designPath = PickWeightedBaseDesign(designs);
        cost = CalculateTraceResourceRequirements(designPath);
        return true;
    }

    private bool TryRollTier2BaseDesign(out string designPath, out Dictionary<string, int> cost)
    {
        string tierDirectory = $"{BaseDesignsDirectory}/tier2";

        if (!Directory.Exists(tierDirectory))
        {
            designPath = null;
            cost = null;
            return false;
        }

        string[] designs = Directory.GetFiles(tierDirectory, "*.csv");

        if (designs.Length == 0)
        {
            designPath = null;
            cost = null;
            return false;
        }

        designPath = PickWeightedBaseDesign(designs);
        cost = CalculateTraceResourceRequirements(designPath);
        return true;
    }

    /// <summary>
    /// Concludes a monument-rush life and always moves the survivor into base-gathering
    /// (recycling first if useful). A genuine monument completion earns bonus rewards - a tier2
    /// base design and boosted weapon-reward odds - while a rush that only did natural looting
    /// still proceeds, just with normal tier0/tier1 odds.
    /// </summary>
    private void ConcludeMonumentRush(Survivor survivor, BasePlayer npc, MonumentTier tier, string monumentName, bool wasGenuineSuccess)
    {
        Guid characterId = survivor.Character.Id;
        bool wasImmediate = _isImmediateMonumentRush.Remove(characterId);

        if (wasImmediate && wasGenuineSuccess)
        {
            // Spec: "everything else can be disregarded then resumed IF AND ONLY IF the bot
            // clears the monument loot path" - unlike the normal rush (which jumps straight to
            // base-gathering either way), a genuine immediate-rush clear resumes the REST of the
            // primitive checklist (sleeping bag/bow/arrows/bandages - hatchet/pickaxe are already
            // owned, so those two checks just pass through immediately) before base-gathering.
            _pursuingPrimitiveGoals.Add(characterId);
        }
        else
        {
            _pursuingPrimitiveGoals.Remove(characterId);
            _pursuingBaseGatherGoal.Add(characterId);
        }

        if (wasGenuineSuccess)
        {
            Puts($"monument-rush: '{survivor.Character.Alias}' successfully finished its rush monument ('{monumentName}') via a real route - rolling straight to a tier2 base and boosting its reward odds to 75%.");

            if (!_rolledBaseDesign.ContainsKey(characterId) && TryRollTier2BaseDesign(out string rushDesignPath, out Dictionary<string, int> rushCost))
            {
                _rolledBaseDesign[characterId] = ("tier2", rushDesignPath, rushCost);
                GetOrSetBaseGatherDeadline(characterId);
                Puts($"monument-rush: '{survivor.Character.Alias}' rolled 'tier2/{Path.GetFileNameWithoutExtension(rushDesignPath)}' as its rush-completion base design.");
            }

            BeginMonumentClearRewards(survivor);
            GrantMonumentRushCompletionReward(survivor, npc, tier);
            TryGrantMonumentKeycardReward(survivor, npc, tier, monumentName);
            TryGrantMonumentClothingReward(survivor, npc, tier);
            TryGrantMonumentMedicalReward(survivor, npc, tier);
            TryGrantMonumentToolBonusReward(survivor, npc, tier);
            TryGrantMonumentBlueprintFragmentReward(survivor, npc, tier);
            EndMonumentClearRewards();
        }
        else
        {
            Puts($"monument-rush: '{survivor.Character.Alias}' finished up at '{monumentName}' without completing a real route - falling back to a normal tier0/tier1 base and normal reward odds.");

            BeginMonumentClearRewards(survivor);
            TryGrantMonumentHandicapReward(survivor, npc, tier, didRealGhostRoute: false, didCardPuzzle: false);
            TryGrantMonumentKeycardReward(survivor, npc, tier, monumentName);
            TryGrantMonumentClothingReward(survivor, npc, tier);
            TryGrantMonumentMedicalReward(survivor, npc, tier);
            TryGrantMonumentToolBonusReward(survivor, npc, tier);
            TryGrantMonumentBlueprintFragmentReward(survivor, npc, tier);
            EndMonumentClearRewards();
        }

        if (!TryStartRecyclingTask(survivor))
        {
            StartLootForResourcesTask(survivor);
        }
    }

    /// <summary>
    /// Drives the "gather for a base" fallback: crafts tools, hunts animals for materials, and
    /// gathers resources toward the rolled design. Returns whether this cycle claimed an action.
    /// </summary>
    private bool TryPursueBaseGatherGoal(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        // Extra guard in case a base was built through another path while this goal was still
        // queued; stops cleanly instead of rolling/gathering toward a second base.
        if (survivor.Character.Home != null)
        {
            _pursuingBaseGatherGoal.Remove(characterId);
            _buildSiteSearchFailures.Remove(characterId);
            return false;
        }

        if (!_rolledBaseDesign.TryGetValue(characterId, out (string Tier, string DesignPath, Dictionary<string, int> Cost) rolled))
        {
            if (!TryRollBaseDesignIgnoringAffordability(out string tier, out string designPath, out Dictionary<string, int> cost))
            {
                VerbosePuts($"base-gather: '{survivor.Character.Alias}' has no tier0/tier1 base design available to roll - staying idle on this goal.");
                return false;
            }

            rolled = (tier, designPath, cost);
            _rolledBaseDesign[characterId] = rolled;
            string costBreakdown = string.Join(", ", cost.OrderByDescending(kvp => kvp.Value).Select(kvp => $"{kvp.Value}x {kvp.Key}"));
            Puts($"base-gather: '{survivor.Character.Alias}' rolled '{rolled.Tier}/{Path.GetFileNameWithoutExtension(rolled.DesignPath)}' to gather toward (real cost: {costBreakdown}).");

            // Rolled once, the first time this survivor reaches base-gather, staggering each
            // survivor's build-by deadline rather than converging on the same instant.
            GetOrSetBaseGatherDeadline(characterId);
        }

        // Hammer and building plan are the tools needed to execute the build, separate from the
        // design's own material cost.
        if (TryPursueOneOffToolGoal(survivor, npc, state, HammerShortname, "hammer"))
        {
            return true;
        }

        if (TryPursueOneOffToolGoal(survivor, npc, state, BuildingPlannerShortname, "building plan"))
        {
            return true;
        }

        // Hunts animals with a bow before building, since animal fat is needed for the furnace's
        // fuel. See TryPursueAnimalHunt for the full pipeline.
        if (TryPursueAnimalHunt(survivor, npc, state))
        {
            return true;
        }

        ItemDefinition hammerDef = ItemManager.FindItemDefinition(HammerShortname);
        ItemDefinition plannerDef = ItemManager.FindItemDefinition(BuildingPlannerShortname);
        bool hasHammer = hammerDef != null && npc.inventory.GetAmount(hammerDef.itemid) >= 1;
        bool hasPlanner = plannerDef != null && npc.inventory.GetAmount(plannerDef.itemid) >= 1;
        bool allResourcesReady = true;

        foreach (KeyValuePair<string, int> requirement in rolled.Cost)
        {
            int target = GetBaseGatherTarget(requirement.Key, requirement.Value);

            int itemId = ItemManager.FindItemDefinition(requirement.Key)?.itemid ?? 0;
            int have = itemId != 0 ? npc.inventory.GetAmount(itemId) : 0;

            if (have >= target)
            {
                continue;
            }

            // Once the build-by deadline passes, a resource shortfall no longer blocks heading to
            // the site; the actual top-up grant only happens once the survivor is physically at
            // the build origin, so it isn't carrying free resources around beforehand.
            if (itemId != 0 && UnityEngine.Time.realtimeSinceStartup >= GetOrSetBaseGatherDeadline(characterId))
            {
                continue;
            }

            allResourcesReady = false;

            // No farmable node exists for metal fragments. This used to start a recycling trip, but
            // recycling is now only for a full inventory or an expired monument-run timer
            // (2026-10-03, Lucas's spec) - the build-by deadline top-up covers any shortfall.
            if (requirement.Key == "metal.fragments")
            {
                continue;
            }

            if (TryGatherCraftIngredient(survivor, npc, state, requirement.Key))
            {
                return true;
            }
        }

        if (!hasHammer || !hasPlanner || !allResourcesReady)
        {
            return false;
        }

        // The base-gather flag only clears once a build genuinely places something, rather than
        // the moment the survivor decides to attempt it - otherwise a stuck walk or interrupted
        // build could permanently drop a survivor out of tracking with no base and no way to retry.
        return TryStartAutonomousBaseBuild(survivor, npc, rolled.DesignPath, characterId);
    }

    // ============================================================
    // Pre-base animal hunting.
    // ============================================================

    private const float AnimalHuntSearchRadius = 150f;

    // How long a hunt (chasing/fighting, not the initial approach walk) gets before giving up,
    // longer than a melee chase since bow combat against a fleeing animal takes longer.
    private const float AnimalHuntGiveUpSeconds = 45f;

    // Caps how much hunting contributes per life, so it doesn't dominate the gather phase.
    private const int AnimalHuntQuota = 5;

    // Overall time budget for hunting, measured from the first hunt attempt this life. Once it
    // expires no new hunt starts, though one already in progress finishes naturally.
    private const float AnimalHuntPhaseWindowSeconds = 900f;

    private const float AnimalHuntCorpseSearchRadius = 8f;

    private static readonly string[] AnimalHarvestToolPriority =
    {
        "knife.skinning",
        "hatchet",
        "stonehatchet",
        "lumberjack.hatchet",
        "frontier_hatchet",
        "concretehatchet",
        "diverhatchet",
        "knife.combat",
        "machete",
        "rock",
    };

    private readonly Dictionary<Guid, int> _animalHuntKillCount = new();

    private readonly Dictionary<Guid, float> _animalHuntPhaseStartTime = new();

    private readonly HashSet<Guid> _activeAnimalHunt = new();

    /// <summary>
    /// Bow-hunts the nearest huntable animal within AnimalHuntSearchRadius once per base-gather
    /// cycle, up to AnimalHuntQuota kills this life, then harvests the corpse for materials.
    /// </summary>
    private bool TryPursueAnimalHunt(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (_activeAnimalHunt.Contains(characterId))
        {
            // Already mid-hunt; this cycle just waits for the poll/callback chain below to resume
            // normal work rather than starting a second hunt on top of the first.
            return true;
        }

        if (_animalHuntKillCount.GetValueOrDefault(characterId) >= AnimalHuntQuota)
        {
            return false;
        }

        if (!HasReadyRangedWeapon(npc))
        {
            return false;
        }

        if (!_animalHuntPhaseStartTime.TryGetValue(characterId, out float phaseStart))
        {
            phaseStart = Time.realtimeSinceStartup;
            _animalHuntPhaseStartTime[characterId] = phaseStart;
        }
        else if (Time.realtimeSinceStartup - phaseStart >= AnimalHuntPhaseWindowSeconds)
        {
            // No new hunt starts once the budget is spent, regardless of kill count.
            return false;
        }

        if (!TryFindNearestHuntableAnimal(npc, AnimalHuntSearchRadius, out BaseCombatEntity animal))
        {
            return false;
        }

        _activeAnimalHunt.Add(characterId);

        Puts($"base-gather: '{survivor.Character.Alias}' spotted a real '{animal.ShortPrefabName}' within {AnimalHuntSearchRadius:F0}m - heading out to hunt it with its bow.");

        if (Vector3.Distance(npc.transform.position, animal.transform.position) <= HuntStandoffDistance)
        {
            StartAnimalHuntEngagement(survivor, animal, state);
            return true;
        }

        StartWalkingWithRecovery(
            survivor,
            GetHuntStandoffPoint(npc, animal),
            onArrived: () => StartAnimalHuntEngagement(survivor, animal, state),
            onFailed: () =>
            {
                _activeAnimalHunt.Remove(characterId);
                VerbosePuts($"base-gather: '{survivor.Character.Alias}' couldn't reach the animal it was hunting - giving up on this one.");
            });

        return true;
    }

    /// <summary>
    /// Last-resort cloth source: hunts and harvests an animal when no hemp is nearby. Reuses
    /// TryPursueAnimalHunt's pipeline but skips its bow-ownership gate, kill quota, and phase
    /// window, since this only fires as an ingredient search that's already cooldown-gated
    /// elsewhere.
    /// </summary>
    private bool TryPursueAnimalHuntForCloth(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (_activeAnimalHunt.Contains(characterId))
        {
            return true;
        }

        // Requires a ranged weapon, since melee-hunting for cloth tends to fail and provokes the
        // animal. A survivor without one falls through to hemp/other cloth sources instead.
        if (!HasReadyRangedWeapon(npc))
        {
            return false;
        }

        if (!TryFindNearestHuntableAnimal(npc, AnimalHuntSearchRadius, out BaseCombatEntity animal))
        {
            return false;
        }

        _activeAnimalHunt.Add(characterId);

        VerbosePuts($"craft-task: '{survivor.Character.Alias}' has no cloth nearby for a bandage - hunting a real '{animal.ShortPrefabName}' for materials instead.");
        if (Vector3.Distance(npc.transform.position, animal.transform.position) <= HuntStandoffDistance)
        {
            StartAnimalHuntEngagement(survivor, animal, state);
            return true;
        }


        StartWalkingWithRecovery(
            survivor,
            GetHuntStandoffPoint(npc, animal),
            onArrived: () => StartAnimalHuntEngagement(survivor, animal, state),
            onFailed: () =>
            {
                _activeAnimalHunt.Remove(characterId);
                VerbosePuts($"craft-task: '{survivor.Character.Alias}' couldn't reach the animal it was hunting for cloth - giving up on this one.");
            });

        return true;
    }

    // Distance a ranged hunt stands off from the animal instead of closing to melee range.
    private const float HuntStandoffDistance = 22f;

    private static Vector3 GetHuntStandoffPoint(BasePlayer npc, BaseCombatEntity animal)
    {
        Vector3 animalPosition = animal.transform.position;
        Vector3 fromAnimal = npc.transform.position - animalPosition;
        fromAnimal.y = 0f;

        if (fromAnimal.magnitude <= HuntStandoffDistance)
        {
            return npc.transform.position;
        }

        Vector3 point = animalPosition + fromAnimal.normalized * HuntStandoffDistance;
        point.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(point) : animalPosition.y;
        return point;
    }

    private bool TryFindNearestHuntableAnimal(BasePlayer npc, float radius, out BaseCombatEntity animal)
    {
        List<BaseCombatEntity> candidates = Pool.Get<List<BaseCombatEntity>>();
        Vector3 origin = npc.transform.position;

        try
        {
            // Bear/Crocodile are gear-score-gated via IsHardAvoidAnimal below, so an under-geared
            // survivor isn't sent out only to flee instead of fighting on arrival.
            CollectEntitiesInRange<Rust.Ai.Gen2.Bear>(origin, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.PolarBear>(origin, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Boar>(origin, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Stag>(origin, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Wolf2>(origin, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Crocodile>(origin, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Panther>(origin, radius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Tiger>(origin, radius, candidates);

            BaseCombatEntity nearest = null;
            float nearestDistanceSqr = float.MaxValue;

            foreach (BaseCombatEntity candidate in candidates)
            {
                if (candidate == null || candidate.IsDestroyed || !candidate.IsAlive())
                {
                    continue;
                }

                if (IsHardAvoidAnimal(candidate, npc))
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

                if (distanceSqr < nearestDistanceSqr)
                {
                    nearestDistanceSqr = distanceSqr;
                    nearest = candidate;
                }
            }

            animal = nearest;
            return animal != null;
        }
        finally
        {
            Pool.FreeUnmanaged(ref candidates);
        }
    }

    /// <summary>
    /// Starts combat against the hunted animal via the shared StartCombat pipeline, then polls
    /// once a second for the outcome. Gives up after AnimalHuntGiveUpSeconds if the hunt isn't
    /// converging.
    /// </summary>
    private void StartAnimalHuntEngagement(Survivor survivor, BaseCombatEntity animal, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
        {
            _activeAnimalHunt.Remove(characterId);
            return;
        }

        if (animal == null || animal.IsDestroyed || !animal.IsAlive())
        {
            // Already dead - nothing left to harvest.
            _activeAnimalHunt.Remove(characterId);
            return;
        }

        Vector3 lastKnownPosition = animal.transform.position;
        float huntStartTime = Time.realtimeSinceStartup;

        StartCombat(survivor, animal);

        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer currentNpc = survivor.Player;

            if (currentNpc == null || currentNpc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                _activeAnimalHunt.Remove(characterId);
                return;
            }

            if (animal != null && !animal.IsDestroyed && animal.IsAlive())
            {
                lastKnownPosition = animal.transform.position;

                if (Time.realtimeSinceStartup - huntStartTime < AnimalHuntGiveUpSeconds)
                {
                    return;
                }

                pollTimer.Destroy();
                _activeAnimalHunt.Remove(characterId);
                VerbosePuts($"base-gather: '{survivor.Character.Alias}' gave up hunting after {AnimalHuntGiveUpSeconds:F0}s - it got away.");
                return;
            }

            // Animal is dead - StartCombat's damage pipeline already ran EndCombat.
            pollTimer.Destroy();

            // An animal kill never produces a BaseCorpse (that's the lootbag/ragdoll a
            // dead PLAYER leaves) - the animal itself just stays dead in place, harvestable
            // the same way a tree/ore node is (see LivingRust.KillLoot.cs's own
            // TryFindNearestDeadHuntableAnimal/StartHarvestingAnimalCorpse). This call used
            // to search for a BaseCorpse here and always fail ("couldn't find a real corpse
            // nearby to harvest" - every time, for every hunt kill - 2026-10-02, Lucas's live
            // report: a bot killed a boar and never harvested it), then stop without
            // continuing the task loop at all. OnEntityDeath's own NotePotentialPriorityKill
            // hook already queued this exact kill as priority loot the instant it died
            // (independent of this function) - continuing the loot task here is all that's
            // needed to let that already-correct pathway pick it up and walk over to harvest
            // it for real.
            _animalHuntKillCount[characterId] = _animalHuntKillCount.GetValueOrDefault(characterId) + 1;
            _activeAnimalHunt.Remove(characterId);
            ContinueLootTask(survivor, state, forceLocalScan: true);
        });
    }

    private bool TryFindNearestCorpse(Vector3 origin, float radius, out BaseCorpse corpse)
    {
        List<BaseCorpse> candidates = Pool.Get<List<BaseCorpse>>();

        try
        {
            Vis.Entities(origin, radius, candidates);

            BaseCorpse nearest = null;
            float nearestDistanceSqr = float.MaxValue;

            foreach (BaseCorpse candidate in candidates)
            {
                if (candidate == null || candidate.IsDestroyed)
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

                if (distanceSqr < nearestDistanceSqr)
                {
                    nearestDistanceSqr = distanceSqr;
                    nearest = candidate;
                }
            }

            corpse = nearest;
            return corpse != null;
        }
        finally
        {
            Pool.FreeUnmanaged(ref candidates);
        }
    }

    /// <summary>
    /// Swings a gather tool against a fresh animal corpse, mirroring StartGatheringResourceNode's
    /// tree/ore pipeline but against a corpse's own resource dispenser instead.
    /// </summary>
    private void StartHarvestingCorpse(Survivor survivor, BaseCorpse corpse, Action onDone)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveAttack(characterId);

        BaseMelee melee = EquipBestGatherToolForType(survivor, AnimalHarvestToolPriority);

        if (melee == null)
        {
            VerbosePuts($"gather-task: '{survivor.Character.Alias}' has no tool to harvest the corpse with.");
            onDone?.Invoke();
            return;
        }

        int hits = 0;
        Timer attackTimer = null;

        attackTimer = timer.Every(AttackHitInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onDone?.Invoke();
                return;
            }

            if (corpse == null || corpse.IsDestroyed)
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onDone?.Invoke();
                return;
            }

            if (!HasLineOfSight(npc, corpse) || !IsWithinLootRange(npc, corpse))
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onDone?.Invoke();
                return;
            }

            FaceDirection(npc, corpse.transform.position - npc.transform.position);

            if (melee.HasAttackCooldown())
            {
                return;
            }

            hits++;

            melee.ServerUse();
            melee.CancelInvoke(melee.ServerUse_Strike);

            HitInfo info = Pool.Get<HitInfo>();
            info.Init(npc, corpse, DamageType.Generic, 0f, corpse.transform.position);
            info.Weapon = melee;
            info.WeaponPrefab = melee;
            info.PointStart = npc.eyes.position;
            info.PointEnd = corpse.transform.position;

            melee.DoAttackShared(info);

            Pool.Free(ref info);

            if (hits >= MaxHitsPerResourceNode)
            {
                Puts($"gather-task: '{survivor.Character.Alias}' finished harvesting the corpse ({hits} hit(s)).");

                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onDone?.Invoke();
            }
        });

        _activeAttacks[characterId] = attackTimer;
    }

    /// <summary>
    /// Finds a clear build origin near the survivor's rolled home-site target (or its current
    /// position for a Coastal roll), walks there if needed, then builds and settles in via
    /// GhostReturnHomeAndDeposit.
    /// </summary>
    private bool TryStartAutonomousBaseBuild(Survivor survivor, BasePlayer npc, string designPath, Guid characterId, HomeBase oldHomeToMigrate = null)
    {
        if (IsBaseBuildInFlight(characterId))
        {
            return true;
        }

        Vector3 startPosition = _homeSiteTarget.TryGetValue(characterId, out Vector3 target) ? target : npc.transform.position;

        _failedBuildSites.TryGetValue(characterId, out List<Vector3> avoidSites);

        bool foundSite = TryFindClearBuildOrigin(startPosition, out Vector3 clearOrigin, out float expectedGroundHeight, out string failureReason, designPath, avoidSites, characterId);

        // Second chance from wherever the survivor is actually standing, since an inland roll's
        // target spot can be bad ground even when the survivor's current surroundings are fine.
        if (!foundSite && Vector3.Distance(startPosition, npc.transform.position) > 1f)
        {
            foundSite = TryFindClearBuildOrigin(npc.transform.position, out clearOrigin, out expectedGroundHeight, out failureReason, designPath, avoidSites, characterId);
        }

        if (!foundSite)
        {
            int failureCount = _buildSiteSearchFailures[characterId] = _buildSiteSearchFailures.GetValueOrDefault(characterId) + 1;
            _buildSiteLifetimeFailures[characterId] = _buildSiteLifetimeFailures.GetValueOrDefault(characterId) + 1;

            if (failureCount >= BuildSiteRerollFailureThreshold
                && TryFindRandomSiteNearMonument(npc.transform.position, out Vector3 rerolledSite))
            {
                _homeSiteTarget[characterId] = rerolledSite;
                _buildSiteSearchFailures.Remove(characterId);
                Puts($"base-gather: '{survivor.Character.Alias}' gave up on its current build target after {failureCount} failed searches (reason: {failureReason}) - rerolled a new site near a monument at {rerolledSite}.");
                return false;
            }

            Puts($"base-gather: '{survivor.Character.Alias}' couldn't find a clear build spot near its target (reason: {failureReason}) - will retry next cycle (search failure #{failureCount}, terrain tolerance loosens as failures build up).");
            return false;
        }

        ReserveBuildSite(characterId, clearOrigin);
        CancelActiveMovement(survivor);
        CancelActiveAttack(survivor.Character.Id);
        CancelActiveRecycling(survivor.Character.Id);

        string designName = Path.GetFileNameWithoutExtension(designPath);
        Puts($"base-gather: '{survivor.Character.Alias}' has everything it needs - heading to build '{designName}'.");

        void OnReplayComplete(int placed, int failed)
        {
            Puts($"base-gather: '{survivor.Character.Alias}' finished building '{designName}' - {placed} piece(s) placed, {failed} failed.");
            _baseBuildStartedAt.Remove(characterId);
            _buildSiteReservations.Remove(characterId);
            _rolledBaseDesign.Remove(characterId);
            _homeSiteTarget.Remove(characterId);

            // A placed == 0 run genuinely built nothing, so Home stays null and the survivor
            // stays flagged for base-gather to roll a fresh design/site and try again.
            if (placed == 0)
            {
                Puts($"base-gather: '{survivor.Character.Alias}' placed nothing at all - staying queued to try again.");
                RecordFailedBuildSite(characterId, clearOrigin);
                _buildSiteReservations.Remove(characterId);
                return;
            }

            _pursuingBaseGatherGoal.Remove(characterId);
            _buildSiteSearchFailures.Remove(characterId);
            _buildSiteLifetimeFailures.Remove(characterId);

            if (oldHomeToMigrate != null)
            {
                // On a tier upgrade, grabs everything from the old base before the normal
                // settle-in deposit, since the new base is empty until the old contents are in
                // hand.
                MigrateOldBaseContents(survivor, oldHomeToMigrate, survivor.Character.Home, () => GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor)));
                return;
            }

            // Settles in by depositing leftover building materials and tools into the new base.
            GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor));
        }

        float relocatedDistance = (clearOrigin - npc.transform.position).magnitude;

        // The deadline-catch-up grant only happens here, once the survivor is confirmed
        // physically at the validated build origin.
        void ProceedToClearAndReplay()
        {
            _baseBuildStartedAt[characterId] = UnityEngine.Time.realtimeSinceStartup;
            GrantAnyStillMissingBaseResources(survivor, characterId);
            ClearBuildSiteThenReplay(survivor, clearOrigin, expectedGroundHeight, designPath, BuildSiteMaxClearingActions, OnReplayComplete);
        }

        if (relocatedDistance > 0.5f)
        {
            WalkToBuildSiteWithRecovery(
                survivor,
                clearOrigin,
                onArrived: ProceedToClearAndReplay,
                onFailed: () =>
                {
                    RecordFailedBuildSite(characterId, clearOrigin);
                    _buildSiteReservations.Remove(characterId);
                    Puts($"base-gather: '{survivor.Character.Alias}' couldn't walk to its build site - remembered it as a bad spot, will try elsewhere next cycle.");
                });
        }
        else
        {
            ProceedToClearAndReplay();
        }

        return true;
    }

    /// <summary>
    /// Tops up any resources the survivor is still short on, called only once it's confirmed at
    /// the build origin so the window of carrying an unearned stockpile stays minimal. Re-checks
    /// current inventory fresh, since gathering along the way may have already closed some of the
    /// gap.
    /// </summary>
    private void GrantAnyStillMissingBaseResources(Survivor survivor, Guid characterId)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || !_rolledBaseDesign.TryGetValue(characterId, out (string Tier, string DesignPath, Dictionary<string, int> Cost) rolled))
        {
            return;
        }

        foreach (KeyValuePair<string, int> requirement in rolled.Cost)
        {
            int target = GetBaseGatherTarget(requirement.Key, requirement.Value);

            int itemId = ItemManager.FindItemDefinition(requirement.Key)?.itemid ?? 0;

            if (itemId == 0)
            {
                continue;
            }

            int have = npc.inventory.GetAmount(itemId);

            if (have >= target)
            {
                continue;
            }

            int shortfall = target - have;

            // Splits the grant into stack-sized chunks, since ItemManager.CreateByItemID creates
            // one Item with no automatic splitting at the game's own stack limit.
            int stackSize = ItemManager.FindItemDefinition(itemId)?.stackable ?? shortfall;
            int remaining = shortfall;
            int actuallyGranted = 0;

            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, stackSize);
                Item granted = ItemManager.CreateByItemID(itemId, chunk);

                if (granted == null)
                {
                    break;
                }

                if (!granted.MoveToContainer(npc.inventory.containerMain))
                {
                    granted.Drop(npc.transform.position, Vector3.zero);
                }

                actuallyGranted += chunk;
                remaining -= chunk;
            }

            if (actuallyGranted > 0)
            {
                Puts($"base-gather: '{survivor.Character.Alias}' hit its build-by deadline still short on {requirement.Key} - topped up {actuallyGranted}x (real {stackSize}-max stacks) right at the build site.");
            }
        }
    }

    /// <summary>
    /// On a tier upgrade, empties the old base into the new one. Temporarily points
    /// Character.Home at oldHome so the existing ghost-enter/collect machinery operates on the
    /// old base, then restores it to newHome before the deposit trip.
    /// </summary>
    private void MigrateOldBaseContents(Survivor survivor, HomeBase oldHome, HomeBase newHome, Action onComplete)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || oldHome == null || newHome == null || ReferenceEquals(oldHome, newHome))
        {
            onComplete?.Invoke();
            return;
        }

        Puts($"tier-upgrade: '{survivor.Character.Alias}' is heading back to its old base to collect everything before moving in.");

        survivor.Character.Home = oldHome;

        GhostEnterHomeForDeposit(survivor, () =>
        {
            int collected = CollectEverythingFromOwnedStorage(survivor);
            Puts($"tier-upgrade: '{survivor.Character.Alias}' collected {collected} item stack(s) from its old base.");

            survivor.Character.Home = newHome;
            onComplete?.Invoke();
        });
    }

    /// <summary>
    /// Reverse of the deposit pipeline: empties every owned box, the cupboard, and any owned
    /// furnace near the current Home into the survivor's inventory, respecting carrying capacity.
    /// </summary>
    private int CollectEverythingFromOwnedStorage(Survivor survivor)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            return 0;
        }

        int collected = 0;

        foreach (StorageContainer box in FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius))
        {
            if (box == null || box.IsDestroyed || box.inventory == null)
            {
                continue;
            }

            collected += TransferAllItems(box.inventory, npc.inventory, npc);
        }

        BuildingPrivlidge cupboard = FindOwnedCupboard(home);

        if (cupboard != null && cupboard.inventory != null)
        {
            collected += TransferAllItems(cupboard.inventory, npc.inventory, npc);
        }

        foreach (BaseOven furnace in FindOwnedFurnacesNear(npc, home.Position, HomeStorageSearchRadius))
        {
            if (furnace == null || furnace.IsDestroyed || furnace.inventory == null)
            {
                continue;
            }

            collected += TransferAllItems(furnace.inventory, npc.inventory, npc);
        }

        return collected;
    }
}
