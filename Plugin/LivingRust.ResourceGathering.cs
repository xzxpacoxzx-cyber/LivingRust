using LivingRust.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Facepunch;
using LivingRust.Models;
using Oxide.Plugins;
using Rust;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Active resource gathering: chopping a standing TreeEntity for wood, and mining an
/// OreResourceEntity for stone/sulfur/metal/HQ metal ore, via Rust's real attack pipeline
/// rather than the passive loot pipeline.
/// </summary>
public partial class LivingRust
{
    /// <summary>
    /// Wood-gathering tool ranking, best first. "rock" is the only universal fallback tool,
    /// since every survivor starts with one.
    /// </summary>
    private static readonly string[] TreeGatherToolPriority =
    {
        "chainsaw",
        "axe.salvaged",
        "hatchet",
        "stonehatchet",
        "lumberjack.hatchet",
        "frontier_hatchet",
        "concretehatchet",
        "diverhatchet",
        "rock",
    };

    private static readonly string[] OreGatherToolPriority =
    {
        "jackhammer",
        "pickaxe",
        "stone.pickaxe",
        "lumberjack.pickaxe",
        "concretepickaxe",
        "diverpickaxe",
        "icepick.salvaged",
        "rock",
    };

    /// <summary>
    /// Checks whether a survivor can attempt this resource type at all before walking
    /// toward a node, so it doesn't walk all the way there with nothing to gather it with.
    /// </summary>
    private static bool HasAnyGatherCapableTool(BasePlayer npc, string[] toolPriority)
    {
        return npc.inventory.containerBelt.itemList
            .Concat(npc.inventory.containerMain.itemList)
            .Any(item => Array.IndexOf(toolPriority, item.info.shortname) >= 0);
    }

    private void ValidateGatherToolPriorityLists()
    {
        List<string> unresolved = new();

        foreach (string shortname in TreeGatherToolPriority.Concat(OreGatherToolPriority).Distinct())
        {
            if (ItemManager.FindItemDefinition(shortname) == null)
            {
                unresolved.Add(shortname);
            }
        }

        if (unresolved.Count > 0)
        {
            Puts($"WARNING: Tree/OreGatherToolPriority contains {unresolved.Count} shortname(s) that don't resolve to any real item - gathering will never recognize these: {string.Join(", ", unresolved)}.");
        }
        else
        {
            Puts("Tree/OreGatherToolPriority validated - all shortnames resolve to real items.");
        }
    }

