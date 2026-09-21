using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Real crafting (2026-08-28) - the first slice of "resource gathering ->
/// crafting -> base building" (Lucas's own explicit sequencing). Scoped
/// deliberately narrow per his own framing: a short, explicit list of
/// specific goals ("I need a sleeping bag = 30 cloth", "I need arrows, I
/// need wood and stone"), not a general "craft anything affordable"
/// scorer - base building (foundations/tool cupboard) is its own later
/// phase, since it needs real construction/placement mechanics nothing
/// here touches yet (sleeping bag placement below is the one deliberate
/// exception - narrow enough in scope to build now rather than waiting).
///
/// Reuses Rust's own real crafting pipeline directly rather than hand-
/// rolling ingredient consumption/item creation - same "call the real
/// game method" approach this project always uses (BaseMelee.DoAttackShared
/// for gathering, ResourceDispenser for GiveResourceFromItem, Deployer.
/// DoDeploy_Regular for placement, etc). Confirmed via decompile:
/// BasePlayer.inventory.crafting is a real, already-initialized
/// ItemCrafter (PlayerInventory wires it to containerMain+containerBelt
/// on Init), and ItemCrafter.CanCraft/CraftItem are the exact same public
/// methods a real player's own craft-panel click reaches.
///
/// BUT ItemCrafter.ServerUpdate - the real method that actually advances
/// the queue and finishes a craft - only ever gets called from
/// BasePlayer.InventoryUpdate, which is gated `if (IsConnected &&
/// !IsDead())` (confirmed via decompile). Every survivor in this project
/// is permanently IsConnected == false, so a real player's queued craft
/// would just sit in ItemCrafter.queue forever, never finishing, on its
/// own - live-confirmed 2026-08-28 ('2ColdRock' started crafting a
/// sleeping bag, logged nothing else for 90+ seconds, no error, just
/// silently stuck). StartCraftQueueDriver below is the fix - the same
/// "the client-only path doesn't reach a disconnected NPC, so drive the
/// real method manually" pattern this project already uses everywhere
/// else (damage effects, attack cooldowns, etc), just applied to crafting
/// specifically.
///
/// Exact ingredient amounts are baked prefab data, not present in the
/// decompiled source at all - confirmed live instead via the new
/// /lr.debug.recipe command (LivingRust.Debug.cs):
///   arrow.wooden: 25x wood, 10x stones -> 2x arrow.wooden (tier 0, 3.0s)
///   sleepingbag:  30x cloth            -> 1x sleepingbag  (tier 0, 30.0s)
/// Both tier 0 - no real Workbench needed for either goal in this phase.
/// </summary>
public partial class LivingRust
{
    private const string SleepingBagShortname = "sleepingbag";
    private const string ArrowShortname = "arrow.wooden";
    private const string ClothShortname = "cloth";

    // Real per-goal targets (2026-08-28, Lucas's own explicit numbers).
    // Arrows: "craft 15 stacks and that's it (max). If it runs out of
    // arrows, it can craft up to 15 stacks again" - 15 real recipe repeats
    // (ItemCrafter's own "amount", NOT the item's inventory stack size) =
    // 15 * 2 = 30 arrows, consuming 15 * 25 = 375 wood and 15 * 10 = 150
    // stone, matching his own worked example exactly. Only re-triggers
    // once the survivor's stock hits zero (ArrowRecraftThreshold), not a
    // rolling top-up - a deliberate change from this system's original
    // "top up whenever below 60" design.
    private const int ArrowMaxBatches = 15;
    private const int ArrowRecraftThreshold = 0;

    // Bandage: real recipe confirmed via the recipe dump (LivingRust/
    // crafting_recipes.csv) - 4x cloth -> 1x bandage, tier 0, 5.0s. 5
    // batches = 20 cloth (2026-09-01, Lucas's own explicit split of the
    // primitive checklist's "50 cloth" total: "30 to make a sleeping bag,
    // 20 to make some bandages"). Same recraft-when-empty pattern as
    // arrows, not a one-off - bandages get consumed healing, same as
    // arrows get consumed shooting. BandageShortname itself already
    // exists (LivingRust.Looting.cs) - reused, not redeclared.
    private const int BandageMaxBatches = 5;
    private const int BandageRecraftThreshold = 0;

    // Sleeping bag: own exactly 1, ever, per life - crafted once then
    // immediately placed (DeploySleepingBagAndAssign below), so the
    // inventory count returns to 0 the moment it's placed. Re-triggering
    // off inventory count alone would then craft a second one forever;
    // _hasPlacedSleepingBag (below) is the real "already done this" gate
    // instead.
    private const int SleepingBagTargetBatches = 1;

    // Real recipe (2026-08-28, Lucas's own explicit numbers - not re-
    // confirmed via /lr.debug.recipe this time, unlike arrow.wooden/
    // sleepingbag, since he already gave the exact figures directly):
    // 200x wood, 50x cloth -> 1x bow.hunting. Own exactly 1 - unlike the
    // sleeping bag, a bow never leaves inventory once crafted (nothing
    // here places or consumes it), so a plain inventory-count check is
    // enough of a gate, no separate "already done this" set needed.
    // Checked BEFORE arrows (Lucas's own explicit sequencing: "craft a
    // bow first, then arrows afterwards" - arrows are useless without one)
    // - already in this project's own real WeaponPriority list
    // (LivingRust.Looting.cs), so the existing equip-best-weapon logic
    // picks it up automatically once it's actually in inventory.
    private const string BowShortname = "bow.hunting";
    private const int BowTargetBatches = 1;

    // Real confirmed prefab names for the wood/stone surface-pile
    // collectibles (GetCollectibleDivertRadius's own doc comment,
    // LivingRust.Looting.cs - "Wood-Collectable"/"Stone-Collectable",
    // confirmed via AssetSceneManifest.json). Checked FIRST for a missing
    // wood/stone ingredient (2026-08-28, Lucas's own explicit spec: "go
    // find enough stone and wood collectable entities OR if none are
    // found in a 50m radius, have them farm a tree and a stone node") -
    // a surface pile is a much lighter/faster top-up than committing to a
    // full tree/ore node, so it's worth preferring when one's actually
    // nearby.
    private const string WoodCollectablePrefabSubstring = "wood-collectable";
    private const string StoneCollectablePrefabSubstring = "stone-collectable";

    // Search range for both the collectible-pile search above and the
    // tree/ore fallback below - widened from the original 50m to 200m
    // (2026-09-01, live-confirmed bots getting stuck unable to find any
    // stone within the old 50m at all, piling up wood indefinitely while
    // waiting), then pulled back to 100m same session once the inland-roll
    // dispersal fix (RollHomeSiteStrategyIfFreshLife, LivingRust.
    // HomeSiteStrategy.cs) landed and 200m's real Physics.OverlapSphere
    // cost (16x a 50m check) became a real suspect in reported server-wide
    // slowness at 300 bots - a middle ground pending confirmation either
    // way.
    private const float CraftIngredientSearchRadius = 100f;

    private readonly HashSet<Guid> _hasPlacedSleepingBag = new();
    private readonly Dictionary<Guid, Timer> _pendingSleepingBagDeployTimers = new();

    // Real spawn-time "what do I want to do first" priority (2026-08-28
    // original ask, made UNCONDITIONAL 2026-09-01 - Lucas's own explicit
    // correction: "the primitive checklist should be unconditional, it
    // happens regardless. THEN the bot rolls for its normal task sheet...
    // that way the bot is set up for success" - a fresh spawn genuinely
    // only has ~50-60 HP and a rock, so skipping this checklist half the
    // time was never actually the right default). There's no real tiered
    // task ladder in this project to hook into - TaskType (LivingRust/
    // Models/TaskType.cs) is a flat enum (None/LootForResources/Recycling)
    // that a lot of other systems already gate on (bot-onsight-summary's
    // own looter filter, among others), and the old AI/GoalType.cs
    // scaffold is genuinely dead code (zero references from anywhere in
    // Plugin/). Rather than risk breaking those existing gates with a
    // competing TaskType, a survivor pursuing this checklist stays
    // TaskType.LootForResources the whole time - this is just a priority
    // REORDERING within that same task (checked at the top of
    // ContinueLootTask, LivingRust.Looting.cs), not a separate one.
    private readonly HashSet<Guid> _hasRolledPrimitiveGoal = new();
    private readonly HashSet<Guid> _pursuingPrimitiveGoals = new();

