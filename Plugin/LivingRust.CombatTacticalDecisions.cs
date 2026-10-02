using System;
using System.Collections.Generic;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Tactical combat decision system, built on top of cover-point finding and
/// damage-dealt/health-rate tracking. Mirrors the loot-destination decision
/// system's weighted-selection approach: five candidate actions each get a
/// continuous weight via a triangular-falloff shape, and one cumulative weighted
/// roll picks the action, so identical situations can plausibly play out
/// differently. Healing is triggered as part of retreating to safety rather than
/// as an independent check, and happens concurrently with the retreat walk rather
/// than only after arriving.
/// </summary>
public partial class LivingRust
{
    private enum CombatTacticalAction
    {
        Push,
        Hold,
        RetreatToCover,
        FlankLeft,
        FlankRight,
    }

    /// <summary>
    /// How often a fresh decision gets rolled, deliberately less often than every
    /// combat tick.
    /// </summary>
    private const float TacticalDecisionReevaluationSeconds = 2.5f;

    private readonly Dictionary<Guid, float> _nextTacticalDecisionTime = new();

    // ============================================================
    // Weight curve tuning constants, first-pass numbers expected to be adjusted
    // as they get tested at scale.
    // ============================================================

    private const float TacticalPushDamageCenter = 60f;
    private const float TacticalPushPeakWeight = 55f;
    private const float TacticalPushFloorWeight = 8f;
    private const float TacticalPushFalloffRadius = 50f;
    private const float TacticalPushMaxUsefulDistance = 25f;

    /// <summary>
    /// How long ago the last landed hit still counts at full confidence. A
    /// continuous exchange keeps this near 1.0; a gap since the last hit discounts
    /// the tally, since the attacker may have had time to heal.
    /// </summary>
    private const float DamageConfidenceDecaySeconds = 4f;

    private const float TacticalHoldBaselineWeight = 52f;

    private const float TacticalRetreatHealthCenter = 65f;

    /// <summary>
    /// Tuned together with TacticalHoldBaselineWeight and
    /// TacticalDecisionReevaluationSeconds to make Retreat a real but less
    /// dominant option relative to holding the fight.
    /// </summary>
    private const float TacticalRetreatPeakWeight = 38f;

    private const float TacticalRetreatFloorWeight = 4f;
    private const float TacticalRetreatFalloffRadius = 45f;

    /// <summary>
    /// Health lost per second (over GetRecentHealthDropRate's rolling window) that
    /// counts as a genuine rapid burst rather than an ordinary gradual fight.
    /// </summary>
    private const float TacticalRapidHealthLossRate = 25f;

    /// <summary>
    /// A genuine rapid burst boosts both retreat-to-cover and push at once, so the
    /// weighted roll can plausibly pick either rather than one deterministic
    /// reaction.
    /// </summary>
    private const float TacticalRapidBurstRetreatMultiplier = 1.8f;

    private const float TacticalRapidBurstPushMultiplier = 1.5f;

    private const float TacticalFlankDistanceCenter = 15f;
    private const float TacticalFlankPeakWeight = 20f;
    private const float TacticalFlankFloorWeight = 4f;
    private const float TacticalFlankFalloffRadius = 20f;

    /// <summary>
    /// How far away from the attacker's current position a fallback retreat point
    /// sits when TryFindCoverPoint finds nothing, so retreating/healing isn't
    /// blocked outright by the lack of formal cover.
    /// </summary>
    private const float TacticalFallbackRetreatDistance = 10f;

    private const float TacticalFlankStepDistance = 10f;

    private const float TacticalFlankAngleDegrees = 70f;

