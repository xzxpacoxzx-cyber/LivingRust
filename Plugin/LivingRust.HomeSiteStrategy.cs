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
/// Real "tie a lot of things together" pipeline (2026-09-01, Lucas's own
/// explicit spec) - a fresh survivor first rolls Coastal (build wherever it
/// ends up) or Inland (head to a random valid map coordinate first), then
/// runs the now-unconditional primitive checklist (LivingRust.Crafting.cs).
/// If that checklist isn't finished within 15 minutes, priority switches to
/// gathering toward a randomly-rolled tier0/tier1 base design's real cost
/// (wood/stones with a +1000 cupboard-upkeep buffer, metal.fragments via
/// opportunistic component looting + recycling - there's no farmable node
/// for components, same reasoning TryGatherCraftIngredient's own doc
/// comment gives), crafting a hammer + building plan along the way, then
/// actually executing the build via the same real ReplayBuildTrace/
/// ClearBuildSiteThenReplay pipeline /lr.debug.replaybuild already proves
/// out, and finally settling in via the already-proven GhostReturnHomeAndDeposit
/// trip.
/// </summary>
public partial class LivingRust
{
    // 15 real minutes (2026-09-01, Lucas's own explicit number).
    private const float PrimitiveGoalTimeLimitSeconds = 900f;

    // Lowered from 0.5 (2026-09-01, live report: "of 250 bots, I have about
    // 60-70 sitting on or around the coastline just aimlessly walking
    // around... I think if we keep closer to a quarter of overall bots on/
    // around the shoreline that's fine"). 0.75 inland means ~25% stay
    // Coastal, matching that ask directly.
    private const float HomeSiteInlandRollChance = 0.75f;

    // Real "give up on the coast, go inland" stall detector (2026-09-01,
    // Lucas's own explicit spec: "if nothing meaningful is being done...
    // after 2-3 minutes the bot has gathered no resources... have the bot
    // just give up and head inland randomly"). Checked only for a survivor
    // that rolled Coastal AND is still pursuing the primitive checklist -
    // an inland-rolled survivor is already headed somewhere, and a
    // survivor with a real base has nowhere useful to redirect to.
    private const float CoastalStallCheckWindowSeconds = 150f;

    private readonly Dictionary<Guid, (float Time, int Wood, int Stone, int Cloth)> _coastalProgressSnapshot = new();

    /// <summary>
    /// Real per-cycle stall check - cheap in the common case (an early
    /// return the moment ANY of the three gating conditions below don't
    /// apply, or the snapshot window simply hasn't elapsed yet). Compares
    /// carried wood/stone/cloth against a snapshot taken
    /// CoastalStallCheckWindowSeconds ago - any genuine increase in any of
    /// the three counts as real progress (a stone/metal ore find would
    /// increment "stone" too, so this isn't blind to ore-node gathering
    /// either) and just refreshes the baseline rather than redirecting.
    /// Only once a full window passes with NO growth at all does this
    /// commit the survivor to a fresh random inland site and a real walk
    /// there, converting it from Coastal to Inland retroactively - reuses
    /// TryFindRandomInlandSite/_homeSiteTarget, the exact same mechanism
    /// the original spawn-time roll already uses, so this survivor now
    /// behaves identically to one that rolled Inland from the start.
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

        // Refresh the baseline regardless - either real progress (new
        // starting point for the next window) or a confirmed stall about
        // to redirect (fresh baseline for wherever it ends up next).
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

    // Keeps a random map coordinate roll away from the very edge of the
    // map (often ocean/border terrain) - same "margin off the edge" idea
    // TryFindClearBuildOrigin's own relocate loop uses locally, just
    // applied at map scale here.
    private const float MapEdgeMarginFraction = 0.1f;
    private const int InlandSiteSearchMaxAttempts = 20;

    // "2000+ wood in storage" (LivingRust.Looting.cs's own
    // HomeFurnaceWoodTrigger) is a SEPARATE, later concern (topping up a
    // furnace once a base already exists) - this is the buffer on TOP of
    // a design's own real construction cost, specifically so the survivor
    // arrives at the build site with enough left over to also seed the
    // new tool cupboard's upkeep once GhostReturnHomeAndDeposit runs
    // afterward (2026-09-01, Lucas's own explicit framing: "gather until X
    // quantity achieved +1000 (for tool cupboard deposit)").
    private const int BaseGatherResourceBuffer = 1000;

    // HammerShortname itself already exists (LivingRust.SpawnKits.cs) - reused, not redeclared.
    private const string BuildingPlannerShortname = "building.planner";