    // Real "retry the checklist later, from a different spot" scheduling
    // (2026-09-01) - set when a 15-minute checklist attempt times out
    // (ContinueLootTask, LivingRust.Looting.cs), consulted at the top of
    // that same function to re-enter _pursuingPrimitiveGoals once the
    // delay elapses. Absent for a survivor that's never timed out, or
    // whose retry has already fired.
    private readonly Dictionary<Guid, float> _primitiveGoalRetryTime = new();

    /// <summary>
    /// Called from StartLootForResourcesTask (LivingRust.Looting.cs) -
    /// that function is the single real entry point every "resume
    /// looting" call site in the project already funnels through
    /// (recycling finished, a monument route completing, EndCombat/
    /// EndFlee resuming, respawn, ...), NOT a spawn-only hook, so the
    /// actual once-per-life gating happens here via
    /// _hasRolledPrimitiveGoal rather than needing a bespoke spawn call
    /// site. _hasRolledPrimitiveGoal/_pursuingPrimitiveGoals both clear on
    /// death (LivingRust.Hooks.cs) so every new life gets this checklist
    /// again. Name kept ("Roll...") despite no longer actually rolling
    /// anything, to avoid a churny rename across every caller/doc comment
    /// that already refers to it.
    /// </summary>
    // Real "already progressed, don't redo the primitive checklist"
    // threshold (2026-09-01, Lucas's own explicit example number) - checked
    // alongside "already owns a stone/metal-tier gather tool" and "already
    // has a base" (ShouldSkipPrimitiveChecklist below). Lucas's own
    // explicit reasoning: this matters most right after a plugin reload -
    // _hasRolledPrimitiveGoal itself resets on every reload (in-memory
    // only, not part of the persisted survivor roster), so without this
    // check every already-equipped, already-based survivor still alive
    // across a reload would otherwise get funneled straight back into
    // "craft a sleeping bag/bow/arrows from scratch" the next time its task
    // loop ticks, even though it's genuinely done with that phase of life.
    private const float SkipPrimitiveChecklistGearScoreThreshold = 30f;

    /// <summary>
    /// Real "does this survivor still need the from-scratch checklist at
    /// all" check (2026-09-01) - true (skip it) if ANY of: already has a
    /// base, GearScore already at/above SkipPrimitiveChecklistGearScoreThreshold,
    /// or already owns a real stone/metal-tier gather tool (the whole
    /// PickaxeFamily/HatchetFamily, LivingRust.Looting.cs - not just the
    /// stone tier specifically, so a survivor that's already found a real
    /// metal hatchet doesn't get sent to craft a redundant stone one).
    /// </summary>
    private bool ShouldSkipPrimitiveChecklist(Survivor survivor, BasePlayer npc)
    {
        if (survivor.Character.Home != null)
        {
            return true;
        }

        if (GetGearScore(npc) >= SkipPrimitiveChecklistGearScoreThreshold)
        {
            return true;
        }

        return HasAnyToolOfFamily(npc, PickaxeFamily) || HasAnyToolOfFamily(npc, HatchetFamily);
    }

    private void RollPrimitiveGoalIfFreshLife(Survivor survivor)
    {
        Guid characterId = survivor.Character.Id;

        if (_hasRolledPrimitiveGoal.Contains(characterId))
        {
            return;
        }

        _hasRolledPrimitiveGoal.Add(characterId);

        BasePlayer npc = survivor.Player;

        if (npc != null && !npc.IsDestroyed && ShouldSkipPrimitiveChecklist(survivor, npc))
        {
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' already has a base/real gather tool/GearScore {SkipPrimitiveChecklistGearScoreThreshold:F0}+ - skipping the primitive starter checklist this life.");

            // Real "next step is base building" (2026-09-01, Lucas's own
            // explicit spec: "if the bot manages to hit the checklist its
            // next step is base building... that is the importance of
            // getting the base down"). A skipped checklist counts as
            // already "hit" for this purpose - goes straight to gathering
            // for a base if it doesn't already have one.
            if (survivor.Character.Home == null)
            {
                _pursuingBaseGatherGoal.Add(characterId);
            }

            return;
        }

        _pursuingPrimitiveGoals.Add(characterId);
        Puts($"craft-task: '{survivor.Character.Alias}' is prioritizing its primitive starter checklist (stone tools, sleeping bag, bandages, bow, arrows) this life.");
    }

    // 1s cadence is far coarser than the real 0.1s InvokeRepeating this
    // replaces (see this file's own doc comment), but ItemCrafter.
    // ServerUpdate's own completion check is wall-clock based (endTime vs
    // UnityEngine.Time.realtimeSinceStartup, confirmed via decompile), not
    // dependent on the delta parameter accumulating anything - calling it
    // once a second still finishes a craft within ~1s of its real craft
    // time, plenty precise for a background NPC nobody's watching a
    // progress bar for.
    private const float CraftQueueDriverIntervalSeconds = 1f;

    private Timer _craftQueueDriverTimer;

    /// <summary>
    /// Started from OnServerInitialized (LivingRust.Main.cs), stopped from
    /// Unload - same lifecycle every other engine-wide timer in this
    /// project already follows (see StartOnSightDetection's own doc
    /// comment, LivingRust.Combat.cs). Manually drives every survivor's
    /// real ItemCrafter.queue forward - see this file's own top doc
    /// comment for why that's necessary at all (BasePlayer.InventoryUpdate,
    /// the real caller, is gated on IsConnected, which is never true for
    /// these NPCs). Skips any survivor with an empty queue - CanCraft/
    /// CraftItem calls above only ever add work here when there's
    /// actually something to finish, so this is a cheap no-op the vast
    /// majority of ticks.
    /// </summary>
    private void StartCraftQueueDriver()
    {
        _craftQueueDriverTimer?.Destroy();
        _craftQueueDriverTimer = timer.Every(CraftQueueDriverIntervalSeconds, () =>
        {
            foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
            {
                BasePlayer npc = survivor.Player;

                if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
                {
                    continue;
                }

                ItemCrafter crafter = npc.inventory?.crafting;

                if (crafter == null || crafter.queue.Count == 0)
                {
                    continue;
                }

                crafter.ServerUpdate(CraftQueueDriverIntervalSeconds);
            }
        });
    }

    private void StopCraftQueueDriver()
    {
        _craftQueueDriverTimer?.Destroy();
        _craftQueueDriverTimer = null;
    }

