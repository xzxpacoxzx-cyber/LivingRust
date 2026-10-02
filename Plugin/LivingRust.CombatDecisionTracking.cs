using System;
using System.Collections.Generic;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Data collection for tactical combat decisions, feeding a future scoring/decision
/// function. Tracks (1) damage dealt to the survivor's current combat target, using
/// only information a real player would have, and (2) a rolling window of the
/// survivor's own recent health-over-time, to distinguish a rapid burst from a
/// gradual bleed-down at the same absolute health value.
/// </summary>
public partial class LivingRust
{
    // ============================================================
    // (1) Damage dealt to current attacker
    // ============================================================

    private readonly Dictionary<Guid, float> _damageDealtToCurrentAttacker = new();

    /// <summary>
    /// Tracks when the last hit actually landed, so a future confidence-weighting
    /// refinement (discounting stale hits where the attacker may have healed) can use
    /// this raw timestamp without needing another pass through this file.
    /// </summary>
    private readonly Dictionary<Guid, float> _lastDamageDealtToAttackerTime = new();

    private void RecordDamageDealtToCurrentAttacker(Survivor attackerSurvivor, BasePlayer victim, float damage)
    {
        Guid characterId = attackerSurvivor.Character.Id;

        if (!_activeCombatTarget.TryGetValue(characterId, out BaseCombatEntity currentTarget) || currentTarget != victim)
        {
            // Ignores hits on anything other than the survivor's official current
            // combat target.
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
    /// Returns the raw damage tally and how long ago the last hit landed. Returns
    /// (0, infinity) if nothing has been recorded yet for this engagement.
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
    /// How far back the rolling health-sample window reaches, covering both a rapid
    /// burst and a gradual bleed with some headroom either side.
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
    /// Returns health points lost per second across the current rolling window. Never
    /// negative; a net gain (e.g. a heal mid-window) reads as 0 drop.
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
