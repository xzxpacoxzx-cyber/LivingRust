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
/// Real ACTIVE resource gathering (2026-08-25) - chopping a standing
/// TreeEntity for wood, mining a real OreResourceEntity for stone/sulfur/
/// metal/HQ metal ore. The first piece of "resource gathering -> crafting
/// -> base building," per Lucas's own explicit sequencing (crafting/
/// building can't have anything real to decide about until raw materials
/// actually exist).
///
/// Genuinely separate from this project's existing "loot" pipeline
/// (StorageContainer/LootableCorpse/DroppedItemContainer/CollectibleEntity,
/// all in LivingRust.Looting.cs) - those are all passive item-transfer, or
/// (StartAttackingContainer's barrel-breaking) a simple BaseEntity.Hurt()
/// call. A tree/ore node's real "give the player wood/stone" logic lives
/// entirely inside a separate Facepunch component, ResourceDispenser
/// (confirmed via full ilspycmd decompile - GatherType.Tree/Ore/Flesh,
/// the same component a LootableCorpse's "gather meat" also uses under
/// GatherType.Flesh). It's ONLY ever triggered by the real attack
/// pipeline: BaseMelee.GetAttackStats sets HitInfo.CanGather from the
/// tool's own real per-GatherType gathering stats, then
/// target.OnAttacked(info) forwards to
/// ResourceDispenser.OnResourceDispenserAttacked - a plain
/// BaseEntity.Hurt() call (StartAttackingContainer's own technique) never
/// sets CanGather at all, so reusing that exact barrel-breaking method
/// here would just damage the tree with zero wood ever produced.
/// GiveResources (inside ResourceDispenser, confirmed via decompile)
/// deposits gathered wood/stone/ore straight into the player's real
/// inventory via BasePlayer.GiveItem (GiveItemOptions.BackpackOverflow) -
/// no separate ground-pickup step needed, unlike every other loot source
/// this project handles.
/// </summary>
public partial class LivingRust
{
    /// <summary>
    /// Real wood-gathering tool ranking, best first (2026-08-25, Lucas's
    /// own explicit correction - the earlier version of this list also
    /// included the pickaxe family/jackhammer as a lower-priority fallback,
    /// which was wrong: "the hatchet family of tools does not damage ore
    /// nodes" and, symmetrically, pickaxe-family/jackhammer tools do
    /// zero real damage to trees - there's no cross-capability to fall
    /// back through at all. "rock" is the ONLY genuine universal fallback
    /// (GiveStartingKit always gives one, and it has real - if modest -
    /// gather damage against both Tree and Ore). See
    /// HasAnyGatherCapableTool's own doc comment for how this gates the
    /// search itself, not just tool selection once already committed.
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
    /// Real "can this survivor even attempt this resource type at all"
    /// pre-check (2026-08-25, Lucas's own explicit request: "negate trees
    /// if they only hold a pickaxe family item or jackhammer... vice versa
    /// for ores, the hatchet family of tools does not damage ore nodes").
    /// Checked BEFORE ever searching/walking toward a node - without this,
    /// a bot with only a pickaxe would still walk all the way to a tree
    /// and only discover it has nothing capable of damaging it once
    /// already there, which is exactly the "set out to chop wood
    /// empty-handed" waste this exists to avoid. toolPriority is the same
    /// real capability list EquipBestGatherToolForType itself uses for
    /// this resource type, so this can never disagree with what actually
    /// gets equipped afterward.
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
    /// Same targeted-belt-slot-swap shape as EquipBestMeleeTool
    /// (LivingRust.Looting.cs) - belt-first search (avoids the "no free
    /// belt slot" capacity trap OrganizeBelt's 6-slot scheme creates),
    /// falls back to main+belt, swaps into the currently-equipped item's
    /// own belt position if the pick isn't already on the belt. Returns
    /// the equipped BaseMelee directly (or null) since every caller here
    /// immediately needs it for the real gather swing anyway.
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
    /// Real per-tick nudge distance while establishing facing toward a
    /// gather target - same magnitude/reasoning as FirstShotTelegraphNudgeDistance
    /// (LivingRust.Combat.cs).
    /// </summary>
    private const float ResourceGatherFacingNudgeDistance = 0.02f;