    /// <summary>
    /// Real "did this survivor actually finish its primitive checklist"
    /// check (2026-08-28) - called from ContinueLootTask's own primitive-
    /// goal branch (LivingRust.Looting.cs) instead of just trusting
    /// TryStartCraftingFallback's own true/false return there. Those two
    /// are NOT the same thing: false can mean "every goal is genuinely
    /// satisfied" OR "an ingredient just isn't reachable from here right
    /// now" (TryGatherCraftIngredient's own new diagnostic covers that
    /// case) - live bug found conflating them: 'ToxicRenegade'/
    /// '5DeadTorch' both got permanently kicked out of primitive-goal
    /// priority the moment a local search came up empty even once, having
    /// completed none of the actual checklist. Arrows deliberately checked
    /// as "owns more than zero," not the full 15-batch target - arrows are
    /// an ongoing consumable top-up (TryPursueArrowGoal keeps recrafting
    /// whenever they hit zero for the rest of this life regardless), not a
    /// one-time completion state the way the other four are.
    /// </summary>
    private bool HasCompletedPrimitiveGoals(Survivor survivor, BasePlayer npc)
    {
        if (!_hasPlacedSleepingBag.Contains(survivor.Character.Id))
        {
            return false;
        }

        ItemDefinition bowDef = ItemManager.FindItemDefinition(BowShortname);

        if (bowDef == null || npc.inventory.GetAmount(bowDef.itemid) < 1)
        {
            return false;
        }

        ItemDefinition arrowDef = ItemManager.FindItemDefinition(ArrowShortname);

        if (arrowDef == null || npc.inventory.GetAmount(arrowDef.itemid) <= 0)
        {
            return false;
        }

        // Same "owns more than zero" check as arrows (2026-09-01) -
        // bandages are the same kind of ongoing consumable top-up
        // (TryPursueBandageGoal keeps recrafting whenever they hit zero),
        // not a one-time completion state.
        ItemDefinition bandageDef = ItemManager.FindItemDefinition(BandageShortname);

        if (bandageDef == null || npc.inventory.GetAmount(bandageDef.itemid) <= 0)
        {
            return false;
        }

        ItemDefinition hatchetDef = ItemManager.FindItemDefinition(StoneHatchetShortname);

        if (hatchetDef == null || npc.inventory.GetAmount(hatchetDef.itemid) < 1)
        {
            return false;
        }

        ItemDefinition pickaxeDef = ItemManager.FindItemDefinition(StonePickaxeShortname);

        if (pickaxeDef == null || npc.inventory.GetAmount(pickaxeDef.itemid) < 1)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checked from ContinueLootTask's own "found nothing at all nearby"
    /// branch, right before TryStartResourceGatheringFallback - crafting
    /// is effectively free (instant, zero movement) whenever the survivor
    /// already holds enough raw material, so it's worth deciding before
    /// ever walking anywhere for more. Priority order matches Lucas's own
    /// examples/explicit sequencing: sleeping bag (a one-off safety net),
    /// then bow (a one-off weapon), then arrows (an ongoing consumable
    /// top-up, useless without the bow already owned). Returns whether
    /// this cycle actually started something (a craft, a placement wait,
    /// or a gather-the-missing-ingredient walk), same true/false contract
    /// TryStartResourceGatheringFallback already uses.
    /// </summary>
    private bool TryStartCraftingFallback(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        // Real "only ever one real craft in flight at a time" guard
        // (2026-08-28, live bug found: 'QuietReaper' queued FOUR separate
        // crafts back-to-back within ~90s - stonehatchet, then arrows,
        // then bow, then stonepickaxe - because each goal's own "already
        // mid-craft" branch stopped claiming the cycle (the earlier same-
        // day fix for "bots shouldn't stand still while crafting"), which
        // let the NEXT goal down the priority list start ITS OWN craft
        // immediately rather than waiting. ItemCrafter.queue processes
        // strictly one task at a time though - it doesn't run several
        // crafts in parallel just because several are queued - so each
        // later item ends up waiting behind everything queued ahead of
        // it, and every one of the four ended up blowing through its own
        // wait-timer's timeout (each only ever sized for that ONE item's
        // real craft time, never "everything else queued ahead of it,
        // plus its own time"). The actual fix isn't bigger timeouts - it's
        // never letting more than one real craft queue up at all. This
        // still fully satisfies "don't stand still while crafting": the
        // bot falls through to normal looting/gathering below exactly
        // like before, it just won't ALSO start a second, unrelated craft
        // on top while the first is still in flight - it'll pick the next
        // goal back up automatically once its own wait-timer notices the
        // queue actually emptied.
        ItemCrafter activeCrafter = npc.inventory?.crafting;

        if (activeCrafter != null && activeCrafter.queue.Count > 0)
        {
            return false;
        }

        // Real two-pass "any order, materials-driven" checklist (2026-09-01,
        // Lucas's own explicit correction: "have it so any of those
        // primitive checklists can be done in any order - not specific,
        // just as the bot gains the required materials it can craft
        // whichever"). Pass 1 below checks every not-yet-completed goal
        // with allowGather:false - each Try* function still does its own
        // real CanCraft check, but is stopped from walking off to gather a
        // MISSING ingredient, so this pass only ever claims the cycle for
        // a goal the survivor can craft RIGHT NOW with whatever it's
        // already carrying (e.g. hemp/wood/stone picked up incidentally
        // while working toward a different goal, or looted from a
        // container along the way - see ReactiveLootCategories' own doc
        // comment for that passive accumulation). Whichever goal happens
        // to be ready first in this fixed loop order gets crafted, but
        // that order no longer determines crafting order in practice -
        // it's just iteration order over a set that's usually either empty
        // or has exactly one member ready at a time. Pass 2 only runs if
        // NOTHING was immediately craftable - ingredient GATHERING still
        // needs to commit to one specific target per cycle (can't walk two
        // directions at once), so it falls back to the same fixed order as
        // before purely as a tie-breaker for what to go gather toward, not
        // as a crafting priority.
        if (TryPursueOneOffToolGoal(survivor, npc, state, StoneHatchetShortname, "stone hatchet", allowGather: false)) return true;
        if (TryPursueOneOffToolGoal(survivor, npc, state, StonePickaxeShortname, "stone pickaxe", allowGather: false)) return true;
        if (TryPursueSleepingBagGoal(survivor, npc, state, allowGather: false)) return true;
        if (TryPursueBandageGoal(survivor, npc, state, allowGather: false)) return true;
        if (TryPursueBowGoal(survivor, npc, state, allowGather: false)) return true;
        if (TryPursueArrowGoal(survivor, npc, state, allowGather: false)) return true;

        if (TryPursueOneOffToolGoal(survivor, npc, state, StoneHatchetShortname, "stone hatchet")) return true;
        if (TryPursueOneOffToolGoal(survivor, npc, state, StonePickaxeShortname, "stone pickaxe")) return true;
        if (TryPursueSleepingBagGoal(survivor, npc, state)) return true;
        if (TryPursueBandageGoal(survivor, npc, state)) return true;
        if (TryPursueBowGoal(survivor, npc, state)) return true;
        if (TryPursueArrowGoal(survivor, npc, state)) return true;

        // Basic base-building materials (2026-08-28, Lucas's own explicit
        // list - "other basic items required to craft before going onto
        // base building") - a distinct tier from the primitive starter
        // checklist above: NOT part of _pursuingPrimitiveGoals/
        // HasCompletedPrimitiveGoals (that roll-based priority only ever
        // covered the bag/bow/arrows/stone-tools survival kit, checked
        // BEFORE normal looting for a rolled survivor), these are checked
        // here in the same low-priority "nothing else to do" tier arrows/
        // stone tools already sat in before the roll existed - every
        // survivor opportunistically works toward them, rolled or not.
        // Real recipes confirmed live via /lr.debug.recipe, 2026-08-28
        // (Lucas's own numbers, not guessed):
        //   door.hinged.wood: 300x wood                       (tier 0, 30s)
        //   lock.code:        100x metal.fragments             (tier 0, 30s)
        //   door.hinged.metal:150x metal.fragments             (tier 0, 30s)
        //   cupboard.tool:    1000x wood                       (tier 0, 30s)
        //   box.wooden.large: 250x wood, 50x metal.fragments   (tier 0, 30s)
        //   workbench1:       500x wood, 100x metal.fragments  (tier 0, 30s)
        // metal.fragments has no active gathering source at all in this
        // project yet (no furnace/smelting system exists to turn mined
        // metal.ore into it, and unlike wood/stone/cloth there's no
        // surface collectible that yields it directly) - TryGatherCraftIngredient's
        // switch has no case for it, so it falls through to the same
        // "no source found" diagnostic every other unhandled ingredient
        // already gets. These four items will only ever get crafted once
        // a survivor has passively accumulated enough fragments from
        // normal container looting - a real, honest limitation until a
        // furnace goal exists, not a bug.
        if (TryPursueOneOffToolGoal(survivor, npc, state, CodeLockShortname, "code lock"))
        {
            return true;
        }

        if (TryPursueOneOffToolGoal(survivor, npc, state, SheetMetalDoorShortname, "sheet metal door"))
        {
            return true;
        }

        if (TryPursueOneOffToolGoal(survivor, npc, state, ToolCupboardShortname, "tool cupboard"))
        {
            return true;
        }

        if (TryPursueOneOffToolGoal(survivor, npc, state, LargeWoodBoxShortname, "large wood box"))
        {
            return true;
        }

        if (TryPursueOneOffToolGoal(survivor, npc, state, Workbench1Shortname, "workbench"))
        {
            return true;
        }

        return false;
    }

    private const string StoneHatchetShortname = "stonehatchet";
    private const string StonePickaxeShortname = "stone.pickaxe";

    private const string WoodenDoorShortname = "door.hinged.wood";
    private const string CodeLockShortname = "lock.code";
    private const string SheetMetalDoorShortname = "door.hinged.metal";
    private const string ToolCupboardShortname = "cupboard.tool";
    private const string LargeWoodBoxShortname = "box.wooden.large";
    private const string Workbench1Shortname = "workbench1";

    private readonly Dictionary<(Guid CharacterId, string Shortname), Timer> _pendingOneOffCraftTimers = new();

    // Safety cap only, same reasoning as SleepingBagDeployWaitMaxTicks/
    // ArrowCraftWaitMaxTicks/BowCraftWaitMaxTicks.
    private const int OneOffCraftWaitMaxTicks = 60;

    /// <summary>
    /// Generic "own exactly one of this, craft it if affordable, gather
    /// whatever's missing if not, wait for the real queue to finish, then
    /// resume" shape - reused for both stone tools rather than copy-
    /// pasting TryPursueBowGoal's own bespoke version a third/fourth time.
    /// TryPursueBowGoal/TryPursueSleepingBagGoal are deliberately left as
    /// their own bespoke functions rather than retrofitted onto this - both
    /// were already live-tested working before this was written, not worth
    /// the risk of a refactor touching proven code for two more callers.
    /// </summary>
    private bool TryPursueOneOffToolGoal(Survivor survivor, BasePlayer npc, LootTaskState state, string shortname, string logLabel, bool allowGather = true)
    {
        ItemDefinition def = ItemManager.FindItemDefinition(shortname);
        ItemBlueprint bp = def?.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bp == null || crafter == null)
        {
            return false;
        }

        if (npc.inventory.GetAmount(def.itemid) >= 1)
        {
            return false;
        }

        (Guid, string) key = (survivor.Character.Id, shortname);

        // Already mid-craft - does NOT claim this cycle (2026-08-28,
        // Lucas's own explicit request: bots shouldn't just stand still
        // for the whole real craft time, an easy target out in the open).
        // Crafting is a background/inventory mechanic in real Rust, not a
        // channeled ability - StartCraftQueueDriver ticks the real queue
        // forward regardless of what the survivor is doing, so there's no
        // real reason to freeze the task loop while it finishes. The wait
        // timer below still runs in the background purely to catch
        // completion for whatever needs a follow-up action once it lands.
        if (crafter.queue.Any(task => !task.cancelled && task.blueprint == bp))
        {
            if (!_pendingOneOffCraftTimers.ContainsKey(key))
            {
                WaitForOneOffCraftThenContinue(survivor, state, key);
            }

            return false;
        }

        if (crafter.CanCraft(bp, 1, free: false))
        {
            crafter.CraftItem(bp, npc, amount: 1);
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' is crafting a {logLabel} ({bp.time:F0}s) - continuing other tasks while it finishes.");
            WaitForOneOffCraftThenContinue(survivor, state, key);
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return true;
        }

        ItemAmount missing = bp.GetIngredients()
            .FirstOrDefault(ingredient => npc.inventory.GetAmount(ingredient.itemid) < (int)ingredient.amount);

        if (missing?.itemDef == null)
        {
            return false;
        }

        return allowGather && TryGatherCraftIngredient(survivor, npc, state, missing.itemDef.shortname);
    }

