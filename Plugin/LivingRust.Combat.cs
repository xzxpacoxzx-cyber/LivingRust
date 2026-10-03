using System;
using System.Collections.Generic;
using System.Linq;
using Facepunch;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Handles ranged combat for survivors against players, scientists, and animals. Covers on-sight and reactive target detection, aiming, firing, and reloading through Rust's real inventory and weapon systems.
/// </summary>
public partial class LivingRust
{
    private readonly Dictionary<Guid, Timer> _activeCombat = new();

    // Tracks what each survivor is currently fighting and how far away, for trace/debug logging.
    private readonly Dictionary<Guid, BaseCombatEntity> _activeCombatTarget = new();

    // Tracks the player a survivor was fighting before an animal interrupted combat, so the player fight can resume once the animal fight ends.
    private readonly Dictionary<Guid, BaseCombatEntity> _pendingResumeCombatTarget = new();

    // Tracks active flee timers for animal encounters. Animals and players share BaseCombatEntity as a common base, so the same combat, aiming, and line-of-sight logic works against both.
    private readonly Dictionary<Guid, Timer> _activeFlee = new();

    // Detection range for spotting a nearby animal on sight, separate from the longer player on-sight range.
    private const float AnimalOnSightRange = 10f;

    /// <summary>
    /// Returns whether an entity is an animal this project treats as a valid combat target. Stag is included but only ever engaged via on-sight (it never attacks first); chickens and other passive animals are excluded.
    /// </summary>
    private static bool IsHuntablePredator(BaseEntity entity)
    {
        return entity is Rust.Ai.Gen2.Bear
            || entity is Rust.Ai.Gen2.PolarBear
            || entity is Rust.Ai.Gen2.Boar
            || entity is Rust.Ai.Gen2.Stag
            || entity is Rust.Ai.Gen2.Wolf2
            || entity is Rust.Ai.Gen2.Crocodile
            || entity is Rust.Ai.Gen2.Panther
            || entity is Rust.Ai.Gen2.Tiger;
    }

    // Detection range for spotting a hostile scientist NPC on sight.
    private const float ScientistOnSightRange = 10f;

    /// <summary>
    /// Prefab name prefixes for scientist NPCs that are not actually hostile (e.g. arena referees, peacekeepers) and should be excluded from combat targeting.
    /// </summary>
    private static readonly string[] PassiveScientistPrefabPrefixes =
    {
        "scientistnpc_arena",
        "scientistnpc_peacekeeper",
    };

