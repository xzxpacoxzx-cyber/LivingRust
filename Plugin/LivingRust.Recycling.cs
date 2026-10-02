using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Handles the recycler workflow: a survivor with a full inventory heads to the nearest usable
/// recycler, feeds it low-value junk, stands guard while it runs, then collects the output and
/// resumes its previous task.
/// </summary>
public partial class LivingRust
{
    private const float RecyclerSearchRadius = 400f;
    private const float RecycleSentryTickIntervalSeconds = 1f;
    private const float RecycleSentryScanHalfAngleDegrees = 60f;
    private const float RecycleSentryScanPeriodSeconds = 6f;

    /// <summary>
    /// A recycler takes 6 input stacks at a time, with 6 output slots right after them.
    /// Input slots: 0-5. Output slots: 6-11.
    /// </summary>
    private const int RecyclerInputSlotCount = 6;
    private const int RecyclerOutputSlotStart = 6;
    private const int RecyclerOutputSlotCount = 6;

    private readonly Dictionary<Guid, Timer> _activeRecycling = new();

    /// <summary>
    /// Which recycler a survivor has fed and started, if any. Separate from _activeRecycling (the
    /// sentry-timer registry) since combat/flee cancels the timer but must not erase which
    /// recycler is in progress, so it can be resumed afterward.
    /// </summary>
    private readonly Dictionary<Guid, Recycler> _pendingRecyclerToResume = new();

    /// <summary>
    /// Marks that the current recycler trip was triggered by a full inventory mid-ghost-route,
    /// so the route can be resumed once the recycler trip is done or abandoned.
    /// </summary>
    private readonly HashSet<Guid> _ghostRouteRecyclerDetour = new();