    /// <summary>
    /// Entry point, called once per combat tick. Self-gates on
    /// TacticalDecisionReevaluationSeconds, so most calls are just a dictionary
    /// lookup. Returns true only when a new non-default action (retreat/flank) was
    /// just started, telling the caller to skip the rest of this tick.
    /// </summary>
    private bool TryStartTacticalRepositioning(Survivor survivor, BasePlayer npc, BaseCombatEntity attacker)
    {
        Guid characterId = survivor.Character.Id;

        if (_nextTacticalDecisionTime.TryGetValue(characterId, out float nextDecisionTime) && Time.realtimeSinceStartup < nextDecisionTime)
        {
            return false;
        }

        _nextTacticalDecisionTime[characterId] = Time.realtimeSinceStartup + TacticalDecisionReevaluationSeconds;

        // Below CombatFleeHealthThreshold, the normal direction/angle cover
        // restriction is relaxed as a last-ditch effort to stay alive: any real
        // cover within CoverMaxUsefulDistance beats no cover at all. Distance cap
        // and geometry/ground validation still apply unchanged.
        bool desperate = npc.health <= CombatFleeHealthThreshold;
        bool hasCover = TryFindCoverPoint(npc, attacker, out Vector3 coverPoint, desperate);

        (float push, float hold, float retreatToCover, float flankLeft, float flankRight) = GetCombatTacticalActionWeights(survivor, npc, attacker);

        float total = push + hold + retreatToCover + flankLeft + flankRight;

        if (total <= 0f)
        {
            return false;
        }

        float roll = UnityEngine.Random.Range(0f, total);
        CombatTacticalAction chosen;

        if (roll < push)
        {
            chosen = CombatTacticalAction.Push;
        }
        else if ((roll -= push) < hold)
        {
            chosen = CombatTacticalAction.Hold;
        }
        else if ((roll -= hold) < retreatToCover)
        {
            chosen = CombatTacticalAction.RetreatToCover;
        }
        else if ((roll -= retreatToCover) < flankLeft)
        {
            chosen = CombatTacticalAction.FlankLeft;
        }
        else
        {
            chosen = CombatTacticalAction.FlankRight;
        }

        switch (chosen)
        {
            case CombatTacticalAction.Push:
            case CombatTacticalAction.Hold:
                // The existing default combat behaviour already handles this;
                // nothing new to do here.
                return false;

            case CombatTacticalAction.RetreatToCover:
                StartTacticalRetreat(survivor, npc, attacker, hasCover, coverPoint);
                return true;

            case CombatTacticalAction.FlankLeft:
                StartTacticalFlank(survivor, npc, attacker, isLeft: true);
                return true;

            default:
                StartTacticalFlank(survivor, npc, attacker, isLeft: false);
                return true;
        }
    }

    /// <summary>
    /// Triangular-falloff weight curve: peaks at center and tapers to a nonzero
    /// floor over the given radius. A float-valued sibling of GearScore.cs's
    /// TierFalloffWeight, since damage/health/distance sit on different scales.
    /// </summary>
    private static float TacticalFalloffWeight(float value, float center, float peak, float floor, float radius)
    {
        float distance = Mathf.Abs(value - center);
        float falloff = Mathf.Clamp01(1f - distance / radius);

        return floor + (peak - floor) * falloff;
    }

    private (float Push, float Hold, float RetreatToCover, float FlankLeft, float FlankRight) GetCombatTacticalActionWeights(Survivor survivor, BasePlayer npc, BaseCombatEntity attacker)
    {
        Guid characterId = survivor.Character.Id;

        float distance = Vector3.Distance(npc.transform.position, attacker.transform.position);

        (float damageDealt, float secondsSinceLastHit) = GetDamageDealtToCurrentAttacker(characterId);
        float damageConfidence = float.IsInfinity(secondsSinceLastHit) ? 0f : Mathf.Clamp01(1f - secondsSinceLastHit / DamageConfidenceDecaySeconds);
        float effectiveDamageDealt = damageDealt * damageConfidence;

        float push = TacticalFalloffWeight(effectiveDamageDealt, TacticalPushDamageCenter, TacticalPushPeakWeight, TacticalPushFloorWeight, TacticalPushFalloffRadius);

        if (distance > TacticalPushMaxUsefulDistance)
        {
            // Beyond this distance, pushing reads as holding/exchanging fire at
            // range rather than closing in.
            push *= 0.3f;
        }

        float hold = TacticalHoldBaselineWeight;

        float healthUrgency = 100f - npc.health;
        float retreatToCover = TacticalFalloffWeight(healthUrgency, TacticalRetreatHealthCenter, TacticalRetreatPeakWeight, TacticalRetreatFloorWeight, TacticalRetreatFalloffRadius);

        float healthDropRate = GetRecentHealthDropRate(characterId);

        if (healthDropRate >= TacticalRapidHealthLossRate)
        {
            // Rapid burst boosts both options rather than picking one
            // deterministically.
            retreatToCover *= TacticalRapidBurstRetreatMultiplier;
            push *= TacticalRapidBurstPushMultiplier;
        }

        float flankBase = TacticalFalloffWeight(distance, TacticalFlankDistanceCenter, TacticalFlankPeakWeight, TacticalFlankFloorWeight, TacticalFlankFalloffRadius);
        float flankLeft = flankBase * UnityEngine.Random.Range(0.7f, 1.3f);
        float flankRight = flankBase * UnityEngine.Random.Range(0.7f, 1.3f);

        return (push, hold, retreatToCover, flankLeft, flankRight);
    }