    private readonly HashSet<Guid> _hasRolledHomeSiteStrategy = new();
    private readonly Dictionary<Guid, float> _primitiveGoalDeadline = new();

    // Only ever set for an "Inland" roll - absent means Coastal (build
    // wherever the survivor happens to end up once it's ready).
    private readonly Dictionary<Guid, Vector3> _homeSiteTarget = new();

    private readonly HashSet<Guid> _pursuingBaseGatherGoal = new();

    // In-flight build guard (2026-09-21, live report: bases built as a
    // jumble of overlapping partial structures; JumpyBuzzard started 'base5'
    // 31 times without finishing). While a build replay is actually running
    // the survivor stands mostly still (clearing trees, placing pieces), so
    // the life-stall watchdog "rescued" it, re-entering the task loop -
    // and with _pursuingBaseGatherGoal now (correctly) kept until success,
    // that re-entry started a brand-new replay at a new site on top of the
    // abandoned one. Only the REPLAY phase is guarded (the walk there stays
    // rescuable), and it expires so a genuinely hung replay can't block
    // forever.
    private readonly Dictionary<Guid, float> _baseBuildStartedAt = new();
    private const float BaseBuildInFlightMaxSeconds = 600f;

    private bool IsBaseBuildInFlight(Guid characterId)
    {
        return _baseBuildStartedAt.TryGetValue(characterId, out float startedAt)
            && UnityEngine.Time.realtimeSinceStartup - startedAt < BaseBuildInFlightMaxSeconds;
    }
    private readonly Dictionary<Guid, (string Tier, string DesignPath, Dictionary<string, int> Cost)> _rolledBaseDesign = new();

    // Real randomized build-by window (2026-09-19, Lucas's own explicit
    // spec: "between 15-30 minutes, the bots should have gotten resources
    // to build a base, built the base... One thing I want to avoid is all
    // the bots instinctively building a base at 15:01"). 0-900s (0-15 min)
    // rolled on top of whenever a survivor's own base-gather goal actually
    // starts (itself already ~15 real minutes into life via the primitive
    // checklist's own PrimitiveGoalTimeLimitSeconds), landing each
    // survivor's own "must be ready" moment somewhere in that next 15
    // minutes rather than every bot converging on the same instant.
    private const float BaseGatherDeadlineWindowSeconds = 900f;

    private readonly Dictionary<Guid, float> _baseGatherDeadline = new();

    /// <summary>
    /// Same lazy-fallback shape as GetOrSetPrimitiveGoalDeadline's own doc
    /// comment describes - covers a survivor that was already mid-base-gather
    /// (rolled a design, entered _pursuingBaseGatherGoal) from BEFORE this
    /// deadline mechanic existed, so _baseGatherDeadline has no entry for
    /// it yet. Rolling fresh right now rather than treating "missing" as
    /// "already due" - a survivor mid-migration deserves the same real 0-15
    /// minute window everyone else gets, not an instant catch-up the very
    /// next time this is checked.
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
    /// Called from StartLootForResourcesTask alongside RollPrimitiveGoalIfFreshLife
    /// - same once-per-life gating pattern, same reasoning (not a
    /// spawn-only hook, internally gated instead). Takes onReady rather
    /// than being fire-and-forget (2026-09-01, live report: bots "piling
    /// up in certain areas... all appear to be farming around the same
    /// area" - the real cause was that an INLAND roll only ever stored
    /// _homeSiteTarget for LATER use at actual base-build time, nothing
    /// made the survivor walk there first, so it just gathered wherever it
    /// happened to already be - almost always near its own beach spawn,
    /// same as every other survivor's). An inland roll now genuinely walks
    /// there via StartWalkingWithRecovery BEFORE onReady fires (letting
    /// the primitive checklist start once it's actually inland, dispersing
    /// bots away from the coast the way the original spawn-roll was always
    /// meant to); a coastal roll (or a failed inland walk) calls onReady
    /// immediately, same as before.
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

