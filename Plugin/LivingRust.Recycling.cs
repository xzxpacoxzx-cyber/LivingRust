using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Recycler workflow (2026-08-15) - Lucas's own explicit answer to "what
/// happens when a bot's inventory is full": rather than idling forever
/// (the one remaining idle dead-end left after the same session's
/// never-idle escalation-ladder work), a full survivor now heads for the
/// nearest usable recycler, dumps its own low-value junk into it, stands
/// guard facing AWAY from it while it runs (scanning slowly left-to-right,
/// not a full 360 - Lucas's own explicit spec), then collects whatever
/// scrap/materials come out. Deliberately stops there once done (Lucas's
/// own explicit "idles (for now)") - resuming the loot task automatically
/// afterward is a natural follow-up, not built yet.
///
/// Confirmed via decompiling Assembly-CSharp.dll: Recycler : StorageContainer,
/// with a single 12-slot inventory (slots 0-5 = input, only accepting
/// items with a real Blueprint; slots 6-11 = output, where
/// scrap/materials land). StartRecycling()/StopRecycling()/IsOn() are all
/// public and safe to call directly server-side (bypassing the client-only
/// SVSwitch RPC entirely). Recycling stops itself automatically once
/// nothing recyclable is left in the input slots (RecycleThink's own
/// `if (flag || !HasRecyclable()) StopRecycling();`), so polling IsOn()
/// for the true-to-false transition is a reliable "it's finished" signal.
/// </summary>
public partial class LivingRust
{
    private const float RecyclerSearchRadius = 400f;
    private const float RecycleSentryTickIntervalSeconds = 1f;
    private const float RecycleSentryScanHalfAngleDegrees = 60f;
    private const float RecycleSentryScanPeriodSeconds = 6f;

    /// <summary>
    /// A real recycler takes 6 input stacks at a time, with 6 output slots
    /// right after them - matches both Lucas's own confirmed correction
    /// (2026-08-15) and the decompiled RecycleThink/MoveItemToOutput loops
    /// (0-6 input, 6-12 output). Input slots: 0-5. Output slots: 6-11.
    /// </summary>
    private const int RecyclerInputSlotCount = 6;
    private const int RecyclerOutputSlotStart = 6;
    private const int RecyclerOutputSlotCount = 6;

    private readonly Dictionary<Guid, Timer> _activeRecycling = new();

    /// <summary>
    /// Which recycler a survivor has actually fed and started, if any - set
    /// the instant StartRecycling() is called, cleared only once
    /// FinishRecycling actually collects from it (or the survivor gives up
    /// on it outright). Deliberately a SEPARATE dictionary from
    /// _activeRecycling (the sentry-timer registry) - combat/flee cancel
    /// the timer immediately (see CancelActiveRecycling) but must NOT erase
    /// which recycler was in progress, since that's exactly what
    /// ResumeOrStartLootTask needs afterward to go back and collect it
    /// rather than leaving real components stranded (2026-08-15, Lucas's
    /// own explicit concern: "so it doesn't leave motherloads of components
    /// behind").
    /// </summary>
    private readonly Dictionary<Guid, Recycler> _pendingRecyclerToResume = new();

    /// <summary>
    /// Marks that the current (or most recently started) recycler trip was
    /// triggered by GhostRouteLootScan noticing a full main inventory
    /// mid-ghost-route (2026-08-17, Lucas's own explicit request: "if the
    /// bot has a full inventory... it breaks the ghostroute, does the WHOLE
    /// Recycler task... then goes back to the ghost route... the same way
    /// that Combat interrupts it"). Read by ResumeOrStartLootTask to
    /// prioritize finishing THIS recycler trip over TryResumeGhostRoute if
    /// combat interrupts the recycling itself - the fed/half-recycled items
    /// already sitting in the recycler matter more than getting back onto
    /// the route a few seconds sooner. Read again by
    /// FinishRecyclerDetourOrIdle to resume the ghost route instead of
    /// idling once the recycler trip is genuinely done or abandoned.
    /// Cleared the instant either of those happens - see both call sites.
    /// </summary>
    private readonly HashSet<Guid> _ghostRouteRecyclerDetour = new();