    private void StartTacticalRetreat(Survivor survivor, BasePlayer npc, BaseCombatEntity attacker, bool hasCover, Vector3 coverPoint)
    {
        Guid characterId = survivor.Character.Id;

        Vector3 destination;

        if (hasCover)
        {
            destination = coverPoint;
            VerbosePuts($"'{survivor.Character.Alias}' is retreating to cover from '{GetAttackerDisplayName(attacker)}'.");
        }
        else
        {
            // No formal cover found; still worth putting distance between them
            // rather than doing nothing. Ground-snapped like TryFindCoverPoint's
            // own candidate validation, to avoid landing somewhere unreachable.
            // Falls back to the raw offset if no ground is found nearby.
            Vector3 awayFromAttacker = npc.transform.position - attacker.transform.position;
            awayFromAttacker.y = 0f;

            if (awayFromAttacker.sqrMagnitude < 0.01f)
            {
                awayFromAttacker = npc.transform.forward;
                awayFromAttacker.y = 0f;
            }

            awayFromAttacker.Normalize();
            Vector3 rawDestination = npc.transform.position + awayFromAttacker * TacticalFallbackRetreatDistance;

            if (_engine.NavigationManager.TryFindGroundBelow(rawDestination + Vector3.up * 4f, 4f, 8f, out float groundY, out _))
            {
                destination = new Vector3(rawDestination.x, groundY, rawDestination.z);
            }
            else
            {
                destination = rawDestination;
            }

            VerbosePuts($"'{survivor.Character.Alias}' is falling back from '{GetAttackerDisplayName(attacker)}' - no real cover found nearby.");
        }

        // Uses StartWalkingWithRecovery, the same escalating stuck-recovery every
        // other exclusive movement task in this project uses.
        StartWalkingWithRecovery(survivor, destination, onArrived: null, onFailed: null);

        // Runs concurrently rather than gated on arrival, since healing-while-moving
        // is the intended behaviour.
        TryUseMedicalItemIfHurt(survivor);
    }

    private void StartTacticalFlank(Survivor survivor, BasePlayer npc, BaseCombatEntity attacker, bool isLeft)
    {
        Guid characterId = survivor.Character.Id;

        Vector3 towardAttacker = attacker.transform.position - npc.transform.position;
        towardAttacker.y = 0f;

        if (towardAttacker.sqrMagnitude < 0.01f)
        {
            towardAttacker = npc.transform.forward;
            towardAttacker.y = 0f;
        }

        towardAttacker.Normalize();

        float angle = isLeft ? -TacticalFlankAngleDegrees : TacticalFlankAngleDegrees;
        Vector3 flankDirection = Quaternion.Euler(0f, angle, 0f) * towardAttacker;
        Vector3 destination = npc.transform.position + flankDirection * TacticalFlankStepDistance;

        VerbosePuts($"'{survivor.Character.Alias}' is flanking {(isLeft ? "left" : "right")} around '{GetAttackerDisplayName(attacker)}'.");

        // Same StartWalkingWithRecovery reasoning as StartTacticalRetreat applies
        // here too.
        StartWalkingWithRecovery(survivor, destination, onArrived: null, onFailed: null);
    }

    private void ClearTacticalDecisionState(Guid characterId)
    {
        _nextTacticalDecisionTime.Remove(characterId);
    }
}
