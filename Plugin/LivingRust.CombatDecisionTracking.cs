using System;
using System.Collections.Generic;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Tactical decision-system foundation, Piece 2 (2026-08-24) - real data
/// collection only, no decision-making yet (that's Piece 3, still not
/// built). Two independent trackers, both scoped from the same design
/// conversation that produced TryFindCoverPoint (LivingRust.Combat.cs) and
/// LivingRust.MonumentCoverPoints.cs:
///
/// (1) Damage dealt to the survivor's own CURRENT combat target this
/// engagement - deliberately NOT the attacker's real hidden health (a real
/// player can't see that), but the bot's own legitimately-known combat log
/// (exactly what a real player's own in-game combat log shows them),
/// weighed conceptually against the target's VISIBLE armor/weapon at
/// decision time - fair information only, matching the same principle
/// already applied to the heal-animation-timing fairness fix earlier this
/// project. Confirmed via Lucas's own knowledge of the real game: Rust's
/// server combat log genuinely does show damage dealt/taken in/after a
/// fight, console-accessible without admin privileges, so this is parity
/// with a real player's own information, not an approximation.
///
/// (2) A rolling window of the survivor's own recent health-over-time, to
/// distinguish a RAPID burst (should react immediately/aggressively) from
/// a GRADUAL bleed-down (allows a more considered choice) at the same
/// absolute health value - the roadmap's own standalone "health-drop-RATE"
/// idea, dated 2026-08-23.
///
/// Both are pure data collection - Piece 3 (the actual scoring/decision
/// function, still not built) is what will actually READ these to make a
/// choice. Building them now, separately, means Piece 3 can be built and
/// tested against real, already-verified data instead of guessing at both
/// the data AND the decision logic at once.
/// </summary>
public partial class LivingRust
{
    // ============================================================
    // (1) Damage dealt to current attacker
    // ============================================================

    private readonly Dictionary<Guid, float> _damageDealtToCurrentAttacker = new();

    /// <summary>
    /// When the last hit actually landed - not yet used for the
    /// "confidence-weighted by contact continuity" refinement Lucas asked
    /// for (a continuous exchange should be trusted fully, a fight with a
    /// real gap since the last landed hit should be discounted, since the
    /// attacker may have had time to heal) - that logic belongs in Piece 3
    /// once it exists, this just makes the raw timestamp available for it
    /// to use rather than needing a THIRD pass through this file later.
    /// </summary>
    private readonly Dictionary<Guid, float> _lastDamageDealtToAttackerTime = new();

    private void RecordDamageDealtToCurrentAttacker(Survivor attackerSurvivor, BasePlayer victim, float damage)
    {
        Guid characterId = attackerSurvivor.Character.Id;

        if (!_activeCombatTarget.TryGetValue(characterId, out BaseCombatEntity currentTarget) || currentTarget != victim)
        {
            // Landed a hit on something other than the survivor's own
            // OFFICIAL current combat target (a stray hit on a bystander,
            // or the target reference hasn't been set up yet this tick) -
            // doesn't count toward "how much have I hurt the thing I'm
            // actually fighting."
            return;
        }

        _damageDealtToCurrentAttacker[characterId] = _damageDealtToCurrentAttacker.GetValueOrDefault(characterId) + damage;
        _lastDamageDealtToAttackerTime[characterId] = Time.realtimeSinceStartup;
    }

    private void ResetDamageDealtToCurrentAttacker(Guid characterId)
    {
        _damageDealtToCurrentAttacker.Remove(characterId);
        _lastDamageDealtToAttackerTime.Remove(characterId);
    }

    /// <summary>
    /// Raw tally + how long ago the last hit landed - Piece 3's own job to
    /// decide what to actually DO with staleness (discount/decay it), not
    /// this file's. Returns (0, has never hit anything) if nothing's been
    /// recorded yet for this engagement.
    /// </summary>
    private (float TotalDamage, float SecondsSinceLastHit) GetDamageDealtToCurrentAttacker(Guid characterId)
    {
        float total = _damageDealtToCurrentAttacker.GetValueOrDefault(characterId);
        float secondsSinceLastHit = _lastDamageDealtToAttackerTime.TryGetValue(characterId, out float lastHitTime)
            ? Time.realtimeSinceStartup - lastHitTime
            : float.PositiveInfinity;

        return (total, secondsSinceLastHit);
    }

    // ============================================================
    // (2) Rolling recent health-over-time (for drop-RATE, not just the
    // instantaneous value)
    // ============================================================

    /// <summary>
    /// How far back the rolling health-sample window reaches - matches the
    /// roadmap's own "1-2 seconds" (rapid burst) vs "3-5+ seconds"
    /// (gradual bleed) framing with a bit of headroom either side.
    /// </summary>
    private const float HealthRateSampleWindowSeconds = 6f;

    private readonly Dictionary<Guid, List<(float Time, float Health)>> _recentHealthSamples = new();

    private void RecordHealthSample(Guid characterId, float health)
    {
        if (!_recentHealthSamples.TryGetValue(characterId, out List<(float Time, float Health)> samples))
        {
            samples = new List<(float Time, float Health)>();
            _recentHealthSamples[characterId] = samples;
        }

        samples.Add((Time.realtimeSinceStartup, health));
        samples.RemoveAll(sample => Time.realtimeSinceStartup - sample.Time > HealthRateSampleWindowSeconds);
    }

    /// <summary>
    /// Health points lost per second across whatever's currently in the
    /// rolling window (never negative - a net gain, e.g. a heal landing
    /// mid-window, reads as 0 "drop" rather than a nonsensical negative
    /// rate). Piece 3's own job to decide what threshold counts as
    /// "rapid" vs "gradual" - this just reports the real number.
    /// </summary>
    private float GetRecentHealthDropRate(Guid characterId)
    {
        if (!_recentHealthSamples.TryGetValue(characterId, out List<(float Time, float Health)> samples) || samples.Count < 2)
        {
            return 0f;
        }

        (float Time, float Health) oldest = samples[0];
        (float Time, float Health) newest = samples[^1];
        float elapsed = newest.Time - oldest.Time;

        if (elapsed < 0.05f)
        {
            return 0f;
        }

        return Mathf.Max(0f, (oldest.Health - newest.Health) / elapsed);
    }

    private void ClearCombatDecisionTracking(Guid characterId)
    {
        _damageDealtToCurrentAttacker.Remove(characterId);
        _lastDamageDealtToAttackerTime.Remove(characterId);
        _recentHealthSamples.Remove(characterId);
    }
}