    /// <summary>
    /// Common "recycling is done/abandoned" exit point every genuine
    /// termination branch below funnels through (2026-08-17) - either goes
    /// back to plain idle (TaskType.None, the original recycler-only
    /// behaviour) or, if this trip was a ghost-route detour
    /// (_ghostRouteRecyclerDetour), resumes the ghost route instead of
    /// stopping at all. Falls back to idle if there's nothing left to
    /// resume (route itself already finished/was abandoned some other way
    /// in the meantime).
    /// </summary>
    private void FinishRecyclerDetourOrIdle(Survivor survivor)
    {
        if (_ghostRouteRecyclerDetour.Remove(survivor.Character.Id) && TryResumeGhostRoute(survivor))
        {
            return;
        }

        // Real fix (2026-09-19, Lucas's own live report: "bots should
        // never be stuck frozen after recycling"). Same orphaning bug
        // already found and fixed once this session for
        // RescueGenuinelyStalledSurvivor (LivingRust.Commands.cs) - setting
        // CurrentTask = None does NOT hand control to any other system;
        // the whole task pipeline is a pure callback chain
        // (StartLootForResourcesTask's own doc comment: "no separate
        // task-tick timer"). A survivor that just finished a completely
        // normal (non-ghost-route) recycle-loot-deposit trip was being
        // silently orphaned here - everything upstream worked correctly
        // (fed the recycler, stood guard, looted the output, walked home
        // and deposited via GhostReturnHomeAndDeposit), then simply
        // stopped dead instead of re-entering the loop.
        BasePlayer npc = survivor.Player;

        if (npc != null && !npc.IsDestroyed && survivor.Character.State != CharacterState.Dead)
        {
            StartLootForResourcesTask(survivor);
        }
        else
        {
            survivor.Character.CurrentTask = TaskType.None;
        }
    }

    /// <summary>
    /// Real recycle fodder only - anything with no Blueprint can't be
    /// recycled at all (confirmed via Recycler.CanBeRecycled's own check),
    /// and even among recyclable items this deliberately only feeds the
    /// bottom two loot-priority tiers (Components/Other - real junk:
    /// gears, tech trash, misc components) so a bot never feeds its own
    /// weapon, armor, medical supplies, or ammo into a recycler just
    /// because inventory happened to be full.
    /// </summary>
    private static bool IsRecycleFodder(Item item)
    {
        // Never feed away a real puzzle fuse (2026-08-17) - it's genuine
        // Components-tier junk by GetLootPriorityTier's own logic, but a
        // survivor en route to (or already committed to) solving a card
        // puzzle needs to actually hold onto it - see
        // LivingRust.CardPuzzles.cs. A full inventory triggering an
        // automatic recycler trip (see _ghostRouteRecyclerDetour's own doc
        // comment) could otherwise burn the bot's only fuse right before it
        // ever reaches the reader.
        if (item.info.shortname == "fuse" || item.info.shortname == "fuse.highgrade")
        {
            return false;
        }

        // Never feed these away either (2026-09-19, Lucas's own explicit
        // ask). All four are real Blueprint-bearing, Tool/Construction-
        // category items that land in the catch-all Other tier below (not
        // Weapon/Armor/Medical/Ammo, so nothing above already excluded
        // them) - a tool cupboard is a base's actual ownership anchor, a
        // stone hatchet/pickaxe is very likely the survivor's ONLY gather
        // tool right when inventory happens to be full (exactly when a
        // recycler detour triggers), and a water bottle/jug is active
        // hydration, not spare junk - recycling any of the four for a
        // handful of scrap was never worth what it cost the survivor.
        if (item.info.shortname == ToolCupboardShortname
            || item.info.shortname == StoneHatchetShortname
            || item.info.shortname == StonePickaxeShortname
            || item.info.shortname == WaterBottleShortname
            || item.info.shortname == WaterJugShortname)
        {
            return false;
        }

        // Tools, construction pieces and food are never junk (2026-09-21,
        // live report: recyclers found full of hammers, building plans and
        // similar - both are required to build and were feeding straight in
        // because the catch-all Other tier admitted anything with a
        // blueprint). Explicit shortnames as well as categories, so a
        // miscategorised item can't slip through.
        if (item.info.shortname == HammerShortname
            || item.info.shortname == BuildingPlannerShortname
            || item.info.category == ItemCategory.Tool
            || item.info.category == ItemCategory.Construction
            || item.info.category == ItemCategory.Food)
        {
            return false;
        }

        if (item.info.Blueprint == null)
        {
            return false;
        }

        LootPriorityTier tier = GetLootPriorityTier(item);

        return tier == LootPriorityTier.Components || tier == LootPriorityTier.Other;
    }