            // Real headstart (2026-09-19, Lucas's own explicit ask: "for
            // the 20% that spawn inland already, maybe they should just
            // instantly start building a tier0 or tier1 base design
            // (gives them a slight headstart)"). Pre-rolls the exact same
            // design/deadline TryPursueBaseGatherGoal would otherwise only
            // roll once the checklist actually finishes. Doesn't change
            // checklist priority at all - an inland survivor still does
            // its checklist first, same as before, ContinueLootTask still
            // checks _pursuingPrimitiveGoals ahead of _pursuingBaseGatherGoal
            // regardless - it just means the design choice and its own
            // randomized build-by deadline (GetOrSetBaseGatherDeadline)
            // start ticking from spawn instead of from checklist-completion,
            // so by the time an inland survivor actually reaches
            // base-gather its clock already has a real head start. Guarded
            // the same way TryPursueBaseGatherGoal itself guards a fresh
            // roll (ContainsKey check) - a no-op if somehow already set.
            if (!_rolledBaseDesign.ContainsKey(characterId)
                && TryRollBaseDesignIgnoringAffordability(out string headstartTier, out string headstartDesignPath, out Dictionary<string, int> headstartCost))
            {
                _rolledBaseDesign[characterId] = (headstartTier, headstartDesignPath, headstartCost);
                GetOrSetBaseGatherDeadline(characterId);
                Puts($"home-site: '{survivor.Character.Alias}' also pre-rolled '{headstartTier}/{Path.GetFileNameWithoutExtension(headstartDesignPath)}' for a head start on base-gathering once its checklist is done.");
            }

            Puts($"home-site: '{survivor.Character.Alias}' rolled INLAND - heading to {site} before starting its primitive checklist.");

            StartWalkingWithRecovery(survivor, site, onArrived: () =>
            {
                Puts($"home-site: '{survivor.Character.Alias}' arrived at its inland site - starting its checklist here.");
                onReady?.Invoke();
            },
            onFailed: () =>
            {
                Puts($"home-site: '{survivor.Character.Alias}' couldn't reach its inland site - starting its checklist wherever it ended up.");
                onReady?.Invoke();
            });

