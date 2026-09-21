using System;
using System.Collections.Generic;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Tactical decision system, Piece 3 (2026-08-24) - the actual scoring/
/// decision function scoped across a long design conversation earlier this
/// session, built on top of Piece 1 (TryFindCoverPoint, LivingRust.Combat.cs
/// + LivingRust.MonumentCoverPoints.cs) and Piece 2 (damage-dealt/health-
/// rate tracking, LivingRust.CombatDecisionTracking.cs).
///
/// Deliberately mirrors the loot-destination decision system exactly
/// (GetTierWeights/TryStartWithGearWeightedDestination, LivingRust.
/// GearScore.cs) rather than inventing a new selection mechanism, per
/// Lucas's own explicit request to reuse "a similar curved weighing scale."
/// Five candidate actions, each gets a continuous weight via the SAME real
/// triangular-falloff SHAPE GearScore.cs's own TierFalloffWeight uses
/// (peak near an "ideal" center, floored so nothing's ever truly
/// impossible) - a local float/per-call-radius sibling (TacticalFalloffWeight,
/// below) rather than the exact same function, since that one is typed
/// against an int gear score with one shared radius constant and these
/// inputs (damage/health/distance) sit on genuinely different natural
/// scales. Then one cumulative weighted roll picks the actual action - not
/// a top-N shortlist, not a hardcoded if/else tree. This is what makes the
/// result read as "deciding" rather than a deterministic script - two bots
/// in an identical situation can plausibly do different things.
///
/// Replaces the old standalone "heal below 70 health, unconditionally,
/// regardless of exposure" trigger entirely - Lucas's own explicit
/// correction: healing shouldn't be its own independent check, it should
/// be something that happens AS PART OF retreating to safety, not a reflex
/// that fires while standing fully exposed mid-exchange. RetreatToCover's
/// execution now triggers TryUseMedicalItemIfHurt itself (which still
/// self-gates on the same health threshold internally, so this composes
/// cleanly - no duplicate logic). Deliberately does NOT gate the walk on
/// reaching cover first - Lucas's own explicit call: real players heal
/// while moving constantly (only run-and-GUN is actually impossible,
/// matching the existing SprintFireSettleSeconds fire-lock), and the
/// only reason an earlier version required standing still was a cosmetic
/// animation-vs-movement conflict that's a non-issue in the context this
/// would ever actually happen in (a real player mid-chase, not calmly
/// inspecting a bot's syringe animation from a metre away).
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
    /// How often a fresh decision gets rolled - deliberately NOT every
    /// combat tick (0.05s), real decisions aren't reconsidered 20x/second.
    /// </summary>
    private const float TacticalDecisionReevaluationSeconds = 2.5f;

    private readonly Dictionary<Guid, float> _nextTacticalDecisionTime = new();

    // ============================================================
    // Weight curve tuning - first-pass numbers, not yet live-tested at
    // scale. Same "ship a reasonable start, tune from real trace evidence"
    // approach as every other constant in this project (cover distances,
    // structure-size thresholds, etc all started as guesses and got
    // corrected from live reports) - expect these to move.
    // ============================================================

    private const float TacticalPushDamageCenter = 60f;
    private const float TacticalPushPeakWeight = 55f;
    private const float TacticalPushFloorWeight = 8f;
    private const float TacticalPushFalloffRadius = 50f;
    private const float TacticalPushMaxUsefulDistance = 25f;

    /// <summary>
    /// How long ago the last landed hit still counts at full confidence -
    /// see LivingRust.CombatDecisionTracking.cs's own doc comment on
    /// GetDamageDealtToCurrentAttacker. A continuous point-blank exchange
    /// keeps this near 1.0 constantly; a real gap since the last hit
    /// (LOS lost, distance opened) discounts the tally, since the
    /// attacker may have had time to patch themselves up in that gap.
    /// </summary>
    private const float DamageConfidenceDecaySeconds = 4f;

    private const float TacticalHoldBaselineWeight = 52f;

    private const float TacticalRetreatHealthCenter = 65f;

    /// <summary>
    /// Lowered from 50 (2026-08-24, Lucas's own live report: cover-seeking
    /// is genuinely good but fires "a bit too often"). Combined with
    /// TacticalHoldBaselineWeight's own bump and the longer
    /// TacticalDecisionReevaluationSeconds - all three first-pass tunings
    /// from the same feedback, together meant to make Retreat a real but
    /// less dominant option relative to just holding the fight, not to
    /// remove it.
    /// </summary>
    private const float TacticalRetreatPeakWeight = 38f;

    private const float TacticalRetreatFloorWeight = 4f;
    private const float TacticalRetreatFalloffRadius = 45f;

    /// <summary>
    /// Health lost per second (over GetRecentHealthDropRate's own rolling
    /// window) that counts as a genuine "rapid burst" rather than an
    /// ordinary gradual fight - Lucas's own concrete example: 60-80%
    /// health lost within roughly 0.01-2 real seconds. ~70 points over
    /// ~2s lands around 35/s, so this sits comfortably below that as the
    /// trigger point without firing on an ordinary slower exchange.
    /// </summary>
    private const float TacticalRapidHealthLossRate = 25f;

    /// <summary>
    /// A genuine rapid burst boosts BOTH retreat-to-cover AND push at
    /// once - Lucas's own explicit fork: "dodge and weave... OR just try
    /// 'I'm most likely going to die here, let's at least go down
    /// trying'" - both need to become real, live options simultaneously
    /// so the weighted roll can plausibly pick either, not one
    /// deterministic reaction to the same trigger.
    /// </summary>
    private const float TacticalRapidBurstRetreatMultiplier = 1.8f;

    private const float TacticalRapidBurstPushMultiplier = 1.5f;

    private const float TacticalFlankDistanceCenter = 15f;
    private const float TacticalFlankPeakWeight = 20f;
    private const float TacticalFlankFloorWeight = 4f;
    private const float TacticalFlankFalloffRadius = 20f;

    /// <summary>
    /// How far away from the attacker's current position a fallback
    /// retreat point sits when TryFindCoverPoint genuinely finds nothing -
    /// Lucas's own explicit call: "it isn't a be all and end all if it
    /// can't find cover... it just needs to use them in the right
    /// situations" - not finding formal cover shouldn't block retreating/
    /// healing outright, just means the destination is a plain
    /// put-some-distance-between-us point instead of a real blocked-LOS
    /// one.
    /// </summary>
    private const float TacticalFallbackRetreatDistance = 10f;

    private const float TacticalFlankStepDistance = 10f;

    private const float TacticalFlankAngleDegrees = 70f;

    /// <summary>
    /// Entry point, called once per combat tick from the main loop
    /// (LivingRust.Combat.cs) - internally self-gates on
    /// TacticalDecisionReevaluationSeconds, so most calls are simply a
    /// dictionary lookup that returns false immediately. Returns true only
    /// when a NEW non-default action (retreat/flank) was just started this
    /// call, telling the caller to skip the rest of this tick - Push/Hold
    /// always return false, since they're the existing default combat
    /// behaviour and don't need to interrupt anything.
    /// </summary>
    private bool TryStartTacticalRepositioning(Survivor survivor, BasePlayer npc, BaseCombatEntity attacker)
    {
        Guid characterId = survivor.Character.Id;

        if (_nextTacticalDecisionTime.TryGetValue(characterId, out float nextDecisionTime) && Time.realtimeSinceStartup < nextDecisionTime)
        {
            return false;
        }

        _nextTacticalDecisionTime[characterId] = Time.realtimeSinceStartup + TacticalDecisionReevaluationSeconds;

        // Last-ditch widening (2026-08-24, Lucas's own explicit request):
        // below CombatFleeHealthThreshold - the same "too low to keep
        // fighting normally" line the fixed disengage check already uses -
        // the normal "10-20m, roughly in front" cover restriction stops
        // mattering. At that point it's a last-ditch effort to stay alive,
        // not a considered tactical repositioning - any real cover within
        // CoverMaxUsefulDistance in ANY direction beats no cover at all.
        // See TryFindCoverPoint's own doc comment for exactly what this
        // relaxes (the direction/angle filter only - the distance cap and
        // every real geometry/ground validation still apply unchanged).
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
                // The existing default combat behaviour already handles
                // this (StartFollowing toward hold distance / holding
                // position) - nothing new to do, let the rest of this
                // tick run exactly as it always has.
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
    /// Local sibling of GearScore.cs's own TierFalloffWeight - same real
    /// triangular-falloff shape (peak at center, tapering to a nonzero
    /// floor over a given radius), but float-valued with its own per-call
    /// radius instead of an int gear-score and one shared constant radius,
    /// since damage/health/distance here all sit on genuinely different
    /// natural scales and each needs its own tuned reach.
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
            // A real player doesn't "push" from 25m+ out - that's still
            // just holding/exchanging fire at range, not closing in.
            push *= 0.3f;
        }

        float hold = TacticalHoldBaselineWeight;

        float healthUrgency = 100f - npc.health;
        float retreatToCover = TacticalFalloffWeight(healthUrgency, TacticalRetreatHealthCenter, TacticalRetreatPeakWeight, TacticalRetreatFloorWeight, TacticalRetreatFalloffRadius);

        float healthDropRate = GetRecentHealthDropRate(characterId);

        if (healthDropRate >= TacticalRapidHealthLossRate)
        {
            // Rapid burst - see TacticalRapidBurstRetreatMultiplier's own
            // doc comment for why this boosts BOTH options rather than
            // picking one deterministically.
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
            // No formal cover found - still worth putting real distance
            // between them rather than doing nothing, see
            // TacticalFallbackRetreatDistance's own doc comment. Ground-
            // snapped the same way TryFindCoverPoint's own live fan search
            // validates its candidates (2026-08-24, live report: bots
            // "semi stuck out in the open" mid-heal - a raw
            // position-plus-offset point with no ground/overlap check can
            // land somewhere the real pathing system then can't reach at
            // all, same class of failure the walk-progress-diag/NoPath
            // lines already show constantly for ordinary loot movement).
            // Falls back to the raw offset if no real ground is found
            // nearby at all - still better than not moving.
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

        // StartWalkingWithRecovery (not a bare StartWalking) - same
        // escalating stuck-recovery every other exclusive movement task in
        // this project already uses (loot/recycle/puzzle walks). onFailed
        // releases the tactical-action lock immediately rather than leaving
        // the bot visibly frozen mid-heal for the rest of
        // TacticalActionDurationSeconds if the destination genuinely can't
        // be reached - the very next tick gets a fresh reroll instead.
        StartWalkingWithRecovery(survivor, destination, onArrived: null, onFailed: null);

        // Deliberately concurrent, not gated on arrival - see this file's
        // own top-of-file doc comment for why healing-while-moving is now
        // the intended behaviour, not a sequential "arrive then heal."
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

        // See StartTacticalRetreat's own doc comment on StartWalkingWithRecovery -
        // same reasoning applies here (a raw lateral offset is just as
        // capable of landing somewhere unreachable).
        StartWalkingWithRecovery(survivor, destination, onArrived: null, onFailed: null);
    }

    private void ClearTacticalDecisionState(Guid characterId)
    {
        _nextTacticalDecisionTime.Remove(characterId);
    }
}