    /// <summary>
    /// Two independent triggers, both wired 2026-08-15 (Lucas's own
    /// explicit spec): (A) ContinueLootTask calls this once main inventory
    /// alone is full - belt-only fullness no longer counts, see its own
    /// call site - and (B) NeverIdleFallback tries this FIRST, before its
    /// existing reroll/road logic, the moment a task has genuinely
    /// exhausted every known loot zone - regardless of whether the
    /// survivor's inventory has room to spare, since converting junk into
    /// usable scrap/materials is worth a detour any time there's nothing
    /// better to do nearby, not just when carrying capacity forces it.
    ///
    /// Returns false (does nothing else, doesn't touch CurrentTask) if
    /// nothing this survivor is carrying is actually recycle fodder
    /// (weapons/armor/medical/ammo never qualify - see IsRecycleFodder) or
    /// no usable recycler is in range - callers decide what "no, thanks"
    /// means for their own context (ContinueLootTask's full-inventory call
    /// falls back to the old idle; NeverIdleFallback just moves on to its
    /// own reroll/road logic instead). Safezone recyclers (Outpost/Bandit
    /// Camp) are excluded the same way those monuments already are from
    /// every other autonomous decision.
    /// </summary>
    private bool TryStartRecyclingTask(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return false;
        }

        bool hasFodder = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Any(IsRecycleFodder);

        if (!hasFodder)
        {
            return false;
        }