    /// <summary>
    /// Equips the best available gather tool for the given resource type, searching the
    /// belt first then falling back to main+belt. Returns the equipped BaseMelee directly.
    /// </summary>
    private BaseMelee EquipBestGatherToolForType(Survivor survivor, string[] toolPriority)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return null;
        }

        Item currentlyEquipped = npc.GetActiveItem();
        Item best = FindBestByPriority(npc.inventory.containerBelt.itemList.ToList(), toolPriority, exclude: null);

        if (best == null)
        {
            int bestRank = int.MaxValue;

            foreach (Item item in npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList))
            {
                int rank = Array.IndexOf(toolPriority, item.info.shortname);

                if (rank >= 0 && rank < bestRank)
                {
                    bestRank = rank;
                    best = item;
                }
            }
        }

        if (best == null)
        {
            return npc.GetHeldEntity() as BaseMelee;
        }

        if (currentlyEquipped == null || currentlyEquipped.uid != best.uid)
        {
            if (!npc.inventory.containerBelt.itemList.Contains(best))
            {
                int targetPosition = currentlyEquipped != null && currentlyEquipped.parent == npc.inventory.containerBelt
                    ? currentlyEquipped.position
                    : -1;

                if (!best.MoveToContainer(npc.inventory.containerBelt, targetPosition))
                {
                    VerbosePuts($"gather-task: '{survivor.Character.Alias}' has a better gather tool ('{best.info.shortname}') but no free belt slot to equip it - keeping whatever's already out.");
                    return npc.GetHeldEntity() as BaseMelee;
                }
            }

            npc.UpdateActiveItem(best.uid);
            ForceRefreshHeldEntity(npc);
        }

        return npc.GetHeldEntity() as BaseMelee;
    }

    /// <summary>
    /// Per-tick nudge distance while establishing facing toward a gather target.
    /// </summary>
    private const float ResourceGatherFacingNudgeDistance = 0.02f;

    /// <summary>
    /// How long the nudge phase runs once gathering starts. A brief position nudge
    /// establishes visible facing since rotation-only updates don't reach observers.
    /// </summary>
    private const float ResourceGatherFacingNudgeSeconds = 0.6f;

    /// <summary>
    /// Rotates the survivor to face a gather target, plus an optional brief position nudge.
    /// For a tree, height is held at eye level; for ore it interpolates down as it depletes.
    /// </summary>
    private void AimAtResourceNode(BasePlayer npc, ResourceEntity node, bool nudge)
    {
        Vector3 targetPoint = node.WorldSpaceBounds().position;

        if (node is TreeEntity)
        {
            targetPoint.y = npc.eyes.position.y;
        }
        else
        {
            float healthFraction = node.MaxHealth() > 0f ? Mathf.Clamp01(node.Health() / node.MaxHealth()) : 0f;
            targetPoint.y = Mathf.Lerp(node.transform.position.y, npc.eyes.position.y, healthFraction);
        }

        Vector3 direction = targetPoint - npc.eyes.position;

        if (direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Quaternion aimRotation = Quaternion.LookRotation(direction);

        npc.transform.rotation = Quaternion.Euler(0f, aimRotation.eulerAngles.y, 0f);
        npc.OverrideViewAngles(aimRotation.eulerAngles);
        npc.eyes.NetworkUpdate(aimRotation);

        if (!nudge)
        {
            return;
        }

        Vector3 flatDirection = direction;
        flatDirection.y = 0f;

        if (flatDirection.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Vector3 nudgedPosition = npc.transform.position + flatDirection.normalized * ResourceGatherFacingNudgeDistance;
        npc.transform.position = nudgedPosition;
        npc.MovePosition(nudgedPosition);
    }

    // Tree/ore health pools run higher than a barrel's, so this cap is higher than
    // MaxHitsPerContainer to avoid giving up prematurely.
    private const int MaxHitsPerResourceNode = 60;

    /// <summary>
    /// Triggers a single gather swing, routing damage through
    /// melee.DoAttackShared(HitInfo) so gather stats, effects, and cooldown all run
    /// through the same pipeline a real swing uses.
    /// </summary>
    /// <summary>
    /// Gather-yield bonus that increases with consecutive hits, matching Rust's own
    /// formula. hitIndex is 0-based.
    /// </summary>
    private const float ResourceGatherBonusPerHit = 0.125f;

    private const float ResourceGatherBonusMax = 1f;

    private static float GetResourceGatherBonusScale(int hitIndex)
    {
        return 1f + Mathf.Clamp(hitIndex * ResourceGatherBonusPerHit, 0f, ResourceGatherBonusMax);
    }

    private void SwingGatherTool(BasePlayer npc, BaseMelee melee, ResourceEntity node, int hitIndex)
    {
        // The jackhammer's engine flag is normally toggled by a client RPC a disconnected
        // bot never sends, so it's turned on directly here instead.
        if (melee is Jackhammer jackhammer)
        {
            jackhammer.SetEngineStatus(true);
        }

        melee.ServerUse();
        melee.CancelInvoke(melee.ServerUse_Strike);

        HitInfo info = Pool.Get<HitInfo>();
        info.Init(npc, node, DamageType.Generic, 0f, node.transform.position);
        info.Weapon = melee;
        info.WeaponPrefab = melee;
        info.PointStart = npc.eyes.position;
        info.PointEnd = node.transform.position;
        info.gatherScale = GetResourceGatherBonusScale(hitIndex);

        melee.DoAttackShared(info);

        Pool.Free(ref info);
    }

    /// <summary>
    /// Swing loop against a live resource node. onSuccess fires once the node is depleted
    /// or the hit cap is reached; onFailed fires when no swing ever landed.
    /// </summary>
    private void StartGatheringResourceNode(Survivor survivor, ResourceEntity node, string[] toolPriority, Action onSuccess, Action onFailed)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveAttack(characterId);

        BaseMelee melee = EquipBestGatherToolForType(survivor, toolPriority);

        if (melee == null)
        {
            // Bare-handed - gathering with no tool at all is impossible, so no point looping.
            VerbosePuts($"gather-task: '{survivor.Character.Alias}' has no tool to gather '{node.ShortPrefabName}' with.");
            onFailed?.Invoke();
            return;
        }

        int hits = 0;
        Timer attackTimer = null;
        float gatherStartTime = Time.realtimeSinceStartup;

        attackTimer = timer.Every(AttackHitInterval, () =>
        {
            if (_activeCombat.ContainsKey(characterId))
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                return;
            }

            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onFailed?.Invoke();
                return;
            }

            if (node == null || node.IsDestroyed || node.Health() <= 0f)
            {
                // Depleted or despawned - either way nothing left to swing at.
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onSuccess?.Invoke();
                return;
            }

            // The range check is type-aware: trees use position-based IsWithinResourceNodeRange,
            // while ore uses the bounds-based IsWithinLootRange since its pivot sits nearer its centre.
            bool inRange = node is TreeEntity
                ? IsWithinResourceNodeRange(npc, node)
                : IsWithinLootRange(npc, node);

            if (!HasLineOfSight(npc, node) || !inRange)
            {
                // A survivor that already landed a hit fails immediately on losing range/LOS.
                // One that never got a hit in gets a timeout window first.
                if (hits > 0 || Time.realtimeSinceStartup - gatherStartTime >= GatherEngagementTimeoutSeconds)
                {
                    if (hits == 0)
                    {
                        VerbosePuts($"gather-task: '{survivor.Character.Alias}' got zero real engagement on '{node.ShortPrefabName}' after {GatherEngagementTimeoutSeconds:F0}s - giving up and blacklisting it.");
                        PoisonResourceNode(node);
                    }
                    else
                    {
                        VerbosePuts($"gather-task: '{survivor.Character.Alias}' can't reach '{node.ShortPrefabName}' anymore - giving up.");
                    }

                    attackTimer.Destroy();
                    _activeAttacks.Remove(characterId);
                    onFailed?.Invoke();
                    return;
                }

                // Still within the engagement window - keep waiting, but skip the swing
                // logic below since there's nothing to aim/swing at while out of range.
                return;
            }

            // Runs every tick regardless of the cooldown gate below, so the bot stays
            // visibly turned toward the node for the whole gather.
            bool stillEstablishingFacing = Time.realtimeSinceStartup - gatherStartTime < ResourceGatherFacingNudgeSeconds;
            AimAtResourceNode(npc, node, nudge: stillEstablishingFacing);

            if (melee.HasAttackCooldown())
            {
                // Still mid-swing/reset for this tool's repeatDelay.
                return;
            }

            hits++;
            SwingGatherTool(npc, melee, node, hitIndex: hits - 1);

            bool depleted = node.IsDestroyed || node.Health() <= 0f;

            if (depleted || hits >= MaxHitsPerResourceNode)
            {
                string message = depleted
                    ? $"gather-task: '{survivor.Character.Alias}' finished gathering from '{node.ShortPrefabName}' ({hits} hit(s))."
                    : $"gather-task: '{survivor.Character.Alias}' gave up gathering from '{node.ShortPrefabName}' after {MaxHitsPerResourceNode} hits.";

                VerbosePuts(message);

                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onSuccess?.Invoke();
                return;
            }
        });

        _activeAttacks[characterId] = attackTimer;
    }

    /// <summary>
    /// Melee reach against a tree specifically, measured against the node's
    /// transform.position rather than WorldSpaceBounds() since a tree's bounds include its
    /// whole canopy. Ore keeps using the original bounds-based approach instead.
    /// </summary>
    private const float ResourceNodeApproachStandoffDistance = 1f;

    private const float ResourceNodeInteractionRange = 1.5f;

    /// <summary>
    /// How close a "stuck" position needs to be to a live tree/ore node before
    /// EscalateStuckRecovery treats the node as the likely cause and gathers it instead.
    /// </summary>
    private const float StuckResourceNodeDetectionRadius = 1.5f;

    /// <summary>
    /// Per-node "give up and blacklist" timeout. If a survivor gets zero engagement on a
    /// node within this window, it gives up and poisons the node for every survivor.
    /// </summary>
    private const float GatherEngagementTimeoutSeconds = 10f;

    /// <summary>
    /// Global, cross-survivor blacklist for a resource node that timed out with zero
    /// engagement. Keyed to the node's NetworkableId and expires after
    /// ResourceNodePoisonDurationSeconds.
    /// </summary>
    private readonly Dictionary<NetworkableId, float> _poisonedResourceNodeUntil = new();

    private const float ResourceNodePoisonDurationSeconds = 600f;

    private void PoisonResourceNode(ResourceEntity node)
    {
        _poisonedResourceNodeUntil[node.net.ID] = Time.realtimeSinceStartup + ResourceNodePoisonDurationSeconds;

        VerbosePuts($"gather-task: '{node.ShortPrefabName}' gave no real engagement within {GatherEngagementTimeoutSeconds:F0}s - every survivor will avoid it for the next {ResourceNodePoisonDurationSeconds:F0}s.");
    }

    private bool IsResourceNodePoisoned(BaseEntity node)
    {
        return _poisonedResourceNodeUntil.TryGetValue(node.net.ID, out float expiresAt) && expiresAt > Time.realtimeSinceStartup;
    }

    private Vector3 GetResourceNodeApproachPoint(ResourceEntity node, BasePlayer npc)
    {
        Vector3 direction = npc.transform.position - node.transform.position;
        direction.y = 0f;

        if (direction.sqrMagnitude < 0.01f)
        {
            direction = npc.transform.forward;
            direction.y = 0f;
        }

        direction.Normalize();

        Vector3 candidate = node.transform.position + direction * ResourceNodeApproachStandoffDistance;

        return SnapApproachPointToNavMesh(npc, candidate);
    }

    private bool IsWithinResourceNodeRange(BasePlayer npc, ResourceEntity node)
    {
        return Vector3.Distance(npc.transform.position, node.transform.position) <= ResourceNodeInteractionRange;
    }

    /// <summary>
    /// Walk-then-gather wrapper for a TreeEntity, using GetResourceNodeApproachPoint
    /// instead of the generic bounds-based GetApproachPoint.
    /// </summary>
    private void GatherTreeAndContinue(Survivor survivor, TreeEntity tree, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        Vector3 approachPoint = GetResourceNodeApproachPoint(tree, npc);

        StartWalkingWithRecovery(
            survivor,
            approachPoint,
            onArrived: () => StartGatheringResourceNode(
                survivor,
                tree,
                TreeGatherToolPriority,
                // forceLocalScan: true guarantees a local check right after a gather
                // finishes, since trees/ore cluster more than containers do.
                onSuccess: () => ContinueLootTask(survivor, state, forceLocalScan: true),
                onFailed: () => ContinueLootTask(survivor, state, forceLocalScan: true)),
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

    /// <summary>
    /// Same idea as GatherTreeAndContinue, for an OreResourceEntity, but uses the original
    /// bounds-based GetApproachPoint since ore needs the opposite fix from trees.
    /// </summary>
    private void GatherOreAndContinue(Survivor survivor, OreResourceEntity ore, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        Vector3 approachPoint = GetApproachPoint(ore, npc);

        StartWalkingWithRecovery(
            survivor,
            approachPoint,
            onArrived: () => StartGatheringResourceNode(
                survivor,
                ore,
                OreGatherToolPriority,
                // forceLocalScan: true - see GatherTreeAndContinue above.
                onSuccess: () => ContinueLootTask(survivor, state, forceLocalScan: true),
                onFailed: () => ContinueLootTask(survivor, state, forceLocalScan: true)),
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

    /// <summary>
    /// En-route sibling of GatherTreeAndContinue: resumes the survivor's original
    /// long-distance walk afterward instead of ContinueLootTask.
    /// </summary>
    private void GatherEnRouteTreeAndResume(Survivor survivor, TreeEntity tree, Action resumeOriginalWalk)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        Vector3 approachPoint = GetResourceNodeApproachPoint(tree, npc);

        StartWalkingWithRecovery(
            survivor,
            approachPoint,
            onArrived: () => StartGatheringResourceNode(
                survivor,
                tree,
                TreeGatherToolPriority,
                onSuccess: resumeOriginalWalk,
                onFailed: resumeOriginalWalk),
            onFailed: resumeOriginalWalk);
    }

    /// <summary>
    /// En-route sibling of GatherOreAndContinue, identical shape to GatherEnRouteTreeAndResume.
    /// </summary>
    private void GatherEnRouteOreAndResume(Survivor survivor, OreResourceEntity ore, Action resumeOriginalWalk)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        Vector3 approachPoint = GetApproachPoint(ore, npc);

        StartWalkingWithRecovery(
            survivor,
            approachPoint,
            onArrived: () => StartGatheringResourceNode(
                survivor,
                ore,
                OreGatherToolPriority,
                onSuccess: resumeOriginalWalk,
                onFailed: resumeOriginalWalk),
            onFailed: resumeOriginalWalk);
    }

    // How far to look for a live tree/ore node when nothing else is left to loot nearby.
    // A separate fallback-of-last-resort search tier, not blended into the main search.
    private const float ResourceNodeSearchRadius = 50f;

    /// <summary>
    /// Fallback-of-last-resort, only consulted from ContinueLootTask's "found nothing at
    /// all" branch. Trees are checked before ore. Returns whether a gather was started.
    /// </summary>
    /// <summary>
    /// Sticky per-survivor gather-type lock: true = locked onto trees, false = locked onto
    /// ore. Cleared on death/despawn so a fresh life can pick either type again.
    /// </summary>
    private readonly Dictionary<Guid, bool> _resourceGatherTypeLock = new();

    private bool TryStartResourceGatheringFallback(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        // High-gear discipline: above the gear threshold a survivor doesn't go farming
        // nodes unless it already has a base and is carrying some raw resource.
        if (GetGearScore(npc) > HighGearResourceAvoidScore
            && (survivor.Character.Home == null || !HasAnyRawResourceInInventory(npc)))
        {
            return false;
        }
        // Carrying its full wood AND stone cap while roaming: no more farming of anything (ore included) -
        // falls through to monument looting / recycling instead.
        if (IsRoamingSaturated(survivor, npc))
        {
            return false;
        }

        bool? lockedToTree = _resourceGatherTypeLock.TryGetValue(characterId, out bool locked) ? locked : (bool?)null;

        // Hitting the wood cap while locked to trees releases the lock so this call falls through to
        // the ore branch. The cap is the rolled design's requirement + 20% before a base exists, and
        // 1000 carried once roaming with one (see GetGatherCap) - not a flat per-life quota.
        if (lockedToTree == true && HasEnoughWoodAlready(survivor, npc))
        {
            Puts($"gather-task: '{survivor.Character.Alias}' has enough wood for now - switching to mining ore instead.");
            _resourceGatherTypeLock.Remove(characterId);
            lockedToTree = null;
        }

        if (lockedToTree != false
            && !HasEnoughWoodAlready(survivor, npc)
            && HasAnyGatherCapableTool(npc, TreeGatherToolPriority)
            && _engine.NavigationManager.TryFindNearestTreeEntity(
                npc.transform.position,
                ResourceNodeSearchRadius,
                out TreeEntity tree,
                candidate => !state.Visited.Contains(candidate.net.ID)
                    && !IsLootTargetClaimed(candidate.net.ID)
                    && !IsInPoisonedZone(candidate.transform.position, state)
                    && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                    && !IsInMonumentAvoidZone(candidate.transform.position)
                        && !IsBelowSafeLootDepth(candidate.transform.position)
                    && !IsResourceNodePoisoned(candidate)))
        {
            state.Visited.Add(tree.net.ID);
            ClaimLootTarget(state, tree.net.ID);
            _resourceGatherTypeLock[characterId] = true;
            VerbosePuts($"gather-task: '{survivor.Character.Alias}' found nothing left to loot nearby - heading to a nearby tree to gather wood instead.");
            GatherTreeAndContinue(survivor, tree, state);
            return true;
        }

        if (lockedToTree == true)
        {
            return false;
        }

        if (!HasAnyGatherCapableTool(npc, OreGatherToolPriority))
        {
            return false;
        }

        if (TryStartOreFallback(survivor, npc, state))
        {
            _resourceGatherTypeLock[characterId] = false;
            return true;
        }

        // Nothing within the normal search radius, so head toward the nearest monument
        // specifically, since quarries/mining outposts have dense ore clusters. See
        // StartExtendedOreSearch. Restricted to survivors pursuing base-gathering.
        if (_pursuingBaseGatherGoal.Contains(characterId) && !HasReachedGatherCap(survivor, npc, StoneShortname))
        {
            StartExtendedOreSearch(survivor, npc, state);
            return true;
        }

        return false;
    }

    // ============================================================
    // Extended ore search - walks toward the nearest monument, re-scans periodically for
    // an ore node within an eyesight cone, diverts to it, and stops once a quota is reached.
    // ============================================================

    private const float ExtendedOreSearchIntervalSeconds = 10f;

    /// <summary>
    /// Half-angle of the "can it actually see this" cone: 90 degrees either side of facing
    /// gives a full 180-degree front hemisphere.
    /// </summary>
    private const float ExtendedOreSearchConeHalfAngle = 90f;

    // Item shortnames for gathered resources - "stones" specifically, not "stone".
    private const string StoneShortname = "stones";
    private const string MetalOreShortname = "metal.ore";
    private const string SulfurOreShortname = "sulfur.ore";
    private const string WoodShortname = "wood";

    /// <summary>
    /// Early-game ore discipline: sulfur and metal ore are avoided until a survivor has a
    /// base down. Stone is not restricted. Checked against GetNodeYields since a node's
    /// actual yield is the source of truth for what it drops.
    /// </summary>
    private static bool IsEarlyGameRestrictedOre(OreResourceEntity candidate)
    {
        IEnumerable<ItemAmount> yields = GetNodeYields(candidate);

        if (yields == null)
        {
            return false;
        }

        return yields.Any(y => y?.itemDef != null
            && (y.itemDef.shortname == MetalOreShortname || y.itemDef.shortname == SulfurOreShortname));
    }

    // Lifetime gathered totals per survivor (updated by OnDispenserGathered). No longer used for caps -
    // gathering now stops on what is CARRIED against GetGatherCap - but still handy for diagnostics.
    private readonly Dictionary<Guid, float> _totalStoneGathered = new();
    private readonly Dictionary<Guid, float> _totalMetalOreGathered = new();
    private readonly Dictionary<Guid, float> _totalSulfurOreGathered = new();
    private readonly Dictionary<Guid, float> _totalWoodGathered = new();

    /// <summary>
    /// Carbon/Oxide hook that fires for every resource handed to a player. Tracks lifetime
    /// stone/metal/sulfur ore and wood totals per survivor.
    /// </summary>
    private void OnDispenserGathered(BasePlayer player, ItemAmount item, float f1, float f2, AttackEntity entity)
    {
        if (player == null || item?.itemDef == null || _engine == null)
        {
            return;
        }

        Survivor survivor = FindSurvivorByPlayer(player);

        if (survivor == null)
        {
            return;
        }

        Guid characterId = survivor.Character.Id;

        switch (item.itemDef.shortname)
        {
            case StoneShortname:
                _totalStoneGathered[characterId] = _totalStoneGathered.GetValueOrDefault(characterId) + item.amount;
                break;
            case MetalOreShortname:
                _totalMetalOreGathered[characterId] = _totalMetalOreGathered.GetValueOrDefault(characterId) + item.amount;
                break;
            case WoodShortname:
                _totalWoodGathered[characterId] = _totalWoodGathered.GetValueOrDefault(characterId) + item.amount;
                break;
            case SulfurOreShortname:
                _totalSulfurOreGathered[characterId] = _totalSulfurOreGathered.GetValueOrDefault(characterId) + item.amount;
                break;
        }
    }

    /// <summary>
    /// Last-resort ore farming (2026-10-03, Lucas's spec). With a base: sulfur first, then metal ore -
    /// neither capped, they only stop when the survivor decides to go home - then plain stone, which
    /// stops at the 1000-carried roaming cap. Before a base: stone only (metal/sulfur stay restricted
    /// until a base exists), up to the rolled design's stone requirement + 20%. This replaces the old
    /// shared per-life 1000 quota, which let plain stone stop ALL mining and capped stone far below
    /// what a base design actually costs.
    /// </summary>
    private bool TryStartOreFallback(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;
        bool hasBase = survivor.Character.Home != null;

        bool Eligible(OreResourceEntity candidate) =>
            !state.Visited.Contains(candidate.net.ID)
            && !IsLootTargetClaimed(candidate.net.ID)
            && !IsInPoisonedZone(candidate.transform.position, state)
            && !IsInThreatFleeZone(characterId, candidate.transform.position)
            && !IsInMonumentAvoidZone(candidate.transform.position)
            && !IsBelowSafeLootDepth(candidate.transform.position)
            && !IsResourceNodePoisoned(candidate);

        List<string> order = new();

        if (hasBase)
        {
            order.Add(SulfurOreShortname);
            order.Add(MetalOreShortname);
        }

        if (!HasReachedGatherCap(survivor, npc, StoneShortname))
        {
            order.Add(StoneShortname);
        }

        foreach (string ore in order)
        {
            string wanted = ore;

            if (_engine.NavigationManager.TryFindNearestOreResourceEntity(
                    npc.transform.position,
                    ResourceNodeSearchRadius,
                    out OreResourceEntity node,
                    candidate => Eligible(candidate)
                        && YieldsContain(GetNodeYields(candidate), wanted)
                        // Pre-base restriction: never a node that also drops metal/sulfur ore.
                        && (hasBase || !IsEarlyGameRestrictedOre(candidate))))
            {
                state.Visited.Add(node.net.ID);
                ClaimLootTarget(state, node.net.ID);
                VerbosePuts($"gather-task: '{survivor.Character.Alias}' found nothing left to loot nearby - heading to a nearby {ore} node.");
                GatherOreAndContinue(survivor, node, state);
                return true;
            }
        }

        return false;
    }

    private readonly Dictionary<Guid, Timer> _extendedOreSearchTimers = new();

    /// <summary>
    /// Checks whether the survivor is facing roughly toward the target, using a flat
    /// angle between eye direction and the candidate.
    /// </summary>
    private static bool IsWithinEyesightCone(BasePlayer npc, BaseEntity target)
    {
        Vector3 toTarget = target.transform.position - npc.eyes.position;
        toTarget.y = 0f;

        if (toTarget.sqrMagnitude < 0.01f)
        {
            return true;
        }

        Vector3 forward = npc.eyes.HeadForward();
        forward.y = 0f;

        if (forward.sqrMagnitude < 0.01f)
        {
            return true;
        }

        return Vector3.Angle(forward, toTarget) <= ExtendedOreSearchConeHalfAngle;
    }

    private void StopExtendedOreSearch(Guid characterId)
    {
        if (_extendedOreSearchTimers.TryGetValue(characterId, out Timer searchTimer))
        {
            searchTimer?.Destroy();
            _extendedOreSearchTimers.Remove(characterId);
        }
    }

    /// <summary>
    /// Kicks off the walk toward the nearest monument plus its own repeating scan, called
    /// when nothing's within the normal search radius.
    /// </summary>
    private const float ExtendedOreSearchMonumentSearchRadius = 4000f;

    private void StartExtendedOreSearch(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        // Heads toward whichever monument is nearest, excluding avoided monuments so a bot
        // doesn't get sent into a safezone and scan there repeatedly.
        MonumentInfo monument = null;
        float bestDistanceSqr = ExtendedOreSearchMonumentSearchRadius * ExtendedOreSearchMonumentSearchRadius;

        foreach (MonumentInfo candidate in MonumentAccess.GetAllMonuments())
        {
            if (candidate == null || IsMonumentExcludedFromAutonomy(candidate) || IsGhostRouteOnlyMonument(candidate))
            {
                continue;
            }

            float distanceSqr = (candidate.transform.position - npc.transform.position).sqrMagnitude;

            if (distanceSqr < bestDistanceSqr)
            {
                bestDistanceSqr = distanceSqr;
                monument = candidate;
            }
        }

        if (monument == null)
        {
            // No monument anywhere on this map - nothing productive left to do for ore.
            return;
        }

        VerbosePuts($"gather-task: '{survivor.Character.Alias}' found no ore within {ResourceNodeSearchRadius:F0}m - heading toward '{monument.name}', scanning every {ExtendedOreSearchIntervalSeconds:F0}s for one along the way.");

        // Walking straight at the monument pivot can land inside a fenced perimeter,
        // causing a false "arrived" and a tight loop, so a scanned loot-zone point is used instead.
        Vector3 monumentWalkTarget = monument.transform.position;

        if (_monumentLootZones.TryGetValue(monument.name, out List<MonumentLootZone> knownZones) && knownZones.Count > 0)
        {
            MonumentLootZone nearestZone = null;
            float nearestZoneDistanceSqr = float.MaxValue;

            foreach (MonumentLootZone zone in knownZones)
            {
                Vector3 zoneWorldPosition = monument.transform.TransformPoint(zone.LocalOffset);
                float distanceSqr = (zoneWorldPosition - npc.transform.position).sqrMagnitude;

                if (distanceSqr < nearestZoneDistanceSqr)
                {
                    nearestZoneDistanceSqr = distanceSqr;
                    nearestZone = zone;
                }
            }

            if (nearestZone != null)
            {
                monumentWalkTarget = JitterZoneDestination(monument.transform.TransformPoint(nearestZone.LocalOffset), nearestZone.Radius, npc);
            }
        }

        StartWalkingWithRecovery(
            survivor,
            monumentWalkTarget,
            onArrived: () =>
            {
                // Reached the monument with nothing spotted en route - one more local scan
                // here, then gives up on this cycle if that's empty too.
                StopExtendedOreSearch(characterId);
                ContinueLootTask(survivor, state, forceLocalScan: true);
            },
            onFailed: () =>
            {
                StopExtendedOreSearch(characterId);

                BasePlayer laterNpc = survivor.Player;

                if (laterNpc == null || laterNpc.IsDestroyed)
                {
                    return;
                }

                ContinueLootTask(survivor, state, forceLocalScan: true);
            });

        StopExtendedOreSearch(characterId);
        _extendedOreSearchTimers[characterId] = timer.Every(ExtendedOreSearchIntervalSeconds, () =>
        {
            BasePlayer currentNpc = survivor.Player;

            if (currentNpc == null
                || currentNpc.IsDestroyed
                || survivor.Character.State == CharacterState.Dead
                || _activeCombat.ContainsKey(characterId))
            {
                StopExtendedOreSearch(characterId);
                return;
            }

            if (HasReachedGatherCap(survivor, currentNpc, StoneShortname))
            {
                StopExtendedOreSearch(characterId);
                CancelActiveMovement(survivor);
                Puts($"gather-task: '{survivor.Character.Alias}' has all the stone its base design needs (+20%) - ending its ore search and carrying on.");
                ContinueLootTask(survivor, state, forceLocalScan: true);
                return;
            }

            if (_engine.NavigationManager.TryFindNearestOreResourceEntity(
                    currentNpc.transform.position,
                    ResourceNodeSearchRadius,
                    out OreResourceEntity spottedOre,
                    candidate => !state.Visited.Contains(candidate.net.ID)
                        && !IsLootTargetClaimed(candidate.net.ID)
                        && !IsInPoisonedZone(candidate.transform.position, state)
                        && !IsInThreatFleeZone(characterId, candidate.transform.position)
                        && !IsInMonumentAvoidZone(candidate.transform.position)
                        && !IsBelowSafeLootDepth(candidate.transform.position)
                        && IsWithinEyesightCone(currentNpc, candidate)
                        && HasLineOfSight(currentNpc, candidate)
                        && !IsResourceNodePoisoned(candidate)
                        // Same base-only condition as the primary ore search above.
                        && (survivor.Character.Home != null || !IsEarlyGameRestrictedOre(candidate))))
            {
                StopExtendedOreSearch(characterId);
                state.Visited.Add(spottedOre.net.ID);
                ClaimLootTarget(state, spottedOre.net.ID);
                VerbosePuts($"gather-task: '{survivor.Character.Alias}' spotted an ore node while searching - diverting to mine it.");
                GatherOreAndContinue(survivor, spottedOre, state);
            }
        });
    }
}
