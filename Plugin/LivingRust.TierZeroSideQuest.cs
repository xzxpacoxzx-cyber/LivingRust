using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Core;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Opportunistic Tier0-monument side quest (2026-10-03, Lucas's own explicit spec): a survivor
/// within 50m of an un-looted Tier0 monument while walking to an unrelated destination, or while
/// generally looting the area, detours to loot it, THEN resumes exactly what it was doing before.
/// Reuses the real monument-zone system (EscalateSearchToMonumentZone, same loot zones/rewards a
/// deliberate monument visit gets) against a throwaway LootTaskState, so the side quest can never
/// corrupt the real task's own CommittedMonumentName/VisitedMonumentZoneIndices/etc. - the real
/// state object is only ever read again once the side quest's own resume callback fires.
/// </summary>
public partial class LivingRust
{
    private const float TierZeroSideQuestRadius = 50f;

    // Per-life memory of which specific Tier0 monument instances a survivor has already
    // side-quested, keyed by monument name + position (MonumentInfo isn't a networked entity, so
    // there's no net.ID to key by - two monuments of the same type never share a position).
    // Reset on death, same "fresh chance next life" convention as every other once-per-life set.
    private readonly Dictionary<Guid, HashSet<(string Name, Vector3 Position)>> _sideQuestedTierZeroMonuments = new();

    // Reentrancy guard - a survivor can only be mid-side-quest once at a time.
    private readonly HashSet<Guid> _activeTierZeroSideQuest = new();

    private bool HasSideQuestedMonument(Guid characterId, MonumentInfo monument)
    {
        return _sideQuestedTierZeroMonuments.TryGetValue(characterId, out HashSet<(string, Vector3)> visited)
            && visited.Contains((monument.name, monument.transform.position));
    }

    private void MarkMonumentSideQuested(Guid characterId, MonumentInfo monument)
    {
        if (!_sideQuestedTierZeroMonuments.TryGetValue(characterId, out HashSet<(string, Vector3)> visited))
        {
            visited = new HashSet<(string, Vector3)>();
            _sideQuestedTierZeroMonuments[characterId] = visited;
        }

        visited.Add((monument.name, monument.transform.position));
    }

    private bool TryFindNearbySideQuestableTierZeroMonument(Vector3 position, Guid characterId, out MonumentInfo found)
    {
        found = null;
        float bestDistSqr = TierZeroSideQuestRadius * TierZeroSideQuestRadius;

        foreach (MonumentInfo monument in MonumentAccess.GetAllMonuments())
        {
            if (monument == null || GetMonumentTier(monument.name) != MonumentTier.TierZero
                || IsMonumentExcludedFromAutonomy(monument)
                || HasSideQuestedMonument(characterId, monument))
            {
                continue;
            }

            float distSqr = (monument.transform.position - position).sqrMagnitude;

            if (distSqr <= bestDistSqr)
            {
                bestDistSqr = distSqr;
                found = monument;
            }
        }

        return found != null;
    }

    /// <summary>
    /// Entry point, safe to call from anywhere in the loot-task loop. Does nothing (returns false)
    /// unless a genuinely un-sidequested Tier0 monument is within range and the survivor is free to
    /// detour (not mid-combat/flee/build/airdrop/already side-questing/already committed to some
    /// other real monument). On a hit, pauses whatever's calling it and resumes via resumeAction
    /// once the side quest concludes (success, early bail, or the monument turns out excluded).
    /// </summary>
    private bool TryPursueTierZeroMonumentSideQuest(Survivor survivor, BasePlayer npc, LootTaskState currentState, Action resumeAction)
    {
        Guid characterId = survivor.Character.Id;

        if (npc == null || npc.IsDestroyed || npc.IsWounded() || survivor.Character.State == CharacterState.Dead
            || _activeCombat.ContainsKey(characterId) || _activeFlee.ContainsKey(characterId)
            || IsBaseBuildInFlight(characterId) || IsAirdropParticipant(characterId)
            || _activeTierZeroSideQuest.Contains(characterId)
            || currentState.CommittedMonumentName != null)
        {
            return false;
        }

        if (!TryFindNearbySideQuestableTierZeroMonument(npc.transform.position, characterId, out MonumentInfo monument))
        {
            return false;
        }

        _activeTierZeroSideQuest.Add(characterId);
        MarkMonumentSideQuested(characterId, monument);

        Puts($"sidequest: '{survivor.Character.Alias}' is {Vector3.Distance(npc.transform.position, monument.transform.position):F0}m from '{monument.name}' (Tier0) - detouring to loot it before continuing.");

        CancelActiveMovement(survivor);

        LootTaskState sideQuestState = new()
        {
            OnMonumentZonesExhausted = () =>
            {
                _activeTierZeroSideQuest.Remove(characterId);
                Puts($"sidequest: '{survivor.Character.Alias}' finished its Tier0 side quest at '{monument.name}' - resuming what it was doing before.");
                resumeAction();
            },
        };

        EscalateSearchToMonumentZone(survivor, sideQuestState);
        return true;
    }
}