    private static bool IsHostileScientist(BaseEntity entity)
    {
        if (entity is not (ScientistNPC or Rust.Ai.Gen2.ScientistNPC2))
        {
            return false;
        }

        string prefab = entity.ShortPrefabName;

        foreach (string passivePrefix in PassiveScientistPrefabPrefixes)
        {
            if (prefab.StartsWith(passivePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Shared predicate for reactive combat: true if the entity is a huntable animal or a hostile scientist.
    /// </summary>
    private static bool IsHuntableThreat(BaseEntity entity)
    {
        return IsHuntablePredator(entity) || IsHostileScientist(entity);
    }

    /// <summary>
    /// Distance at which a survivor breaks off and flees from a nearby BradleyAPC, since its weapons are lethal enough to warrant an early, wide detection radius.
    /// </summary>
    private const float BradleyDangerRadius = 100f;

    /// <summary>
    /// Finds the nearest living BradleyAPC within range, reusing the standard entity-scan helper.
    /// </summary>
    private static bool TryFindNearbyBradley(Vector3 position, out BradleyAPC bradley)
    {
        List<BaseCombatEntity> candidates = Pool.Get<List<BaseCombatEntity>>();
        bradley = null;

        try
        {
            CollectEntitiesInRange<BradleyAPC>(position, BradleyDangerRadius, candidates);

            foreach (BaseCombatEntity candidate in candidates)
            {
                if (candidate is BradleyAPC candidateBradley && !candidateBradley.IsDestroyed && candidateBradley.IsAlive())
                {
                    bradley = candidateBradley;
                    return true;
                }
            }
        }
        finally
        {
            Pool.FreeUnmanaged(ref candidates);
        }

        return false;
    }

    /// <summary>
    /// Determines whether a dangerous animal (all huntable predators except Stag) should be avoided rather than fought: true only when the survivor has neither a ready ranged weapon nor a real melee weapon (hatchet-tier or better). A melee-capable survivor is expected to fight rather than steer around it.
    /// </summary>
    private bool IsHardAvoidAnimal(BaseEntity entity, BasePlayer npc)
    {
        if (entity is Rust.Ai.Gen2.Bear || entity is Rust.Ai.Gen2.Crocodile || entity is Rust.Ai.Gen2.Boar || entity is Rust.Ai.Gen2.Wolf2
            || entity is Rust.Ai.Gen2.Panther || entity is Rust.Ai.Gen2.Tiger || entity is Rust.Ai.Gen2.PolarBear)
        {
            return !HasReadyRangedWeapon(npc) && !HasCombatReadyMeleeWeapon(npc);
        }

        return false;
    }

    /// <summary>
    /// Chance a survivor with nothing but a rock (or bone knife) fights back against a hard-avoid animal instead of fleeing, rolled once per encounter. Always fleeing still gets a survivor run down and bitten about as often as fighting would, so this is a genuine fight-or-flight choice rather than a guaranteed loss either way.
    /// </summary>
    private const float UnarmedAnimalFightChance = 0.25f;

    // Detection radius for hard-avoid animals, wider than AnimalOnSightRange so there is room to steer around them during movement.
    private const float HardAvoidAnimalRadius = 50f;

    private bool TryFindNearbyHardAvoidAnimal(BasePlayer npc, out BaseEntity animal)
    {
        List<BaseCombatEntity> candidates = Pool.Get<List<BaseCombatEntity>>();
        animal = null;
        float nearestDistanceSqr = float.MaxValue;
        Vector3 position = npc.transform.position;

        try
        {
            CollectEntitiesInRange<Rust.Ai.Gen2.Bear>(position, HardAvoidAnimalRadius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Crocodile>(position, HardAvoidAnimalRadius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Boar>(position, HardAvoidAnimalRadius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.PolarBear>(position, HardAvoidAnimalRadius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Wolf2>(position, HardAvoidAnimalRadius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Panther>(position, HardAvoidAnimalRadius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Tiger>(position, HardAvoidAnimalRadius, candidates);

            foreach (BaseCombatEntity candidate in candidates)
            {
                if (candidate == null || candidate.IsDestroyed || !candidate.IsAlive())
                {
                    continue;
                }

                // Skip candidates the survivor is equipped to fight; only genuine avoid-threats are steered around.
                if (!IsHardAvoidAnimal(candidate, npc))
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - position).sqrMagnitude;

                if (distanceSqr < nearestDistanceSqr)
                {
                    nearestDistanceSqr = distanceSqr;
                    animal = candidate;
                }
            }
        }
        finally
        {
            Pool.FreeUnmanaged(ref candidates);
        }

        return animal != null;
    }

    // Detection radius for hostile scientists when deciding whether to avoid nearby loot.
    private const float ScientistLootAvoidDetectionRadius = 30f;

    // Distance from a detected hostile scientist within which a loot container is skipped.
    private const float ScientistLootAvoidContainerRadius = 15f;

    /// <summary>
    /// Returns the positions of nearby hostile scientists visible to the survivor, computed once per loot search. Returns an empty list if the survivor is already armed, since avoidance does not apply then.
    /// </summary>
    private List<Vector3> GetNearbyVisibleHostileScientistPositions(BasePlayer npc)
    {
        List<Vector3> positions = new();

        if (HasReadyRangedWeapon(npc))
        {
            return positions;
        }

        List<BaseCombatEntity> candidates = Pool.Get<List<BaseCombatEntity>>();

        try
        {
            CollectEntitiesInRange<ScientistNPC>(npc.transform.position, ScientistLootAvoidDetectionRadius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.ScientistNPC2>(npc.transform.position, ScientistLootAvoidDetectionRadius, candidates);

            foreach (BaseCombatEntity candidate in candidates)
            {
                if (candidate != null && !candidate.IsDestroyed && candidate.IsAlive()
                    && IsHostileScientist(candidate) && HasCombatLineOfSight(npc, candidate))
                {
                    positions.Add(candidate.transform.position);
                }
            }
        }
        finally
        {
            Pool.FreeUnmanaged(ref candidates);
        }

        return positions;
    }

    private static bool IsNearVisibleHostileScientist(Vector3 candidatePosition, List<Vector3> nearbyVisibleHostileScientistPositions)
    {
        foreach (Vector3 scientistPosition in nearbyVisibleHostileScientistPositions)
        {
            if (Vector3.Distance(candidatePosition, scientistPosition) <= ScientistLootAvoidContainerRadius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the aim point for a combat target: a player's eyes, or an animal's center point if it has no eyes component.
    /// </summary>
    private static Vector3 GetAimPoint(BaseCombatEntity target)
    {
        return target is BasePlayer player ? player.eyes.position : target.CenterPoint();
    }

    /// <summary>
    /// Returns a display name for logging: a player's display name, or an animal's prefab shortname.
    /// </summary>
    private static string GetAttackerDisplayName(BaseCombatEntity attacker)
    {
        return attacker is BasePlayer player ? player.displayName : attacker.ShortPrefabName;
    }

    /// <summary>
    /// On-sight detection range for real players, used by the periodic sweep that starts combat when a bot spots a target and interrupts its current task.
    /// </summary>
    private const float OnSightDetectionRange = 125f;

    // Detection range for bot-vs-bot on-sight spotting, kept narrower than OnSightDetectionRange to limit the cost of the O(n^2) scan across the bot population.
    private const float BotOnSightDetectionRange = 50f;

    // A bow-armed survivor only initiates on-sight combat within this range, regardless of the
    // normal player/bot detection ranges above - 2026-10-03, Lucas's own explicit spec ("bots
    // with bows please only engage other bots or players if they are within 50 metres of the
    // target, not 100m+"). Real bow combat (arrow drop/spread/cadence) is tuned for a much
    // closer fight than a firearm's effective range, so the long OnSightDetectionRange (125m)
    // was letting archers pick fights they had no business starting that far out.
    private const float BowOnSightEngagementRange = 50f;

    private static bool IsCurrentlyWieldingBow(BasePlayer npc)
    {
        Item activeItem = npc.GetActiveItem();
        return activeItem != null && Array.IndexOf(NonCombatCapableRangedWeaponShortnames, activeItem.info.shortname) >= 0;
    }

    // Caps baseRange to BowOnSightEngagementRange for a bow-armed survivor before the usual
    // biome-visibility scaling is applied, so a bow user in open terrain still can't initiate
    // past 50m the way a firearm user can.
    private static float GetOnSightEngagementRange(BasePlayer npc, float baseRange)
    {
        return IsCurrentlyWieldingBow(npc) ? Mathf.Min(baseRange, BowOnSightEngagementRange) : baseRange;
    }

    // How often the on-sight detection sweep runs, controlling how quickly a bot notices a nearby target. Reactive combat (taking damage) is unaffected and always responds instantly.
    private const float OnSightDetectionIntervalSeconds = 2f;

    private Timer _onSightDetectionTimer;

    /// <summary>
    /// Starts the recurring on-sight detection timer.
    /// </summary>
    private void StartOnSightDetection()
    {
        _onSightDetectionTimer?.Destroy();
        _onSightDetectionTimer = timer.Every(OnSightDetectionIntervalSeconds, RunOnSightDetectionScan);
    }

    private void StopOnSightDetection()
    {
        _onSightDetectionTimer?.Destroy();
        _onSightDetectionTimer = null;
    }

    /// <summary>
    /// Next time to log a periodic summary of looting survivors' gear scores and proximity, for diagnostics.
    /// </summary>
    private float _nextBotOnSightSummaryLogTime;

    private void LogBotOnSightSummary()
    {
        if (_engine == null)
        {
            return;
        }

        List<(string Alias, int GearScore, Vector3 Position, bool ScanBlocked, BasePlayer Npc)> looters = new();

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || !npc.IsAlive() || survivor.Character.State == CharacterState.Dead)
            {
                continue;
            }

            if (survivor.Character.CurrentTask != TaskType.LootForResources)
            {
                continue;
            }

            // Mirrors the same skip conditions RunOnSightDetectionScan applies, for accurate diagnostics.
            bool scanBlocked = npc.IsWounded() || _activeCombat.ContainsKey(survivor.Character.Id) || _activeAttacks.ContainsKey(survivor.Character.Id);

            looters.Add((survivor.Character.Alias, GetGearScore(npc), npc.transform.position, scanBlocked, npc));
        }

        if (looters.Count < 2)
        {
            VerbosePuts($"bot-onsight-summary: only {looters.Count} looting survivor(s) right now - need at least 2 for bot-vs-bot to ever matter.");
            return;
        }

        float nearestPairDistance = float.MaxValue;
        bool nearestPairHasLos = false;
        bool nearestPairBlocked = false;

        for (int i = 0; i < looters.Count; i++)
        {
            for (int j = i + 1; j < looters.Count; j++)
            {
                float distance = Vector3.Distance(looters[i].Position, looters[j].Position);

                if (distance < nearestPairDistance)
                {
                    nearestPairDistance = distance;
                    nearestPairHasLos = HasCombatLineOfSight(looters[i].Npc, looters[j].Npc);
                    nearestPairBlocked = looters[i].ScanBlocked || looters[j].ScanBlocked;
                }
            }
        }

        int eligibleCount = looters.Count(l => l.GearScore >= 15);
        int scanBlockedCount = looters.Count(l => l.ScanBlocked);

        VerbosePuts($"bot-onsight-summary: {looters.Count} looting survivor(s) ({eligibleCount} with gear >= 15, {scanBlockedCount} scan-blocked [wounded/_activeCombat/_activeAttacks]), nearest pair {nearestPairDistance:F0}m apart, LOS={nearestPairHasLos}, thatPairBlocked={nearestPairBlocked} (need <= {OnSightDetectionRange:F0}m + real LOS + not scan-blocked).");
    }

    private void RunOnSightDetectionScan()
    {
        if (_engine == null)
        {
            return;
        }

        if (Time.realtimeSinceStartup >= _nextBotOnSightSummaryLogTime)
        {
            _nextBotOnSightSummaryLogTime = Time.realtimeSinceStartup + 10f;
            LogBotOnSightSummary();
        }

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            Guid characterId = survivor.Character.Id;
            BasePlayer npc = survivor.Player;

            // Skip survivors already in combat or mid-loot-transfer, so an on-sight sighting doesn't interrupt an in-progress loot action. Actually taking damage still overrides this via the separate reactive path.
            if (npc == null
                || npc.IsDestroyed
                || npc.IsWounded()
                || survivor.Character.State == CharacterState.Dead
                || _activeCombat.ContainsKey(characterId)
                || _activeAttacks.ContainsKey(characterId))
            {
                continue;
            }

            // Only suppresses combat against real players; animal/scientist on-sight detection below still runs.
            if (!_disablePlayerCombat)
            {
                bool startedPlayerCombat = false;

                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    if (player == null
                        || player.IsDestroyed
                        || player == npc
                        || !player.IsConnected
                        || player.IsSleeping()
                        || !player.IsAlive())
                    {
                        continue;
                    }

                    if (Vector3.Distance(npc.transform.position, player.transform.position) > GetOnSightEngagementRange(npc, OnSightDetectionRange) * GetBiomeVisibilityFactor(npc.transform.position, player.transform.position))
                    {
                        continue;
                    }

                    if (!HasCombatLineOfSight(npc, player))
                    {
                        continue;
                    }

                    // Only engage a real player if genuinely equipped with a ranged or melee weapon.
                    if (!HasReadyRangedWeapon(npc) && !HasCombatReadyMeleeWeapon(npc))
                    {
                        continue;
                    }

                    // During the early server-uptime grace period, only engage a real player if significantly better geared than them.
                    if (Time.realtimeSinceStartup < BotVsBotAggressionGracePeriodSeconds
                        && GetGearScore(npc) < GetGearScore(player) + RealPlayerGraceGearScoreAdvantage)
                    {
                        continue;
                    }

                    // While looting, whether to break off and engage is scaled by gear score; a roaming survivor always engages on sight.
                    if (survivor.Character.CurrentTask == TaskType.LootForResources
                        && !IsInAirdropHotZone(characterId, npc.transform.position)
                        && UnityEngine.Random.value > GetLootAbandonChance(GetGearScore(npc)))
                    {
                        continue;
                    }

                    StartCombat(survivor, player);
                    startedPlayerCombat = true;
                    break;
                }

                if (startedPlayerCombat)
                {
                    continue;
                }
            }

            // Animal/scientist on-sight detection: an armed bot fights, an unarmed bot flees proactively rather than waiting to be hit. Runs only if the real-player check above did not already start a fight.
            bool startedNonPlayerCombat = TryStartAnimalOnSightCombat(survivor, npc);

            if (!startedNonPlayerCombat)
            {
                // Only tries a scientist target if no animal fight was already started this tick.
                startedNonPlayerCombat = TryStartScientistOnSightCombat(survivor, npc);
            }

            // Bot-vs-bot on-sight aggression, attempted unconditionally rather than only while looting; eligibility is still gated internally by gear score.
            if (!startedNonPlayerCombat)
            {
                TryStartBotOnSightCombat(survivor, npc);
            }
        }
    }

    /// <summary>
    /// Returns the probability a survivor abandons its current loot task to engage a spotted target, scaled by gear score: low-gear survivors stay on task, high-gear survivors almost always break off. Re-rolled on each detection tick.
    /// </summary>
    private static float GetLootAbandonChance(int gearScore)
    {
        if (gearScore <= 14)
        {
            return 0f;
        }

        if (gearScore <= 30)
        {
            return Mathf.Lerp(0f, 0.4f, Mathf.InverseLerp(15f, 30f, gearScore));
        }

        if (gearScore <= 50)
        {
            return Mathf.Lerp(0.3f, 0.7f, Mathf.InverseLerp(31f, 50f, gearScore));
        }

        return 0.9f;
    }

    // Gear score advantage needed, absent a ranged-weapon gap, to count as a genuine outmatch between two similarly armed survivors.
    private const int OutmatchGearScoreAdvantage = 20;

    /// <summary>
    /// Returns whether npc has a clear combat advantage over otherNpc: either npc has a ranged weapon and otherNpc does not, or npc's gear score is significantly higher.
    /// </summary>
    private bool HasOutmatchingCombatAdvantage(BasePlayer npc, BasePlayer otherNpc)
    {
        bool hasRanged = npc.GetHeldEntity() is BaseProjectile;
        bool otherHasRanged = otherNpc.GetHeldEntity() is BaseProjectile;

        if (hasRanged && !otherHasRanged)
        {
            return true;
        }

        // If neither has a ranged weapon, a real melee weapon against bare hands/a rock also counts as an outmatch.
        if (!hasRanged && !otherHasRanged && HasCombatReadyMeleeWeapon(npc) && !HasCombatReadyMeleeWeapon(otherNpc))
        {
            return true;
        }

        return GetGearScore(npc) - GetGearScore(otherNpc) >= OutmatchGearScoreAdvantage;
    }

    /// <summary>
    /// Server-wide grace period letting the bot population bootstrap before proactive bot-vs-bot aggression begins. Reactive self-defense and real-player interaction are unaffected.
    /// </summary>
    private const float BotVsBotAggressionGracePeriodSeconds = 1800f;

    // Gear score advantage a bot needs over a real player to bypass the real-player grace period above.
    private const int RealPlayerGraceGearScoreAdvantage = 20;

    // Throttles repeat diagnostic logging for the same still-not-engaging bot pair; the actual detection/combat roll is unaffected.
    private const float BotOnSightDiagLogCooldownSeconds = 15f;

    private readonly Dictionary<(Guid, Guid), float> _lastBotOnSightDiagLogTime = new();

    private void TryStartBotOnSightCombat(Survivor survivor, BasePlayer npc)
    {
        if (_engine == null)
        {
            return;
        }

        // Inside an airdrop hot zone, a survivor skips the grace period and abandon-chance rolls and simply engages the nearest visible living survivor.
        if (IsInAirdropHotZone(survivor.Character.Id, npc.transform.position))
        {
            Survivor nearestTarget = null;
            float nearestDistance = BotOnSightDetectionRange;

            foreach (Survivor other in _engine.SurvivorManager.GetAll())
            {
                BasePlayer otherNpc = other.Player;

                if (other == survivor || otherNpc == null || otherNpc.IsDestroyed || otherNpc.IsWounded()
                    || other.Character.State == CharacterState.Dead || !otherNpc.IsAlive())
                {
                    continue;
                }

                float distance = Vector3.Distance(npc.transform.position, otherNpc.transform.position);

                if (distance < nearestDistance && HasCombatLineOfSight(npc, otherNpc))
                {
                    nearestDistance = distance;
                    nearestTarget = other;
                }
            }

            if (nearestTarget != null)
            {
                StartCombat(survivor, nearestTarget.Player);
            }

            return;
        }

        // _skipBotVsBotGracePeriod lets this grace period be bypassed for testing.
        if (!_skipBotVsBotGracePeriod && Time.realtimeSinceStartup < BotVsBotAggressionGracePeriodSeconds)
        {
            return;
        }

        // A survivor with no ready ranged weapon and no real melee weapon never proactively picks a fight; reactive self-defense and fleeing from predators are unaffected.
        if (!HasReadyRangedWeapon(npc) && !HasCombatReadyMeleeWeapon(npc))
        {
            return;
        }

        // A genuinely undergeared survivor can still take a fight if it clearly outmatches the target, even though the probabilistic roll below stays at 0% for it.
        float abandonChance = GetLootAbandonChance(GetGearScore(npc));

        foreach (Survivor other in _engine.SurvivorManager.GetAll())
        {
            if (other == survivor)
            {
                continue;
            }

            BasePlayer otherNpc = other.Player;

            if (otherNpc == null
                || otherNpc.IsDestroyed
                || otherNpc.IsWounded()
                || other.Character.State == CharacterState.Dead
                || !otherNpc.IsAlive())
            {
                continue;
            }

            if (Vector3.Distance(npc.transform.position, otherNpc.transform.position) > GetOnSightEngagementRange(npc, BotOnSightDetectionRange) * GetBiomeVisibilityFactor(npc.transform.position, otherNpc.transform.position))
            {
                continue;
            }

            if (!HasCombatLineOfSight(npc, otherNpc))
            {
                continue;
            }

            // Don't proactively target a survivor with no real weapon; reactive self-defense still applies if that survivor fights back.
            if (!HasReadyRangedWeapon(otherNpc) && !HasCombatReadyMeleeWeapon(otherNpc))
            {
                continue;
            }

            // A genuine outmatch always engages; otherwise the normal gear-threshold roll below decides.
            bool outmatches = HasOutmatchingCombatAdvantage(npc, otherNpc);
            bool tookTheFight = outmatches || UnityEngine.Random.value <= abandonChance;

            // Diagnostic logging for bot-vs-bot detection, throttled per pair so a mutually-visible pair doesn't spam the log every tick. An actual engage always bypasses the cooldown.
            (Guid, Guid) botOnSightDiagKey = (survivor.Character.Id, other.Character.Id);
            float nowForDiag = Time.realtimeSinceStartup;

            if (tookTheFight
                || !_lastBotOnSightDiagLogTime.TryGetValue(botOnSightDiagKey, out float lastDiagLogTime)
                || nowForDiag - lastDiagLogTime >= BotOnSightDiagLogCooldownSeconds)
            {
                _lastBotOnSightDiagLogTime[botOnSightDiagKey] = nowForDiag;
                VerbosePuts($"bot-onsight-diag: '{survivor.Character.Alias}' (gear {GetGearScore(npc)}, {abandonChance:P0} chance{(outmatches ? ", OUTMATCHES" : "")}) spotted '{other.Character.Alias}' (gear {GetGearScore(otherNpc)}) - {(tookTheFight ? "engaging" : "roll failed, staying on task")}.");
            }

            if (!tookTheFight)
            {
                // Spotted, but this roll didn't take the fight; stays on task and gets another chance next scan tick.
                continue;
            }

            StartCombat(survivor, otherNpc);
            return;
        }
    }

    /// <summary>
    /// Scans for the nearest huntable predator within AnimalOnSightRange and engages it. Returns whether combat was started.
    /// </summary>
    private bool TryStartAnimalOnSightCombat(Survivor survivor, BasePlayer npc)
    {
        List<BaseCombatEntity> candidates = Pool.Get<List<BaseCombatEntity>>();

        try
        {
            CollectEntitiesInRange<Rust.Ai.Gen2.Bear>(npc.transform.position, AnimalOnSightRange, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.PolarBear>(npc.transform.position, AnimalOnSightRange, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Boar>(npc.transform.position, AnimalOnSightRange, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Stag>(npc.transform.position, AnimalOnSightRange, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Wolf2>(npc.transform.position, AnimalOnSightRange, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Crocodile>(npc.transform.position, AnimalOnSightRange, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Panther>(npc.transform.position, AnimalOnSightRange, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Tiger>(npc.transform.position, AnimalOnSightRange, candidates);

            return TryEngageNearestCandidate(survivor, npc, candidates);
        }
        finally
        {
            Pool.FreeUnmanaged(ref candidates);
        }
    }

    /// <summary>
    /// Scans for the nearest hostile scientist NPC within ScientistOnSightRange and engages it, excluding known passive/safe-zone prefabs.
    /// </summary>
    private bool TryStartScientistOnSightCombat(Survivor survivor, BasePlayer npc)
    {
        List<BaseCombatEntity> candidates = Pool.Get<List<BaseCombatEntity>>();

        try
        {
            CollectEntitiesInRange<ScientistNPC>(npc.transform.position, ScientistOnSightRange, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.ScientistNPC2>(npc.transform.position, ScientistOnSightRange, candidates);

            candidates.RemoveAll(candidate => !IsHostileScientist(candidate));

            return TryEngageNearestCandidate(survivor, npc, candidates);
        }
        finally
        {
            Pool.FreeUnmanaged(ref candidates);
        }
    }

    private bool TryEngageNearestCandidate(Survivor survivor, BasePlayer npc, List<BaseCombatEntity> candidates)
    {
        BaseCombatEntity nearest = null;
        float nearestDistance = float.MaxValue;

        foreach (BaseCombatEntity candidate in candidates)
        {
            if (candidate == null || candidate.IsDestroyed || !candidate.IsAlive())
            {
                continue;
            }

            float distance = Vector3.Distance(npc.transform.position, candidate.transform.position);

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = candidate;
            }
        }

        if (nearest != null && HasCombatLineOfSight(npc, nearest))
        {
            StartCombat(survivor, nearest);
            return true;
        }

        return false;
    }

    private static void CollectEntitiesInRange<T>(Vector3 origin, float radius, List<BaseCombatEntity> results) where T : BaseCombatEntity
    {
        List<T> buffer = Pool.Get<List<T>>();

        try
        {
            BaseEntity.Query.Server.GetInSphere(origin, radius, buffer);

            foreach (T entity in buffer)
            {
                results.Add(entity);
            }
        }
        finally
        {
            Pool.FreeUnmanaged(ref buffer);
        }
    }

    // Per-tick combat cadence, also acting as an upper bound on fire rate since a weapon can only fire once per tick. Kept below every real Rust firearm's own repeat delay so fire rate is never artificially capped.
    private const float CombatTickInterval = 0.05f;

    // Health threshold below which a survivor disengages and flees. An absolute value, since every survivor is always initialized to the same max health.
    private const float CombatFleeHealthThreshold = 25f;

    /// <summary>
    /// Health threshold below which a survivor uses medical supplies to self-heal, checked every loot-task cycle including mid-combat. Set high so a survivor tops off close to full health whenever supplies are available.
    /// </summary>
    private const float CombatHealHealthThreshold = 95f;

    /// <summary>
    /// Health level a heal chain continues using items up to, without re-equipping the weapon in between. Set higher than CombatHealHealthThreshold so a chain doesn't stop as soon as it crosses the start threshold.
    /// </summary>
    private const float CombatHealDoneThreshold = 80f;

    private const float CombatHealCooldownSeconds = 4f;

    private readonly Dictionary<Guid, float> _lastSelfHealTime = new();

    /// <summary>
    /// Per-survivor deadline marking an active heal animation, so the combat tick's weapon-check does not mistake the swapped-out medical item for having lost the weapon.
    /// </summary>
    private readonly Dictionary<Guid, float> _healingUntilTime = new();

    /// <summary>
    /// Per-survivor counter incremented on each fresh heal chain start. Lets a deferred continuation detect that its chain has been superseded and stop instead of colliding with a newer one.
    /// </summary>
    private readonly Dictionary<Guid, int> _healChainGeneration = new();

    /// <summary>
    /// Priority order of held medical items to use for self-healing. Excludes largemedkit, which heals passively rather than through an active use.
    /// </summary>
    private static readonly string[] CombatHealItemPriority = { "syringe.medical", "bandage" };

    /// <summary>
    /// Attempts to self-heal a survivor with a held medical item, calling the real MedicalTool ServerUse() so consumption, cooldown, animation, and heal amount all match a real player's usage.
    /// </summary>
    private bool TryUseMedicalItemIfHurt(Survivor survivor)
    {
        Guid characterId = survivor.Character.Id;
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || !npc.IsAlive())
        {
            return false;
        }

        if (npc.health >= CombatHealHealthThreshold)
        {
            return false;
        }

        if (_lastSelfHealTime.TryGetValue(characterId, out float lastHeal) && Time.realtimeSinceStartup - lastHeal < CombatHealCooldownSeconds)
        {
            return false;
        }

        // Explicit "already busy healing" check, preventing two heal chains from running concurrently for the same survivor.
        if (_healingUntilTime.TryGetValue(characterId, out float healingUntilTime) && Time.realtimeSinceStartup < healingUntilTime)
        {
            return false;
        }

        if (FindBestHealItem(npc) == null)
        {
            return false;
        }

        int generation = _healChainGeneration.TryGetValue(characterId, out int existingGeneration) ? existingGeneration + 1 : 1;
        _healChainGeneration[characterId] = generation;

        UseOneMedicalItemThenMaybeContinue(survivor, generation);
        return true;
    }

    private static Item FindBestHealItem(BasePlayer npc)
    {
        foreach (string shortname in CombatHealItemPriority)
        {
            Item found = npc.inventory.containerBelt.itemList
                .Concat(npc.inventory.containerMain.itemList)
                .FirstOrDefault(item => item.info.shortname == shortname);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Performs one MedicalTool use and then decides whether to keep chaining into another item. Called from TryUseMedicalItemIfHurt and recursively from its own completion callback.
    /// </summary>
    /// <summary>
    /// Delay before calling ServerUse() after equipping the item, letting the draw animation settle first, matching real NPC healing behavior.
    /// </summary>
    private const float MedicalItemPreUseDelaySeconds = 1f;

    /// <summary>
    /// Hold time after using a medical item before re-equipping the weapon.
    /// </summary>
    private const float MedicalItemPostUseHoldSeconds = 2f;

    private bool IsCurrentHealGeneration(Guid characterId, int generation)
    {
        return _healChainGeneration.TryGetValue(characterId, out int currentGeneration) && currentGeneration == generation;
    }

    private void UseOneMedicalItemThenMaybeContinue(Survivor survivor, int generation)
    {
        Guid characterId = survivor.Character.Id;
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || !npc.IsAlive() || !IsCurrentHealGeneration(characterId, generation))
        {
            return;
        }

        // Healing runs concurrently with movement, so no movement is cancelled here.
        Item healItem = FindBestHealItem(npc);

        if (healItem == null)
        {
            // Out of medical items mid-chain; stop here and return to fighting at whatever health was reached.
            _lastSelfHealTime[characterId] = Time.realtimeSinceStartup;
            EquipBestWeaponForDisplay(survivor);
            return;
        }

        npc.UpdateActiveItem(healItem.uid);
        ForceRefreshHeldEntity(npc);

        if (npc.GetHeldEntity() is not MedicalTool equippedTool)
        {
            _lastSelfHealTime[characterId] = Time.realtimeSinceStartup;
            EquipBestWeaponForDisplay(survivor);
            return;
        }

        // Clear the aiming-in state before healing so it doesn't conflict with the medical item's use animation; combat naturally re-sets it once fighting resumes.
        SetAimingIn(npc, false);

        ItemId healItemUid = healItem.uid;
        string healItemShortname = healItem.info.shortname;

        // Post-use hold is derived from the item's own real animation duration rather than a fixed value, so total timing matches the actual item.
        float postUseHold = Mathf.Max(equippedTool.healDurationSelf - MedicalItemPreUseDelaySeconds, MedicalItemPostUseHoldSeconds);
        float totalDuration = MedicalItemPreUseDelaySeconds + postUseHold;

        _healingUntilTime[characterId] = Time.realtimeSinceStartup + totalDuration;

        VerbosePuts($"'{survivor.Character.Alias}' is using '{healItemShortname}' ({totalDuration:F1}s)...");

        timer.Once(MedicalItemPreUseDelaySeconds, () =>
        {
            BasePlayer useNpc = survivor.Player;

            // If a newer heal chain has already started, this orphaned older chain must not touch anything.
            if (useNpc == null || useNpc.IsDestroyed || survivor.Character.State == CharacterState.Dead || !IsCurrentHealGeneration(characterId, generation))
            {
                return;
            }

            // Re-verify the item is still held, since enough time has passed for it to have been dropped, lost, or interrupted.
            if (useNpc.inventory.FindItemByUID(healItemUid) == null || useNpc.GetHeldEntity() is not MedicalTool heldTool || heldTool.GetItem()?.uid != healItemUid)
            {
                EquipBestWeaponForDisplay(survivor);
                return;
            }

            float healthBefore = useNpc.health;

            heldTool.ServerUse();

            VerbosePuts($"heal-diag: '{survivor.Character.Alias}' used '{healItemShortname}' - health {healthBefore:F0} -> {useNpc.health:F0}.");

            timer.Once(postUseHold, () =>
            {
                BasePlayer laterNpc = survivor.Player;

                if (laterNpc == null || laterNpc.IsDestroyed || survivor.Character.State == CharacterState.Dead || !IsCurrentHealGeneration(characterId, generation))
                {
                    return;
                }

                // Keep chaining into another item if still below the done threshold and an item is available.
                if (laterNpc.health < CombatHealDoneThreshold && FindBestHealItem(laterNpc) != null)
                {
                    UseOneMedicalItemThenMaybeContinue(survivor, generation);
                    return;
                }

                _lastSelfHealTime[characterId] = Time.realtimeSinceStartup;
                EquipBestWeaponForDisplay(survivor);
            });
        });
    }

    /// <summary>
    /// How long a fight continues with no line of sight on the attacker before giving up entirely, regardless of distance. Does not apply to animal targets, which are always pursued.
    /// </summary>
    private const float NoRealEngagementDisengageSeconds = 15f;

    /// <summary>
    /// Distance to back away from the attacker when forced to reload mid-fight with an empty magazine.
    /// </summary>
    private const float ReloadRetreatDistance = 8f;

    /// <summary>
    /// How long the reload retreat lasts once triggered.
    /// </summary>
    private const float ReloadRetreatDuration = 2.5f;

    /// <summary>
    /// Per-survivor deadline marking exposure right after reloading an empty weapon mid-fight; real damage landing within this window triggers a retreat.
    /// </summary>
    private readonly Dictionary<Guid, float> _reloadExposureWindowUntil = new();

    /// <summary>
    /// Per-survivor deadline until which the combat tick's aim/fire/follow logic is suspended during a reload retreat.
    /// </summary>
    private readonly Dictionary<Guid, float> _combatRetreatUntilTime = new();

    /// <summary>
    /// Triggers a reload retreat when real damage lands while the reload exposure window is still open, backing away from the actual damage source.
    /// </summary>
    private void TryTriggerReloadRetreat(Survivor survivor, BasePlayer npc, BaseCombatEntity damageSource)
    {
        Guid characterId = survivor.Character.Id;

        if (!_reloadExposureWindowUntil.TryGetValue(characterId, out float windowEnd) || Time.realtimeSinceStartup > windowEnd)
        {
            return;
        }

        _reloadExposureWindowUntil.Remove(characterId);

        Vector3 awayFromDamage = npc.transform.position - damageSource.transform.position;
        awayFromDamage.y = 0f;
        awayFromDamage = awayFromDamage.sqrMagnitude > 0.01f ? awayFromDamage.normalized : npc.eyes.BodyForward() * -1f;

        Vector3 retreatDestination = npc.transform.position + awayFromDamage * ReloadRetreatDistance;

        _combatRetreatUntilTime[characterId] = Time.realtimeSinceStartup + ReloadRetreatDuration;

        VerbosePuts($"'{survivor.Character.Alias}' got hit mid-reload - backing away {ReloadRetreatDistance:F0}m to create distance.");

        StartWalking(survivor, retreatDestination);
    }

    // Minimum pause between volleys in the distance-banded burst system.
    private const float BurstPauseFloorSeconds = 1f;

    // Distance below which the P17 fires in burst mode; beyond it, single/semi-auto.
    private const float P17BurstSwitchDistance = 20f;

    // Pace floor for the P17's single-fire (semi-auto) mode beyond P17BurstSwitchDistance.
    private const float P17SingleFirePaceSeconds = 0.67f;

    // Periodically nudges a bot's held combat distance closer to its target, so it doesn't camp passively at max range forever. Distance bands for firing still use the real live distance, not this shrinking hold distance.
    private const float AdvanceIntervalSeconds = 4f;
    private const float AdvanceStepMinMeters = 10f;
    private const float AdvanceStepMaxMeters = 20f;

    // Faster advance cadence used for shotguns, which close distance more aggressively than other weapons.
    private const float ShotgunAdvanceIntervalSeconds = 1.5f;

    // Shortnames this project treats as shotguns, used to apply the shotgun-specific advance cadence.
    private static readonly HashSet<string> ShotgunShortnames = new()
    {
        "shotgun.pump",
        "shotgun.m4",
        "shotgun.spas12",
        "shotgun.double",
    };
    private const float AdvanceMinimumDistance = 5f;

    // Smaller "close a few metres" nudge applied in the natural pause between burst volleys, rather than interrupting an active exchange. Continuous-fire weapons keep using the periodic timer instead.
    private const float PostBurstAdvanceStepMinMeters = 3f;
    private const float PostBurstAdvanceStepMaxMeters = 6f;

    // Grace period after sprinting before firing is allowed again, letting the stop-sprint animation settle so a bot doesn't visibly fire mid-sprint.
    private const float SprintFireSettleSeconds = 0.3f;

    // Reaction-time delay, distance-scaled, before a bot acts on regaining line of sight (losing LOS is still instant). Checked against live distance at the moment LOS returns.
    private static readonly (float MaxDistance, float Min, float Max)[] LosRegainReactionBands =
    {
        (10f, 0.02f, 0.02f),
        (15f, 0.03f, 0.05f),
        (20f, 0.05f, 0.06f),
        (25f, 0.06f, 0.08f),
        (30f, 0.08f, 0.09f),
        (35f, 0.09f, 0.11f),
        (40f, 0.10f, 0.13f),
        (45f, 0.11f, 0.14f),
        (50f, 0.13f, 0.16f),
        (55f, 0.14f, 0.18f),
        (60f, 0.15f, 0.19f),
        (65f, 0.16f, 0.21f),
        (70f, 0.18f, 0.23f),
        (75f, 0.19f, 0.24f),
        (80f, 0.20f, 0.26f),
        (85f, 0.21f, 0.27f),
        (90f, 0.23f, 0.29f),
        (95f, 0.24f, 0.30f),
        (100f, 0.25f, 0.31f),
    };

    // Flat LOS-regain reaction delay for distances beyond the last LosRegainReactionBands entry (100m+).
    private const float LosRegainReactionExtremeSeconds = 3f;

    // Global multiplier applied to LosRegainReactionBands only (not the 100m+ flat extreme value).
    private const float LosRegainReactionMultiplier = 1.3f;

    private static float RollLosRegainDelay(float distance)
    {
        foreach (var band in LosRegainReactionBands)
        {
            if (distance <= band.MaxDistance)
            {
                return UnityEngine.Random.Range(band.Min, band.Max) * LosRegainReactionMultiplier;
            }
        }

        return LosRegainReactionExtremeSeconds;
    }

    /// <summary>
    /// Per-weapon-family fire behavior: engagement/pursue range and distance-banded burst sizes. An empty Bands array means continuous fire; a band's minBurst of -1 means continuous fire just for that band.
    /// </summary>
    private readonly struct WeaponFireProfile
    {
        public readonly float EngagementRange;
        public readonly float PursueRange;
        public readonly (float MaxDistance, int MinBurst, int MaxBurst)[] Bands;

        /// <summary>
        /// pursueRange defaults to engagementRange, but can be set wider for short-range weapons like shotguns so a bot still chases a distant attacker instead of disengaging immediately.
        /// </summary>
        public WeaponFireProfile(float engagementRange, float pursueRange, params (float, int, int)[] bands)
        {
            EngagementRange = engagementRange;
            PursueRange = pursueRange;
            Bands = bands;
        }

        public WeaponFireProfile(float engagementRange, params (float, int, int)[] bands)
            : this(engagementRange, engagementRange, bands)
        {
        }
    }

    // Chase distance for short-range weapons like shotguns, wider than their actual firing range.
    private const float ShotgunPursueRange = 80f;

    // Fire profile for MP5/Thompson/M16A2: distance-banded burst sizes, with a guaranteed minimum burst only at point-blank range.
    private static readonly WeaponFireProfile AutoRifleProfile = new(100f, (10f, 10, 15), (20f, 0, 12), (30f, 0, 10), (60f, 0, 6), (100f, 0, 3));

    // Fire profile for the AK and LR300, with larger bursts at 50m+ than the shared AutoRifleProfile.
    private static readonly WeaponFireProfile AkLr300Profile = new(100f, (10f, 10, 15), (20f, 0, 12), (30f, 0, 10), (50f, 0, 6), (60f, 0, 8), (100f, 0, 4));

    // Fire profile for the M249/HMLMG: continuous fire at point-blank range, banded bursts beyond it.
    private static readonly WeaponFireProfile LmgProfile = new(100f, (10f, -1, -1), (20f, 0, 26), (30f, 0, 24), (50f, 0, 18), (75f, 0, 15), (90f, 0, 10), (100f, 0, 6));

    // Fire profile for the Custom SMG / Handmade SMG.
    private static readonly WeaponFireProfile CustomSmgProfile = new(100f, (10f, 8, 12), (15f, 0, 12), (30f, 0, 10), (60f, 0, 5), (100f, 0, 2));

    // Fire profile for semi-auto rifles (SKS, M39, etc).
    private static readonly WeaponFireProfile SemiAutoRifleProfile = new(100f, (10f, 3, 5), (30f, 0, 5), (60f, 0, 4), (100f, 0, 3));

    // Fire profile for standard pistols.
    private static readonly WeaponFireProfile PistolProfile = new(100f, (10f, 5, 10), (30f, 0, 10), (60f, 0, 5), (100f, 0, 3));

    // Fire profile for the Python revolver, more conservative than PistolProfile due to its limited cylinder capacity, with continuous fire at point-blank range.
    private static readonly WeaponFireProfile PythonProfile = new(100f, (10f, -1, -1), (20f, 0, 4), (30f, 0, 3), (40f, 0, 2), (50f, 0, 2), (100f, 0, 2));

    // Shared fire profile for Pump/M4/SPAS-12 shotguns.
    private static readonly WeaponFireProfile ShotgunBurstProfile = new(35f, ShotgunPursueRange, (10f, -1, -1), (15f, 2, 4), (20f, 1, 3), (25f, 0, 2), (30f, 0, 1), (35f, 0, 1));

    // Fire profile for the Double Barrel shotgun: continuous fire only within 10m, since its 2-shell capacity can't sustain a variable burst.
    private static readonly WeaponFireProfile DoubleBarrelProfile = new(10f, ShotgunPursueRange);
    private static readonly WeaponFireProfile SniperProfile = new(150f);
    private static readonly WeaponFireProfile DefaultFireProfile = new(20f, ShotgunPursueRange);

    // Maps weapon shortnames to fire profiles. ValidateCombatFireProfiles checks these shortnames at boot. m16a2/pistol.prototype17 are included only for their engagement/pursue range; their burst count comes from a separate fixed path.
    private static readonly Dictionary<string, WeaponFireProfile> WeaponFireProfiles = new()
    {
        ["rifle.lr300"] = AkLr300Profile,
        ["lmg.m249"] = LmgProfile,
        ["hmlmg"] = LmgProfile,
        ["rifle.ak"] = AkLr300Profile,
        ["smg.mp5"] = AutoRifleProfile,
        ["smg.thompson"] = AutoRifleProfile,
        ["m16a2"] = AutoRifleProfile,

        ["smg.2"] = CustomSmgProfile,

        // Handmade SMG, grouped with the Custom SMG as a low-tier weapon.
        ["t1_smg"] = CustomSmgProfile,

        ["rifle.semiauto"] = SemiAutoRifleProfile,
        ["rifle.sks"] = SemiAutoRifleProfile,
        ["rifle.m39"] = SemiAutoRifleProfile,

        ["pistol.python"] = PythonProfile,
        ["pistol.revolver"] = PistolProfile,
        ["pistol.semiauto"] = PistolProfile,
        ["pistol.m92"] = PistolProfile,
        ["revolver.hc"] = PistolProfile,
        ["pistol.prototype17"] = PistolProfile,

        ["shotgun.pump"] = ShotgunBurstProfile,
        ["shotgun.m4"] = ShotgunBurstProfile,
        ["shotgun.spas12"] = ShotgunBurstProfile,
        ["shotgun.double"] = DoubleBarrelProfile,

        ["rifle.l96"] = SniperProfile,
        ["rifle.bolt"] = SniperProfile,
    };

    private static WeaponFireProfile GetCombatFireProfile(string shortname)
    {
        return WeaponFireProfiles.TryGetValue(shortname, out WeaponFireProfile profile) ? profile : DefaultFireProfile;
    }

    /// <summary>
    /// Distance-scaled bullet spread per weapon: tighter at point-blank, wider at range, interpolated linearly by distance. Shotguns and snipers are excluded since they already have real spread or should stay precise.
    /// </summary>
    private static readonly Dictionary<string, (float MinDegrees, float MaxDegrees)> WeaponSpreadDegrees = new()
    {
        // Rifle tier - tightest control at range.
        ["rifle.lr300"] = (0.15f, 1.2f),
        ["lmg.m249"] = (0.15f, 1.2f),
        ["hmlmg"] = (0.15f, 1.2f),
        ["rifle.ak"] = (0.15f, 1.2f),
        ["rifle.semiauto"] = (0.15f, 1.2f),
        ["rifle.sks"] = (0.15f, 1.2f),
        ["rifle.m39"] = (0.15f, 1.2f),
        ["m16a2"] = (0.15f, 1.2f),

        // MP5 - a real, proper SMG, but still a notch behind a rifle.
        ["smg.mp5"] = (0.25f, 1.8f),

        // Thompson - noticeably worse accuracy than AK/LR300/MP5 at range despite sharing the same burst profile.
        ["smg.thompson"] = (0.35f, 2.5f),

        // Pistols - middling, in between the named SMGs and the low tier.
        ["pistol.python"] = (0.25f, 2.0f),
        ["pistol.revolver"] = (0.25f, 2.0f),
        ["pistol.semiauto"] = (0.25f, 2.0f),
        ["pistol.m92"] = (0.25f, 2.0f),
        ["revolver.hc"] = (0.25f, 2.0f),
        ["pistol.prototype17"] = (0.25f, 2.0f),

        // Low tier - both real in-game "low accuracy" weapons.
        ["smg.2"] = (0.45f, 3.2f),
        ["t1_smg"] = (0.45f, 3.2f),

        // Bows/crossbows: a bow is hand-drawn so it has the loosest spread; crossbows are steadier. Arrow drop is applied separately in FireAt.
        ["bow.hunting"] = (0.8f, 5.0f),
        ["bow.compound"] = (0.5f, 3.5f),
        ["crossbow"] = (0.4f, 3.0f),
        ["minicrossbow"] = (0.5f, 3.5f),
    };

    private static readonly Dictionary<string, float> BowMinShotIntervalSeconds = new()
    {
        ["bow.hunting"] = 1.6f,
        ["bow.compound"] = 1.0f,
        ["crossbow"] = 2.4f,
        ["minicrossbow"] = 1.0f,
    };

    // Simulates arrow drop for bow/crossbow shots, since a bot's shot is otherwise resolved as an instant hit with no physical arrow. Each shot compensates for 70-100% of the true drop, like an imperfect archer.
    private const float ArrowMuzzleSpeed = 55f;
    private const float ArrowGravity = 9.81f;
    private const float ArrowCompensationMin = 0.7f;

    /// <summary>
    /// Per-weapon fire-rate slowdown applied beyond a distance threshold, added on top of the weapon's real cooldown via FireWithPacing. Currently only slows the Python at 30m+, to read as more realistic for a revolver. Weapons not listed have no slowdown.
    /// </summary>
    private static readonly Dictionary<string, (float NearMultiplier, float FarMultiplier, float FarThresholdMeters)> WeaponFireRateMultiplier = new()
    {
        ["pistol.python"] = (1f, 2.184f, 30f),
    };

    /// <summary>
    /// Returns the fire-rate multiplier for a weapon at a given distance, or 1f (no slowdown) if not listed in WeaponFireRateMultiplier.
    /// </summary>
    private static float GetFireRateMultiplier(string weaponShortname, float distance)
    {
        if (!WeaponFireRateMultiplier.TryGetValue(weaponShortname, out var multiplier))
        {
            return 1f;
        }

        return distance >= multiplier.FarThresholdMeters ? multiplier.FarMultiplier : multiplier.NearMultiplier;
    }

    /// <summary>
    /// Returns the bullet spread for a weapon at a given distance, linearly interpolated between its point-blank and 100m+ spread values. Weapons with no entry get 0 spread.
    /// </summary>
    // Global multiplier applied across the whole WeaponSpreadDegrees table to uniformly widen spread without hand-editing each tier.
    private const float SpreadDegreesMultiplier = 1.155f;

    private static float GetWeaponSpreadDegrees(string shortname, float distance)
    {
        if (!WeaponSpreadDegrees.TryGetValue(shortname, out var spread))
        {
            return 0f;
        }

        float t = Mathf.Clamp01(distance / 100f);
        return Mathf.Lerp(spread.MinDegrees, spread.MaxDegrees, t) * SpreadDegreesMultiplier;
    }

    /// <summary>
    /// Returns the burst size range for the current distance band, or (-1, -1) for continuous fire. Falls back to the longest-range band if distance exceeds every defined band.
    /// </summary>
    private static (int MinBurst, int MaxBurst) GetBurstBandForDistance(WeaponFireProfile profile, float distance)
    {
        if (profile.Bands == null || profile.Bands.Length == 0)
        {
            return (-1, -1);
        }

        foreach (var band in profile.Bands)
        {
            if (distance <= band.MaxDistance)
            {
                return (band.MinBurst, band.MaxBurst);
            }
        }

        var last = profile.Bands[^1];
        return (last.MinBurst, last.MaxBurst);
    }

    private void ValidateCombatFireProfiles()
    {
        List<string> unresolved = new();

        foreach (string shortname in WeaponFireProfiles.Keys)
        {
            if (ItemManager.FindItemDefinition(shortname) == null)
            {
                unresolved.Add(shortname);
            }
        }

        if (unresolved.Count > 0)
        {
            Puts($"WARNING: WeaponFireProfiles contains {unresolved.Count} shortname(s) that don't resolve to any real item - these will never get their intended burst/range behaviour: {string.Join(", ", unresolved)}.");
        }
        else
        {
            Puts($"WeaponFireProfiles validated - all {WeaponFireProfiles.Count} shortnames resolve to real items.");
        }
    }

    /// <summary>
    /// Handles damage taken by a survivor, starting or continuing combat against the real attacker. Ignores environmental damage with no attacker.
    /// </summary>
    private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
    {
        if (entity is not BasePlayer player)
        {
            return;
        }

        // Diagnostic logging of damage dealt/taken in both directions, for comparing bot combat performance against real players.
        if (info != null && info.damageTypes.Total() > 0f)
        {
            BasePlayer damageAttacker = info.InitiatorPlayer;
            Survivor attackerSurvivor = damageAttacker != null ? FindSurvivorByPlayer(damageAttacker) : null;
            Survivor victimSurvivor = FindSurvivorByPlayer(player);

            // Logs how far off-center a hit landed, paired with shooter-to-target distance, for analyzing bullet spread accuracy.
            float shotDistance = damageAttacker != null ? Vector3.Distance(damageAttacker.transform.position, player.transform.position) : -1f;
            float offsetFromCenter = Vector3.Distance(info.HitPositionWorld, player.CenterPoint());

            if (attackerSurvivor != null && victimSurvivor == null)
            {
                VerbosePuts($"damage-diag: '{attackerSurvivor.Character.Alias}' dealt {info.damageTypes.Total():F1} damage to '{player.displayName}' (hit: {info.boneArea}, distance: {shotDistance:F1}m, offset-from-center: {offsetFromCenter:F2}m).");
            }
            else if (victimSurvivor != null && attackerSurvivor == null && damageAttacker != null)
            {
                VerbosePuts($"damage-diag: '{victimSurvivor.Character.Alias}' took {info.damageTypes.Total():F1} damage from '{damageAttacker.displayName}' (hit: {info.boneArea}, distance: {shotDistance:F1}m, offset-from-center: {offsetFromCenter:F2}m).");
            }

            // Tracks damage dealt to the current combat target for tactical decision-making, for every hit regardless of target type.
            if (attackerSurvivor != null)
            {
                RecordDamageDealtToCurrentAttacker(attackerSurvivor, player, info.damageTypes.Total());
            }

            if (victimSurvivor != null)
            {
                RecordHealthSample(victimSurvivor.Character.Id, player.health);
            }
        }

        Survivor survivor = FindSurvivorByPlayer(player);

        if (survivor == null || survivor.Character.State == CharacterState.Dead)
        {
            return;
        }

        // While debug invincibility is active, restore full health (and clear wounded state) on every hit instead of blocking the damage itself.
        if (_botsInvincible)
        {
            player.InitializeHealth(player.MaxHealth(), player.MaxHealth());

            if (player.IsWounded())
            {
                player.StopWounded();
            }
        }

        // Uses the raw Initiator (not InitiatorPlayer) so an animal attacker resolves correctly too, not just a real player.
        BaseEntity initiator = info?.Initiator;
        BaseCombatEntity attacker = initiator as BasePlayer;

        // While player combat is disabled for debugging, drop a real player attacker entirely; animal attackers are unaffected.
        if (attacker != null && _disablePlayerCombat)
        {
            attacker = null;
        }

        // A hostile scientist attacking also triggers the same reactive turn-and-fight response as a player or animal.
        if (attacker == null && IsHuntableThreat(initiator))
        {
            attacker = initiator as BaseCombatEntity;
        }

        if (attacker == null || attacker == player)
        {
            return;
        }

        // Triggers a reload retreat if this damage lands during an open reload-exposure window.
        TryTriggerReloadRetreat(survivor, player, attacker);

        StartCombat(survivor, attacker);
    }

    /// <summary>
    /// Logs every shot a real player fires, as reference data for the spread-accuracy diagnostics in OnEntityTakeDamage. Never fires for bots, since they use ServerUse() directly rather than the client fire RPC this hook is keyed off.
    /// </summary>
    private void OnWeaponFired(BaseProjectile projectile, BasePlayer player, ItemModProjectile mod, ProtoBuf.ProjectileShoot shoot)
    {
        VerbosePuts($"shot-fired-diag: '{player.displayName}' fired '{projectile.GetItem()?.info.shortname}' from {player.transform.position}.");
    }

    /// <summary>
    /// Equips the highest-scoring ranged weapon this survivor owns that actually has usable ammo, trying each candidate in descending gear-score order until one has (or can reload) ammo. Returns false if nothing owned has any ammo.
    /// </summary>
    private bool TryEquipBestArmedWeapon(Survivor survivor, out BaseProjectile weapon)
    {
        weapon = null;
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return false;
        }

        // Fast path: if the currently held weapon already has (or can reload into having) ammo, no belt-shuffling is needed.
        if (npc.GetHeldEntity() is BaseProjectile currentWeapon
            && Array.IndexOf(WeaponPriority, currentWeapon.GetItem()?.info.shortname) >= 0)
        {
            if (currentWeapon.primaryMagazine.contents <= 0)
            {
                currentWeapon.ServerTryReload(npc.inventory);
            }

            if (currentWeapon.primaryMagazine.contents > 0)
            {
                weapon = currentWeapon;
                return true;
            }
        }

        // Excludes melee weapons (also present in WeaponPriority) since they have no magazine to test and would otherwise cause a visible equip/discard flicker when out of ranged ammo.
        List<Item> candidates = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => Array.IndexOf(WeaponPriority, item.info.shortname) >= 0
                && Array.IndexOf(MeleeToolPriority, item.info.shortname) < 0)
            .OrderByDescending(item => WeaponGearScore.TryGetValue(item.info.shortname, out int score) ? score : 0)
            .ToList();

        foreach (Item candidate in candidates)
        {
            if (!npc.inventory.containerBelt.itemList.Contains(candidate)
                && !candidate.MoveToContainer(npc.inventory.containerBelt, BeltWeaponSlot))
            {
                continue;
            }

            npc.UpdateActiveItem(candidate.uid);

            if (npc.GetHeldEntity() is not BaseProjectile candidateWeapon)
            {
                continue;
            }

            if (candidateWeapon.primaryMagazine.contents <= 0)
            {
                candidateWeapon.ServerTryReload(npc.inventory);
            }

            if (candidateWeapon.primaryMagazine.contents > 0)
            {
                // Re-applies UpdateActiveItem for the final winner after candidate-cycling settles, and forces a held-entity refresh so the client's held-item visual stays in sync.
                npc.UpdateActiveItem(candidateWeapon.GetItem().uid);
                ForceRefreshHeldEntity(npc);

                weapon = candidateWeapon;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Minimum turn angle required before a fresh engagement's first shot gets a telegraph delay; small corrections fire immediately.
    /// </summary>
    private const float FirstShotTelegraphMinTurnDegrees = 20f;

    /// <summary>
    /// Turn speed used to compute the telegraph delay before a fresh engagement's first shot, so the bot visibly finishes turning toward the attacker before firing. Scaled by turn angle and capped by FirstShotTelegraphMaxSeconds.
    /// </summary>
    private const float FirstShotTelegraphDegreesPerSecond = 540f;

    private const float FirstShotTelegraphMaxSeconds = 0.6f;

    /// <summary>
    /// Small real position nudge applied each tick during the first-shot telegraph, so the client's velocity-driven animation updates the bot's facing instead of relying on a static rotation flag.
    /// </summary>
    private const float FirstShotTelegraphNudgeDistance = 0.02f;

    /// <summary>
    /// Amplitude of the side-to-side jitter strafe used during combat.
    /// </summary>
    private const float CombatJitterAmplitude = 1.2f;

    /// <summary>
    /// Full oscillation period of the combat jitter strafe.
    /// </summary>
    private const float CombatJitterPeriodSeconds = 1.8f;

    /// <summary>
    /// Minimum angle off the survivor's pre-engagement facing for a fight's start to count as a genuine ambush.
    /// </summary>
    private const float RearAmbushAngleThreshold = 110f;

    /// <summary>
    /// Extra rounds added to both ends of the normal burst range on the first volley of a fight that started as an ambush, capped by RearAmbushBurstCap.
    /// </summary>
    private const int RearAmbushBurstBonusRounds = 3;

    private const int RearAmbushBurstCap = 10;

    /// <summary>
    /// Entry point into ranged combat. Cancels the survivor's current task and starts a per-tick fight against the attacker. Bails out if the survivor has no ranged weapon (no melee-vs-players fallback yet).
    /// </summary>
    private void StartCombat(Survivor survivor, BaseCombatEntity attacker)
    {
        Guid characterId = survivor.Character.Id;

        if (_activeCombat.ContainsKey(characterId))
        {
            // An animal attack interrupts an ongoing player fight; the interrupted player is remembered so combat can resume with them once the animal fight ends.
            if (IsHuntablePredator(attacker)
                && _activeCombatTarget.TryGetValue(characterId, out BaseCombatEntity currentTarget)
                && currentTarget is BasePlayer
                && currentTarget != attacker)
            {
                _pendingResumeCombatTarget[characterId] = currentTarget;
                CancelActiveCombat(characterId);
                // Falls through to the normal setup below, now targeting the animal instead of returning.
            }
            else
            {
                return;
            }
        }

        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || attacker == null || attacker.IsDestroyed)
        {
            return;
        }

        // A hard-avoid animal means no ranged weapon and no real melee weapon; this is the single source of truth for that rule across every combat-start path. Even here, a survivor holding at least a rock gets a fight-or-flight roll rather than an automatic flee, since always fleeing still gets it run down and bitten about as often as fighting would.
        if (IsHardAvoidAnimal(attacker, npc))
        {
            _pendingResumeCombatTarget.Remove(characterId);

            if (HasAnyMeleeTool(npc) && UnityEngine.Random.value < UnarmedAnimalFightChance)
            {
                VerbosePuts($"'{survivor.Character.Alias}' spotted a '{attacker.ShortPrefabName}' - fight-or-flight rolled fight, swinging back with whatever it's holding.");
                StartMeleeCombat(survivor, attacker);
                return;
            }

            StartFleeingFromThreat(survivor, attacker, $"'{survivor.Character.Alias}' spotted a '{attacker.ShortPrefabName}' - hard avoid, fleeing rather than fighting it.", safeDistanceOverride: HardAvoidAnimalRadius);
            return;
        }

        // Combat never starts inside a safe zone; the survivor simply declines to fight rather than fleeing.
        if (npc.InSafeZone() || attacker.InSafeZone())
        {
            _pendingResumeCombatTarget.Remove(characterId);
            return;
        }

        // Weapon check runs before cancelling movement/attack state, so an unarmed survivor's in-progress task isn't repeatedly interrupted by on-sight detection re-scanning it. TryEquipBestArmedWeapon picks the weapon to fight with; if nothing usable is owned, EquipBestWeaponForDisplay still equips something for display only.
        bool hasArmedWeapon = TryEquipBestArmedWeapon(survivor, out BaseProjectile equippedWeapon);

        if (!hasArmedWeapon)
        {
            EquipBestWeaponForDisplay(survivor);

            // Without a ranged weapon, an unarmed survivor still fights back in melee - against a player, bot, hostile scientist, or animal - if it has a real melee weapon. Reaching this point with an animal attacker and no real weapon can't happen: IsHardAvoidAnimal above already caught and resolved that case (fight-or-flight roll or flee).
            if (HasCombatReadyMeleeWeapon(npc))
            {
                StartMeleeCombat(survivor, attacker);
                _pendingResumeCombatTarget.Remove(characterId);
                return;
            }

            // Falls through to the flee branch for anything not worth fighting. Stag is excluded since it never attacks and should not trigger a flee reaction.
            if (IsHuntableThreat(attacker) && attacker is not Rust.Ai.Gen2.Stag)
            {
                StartFleeingFromThreat(survivor, attacker);
            }

            // Clears any dangling pending-resume state left by an interrupt that could not be completed.
            _pendingResumeCombatTarget.Remove(characterId);

            return;
        }

        CancelActiveMovement(survivor);
        CancelActiveAttack(characterId);
        CancelActiveRecycling(characterId);
        StopExtendedOreSearch(characterId);

        TaskType previousTask = survivor.Character.CurrentTask;

        VerbosePuts($"'{survivor.Character.Alias}' engaging '{GetAttackerDisplayName(attacker)}' in combat.");

        // Movement during combat is handled by StartFollowing, called once here and left to run on its own cadence. Facing while stopped is left to this file's own AimAtPlayer via skipIdleFacing, so the two don't fight over rotation.
        string weaponShortname = equippedWeapon.GetItem()?.info.shortname ?? string.Empty;
        WeaponFireProfile fireProfile = GetCombatFireProfile(weaponShortname);
        float engagementRange = fireProfile.EngagementRange;
        float pursueRange = fireProfile.PursueRange;

        // True for weapons that fire in bursts (fixed-burst weapons, or any profile with distance bands); continuous-fire weapons (pump/SPAS/double shotguns, snipers) use the periodic timer for gap-closing instead.
        bool isShotgunWeapon = ShotgunShortnames.Contains(weaponShortname);

        // Counter-snipe: a bot hit by a semi-auto or full-auto weapon can return fire at any distance. Shotguns and bolt-action snipers are excluded since they have no equivalent long-range burst rate.
        bool supportsLongRangeCounterFire = !isShotgunWeapon
            && weaponShortname != "rifle.l96"
            && weaponShortname != "rifle.bolt";

        // Shotguns are excluded from burst pacing since they are only effective at short range and should close distance aggressively instead.
        bool weaponUsesBurstPacing = !isShotgunWeapon
            && (weaponShortname == "m16a2"
                || weaponShortname == "pistol.prototype17"
                || (fireProfile.Bands != null && fireProfile.Bands.Length > 0));

        // Shotguns re-evaluate and advance on a shorter cadence than other weapons, to close distance quickly.
        float advanceIntervalSeconds = isShotgunWeapon ? ShotgunAdvanceIntervalSeconds : AdvanceIntervalSeconds;

        StartFollowing(survivor, attacker, engagementRange, skipIdleFacing: true);

        // Per-fight state for gradually advancing the hold distance toward the target over time.
        float currentHoldDistance = engagementRange;
        float nextAdvanceTime = Time.realtimeSinceStartup + advanceIntervalSeconds;

        // Set when a burst finishes firing, consumed each tick regardless of whether it was acted on.
        bool pendingPostBurstAdvance = false;

        // Last position the bot actually saw the attacker at while it had line of sight, so pushing closer while blind walks to the last known position rather than tracking through walls.
        Vector3 lastKnownPosition = attacker.transform.position;

        // The survivor's facing at the moment combat starts, captured before AimAtPlayer touches it, so the first shot's telegraph can measure how far it needs to turn.
        float preEngagementFacingYaw = npc.transform.rotation.eulerAngles.y;
        bool hasFiredFirstShotThisFight = false;
        float firstShotTelegraphEndsAt = -1f;

        // Ambush detection, checked once at the start of the fight against the survivor's pre-engagement facing, to trigger a larger first-volley reaction when attacked from outside its forward view.
        Vector3 rearCheckDirection = attacker.transform.position - npc.transform.position;
        rearCheckDirection.y = 0f;
        bool wasAttackedFromBehind = rearCheckDirection.sqrMagnitude > 0.01f
            && Vector3.Angle(npc.transform.forward, rearCheckDirection) >= RearAmbushAngleThreshold;
        bool hasFiredReactionBurstThisFight = false;

        // Tracks whether the previous tick had line of sight, so a change can be reacted to immediately rather than waiting for the periodic advance timer.
        bool wasLosLastTick = true;

        // Seeded as already-regained (zero delay) rather than the "not regaining" sentinel, so the very first combat tick can fire immediately if the attacker is already visible instead of rolling a full reaction-time delay.
        float losRegainedAt = Time.realtimeSinceStartup;
        float currentLosRegainDelay = 0f;

        // Last time line of sight on the attacker was confirmed; used to de-aggro if no real engagement happens within NoRealEngagementDisengageSeconds.
        float lastRealLosTime = Time.realtimeSinceStartup;

        // Per-fight burst-fire state, covering both the fixed-burst path and the distance-banded random-burst path. Rust's ServerUse() always fires exactly one shot, so multi-shot volleys are tracked manually.
        int burstShotsFired = 0;
        int burstTargetCount = 0;
        float nextBurstAllowedTime = 0f;

        // Tracks the last time the bot was sprinting; negative infinity means a fight that never needed to sprint does not wait out a settle window.
        float lastSprintingTime = float.NegativeInfinity;

        // Extra cooldown layered on top of the weapon's own fire-rate gate, for weapons whose real repeat delay still fires too fast. No-op for weapons not in WeaponFireRateMultiplier.
        float nextShotAllowedTime = 0f;

        // Routes every shot in this fight through the same pacing logic, so WeaponFireRateMultiplier applies uniformly regardless of which firing path is active.
        void FireWithPacing(BasePlayer shooterNpc, BaseProjectile shooterWeapon, BaseCombatEntity shooterTarget, float shooterDistance)
        {
            FireAt(shooterNpc, shooterWeapon, shooterTarget, weaponShortname, shooterDistance);

            float multiplier = GetFireRateMultiplier(weaponShortname, shooterDistance);

            if (multiplier > 1f)
            {
                nextShotAllowedTime = Time.realtimeSinceStartup + shooterWeapon.repeatDelay * (multiplier - 1f);
            }

            // Bows need a floor on time between shots, since the raw weapon cooldown does not model the draw/nock between arrows.
            if (BowMinShotIntervalSeconds.TryGetValue(weaponShortname, out float minInterval))
            {
                nextShotAllowedTime = Mathf.Max(nextShotAllowedTime, Time.realtimeSinceStartup + minInterval);
            }
        }

        // Applies a small, self-correcting lateral strafe so a bot doesn't stand still while firing or reloading. Perpendicular to the current direction toward the attacker, called from both the firing-hold and reload-exposure paths.
        void ApplyCombatJitter(BasePlayer jitterNpc, BaseCombatEntity jitterAttacker)
        {
            Vector3 towardAttackerForJitter = jitterAttacker.transform.position - jitterNpc.transform.position;
            towardAttackerForJitter.y = 0f;

            if (towardAttackerForJitter.sqrMagnitude <= 0.01f)
            {
                return;
            }

            Vector3 strafeAxis = Vector3.Cross(Vector3.up, towardAttackerForJitter.normalized);
            float jitterAngularFrequency = 2f * Mathf.PI / CombatJitterPeriodSeconds;
            float currentJitterPhase = Time.realtimeSinceStartup * jitterAngularFrequency;
            float previousJitterPhase = (Time.realtimeSinceStartup - CombatTickInterval) * jitterAngularFrequency;
            float jitterDelta = (Mathf.Sin(currentJitterPhase) - Mathf.Sin(previousJitterPhase)) * CombatJitterAmplitude;
            Vector3 jitteredPosition = jitterNpc.transform.position + strafeAxis * jitterDelta;
            jitterNpc.transform.position = jitteredPosition;
            jitterNpc.MovePosition(jitteredPosition);
        }

        Timer combatTimer = null;

        combatTimer = timer.Every(CombatTickInterval, () =>
        {
            BasePlayer currentNpc = survivor.Player;

            if (currentNpc == null || currentNpc.IsDestroyed || currentNpc.IsWounded())
            {
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            if (attacker == null || attacker.IsDestroyed || !attacker.IsAlive())
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - '{(attacker != null ? GetAttackerDisplayName(attacker) : "attacker")}' is gone.");
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            // Ends the fight the moment either combatant enters a safe zone mid-chase, mirroring StartCombat's own entry gate.
            if (currentNpc.InSafeZone() || attacker.InSafeZone())
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - safezone.");
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            float distance = Vector3.Distance(currentNpc.transform.position, attacker.transform.position);

            // Give-up distance is pursueRange rather than the shorter engagementRange, so the bot has a real chance to close the gap before disengaging. Animal targets never give up on distance, so a wounded, fleeing animal isn't lost mid-chase. supportsLongRangeCounterFire also bypasses this, so a bot being shot at from extreme range keeps fighting back.
            if (distance > pursueRange && !IsHuntablePredator(attacker) && !supportsLongRangeCounterFire)
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - '{GetAttackerDisplayName(attacker)}' got too far away ({distance:F0}m).");
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            // An armed bot fighting a predator does not get the low-health bail-out a player fight does; only animal targets skip this disengage check.
            if (currentNpc.health <= CombatFleeHealthThreshold && !IsHuntablePredator(attacker) && !_disableLowHealthDisengage)
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - too low on health ({currentNpc.health:F0}) to keep fighting.");
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            // Gives up if no line of sight on the attacker was confirmed for the whole disengage window, catching an attacker that's alive and in range but permanently unreachable.
            if (Time.realtimeSinceStartup - lastRealLosTime >= NoRealEngagementDisengageSeconds && !IsHuntablePredator(attacker))
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - no real shot at '{GetAttackerDisplayName(attacker)}' in {NoRealEngagementDisengageSeconds:F0}s (out of reach?).");
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            // Skips aim/fire/follow for the retreat's own duration while a reload retreat is active; other disengage checks still run.
            if (_combatRetreatUntilTime.TryGetValue(characterId, out float retreatUntilTime) && Time.realtimeSinceStartup < retreatUntilTime)
            {
                return;
            }

            // Skips aim/fire while mid-heal-animation, same shape as the retreat gate above.
            if (_healingUntilTime.TryGetValue(characterId, out float healingUntilTime) && Time.realtimeSinceStartup < healingUntilTime)
            {
                return;
            }

            // Aim/fire runs normally during a tactical retreat or flank; real cover naturally stops fire once LOS breaks, and the sprint-settle gate further down prevents firing while actively sprinting away.
            bool rawLos = HasCombatLineOfSight(currentNpc, attacker);

            if (rawLos)
            {
                lastKnownPosition = attacker.transform.position;
                lastRealLosTime = Time.realtimeSinceStartup;

                // Stops any hunt-search sweep still running so it doesn't fight StartFollowing for movement control.
                StopHuntSearch(characterId);

                if (losRegainedAt < 0f)
                {
                    losRegainedAt = Time.realtimeSinceStartup;

                    // Rolled once per regain, using the distance at the moment LOS came back.
                    currentLosRegainDelay = RollLosRegainDelay(distance);
                }
            }
            else
            {
                losRegainedAt = -1f;
            }

            // hasLos is the confirmed, reaction-delayed value actually acted on: losing LOS is instant, but regaining it holds off for currentLosRegainDelay so the reaction reads as human rather than instant.
            bool hasLos = rawLos && Time.realtimeSinceStartup - losRegainedAt >= currentLosRegainDelay;

            // Without LOS, disengage instead of blindly pushing toward the last known position if already standing in a known-bad monument avoid zone, since that push would walk back into the same terrain that blocked it.
            if (!hasLos && IsInMonumentAvoidZone(currentNpc.transform.position))
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - can't safely push toward '{GetAttackerDisplayName(attacker)}' without LOS through known-bad terrain here.");
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            // Tactical decision system: runs every tick regardless of hasLos, though it only rolls a fresh decision every couple of seconds internally. Returning immediately once a tactical action starts avoids a one-tick misread of the weapon swap a heal chain causes.
            if (TryStartTacticalRepositioning(survivor, currentNpc, attacker))
            {
                return;
            }

            // Reacts to a confirmed LOS change on the tick it happens, so regaining LOS mid-push doesn't leave the bot committed to blindly sprinting at the old position for the rest of the advance interval.
            bool losChanged = hasLos != wasLosLastTick;

            // Diagnostic logging of every LOS transition, to help verify a bot's aim/fire timing lines up with when it actually had line of sight on its target.
            if (losChanged)
            {
                VerbosePuts($"los-diag: '{survivor.Character.Alias}' hasLos {(hasLos ? "REGAINED" : "LOST")} vs '{GetAttackerDisplayName(attacker)}' at distance {distance:F1}m (rawLos={rawLos}, currentLosRegainDelay={currentLosRegainDelay:F2}s).");
            }

            // True once already in the closest/best-burst band, since closing further doesn't make the bot deadlier and only costs more time sprint-locked out of firing.
            bool alreadyInBestBand = fireProfile.Bands != null
                && fireProfile.Bands.Length > 0
                && distance <= fireProfile.Bands[0].MaxDistance;

            // Tracks sprinting state here so the periodic advance block below can use it too; a settle window after sprinting stops gives every distance tier a chance to fire before the bot advances further.
            if (currentNpc.modelState.sprinting)
            {
                lastSprintingTime = Time.realtimeSinceStartup;
            }

            bool settledLongEnoughToAdvance = Time.realtimeSinceStartup - lastSprintingTime >= SprintFireSettleSeconds;

            if (losChanged)
            {
                if (hasLos)
                {
                    StartFollowing(survivor, attacker, currentHoldDistance, skipIdleFacing: true);
                }
                else
                {
                    PushTowardLastKnownPosition(survivor, currentNpc, lastKnownPosition);
                }

                nextAdvanceTime = Time.realtimeSinceStartup + advanceIntervalSeconds;
                wasLosLastTick = hasLos;
            }
            else if (Time.realtimeSinceStartup >= nextAdvanceTime
                && currentHoldDistance > AdvanceMinimumDistance
                && (!hasLos || !weaponUsesBurstPacing)
                && !(hasLos && alreadyInBestBand)
                && settledLongEnoughToAdvance)
            {
                // Only shrinks the hold distance once the bot has settled at its current distance (not sprinting), so every distance tier gets a genuine chance to fire before pushing closer again. Skipped while LOS holds and the weapon fires in bursts, in favor of the smaller burst-reactive nudge below.
                float step = UnityEngine.Random.Range(AdvanceStepMinMeters, AdvanceStepMaxMeters);
                currentHoldDistance = Mathf.Max(AdvanceMinimumDistance, currentHoldDistance - step);

                if (hasLos)
                {
                    StartFollowing(survivor, attacker, currentHoldDistance, skipIdleFacing: true);
                }
                else
                {
                    PushTowardLastKnownPosition(survivor, currentNpc, lastKnownPosition);
                }

                nextAdvanceTime = Time.realtimeSinceStartup + advanceIntervalSeconds;
            }

            if (hasLos && pendingPostBurstAdvance && !alreadyInBestBand && currentHoldDistance > AdvanceMinimumDistance)
            {
                float postBurstStep = UnityEngine.Random.Range(PostBurstAdvanceStepMinMeters, PostBurstAdvanceStepMaxMeters);
                currentHoldDistance = Mathf.Max(AdvanceMinimumDistance, currentHoldDistance - postBurstStep);
                StartFollowing(survivor, attacker, currentHoldDistance, skipIdleFacing: true);

                // Avoids a redundant periodic nudge stacking immediately on top of the one just applied.
                nextAdvanceTime = Time.realtimeSinceStartup + advanceIntervalSeconds;
            }

            pendingPostBurstAdvance = false;

            if (!hasLos)
            {
                // Stays engaged rather than disengaging over a momentary LOS break, but doesn't fire blind; waits for a clear shot while closing the gap toward lastKnownPosition.
                SetAimingIn(currentNpc, false);
                return;
            }

            bool inEngagementRange = distance <= engagementRange * GetBiomeVisibilityFactor(currentNpc.transform.position, attacker.transform.position);

            // Runs every tick LOS is confirmed, regardless of sprinting, so aim tracks the target's true bearing continuously. Firing itself stays gated separately by SprintFireSettleSeconds below.
            AimAtPlayer(currentNpc, attacker);

            // Aim-in pose stays sprint-gated, since sprinting locks weapon use and the ADS pose shouldn't show while sprinting.
            SetAimingIn(currentNpc, !currentNpc.modelState.sprinting);

            if (currentNpc.GetHeldEntity() is not BaseProjectile weapon)
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - lost its ranged weapon.");
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            if (weapon.primaryMagazine.contents <= 0)
            {
                // Reloads by consuming matching ammo from the survivor's inventory automatically.
                weapon.ServerTryReload(currentNpc.inventory);

                if (weapon.primaryMagazine.contents > 0)
                {
                    // Reload succeeded after being fully dry; marks an exposure window covering the reload animation duration, so real incoming damage during it (handled in OnEntityTakeDamage) can trigger a retreat. An unopposed reload never retreats.
                    _reloadExposureWindowUntil[characterId] = Time.realtimeSinceStartup + weapon.reloadTime;

                    // Applies a bit of jitter during reload movement, since Rust allows reloading while moving unlike firing.
                    ApplyCombatJitter(currentNpc, attacker);
                    return;
                }

                // No matching ammo left for this weapon anywhere in inventory; swaps to the next best-scored weapon that still has usable ammo, recomputing every piece of fight state derived from the weapon.
                if (TryEquipBestArmedWeapon(survivor, out BaseProjectile nextWeapon))
                {
                    weaponShortname = nextWeapon.GetItem()?.info.shortname ?? string.Empty;
                    fireProfile = GetCombatFireProfile(weaponShortname);
                    engagementRange = fireProfile.EngagementRange;
                    pursueRange = fireProfile.PursueRange;
                    isShotgunWeapon = ShotgunShortnames.Contains(weaponShortname);
                    supportsLongRangeCounterFire = !isShotgunWeapon
                        && weaponShortname != "rifle.l96"
                        && weaponShortname != "rifle.bolt";
                    weaponUsesBurstPacing = !isShotgunWeapon
                        && (weaponShortname == "m16a2"
                            || weaponShortname == "pistol.prototype17"
                            || (fireProfile.Bands != null && fireProfile.Bands.Length > 0));
                    advanceIntervalSeconds = isShotgunWeapon ? ShotgunAdvanceIntervalSeconds : AdvanceIntervalSeconds;
                    currentHoldDistance = engagementRange;
                    burstShotsFired = 0;
                    burstTargetCount = 0;

                    VerbosePuts($"'{survivor.Character.Alias}' ran dry - switched to '{weaponShortname}'.");
                    return;
                }

                // Nothing owned has any ammo left; disengages and equips the best-effort display weapon instead of leaving the empty gun held.
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - no ammo left for anything it owns.");
                EquipBestWeaponForDisplay(survivor);
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            if (weapon.HasAttackCooldown())
            {
                // Real per-weapon fire-rate gate, same mechanism the melee loop relies on.
                return;
            }

            if (Time.realtimeSinceStartup < nextShotAllowedTime)
            {
                // Extra pacing on top of the real cooldown just checked, for weapons whose repeat delay alone still fires too fast.
                return;
            }

            if (!inEngagementRange && !supportsLongRangeCounterFire)
            {
                // Waits for StartFollowing to close the gap rather than firing on a target beyond the weapon's realistic engagement range.
                return;
            }

            // supportsLongRangeCounterFire lets a qualifying weapon fire through the engagement-range gate above; GetBurstBandForDistance already falls back to the furthest defined band beyond that range.

            if (Time.realtimeSinceStartup - lastSprintingTime < SprintFireSettleSeconds)
            {
                // A player cannot fire accurately while sprinting, so firing is skipped until the bot settles into a walk/stop plus a short settle window covering the sprint-to-stand transition.
                return;
            }

            // Applied once the survivor has genuinely settled (not sprinting, not mid-heal/retreat/telegraph).
            ApplyCombatJitter(currentNpc, attacker);

            // First-shot turn telegraph: holds the fight's first shot back proportional to how far the survivor needs to turn, nudging a tiny real step toward the attacker each tick so the client's rotation interpolation has genuine velocity to key off. Only runs once per fight; later shots rely on AimAtPlayer's per-tick tracking instead.
            if (!hasFiredFirstShotThisFight)
            {
                if (firstShotTelegraphEndsAt < 0f)
                {
                    float turnNeededDegrees = Mathf.Abs(Mathf.DeltaAngle(preEngagementFacingYaw, currentNpc.transform.rotation.eulerAngles.y));

                    float telegraphSeconds = turnNeededDegrees >= FirstShotTelegraphMinTurnDegrees
                        ? Mathf.Min(turnNeededDegrees / FirstShotTelegraphDegreesPerSecond, FirstShotTelegraphMaxSeconds)
                        : 0f;

                    firstShotTelegraphEndsAt = Time.realtimeSinceStartup + telegraphSeconds;

                    if (telegraphSeconds > 0f)
                    {
                        VerbosePuts($"'{survivor.Character.Alias}' turning {turnNeededDegrees:F0} deg to face '{GetAttackerDisplayName(attacker)}' before its first shot ({telegraphSeconds:F2}s telegraph).");
                    }
                }

                if (Time.realtimeSinceStartup < firstShotTelegraphEndsAt)
                {
                    Vector3 nudgeDirection = attacker.transform.position - currentNpc.transform.position;
                    nudgeDirection.y = 0f;

                    if (nudgeDirection.sqrMagnitude > 0.0001f)
                    {
                        Vector3 nudgedPosition = currentNpc.transform.position + nudgeDirection.normalized * FirstShotTelegraphNudgeDistance;
                        currentNpc.transform.position = nudgedPosition;
                        currentNpc.MovePosition(nudgedPosition);
                    }

                    return;
                }

                hasFiredFirstShotThisFight = true;
            }

            // M16A2/P17 dedicated path, gated by exact weapon shortname rather than a generic burst-capability flag, since other weapons could share that flag for unrelated reasons.
            bool isP17 = weaponShortname == "pistol.prototype17";
            bool isM16A2 = weaponShortname == "m16a2";

            // Which code path P17 takes is driven by this distance check rather than weapon.UsingBurstMode(), whose underlying flag doesn't reliably reflect fire mode. The flag is still toggled below as a best-effort cosmetic sync.
            bool p17InBurstRange = isP17 && distance <= P17BurstSwitchDistance;

            if (isP17)
            {
                bool wantBurst = p17InBurstRange;

                if (weapon.UsingBurstMode() != wantBurst)
                {
                    weapon.SetFlagLocal(BaseEntity.Flags.Reserved6, !weapon.HasFlag(BaseEntity.Flags.Reserved6));
                }
            }

            if (isM16A2 || p17InBurstRange)
            {
                // Fixed 3-round burst mechanic: the M16A2 always uses it, the P17 only within P17BurstSwitchDistance. Allows up to GetBurstModeCount() shots, then forces a pause before the next volley.
                if (burstShotsFired >= weapon.GetBurstModeCount())
                {
                    if (Time.realtimeSinceStartup < nextBurstAllowedTime)
                    {
                        return;
                    }

                    burstShotsFired = 0;
                }

                FireWithPacing(currentNpc, weapon, attacker, distance);
                burstShotsFired++;

                if (burstShotsFired >= weapon.GetBurstModeCount())
                {
                    nextBurstAllowedTime = Time.realtimeSinceStartup + weapon.TimeBetweenBursts();
                    pendingPostBurstAdvance = true;
                }

                return;
            }

            if (isP17)
            {
                // P17 beyond P17BurstSwitchDistance fires semi-auto in grouped bursts, reusing PistolProfile's distance bands the same as other pistols.
                (int p17MinBurst, int p17MaxBurst) = GetBurstBandForDistance(fireProfile, distance);

                if (burstShotsFired >= burstTargetCount)
                {
                    if (Time.realtimeSinceStartup < nextBurstAllowedTime)
                    {
                        return;
                    }

                    burstTargetCount = Mathf.Max(1, UnityEngine.Random.Range(p17MinBurst, p17MaxBurst + 1));
                    burstShotsFired = 0;
                }

                FireWithPacing(currentNpc, weapon, attacker, distance);
                burstShotsFired++;

                if (burstShotsFired >= burstTargetCount)
                {
                    // Uses P17's own pacing floor rather than the shared BurstPauseFloorSeconds.
                    nextBurstAllowedTime = Time.realtimeSinceStartup + Mathf.Max(weapon.repeatDelay * 2f, P17SingleFirePaceSeconds);
                    pendingPostBurstAdvance = true;
                }

                return;
            }

            // Distance-banded burst system used by every other ranged weapon. Re-reads the current live distance each time a new volley starts, so closing distance mid-fight gets a bigger burst on the next volley.
            (int minBurst, int maxBurst) = GetBurstBandForDistance(fireProfile, distance);

            if (minBurst < 0)
            {
                // Continuous fire with no volley/pause bookkeeping, for profiles with no bands or an explicitly continuous band.
                FireWithPacing(currentNpc, weapon, attacker, distance);
                burstShotsFired = 0;
                burstTargetCount = 0;
                return;
            }

            if (burstShotsFired >= burstTargetCount)
            {
                if (Time.realtimeSinceStartup < nextBurstAllowedTime)
                {
                    return;
                }

                // Starting a fresh volley. The pause between volleys is floored at BurstPauseFloorSeconds so fast automatics still get a perceptible gap rather than reading as continuous fire.
                if (wasAttackedFromBehind && !hasFiredReactionBurstThisFight)
                {
                    int reactionMin = Mathf.Min(minBurst + RearAmbushBurstBonusRounds, RearAmbushBurstCap);
                    int reactionMax = Mathf.Min(maxBurst + RearAmbushBurstBonusRounds, RearAmbushBurstCap);
                    burstTargetCount = UnityEngine.Random.Range(reactionMin, reactionMax + 1);
                    hasFiredReactionBurstThisFight = true;
                    VerbosePuts($"'{survivor.Character.Alias}' was hit from behind by '{GetAttackerDisplayName(attacker)}' - spinning and returning fire with a heavy burst ({burstTargetCount} rounds).");
                }
                else
                {
                    burstTargetCount = UnityEngine.Random.Range(minBurst, maxBurst + 1);
                }

                burstShotsFired = 0;
                nextBurstAllowedTime = Time.realtimeSinceStartup + Mathf.Max(weapon.repeatDelay * 2f, BurstPauseFloorSeconds);

                if (burstTargetCount == 0)
                {
                    // A valid roll for low-tier weapons whose longer-range bands allow zero rounds; nextBurstAllowedTime is already scheduled above.
                    return;
                }
            }

            FireWithPacing(currentNpc, weapon, attacker, distance);
            burstShotsFired++;

            if (burstShotsFired >= burstTargetCount)
            {
                pendingPostBurstAdvance = true;
            }
        });

        // Resets the damage-dealt tally when engaging a genuinely new attacker, so it reflects this fight rather than a previous one.
        if (!_activeCombatTarget.TryGetValue(characterId, out BaseCombatEntity previousAttacker) || previousAttacker != attacker)
        {
            ResetDamageDealtToCurrentAttacker(characterId);
        }

        _activeCombat[characterId] = combatTimer;
        _activeCombatTarget[characterId] = attacker;
    }

    // Pause between a fight ending and the survivor resuming its previous task, regardless of why the fight ended, so it doesn't snap immediately back to looting.
    private const float PostCombatSettleSeconds = 3f;

    /// <summary>
    /// Distance within which a still-alive, still-visible attacker postpones the post-combat reload, so a bot doesn't fumble with a mag change while the same threat is still nearby.
    /// </summary>
    private const float PostCombatReloadSafetyRadius = 30f;

    private const float PostCombatReloadSafetyRecheckInterval = 1f;

    /// <summary>
    /// Caps how long the post-combat reload can be deferred waiting for safety; after this many rechecks, reloads anyway rather than staying under-loaded indefinitely.
    /// </summary>
    private const int PostCombatReloadSafetyMaxRechecks = 8;

    /// <summary>
    /// Minimum delay before the first post-combat reload attempt, even with no visible threat, so a fresh threat within the first couple seconds doesn't catch the bot mid-reload with a stripped mag.
    /// </summary>
    private const float PostCombatReloadMinimumDelaySeconds = 4.5f;

    private bool IsUnsafeToReloadRightNow(BasePlayer npc, BaseCombatEntity lastAttacker)
    {
        return lastAttacker != null
            && !lastAttacker.IsDestroyed
            && lastAttacker.IsAlive()
            && Vector3.Distance(npc.transform.position, lastAttacker.transform.position) <= PostCombatReloadSafetyRadius
            && HasCombatLineOfSight(npc, lastAttacker);
    }

    /// <summary>
    /// Polls until the just-fought attacker is no longer an immediate threat, or the recheck cap is hit, then reloads. Does not block the survivor's task-resume timer; it only decides when reloading is safe.
    /// </summary>
    private void ReloadWhenSafe(Survivor survivor, BaseCombatEntity lastAttacker, int attempt)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
        {
            return;
        }

        if (attempt < PostCombatReloadSafetyMaxRechecks && IsUnsafeToReloadRightNow(npc, lastAttacker))
        {
            timer.Once(PostCombatReloadSafetyRecheckInterval, () => ReloadWhenSafe(survivor, lastAttacker, attempt + 1));
            return;
        }

        if (npc.GetHeldEntity() is BaseProjectile weapon && weapon.primaryMagazine.contents < weapon.primaryMagazine.capacity)
        {
            int contentsBefore = weapon.primaryMagazine.contents;
            weapon.ServerTryReload(npc.inventory);
            VerbosePuts($"reload-diag: '{survivor.Character.Alias}' deferred post-combat reload on '{weapon.GetItem()?.info.shortname}' (waited for safety, attempt {attempt}) - contents {contentsBefore} -> {weapon.primaryMagazine.contents} (capacity {weapon.primaryMagazine.capacity}).");
        }
    }

    private void EndCombat(Guid characterId, Timer combatTimer, Survivor survivor, TaskType previousTask)
    {
        BaseCombatEntity lastAttacker = _activeCombatTarget.TryGetValue(characterId, out BaseCombatEntity trackedAttacker) ? trackedAttacker : null;

        combatTimer.Destroy();
        _activeCombat.Remove(characterId);
        _activeCombatTarget.Remove(characterId);
        StopHuntSearch(characterId);

        // Stops the StartFollowing movement kicked off in StartCombat -
        // nothing else was tearing this down on disengage before, which
        // would have left a survivor still walking toward/holding near a
        // dead or fled attacker after combat itself had already ended.
        CancelActiveMovement(survivor);

        BasePlayer npc = survivor.Player;

        if (npc != null && !npc.IsDestroyed)
        {
            SetAimingIn(npc, false);
        }

        // Reload duration is only set when a reload actually fires below, to stretch the post-combat settle beat so the bot isn't seen walking off mid-reload-animation.
        float reloadDuration = 0f;

        if (npc != null && !npc.IsDestroyed && npc.GetHeldEntity() is BaseProjectile weapon && weapon.primaryMagazine.contents < weapon.primaryMagazine.capacity)
        {
            // Only reloads if there's an actual gap to fill, same as the mid-fight reload check. Always reloads asynchronously after PostCombatReloadMinimumDelaySeconds and an IsUnsafeToReloadRightNow safety check, rather than reloading immediately in the open.
            VerbosePuts($"'{survivor.Character.Alias}' will reload once it's had a moment to settle.");
            timer.Once(PostCombatReloadMinimumDelaySeconds, () => ReloadWhenSafe(survivor, lastAttacker, 0));
        }

        // A player fight interrupted by an animal resumes immediately here, bypassing PostCombatSettleSeconds and the loot-resume path, re-establishing combat against the player fresh.
        if (_pendingResumeCombatTarget.TryGetValue(characterId, out BaseCombatEntity pendingTarget))
        {
            _pendingResumeCombatTarget.Remove(characterId);

            if (pendingTarget != null
                && !pendingTarget.IsDestroyed
                && pendingTarget is BasePlayer pendingPlayer
                && pendingPlayer.IsAlive()
                && survivor.Player != null
                && !survivor.Player.IsDestroyed
                && survivor.Character.State != CharacterState.Dead)
            {
                StartCombat(survivor, pendingTarget);
                return;
            }
        }

        // Always resumes a productive task after combat, regardless of previousTask, so a survivor that ends up with no task assigned doesn't get stranded idle indefinitely.
        if (survivor.Player == null
            || survivor.Player.IsDestroyed
            || survivor.Character.State == CharacterState.Dead)
        {
            return;
        }

        float settleDelay = Mathf.Max(PostCombatSettleSeconds, reloadDuration);

        timer.Once(settleDelay, () =>
        {
            // Re-checked after the pause, since a fresh attacker, death, or plugin reload can occur during it. Guards against stomping a new fight that started during the pause.
            if (survivor.Player == null
                || survivor.Player.IsDestroyed
                || survivor.Character.State == CharacterState.Dead
                || _activeCombat.ContainsKey(characterId))
            {
                return;
            }

            // Records when this fight ended so ResumeOrStartLootTask can hold off before retrying movement, avoiding walking straight back into the same attacker.
            _lastCombatEndTime[characterId] = Time.realtimeSinceStartup;

            // Resumes a recycler task already in progress before the fight, rather than always starting a fresh one.
            ResumeOrStartLootTask(survivor);
        });
    }

    /// <summary>
    /// Toggles the shared ModelState.aiming flag and broadcasts it via SendModelState(true) so other clients see the aim pose change. No-ops if the flag already matches, so it can be called every combat tick safely.
    /// </summary>
    private void SetAimingIn(BasePlayer npc, bool aiming)
    {
        if (npc.modelState.aiming == aiming)
        {
            return;
        }

        npc.modelState.aiming = aiming;
        npc.SendModelState(true);
    }

    private void AimAtPlayer(BasePlayer npc, BaseCombatEntity target)
    {
        Vector3 direction = GetAimPoint(target) - npc.eyes.position;

        if (direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Quaternion aimRotation = Quaternion.LookRotation(direction);

        npc.transform.rotation = Quaternion.Euler(0f, aimRotation.eulerAngles.y, 0f);
        npc.OverrideViewAngles(aimRotation.eulerAngles);
        npc.eyes.NetworkUpdate(aimRotation);

        // Diagnostic logging of the facing yaw pushed to observers on this aim tick, for verifying visual aim updates line up with real fire events.
        VerbosePuts($"aim-diag: '{npc.displayName}' AimAtPlayer -> facing yaw {aimRotation.eulerAngles.y:F1} deg toward '{GetAttackerDisplayName(target)}'.");

        // Sets tickViewAngles directly, since the weapon-aim IK is driven by that field rather than the plain viewAngles set above, and a disconnected bot never populates it via a client RPC.
        SetTickViewAngles(npc, aimRotation.eulerAngles);

        // Pushes an immediate network update, since GetNetworkRotation() reads viewAngles and nothing else broadcasts the updated value to observers.
        npc.SendNetworkUpdateImmediate();
    }

    /// <summary>
    /// Fires the weapon at the target using an explicit origin/direction override rather than relying on the owner's eyes/viewAngles, since those don't reliably update for a disconnected bot. Keeps PvP-scaled damage rather than NPC-scaled.
    /// </summary>
    private void FireAt(BasePlayer npc, BaseProjectile weapon, BaseCombatEntity target, string weaponShortname, float distance)
    {
        weapon.useOwnerForward = false;

        Vector3 origin = npc.eyes.position;
        Vector3 aimPoint = GetAimPoint(target);
        Vector3 perfectDirection = aimPoint - origin;

        if (Array.IndexOf(NonCombatCapableRangedWeaponShortnames, weaponShortname) >= 0)
        {
            float flightSeconds = distance / ArrowMuzzleSpeed;
            float trueDrop = 0.5f * ArrowGravity * flightSeconds * flightSeconds;
            perfectDirection.y -= trueDrop * (1f - UnityEngine.Random.Range(ArrowCompensationMin, 1f));
        }

        // Applies distance-scaled spread via the same AimConeUtil math Rust's own ServerUse uses internally, fed a distance-aware angle instead of the flat default.
        float spreadDegrees = GetWeaponSpreadDegrees(weaponShortname, distance);
        Vector3 fireDirection = spreadDegrees > 0f
            ? AimConeUtil.GetModifiedAimConeDirection(spreadDegrees, perfectDirection)
            : perfectDirection;

        Matrix4x4 originOverride = Matrix4x4.TRS(origin, Quaternion.LookRotation(fireDirection), Vector3.one);

        // Logs the real fire direction (what the shot actually uses) alongside the model's current visual facing, for diagnosing any gap between the two.
        float realFireYaw = Quaternion.LookRotation(fireDirection).eulerAngles.y;
        float visualFacingYaw = npc.transform.rotation.eulerAngles.y;
        VerbosePuts($"fire-diag: '{npc.displayName}' fired '{weaponShortname}' at '{GetAttackerDisplayName(target)}' ({distance:F1}m) - real fire yaw {realFireYaw:F1} deg, visual facing yaw {visualFacingYaw:F1} deg (delta {Mathf.DeltaAngle(realFireYaw, visualFacingYaw):F1} deg).");

        weapon.ServerUse(new HeldEntityServerUseParams(1f, 1f, originOverride, useBulletThickness: true, useProtectionForNPCs: false));

        // Applies weapon condition loss directly, since ServerUse() alone does not apply it for a bot that never sends the client fire RPC.
        weapon.UpdateItemCondition();
    }

    private void CancelActiveCombat(Guid characterId)
    {
        if (_activeCombat.TryGetValue(characterId, out Timer combatTimer))
        {
            combatTimer.Destroy();
            _activeCombat.Remove(characterId);
            _activeCombatTarget.Remove(characterId);
        }
    }

    // How far each individual flee leg runs, re-issued on every FleeReassessIntervalSeconds tick using the threat's current position.
    private const float FleeRunDistance = 15f;

    // Flee tuning for hostile scientists, used by StartFleeingFromThreat.
    private const float ScientistFleeMinDistance = 50f;
    private const float ScientistFleeMaxDistance = 100f;
    private const float ScientistFleePoisonRadius = 40f;
    private const float ScientistFleePoisonDurationSeconds = 300f;

    // Distance at which a fleeing survivor is considered safely clear of a threat and resumes its previous task. Kept large enough to clear on-sight detection range with margin, avoiding an immediate re-detect/re-flee loop.
    private const float FleeSafeDistance = 20f;

    // How often the flee escape heading is recalculated against the threat's current position, so a fast pursuer can't cut the corner on a stale straight-line escape.
    private const float FleeReassessIntervalSeconds = 0.75f;

    // Safety cap so a survivor cornered against terrain/water doesn't run in place forever; a normal chase ends via FleeSafeDistance well before this.
    private const float FleeMaxDurationSeconds = 20f;

    /// <summary>
    /// Starts a survivor fleeing from a threat it can't fight, mirroring StartCombat's task-cancel/remember/resume shape but ending in flight: repeatedly runs from the threat's live position rather than a single one-shot walk.
    /// </summary>
    private void StartFleeingFromThreat(Survivor survivor, BaseCombatEntity threat, string reason = null, float? safeDistanceOverride = null)
    {
        Guid characterId = survivor.Character.Id;

        if (_activeFlee.ContainsKey(characterId))
        {
            // Already fleeing; the existing timer already re-evaluates the live threat position each tick.
            return;
        }

        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || threat == null || threat.IsDestroyed)
        {
            return;
        }

        CancelActiveMovement(survivor);
        CancelActiveAttack(characterId);
        CancelActiveRecycling(characterId);
        StopExtendedOreSearch(characterId);

        TaskType previousTask = survivor.Character.CurrentTask;
        float fleeStartedAt = Time.realtimeSinceStartup;

        // Lets a call site widen the safe-clear distance beyond the default (e.g. for a BradleyAPC's much larger danger radius), so the flee doesn't end still within the threat's own detection range.
        float safeDistance = safeDistanceOverride ?? FleeSafeDistance;

        // An unarmed survivor fleeing a hostile scientist runs 50-100m clear and marks the spot off-limits for itself for a few minutes, since a short flee just gets it shot in the back.
        bool scientistFlee = safeDistanceOverride == null && IsHostileScientist(threat);

        if (scientistFlee)
        {
            safeDistance = UnityEngine.Random.Range(ScientistFleeMinDistance, ScientistFleeMaxDistance);
        }

        // reason lets a call site override the default message, since not every flee case (e.g. an armed bot fleeing a BradleyAPC) fits the default unarmed wording.
        VerbosePuts(reason ?? $"'{survivor.Character.Alias}' has no weapon to fight off '{GetAttackerDisplayName(threat)}' - fleeing.");

        // Marks the area around the flee as poisoned so the resumed loot task doesn't immediately walk back into the same danger.
        if (scientistFlee)
        {
            PoisonAreaFromThreatFlee(survivor, npc.transform.position, ScientistFleePoisonRadius, ScientistFleePoisonDurationSeconds);
        }
        else
        {
            PoisonAreaFromThreatFlee(survivor, npc.transform.position);
        }

        // Flees toward real cover when available via TryFindCoverPoint, always in desperate mode since an unarmed or outmatched survivor has no better option. Re-evaluated every reassess tick as new cover comes into range.
        void RunAwayFromThreat(BasePlayer fleeingNpc)
        {
            if (TryFindCoverPoint(fleeingNpc, threat, out Vector3 coverPoint, desperate: true))
            {
                VerbosePuts($"'{survivor.Character.Alias}' is fleeing toward real cover from '{GetAttackerDisplayName(threat)}'.");
                StartWalkingWithRecovery(survivor, coverPoint, onArrived: null, onFailed: null);
                return;
            }

            Vector3 away = fleeingNpc.transform.position - threat.transform.position;
            away.y = 0f;

            // Picks an arbitrary escape direction when directly on top of the threat, rather than dividing by near-zero.
            if (away.sqrMagnitude < 0.01f)
            {
                away = UnityEngine.Random.insideUnitSphere;
                away.y = 0f;
            }

            Vector3 fleeTarget = fleeingNpc.transform.position + away.normalized * FleeRunDistance;
            StartWalking(survivor, fleeTarget);
        }

        RunAwayFromThreat(npc);

        Timer fleeTimer = null;

        fleeTimer = timer.Every(FleeReassessIntervalSeconds, () =>
        {
            BasePlayer currentNpc = survivor.Player;

            if (currentNpc == null || currentNpc.IsDestroyed || currentNpc.IsWounded() || survivor.Character.State == CharacterState.Dead)
            {
                // Logs which specific reason ended the flee, for diagnostics.
                VerbosePuts($"'{survivor.Character.Alias}' flee ended (npc gone/wounded/dead) after {Time.realtimeSinceStartup - fleeStartedAt:F0}s.");
                EndFlee(characterId, fleeTimer, survivor, previousTask);
                return;
            }

            // Fights instead of continuing to flee if a real weapon was obtained mid-flight, re-entering through StartCombat's normal armed path.
            if (currentNpc.GetHeldEntity() is BaseProjectile)
            {
                EndFlee(characterId, fleeTimer, survivor, previousTask);
                StartCombat(survivor, threat);
                return;
            }

            if (threat == null || threat.IsDestroyed || !threat.IsAlive())
            {
                // threat may itself be the null/destroyed case reported here, so GetAttackerDisplayName isn't safe to call unconditionally.
                string threatLabel = threat != null && !threat.IsDestroyed ? GetAttackerDisplayName(threat) : "threat";
                VerbosePuts($"'{survivor.Character.Alias}' flee ended ('{threatLabel}' gone/dead) after {Time.realtimeSinceStartup - fleeStartedAt:F0}s.");
                EndFlee(characterId, fleeTimer, survivor, previousTask);
                return;
            }

            float distance = Vector3.Distance(currentNpc.transform.position, threat.transform.position);

            if (distance >= safeDistance)
            {
                VerbosePuts($"'{survivor.Character.Alias}' is safely clear of '{GetAttackerDisplayName(threat)}' ({distance:F0}m) - resuming.");
                EndFlee(characterId, fleeTimer, survivor, previousTask);
                return;
            }

            if (Time.realtimeSinceStartup - fleeStartedAt >= FleeMaxDurationSeconds)
            {
                VerbosePuts($"'{survivor.Character.Alias}' has been fleeing '{GetAttackerDisplayName(threat)}' for {FleeMaxDurationSeconds:F0}s - giving up on the chase.");
                EndFlee(characterId, fleeTimer, survivor, previousTask);
                return;
            }

            RunAwayFromThreat(currentNpc);
        });

        _activeFlee[characterId] = fleeTimer;
    }

    private void EndFlee(Guid characterId, Timer fleeTimer, Survivor survivor, TaskType previousTask)
    {
        fleeTimer.Destroy();
        _activeFlee.Remove(characterId);

        CancelActiveMovement(survivor);

        // Always resumes a productive task after fleeing, regardless of previousTask, same reasoning as EndCombat.
        if (survivor.Player == null
            || survivor.Player.IsDestroyed
            || survivor.Character.State == CharacterState.Dead)
        {
            return;
        }

        // Records the flee end time, same reasoning as EndCombat's identical line.
        _lastCombatEndTime[characterId] = Time.realtimeSinceStartup;

        // Resumes an already-started task rather than abandoning it, same as EndCombat.
        ResumeOrStartLootTask(survivor);
    }

    private void CancelActiveFlee(Guid characterId)
    {
        if (_activeFlee.TryGetValue(characterId, out Timer fleeTimer))
        {
            fleeTimer.Destroy();
            _activeFlee.Remove(characterId);
        }
    }

    private void CancelPendingResumeCombatTarget(Guid characterId)
    {
        _pendingResumeCombatTarget.Remove(characterId);
    }

    // ============================================================
    // Tactical decision-making, Piece 1: cover-point discovery.
    // ============================================================

    /// <summary>
    /// Cover means solid geometry physically blocking the line of sight between the survivor and the attacker's eyes, checked via physics raycast against real Rust geometry rather than any object-type recognition. Distances are checked nearest-first, angles checked straight-away-from-attacker first.
    /// </summary>
    private static readonly float[] CoverSearchDistances = { 5f, 8f, 12f };

    /// <summary>
    /// Forward-biased 180-degree arc centered on directly away from the attacker, so a bot doesn't turn its back on the attacker to reach cover.
    /// </summary>
    private static readonly float[] CoverSearchAngleOffsets = { 0f, 30f, -30f, 60f, -60f, 90f, -90f };

    /// <summary>
    /// Full 360-degree fan used only in desperate mode (critically low health), since a bot about to die has nothing to lose by turning its back on the attacker to break line of sight.
    /// </summary>
    private static readonly float[] CoverSearchAngleOffsetsDesperate = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f };

    /// <summary>
    /// Rejects a cover candidate whose ground height differs from the survivor's current height by more than this, so candidates stay reachable by ordinary ground movement rather than an unreachable cliff top or boulder crown.
    /// </summary>
    private const float CoverMaxVerticalDelta = 4f;

    /// <summary>
    /// Backs a found cover point off slightly toward the survivor's current position, so it doesn't end up standing flush against the blocking geometry.
    /// </summary>
    private const float CoverStandoffDistance = 0.5f;

    /// <summary>
    /// Max distance worth traveling for cover. The live fan search already stays within this by construction; this caps the monument-cache lookup, which has no natural distance ceiling of its own.
    /// </summary>
    private const float CoverMaxUsefulDistance = 20f;

    /// <summary>
    /// Max angle from directly away from the attacker a cover point can sit at for the monument-cache lookup, matching the effective arc of CoverSearchAngleOffsets.
    /// </summary>
    private const float CoverMaxDirectionAngle = 100f;

    /// <summary>
    /// Cover means a majority of the body shielded, not a fully unbroken sightline. Checked via two linecasts at proportional body-height fractions (low and mid); both must be blocked, while the eye-height sightline itself is not required to be blocked.
    /// </summary>
    private const float CoverPartialLowHeightFraction = 0.2f;

    private const float CoverPartialMidHeightFraction = 0.6f;

    private bool IsPartialCoverPoint(Vector3 groundPoint, Vector3 attackerEyePos, float eyeHeight)
    {
        Vector3 lowPoint = groundPoint + Vector3.up * (eyeHeight * CoverPartialLowHeightFraction);
        Vector3 midPoint = groundPoint + Vector3.up * (eyeHeight * CoverPartialMidHeightFraction);

        bool lowBlocked = Physics.Linecast(lowPoint, attackerEyePos, out RaycastHit _, CombatLineOfSightBlockingMask, QueryTriggerInteraction.Ignore);
        bool midBlocked = Physics.Linecast(midPoint, attackerEyePos, out RaycastHit _, CombatLineOfSightBlockingMask, QueryTriggerInteraction.Ignore);

        return lowBlocked && midBlocked;
    }

    /// <summary>
    /// Finds a nearby cover point relative to an attacker. desperate relaxes only the direction/angle restriction; geometry validation and the distance cap are unchanged.
    /// </summary>
    private bool TryFindCoverPoint(BasePlayer npc, BaseCombatEntity attacker, out Vector3 coverPoint, bool desperate = false)
    {
        coverPoint = default;

        if (npc == null || npc.IsDestroyed || attacker == null || attacker.IsDestroyed)
        {
            return false;
        }

        // Prefers the pre-scanned monument cache when near a known monument, falling through to the live fan search if none is nearby or it has no cached points.
        if (TryGetNearestMonumentForCover(npc.transform.position, out MonumentInfo nearestMonument)
            && TryFindCoverPointNearMonument(npc, attacker, nearestMonument, out Vector3 monumentCoverPoint, desperate))
        {
            coverPoint = monumentCoverPoint;
            return true;
        }

        Vector3 selfPos = npc.transform.position;
        Vector3 attackerEyePos = GetAimPoint(attacker);
        float eyeHeight = npc.eyes.position.y - selfPos.y;

        Vector3 awayFromAttacker = selfPos - attackerEyePos;
        awayFromAttacker.y = 0f;

        if (awayFromAttacker.sqrMagnitude < 0.01f)
        {
            // Falls back to the survivor's current facing when standing right on top of the attacker, rather than dividing by near-zero.
            awayFromAttacker = npc.transform.forward;
            awayFromAttacker.y = 0f;
        }

        awayFromAttacker.Normalize();

        float[] angleOffsets = desperate ? CoverSearchAngleOffsetsDesperate : CoverSearchAngleOffsets;

        foreach (float distance in CoverSearchDistances)
        {
            foreach (float angleOffset in angleOffsets)
            {
                Vector3 direction = Quaternion.Euler(0f, angleOffset, 0f) * awayFromAttacker;
                Vector3 candidateXZ = selfPos + direction * distance;

                if (!_engine.NavigationManager.TryFindGroundBelow(candidateXZ + Vector3.up * 4f, 4f, 8f, out float groundY, out _))
                {
                    // No real ground found near this candidate at all (off
                    // the map, over a cliff edge, mid-air) - not a viable
                    // retreat point regardless of LOS.
                    continue;
                }

                if (Mathf.Abs(groundY - selfPos.y) > CoverMaxVerticalDelta)
                {
                    // Ground exists, but is not reachable by walking from the survivor's current elevation.
                    continue;
                }

                Vector3 groundPoint = new Vector3(candidateXZ.x, groundY, candidateXZ.z);

                if (_engine.NavigationManager.IsBodyOverlapping(groundPoint))
                {
                    // Solid geometry occupies the candidate spot itself, so the survivor can't actually stand there.
                    continue;
                }

                if (IsPartialCoverPoint(groundPoint, attackerEyePos, eyeHeight))
                {
                    // Blocks a majority of the body; backed off slightly toward the survivor's current position rather than standing flush against the blocking geometry.
                    float standoffFraction = Mathf.Clamp01(1f - CoverStandoffDistance / distance);
                    coverPoint = Vector3.Lerp(selfPos, groundPoint, standoffFraction);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// How close to lastKnownPosition counts as arrived for the hunt-search behavior, allowing for ordinary ground-navigation slop.
    /// </summary>
    private const float HuntSearchArrivalDistance = 5f;

    /// <summary>
    /// Radius the hunt-search sweep walks to random points within, once at the last known position, so the bot looks like it's actively searching rather than standing still.
    /// </summary>
    private const float HuntSearchRadius = 20f;

    /// <summary>
    /// Whether a survivor currently has a hunt-search loop actively chaining itself. Guards against starting a second loop, and is checked before each new leg. Cleared when LOS is regained or combat ends.
    /// </summary>
    private readonly Dictionary<Guid, bool> _huntSearchActive = new();

    /// <summary>
    /// A blind push toward a lost attacker's last known position, opportunistically routing through a monument cover-point cache when nearby. Falls through to a plain walk otherwise.
    ///
    /// Once genuinely at lastKnownPosition with still no LOS, hands off to a self-sustaining hunt-search loop (ContinueHuntSearch) instead of standing still.
    /// </summary>
    private void PushTowardLastKnownPosition(Survivor survivor, BasePlayer npc, Vector3 lastKnownPosition)
    {
        Guid characterId = survivor.Character.Id;

        if (Vector3.Distance(npc.transform.position, lastKnownPosition) <= HuntSearchArrivalDistance)
        {
            if (!_huntSearchActive.TryGetValue(characterId, out bool active) || !active)
            {
                _huntSearchActive[characterId] = true;
                VerbosePuts($"'{survivor.Character.Alias}' reached the last place it saw its target and starts searching the area.");
                ContinueHuntSearch(survivor, lastKnownPosition);
            }

            return;
        }

        if (TryGetNearestMonumentForCover(npc.transform.position, out MonumentInfo monument)
            && TryFindCoverPointAlongPath(npc.transform.position, lastKnownPosition, monument, out Vector3 waypoint))
        {
            StartWalking(survivor, waypoint, onArrived: () => StartWalking(survivor, lastKnownPosition), onFailed: () => StartWalking(survivor, lastKnownPosition));
            return;
        }

        StartWalking(survivor, lastKnownPosition);
    }

    /// <summary>
    /// One leg of the hunt-search sweep: walks to a fresh random point within HuntSearchRadius of the original anchor position, chaining into the next leg until _huntSearchActive says to stop.
    /// </summary>
    private void ContinueHuntSearch(Survivor survivor, Vector3 anchor)
    {
        Guid characterId = survivor.Character.Id;

        if (!_huntSearchActive.TryGetValue(characterId, out bool active) || !active)
        {
            return;
        }

        Vector2 randomOffset = UnityEngine.Random.insideUnitCircle * HuntSearchRadius;
        Vector3 searchPoint = anchor + new Vector3(randomOffset.x, 0f, randomOffset.y);

        if (_engine.NavigationManager.TryFindGroundBelow(searchPoint + Vector3.up * 4f, 4f, 8f, out float groundY, out _))
        {
            searchPoint = new Vector3(searchPoint.x, groundY, searchPoint.z);
        }

        StartWalkingWithRecovery(survivor, searchPoint,
            onArrived: () => ContinueHuntSearch(survivor, anchor),
            onFailed: () => ContinueHuntSearch(survivor, anchor));
    }

    private void StopHuntSearch(Guid characterId)
    {
        _huntSearchActive.Remove(characterId);
    }
}
