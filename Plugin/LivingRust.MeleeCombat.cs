using System;
using Facepunch;
using LivingRust.Models;
using Oxide.Plugins;
using Rust;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Real melee-vs-players/animals (2026-09-01, Lucas's own explicit
/// request: "work on melee pvp/pve. this will be quite important for bots
/// to farm animals for cloth and to protect themselves"). Confirmed via
/// StartCombat's own prior doc comment that this genuinely never existed -
/// "No ranged weapon owned - explicitly out of scope for v1... melee-vs-
/// players is a separate, still-deferred scope." A survivor with nothing
/// but a melee tool used to just silently bail out of combat entirely (or
/// flee, for a real animal threat) - it never fought back.
///
/// Reuses the exact same real damage pipeline this project already proved
/// out for resource gathering (SwingGatherTool, LivingRust.ResourceGathering.cs -
/// ServerUse()+CancelInvoke(ServerUse_Strike) for the visual swing, then
/// BaseMelee.DoAttackShared(HitInfo) for the actual authentic damage,
/// which runs the weapon's own real GetAttackStats/OnAttacked/impact-
/// effect/condition-loss/cooldown pipeline - just retargeted from a
/// ResourceEntity to a living BaseCombatEntity, with no gatherScale
/// (that field only matters against a real ResourceDispenser). Registers
/// in the SAME _activeCombat/_activeCombatTarget dictionaries the ranged
/// system already uses, and disengages through the exact same real
/// EndCombat - dozens of other systems project-wide already check
/// _activeCombat to know "is this survivor currently fighting" (on-sight
/// scanning, the ghost-route combat-preemption guard, flee conflict
/// checks, ...); a separate parallel dictionary would leave every one of
/// them blind to an active melee fight.
/// </summary>
public partial class LivingRust
{
    // Real melee reach - tightened from an initial 2.5f (2026-09-01,
    // Lucas's own explicit correction: "have the bots be within 0.5
    // metres before they actually start attacking otherwise they reach
    // too far"). Used both as StartFollowing's own hold distance below AND
    // the per-tick swing-eligibility gate, so a survivor now genuinely
    // closes to point-blank before ever swinging, rather than stopping at
    // the old, more generous distance and landing hits from visibly too
    // far away.
    private const float MeleeEngagementRange = 0.5f;

    // Real per-swing step-in nudge (2026-09-01, Lucas's own explicit ask:
    // "take a slight step towards them with each hit so they visually are
    // facing target"). Same magnitude/reasoning as FirstShotTelegraphNudgeDistance
    // (this file's own sibling in ranged combat) and ResourceGatherFacingNudgeDistance
    // (LivingRust.ResourceGathering.cs) - a disconnected bot's rotation-
    // only updates (transform.rotation/OverrideViewAngles/eyes.NetworkUpdate)
    // never actually reach an observer's screen on their own (confirmed via
    // decompile: NetworkPositionTick only populates for a player that just
    // sent a real PlayerTick RPC), but a real POSITION change DOES
    // broadcast - a real client's own character model infers orientation
    // from OBSERVED MOVEMENT, not the explicit networked rotation value.
    // Applied on EVERY swing here (not just once at the start of the fight
    // the way the gather-facing nudge is time-bounded) - Lucas's own
    // explicit "with each hit" framing, giving a real lunge-forward visual
    // on every attack rather than a static plant-and-swing.
    private const float MeleeSwingStepNudgeDistance = 0.02f;

    // Real bounded-aggression cap (2026-09-01, Lucas's own explicit spec:
    // "chase the bot for 20 seconds and if the bot with the melee weapon
    // doesn't kill the bot or player, it gives up and resumes its other
    // task (de-aggro)"). Applied universally to every melee fight, not
    // just proactive on-sight aggression - Lucas's own literal example was
    // proactive ("another bot walks near it, attack"), but a real player
    // wouldn't chase a losing melee fight forever either, and most genuine
    // reactive self-defense fights resolve well within 20s anyway (melee
    // exchanges are fast), so a single shared cap covers both cases safely
    // without needing to thread a proactive/reactive distinction through
    // every one of StartCombat's own many call sites.
    private const float MeleeChaseGiveUpSeconds = 20f;

    /// <summary>
    /// Real melee fight loop - same overall shape as StartCombat's own
    /// ranged tick (StartFollowing to close distance, a real per-weapon
    /// attack-cooldown gate, disengage on low health, hand off to ranged
    /// combat if a real gun turns up mid-fight, real EndCombat teardown),
    /// but with melee's own much simpler range/cadence model instead of
    /// WeaponFireProfile's burst/band system - no ammo, no burst pacing,
    /// no engagement bands, just "am I in real swinging range, and has my
    /// weapon's own repeatDelay actually elapsed since the last swing."
    /// </summary>
    private void StartMeleeCombat(Survivor survivor, BaseCombatEntity attacker)
    {
        Guid characterId = survivor.Character.Id;

        if (_activeCombat.ContainsKey(characterId))
        {
            return;
        }

        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || attacker == null || attacker.IsDestroyed || !attacker.IsAlive())
        {
            return;
        }

        EquipBestMeleeTool(survivor);

        if (npc.GetHeldEntity() is not BaseMelee)
        {
            // Genuinely nothing to swing at all (shouldn't happen -
            // GiveStartingKit always gives a rock - but a real, cheap
            // guard rather than assuming).
            return;
        }

        CancelActiveMovement(survivor);
        CancelActiveAttack(characterId);
        CancelActiveRecycling(characterId);
        StopExtendedOreSearch(characterId);

        TaskType previousTask = survivor.Character.CurrentTask;
        float combatStartedAt = Time.realtimeSinceStartup;

        Puts($"'{survivor.Character.Alias}' engaging '{GetAttackerDisplayName(attacker)}' in melee combat.");

        _activeCombatTarget[characterId] = attacker;
        StartFollowing(survivor, attacker, MeleeEngagementRange, skipIdleFacing: true);

        Timer meleeTimer = null;

        meleeTimer = timer.Every(AttackHitInterval, () =>
        {
            BasePlayer currentNpc = survivor.Player;

            if (currentNpc == null || currentNpc.IsDestroyed || currentNpc.IsWounded() || survivor.Character.State == CharacterState.Dead)
            {
                EndCombat(characterId, meleeTimer, survivor, previousTask);
                return;
            }

            if (attacker == null || attacker.IsDestroyed || !attacker.IsAlive())
            {
                Puts($"'{survivor.Character.Alias}' finished off '{GetAttackerDisplayName(attacker)}' in melee.");
                EndCombat(characterId, meleeTimer, survivor, previousTask);
                return;
            }

            // Real weapon found mid-fight (looted while closing distance) -
            // hand off to the real ranged combat system instead, same
            // "re-enter through StartCombat's normal path" pattern
            // StartFleeingFromThreat's own mid-flight weapon check uses.
            if (currentNpc.GetHeldEntity() is BaseProjectile)
            {
                EndCombat(characterId, meleeTimer, survivor, previousTask);
                StartCombat(survivor, attacker);
                return;
            }

            if (currentNpc.health <= CombatFleeHealthThreshold)
            {
                Puts($"'{survivor.Character.Alias}' is too hurt to keep fighting '{GetAttackerDisplayName(attacker)}' in melee - disengaging.");
                EndCombat(characterId, meleeTimer, survivor, previousTask);
                StartFleeingFromThreat(survivor, attacker);
                return;
            }

            if (Time.realtimeSinceStartup - combatStartedAt >= MeleeChaseGiveUpSeconds)
            {
                Puts($"'{survivor.Character.Alias}' gave up on '{GetAttackerDisplayName(attacker)}' in melee after {MeleeChaseGiveUpSeconds:F0}s - de-aggroing.");
                EndCombat(characterId, meleeTimer, survivor, previousTask);
                return;
            }

            // Face the target EVERY tick, not just at swing time (2026-09-21,
            // Lucas's own live report: chasing bots faced odd directions,
            // usually away from their target). StartFollowing above is told
            // skipIdleFacing:true because combat is supposed to own facing,
            // but this tick only ever aimed inside the swing branch below -
            // so for the whole chase nothing set the facing at all and the
            // survivor kept looking down its last walk direction.
            AimAtPlayer(currentNpc, attacker);

            float distance = Vector3.Distance(currentNpc.transform.position, attacker.transform.position);

            if (distance > MeleeEngagementRange)
            {
                // Still closing - StartFollowing (fired once above, runs
                // on its own independent tick) handles the actual
                // movement, same "fire and forget" contract the ranged
                // combat tick already relies on.
                return;
            }

            if (currentNpc.GetHeldEntity() is not BaseMelee currentMelee || currentMelee.HasAttackCooldown())
            {
                // Either lost/swapped its melee weapon this exact tick, or
                // still mid-swing/reset for this tool's own real
                // repeatDelay - a real player can't click faster than
                // their weapon's own animation allows.
                return;
            }

            // ServerUse_Strike raycasts from the wielder's eyes forward -
            // needs to actually be looking at the target this tick for a
            // real hit to land, not just have arrived nearby once.
            AimAtPlayer(currentNpc, attacker);

            // Real per-swing step-in (2026-09-01, see MeleeSwingStepNudgeDistance's
            // own doc comment) - a small real position write toward the
            // target on every swing, so a watching client's own client-side
            // orientation inference (driven by observed movement, not the
            // rotation write above) actually shows the survivor facing/
            // lunging at its target each hit instead of looking static.
            Vector3 stepDirection = attacker.transform.position - currentNpc.transform.position;
            stepDirection.y = 0f;

            if (stepDirection.sqrMagnitude >= 0.0001f)
            {
                Vector3 steppedPosition = currentNpc.transform.position + stepDirection.normalized * MeleeSwingStepNudgeDistance;
                currentNpc.transform.position = steppedPosition;
                currentNpc.MovePosition(steppedPosition);
                survivor.Position = steppedPosition;
                survivor.Character.Position = steppedPosition;
            }

            // ServerUse() purely for the swing animation/sound (SignalBroadcast
            // + configured VFX) - real damage comes from DoAttackShared
            // below, not from ServerUse_Strike's own delayed native hit-
            // test, which gets cancelled immediately after being scheduled
            // (same double-hit-avoidance reasoning SwingGatherTool's own
            // doc comment gives for resource nodes).
            currentMelee.ServerUse();
            currentMelee.CancelInvoke(currentMelee.ServerUse_Strike);

            HitInfo info = Pool.Get<HitInfo>();
            info.Init(currentNpc, attacker, DamageType.Generic, 0f, attacker.transform.position);
            info.Weapon = currentMelee;
            info.WeaponPrefab = currentMelee;
            info.PointStart = currentNpc.eyes.position;
            info.PointEnd = attacker.transform.position;

            // Real authentic damage pipeline - DoAttackShared runs the
            // weapon's own GetAttackStats/OnAttacked/real impact effect/
            // condition loss/cooldown, the exact same one a real player's
            // own swing uses. No gatherScale set (that field only matters
            // against a ResourceEntity's own ResourceDispenser.DoGather,
            // irrelevant against a living target).
            currentMelee.DoAttackShared(info);

            Pool.Free(ref info);
        });

        _activeCombat[characterId] = meleeTimer;
    }
}