    private void WaitForOneOffCraftThenContinue(Survivor survivor, LootTaskState state, (Guid CharacterId, string Shortname) key)
    {
        if (_pendingOneOffCraftTimers.TryGetValue(key, out Timer existing))
        {
            existing.Destroy();
        }

        int ticks = 0;
        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                _pendingOneOffCraftTimers.Remove(key);
                return;
            }

            ItemCrafter crafter = npc.inventory.crafting;

            if (crafter == null || crafter.queue.Count == 0)
            {
                pollTimer.Destroy();
                _pendingOneOffCraftTimers.Remove(key);

                // Real "drop the rock now that a stone tool exists" fix
                // (2026-09-01, Lucas's own explicit ask) - this shared
                // completion path covers every one-off tool goal
                // (stonehatchet/stone.pickaxe included), and unlike
                // OnLootObtained (container loot only), nothing here was
                // previously running DropRockIfUpgraded/EquipBestWeaponForDisplay/
                // etc after a genuine craft landed. Harmless no-op for the
                // non-tool one-off goals (doors/cupboard/box/workbench)
                // sharing this same function.
                RunLootHookSafely(survivor, nameof(PerformReorganizationCheck), () => PerformReorganizationCheck(survivor, npc));

                ContinueLootTask(survivor, state, forceLocalScan: true);
                return;
            }

