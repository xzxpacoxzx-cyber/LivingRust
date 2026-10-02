using System;
using Facepunch;
using LivingRust.Models;
using Oxide.Plugins;
using Rust;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Melee combat against players and animals for a survivor holding only a melee
/// tool. Reuses the same damage pipeline as resource gathering (ServerUse plus
/// BaseMelee.DoAttackShared) retargeted at a living entity, and shares the same
/// _activeCombat/_activeCombatTarget tracking and EndCombat teardown as ranged combat.
/// </summary>
public partial class LivingRust
{
    // Melee engagement range: how close a survivor closes before swinging, used
    // both as StartFollowing's hold distance and the per-tick swing-eligibility gate.
    private const float MeleeEngagementRange = 0.5f;

    // Small forward position nudge applied on every swing. A pure rotation update
    // doesn't reliably broadcast to observing clients, but a position change does,
    // so this gives a visible lunge toward the target on each hit.
    private const float MeleeSwingStepNudgeDistance = 0.02f;

    // How long a melee fight can run before the survivor gives up and de-aggros,
    // applied to every melee fight regardless of how it started.
    private const float MeleeChaseGiveUpSeconds = 20f;

    /// <summary>
    /// Melee fight loop: same overall shape as the ranged combat tick
    /// (close distance, cooldown gate, disengage on low health, hand off to
    /// ranged combat if a gun turns up, EndCombat teardown), but using a simple
    /// range/cooldown check instead of the ranged burst/band system.
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
            // Guard against nothing being equipped to swing.
            return;
        }

        CancelActiveMovement(survivor);
        CancelActiveAttack(characterId);
        CancelActiveRecycling(characterId);
        StopExtendedOreSearch(characterId);

        TaskType previousTask = survivor.Character.CurrentTask;
        float combatStartedAt = Time.realtimeSinceStartup;

        VerbosePuts($"'{survivor.Character.Alias}' engaging '{GetAttackerDisplayName(attacker)}' in melee combat.");

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

            // Same safezone protection as the ranged combat tick.
            if (currentNpc.InSafeZone() || attacker.InSafeZone())
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - safezone.");
                EndCombat(characterId, meleeTimer, survivor, previousTask);
                return;
            }

            // Ranged weapon picked up mid-fight, so hand off to ranged combat.
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

            // Faces the target every tick, not just at swing time, since
            // StartFollowing is told to skip idle facing and combat owns it instead.
            AimAtPlayer(currentNpc, attacker);

            float distance = Vector3.Distance(currentNpc.transform.position, attacker.transform.position);

            if (distance > MeleeEngagementRange)
            {
                // Still closing; StartFollowing handles the actual movement.
                return;
            }

            if (currentNpc.GetHeldEntity() is not BaseMelee currentMelee || currentMelee.HasAttackCooldown())
            {
                // Weapon changed, or still on cooldown from the last swing.
                return;
            }

            // Needs to be looking at the target this tick for the hit to land.
            AimAtPlayer(currentNpc, attacker);

            // Small forward step on every swing for a visible lunge toward the target.
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

            // ServerUse() only plays the swing animation/sound; actual damage
            // comes from DoAttackShared below, so the native hit test is cancelled.
            currentMelee.ServerUse();
            currentMelee.CancelInvoke(currentMelee.ServerUse_Strike);

            HitInfo info = Pool.Get<HitInfo>();
            info.Init(currentNpc, attacker, DamageType.Generic, 0f, attacker.transform.position);
            info.Weapon = currentMelee;
            info.WeaponPrefab = currentMelee;
            info.PointStart = currentNpc.eyes.position;
            info.PointEnd = attacker.transform.position;

            // DoAttackShared runs the weapon's real damage/impact/cooldown pipeline,
            // the same one a real player's swing uses.
            currentMelee.DoAttackShared(info);

            Pool.Free(ref info);
        });

        _activeCombat[characterId] = meleeTimer;
    }
}
