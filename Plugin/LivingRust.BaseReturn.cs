using LivingRust.Core;
using System;
using System.Collections.Generic;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Scheduled return to base: a survivor with a base heads home on a flat cycle to
    // bank its loot, so it doesn't stay out in the open too long. The trip is
    // skipped and re-assessed later whenever the survivor is looting a monument, in
    // a fight, or building. Any trip home restarts the cycle.
    private const float BaseReturnCycleSeconds = 900f;
    private const float BaseReturnDeferSeconds = 300f;
    private const float BaseReturnCheckIntervalSeconds = 15f;
    private const float BaseReturnMonumentMargin = 15f;

    private readonly Dictionary<Guid, float> _nextBaseReturnTime = new();
    private Timer _baseReturnTimer;

    private void StartBaseReturnScheduler()
    {
        _baseReturnTimer?.Destroy();
        _baseReturnTimer = timer.Every(BaseReturnCheckIntervalSeconds, RunBaseReturnScheduler);

        _moveHealTimer?.Destroy();
        _moveHealTimer = timer.Every(MoveHealCheckIntervalSeconds, RunMoveHealCheck);
    }

    // Heal-while-moving: bandages and syringes get used mid-walk rather than only
    // at the next task callback, since long walks would otherwise leave a hurt
    // survivor walking hurt for many seconds. TryUseMedicalItemIfHurt never cancels
    // movement and no-ops when healthy/on cooldown, so polling it is cheap. Combat
    // has its own tactical heal and is skipped here.
    private const float MoveHealCheckIntervalSeconds = 2f;
    private Timer _moveHealTimer;

    private void RunMoveHealCheck()
    {
        if (_engine == null)
        {
            return;
        }

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || !npc.IsAlive() || npc.IsWounded()
                || survivor.Character.State == CharacterState.Dead
                || npc.health >= CombatHealHealthThreshold
                || _activeCombat.ContainsKey(survivor.Character.Id))
            {
                continue;
            }

            TryUseMedicalItemIfHurt(survivor);
        }
    }

    private void StopBaseReturnScheduler()
    {
        _baseReturnTimer?.Destroy();
        _baseReturnTimer = null;
        _moveHealTimer?.Destroy();
        _moveHealTimer = null;
    }

    private void MarkBaseVisit(Guid characterId)
    {
        _nextBaseReturnTime[characterId] = Time.realtimeSinceStartup + BaseReturnCycleSeconds;
    }

    private void RunBaseReturnScheduler()
    {
        if (_engine == null)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            Guid characterId = survivor.Character.Id;
            BasePlayer npc = survivor.Player;

            if (npc != null && !npc.IsDestroyed && survivor.Character.State != CharacterState.Dead && !npc.IsWounded()
                && TryHandleGearWindfall(survivor, npc))
            {
                continue;
            }

            if (survivor.Character.Home == null || npc == null || npc.IsDestroyed
                || survivor.Character.State == CharacterState.Dead || npc.IsWounded())
            {
                _nextBaseReturnTime.Remove(characterId);
                continue;
            }

            if (!_nextBaseReturnTime.TryGetValue(characterId, out float due))
            {
                _nextBaseReturnTime[characterId] = now + BaseReturnCycleSeconds;
                continue;
            }

            if (now < due)
            {
                continue;
            }

            if (IsBusyForBaseReturn(survivor, npc))
            {
                _nextBaseReturnTime[characterId] = now + BaseReturnDeferSeconds;
                continue;
            }

            MarkBaseVisit(characterId);

            Puts($"base-return: '{survivor.Character.Alias}' is heading back to base on its 15-minute cycle.");

            CancelActiveMovement(survivor);
            CancelActiveAttack(characterId);
            CancelActiveRecycling(characterId);
            StopExtendedOreSearch(characterId);

            GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor));
        }
    }

    // Covers looting a monument, mid-fight, and building, plus states already
    // committed to something that shouldn't be interrupted.
    private bool IsBusyForBaseReturn(Survivor survivor, BasePlayer npc)
    {
        Guid characterId = survivor.Character.Id;

        if (_activeCombat.ContainsKey(characterId)
            || _activeFlee.ContainsKey(characterId)
            || IsBaseBuildInFlight(characterId)
            || IsAirdropParticipant(characterId)
            || _activeHomeDoorCrossings.Contains(characterId)
            || _pendingRecyclerToResume.ContainsKey(characterId)
            || _pendingGhostRouteToResume.ContainsKey(characterId)
            || _activeRecycling.ContainsKey(characterId))
        {
            return true;
        }

        if (MonumentAccess.GetAllMonuments().Count > 0)
        {
            foreach (MonumentInfo monument in MonumentAccess.GetAllMonuments())
            {
                if (monument == null)
                {
                    continue;
                }

                Bounds expanded = monument.Bounds;
                expanded.Expand(BaseReturnMonumentMargin * 2f);

                if (new OBB(monument.transform, expanded).Contains(npc.transform.position))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Windfall: when a survivor's gear score jumps sharply, it is carrying
    // something worth protecting and stops to bank it. With a base it heads home and
    // deposits; without one it establishes a base right now and carries on. Only
    // fires out of combat and when the area looks safe.
    private const float GearWindfallFraction = 0.75f;
    private const int GearWindfallMinAbsolute = 10;
    private readonly Dictionary<Guid, int> _gearBaseline = new();

    private bool TryHandleGearWindfall(Survivor survivor, BasePlayer npc)
    {
        Guid characterId = survivor.Character.Id;

        if (_activeCombat.ContainsKey(characterId) || _activeFlee.ContainsKey(characterId))
        {
            return false;
        }

        int gear = GetGearScore(npc);

        if (!_gearBaseline.TryGetValue(characterId, out int baseline))
        {
            _gearBaseline[characterId] = gear;
            return false;
        }

        bool jumped = gear - baseline >= GearWindfallMinAbsolute && gear > baseline * (1f + GearWindfallFraction);

        if (!jumped)
        {
            _gearBaseline[characterId] = gear;
            return false;
        }

        if (IsBaseBuildInFlight(characterId))
        {
            return false;
        }

        // Safety judgement: don't turn for home with a crowd on top of us.
        int nearby = 0;

        foreach (Survivor other in _engine.SurvivorManager.GetAll())
        {
            if (other != survivor && other.Player != null && !other.Player.IsDestroyed
                && other.Character.State != CharacterState.Dead
                && Vector3.Distance(other.Player.transform.position, npc.transform.position) <= 30f)
            {
                nearby++;
            }
        }

        if (nearby >= 3)
        {
            return false;
        }

        _gearBaseline[characterId] = gear;
        Puts($"windfall: '{survivor.Character.Alias}' just jumped from gear {baseline} to {gear} - stopping everything to bank it.");

        EndAirdropParticipation(survivor, resume: false);
        CancelActiveMovement(survivor);
        CancelActiveAttack(characterId);
        CancelActiveRecycling(characterId);
        StopExtendedOreSearch(characterId);

        if (survivor.Character.Home != null)
        {
            MarkBaseVisit(characterId);
            GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor));
            return true;
        }

        // No base: go build one now. The deadline is set in the past so
        // TryPursueBaseGatherGoal treats any resource shortfall as already
        // topped-up rather than gathering.
        _pursuingPrimitiveGoals.Remove(characterId);
        _pursuingBaseGatherGoal.Add(characterId);
        _baseGatherDeadline[characterId] = Time.realtimeSinceStartup - 1f;
        StartLootForResourcesTask(survivor);
        return true;
    }
}