        if (!_engine.NavigationManager.TryFindNearestRecycler(
            npc.transform.position,
            RecyclerSearchRadius,
            out Recycler recycler,
            candidate => candidate.GetRecyclerState() != Recycler.RecyclerState.Unpowered
                && !candidate.IsSafezoneRecycler()
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)))
        {
            return false;
        }

        survivor.Character.CurrentTask = TaskType.Recycling;

        Vector3 approachPoint = GetApproachPoint(recycler, npc);

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' is heading to a recycler to free up space ({Vector3.Distance(npc.transform.position, approachPoint):F0}m away).");

        StartWalkingWithRecovery(
            survivor,
            approachPoint,
            onArrived: () => BeginRecycling(survivor, recycler),
            onFailed: () =>
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach a recycler - done looting.");
                FinishRecyclerDetourOrIdle(survivor);
            });

        return true;
    }

    /// <summary>
    /// Hard cap on recycling rounds at one recycler visit (2026-08-15,
    /// pure safety net) - purely a termination guarantee against a
    /// pathological edge case (some item that keeps passing IsRecycleFodder
    /// but never actually transfers), same "near-impossible but not
    /// actually impossible" reasoning as MaxDistanceDecisionRerolls
    /// (LivingRust.GearScore.cs). Real inventories (main + belt, ~30 slots
    /// total) can never need more than ~5 rounds at 6 stacks/round, so this
    /// never limits genuine commitment to clearing everything out.
    /// </summary>
    private const int MaxRecycleRoundsPerVisit = 12;

    /// <summary>
    /// Feeds every real fodder item into the recycler's input slots
    /// (Item.MoveToContainer routes through Recycler's own
    /// RecyclerItemFilter automatically, so anything that genuinely can't
    /// be recycled or won't fit just fails to move, no separate check
    /// needed here beyond the fodder pre-filter), starts it, and begins
    /// the sentry-guard loop. Only 6 stacks fit per round (a real
    /// recycler's own input capacity) - FinishRecycling calls this again
    /// for another round if fodder remains once a round completes, rather
    /// than stopping after just the first 6 stacks (2026-08-15, Lucas's
    /// own explicit correction: "it needs to commit and recycle its
    /// inventory, not just 6 stacks of items").
    /// </summary>
    private void BeginRecycling(Survivor survivor, Recycler recycler, int round = 1)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || recycler == null || recycler.IsDestroyed)
        {
            FinishRecyclerDetourOrIdle(survivor);
            return;
        }

        if (round > MaxRecycleRoundsPerVisit)
        {
            Puts($"loot-task: '{survivor.Character.Alias}' hit the {MaxRecycleRoundsPerVisit}-round recycling safety cap - stopping with whatever's left in inventory.");
            FinishRecyclerDetourOrIdle(survivor);
            return;
        }

        // Sweep any recycle fodder still sitting in the belt into main
        // first (2026-08-15, Lucas's own explicit request) - same
        // "the belt's only intended occupants are weapon/medical/gather-
        // tool/overflow" hygiene DeclutterBeltOfWearables already applies
        // to stray wearables (LivingRust.Looting.cs), now extended to
        // components too. Best-effort only - MoveToContainer just fails
        // silently if main has no room yet (exactly the case on round 1
        // when main being full is often the whole reason a survivor's
        // here), which is fine: the fodder search two lines down still
        // pulls from both containers regardless, so nothing that fails to
        // consolidate is ever skipped from actually being fed/recycled.
        // Re-run fresh at the top of every round, not just once, so
        // whatever space a previous round's feed just freed up in main
        // lets the next round's sweep actually succeed.
        foreach (Item beltItem in npc.inventory.containerBelt.itemList.Where(IsRecycleFodder).ToList())
        {
            beltItem.MoveToContainer(npc.inventory.containerMain);
        }

        // Clear the recycler before using it (2026-09-21, Lucas's own
        // explicit spec: "before looting, check the recycler's input/output
        // boxes, if they are full loot them, then deposit the items it
        // wants to recycle"). Finished output is always collected; leftover
        // INPUT stacks (junk another survivor - or an older bot - left
        // behind, hammers and plans included) are only pulled out when the
        // recycler isn't currently running, so nobody's active batch gets
        // taken mid-recycle.
        int clearedFromRecycler = 0;

        for (int slot = 0; slot < RecyclerOutputSlotStart + RecyclerOutputSlotCount; slot++)
        {
            bool isInputSlot = slot < RecyclerInputSlotCount;

            if (isInputSlot && recycler.IsOn())
            {
                continue;
            }

            Item leftover = recycler.inventory.GetSlot(slot);

            if (leftover != null && (leftover.MoveToContainer(npc.inventory.containerMain) || npc.inventory.GiveItem(leftover)))
            {
                clearedFromRecycler++;
            }
        }

        if (clearedFromRecycler > 0)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' cleared {clearedFromRecycler} item stack(s) out of the recycler before using it.");
        }

        // Only the first RecyclerInputSlotCount fodder items can actually
        // go in (real recyclers take 6 input stacks at a time, not one per
        // raw inventory slot - Lucas's own explicit correction, 2026-08-15).
        // Ordered by tier first so, if there's more fodder than room,
        // genuine Components get fed ahead of catch-all Other junk rather
        // than whichever happened to come first in inventory order.
        List<Item> fodder = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(IsRecycleFodder)
            .OrderBy(GetLootPriorityTier)
            .Take(RecyclerInputSlotCount)
            .ToList();

        int fed = 0;

        foreach (Item item in fodder)
        {
            if (item.MoveToContainer(recycler.inventory))
            {
                fed++;
            }
        }

        if (fed == 0 || !recycler.HasRecyclable() || recycler.GetRecyclerState() == Recycler.RecyclerState.Unpowered)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' reached a recycler but couldn't actually feed it anything - done looting.");
            FinishRecyclerDetourOrIdle(survivor);
            return;
        }

        recycler.StartRecycling();
        _pendingRecyclerToResume[survivor.Character.Id] = recycler;

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' fed {fed} item stack(s) into a recycler (round {round}) and is standing guard while it runs.");

        StartRecycleGuardLoop(survivor, recycler, round);
    }

    /// <summary>
    /// The actual sentry-guard timer - split out from BeginRecycling
    /// (2026-08-15) so ResumeRecyclingAfterCombat can restart the exact
    /// same loop after a fight without re-feeding/re-starting the
    /// recycler. Facing base direction is recomputed fresh each call
    /// rather than passed in, so this reads correctly whether it's being
    /// started for the first time or resumed from a completely different
    /// approach angle after combat moved the survivor around. round is
    /// only threaded through so FinishRecycling can pass round + 1 back
    /// into BeginRecycling if more fodder remains - a post-combat resume
    /// (ResumeRecyclingAfterCombat) doesn't track the exact round it left
    /// off on and just passes 1, which only affects MaxRecycleRoundsPerVisit's
    /// safety-cap counting, not correctness.
    /// </summary>
    private void StartRecycleGuardLoop(Survivor survivor, Recycler recycler, int round = 1)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || recycler == null || recycler.IsDestroyed)
        {
            return;
        }

        // Base facing is straight away from the recycler (back to the
        // machine, eyes on the surroundings) - Lucas's own explicit spec.
        // Falls back to whatever the bot's already facing in the
        // vanishingly rare case it's standing exactly on the recycler's
        // own origin point (zero-length direction).
        Vector3 guardBaseDirection = npc.transform.position - recycler.transform.position;
        guardBaseDirection.y = 0f;

        if (guardBaseDirection.sqrMagnitude < 0.01f)
        {
            guardBaseDirection = npc.transform.forward;
        }

        guardBaseDirection.Normalize();

        float startTime = UnityEngine.Time.realtimeSinceStartup;
        Guid characterId = survivor.Character.Id;

        Timer recycleTimer = null;

        recycleTimer = timer.Every(RecycleSentryTickIntervalSeconds, () =>
        {
            BasePlayer liveNpc = survivor.Player;

            // Combat/flee taking over is its own interrupt path (mirrors
            // every other timer-driven loop in this project) - the sentry
            // stance is cosmetic idle behaviour, not a real defense
            // mechanism, so it steps aside the instant something real is
            // happening rather than fighting real combat aiming for
            // control of the same viewAngles. _pendingRecyclerToResume is
            // deliberately NOT cleared here - see its own doc comment -
            // that's what lets ResumeOrStartLootTask send the survivor
            // back to this exact recycler once combat/flee ends.
            if (liveNpc == null || liveNpc.IsDestroyed || recycler == null || recycler.IsDestroyed
                || _activeCombat.ContainsKey(characterId) || _activeFlee.ContainsKey(characterId))
            {
                recycleTimer.Destroy();
                _activeRecycling.Remove(characterId);
                return;
            }

            if (!recycler.IsOn())
            {
                recycleTimer.Destroy();
                _activeRecycling.Remove(characterId);
                FinishRecycling(survivor, recycler, round);
                return;
            }

            // Slow left-right sweep, not a full 360 - a sine wave between
            // +/-RecycleSentryScanHalfAngleDegrees off the base "away from
            // the recycler" heading.
            float elapsed = UnityEngine.Time.realtimeSinceStartup - startTime;
            float phase = Mathf.Sin(elapsed / RecycleSentryScanPeriodSeconds * Mathf.PI * 2f);
            Vector3 scanDirection = Quaternion.AngleAxis(phase * RecycleSentryScanHalfAngleDegrees, Vector3.up) * guardBaseDirection;

            Quaternion rotation = Quaternion.LookRotation(scanDirection);
            liveNpc.transform.rotation = rotation;
            liveNpc.OverrideViewAngles(rotation.eulerAngles);
            liveNpc.tickViewAngles = rotation.eulerAngles;

            // eyes.NetworkUpdate alone (AimAtContainer's own fix,
            // LivingRust.Looting.cs) only refreshes eyes.bodyRotation - a
            // SERVER-SIDE value used for raycast/hit-detection accuracy,
            // not what an observer's client actually renders. Confirmed a
            // DIFFERENT, already-documented bug in this project (see
            // AimAtPlayer's own doc comment, LivingRust.Combat.cs):
            // GetNetworkRotation() - what every observer's client actually
            // receives - reads viewAngles, but nothing pushes a fresh
            // network snapshot out to broadcast it for a disconnected bot
            // (no real PlayerTick RPC ever fires to trigger Rust's own
            // batch sync). SendNetworkUpdateImmediate() is the actual fix
            // that was missing here - without it the rotation was correct
            // server-side the whole time but never reached any client,
            // exactly the "stuck facing the recycler" symptom reported
            // live (2026-08-15) even though feeding/starting/guarding was
            // all working correctly server-side.
            liveNpc.eyes.NetworkUpdate(rotation);
            liveNpc.SendNetworkUpdateImmediate();
        });

        _activeRecycling[characterId] = recycleTimer;
    }

    /// <summary>
    /// Collects everything sitting in the recycler's output slots (6-11)
    /// into the survivor's own inventory - real slots the survivor itself
    /// just freed room for by feeding the input side, so this should
    /// almost always fully succeed. Loops back into another BeginRecycling
    /// round if any recycle fodder remains anywhere in inventory
    /// (2026-08-15, Lucas's own explicit correction: "it needs to commit
    /// and recycle its inventory, not just 6 stacks of items") - only
    /// stops and idles once a genuinely empty check confirms nothing
    /// fodder-worthy is left ("any components left? no? go idle").
    /// </summary>
    private void FinishRecycling(Survivor survivor, Recycler recycler, int round = 1)
    {
        _pendingRecyclerToResume.Remove(survivor.Character.Id);

        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || recycler == null || recycler.IsDestroyed)
        {
            FinishRecyclerDetourOrIdle(survivor);
            return;
        }

        List<Item> output = new();

        for (int slot = RecyclerOutputSlotStart; slot < RecyclerOutputSlotStart + RecyclerOutputSlotCount; slot++)
        {
            Item item = recycler.inventory.GetSlot(slot);

            if (item != null)
            {
                output.Add(item);
            }
        }

        int collected = 0;

        foreach (Item item in output)
        {
            if (TryTransferSingleItem(item, npc.inventory, npc))
            {
                collected++;
            }
        }

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' finished recycling round {round} and collected {collected} item stack(s).");

        bool hasMoreFodder = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Any(IsRecycleFodder);

        if (hasMoreFodder)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' still has components left - feeding another round.");
            BeginRecycling(survivor, recycler, round + 1);
            return;
        }

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' has no components left to recycle - done.");

        // Real "go home and deposit" trip (2026-09-01, Lucas's own
        // explicit roadmap ask: "recycle components then go back to base
        // and deposit said items that were recycled... then re-continue
        // previous task"). CurrentTask deliberately stays TaskType.
        // Recycling for the whole trip (not cleared until
        // FinishRecyclerDetourOrIdle actually runs afterward) - the exact
        // same "already recycling, don't start a second trip" gate
        // GhostRouteLootScan's own full-inventory check already relies on
        // (see its own doc comment) keeps working correctly while this
        // trip is in progress. No-op straight through to the normal
        // idle/resume-route behaviour if this survivor has no home at all
        // (GhostReturnHomeAndDeposit's own null-home guard).
        SweepDroppedItemsNear(
            survivor,
            recycler.transform.position,
            RecyclerDroppedItemSweepRadius,
            RecyclerDroppedItemSweepMaxPickups,
            () => GhostReturnHomeAndDeposit(survivor, () => FinishRecyclerDetourOrIdle(survivor)));
    }

    // Dropped-item double check after a recycler visit (2026-09-21, Lucas's
    // own live report: scrap, metal fragments and other progression items
    // found lying around recyclers - a survivor with a full inventory
    // sheds its lowest-value item to make room for recycler output, and
    // nothing ever went back for what it shed or for what anyone else
    // dropped there). Real dropped-item entities only (collectibles are
    // handled by the normal search), skipping anything the loot filters
    // wouldn't take anyway; capped so a pile of junk can't trap a survivor.
    private const float RecyclerDroppedItemSweepRadius = 20f;
    private const int RecyclerDroppedItemSweepMaxPickups = 8;

    private void SweepDroppedItemsNear(Survivor survivor, Vector3 center, float radius, int picksRemaining, Action onDone)
    {
        BasePlayer npc = survivor.Player;

        if (picksRemaining <= 0 || npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead
            || _activeCombat.ContainsKey(survivor.Character.Id))
        {
            onDone?.Invoke();
            return;
        }

        if (!_engine.NavigationManager.TryFindNearestDroppedItem(
                center,
                radius,
                out DroppedItem dropped,
                filter: candidate => candidate.item != null
                    && !IsNeverLootItem(candidate.item.info.shortname)
                    && GetLootPriorityTier(candidate.item) <= LootPriorityTier.Components))
        {
            onDone?.Invoke();
            return;
        }

        StartWalkingWithRecovery(
            survivor,
            dropped.transform.position,
            onArrived: () => PickupDroppedItemAndContinue(
                survivor,
                dropped,
                new LootTaskState(),
                onDone: () => SweepDroppedItemsNear(survivor, center, radius, picksRemaining - 1, onDone)),
            onFailed: () => onDone?.Invoke());
    }

    private void CancelActiveRecycling(Guid characterId)
    {
        if (_activeRecycling.TryGetValue(characterId, out Timer recycleTimer))
        {
            recycleTimer?.Destroy();
            _activeRecycling.Remove(characterId);
        }
    }

    /// <summary>
    /// When each survivor's most recent fight/flee genuinely ended (see
    /// PostCombatResumeCooldownSeconds' own doc comment) - set by
    /// EndCombat/EndFlee (LivingRust.Combat.cs) right before each of them
    /// calls ResumeOrStartLootTask.
    /// </summary>
    private readonly Dictionary<Guid, float> _lastCombatEndTime = new();

    /// <summary>
    /// Real quiet window (2026-08-21, Lucas's own explicit request, "let's
    /// see how the bot reacts" - starting at 10s) after a fight/flee ends
    /// before ResumeOrStartLootTask will attempt any real walk (ghost-route
    /// resume, recycler resume, or a fresh task's own initial walk) - live
    /// evidence (9741SneakyGrunt, Nuclear Missile Silo) showed a survivor
    /// repeatedly re-engaged by the same Scientist could rack up 10 combat/
    /// detour cycles in about 4 seconds, each one destroying and restarting
    /// StartWalkingWithRecovery's own stuck-escalation ladder from tier 0
    /// (wiggle/nudge/teleport/phase-through) before it ever got the
    /// uninterrupted time to reach its own real last-resort phase-through
    /// tier. Doesn't fix genuinely blocked geometry by itself (that's what
    /// the trace itself needs) - purely stops repeated combat interrupts
    /// from starving the recovery ladder of the time it needs to actually
    /// escalate, and stops a rapid re-engage loop from hammering the same
    /// doomed walk attempt over and over. Self-defense (StartCombat/on-
    /// sight detection) is completely unaffected - a re-engaged survivor
    /// still fights back immediately regardless of this cooldown, this
    /// only delays the POST-fight relocate attempt.
    /// </summary>
    // Same 50% bump as PostCombatSettleSeconds/PostCombatReloadMinimumDelaySeconds
    // (LivingRust.Combat.cs, 2026-08-24) - 5f -> 7.5f.
    private const float PostCombatResumeCooldownSeconds = 7.5f;

    /// <summary>
    /// Called from EndCombat/EndFlee in place of an unconditional
    /// StartLootForResourcesTask (2026-08-15, Lucas's own explicit
    /// request) - a survivor that had already fed and started a recycler
    /// before getting pulled into a fight goes back to collect it rather
    /// than abandoning real components/scrap it already paid the walk for.
    /// Only resumes if the recycler's still there and it's still that
    /// survivor's task to finish (a fresh gear-weighted task elsewhere -
    /// e.g. the never-idle fallback rolling a brand new monument before
    /// this particular fight even started - would already have overwritten
    /// CurrentTask, so this doesn't fight that). Falls back to the normal
    /// fresh task exactly like before if there's nothing to resume, or if
    /// the walk back fails.
    ///
    /// Holds off entirely (past the PostCombatResumeCooldownSeconds check
    /// below) if this survivor's own fight ended too recently - loots
    /// whatever's already within real reach right here (no travel, so no
    /// new way to get stuck) and reschedules itself for whenever the
    /// window actually expires, rather than immediately attempting a real
    /// walk that could walk it straight back into whatever just attacked
    /// it.
    /// </summary>
    // Real per-survivor tracking (2026-08-28, live bug found: '388RustyBandit'
    // logged 60+ duplicate "staying put" lines within a single second) -
    // ResumeOrStartLootTask is called from three independent places
    // (EndCombat, EndFlee, and its own self-rescheduling timer.Once below),
    // none of which previously cancelled a PREVIOUSLY scheduled resume
    // timer before starting a new one. A survivor cycling through combat
    // -> flee -> combat a few times in one firefight (exactly what
    // repeated engagement/re-engagement looks like) spun up a separate
    // parallel self-rescheduling chain on every single call, none of them
    // ever cleaned up - they eventually co-fire, each one calling
    // LootWhateverIsHereNow/logging/rescheduling independently, producing
    // exactly this kind of duplicate-line burst. Cancelling any existing
    // pending timer before scheduling a new one (same pattern this
    // project already uses for _pendingSleepingBagDeployTimers/
    // _pendingArrowCraftTimers/etc) guarantees only ever one chain is
    // active per survivor.
    private readonly Dictionary<Guid, Timer> _pendingPostCombatResumeTimers = new();

    private void ResumeOrStartLootTask(Survivor survivor)
    {
        Guid characterId = survivor.Character.Id;

        if (_pendingPostCombatResumeTimers.TryGetValue(characterId, out Timer existingResumeTimer))
        {
            existingResumeTimer.Destroy();
            _pendingPostCombatResumeTimers.Remove(characterId);
        }

        BasePlayer earlyNpc = survivor.Player;

        if (earlyNpc == null || earlyNpc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
        {
            return;
        }

        float cooldownRemaining = _lastCombatEndTime.TryGetValue(characterId, out float lastCombatEnd)
            ? (lastCombatEnd + PostCombatResumeCooldownSeconds) - UnityEngine.Time.realtimeSinceStartup
            : 0f;

        if (cooldownRemaining > 0f)
        {
            LootWhateverIsHereNow(survivor, earlyNpc);

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' is staying put for {cooldownRemaining:F0}s more before trying to relocate - too soon after its last fight.");

            Timer resumeTimer = null;
            resumeTimer = timer.Once(cooldownRemaining, () =>
            {
                _pendingPostCombatResumeTimers.Remove(characterId);
                ResumeOrStartLootTask(survivor);
            });
            _pendingPostCombatResumeTimers[characterId] = resumeTimer;
            return;
        }

        BasePlayer npc = survivor.Player;

        // A recycler trip that was ITSELF a ghost-route detour (see
        // _ghostRouteRecyclerDetour's own doc comment, 2026-08-17) takes
        // priority over the general ghost-route resume check below - the
        // survivor already committed to (and possibly half-fed) this
        // specific recycler before combat interrupted it, so finishing that
        // off comes first, exactly like it would finish looting a corpse
        // before resuming the route after a normal combat detour. Falls
        // through to the general checks below if the recycler itself is
        // gone by the time this runs (destroyed/despawned) - nothing left
        // here to prioritize finishing.
        if (npc != null && !npc.IsDestroyed
            && _ghostRouteRecyclerDetour.Contains(characterId)
            && _pendingRecyclerToResume.TryGetValue(characterId, out Recycler ghostRouteRecycler)
            && ghostRouteRecycler != null && !ghostRouteRecycler.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.Recycling;

            Vector3 recyclerApproachPoint = GetApproachPoint(ghostRouteRecycler, npc);

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' is heading back to finish recycling before resuming its ghost route ({Vector3.Distance(npc.transform.position, recyclerApproachPoint):F0}m away).");

            StartWalkingWithRecovery(
                survivor,
                recyclerApproachPoint,
                onArrived: () => ResumeRecyclingAfterCombat(survivor, ghostRouteRecycler),
                onFailed: () =>
                {
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't get back to its recycler - resuming the ghost route instead.");
                    _pendingRecyclerToResume.Remove(characterId);

                    if (!(_ghostRouteRecyclerDetour.Remove(characterId) && TryResumeGhostRoute(survivor)))
                    {
                        StartLootForResourcesTask(survivor);
                    }
                });

            return;
        }

        // Checked ahead of the recycler resume below (2026-08-16, Lucas's
        // own explicit request) - see TryResumeGhostRoute's own doc comment
        // (LivingRust.Looting.cs). Returns true and takes over entirely the
        // instant it commits to a resume attempt.
        if (TryResumeGhostRoute(survivor))
        {
            return;
        }

        if (npc != null && !npc.IsDestroyed
            && _pendingRecyclerToResume.TryGetValue(characterId, out Recycler recycler)
            && recycler != null && !recycler.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.Recycling;

            Vector3 approachPoint = GetApproachPoint(recycler, npc);

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' is heading back to finish recycling after the fight ({Vector3.Distance(npc.transform.position, approachPoint):F0}m away).");

            StartWalkingWithRecovery(
                survivor,
                approachPoint,
                onArrived: () => ResumeRecyclingAfterCombat(survivor, recycler),
                onFailed: () =>
                {
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't get back to its recycler - starting a fresh search instead.");
                    _pendingRecyclerToResume.Remove(characterId);
                    StartLootForResourcesTask(survivor);
                });

            return;
        }

        StartLootForResourcesTask(survivor);
    }

    /// <summary>
    /// Back at a recycler it already fed/started before combat interrupted
    /// it - the recycler kept running on Rust's own real timer the whole
    /// time regardless of what happened to the survivor, so this just
    /// checks whether it's still going (resume the sentry guard) or
    /// already finished while the survivor was away (collect immediately,
    /// same as a normal finish).
    /// </summary>
    private void ResumeRecyclingAfterCombat(Survivor survivor, Recycler recycler)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || recycler == null || recycler.IsDestroyed)
        {
            _pendingRecyclerToResume.Remove(survivor.Character.Id);
            FinishRecyclerDetourOrIdle(survivor);
            return;
        }

        if (recycler.IsOn())
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' is back and standing guard again while its recycler finishes.");
            StartRecycleGuardLoop(survivor, recycler);
            return;
        }

        FinishRecycling(survivor, recycler);
    }
}