    /// <summary>
    /// Common "recycling is done/abandoned" exit point: resumes the ghost route if this trip was
    /// a detour from one, otherwise falls back to idle or restarts the loot task.
    /// </summary>
    private void FinishRecyclerDetourOrIdle(Survivor survivor)
    {
        if (_ghostRouteRecyclerDetour.Remove(survivor.Character.Id) && TryResumeGhostRoute(survivor))
        {
            return;
        }

        // Restarts the loot task rather than just clearing CurrentTask, since the task pipeline
        // is a pure callback chain with no separate task-tick timer to pick it back up otherwise.
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
    /// Checks whether an item is recycle fodder: only the bottom two loot-priority tiers
    /// (Components/Other), so weapons, armor, medical supplies, and ammo are never fed in.
    /// </summary>
    private static bool IsRecycleFodder(Item item)
    {
        // Never feed away a puzzle fuse - a survivor pursuing a card puzzle needs to keep it.
        if (item.info.shortname == "fuse" || item.info.shortname == "fuse.highgrade")
        {
            return false;
        }

        // Never feed away a tool cupboard (base ownership anchor), stone tools (likely the
        // survivor's only gather tool), or water containers (active hydration, not junk).
        if (item.info.shortname == ToolCupboardShortname
            || item.info.shortname == StoneHatchetShortname
            || item.info.shortname == StonePickaxeShortname
            || item.info.shortname == WaterBottleShortname
            || item.info.shortname == WaterJugShortname)
        {
            return false;
        }

        // Tools, construction pieces and food are never junk. Explicit shortnames as well as
        // categories, so a miscategorised item can't slip through.
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
    /// Starts a recycling trip. Two triggers: ContinueLootTask calls this once main inventory is
    /// full, and NeverIdleFallback tries this first once every known loot zone is exhausted.
    /// Returns false if nothing carried is recycle fodder or no usable recycler is in range;
    /// safezone recyclers are excluded like other autonomous decisions.
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
    /// Hard cap on recycling rounds at one recycler visit, a safety net against a pathological
    /// edge case. Real inventories never need more than ~5 rounds, so this never limits genuine use.
    /// </summary>
    private const int MaxRecycleRoundsPerVisit = 12;

    /// <summary>
    /// Feeds fodder items into the recycler's input slots, starts it, and begins the sentry-guard
    /// loop. Only 6 stacks fit per round; FinishRecycling calls this again for another round if
    /// fodder remains, rather than stopping after the first 6 stacks.
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

        // Sweeps recycle fodder from the belt into main first, best-effort. The fodder search
        // below still pulls from both containers regardless, so nothing is skipped if this fails.
        foreach (Item beltItem in npc.inventory.containerBelt.itemList.Where(IsRecycleFodder).ToList())
        {
            beltItem.MoveToContainer(npc.inventory.containerMain);
        }

        // Clears the recycler before using it: finished output is always collected, and leftover
        // input stacks are only pulled when the recycler isn't running, so no active batch is taken.
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

        // Only the first RecyclerInputSlotCount fodder items fit. Ordered by tier first so, if
        // there's more fodder than room, Components get fed ahead of catch-all Other junk.
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
    /// The sentry-guard timer, split out from BeginRecycling so ResumeRecyclingAfterCombat can
    /// restart the same loop after a fight without re-feeding the recycler.
    /// </summary>
    private void StartRecycleGuardLoop(Survivor survivor, Recycler recycler, int round = 1)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || recycler == null || recycler.IsDestroyed)
        {
            return;
        }

        // Base facing is straight away from the recycler, back to the machine. Falls back to
        // whatever the bot's already facing in the rare case it's standing on the origin point.
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

            // Combat/flee taking over steps this cosmetic sentry stance aside immediately.
            // _pendingRecyclerToResume is deliberately not cleared here, so the survivor can be
            // sent back to this recycler once combat/flee ends.
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

            // Slow left-right sweep, not a full 360: a sine wave off the base heading.
            float elapsed = UnityEngine.Time.realtimeSinceStartup - startTime;
            float phase = Mathf.Sin(elapsed / RecycleSentryScanPeriodSeconds * Mathf.PI * 2f);
            Vector3 scanDirection = Quaternion.AngleAxis(phase * RecycleSentryScanHalfAngleDegrees, Vector3.up) * guardBaseDirection;

            Quaternion rotation = Quaternion.LookRotation(scanDirection);
            liveNpc.transform.rotation = rotation;
            liveNpc.OverrideViewAngles(rotation.eulerAngles);
            SetTickViewAngles(liveNpc, rotation.eulerAngles);

            // eyes.NetworkUpdate alone only refreshes the server-side rotation value;
            // SendNetworkUpdateImmediate() is needed to actually broadcast it to observers.
            liveNpc.eyes.NetworkUpdate(rotation);
            liveNpc.SendNetworkUpdateImmediate();
        });

        _activeRecycling[characterId] = recycleTimer;
    }

    /// <summary>
    /// Collects everything from the recycler's output slots into the survivor's inventory, then
    /// loops back into another BeginRecycling round if fodder remains, or idles once done.
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

        // Goes home to deposit recycled items, then resumes the previous task. CurrentTask stays
        // Recycling for the whole trip so the "already recycling" gate elsewhere keeps working.
        SweepDroppedItemsNear(
            survivor,
            recycler.transform.position,
            RecyclerDroppedItemSweepRadius,
            RecyclerDroppedItemSweepMaxPickups,
            () => GhostReturnHomeAndDeposit(survivor, () => FinishRecyclerDetourOrIdle(survivor)));
    }

    // Sweeps dropped items near the recycler after a visit, since a full inventory can shed its
    // lowest-value item to make room. Capped so a pile of junk can't trap a survivor.
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
    /// When each survivor's most recent fight/flee ended, set by EndCombat/EndFlee right before
    /// each calls ResumeOrStartLootTask.
    /// </summary>
    private readonly Dictionary<Guid, float> _lastCombatEndTime = new();

    /// <summary>
    /// Quiet window after a fight/flee ends before ResumeOrStartLootTask attempts any real walk.
    /// Prevents a rapid re-engage loop from repeatedly restarting the stuck-recovery escalation
    /// ladder before it can reach its last-resort tier. Self-defense is unaffected by this delay.
    /// </summary>
    private const float PostCombatResumeCooldownSeconds = 7.5f;

    /// <summary>
    /// Called from EndCombat/EndFlee in place of an unconditional StartLootForResourcesTask: a
    /// survivor that had already fed and started a recycler before combat goes back to collect
    /// it. Holds off entirely if the fight ended too recently, looting only what's in reach and
    /// rescheduling itself for when the cooldown expires.
    /// </summary>
    // Cancels any existing pending resume timer before scheduling a new one, so repeated
    // combat/flee cycles in one fight can't spin up multiple parallel resume chains.
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

        // A recycler trip that was itself a ghost-route detour takes priority over the general
        // ghost-route resume check below, finishing what's already committed first.
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

        // Checked ahead of the recycler resume below. Returns true and takes over entirely once
        // it commits to a resume attempt.
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
    /// Back at a recycler already fed/started before combat interrupted it. Checks whether it's
    /// still running (resume the sentry guard) or already finished (collect immediately).
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