            if (++ticks >= OneOffCraftWaitMaxTicks)
            {
                pollTimer.Destroy();
                _pendingOneOffCraftTimers.Remove(key);
                Puts($"craft-task: '{survivor.Character.Alias}' gave up waiting for its {key.Shortname} craft to finish after {OneOffCraftWaitMaxTicks}s - resuming normally.");
                ContinueLootTask(survivor, state, forceLocalScan: true);
            }
        });

        _pendingOneOffCraftTimers[key] = pollTimer;
    }

    private bool TryPursueSleepingBagGoal(Survivor survivor, BasePlayer npc, LootTaskState state, bool allowGather = true)
    {
        Guid characterId = survivor.Character.Id;

        if (_hasPlacedSleepingBag.Contains(characterId))
        {
            return false;
        }

        ItemDefinition bagDef = ItemManager.FindItemDefinition(SleepingBagShortname);
        ItemBlueprint bp = bagDef?.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bp == null || crafter == null)
        {
            return false;
        }

        // Already holding a finished bag (crafted this cycle or an
        // earlier one that hasn't been placed yet, e.g. CanBuild failed
        // last try) - nothing new to start, just make sure a deploy is
        // actually pending for it. Doesn't claim this cycle (2026-08-28,
        // Lucas's own explicit request: bots shouldn't stand still while
        // crafting/waiting) - the watcher below will place it wherever
        // the survivor happens to be the moment CanBuild allows it,
        // whatever else it's doing in the meantime.
        if (npc.inventory.GetAmount(bagDef.itemid) > 0)
        {
            if (!_pendingSleepingBagDeployTimers.ContainsKey(characterId))
            {
                WaitForSleepingBagThenDeploy(survivor, bagDef, state);
            }

            return false;
        }

        // Already mid-craft (real ItemCrafter queue, driven forward by
        // StartCraftQueueDriver above) - just wait for it, don't issue a
        // second CraftItem on top, and don't claim this cycle either -
        // same reasoning as the held-but-undeployed branch above.
        if (crafter.queue.Any(task => !task.cancelled && task.blueprint == bp))
        {
            if (!_pendingSleepingBagDeployTimers.ContainsKey(characterId))
            {
                WaitForSleepingBagThenDeploy(survivor, bagDef, state);
            }

            return false;
        }

        if (crafter.CanCraft(bp, SleepingBagTargetBatches, free: false))
        {
            crafter.CraftItem(bp, npc, amount: SleepingBagTargetBatches);
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' is crafting a sleeping bag ({bp.time:F0}s) - continuing other tasks while it finishes.");
            WaitForSleepingBagThenDeploy(survivor, bagDef, state);
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return true;
        }

        ItemAmount missing = bp.GetIngredients()
            .FirstOrDefault(ingredient => npc.inventory.GetAmount(ingredient.itemid) < (int)ingredient.amount);

        if (missing?.itemDef == null)
        {
            return false;
        }

        return allowGather && TryGatherCraftIngredient(survivor, npc, state, missing.itemDef.shortname);
    }

    // Real 30s craft time (confirmed live via /lr.debug.recipe) plus a
    // generous buffer - safety cap only, so a survivor that dies or has
    // its bag item drop/despawn mid-wait for some unrelated reason
    // doesn't leave an orphaned poll running forever.
    private const int SleepingBagDeployWaitMaxTicks = 45;

    private void WaitForSleepingBagThenDeploy(Survivor survivor, ItemDefinition bagDef, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (_pendingSleepingBagDeployTimers.TryGetValue(characterId, out Timer existing))
        {
            existing.Destroy();
        }

        int ticks = 0;
        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                _pendingSleepingBagDeployTimers.Remove(characterId);
                return;
            }

            if (npc.inventory.GetAmount(bagDef.itemid) > 0)
            {
                pollTimer.Destroy();
                _pendingSleepingBagDeployTimers.Remove(characterId);
                DeploySleepingBagAndAssign(survivor, bagDef, state);
                return;
            }

            if (++ticks >= SleepingBagDeployWaitMaxTicks)
            {
                pollTimer.Destroy();
                _pendingSleepingBagDeployTimers.Remove(characterId);

                // Real safety-net diagnostic (2026-08-28) - the original
                // version of this timeout was silent, which is exactly
                // what made the StartCraftQueueDriver bug (this file's own
                // top doc comment) so hard to notice live: '2ColdRock'
                // just went quiet for 90+ seconds with zero indication
                // anything had gone wrong. If this ever fires again for a
                // genuinely different reason, it should be loud about it.
                Puts($"craft-task: '{survivor.Character.Alias}' gave up waiting for its sleeping bag craft to finish after {SleepingBagDeployWaitMaxTicks}s - resuming normally.");
                ContinueLootTask(survivor, state, forceLocalScan: true);
            }
        });

        _pendingSleepingBagDeployTimers[characterId] = pollTimer;
    }

    /// <summary>
    /// Real placement (2026-08-28) - mirrors Deployer.DoDeploy_Regular
    /// (confirmed via decompile) directly server-side rather than going
    /// through its real RPC path (DoDeploy reads a client-supplied aim
    /// Ray - meaningless for a disconnected survivor with no camera).
    /// Places flat at the survivor's own current ground-snapped position
    /// facing its current body direction, the closest sane equivalent to
    /// "a real player aims roughly forward and clicks." SetDeployedBy is
    /// the exact same SendMessage a real deploy uses - confirmed via
    /// decompile (SleepingBag.SetDeployedBy sets deployerUserID then
    /// AddBagForPlayer registers it in the real per-player bag list),
    /// which is also exactly what the real respawn-point selection reads -
    /// no separate "assign" step needed, placement IS the assignment.
    /// </summary>
    private void DeploySleepingBagAndAssign(Survivor survivor, ItemDefinition bagDef, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        // Every return path below resumes the survivor's normal loop -
        // WaitForSleepingBagThenDeploy's own call into this function was
        // the last thing keeping it busy, so without this it would just
        // sit idle indefinitely on any failure/retry path (a real bug
        // caught in the same live trace as the ItemCrafter.ServerUpdate
        // fix this file's own top doc comment describes - a survivor
        // stuck silent for 90+ seconds turned out to be two separate
        // issues, not one).
        Item bagItem = npc.inventory.FindItemByItemID(bagDef.itemid);

        if (bagItem == null)
        {
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return;
        }

        ItemModDeployable modDeployable = bagDef.GetComponent<ItemModDeployable>();

        if (modDeployable == null)
        {
            Puts($"craft-task: '{survivor.Character.Alias}' has a sleeping bag but it has no ItemModDeployable - can't place it.");
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return;
        }

        if (!npc.CanBuild())
        {
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' can't place a sleeping bag here (no building permission) - will retry once it moves on.");
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return;
        }

        if (!_engine.NavigationManager.TryGetGroundHeight(npc.transform.position, out float groundHeight))
        {
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return;
        }

        Vector3 position = npc.transform.position;
        position.y = groundHeight;

        // Same real formula Deployer.GetDeployedRotation uses (confirmed
        // via decompile) - forward is the surface normal (flat ground =
        // Vector3.up), placeDir hints the facing via the "upwards"
        // parameter.
        Quaternion rotation = Quaternion.LookRotation(Vector3.up, npc.eyes.BodyForward()) * Quaternion.Euler(90f, 0f, 0f);

        BaseEntity bagEntity = GameManager.server.CreateEntity(modDeployable.entityPrefab.resourcePath, position, rotation);

        if (bagEntity == null)
        {
            Puts($"craft-task: '{survivor.Character.Alias}' failed to create a sleeping bag entity ('{modDeployable.entityPrefab.resourcePath}').");
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return;
        }

        bagEntity.skinID = bagItem.skin;
        bagEntity.SendMessage("SetDeployedBy", npc, SendMessageOptions.DontRequireReceiver);
        bagEntity.OwnerID = npc.userID;
        bagEntity.Spawn();

        bagItem.UseItem(1);

        _hasPlacedSleepingBag.Add(survivor.Character.Id);

        Puts($"craft-task: '{survivor.Character.Alias}' placed a sleeping bag - will respawn there on death.");
        ContinueLootTask(survivor, state, forceLocalScan: true);
    }

    private readonly Dictionary<Guid, Timer> _pendingBowCraftTimers = new();

    private bool TryPursueBowGoal(Survivor survivor, BasePlayer npc, LootTaskState state, bool allowGather = true)
    {
        Guid characterId = survivor.Character.Id;
        ItemDefinition bowDef = ItemManager.FindItemDefinition(BowShortname);
        ItemBlueprint bp = bowDef?.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bp == null || crafter == null)
        {
            return false;
        }

        if (npc.inventory.GetAmount(bowDef.itemid) >= BowTargetBatches)
        {
            return false;
        }

        // Already mid-craft - doesn't claim this cycle (2026-08-28, Lucas's
        // own explicit request: bots shouldn't stand still while
        // crafting), same reasoning TryPursueOneOffToolGoal's own doc
        // comment gives.
        if (crafter.queue.Any(task => !task.cancelled && task.blueprint == bp))
        {
            if (!_pendingBowCraftTimers.ContainsKey(characterId))
            {
                WaitForBowCraftThenContinue(survivor, state);
            }

            return false;
        }

        if (crafter.CanCraft(bp, BowTargetBatches, free: false))
        {
            crafter.CraftItem(bp, npc, amount: BowTargetBatches);
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' is crafting a bow.hunting ({bp.time:F0}s) - continuing other tasks while it finishes.");
            WaitForBowCraftThenContinue(survivor, state);
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return true;
        }

        ItemAmount missing = bp.GetIngredients()
            .FirstOrDefault(ingredient => npc.inventory.GetAmount(ingredient.itemid) < (int)ingredient.amount);

        if (missing?.itemDef == null)
        {
            return false;
        }

        return allowGather && TryGatherCraftIngredient(survivor, npc, state, missing.itemDef.shortname);
    }

    // Safety cap only, same reasoning as SleepingBagDeployWaitMaxTicks/
    // ArrowCraftWaitMaxTicks - generous rather than exact since a real
    // craft time wasn't re-queried live for this one (Lucas gave the
    // ingredient numbers directly).
    private const int BowCraftWaitMaxTicks = 60;

    private void WaitForBowCraftThenContinue(Survivor survivor, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (_pendingBowCraftTimers.TryGetValue(characterId, out Timer existing))
        {
            existing.Destroy();
        }

        int ticks = 0;
        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                _pendingBowCraftTimers.Remove(characterId);
                return;
            }

            ItemCrafter crafter = npc.inventory.crafting;

            if (crafter == null || crafter.queue.Count == 0)
            {
                pollTimer.Destroy();
                _pendingBowCraftTimers.Remove(characterId);
                ContinueLootTask(survivor, state, forceLocalScan: true);
                return;
            }

            if (++ticks >= BowCraftWaitMaxTicks)
            {
                pollTimer.Destroy();
                _pendingBowCraftTimers.Remove(characterId);
                Puts($"craft-task: '{survivor.Character.Alias}' gave up waiting for its bow craft to finish after {BowCraftWaitMaxTicks}s - resuming normally.");
                ContinueLootTask(survivor, state, forceLocalScan: true);
            }
        });

        _pendingBowCraftTimers[characterId] = pollTimer;
    }

    private readonly Dictionary<Guid, Timer> _pendingArrowCraftTimers = new();

    private bool TryPursueArrowGoal(Survivor survivor, BasePlayer npc, LootTaskState state, bool allowGather = true, int recraftThreshold = ArrowRecraftThreshold)
    {
        Guid characterId = survivor.Character.Id;
        ItemDefinition arrowDef = ItemManager.FindItemDefinition(ArrowShortname);
        ItemBlueprint bp = arrowDef?.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bp == null || crafter == null)
        {
            return false;
        }

        if (npc.inventory.GetAmount(arrowDef.itemid) > recraftThreshold)
        {
            return false;
        }

        // Already mid-craft (real ItemCrafter queue, driven forward by
        // StartCraftQueueDriver above) - let it finish rather than
        // stacking a second run on top. Makes sure a resume waiter is
        // actually running - real live bug (2026-08-28, 'ShadyCoyote'):
        // the original version of this branch just returned false with
        // nothing waiting on the craft at all, so once CraftItem below was
        // called the survivor's whole task loop went dead until something
        // unrelated (death, combat) happened to wake it back up - the
        // finished arrows just sat unused in inventory forever, same root
        // problem WaitForSleepingBagThenDeploy already solves for the bag
        // goal. Doesn't claim this cycle though (2026-08-28, later same
        // day - Lucas's own explicit follow-up: bots shouldn't stand
        // still while crafting) - the waiter running in the background is
        // enough of a safety net now, no need to also block here.
        if (crafter.queue.Any(task => !task.cancelled && task.blueprint == bp))
        {
            if (!_pendingArrowCraftTimers.ContainsKey(characterId))
            {
                WaitForArrowCraftThenContinue(survivor, state);
            }

            return false;
        }

        int arrowBatches = GetAffordableBatches(crafter, bp, ArrowMaxBatches);

        if (arrowBatches >= 1)
        {
            crafter.CraftItem(bp, npc, amount: arrowBatches);
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' is crafting {arrowBatches}x arrow.wooden batches ({bp.time * arrowBatches:F0}s) - continuing other tasks while it finishes.");
            WaitForArrowCraftThenContinue(survivor, state);
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return true;
        }

        ItemAmount missing = bp.GetIngredients()
            .Where(ingredient => npc.inventory.GetAmount(ingredient.itemid) < (int)ingredient.amount * ArrowMaxBatches)
            .OrderBy(ingredient => (float)npc.inventory.GetAmount(ingredient.itemid) / ((int)ingredient.amount * ArrowMaxBatches))
            .FirstOrDefault();

        if (missing?.itemDef == null)
        {
            return false;
        }

        return allowGather && TryGatherCraftIngredient(survivor, npc, state, missing.itemDef.shortname);
    }

    // Real max craft time (ArrowMaxBatches * arrow.wooden's own 3.0s,
    // confirmed live via /lr.debug.recipe = 45s) plus a generous buffer -
    // safety cap only, same reasoning as SleepingBagDeployWaitMaxTicks.
    private const int ArrowCraftWaitMaxTicks = 70;

    private void WaitForArrowCraftThenContinue(Survivor survivor, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (_pendingArrowCraftTimers.TryGetValue(characterId, out Timer existing))
        {
            existing.Destroy();
        }

        int ticks = 0;
        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                _pendingArrowCraftTimers.Remove(characterId);
                return;
            }

            ItemCrafter crafter = npc.inventory.crafting;

            if (crafter == null || crafter.queue.Count == 0)
            {
                pollTimer.Destroy();
                _pendingArrowCraftTimers.Remove(characterId);
                ContinueLootTask(survivor, state, forceLocalScan: true);
                return;
            }

            if (++ticks >= ArrowCraftWaitMaxTicks)
            {
                pollTimer.Destroy();
                _pendingArrowCraftTimers.Remove(characterId);
                Puts($"craft-task: '{survivor.Character.Alias}' gave up waiting for its arrow craft to finish after {ArrowCraftWaitMaxTicks}s - resuming normally.");
                ContinueLootTask(survivor, state, forceLocalScan: true);
            }
        });

        _pendingArrowCraftTimers[characterId] = pollTimer;
    }

    // Largest batch count (up to maxBatches) the survivor can actually
    // afford right now (2026-09-21, live report: a bot held a bow and had
    // materials for arrows but never made any). The arrow/bandage goals used
    // to demand the FULL batch count's worth of ingredients or craft
    // nothing at all, so a survivor with enough for a few batches sat
    // arrowless while it went off to gather more. 0 = can't afford even one.
    private static int GetAffordableBatches(ItemCrafter crafter, ItemBlueprint bp, int maxBatches)
    {
        for (int batches = maxBatches; batches >= 1; batches--)
        {
            if (crafter.CanCraft(bp, batches, free: false))
            {
                return batches;
            }
        }

        return 0;
    }

    private readonly Dictionary<Guid, Timer> _pendingBandageCraftTimers = new();

    // Real "should want to be at 100 health all the time" bandage-supply
    // gate (2026-09-07, Lucas's own explicit spec) - see
    // TryPursueBandageSupplyIfHurt's own doc comment.
    private const float HurtBandageSupplyHealthThreshold = 80f;

    /// <summary>
    /// Real post-checklist bandage restock (2026-09-07, Lucas's own
    /// explicit follow-up after a live trace found 90%+ of deaths were
    /// bleeding out with no attacker attached - see OnPlayerDeath's own
    /// UNRECORDED-damage doc comment, LivingRust.Hooks.cs). TryPursueBandageGoal
    /// itself already existed and already does exactly the right thing
    /// (recraft-when-low, gather cloth if needed) - its only real gap was
    /// that its ONLY caller was TryStartCraftingFallback, reachable purely
    /// during the primitive-checklist phase (_pursuingPrimitiveGoals).
    /// Once a survivor finishes/skips the checklist, it never restocked
    /// bandages again for the rest of that life, no matter how hurt it
    /// got. This is the same real goal, just reachable from ContinueLootTask's
    /// own universal per-cycle priority chain (checked regardless of
    /// checklist/base-gather/normal-looting phase) and specifically gated
    /// on actually being hurt with nothing left to heal with - "passively
    /// set, not forcefully done," Lucas's own framing: a survivor at full
    /// health, or one that's hurt but still has a real syringe/bandage on
    /// hand (which the new unconditional TryUseMedicalItemIfHurt call
    /// already owns actually USING), has nothing to claim here at all.
    /// </summary>
    // Real survival-kit upkeep (2026-09-21, Lucas's own explicit spec: a
    // bot with a bow, arrows and bandages should be able to fend for
    // itself against animals, scientists and other bots - and a bot that
    // ALREADY carries a real firearm with ammo shouldn't want a bow at
    // all, this is the bare minimum for primitive-level survivors). Runs
    // from ContinueLootTask's universal priority chain (after the
    // checklist, which owns all of this itself while it's active), so it
    // claims the cycle - crafting or fetching cloth/wood - until the kit is
    // complete, which is what actually gates roaming/looting. Every goal
    // it calls already backs off (cooldowns, "no source nearby") when the
    // ingredients genuinely can't be found, so it can't trap a survivor.
    private const int SurvivalKitMinBandages = 3;
    private const int SurvivalKitMinArrows = 10;

    private bool TryPursueSurvivalKitUpkeep(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        if (_pursuingPrimitiveGoals.Contains(survivor.Character.Id))
        {
            return false;
        }

        // Counts EVERY heal item it would actually use (syringes, medkits,
        // bandages - CombatHealItemPriority), not just bandages: a geared
        // survivor carrying syringes has a real healing supply already and
        // shouldn't go crafting bandages. This only ever runs from
        // ContinueLootTask, which returns immediately while a fight is
        // active (_activeCombat), so it can't pull anyone out of combat -
        // it's just what a survivor reaches for once a fight has ended.
        int healSupply = 0;

        foreach (string healShortname in CombatHealItemPriority)
        {
            ItemDefinition healDef = ItemManager.FindItemDefinition(healShortname);

            if (healDef != null)
            {
                healSupply += npc.inventory.GetAmount(healDef.itemid);
            }
        }

        ItemDefinition bandageDef = ItemManager.FindItemDefinition(BandageShortname);

        if (bandageDef != null
            && healSupply < SurvivalKitMinBandages
            && TryPursueBandageGoal(survivor, npc, state, allowGather: true, recraftThreshold: SurvivalKitMinBandages - 1))
        {
            return true;
        }

        if (HasReadyFirearm(npc))
        {
            return false;
        }

        if (!HasBowFamilyWeapon(npc))
        {
            return TryPursueBowGoal(survivor, npc, state);
        }

        ItemDefinition arrowDef = ItemManager.FindItemDefinition(ArrowShortname);

        return arrowDef != null
            && npc.inventory.GetAmount(arrowDef.itemid) < SurvivalKitMinArrows
            && TryPursueArrowGoal(survivor, npc, state, allowGather: true, recraftThreshold: SurvivalKitMinArrows - 1);
    }

    private bool TryPursueBandageSupplyIfHurt(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        if (npc.health >= HurtBandageSupplyHealthThreshold)
        {
            return false;
        }

        if (FindBestHealItem(npc) != null)
        {
            return false;
        }

        return TryPursueBandageGoal(survivor, npc, state);
    }

    /// <summary>
    /// Real bandage goal (2026-09-01, part of the primitive checklist's
    /// explicit cloth split - "20 to make some bandages") - same shape as
    /// TryPursueArrowGoal (recraft-when-empty batch goal, not a one-off),
    /// since bandages get consumed the same ongoing way arrows do.
    /// </summary>
    private bool TryPursueBandageGoal(Survivor survivor, BasePlayer npc, LootTaskState state, bool allowGather = true, int recraftThreshold = BandageRecraftThreshold)
    {
        Guid characterId = survivor.Character.Id;
        ItemDefinition bandageDef = ItemManager.FindItemDefinition(BandageShortname);
        ItemBlueprint bp = bandageDef?.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bp == null || crafter == null)
        {
            return false;
        }

        if (npc.inventory.GetAmount(bandageDef.itemid) > recraftThreshold)
        {
            return false;
        }

        if (crafter.queue.Any(task => !task.cancelled && task.blueprint == bp))
        {
            if (!_pendingBandageCraftTimers.ContainsKey(characterId))
            {
                WaitForBandageCraftThenContinue(survivor, state);
            }

            return false;
        }

        int bandageBatches = GetAffordableBatches(crafter, bp, BandageMaxBatches);

        if (bandageBatches >= 1)
        {
            crafter.CraftItem(bp, npc, amount: bandageBatches);
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' is crafting {bandageBatches}x bandage batches ({bp.time * bandageBatches:F0}s) - continuing other tasks while it finishes.");
            WaitForBandageCraftThenContinue(survivor, state);
            ContinueLootTask(survivor, state, forceLocalScan: true);
            return true;
        }

        ItemAmount missing = bp.GetIngredients()
            .Where(ingredient => npc.inventory.GetAmount(ingredient.itemid) < (int)ingredient.amount * BandageMaxBatches)
            .OrderBy(ingredient => (float)npc.inventory.GetAmount(ingredient.itemid) / ((int)ingredient.amount * BandageMaxBatches))
            .FirstOrDefault();

        if (missing?.itemDef == null)
        {
            return false;
        }

        return allowGather && TryGatherCraftIngredient(survivor, npc, state, missing.itemDef.shortname);
    }

    // Real max craft time (BandageMaxBatches * bandage's own 5.0s = 25s)
    // plus a generous buffer - safety cap only, same reasoning as
    // ArrowCraftWaitMaxTicks.
    private const int BandageCraftWaitMaxTicks = 45;

    private void WaitForBandageCraftThenContinue(Survivor survivor, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        if (_pendingBandageCraftTimers.TryGetValue(characterId, out Timer existing))
        {
            existing.Destroy();
        }

        int ticks = 0;
        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                _pendingBandageCraftTimers.Remove(characterId);
                return;
            }

            ItemCrafter crafter = npc.inventory.crafting;

            if (crafter == null || crafter.queue.Count == 0)
            {
                pollTimer.Destroy();
                _pendingBandageCraftTimers.Remove(characterId);
                ContinueLootTask(survivor, state, forceLocalScan: true);
                return;
            }

            if (++ticks >= BandageCraftWaitMaxTicks)
            {
                pollTimer.Destroy();
                _pendingBandageCraftTimers.Remove(characterId);
                Puts($"craft-task: '{survivor.Character.Alias}' gave up waiting for its bandage craft to finish after {BandageCraftWaitMaxTicks}s - resuming normally.");
                ContinueLootTask(survivor, state, forceLocalScan: true);
            }
        });

        _pendingBandageCraftTimers[characterId] = pollTimer;
    }

    /// <summary>
    /// Dispatches a real gather trip for one specific missing recipe
    /// ingredient. Wood/stone check a real surface-pile collectible
    /// within CraftIngredientSearchRadius first (Lucas's own explicit
    /// spec, 2026-08-28: "go find enough stone and wood collectable
    /// entities OR if none are found in a 50m radius, have them farm a
    /// tree and a stone node"), falling back to the exact same active
    /// tree/ore gathering (GatherTreeAndContinue/GatherOreAndContinue,
    /// LivingRust.ResourceGathering.cs) the general resource fallback
    /// uses. Cloth uses a new hemp-collectible detour - Rust's own real
    /// source for it (confirmed live this session: a hemp collectible's
    /// DoPickup already yields "cloth" alongside "seed.hemp").
    /// Deliberately bypasses the sticky tree-vs-ore gather-type lock
    /// (TryStartResourceGatheringFallback's own _resourceGatherTypeLock) -
    /// that lock exists to stop the GENERIC fallback from randomly mixing
    /// resource types, but a specific recipe needing wood one cycle and
    /// stone the next is a real, deliberate reason to switch, not mixing.
    /// </summary>
    // Real per-ingredient skip-ahead (2026-09-01, Lucas's own explicit
    // ask: "#2 could be a good fix" - see TryStartCraftingFallback's own
    // doc comment for the full "stuck retrying the same blocked
    // ingredient every single cycle" problem this solves). Once a real
    // search for a specific ingredient genuinely finds nothing, that
    // EXACT (survivor, ingredient) pair is cooled down for
    // IngredientSearchCooldownSeconds - every goal that needs it (stone
    // hatchet AND stone pickaxe both need stones, for example) fails fast
    // without repeating the same real search, letting the checklist's own
    // sequential fallback chain reach bow/bandage/sleeping bag - whatever
    // doesn't need the blocked ingredient - instead of burning the whole
    // cycle stuck on the first thing in priority order. Cleared the
    // instant a search actually succeeds, so a genuinely resolved shortage
    // (the survivor wandered somewhere better) isn't held back by a stale
    // cooldown.
    private const float IngredientSearchCooldownSeconds = 120f;

    private readonly Dictionary<(Guid CharacterId, string Ingredient), float> _ingredientSearchCooldownUntil = new();

    private bool TryGatherCraftIngredient(Survivor survivor, BasePlayer npc, LootTaskState state, string ingredientShortname)
    {
        (Guid, string) cooldownKey = (survivor.Character.Id, ingredientShortname);

        if (_ingredientSearchCooldownUntil.TryGetValue(cooldownKey, out float cooldownUntil) && UnityEngine.Time.realtimeSinceStartup < cooldownUntil)
        {
            return false;
        }

        bool found = ingredientShortname switch
        {
            // HasEnoughWoodAlready gate (2026-09-19, LivingRust.Looting.cs) -
            // live report: 'GrimMarauder' repeatedly hit this exact "needs
            // more wood to craft - heading to a nearby tree" branch, a real
            // contributor (alongside the two other now-capped sources) to
            // it carrying 7000 wood with no base even started. The
            // checklist's own real wood needs (sleeping bag/bow/arrows) are
            // modest - a survivor already sitting on plenty shouldn't
            // commit to a whole extra tree just because ONE specific
            // recipe step's own narrow check ran short. Collectible pickup
            // stays uncapped (a small ground pile, not a full-node
            // commitment).
            WoodShortname => TryGatherViaCollectible(survivor, npc, state, WoodCollectablePrefabSubstring, "wood")
                || (!HasEnoughWoodAlready(survivor, npc) && TryGatherViaTree(survivor, npc, state)),
            StoneShortname => TryGatherViaCollectible(survivor, npc, state, StoneCollectablePrefabSubstring, "stone")
                || TryGatherViaOre(survivor, npc, state),
            ClothShortname => TryGatherViaCollectible(survivor, npc, state, HempCollectablePrefabSubstring, "cloth")
                || TryPursueAnimalHuntForCloth(survivor, npc, state),

            // Real "full scan of all ores" addition (2026-09-01, Lucas's
            // own explicit ask, alongside the search-radius widening
            // above) - metal.ore/sulfur.ore previously had NO case here at
            // all (fell straight to the false default), unlike stones.
            // Reuses the exact same TryGatherViaOre "find nearest real ore
            // node, gather whatever it yields" mechanism stones already
            // uses - this project doesn't discriminate by node type
            // (stone/sulfur/metal deposits all look the same to
            // TryFindNearestOreResourceEntity), which is already how the
            // existing stone case has always behaved, not a new risk.
            "metal.ore" => TryGatherViaOre(survivor, npc, state),
            "sulfur.ore" => TryGatherViaOre(survivor, npc, state),

            _ => false,
        };

        // Real diagnostic (2026-08-28, live bug found: 'ToxicRenegade'/
        // '5DeadTorch' both got silently kicked out of primitive-goal
        // priority the first time an ingredient genuinely wasn't reachable
        // nearby, with zero log trace explaining why - this exhaustion
        // case was completely silent before). Not itself a failure worth
        // fixing here - a real map position can genuinely have no hemp/
        // wood/stone within CraftIngredientSearchRadius, same as any other
        // "nothing found nearby" case elsewhere in this project - but it
        // needs to be visible, and (see ContinueLootTask's own updated
        // primitive-goal check, LivingRust.Looting.cs) must NOT be
        // confused with "goal complete."
        if (!found)
        {
            _ingredientSearchCooldownUntil[cooldownKey] = UnityEngine.Time.realtimeSinceStartup + IngredientSearchCooldownSeconds;
            VerbosePuts($"craft-task: '{survivor.Character.Alias}' needs more {ingredientShortname} to craft but found no source within {CraftIngredientSearchRadius:F0}m - skipping ahead to other goals for {IngredientSearchCooldownSeconds:F0}s.");
        }
        else
        {
            _ingredientSearchCooldownUntil.Remove(cooldownKey);
        }

        return found;
    }

    private const string HempCollectablePrefabSubstring = "hemp-collectable";

    private bool TryGatherViaCollectible(Survivor survivor, BasePlayer npc, LootTaskState state, string prefabSubstring, string logLabel)
    {
        if (!_engine.NavigationManager.TryFindNearestCollectible(
                npc.transform.position,
                CraftIngredientSearchRadius,
                out CollectibleEntity collectible,
                candidate => !state.Visited.Contains(candidate.net.ID)
                    && !IsLootTargetClaimed(candidate.net.ID)
                    && !IsInPoisonedZone(candidate.transform.position, state)
                    && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                    && !IsInMonumentAvoidZone(candidate.transform.position)
                    && !IsBelowSafeLootDepth(candidate.transform.position)
                    && candidate.itemList != null
                    && candidate.itemList.Length > 0
                    && IsResourceAllowedForHighGear(survivor, npc, candidate.itemList)
                    && candidate.ShortPrefabName.IndexOf(prefabSubstring, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return false;
        }

        state.Visited.Add(collectible.net.ID);
        ClaimLootTarget(state, collectible.net.ID);
        VerbosePuts($"craft-task: '{survivor.Character.Alias}' needs more {logLabel} to craft - heading to a nearby {logLabel} pile.");
        GatherCraftCollectibleAndContinue(survivor, collectible, state);
        return true;
    }

    private bool TryGatherViaTree(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        if (!HasAnyGatherCapableTool(npc, TreeGatherToolPriority))
        {
            return false;
        }

        if (!_engine.NavigationManager.TryFindNearestTreeEntity(
                npc.transform.position,
                CraftIngredientSearchRadius,
                out TreeEntity tree,
                candidate => !state.Visited.Contains(candidate.net.ID)
                    && !IsLootTargetClaimed(candidate.net.ID)
                    && !IsInPoisonedZone(candidate.transform.position, state)
                    && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                    && !IsInMonumentAvoidZone(candidate.transform.position)
                    && !IsBelowSafeLootDepth(candidate.transform.position)
                    && !IsResourceNodePoisoned(candidate)))
        {
            return false;
        }

        state.Visited.Add(tree.net.ID);
        ClaimLootTarget(state, tree.net.ID);
        VerbosePuts($"craft-task: '{survivor.Character.Alias}' needs more wood to craft - heading to a nearby tree.");
        GatherTreeAndContinue(survivor, tree, state);
        return true;
    }

    private bool TryGatherViaOre(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        if (!HasAnyGatherCapableTool(npc, OreGatherToolPriority))
        {
            return false;
        }

        if (!_engine.NavigationManager.TryFindNearestOreResourceEntity(
                npc.transform.position,
                CraftIngredientSearchRadius,
                out OreResourceEntity ore,
                candidate => !state.Visited.Contains(candidate.net.ID)
                    && !IsLootTargetClaimed(candidate.net.ID)
                    && !IsInPoisonedZone(candidate.transform.position, state)
                    && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                    && !IsInMonumentAvoidZone(candidate.transform.position)
                    && !IsBelowSafeLootDepth(candidate.transform.position)
                    && !IsResourceNodePoisoned(candidate)))
        {
            return false;
        }

        state.Visited.Add(ore.net.ID);
        ClaimLootTarget(state, ore.net.ID);
        VerbosePuts($"craft-task: '{survivor.Character.Alias}' needs more stone to craft - heading to a nearby ore node.");
        GatherOreAndContinue(survivor, ore, state);
        return true;
    }

    /// <summary>
    /// Same walk-then-gather shape as GatherTreeAndContinue/GatherOreAndContinue
    /// (LivingRust.ResourceGathering.cs), for a real collectible pile
    /// (hemp/wood/stone) - PickupCollectibleAndContinue itself already
    /// handles the actual pickup/range check, this just supplies the
    /// missing walk leg the same way the tree/ore wrappers do.
    /// </summary>
    private void GatherCraftCollectibleAndContinue(Survivor survivor, CollectibleEntity collectible, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        Vector3 approachPoint = GetApproachPoint(collectible, npc);

        StartWalkingWithRecovery(
            survivor,
            approachPoint,
            onArrived: () => PickupCollectibleAndContinue(survivor, collectible, state, onDone: () => ContinueLootTask(survivor, state, forceLocalScan: true)),
            onFailed: () =>
            {
                BasePlayer laterNpc = survivor.Player;

                if (laterNpc == null || laterNpc.IsDestroyed)
                {
                    return;
                }

                ContinueLootTask(survivor, state, forceLocalScan: true);
            });
    }
}