    /// <summary>
    /// How long the nudge phase runs once gathering starts (2026-08-25,
    /// Lucas's own explicit request: "have the bot actually look at the
    /// tree or ore it is intending to hit... it isn't doing the same as
    /// combat where it is visually facing away"). Same root cause already
    /// root-caused for combat's own visible-facing bug (this project's own
    /// roadmap, Phase 1b): a disconnected bot's rotation-only server
    /// updates (transform.rotation/OverrideViewAngles/eyes.NetworkUpdate)
    /// never actually reach an observer's screen - confirmed via decompile,
    /// NetworkPositionTick only populates for players that just sent a
    /// real PlayerTick RPC. Real POSITION changes DO visibly broadcast
    /// though, and the client's own character model infers orientation
    /// from OBSERVED MOVEMENT, not the explicit networked rotation value -
    /// same fix FirstShotTelegraphNudgeDistance already established for
    /// combat's own first-shot turn. Deliberately time-bounded rather than
    /// nudging for the WHOLE gather (which could run dozens of seconds) -
    /// nudging every tick toward the same target monotonically walks the
    /// bot into the node over time with no natural stopping point; a
    /// short window right at the start is enough to establish the correct
    /// visible facing once, matching combat's own one-shot-per-fight
    /// telegraph shape.
    /// </summary>
    private const float ResourceGatherFacingNudgeSeconds = 0.6f;

    /// <summary>
    /// Same rotation-set shape as AimAtContainer (LivingRust.Looting.cs),
    /// plus an optional brief real position nudge - see
    /// ResourceGatherFacingNudgeSeconds' own doc comment for why the nudge
    /// exists and why it's time-bounded rather than continuous.
    ///
    /// The aim TARGET's height is deliberately NOT the entity's raw
    /// WorldSpaceBounds().position (2026-08-25, Lucas's own live report:
    /// "the bots seem to look up at the sky when they are farming trees").
    /// A tree's own real bounds include its full canopy/branch spread
    /// (confirmed live via /lr.debug.scan: a single beech tree's bounds
    /// extend 6m+ vertically), so aiming at the raw bounds centre tilts the
    /// head sharply upward - a real player chopping a tree looks roughly
    /// at the trunk, level with their own eyes, not up at the crown. Ore
    /// nodes get their own real behaviour instead (Lucas's own explicit
    /// follow-up): a real player's gaze naturally droops toward the rock
    /// as it's mined down and shrinks, so this interpolates from eye level
    /// (full health) down to the node's own base height (fully depleted)
    /// using its real Health()/MaxHealth() fraction - no separate state to
    /// track, it just reads however depleted the node already is each
    /// tick.
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

    // Real tree/ore health pools run noticeably higher than a barrel's -
    // a higher cap than MaxHitsPerContainer (20) avoids prematurely giving
    // up on a genuinely fellable/mineable node.
    private const int MaxHitsPerResourceNode = 60;

    /// <summary>
    /// Real per-swing gather trigger - see this file's own top-of-file doc
    /// comment for why a plain Hurt() (StartAttackingContainer's own
    /// barrel-breaking technique) can't be reused here. Mirrors
    /// StartAttackingContainer's own ServerUse()+CancelInvoke(ServerUse_Strike)
    /// pattern for the swing animation/sound (same double-hit-avoidance
    /// reasoning - the real native ServerUse_Strike hit-test is cancelled
    /// before it can fire, only this method's own controlled hit ever
    /// lands), but swaps the actual damage/gather call from Hurt() to
    /// melee.DoAttackShared(HitInfo) - the real method GetAttackStats
    /// (sets CanGather from the tool's own configured gather stats) AND
    /// OnAttacked (the entity's own real reaction, which is what actually
    /// invokes ResourceDispenser.DoGather) AND the real impact effect AND
    /// tool condition loss AND the real attack cooldown all run through in
    /// one call - genuinely the same pipeline a real swing uses, just
    /// invoked directly/synchronously instead of waiting on
    /// ServerUse_Strike's own real-but-flaky-against-a-stationary-NPC
    /// raycast timing. DoAttackShared already fires its own real impact
    /// effect internally (confirmed via decompile), so no separate
    /// PlayMeleeImpactEffect call is needed here the way barrel-breaking
    /// needs one of its own.
    /// </summary>
    /// <summary>
    /// Real gather-yield hotspot bonus (2026-08-25, Lucas's own explicit
    /// request/"free handicap" framing, then his own correction: "each hit
    /// on the X adds up a multiplier, it doesn't just instantly jump to
    /// 2x... same for ores of any kind"). Matches the real formula exactly
    /// (confirmed via decompile, TreeEntity.OnAttacked): a real player's
    /// FIRST landed hit is level 0 (gatherScale = 1f + clamp(0 * 0.125f,
    /// 0f, 1f) = 1.0x, no bonus at all yet), climbing by 0.125x per
    /// consecutive hit, capping at 2.0x from the 9th hit onward
    /// (level 8: 1 + 8*0.125 = 2.0, clamped). hitIndex here is 0-based
    /// (the caller's own landed-hit counter, already incremented before
    /// this call, minus 1) to match that real zero-indexed level exactly.
    /// The red-X marker itself is purely a CLIENT-visual effect (broadcast
    /// via ClientRPC, not a real server-side networked entity - confirmed
    /// live, a /lr.debug.scan right next to an actively-farmed tree found
    /// nothing resembling one), so there's no real position to detect/
    /// replicate - HitInfo.gatherScale is just a public field
    /// ResourceDispenser.DoGather reads directly, and TreeEntity's own
    /// bonus-recalculation block only ever overwrites it when a REAL
    /// marker/bypass-item condition is true (neither ever true for this
    /// synthetic hit), so setting it here flows straight through untouched
    /// either way - same real formula, just computed here instead of
    /// requiring an actual on-screen marker to chase.
    /// </summary>
    private const float ResourceGatherBonusPerHit = 0.125f;