            return;
        }

        Puts($"home-site: '{survivor.Character.Alias}' rolled COASTAL - will build near wherever it ends up.");
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
    /// Picks a random valid map coordinate, retrying up to
    /// InlandSiteSearchMaxAttempts times against two real checks: not
    /// underwater (WaterLevel.GetWaterLevel vs the real terrain heightmap -
    /// the same swim-detection API LivingRust.Commands.cs already uses) and
    /// not inside a monument's own no-build buffer (IsInsideMonumentNoBuildZone,
    /// LivingRust.BaseBuilding.cs). "Inland" here means "a random valid
    /// point well off the map edge," not a true distance-from-coastline
    /// guarantee - a real coastline-distance computation would need much
    /// more work than this roll is worth.
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

    /// <summary>
    /// Rolls a random tier0/tier1 design REGARDLESS of current
    /// affordability (2026-09-01, Lucas's own explicit spec: "roll for any
    /// one of the tier0 or tier1 base builds and gather the required
    /// resources to build it") - the opposite direction from
    /// TryChooseAffordableBaseDesign (which only ever picks something
    /// already affordable RIGHT NOW). Same random-pick-within-a-tier logic
    /// RunDebugReplayBuild's own tier-explicit mode already uses.
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
        designPath = designs[UnityEngine.Random.Range(0, designs.Length)];

        // Real reduced burden (2026-09-01, Lucas's own explicit ask - see
        // CalculateTraceResourceRequirements' own doc comment on
        // freeFirstTwoDoorsAndLocks for the full "bot idling mid-build"
        // reasoning).
        cost = CalculateTraceResourceRequirements(designPath, freeFirstTwoDoorsAndLocks: true);
        return true;
    }

    /// <summary>
    /// Real "farm for a base" fallback (2026-09-01) - see this file's own
    /// top doc comment for the full pipeline. Returns whether this cycle
    /// actually claimed something (a craft, a gather walk, a recycling
    /// trip, or the build itself starting), same true/false contract every
    /// other fallback in this project uses.
    /// </summary>
    private bool TryPursueBaseGatherGoal(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        // Defense in depth (2026-09-01) - every real entry point into this
        // goal (checklist finished, checklist skipped, checklist timed out)
        // already guards on Character.Home == null before adding to
        // _pursuingBaseGatherGoal, but a survivor could still build a base
        // through some other path (e.g. GhostReturnHomeAndDeposit only
        // clears _rolledBaseDesign/_homeSiteTarget on ITS OWN build
        // completion) while already queued here - stop cleanly rather than
        // rolling/gathering toward a second base.
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
                Puts($"base-gather: '{survivor.Character.Alias}' has no tier0/tier1 base design available to roll - staying idle on this goal.");
                return false;
            }

            rolled = (tier, designPath, cost);
            _rolledBaseDesign[characterId] = rolled;
            string costBreakdown = string.Join(", ", cost.OrderByDescending(kvp => kvp.Value).Select(kvp => $"{kvp.Value}x {kvp.Key}"));
            Puts($"base-gather: '{survivor.Character.Alias}' rolled '{rolled.Tier}/{Path.GetFileNameWithoutExtension(rolled.DesignPath)}' to gather toward (real cost: {costBreakdown}).");

            // Real randomized build-by deadline (2026-09-19, Lucas's own
            // explicit spec: "have the bots randomly roll to build the
            // base after that 15 minute primitive timer... between 15-30
            // minutes, the bots should have gotten resources to build a
            // base, built the base"). Rolled once, right here, the first
            // time this survivor ever reaches base-gather (which itself
            // only starts around the real 15-minute checklist mark) -
            // staggers each survivor's own "must be ready by" moment
            // somewhere in that next 0-15 minutes rather than every bot
            // converging on the exact same instant. See the resource-check
            // loop below (GetOrSetBaseGatherDeadline) for what actually
            // happens once it's reached.
            GetOrSetBaseGatherDeadline(characterId);
        }

        // Hammer + building plan (2026-09-01, Lucas's own explicit list -
        // "sheet metal door, tool cupboard, code locks, building plan,
        // hammer etc" - the deployables/locks are already covered by the
        // design's own CalculateTraceResourceRequirements cost below,
        // hammer/planner are the two real TOOLS needed to actually execute
        // the build, not part of that cost). Both are real, simple tier0
        // wood-only recipes (confirmed via the recipe dump) - reuses
        // TryPursueOneOffToolGoal directly, same as stone tools.
        if (TryPursueOneOffToolGoal(survivor, npc, state, HammerShortname, "hammer"))
        {
            return true;
        }

        if (TryPursueOneOffToolGoal(survivor, npc, state, BuildingPlannerShortname, "building plan"))
        {
            return true;
        }

        // Real "hunt with the bow before building" ask (2026-09-01, Lucas's
        // own explicit spec: "once they get bows and before they try to
        // build their base... use the bows to fight any nearby animals to
        // farm with their hatchet or knife... help in getting the required
        // items/materials for the base to be built - specifically
        // furnaces" - animal fat is the real ingredient low grade fuel
        // needs, which a furnace needs to actually smelt ore once built).
        // See TryPursueAnimalHunt's own doc comment for the full pipeline.
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
            int target = requirement.Key == "wood" || requirement.Key == "stones"
                ? requirement.Value + BaseGatherResourceBuffer
                : requirement.Value;

            int itemId = ItemManager.FindItemDefinition(requirement.Key)?.itemid ?? 0;
            int have = itemId != 0 ? npc.inventory.GetAmount(itemId) : 0;

            if (have >= target)
            {
                continue;
            }

            // Deadline pass-through (2026-09-19, Lucas's own explicit spec -
            // see BaseGatherDeadlineWindowSeconds' own doc comment). Once
            // this survivor's own randomly-rolled build-by moment has
            // passed, a real shortfall no longer blocks heading to the
            // build site - it just doesn't grant anything YET. Real
            // security fix, same day, Lucas's own follow-up: granting the
            // top-up HERE (mid-decision, before any travel) used to mean a
            // survivor could walk around for real minutes carrying
            // thousands of free stone/wood it hadn't earned - "basically a
            // loot bag waiting for a player to kill them." The actual
            // grant now only happens once TryStartAutonomousBaseBuild
            // confirms the survivor is physically AT the validated build
            // origin (GrantAnyStillMissingBaseResources, right before
            // ClearBuildSiteThenReplay starts) - this loop's only job now
            // is deciding whether that trip is worth starting at all.
            if (itemId != 0 && UnityEngine.Time.realtimeSinceStartup >= GetOrSetBaseGatherDeadline(characterId))
            {
                continue;
            }

            allResourcesReady = false;

            // Real fragments-via-recycling path (2026-09-01, Lucas's own
            // explicit spec: "I require metal fragments? Gather components
            // and recycle components until X quantity is achieved") -
            // there's no farmable node for components (only real container
            // loot produces them, same reasoning TryGatherCraftIngredient's
            // own doc comment gives for why metal.fragments has no gather
            // case there either), so this triggers a real recycler trip
            // the moment there's fodder worth feeding rather than a
            // dedicated walk - normal opportunistic looting (still running
            // underneath this goal, same "loot collectibles on the way"
            // pattern the primitive checklist already relies on) is what
            // actually accumulates components in the first place.
            if (requirement.Key == "metal.fragments")
            {
                if (TryStartRecyclingTask(survivor))
                {
                    return true;
                }

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

        // Real fix (2026-09-19, live report: 'RecklessScrapper615' found
        // dead with a broken hatchet, an inventory full of stone and wood,
        // and no base anywhere, despite having clearly already rolled a
        // design and set off to build one over half an hour earlier). This
        // used to remove _pursuingBaseGatherGoal right here - the instant
        // the survivor merely DECIDED to attempt the build, not once the
        // build actually succeeded. TryStartAutonomousBaseBuild's own walk
        // is asynchronous (WalkToBuildSiteWithRecovery) and its own
        // onFailed comment already promised "will retry next cycle," but
        // with the flag already gone there was no longer any "next cycle"
        // left to retry on - a stuck walk (confirmed live: forest geometry
        // repeatedly blocking every waypoint), a clean pathing failure, or
        // an external interruption (the life-stall watchdog's own force-
        // relocate rescue, which bypasses this function's callbacks
        // entirely) all permanently dropped the survivor out of base-
        // gather tracking with Character.Home still null - it just quietly
        // fell back to being an ordinary looter with a base's worth of
        // materials in its pockets and nowhere to put them. The flag now
        // stays set until AdvanceBuildReplay's own OnReplayComplete
        // confirms a genuine placement (LivingRust.BaseBuilding.cs, mirrors
        // the exact same placed > 0 condition that function already uses
        // to decide whether to set Character.Home at all) - a synchronous
        // TryFindClearBuildOrigin failure, a failed walk, or a watchdog
        // rescue (see RescueGenuinelyStalledSurvivor's own doc comment,
        // LivingRust.Commands.cs) now all naturally retry through this same
        // function on whatever future cycle reaches it, exactly as the
        // "will retry next cycle" comments already assumed.
        return TryStartAutonomousBaseBuild(survivor, npc, rolled.DesignPath, characterId);
    }

    // ============================================================
    // Real pre-base animal hunting (2026-09-01, Lucas's own explicit spec -
    // see TryPursueAnimalHunt's own doc comment for the full pipeline).
    // First-pass numbers/mechanics, same "ship something reasonable, tune
    // from live evidence" approach as every other constant in this project.
    // ============================================================

    private const float AnimalHuntSearchRadius = 150f;

    // How long a real hunt (chasing/fighting, not the initial approach
    // walk) gets before giving up entirely - same bounded-aggression shape
    // MeleeChaseGiveUpSeconds already uses, just longer since bow combat
    // against a fleeing animal realistically takes longer than a melee
    // exchange (missed arrows, the animal running between shots).
    private const float AnimalHuntGiveUpSeconds = 45f;

    // Capped the same way every other gather quota in this project is
    // (WoodQuota/OreQuotaStone/etc) - hunting contributes real materials
    // toward the base without dominating the whole gather phase forever.
    private const int AnimalHuntQuota = 5;

    // Real overall time budget (2026-09-01, Lucas's own explicit follow-up:
    // "keep this within the 15 minute window... if they can't find any
    // animals or find the required resources to build a base just have the
    // bot resort to whatever the next portion was"). Measured from the
    // FIRST hunt attempt this life (_animalHuntPhaseStartTime, set once,
    // never refreshed), not a rolling window - once 15 real minutes have
    // passed since hunting started, no NEW hunt gets started regardless of
    // AnimalHuntQuota, and the caller (TryPursueBaseGatherGoal) falls
    // through to its own next priority (hammer/planner/resource gathering)
    // exactly like any other cycle where this returns false. A hunt already
    // in progress when the window closes still runs to its own natural
    // conclusion (AnimalHuntGiveUpSeconds/kill/corpse-harvest) rather than
    // being cut off mid-fight.
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
    /// Real bow-hunting pass, tried once per base-gather cycle right after
    /// the hammer/planner checks and before the resource-requirement loop -
    /// see this file's own TryPursueBaseGatherGoal call site. Only fires
    /// while the survivor owns a real bow/crossbow (BowFamily, LivingRust.
    /// Looting.cs) and hasn't already hit AnimalHuntQuota kills this life.
    /// Finds the nearest real huntable animal (the same 8 types
    /// IsHuntablePredator/TryStartAnimalOnSightCombat already recognize,
    /// LivingRust.Combat.cs) within AnimalHuntSearchRadius - deliberately
    /// much wider than the 10m reactive on-sight range, since this is a
    /// genuine deliberate search, not a passive reaction - walks toward it,
    /// engages via the exact same StartCombat ranged pipeline already
    /// proven for animal on-sight combat, then polls for the kill and
    /// harvests the corpse with a real hatchet/knife swing
    /// (StartHarvestingCorpse below - the same real attack-based
    /// ResourceDispenser pipeline this project's own tree/ore gathering
    /// already uses, SwingGatherTool/StartGatheringResourceNode in
    /// LivingRust.ResourceGathering.cs, just retargeted at a corpse's own
    /// dispenser instead of a tree/rock's).
    /// </summary>
    private bool TryPursueAnimalHunt(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (_activeAnimalHunt.Contains(characterId))
        {
            // Already mid-hunt (walking to it, fighting it, or harvesting
            // the corpse) - the poll/callback chain below owns resuming
            // normal base-gather work once it's done, so this cycle just
            // claims itself and waits rather than starting a second hunt
            // on top of the first.
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
            // See AnimalHuntPhaseWindowSeconds's own doc comment - no NEW
            // hunt starts once the 15-minute budget is spent, regardless of
            // kill count; falls through to the caller's own next priority.
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
    /// Real "kill animals and farm them for cloth" fallback (2026-09-07,
    /// Lucas's own explicit spec: when a survivor needs cloth for a
    /// bandage and no real hemp collectible is nearby, "kill animals and
    /// farm them for cloth OR go farm some hemp fibers" - the hemp half is
    /// TryGatherViaCollectible, already wired; this is the animal half).
    /// Reuses the exact same hunt-then-harvest pipeline TryPursueAnimalHunt
    /// uses for the post-checklist base-gather phase, but deliberately
    /// WITHOUT that function's bow-ownership gate, kill quota, or 15-minute
    /// phase window - those all exist to keep hunting from dominating the
    /// dedicated base-gather goal specifically, none of which applies here:
    /// this only ever fires as a last-resort ingredient search for cloth,
    /// already gated by TryGatherCraftIngredient's own IngredientSearchCooldownSeconds
    /// so it can't spin repeatedly either. A survivor with only a rock can
    /// still melee-hunt here - StartCombat itself already picks whatever
    /// real weapon (ranged or melee) the survivor actually has.
    /// </summary>
    private bool TryPursueAnimalHuntForCloth(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (_activeAnimalHunt.Contains(characterId))
        {
            return true;
        }

        // Ranged only (2026-09-21, Lucas's own explicit spec: "a bot should
        // attempt to kill an animal with a ranged weapon, not a melee
        // weapon" - melee-hunting a stag for cloth just fails, and a
        // hostile animal fights back). A survivor without a bow/firearm and
        // ammo falls through to hemp/other cloth sources instead; the
        // survival-kit upkeep (TryPursueSurvivalKitUpkeep) is what gets it
        // the bow in the first place.
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

    // How far from the animal a ranged hunt starts shooting (2026-09-21) -
    // walking up onto the animal's own position (the old behaviour) put
    // every hunt at melee range, exactly where a boar or wolf hurts most.
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
            // Bear/Crocodile gear-score-gated (2026-09-15) - see
            // IsHardAvoidAnimal's own doc comment (LivingRust.Combat.cs).
            // Still collected here same as every other candidate; the
            // IsHardAvoidAnimal filter below is what actually keeps an
            // under-geared survivor from being sent all the way out to one
            // only to flee instead of fighting on arrival - once geared
            // past the relevant threshold, either becomes a completely
            // normal hunt target.
            CollectEntitiesInRange<Bear>(origin, radius, candidates);
            CollectEntitiesInRange<Polarbear>(origin, radius, candidates);
            CollectEntitiesInRange<Boar>(origin, radius, candidates);
            CollectEntitiesInRange<Stag>(origin, radius, candidates);
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
    /// Starts real combat against the hunted animal (StartCombat - the same
    /// pipeline animal on-sight combat already uses, complete with its own
    /// pursuit/reload/disengage logic), then polls once a second for the
    /// outcome rather than needing a completion callback threaded through
    /// StartCombat itself. AnimalHuntGiveUpSeconds bounds how long this
    /// waits before abandoning a hunt that isn't converging (animal fled,
    /// bot can't catch up, etc) - same "give it a real chance, but not
    /// forever" shape as every other bounded-aggression/give-up timer in
    /// this project.
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
            // Already dead (finished off by something/someone else, or
            // simply despawned) - nothing real to harvest, same clean
            // give-up shape as any other failed gather attempt.
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

            // Animal is dead - StartCombat's own real damage pipeline
            // already finished it off and ran EndCombat by this point, no
            // separate "did the fight resolve" check needed here.
            pollTimer.Destroy();

            if (!TryFindNearestCorpse(lastKnownPosition, AnimalHuntCorpseSearchRadius, out BaseCorpse corpse))
            {
                VerbosePuts($"base-gather: '{survivor.Character.Alias}' killed its hunt target but couldn't find a real corpse nearby to harvest.");
                _activeAnimalHunt.Remove(characterId);
                return;
            }

            Puts($"base-gather: '{survivor.Character.Alias}' finished off its hunt target - harvesting the corpse.");

            StartHarvestingCorpse(survivor, corpse, onDone: () =>
            {
                _animalHuntKillCount[characterId] = _animalHuntKillCount.GetValueOrDefault(characterId) + 1;
                _activeAnimalHunt.Remove(characterId);
                ContinueLootTask(survivor, state, forceLocalScan: true);
            });
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
    /// Real swing loop against a fresh animal corpse - same overall shape
    /// as StartGatheringResourceNode (LivingRust.ResourceGathering.cs), but
    /// against a BaseCorpse's own ResourceDispenser (GatherType.Flesh)
    /// instead of a ResourceEntity's - real Rust animal corpses work
    /// exactly like a tree/ore node mechanically (a real attack-based
    /// harvest, not an inventory-menu loot), just a different concrete
    /// entity type, so this is a deliberate near-duplicate rather than
    /// reusing that function directly (keeps its own well-tested tree/ore
    /// contract untouched rather than widening its type signature).
    /// MaxHitsPerResourceNode-capped the same way, since a corpse doesn't
    /// expose a comparable Health()/IsDestroyed-on-depletion signal to
    /// gate on the way a tree/ore node's own DoAttackShared naturally
    /// destroys the node once emptied.
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
    /// Real build trigger (2026-09-01) - mirrors RunDebugReplayBuild's own
    /// real call chain exactly (TryFindClearBuildOrigin -> walk there if
    /// relocated -> ClearBuildSiteThenReplay), just against the survivor's
    /// own rolled home-site target (or its current position for a Coastal
    /// roll) instead of an admin's own position. On real completion,
    /// settles in via GhostReturnHomeAndDeposit - Character.Home gets set
    /// (and its real door routes auto-loaded/reprojected, LoadHomeDoorRoutes)
    /// by ReplayBuildTrace itself, so that trip works immediately with no
    /// extra wiring.
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

        // Second chance from wherever the survivor is actually standing
        // (2026-09-21) - an inland roll's own target spot can be bad
        // ground all the way out through the relocate spiral while the
        // survivor's own current surroundings are perfectly fine.
        if (!foundSite && Vector3.Distance(startPosition, npc.transform.position) > 1f)
        {
            foundSite = TryFindClearBuildOrigin(npc.transform.position, out clearOrigin, out expectedGroundHeight, out failureReason, designPath, avoidSites, characterId);
        }

        if (!foundSite)
        {
            _buildSiteSearchFailures[characterId] = _buildSiteSearchFailures.GetValueOrDefault(characterId) + 1;
            Puts($"base-gather: '{survivor.Character.Alias}' couldn't find a clear build spot near its target (reason: {failureReason}) - will retry next cycle (search failure #{_buildSiteSearchFailures[characterId]}, terrain tolerance loosens as failures build up).");
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

            // The real completion point for _pursuingBaseGatherGoal (2026-
            // 09-19) - see this function's own caller (TryPursueBaseGatherGoal)
            // for the full reasoning on why this moved down here instead of
            // firing the instant the survivor merely decided to attempt a
            // build. Mirrors AdvanceBuildReplay's own placed > 0 condition
            // for whether Character.Home actually got set
            // (LivingRust.BaseBuilding.cs) - a placed == 0 run genuinely
            // built nothing, Home is still null, and the survivor stays
            // flagged so the next cycle that reaches TryPursueBaseGatherGoal
            // rolls a fresh design/site and tries again instead of quietly
            // reverting to an ordinary looter with no way back to base-
            // gathering.
            if (placed == 0)
            {
                Puts($"base-gather: '{survivor.Character.Alias}' placed nothing at all - staying queued to try again.");
                RecordFailedBuildSite(characterId, clearOrigin);
                _buildSiteReservations.Remove(characterId);
                return;
            }

            _pursuingBaseGatherGoal.Remove(characterId);
            _buildSiteSearchFailures.Remove(characterId);

            if (oldHomeToMigrate != null)
            {
                // Real "grab everything from the old base" step (2026-09-01,
                // Lucas's own explicit caveat on tier upgrades: "the bot
                // should finish the new base, grab everything from the old
                // base and transfer it to the new base"). Runs BEFORE the
                // normal settle-in deposit below, not after - the new base
                // is genuinely empty right now (freshly built), so there's
                // nothing to deposit into it until the old base's contents
                // are actually in hand.
                MigrateOldBaseContents(survivor, oldHomeToMigrate, survivor.Character.Home, () => GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor)));
                return;
            }

            // Real "settle in" step (2026-09-01, Lucas's own explicit
            // spec: "deposit any building materials and building
            // plan/hammer... any excess components etc into the crates").
            // Reuses the exact same ghostroute-mandatory deposit trip
            // already proven live on GhostReturnHomeAndDeposit.
            GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor));
        }

        float relocatedDistance = (clearOrigin - npc.transform.position).magnitude;

        // Real security fix (2026-09-19, Lucas's own explicit ask) - the
        // deadline-catch-up grant only happens HERE now, once the survivor
        // is confirmed physically at the validated build origin, not back
        // when TryPursueBaseGatherGoal merely decided the trip was worth
        // starting. See that loop's own doc comment for the full
        // reasoning ("basically a loot bag waiting for a player to kill
        // them").
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
    /// The real deadline-catch-up grant (2026-09-19) - see
    /// TryPursueBaseGatherGoal's own resource-check loop for why this was
    /// moved out of that decision point and down here instead. Only ever
    /// called once TryStartAutonomousBaseBuild has confirmed the survivor
    /// is physically standing at its validated build origin, right before
    /// ClearBuildSiteThenReplay actually starts placing pieces - so the
    /// window where a survivor is carrying an unearned stockpile is as
    /// close to zero as this design allows, rather than the real minutes
    /// of walking it used to be exposed for. Re-checks current inventory
    /// fresh (not whatever TryPursueBaseGatherGoal saw when it decided to
    /// come here) since real gathering/looting along the way may have
    /// already closed some of the gap on its own.
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
            int target = requirement.Key == "wood" || requirement.Key == "stones"
                ? requirement.Value + BaseGatherResourceBuffer
                : requirement.Value;

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

            // Real stack-size fix (2026-09-19, live report: bots carrying
            // a single "impossible" 5000-6000 stack of stone/wood).
            // ItemManager.CreateByItemID creates exactly ONE Item with
            // amount set to whatever's passed - no automatic splitting at
            // the real game's own stack limit (ItemDefinition.stackable -
            // 1000 for stone/wood). This project's own GiveItem helper
            // (LivingRust.Hooks.cs) has the identical gap; every caller
            // that can need more than one stack already loops in
            // stack-sized chunks itself instead (see SpawnKits.cs's own
            // ammoStackSize pattern).
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
    /// Real "empty the old base into the new one" step (2026-09-01, Lucas's
    /// own explicit caveat on tier upgrades). Temporarily points
    /// Character.Home at oldHome so the existing ghost-enter/collect
    /// machinery (GhostEnterHomeForDeposit, FindOwnedStorageBoxesNear,
    /// FindOwnedCupboard, FindOwnedFurnacesNear - every one of them reads
    /// survivor.Character.Home fresh internally rather than taking it as a
    /// parameter) operates on the OLD base instead of the new one, then
    /// restores it to newHome before the real deposit trip - cheaper than
    /// threading an explicit HomeBase parameter through that whole call
    /// chain for what's a genuinely rare, one-off event (a tier upgrade
    /// finishing), and safe here specifically because nothing else touches
    /// this survivor's Home concurrently during a single ghost-controlled
    /// trip (CancelActiveMovement/CancelActiveAttack/CancelActiveRecycling
    /// already seized control back in TryStartAutonomousBaseBuild).
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
    /// Reverse of the deposit pipeline - empties every owned box, the
    /// cupboard, and any owned furnace near the current Home into the
    /// survivor's own inventory. Reuses TransferAllItems (the same real
    /// loot-priority-aware transfer LootContainerAndContinue/recycler
    /// collection already use) rather than a raw unconditional dump, so
    /// this respects real carrying capacity - a stockpile too large for
    /// one trip simply leaves the rest behind for now rather than
    /// overflowing, same as a real player would need multiple trips too.
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
