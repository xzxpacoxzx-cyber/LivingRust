using System;
using System.Collections.Generic;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Scheduled return to base (2026-09-21, Lucas's own explicit spec): a
    // survivor with a base heads home on a flat 20-minute cycle to bank its
    // loot, so it doesn't spend ages out in the open as a walking pinata.
    // The trip is skipped (and re-assessed 5 minutes later) whenever the
    // survivor is inside a monument looting, in a fight, or building. Any
    // real trip home (full inventory, post-recycling, this one) restarts the
    // 20 minutes.
    private const float BaseReturnCycleSeconds = 1200f;
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

    // Heal-while-moving (2026-09-21, Lucas's own explicit spec): bandages and
    // syringes get used mid-walk, not only at the next task callback. Long
    // walks (airdrop journeys, base returns, monument approaches) otherwise
    // go many seconds between ContinueLootTask calls, so a hurt survivor
    // would keep walking hurt. TryUseMedicalItemIfHurt never cancels movement
    // and no-ops when healthy/on cooldown/already mid-chain, so polling it is
    // cheap. Combat has its own tactical heal, skipped here.
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

            Puts($"base-return: '{survivor.Character.Alias}' is heading back to base on its 20-minute cycle.");

            CancelActiveMovement(survivor);
            CancelActiveAttack(characterId);
            CancelActiveRecycling(characterId);
            StopExtendedOreSearch(characterId);

            GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor));
        }
    }

    // The three cases the spec names (inside a monument looting, mid-fight,
    // building) plus the states that are already about home or already
    // committed to something that shouldn't be yanked away.
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

        if (TerrainMeta.Path?.Monuments != null)
        {
            foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
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

    // Windfall (2026-09-21, Lucas's own explicit spec): when a survivor's gear
    // score jumps by more than 75% (a bow user killing someone with a
    // firearm, say) it is "rich" and carrying something worth protecting -
    // hard stop, go bank it. With a base it heads home and deposits; without
    // one it goes to establish a base right now (resources topped up at the
    // site) and then carries on. Only ever fires out of combat and when the
    // area looks safe; until then the baseline is left alone so the jump is
    // still detected on a later tick.
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

        // No base: skip whatever checklist is left and go build one now. The
        // deadline is set in the past so TryPursueBaseGatherGoal treats any
        // resource shortfall as topped-up-at-the-site instead of gathering.
        _pursuingPrimitiveGoals.Remove(characterId);
        _pursuingBaseGatherGoal.Add(characterId);
        _baseGatherDeadline[characterId] = Time.realtimeSinceStartup - 1f;
        StartLootForResourcesTask(survivor);
        return true;
    }
}