    private const float ResourceGatherBonusMax = 1f;

    private static float GetResourceGatherBonusScale(int hitIndex)
    {
        return 1f + Mathf.Clamp(hitIndex * ResourceGatherBonusPerHit, 0f, ResourceGatherBonusMax);
    }

    private void SwingGatherTool(BasePlayer npc, BaseMelee melee, ResourceEntity node, int hitIndex)
    {
        // Real jackhammer engine state (2026-08-25, Lucas's own live
        // report: "that visual bug of swapping back to the invisible tool
        // when they have the jackhammer out"). Confirmed via decompile -
        // Jackhammer (: BaseMelee) has a genuine networked on/off flag
        // (Flags.Reserved8, set via SetEngineStatus, broadcast with a real
        // SendNetworkUpdate) that a real client normally toggles by RPC
        // right before swinging it (revving it up). A disconnected bot
        // never sends that RPC, so without this the engine flag just
        // never turns on at all - the model sits in its real "off" pose
        // the whole time, which is almost certainly what read as an
        // "invisible tool." SetEngineStatus is a public method - calling
        // it directly server-side (no RPC ceremony needed, we already ARE
        // the server) turns it on for real, correctly networked. Nothing
        // needs to explicitly turn it back off - Jackhammer.SetHeld(false)
        // already does that automatically the moment the bot equips
        // anything else.
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
    /// Real swing loop against a live resource node - same AttackHitInterval
    /// cadence/LOS/range-recheck shape as StartAttackingContainer, minus
    /// the inventory-transfer step (GiveResources already deposits gathered
    /// wood/stone/ore directly into the survivor's inventory - see this
    /// file's own top-of-file doc comment). onSuccess fires once the node
    /// is genuinely depleted (or the hit cap is hit - already-gathered
    /// resources stay gathered either way, this is just "stop swinging at
    /// a stump"); onFailed fires for every way this never got a real
    /// swing in at all (no tool, unreachable, despawned before arrival).
    /// </summary>
    private void StartGatheringResourceNode(Survivor survivor, ResourceEntity node, string[] toolPriority, Action onSuccess, Action onFailed)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveAttack(characterId);

        BaseMelee melee = EquipBestGatherToolForType(survivor, toolPriority);

        if (melee == null)
        {
            // Genuinely bare-handed (lost/dropped even the starting rock) -
            // real Rust can't gather with no tool at all, no point looping.
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
                // Depleted (real success - resources already deposited via
                // ResourceDispenser.GiveResources/AssignFinishBonus, no
                // separate pickup needed here) or despawned out from under
                // it - either way nothing left to swing at.
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onSuccess?.Invoke();
                return;
            }

            // Real range check is type-aware (2026-08-25, Lucas's own live
            // report: ore gathering "walk up to it, give up and leave" -
            // every single logged ore attempt failed on the very first
            // tick, before a single hit ever landed). IsWithinResourceNodeRange
            // measures from the node's own real transform.position, which
            // works well for a tree (a thin trunk whose pivot sits right
            // at its own base/ground level) but not for ore - a real rock's
            // pivot commonly sits nearer the CENTRE of its physical mass,
            // so standing right at its visible surface can still be well
            // outside a tight tolerance measured from that pivot. Ore
            // reuses the original bounds-based IsWithinLootRange instead
            // (the same real check StartAttackingContainer's own barrel-
            // breaking already relies on) - a rock's own WorldSpaceBounds()
            // genuinely does represent its real surface (unlike a tree's,
            // which balloons out to include the whole canopy), so the
            // bounds-based check that was wrong for trees is actually
            // right for ore.
            bool inRange = node is TreeEntity
                ? IsWithinResourceNodeRange(npc, node)
                : IsWithinLootRange(npc, node);

            if (!HasLineOfSight(npc, node) || !inRange)
            {
                // See GatherEngagementTimeoutSeconds' own doc comment - a
                // survivor that already landed a real hit loses range/LOS
                // instantly and fails immediately, same as before (a
                // legitimate mid-gather interruption, not a dead node).
                // One that never got a single hit in gets a real
                // GatherEngagementTimeoutSeconds window first - only once
                // that fully elapses with zero hits does this count as a
                // genuine dead end worth poisoning for every survivor.
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

                // Still within the engagement window - keep waiting instead
                // of failing on the very first bad tick, but skip the swing
                // logic below (nothing to aim/swing at while out of range).
                return;
            }

            // Runs every tick regardless of the cooldown gate below, not
            // just on ticks that actually swing - see AimAtResourceNode's
            // own doc comment for why. Keeps the bot visibly turned toward
            // the node for the whole gather, not just each swing instant.
            bool stillEstablishingFacing = Time.realtimeSinceStartup - gatherStartTime < ResourceGatherFacingNudgeSeconds;
            AimAtResourceNode(npc, node, nudge: stillEstablishingFacing);

            if (melee.HasAttackCooldown())
            {
                // Still mid-swing/reset for this exact tool's real
                // repeatDelay - same real per-weapon gate
                // StartAttackingContainer's own swing loop uses.
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
    /// Real melee reach against a TREE specifically (2026-08-25, Lucas's
    /// own live report: "the bot isn't actually within 'hitting distance'
    /// of the tree and is about 3-5 meters away"). Root cause:
    /// GetApproachPoint/IsWithinLootRange (LivingRust.Looting.cs) both
    /// measure against the entity's own WorldSpaceBounds() - fine for a
    /// barrel (a small, tight collider), but a real tree's bounds include
    /// its whole canopy/branch spread, so "just outside the bounds" can
    /// land several real metres from the trunk itself. These two measure
    /// against the node's own real transform.position (its actual base
    /// pivot, right at ground level for a tree) instead, with a standoff/
    /// tolerance close to real melee reach (BaseMelee.maxDistance default
    /// 1.5f, confirmed via decompile - same real value
    /// ContainerApproachStandoffDistance's own doc comment already cites
    /// for barrels).
    ///
    /// TREE-ONLY (2026-08-25, follow-up live report: ore gathering "walk
    /// up to it, give up and leave") - a real ore rock's pivot commonly
    /// sits nearer the CENTRE of its physical mass rather than at its
    /// visible surface the way a tree trunk's base does, so this same
    /// tight position-based tolerance made every real ore attempt fail on
    /// arrival before a single hit landed. Ore keeps using the original
    /// bounds-based GetApproachPoint/IsWithinLootRange instead (see
    /// GatherOreAndContinue/StartGatheringResourceNode's own inRange
    /// branch) - a rock's own WorldSpaceBounds() genuinely does represent
    /// its real surface (it doesn't balloon out the way a tree's
    /// canopy-inclusive bounds do), so the bounds-based check that was
    /// wrong for trees is actually right for ore.
    /// </summary>
    private const float ResourceNodeApproachStandoffDistance = 1f;

    private const float ResourceNodeInteractionRange = 1.5f;

    /// <summary>
    /// How close a "stuck" position needs to be to a live tree/ore node
    /// before EscalateStuckRecovery (LivingRust.Looting.cs) treats the node
    /// itself as the likely cause and gathers it instead of running the
    /// normal wiggle/nudge/teleport ladder. Matches ResourceNodeInteractionRange
    /// rather than a tighter 1m literal - ore's own bounds-based range check
    /// inside StartGatheringResourceNode is the real final gate anyway, this
    /// only needs to be generous enough to catch the node as a candidate at
    /// all.
    /// </summary>
    private const float StuckResourceNodeDetectionRadius = 1.5f;

    /// <summary>
    /// Real per-node "give up and blacklist" timeout (2026-09-01, Lucas's
    /// own explicit spec: "if the bot doesn't get successful return /
    /// engagement from that specific task... it just fails after 10
    /// seconds and tries a different tree or a separate task... + poisons
    /// that tree for itself and any other bots for 10 minutes"). Root
    /// cause this fixes: StartGatheringResourceNode's own range/LOS check
    /// previously failed INSTANTLY on the very first tick with zero hits
    /// landed if the approach point turned out to be unreachable (a live
    /// trace confirmed this - "gather-task: can't reach 'X' anymore -
    /// giving up" logged the same tick gathering started, hits=0) - fine
    /// as a fast-fail for a target that genuinely disappeared mid-swing,
    /// but far too eager for a node that was simply never reachable to
    /// begin with. Only applies while hits == 0 (see the real per-tick
    /// check below) - once a survivor has actually landed a hit, losing
    /// range/LOS afterward is treated as a real, immediate interruption
    /// same as before (the node itself was legitimately being worked, not
    /// a dead end), and does NOT poison it.
    /// </summary>
    private const float GatherEngagementTimeoutSeconds = 10f;

    /// <summary>
    /// Global, cross-survivor blacklist for a resource node that timed out
    /// with zero real engagement (see GatherEngagementTimeoutSeconds) -
    /// deliberately NOT the existing per-task LootTaskState.PoisonedZones
    /// (LivingRust.Looting.cs), which only ever poisons an AREA for the ONE
    /// survivor that hit it, resets every fresh life/task, and wouldn't
    /// stop a second bot from walking into the exact same dead node five
    /// seconds later. This is keyed to the specific node's own NetworkableId,
    /// shared by every survivor's candidate search (every TryFindNearestTreeEntity/
    /// TryFindNearestOreResourceEntity filter below now excludes a poisoned
    /// node), and expires on its own after ResourceNodePoisonDurationSeconds
    /// rather than needing any explicit per-task cleanup.
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
    /// Walk-then-gather wrapper for a real TreeEntity - same
    /// StartWalkingWithRecovery shape every other real loot-source
    /// dispatch in ContinueLootTask already uses, but GetResourceNodeApproachPoint
    /// instead of the generic bounds-based GetApproachPoint - see its own
    /// doc comment for why.
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
                // forceLocalScan: true (2026-08-25, Lucas's own live
                // report: "after the bots are finished mining the tree
                // they just beeline to somewhere") - ContinueLootTask's
                // own 50/50 dice roll to skip local search entirely and
                // jump straight to a distant road/monument (existing
                // behaviour, built for spreading bots off exhausted local
                // ground) was firing immediately after a gather finished,
                // even with other unvisited trees genuinely still nearby -
                // confirmed live, a bot walked 117m for a road right after
                // felling a tree. Real trees/ore cluster far more than
                // containers do, so skipping the very next local check
                // wastes an obvious opportunity in a way that reads as
                // erratic. Same forceLocalScan escape hatch this project
                // already uses elsewhere for "guarantee this one cycle
                // actually looks locally before any dice get rolled."
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
    /// Same idea as GatherTreeAndContinue, for a real OreResourceEntity -
    /// but uses the original bounds-based GetApproachPoint (LivingRust.
    /// Looting.cs), NOT GetResourceNodeApproachPoint - see
    /// ResourceNodeApproachStandoffDistance's own doc comment for why ore
    /// specifically needs the opposite fix from trees.
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
                // forceLocalScan: true - see GatherTreeAndContinue's own
                // doc comment for why.
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
    /// En-route sibling of GatherTreeAndContinue (2026-09-15, LivingRust.
    /// Looting.cs's own TryFindEnRouteLootCandidate/StartLongDistanceWalkDirect) -
    /// same real full-node-clear gather via StartGatheringResourceNode, but
    /// resumes the survivor's ORIGINAL long-distance walk afterward instead
    /// of ContinueLootTask, since this fires mid-journey to a destination
    /// the survivor hadn't arrived at yet rather than as part of the normal
    /// local loot loop.
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
    /// En-route sibling of GatherOreAndContinue - see GatherEnRouteTreeAndResume's
    /// own doc comment for the full reasoning, identical shape.
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

    // How far to look for a live tree/ore node when nothing else is left
    // to loot nearby - same real ballpark as LootSearchRadius (50f), not
    // reused directly since resource nodes are a genuinely separate search
    // tier (the fallback-of-last-resort, see ContinueLootTask's own call
    // site) rather than blended into the main proximity-priority search
    // the other six loot categories already share.
    private const float ResourceNodeSearchRadius = 50f;

    /// <summary>
    /// Real fallback-of-last-resort (2026-08-25) - only ever consulted
    /// from ContinueLootTask's own "found nothing at all" branch, right
    /// before EscalateSearchToMonumentZone. Deliberately NOT folded into
    /// the main six-category proximity-priority blend above it (container/
    /// barrel/corpse/bag/dropped-item/collectible) - that system is
    /// already extremely tightly tuned across many live-test passes
    /// (NearbyLootPriorityRadius overrides, strict tiers, per-type
    /// exclusion filters), and active gathering is a genuinely different
    /// kind of need (raw materials, not scavenged items) rather than one
    /// more thing to blend into "whichever's closest." Trees checked
    /// before ore - wood is the more universally needed resource for
    /// whatever crafting/building eventually needs it. Returns whether a
    /// gather was actually started, so the caller can fall through to
    /// EscalateSearchToMonumentZone if even this comes up empty.
    /// </summary>
    /// <summary>
    /// Sticky per-survivor gather-type lock (2026-08-28, Lucas's own live
    /// report: bots assigned to mine ore kept drifting back to trees the
    /// very next time this fallback ran, since it unconditionally tried
    /// tree first on every single call regardless of what the survivor was
    /// just doing). true = locked onto trees, false = locked onto ore.
    /// Set the first time a survivor actually starts gathering either type
    /// (below), and consulted here to restrict this fallback to ONLY that
    /// type from then on - "just for now" per Lucas's own framing, not a
    /// real decision-making system (that's the deferred separate
    /// TaskType.GatherResources work). Cleared on death/despawn
    /// (LivingRust.Hooks.cs) so a fresh life can pick either type again.
    /// </summary>
    private readonly Dictionary<Guid, bool> _resourceGatherTypeLock = new();

    private bool TryStartResourceGatheringFallback(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        // High-gear discipline (see LivingRust.HighGearResources.cs): above the
        // gear threshold a survivor doesn't go farming nodes at all unless it
        // already has a base AND is already carrying some raw resource.
        if (GetGearScore(npc) > HighGearResourceAvoidScore
            && (survivor.Character.Home == null || !HasAnyRawResourceInInventory(npc)))
        {
            return false;
        }
        bool? lockedToTree = _resourceGatherTypeLock.TryGetValue(characterId, out bool locked) ? locked : (bool?)null;

        // Real quota-driven handoff (2026-08-28, Lucas's own explicit
        // spec: "farm wood first, get to 3000, then quit that task and
        // farm ore until 5000 stone is acquired") - unlike the ore quota
        // below (which is terminal, idles in place once reached), hitting
        // the wood quota while locked to trees releases the lock instead
        // of just stopping, so this exact same call falls through to the
        // ore branch below rather than needing a second cycle to notice.
        //
        // HasEnoughWoodAlready (2026-09-19, LivingRust.Looting.cs) checked
        // ALONGSIDE the cumulative WoodQuota check, not instead of it -
        // live report: '8LostFarmer' sat on 6000 wood despite hitting this
        // exact "heading to a nearby tree" branch 25 separate times, NEVER
        // once logging "reached its wood quota." WoodQuota/_totalWoodGathered
        // tracks cumulative wood ever gathered through this one fallback
        // specifically, never decremented by crafting/spending, and blind
        // to wood gained through every OTHER source (collectible pickups,
        // en-route stops, loot bags, checklist fetches) - real evidence it
        // wasn't actually capping anything for this bot. HasEnoughWoodAlready
        // instead checks what the survivor is ACTUALLY CARRYING right now
        // against what it actually needs (the rolled base design's real
        // cost + buffer once one exists, a flat pre-design cap before
        // that) - the same real target the en-route fix already uses, now
        // applied here too since this fallback turned out to be the far
        // bigger contributor. Same lock-release/log/fall-through-to-ore
        // shape as the original quota check, so either one reaching its
        // limit behaves identically.
        if (lockedToTree == true && (HasReachedWoodQuota(characterId) || HasEnoughWoodAlready(survivor, npc)))
        {
            Puts($"gather-task: '{survivor.Character.Alias}' has enough wood for now ({_totalWoodGathered.GetValueOrDefault(characterId):F0} gathered via this fallback) - switching to mining ore instead.");
            _resourceGatherTypeLock.Remove(characterId);
            lockedToTree = null;
        }

        if (lockedToTree != false
            && !HasReachedWoodQuota(characterId)
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

        if (HasReachedOreQuota(survivor.Character.Id) || !HasAnyGatherCapableTool(npc, OreGatherToolPriority))
        {
            return false;
        }

        if (_engine.NavigationManager.TryFindNearestOreResourceEntity(
                npc.transform.position,
                ResourceNodeSearchRadius,
                out OreResourceEntity ore,
                candidate => !state.Visited.Contains(candidate.net.ID)
                    && !IsLootTargetClaimed(candidate.net.ID)
                    && !IsInPoisonedZone(candidate.transform.position, state)
                    && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                    && !IsInMonumentAvoidZone(candidate.transform.position)
                        && !IsBelowSafeLootDepth(candidate.transform.position)
                    && !IsResourceNodePoisoned(candidate)))
        {
            state.Visited.Add(ore.net.ID);
            ClaimLootTarget(state, ore.net.ID);
            _resourceGatherTypeLock[characterId] = false;
            VerbosePuts($"gather-task: '{survivor.Character.Alias}' found nothing left to loot nearby - heading to a nearby ore node to mine instead.");
            GatherOreAndContinue(survivor, ore, state);
            return true;
        }

        // Real extended ore search (2026-08-25, Lucas's own explicit
        // request) - nothing within the normal ResourceNodeSearchRadius
        // (50m), so rather than falling through to the generic road/
        // monument-zone escalation (built for containers, not raw
        // materials), head toward the nearest real monument specifically -
        // Rust's own quarries/mining outposts are exactly where dense ore
        // clusters actually tend to be. See StartExtendedOreSearch's own
        // doc comment for the full periodic-scan/divert/quota mechanism.
        // Already confirmed above (HasAnyGatherCapableTool) that this
        // survivor actually owns something capable of mining before ever
        // starting the walk.
        //
        // Restricted to survivors actually pursuing base-gathering
        // (2026-09-01, Lucas's own explicit ask: "remove that extended ore
        // search UNTIL the bots travel inland and decide 'I want to build
        // a base'... rather than check for that beforehand") - this is
        // exactly the mechanism that kept sending ordinary looting bots
        // toward monuments with known bad geometry (apartments_complex's
        // own unclimbable entrance step, before that monument was even
        // fixed) for no strong enough reason - normal looting doesn't
        // really need ore badly enough to justify a long walk into
        // whatever monument happens to be nearest. Falls through to
        // EscalateSearchToMonumentZone instead (this function's own return
        // false), same as if extended search had simply never existed, for
        // any survivor not in _pursuingBaseGatherGoal.
        if (_pursuingBaseGatherGoal.Contains(characterId))
        {
            StartExtendedOreSearch(survivor, npc, state);
            return true;
        }

        return false;
    }

    // ============================================================
    // Extended ore search (2026-08-25) - triggered from
    // TryStartResourceGatheringFallback above once no ore is found within
    // the normal ResourceNodeSearchRadius. Lucas's own explicit spec: walk
    // toward the nearest monument, re-scan every 10s for a real ore node
    // within a genuine 180-degree eyesight cone (not omniscient awareness),
    // divert and clear out that immediate area before resuming the walk/
    // scan cycle, and stop entirely (idle in place) once any one of three
    // real cumulative totals is reached.
    // ============================================================

    private const float ExtendedOreSearchIntervalSeconds = 10f;

    /// <summary>
    /// Half-angle of the real "can it actually see this" cone (2026-08-25,
    /// Lucas's own explicit spec: "within LOS 180 degree cone of it's
    /// eyesight"). 90 degrees either side of the survivor's own current
    /// facing = a full 180-degree front hemisphere, excluding anything
    /// behind it - a real player scanning for ore while walking doesn't
    /// have eyes in the back of their head.
    /// </summary>
    private const float ExtendedOreSearchConeHalfAngle = 90f;

    // Real shortnames confirmed via decompile (ResourceDispenser.
    // CacheResourceTypeItems) - "stones" specifically, not "stone".
    private const string StoneShortname = "stones";
    private const string MetalOreShortname = "metal.ore";
    private const string SulfurOreShortname = "sulfur.ore";
    private const string WoodShortname = "wood";

    /// <summary>
    /// Real cumulative quota - lowered from 5000/3000/2000 to a flat 1000
    /// each (2026-09-01, Lucas's own explicit spec for the post-checklist
    /// gear-weighted stage specifically: "cap stone / wood gathering to
    /// 1000 per type of ore / wood. That way it can focus more on looting
    /// barrels/monuments" - raw resource farming was crowding out real
    /// container/monument looting once a survivor graduated past the
    /// primitive checklist). Hitting ANY ONE of these three stops the
    /// whole extended search (and the normal nearby-ore fallback above)
    /// outright, not "all three." Lifetime totals per survivor, tracked
    /// via OnDispenserGathered below - reset on death/respawn (a fresh
    /// life has no memory of a previous one's mining), same as most other
    /// per-survivor state this project clears there.
    /// </summary>
    private const float OreQuotaStone = 1000f;

    private const float OreQuotaMetalOre = 1000f;

    private const float OreQuotaSulfurOre = 1000f;

    /// <summary>
    /// Real cumulative wood quota - lowered from 3000 to 1000 alongside the
    /// ore quotas above (2026-09-01, same "focus more on looting barrels/
    /// monuments" spec). Same lifetime-total-per-survivor shape as the ore
    /// quotas, but its own dedicated dictionary/threshold since wood is
    /// tracked and gated completely separately from the tree-vs-ore
    /// gather-type lock (TryStartResourceGatheringFallback releases that
    /// lock once this is reached, handing off to ore rather than just
    /// stopping the way the ore quota below does).
    /// </summary>
    private const float WoodQuota = 1000f;

    private readonly Dictionary<Guid, float> _totalStoneGathered = new();
    private readonly Dictionary<Guid, float> _totalMetalOreGathered = new();
    private readonly Dictionary<Guid, float> _totalSulfurOreGathered = new();
    private readonly Dictionary<Guid, float> _totalWoodGathered = new();

    private bool HasReachedWoodQuota(Guid characterId)
    {
        return _totalWoodGathered.GetValueOrDefault(characterId) >= WoodQuota;
    }

    /// <summary>
    /// Real, general Carbon/Oxide hook (confirmed via decompiling
    /// Carbon.Hooks.Oxide.dll - a genuine transpiler patch into
    /// ResourceDispenser.GiveResourceFromItem, the exact real method both
    /// a normal player's swing AND this project's own SwingGatherTool
    /// ultimately reach) - fires for every real resource actually handed
    /// to a player, whatever the source. Tracks lifetime stone/metal/
    /// sulfur ore and wood totals per survivor for the quota checks; every
    /// other gathered resource (HQ metal, etc) is deliberately ignored
    /// here, Lucas's own quotas only ever named these four.
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

    private bool HasReachedOreQuota(Guid characterId)
    {
        return _totalStoneGathered.GetValueOrDefault(characterId) >= OreQuotaStone
            || _totalMetalOreGathered.GetValueOrDefault(characterId) >= OreQuotaMetalOre
            || _totalSulfurOreGathered.GetValueOrDefault(characterId) >= OreQuotaSulfurOre;
    }

    private readonly Dictionary<Guid, Timer> _extendedOreSearchTimers = new();

    /// <summary>
    /// Real "am I facing roughly toward this" check - flat (Y-ignored,
    /// matches every other horizontal facing/direction check in this
    /// project) angle between the survivor's own current eye direction and
    /// the candidate, against ExtendedOreSearchConeHalfAngle.
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
    /// Kicks off the walk toward the nearest real monument plus its own
    /// repeating scan - called once from TryStartResourceGatheringFallback
    /// when nothing's within the normal search radius. No maxDistance cap
    /// on the monument search itself (a generous 4000f, well beyond any
    /// real map's extent) - Lucas's own spec was just "the nearest
    /// monument," not one within some further range limit of its own.
    /// </summary>
    private const float ExtendedOreSearchMonumentSearchRadius = 4000f;

    private void StartExtendedOreSearch(Survivor survivor, BasePlayer npc, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        // Real gap fix (2026-09-01, live report + log-confirmed: GhostDigger
        // - out of ore, nothing craftable, no home-gather target ready -
        // literally logged "heading toward 'stables_a.prefab'" and got
        // stuck there, exactly explaining the safezone pileups even after
        // every other monument-avoidance fix landed). This is a COMPLETELY
        // different mechanism from the container/corpse/resource-node
        // scans fixed earlier - it heads a bot toward whichever monument is
        // LITERALLY nearest with zero regard for type, so a bot that
        // happens to be near a safezone when it runs low on ore walks
        // straight into it and scans there repeatedly. Excluding avoided
        // monuments here is the real fix for this specific bot's own
        // behavior; a plain TryGetNearestMonument call has no filter
        // parameter, so this loop reimplements it with one.
        MonumentInfo monument = null;
        float bestDistanceSqr = ExtendedOreSearchMonumentSearchRadius * ExtendedOreSearchMonumentSearchRadius;

        foreach (MonumentInfo candidate in TerrainMeta.Path?.Monuments ?? Enumerable.Empty<MonumentInfo>())
        {
            if (candidate == null || IsMonumentExcludedFromAutonomy(candidate))
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
            // No real monument anywhere on this map at all - genuinely
            // nothing productive left to do for ore specifically. Falls
            // back to whatever ContinueLootTask's own caller does next
            // (EscalateSearchToMonumentZone/road-following) by simply not
            // starting anything here - TryStartResourceGatheringFallback
            // already returned true for this cycle though, so this is a
            // real, if extremely unlikely, dead end rather than a retry
            // loop.
            return;
        }

        VerbosePuts($"gather-task: '{survivor.Character.Alias}' found no ore within {ResourceNodeSearchRadius:F0}m - heading toward '{monument.name}', scanning every {ExtendedOreSearchIntervalSeconds:F0}s for one along the way.");

        // Real live bug (2026-08-28, Lucas's own report: 'ScrappyWeasel'
        // oscillating in place right at the edge of power_sub_big_1, no
        // stuck/phasing diagnostics ever firing) - this used to walk
        // straight at monument.transform.position, which for a fenced
        // monument (power substations, water treatment, military bases,
        // ...) commonly sits INSIDE the perimeter fence, genuinely
        // unreachable from outside. Because the walk's own arrival
        // tolerance (WaypointArriveDistance) is far smaller than the gap
        // between "right at the fence" and "actually at that point," the
        // survivor reads as having "arrived" the moment it hits the fence,
        // immediately re-triggers this exact same fallback next
        // ContinueLootTask cycle, and repeats - a tight infinite loop that
        // never once invokes the stuck-recovery ladder (it never actually
        // fails to arrive), just silently burns CPU on repath spam
        // forever. The exact same monument-fence problem already got
        // solved for container looting (EscalateSearchToMonumentZone,
        // LivingRust.MonumentLootZones.cs, live-fixed for 'SilentHunter'
        // at power_sub_small_2) by walking to a real scanned loot-zone
        // point instead of the monument's own transform - reusing that
        // exact same known-good data here rather than the raw pivot.
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
                // Reached the monument with nothing spotted en route - one
                // more real local scan right here (monuments/quarries are
                // exactly where ore clusters), then genuinely give up on
                // this cycle if even that comes up empty, same as any
                // other exhausted search.
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

            if (HasReachedOreQuota(characterId))
            {
                StopExtendedOreSearch(characterId);
                CancelActiveMovement(survivor);
                survivor.Character.CurrentTask = TaskType.None;
                Puts($"gather-task: '{survivor.Character.Alias}' reached its ore quota (stone {_totalStoneGathered.GetValueOrDefault(characterId):F0}, metal {_totalMetalOreGathered.GetValueOrDefault(characterId):F0}, sulfur {_totalSulfurOreGathered.GetValueOrDefault(characterId):F0}) - idling in place.");
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
                        && !IsResourceNodePoisoned(candidate)))
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
