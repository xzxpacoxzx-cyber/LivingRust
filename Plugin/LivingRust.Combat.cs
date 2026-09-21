using System;
using System.Collections.Generic;
using System.Linq;
using Facepunch;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Phase 1 combat, deliberately narrow (Lucas's own explicit sequencing,
/// 2026-08-11): ranged weapons only (pistols/SMGs/rifles - bows/crossbows/
/// melee-vs-players/thrown explosives are all later work), fixed-rule
/// disengage (no Personality/aggression traits exist yet to drive this
/// properly). The real unknown this phase exists to answer live is
/// ammunition - can a survivor genuinely pull ammo from its own inventory
/// and reload mid-fight using Rust's real mechanics, not a scripted
/// shortcut.
///
/// Originally reactive-only (fight back once shot, no on-sight detection) -
/// on-sight detection added 2026-08-14 (Lucas's own explicit request), see
/// OnSightDetectionRange's own doc comment. Real players only for now -
/// bot-vs-bot on-sight detection is explicitly deferred ("players and bots
/// later").
///
/// Deliberately does NOT port Rust's own Scientist2FSM/NpcShootingComponent -
/// confirmed via decompile that NpcShootingComponent creates its weapon
/// directly (ItemManager.Create -> SetParent on a hand bone), bypassing
/// player.inventory/belt entirely, which is exactly the ammo/inventory
/// disconnect this phase needs to avoid. The only piece actually reused from
/// Rust's own combat plumbing is the real weapon-firing primitive,
/// BaseProjectile.ServerUse() - the same HeldEntity.ServerUse() entry point
/// BaseMelee.ServerUse() already uses for melee (see StartAttackingContainer
/// in LivingRust.Looting.cs) - plus the real, inventory-aware
/// BaseProjectile.ServerTryReload(IAmmoContainer) reload method (confirmed
/// via decompile: PlayerInventory implements IAmmoContainer directly, and
/// ServerTryReload internally finds/consumes matching ammo itself - no
/// manual PlayerInventory.FindAmmo bookkeeping needed on this end at all).
/// </summary>
public partial class LivingRust
{
    private readonly Dictionary<Guid, Timer> _activeCombat = new();

    // Debug/trace visibility only (2026-08-13) - lets the trace CSV log
    // what a survivor is actually fighting and how far away, so a "why did
    // this bot sprint the whole fight instead of firing" question can be
    // answered from the trace alone instead of guessing whether it was
    // still closing initial distance, chasing a fast-moving real player, or
    // stuck on something else entirely.
    private readonly Dictionary<Guid, BaseCombatEntity> _activeCombatTarget = new();

    // The player a survivor was fighting before an animal interrupted it -
    // see StartCombat's retarget branch and EndCombat's own doc comment for
    // the full "disengage the player, kill the animal, re-engage the
    // player as quick as possible" flow (Lucas's own explicit spec,
    // 2026-08-14). Only ever holds an entry for the duration of the
    // animal fight that interrupted it - consumed and removed the instant
    // that fight ends, one way or another.
    private readonly Dictionary<Guid, BaseCombatEntity> _pendingResumeCombatTarget = new();

    // Animal interaction (2026-08-14) - Bear/Polarbear/Wolf2/Boar/Crocodile.
    // Reactive (got attacked) always applies; on-sight (see
    // AnimalOnSightRange below) applies only while armed, per Lucas's own
    // explicit 2026-08-14 spec: "if the bot is within 20 metres of either a
    // polar bear, bear, wolf2, boar or crocodile and it is armed, the bot
    // just attempts to kill it at any cost." BaseAnimalNPC/BaseNPC2 both
    // share BaseCombatEntity as a common ancestor with BasePlayer (confirmed
    // via decompile), which is why StartCombat/AimAtPlayer/FireAt/
    // HasCombatLineOfSight below are typed against BaseCombatEntity rather
    // than BasePlayer specifically - the exact same fire-profile/spread/
    // pacing system now runs unmodified against either kind of attacker,
    // real player or animal.
    private readonly Dictionary<Guid, Timer> _activeFlee = new();

    // Flat range for animal on-sight detection - separate from
    // OnSightDetectionRange (125f, real players only) since this is a much
    // closer "it's right there, deal with it" trigger rather than a long-
    // range spot-and-engage. Lowered from 30f to 10f (2026-08-15, Lucas's
    // own explicit request: "the 30ish metres is really messing with the
    // bots' thought process") - at 30-50m a bot passing anywhere near a
    // monument/open-world animal spotted it well before it was a real
    // threat, triggering flee reactions (or, for armed bots, engage
    // decisions) constantly rather than only when something's genuinely
    // close and imminent.
    private const float AnimalOnSightRange = 10f;

    /// <summary>
    /// Any animal this project treats as a valid combat target - originally
    /// predators only (fight if armed, flee if not), widened 2026-08-14 to
    /// also include Stag (deer) per explicit request: "implement this for
    /// non-aggressive animals also (except chickens)." Stag never attacks a
    /// survivor itself, so it only ever reaches this via the armed on-sight
    /// path (TryStartAnimalOnSightCombat) - the reactive/flee paths simply
    /// never trigger for it, same code, no animal-specific branching
    /// needed. Chicken deliberately excluded (explicit exception) - still
    /// out of scope, same as every other passive/farmable animal not
    /// listed here. Two distinct real class hierarchies (confirmed via
    /// decompile): Bear/Polarbear/Boar/Stag are legacy BaseAnimalNPC,
    /// Wolf2/Crocodile/Panther/Tiger (Panther/Tiger added 2026-08-14, same
    /// explicit "aggressive animals the bot should attack" request) are the
    /// newer Gen2 Rust.Ai.Gen2.BaseNPC2 - both ultimately reach
    /// BaseCombatEntity, which is all this project's combat system
    /// actually needs.
    /// </summary>
    private static bool IsHuntablePredator(BaseEntity entity)
    {
        return entity is Bear
            || entity is Polarbear
            || entity is Boar
            || entity is Stag
            || entity is Rust.Ai.Gen2.Wolf2
            || entity is Rust.Ai.Gen2.Crocodile
            || entity is Rust.Ai.Gen2.Panther
            || entity is Rust.Ai.Gen2.Tiger;
    }

    // Flat range for scientist NPC on-sight detection - "separate to
    // aggressive animals" (own category, own range) even though it shares
    // the same underlying combat/flee mechanics. Lowered from 50f to 10f
    // same session as AnimalOnSightRange, same reasoning - a bot noticing a
    // scientist from 50m out and reacting immediately read as too jumpy/
    // premature; 10f means it only reacts once genuinely close.
    private const float ScientistOnSightRange = 10f;

    /// <summary>
    /// Every real scientist NPC prefab is confirmed via decompile to be one
    /// of exactly two C# classes: legacy `ScientistNPC : HumanNPC :
    /// NPCPlayer : BasePlayer` (oilrig/junkpile-era prefabs) or the newer
    /// `Rust.Ai.Gen2.ScientistNPC2 : BaseNPC2` (the scientistnpc_* monument-
    /// guard family) - both reach BaseCombatEntity, same as every other
    /// target type this project fights. A type check alone would ALSO
    /// match two genuinely non-hostile scientist prefabs though (Lucas's
    /// own follow-up, 2026-08-14: "remove any passive scientists that
    /// don't usually fight the bot/player... any in safe zones etc") -
    /// PassiveScientistPrefabPrefixes filters those back out by their real
    /// prefab shortname. NOTE: this exclusion list is based on general
    /// Rust game knowledge (Arena is the Bandit Camp 1v1 official/referee,
    /// Peacekeeper only intervenes against already-hostile players and
    /// sits in a safe zone), NOT independently confirmed via decompiled AI/
    /// aggro logic the way the class hierarchy above was - flag any other
    /// prefab from the pasted list that turns out to also be non-hostile
    /// (e.g. Outbreak event scientists were left IN this list, unconfirmed
    /// either way) and it can be added here the same way.
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
    /// Shared predicate for the mechanics scientists and animals both use
    /// identically (armed-fight/unarmed-flee on the reactive path,
    /// regardless of facing/LOS - a shot from behind/an angle the bot can't
    /// see still triggers this exactly like being shot by a player already
    /// does, no LOS check involved in the reactive trigger itself, only in
    /// the on-sight scan and the per-tick fire/aim decision once a fight is
    /// already running). Deliberately NOT used for the animal-only
    /// features (on-sight retarget-interrupts-player, the low-health "at
    /// any cost" bypass) - those stay scoped to IsHuntablePredator alone,
    /// matching what was actually asked for.
    /// </summary>
    private static bool IsHuntableThreat(BaseEntity entity)
    {
        return IsHuntablePredator(entity) || IsHostileScientist(entity);
    }

    /// <summary>
    /// How close a live BradleyAPC needs to be before a ghost route breaks
    /// off entirely and flees (2026-08-22, Lucas's own explicit framing:
    /// "Bradley = Death" - its autocannon/rockets can down a survivor in a
    /// single burst, an order of magnitude more lethal than any hostile
    /// scientist this project already reacts to). Deliberately wider than
    /// every other threat-detection radius in this file
    /// (ScientistLootAvoidDetectionRadius, ScientistOnSightRange, etc) -
    /// Bradley's own real engagement range is itself large, so waiting
    /// until it's already close enough to be an immediate threat would be
    /// too late to actually get clear in time.
    /// </summary>
    private const float BradleyDangerRadius = 100f;

    /// <summary>
    /// Confirmed via decompile (BradleyAPC : BaseCombatEntity) - a real,
    /// engageable BaseCombatEntity like everything else this file already
    /// scans for, so the same CollectEntitiesInRange helper and
    /// StartFleeingFromThreat flee mechanism both apply directly with no
    /// new movement/threat-tracking code needed.
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

    // Gear-score floor below which each hard-avoid species is still
    // avoided (2026-09-15, Lucas's own explicit refinement - originally an
    // unconditional avoid for both, widened same-day to "unless geared
    // enough to actually stand a chance"). Crocodile's threshold sits above
    // Bear's - real Rust balance, a croc's ambush/DPS is the nastier of the
    // two even accounting for a bear's bulkier health pool. Once a
    // survivor's GetGearScore clears the relevant threshold, that species
    // drops out of IsHardAvoidAnimal entirely and falls back to the normal
    // always-fight-if-armed IsHuntablePredator path.
    private const float BearHardAvoidGearScoreThreshold = 30f;
    private const float CrocodileHardAvoidGearScoreThreshold = 50f;

    // Added 2026-09-19 (Lucas's own explicit live report/ask) - Boar and
    // Polarbear turned out to be the REAL dominant killers once Bear/
    // Crocodile stopped absorbing most deaths: live trace, 636 deaths
    // across a 150-bot batch in 67 minutes, 76% Bite, and of the actual
    // fight engagements Boar alone accounted for 1,317 - an order of
    // magnitude more than every other species combined. Boar's own
    // threshold is deliberately low - Lucas's own framing: "at this stage
    // a bot should already have its primitive tools (bow, arrows, stone
    // tools etc). This should be more than enough to kill a boar" - a
    // checklist-complete bot's GearScore is typically only single digits
    // to ~15 (a bow alone scores 5 in WeaponGearScore, armor near-zero this
    // early), so this is tuned to clear right around checklist completion,
    // not requiring genuinely good gear. Polarbear's sits above Bear's -
    // real Rust's toughest common bear variant, same "nastier than the
    // baseline" reasoning CrocodileHardAvoidGearScoreThreshold already
    // uses.
    private const float BoarHardAvoidGearScoreThreshold = 15f;
    private const float PolarbearHardAvoidGearScoreThreshold = 35f;

    // Added 2026-09-19, same session, follow-up ask right after Boar/
    // Polarbear - once those two stopped dominating, Wolf2/Panther/Tiger
    // immediately became the new majority of fight engagements in the next
    // live trace (64/38/25 respectively over 30 minutes, still 61% Bite
    // overall). Ordered by real Rust danger: Wolf2 roughly on par with
    // Boar (pack-hunter but individually not much tougher) so a similarly
    // low bar; Panther a genuine Gen2 apex predator, comparable to Bear;
    // Tiger the toughest common animal in the game, above even Polarbear.
    private const float Wolf2HardAvoidGearScoreThreshold = 20f;
    private const float PantherHardAvoidGearScoreThreshold = 35f;
    private const float TigerHardAvoidGearScoreThreshold = 45f;

    /// <summary>
    /// Every real IsHuntablePredator member except Stag - own tier, gated
    /// by species-specific gear-score threshold rather than a single flat
    /// rule. Stag is deliberately excluded (see IsHuntablePredator's own
    /// doc comment) - it never attacks a survivor and flees on its own,
    /// so it was never a threat this system needed to address. Live trace
    /// evidence across two rounds of this same investigation (2026-09-19):
    /// naked/rock-only fresh spawns were dying to Bite far more than
    /// anything else, and tracing individual bots showed spawn-die-respawn-
    /// die loops as short as 9 real seconds - the existing "fight anything
    /// with a melee tool, rock included, at any cost" design (see
    /// StartCombat's own doc comment above) doesn't distinguish a boar from
    /// a bear, and a rock stands no real chance against any of these six
    /// specifically. Rather than tune the fight (a rock realistically never
    /// beats a bear regardless), all six get pulled out of combat entirely
    /// while under-geared - never fought, melee or ranged - and handled by
    /// the avoidance system instead (HardAvoidAnimalRadius/
    /// TryFindNearbyHardAvoidAnimal below, and AdvanceAlongPath's own
    /// detour logic, LivingRust.Commands.cs). Gear-score-gated rather than
    /// an unconditional avoid - a well-geared survivor can and should fight
    /// any of these once it's no longer a hopeless mismatch; the deliberate
    /// furnace-material hunt (TryFindNearestHuntableAnimal, LivingRust.
    /// HomeSiteStrategy.cs) also naturally respects this same gate, so an
    /// under-geared survivor won't be sent hunting something it can't
    /// handle either.
    /// </summary>
    private bool IsHardAvoidAnimal(BaseEntity entity, BasePlayer npc)
    {
        // Base/bow rule (2026-09-21, Lucas's own explicit spec): a survivor
        // with NO base yet steers clear of every one of these hostile
        // animals regardless of gear (its priority is getting a base
        // down, not a fight); once it has a base AND a ready ranged
        // weapon (a bow with arrows, or a firearm with ammo) it's expected
        // to handle them itself and stops avoiding. A based survivor
        // without a ranged weapon falls through to the gear-score gates.
        if (entity is Bear || entity is Rust.Ai.Gen2.Crocodile || entity is Boar || entity is Rust.Ai.Gen2.Wolf2
            || entity is Rust.Ai.Gen2.Panther || entity is Rust.Ai.Gen2.Tiger || entity is Polarbear)
        {
            Survivor owner = GetSurvivorCached(npc);

            if (owner != null)
            {
                if (owner.Character.Home == null)
                {
                    return true;
                }

                if (HasReadyRangedWeapon(npc))
                {
                    return false;
                }
            }
        }

        if (entity is Bear)
        {
            return GetGearScore(npc) < BearHardAvoidGearScoreThreshold;
        }

        if (entity is Rust.Ai.Gen2.Crocodile)
        {
            return GetGearScore(npc) < CrocodileHardAvoidGearScoreThreshold;
        }

        if (entity is Boar)
        {
            return GetGearScore(npc) < BoarHardAvoidGearScoreThreshold;
        }

        if (entity is Rust.Ai.Gen2.Wolf2)
        {
            return GetGearScore(npc) < Wolf2HardAvoidGearScoreThreshold;
        }

        if (entity is Rust.Ai.Gen2.Panther)
        {
            return GetGearScore(npc) < PantherHardAvoidGearScoreThreshold;
        }

        if (entity is Rust.Ai.Gen2.Tiger)
        {
            return GetGearScore(npc) < TigerHardAvoidGearScoreThreshold;
        }

        if (entity is Polarbear)
        {
            return GetGearScore(npc) < PolarbearHardAvoidGearScoreThreshold;
        }

        return false;
    }

    // Deliberately wider than AnimalOnSightRange (10f) - this needs to
    // catch a hard-avoid animal early enough for AdvanceAlongPath's detour
    // to actually have room to steer around it, not just react once
    // already close. Shared by both the proactive movement detour and
    // StartCombat's own reactive/on-sight flee-instead-of-fight branch, so
    // "how close is too close" means the same thing everywhere this
    // project reacts to one of these two.
    private const float HardAvoidAnimalRadius = 50f;

    private bool TryFindNearbyHardAvoidAnimal(BasePlayer npc, out BaseEntity animal)
    {
        List<BaseCombatEntity> candidates = Pool.Get<List<BaseCombatEntity>>();
        animal = null;
        float nearestDistanceSqr = float.MaxValue;
        Vector3 position = npc.transform.position;

        try
        {
            CollectEntitiesInRange<Bear>(position, HardAvoidAnimalRadius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Crocodile>(position, HardAvoidAnimalRadius, candidates);
            CollectEntitiesInRange<Boar>(position, HardAvoidAnimalRadius, candidates);
            CollectEntitiesInRange<Polarbear>(position, HardAvoidAnimalRadius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Wolf2>(position, HardAvoidAnimalRadius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Panther>(position, HardAvoidAnimalRadius, candidates);
            CollectEntitiesInRange<Rust.Ai.Gen2.Tiger>(position, HardAvoidAnimalRadius, candidates);

            foreach (BaseCombatEntity candidate in candidates)
            {
                if (candidate == null || candidate.IsDestroyed || !candidate.IsAlive())
                {
                    continue;
                }

                // Gear-score-gated (2026-09-15) - a candidate this survivor
                // is actually geared enough to fight isn't a hard-avoid
                // threat for IT specifically, so it's skipped here rather
                // than steered around; StartCombat's own IsHardAvoidAnimal
                // check falls through to the normal fight path for it.
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

    // How far around the bot to look for a hostile scientist at all, for
    // loot-avoidance purposes - separate from ScientistOnSightRange (that
    // one's for combat engagement, this one's just "is there one anywhere
    // near enough to matter for looting"). Deliberately smaller - a
    // scientist 50m away isn't a reason to skip a nearby container.
    private const float ScientistLootAvoidDetectionRadius = 30f;

    // How close a loot candidate needs to be to a detected, visible hostile
    // scientist to actually get skipped - Lucas's own framing, "avoid
    // containers near the scientist," not "stop looting entirely while any
    // scientist is anywhere in sight."
    private const float ScientistLootAvoidContainerRadius = 15f;

    /// <summary>
    /// Positions of every hostile scientist within ScientistLootAvoidDetection
    /// Radius that npc can actually see (HasCombatLineOfSight) right now -
    /// computed once per search cycle (LivingRust.Looting.cs) rather than
    /// per candidate, so 200 bots each running a full loot search doesn't
    /// mean a fresh physics query per container. Empty list (not even
    /// queried) whenever the bot already has a real ranged weapon with ammo -
    /// see HasReadyRangedWeapon's own doc comment - since the avoidance never
    /// applies to an armed bot regardless of what's nearby.
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
    /// Generic aim point for anything StartCombat can target - a real
    /// player uses their actual eyes (matches every other player-combat aim
    /// calculation already in this file), while an animal has no `.eyes`
    /// component at all, so it falls back to CenterPoint() (real BaseEntity
    /// method, same one used elsewhere in this project for hit-offset
    /// diagnostics).
    /// </summary>
    private static Vector3 GetAimPoint(BaseCombatEntity target)
    {
        return target is BasePlayer player ? player.eyes.position : target.CenterPoint();
    }

    /// <summary>
    /// Real players show their actual name; animals have no displayName at
    /// all, so this falls back to the real prefab shortname (e.g. "bear",
    /// "wolf") for log readability.
    /// </summary>
    private static string GetAttackerDisplayName(BaseCombatEntity attacker)
    {
        return attacker is BasePlayer player ? player.displayName : attacker.ShortPrefabName;
    }

    /// <summary>
    /// On-sight detection - 2026-08-14, Lucas's own explicit request: "if
    /// bots see me within their LOS... they will prioritise combat...
    /// over whatever their current task is." Flat 125m for every weapon
    /// (Lucas's own explicit simplification, not weapon-scaled - "we can
    /// adjust as needed" later). A single shared timer sweeps every
    /// spawned survivor once per OnSightDetectionIntervalSeconds rather
    /// than each survivor running its own continuous perception loop -
    /// cheap enough at this scale (a periodic scan, not a per-tick one) and
    /// avoids needing a whole new per-survivor ticking system just for
    /// this. Real players only for now (BasePlayer.activePlayerList, the
    /// same real static list Rust itself maintains for every connected
    /// player) - bot-vs-bot on-sight detection is explicitly deferred.
    /// Reuses StartCombat directly (same entry point OnEntityTakeDamage's
    /// reactive path already uses) - it already cancels whatever the
    /// survivor was doing (movement/loot task) and remembers previousTask
    /// to resume after, so "prioritise combat over the current task" was
    /// already built, this just adds a second way to trigger it.
    /// </summary>
    private const float OnSightDetectionRange = 125f;

    // Bot-vs-bot's OWN detection range (2026-09-19) - previously reused
    // OnSightDetectionRange directly, unlike every other on-sight system in
    // this file (AnimalOnSightRange/ScientistOnSightRange both got their
    // own dedicated, deliberately-narrowed ranges - see AnimalOnSightRange's
    // own doc comment, "the 30ish metres is really messing with the bots'
    // thought process"). 125m was fine for the real-player check (cheap
    // regardless of range - at most 1-2 real players to check against), but
    // this same constant ALSO gated the bot-vs-bot inner loop, which is
    // O(n²) over the whole survivor population - a 125m radius circle
    // (~49,000m²) around every one of 150-200 bots meant huge numbers of
    // simultaneous candidates in any clustered area. Live report: a real
    // player playing alongside the bots found the game "almost unplayable"
    // from rubber-banding; live trace showed 60-80+ bot-onsight-diag lines/
    // second sustained and up to 64 distinct bots with a live candidate in
    // a single scan tick. Tightened to a real PvP-relevant "notice someone
    // across open ground" distance rather than the animal/scientist
    // "immediate threat" 10m - proactive bot-vs-bot spotting is closer to
    // real player awareness than an animal-avoidance reaction.
    private const float BotOnSightDetectionRange = 50f;

    // How often the shared sweep below runs - not gated by weapon fire
    // rate/combat tick cadence at all, this only decides how quickly a bot
    // NOTICES someone before StartCombat (and its own real per-tick logic)
    // takes over. Widened 1s -> 2s (2026-09-19, same live rubber-banding
    // report as BotOnSightDetectionRange above) - halves this scan's own
    // sustained CPU cost; only affects how promptly a bot re-rolls an
    // OPTIONAL on-sight fight, not anything safety-critical (real incoming
    // damage still reacts instantly via the separate OnEntityTakeDamage
    // path, completely untouched by this interval).
    private const float OnSightDetectionIntervalSeconds = 2f;

    private Timer _onSightDetectionTimer;

    /// <summary>
    /// Started from OnServerInitialized (LivingRust.Main.cs), stopped from
    /// Unload - same lifecycle every other engine-wide timer in this
    /// project already follows.
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
    /// Throttled (2026-08-24) - Lucas's own live report: zero
    /// bot-onsight-diag lines ever appeared, meaning the scan never even
    /// reaches the range/LOS check for two looting survivors together, not
    /// just failing the gear-score roll. That's a genuinely different
    /// question (is anyone ever close enough, or is gear the bottleneck),
    /// so this logs a periodic ground-truth snapshot instead of guessing -
    /// how many survivors are actually looting right now, how many clear
    /// the gear floor (15+) to even roll, and how far apart the closest
    /// pair of looting survivors actually is, regardless of LOS.
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

            // Same top-level skip RunOnSightDetectionScan itself applies
            // (npc.IsWounded/_activeCombat/_activeAttacks) - tracked here
            // too so this can distinguish "nobody's close enough" from
            // "everyone's close enough but mid-item-transfer, so the scan
            // never even looks at them this tick."
            bool scanBlocked = npc.IsWounded() || _activeCombat.ContainsKey(survivor.Character.Id) || _activeAttacks.ContainsKey(survivor.Character.Id);

            looters.Add((survivor.Character.Alias, GetGearScore(npc), npc.transform.position, scanBlocked, npc));
        }

        if (looters.Count < 2)
        {
            Puts($"bot-onsight-summary: only {looters.Count} looting survivor(s) right now - need at least 2 for bot-vs-bot to ever matter.");
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

        Puts($"bot-onsight-summary: {looters.Count} looting survivor(s) ({eligibleCount} with gear >= 15, {scanBlockedCount} scan-blocked [wounded/_activeCombat/_activeAttacks]), nearest pair {nearestPairDistance:F0}m apart, LOS={nearestPairHasLos}, thatPairBlocked={nearestPairBlocked} (need <= {OnSightDetectionRange:F0}m + real LOS + not scan-blocked).");
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

            // Already fighting something (a real player who shot it, or a
            // previous on-sight trigger this exact scan already handled)
            // has nothing to gain from re-scanning - StartCombat itself
            // would just no-op anyway, but skipping here avoids the wasted
            // LOS raycasts entirely.
            //
            // _activeAttacks specifically covers mid-transfer looting (the
            // per-item timer in LootMultiContainerEntityAndContinue/
            // PickupDroppedItemAndContinue/PickupCollectibleAndContinue) and
            // melee-container-attacking - Lucas's own explicit request,
            // 2026-08-15: a bot already elbow-deep in a corpse/container
            // should finish that specific loot action before reacting to
            // merely SPOTTING someone, rather than abandoning it mid-way
            // (leaving real items behind) the instant anyone walks past.
            // Deliberately scoped to on-sight only, NOT the reactive
            // OnEntityTakeDamage path (StartCombat itself, called from
            // both) - actually getting shot while looting should still
            // always override everything immediately, same as before.
            if (npc == null
                || npc.IsDestroyed
                || npc.IsWounded()
                || survivor.Character.State == CharacterState.Dead
                || _activeCombat.ContainsKey(characterId)
                || _activeAttacks.ContainsKey(characterId))
            {
                continue;
            }

            // See _disablePlayerCombat's own doc comment (LivingRust.
            // Debug.cs) - only the PLAYER half of this scan is suppressed,
            // so animal on-sight detection (below) still runs while it's
            // on. That's the whole point of the toggle: isolate bot-vs-
            // animal behavior from a real player's own gunfire, not disable
            // combat outright.
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

                    if (Vector3.Distance(npc.transform.position, player.transform.position) > OnSightDetectionRange * GetBiomeVisibilityFactor(npc.transform.position, player.transform.position))
                    {
                        continue;
                    }

                    if (!HasCombatLineOfSight(npc, player))
                    {
                        continue;
                    }

                    // Gear-score-scaled break-off (2026-08-24, Lucas's own
                    // explicit follow-up - "the breaks off looting should
                    // be scaled according to the bots gearscore," extending
                    // TryStartBotOnSightCombat's own curve to real players
                    // too). Deliberately scoped to LOOTING ONLY, same as
                    // the bot-vs-bot case - a survivor that's just roaming
                    // (CurrentTask != LootForResources) still prioritises
                    // combat unconditionally on sight, per Lucas's own
                    // earlier 2026-08-14 spec, and actually taking damage
                    // always still fights back regardless of gear
                    // (OnEntityTakeDamage's own reactive path, completely
                    // separate from this on-sight scan) - this only throttles
                    // whether a bot chooses to ABANDON active looting to go
                    // pick a fight it wasn't forced into.
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

            // Animal/scientist on-sight detection (2026-08-14, Lucas's own
            // explicit spec). No longer gated to armed bots only
            // (2026-08-15) - an unarmed bot walking toward a nearby
            // predator used to take zero evasive action until it was
            // actually hit at least once (StartFleeingFromThreat only fired
            // reactively from OnEntityTakeDamage), and by the time contact
            // happened it was often already too late to get a clean head
            // start - live report of bots "freezing" near aggressive
            // animals matches this. Both TryStartAnimalOnSightCombat and
            // TryStartScientistOnSightCombat route through StartCombat,
            // which already branches correctly on GetHeldEntity() itself -
            // an armed bot still fights (unchanged), an unarmed bot now
            // flees proactively on-sight instead of waiting to get hit
            // first. This isn't "provoking" anything, just reacting to a
            // threat earlier, so it doesn't conflict with the original
            // "don't pick a fight you can't win" reasoning this gate was
            // built for. Real player on-sight above already takes priority
            // via the `continue` on a successful match - a bot that just
            // started fighting a player this exact tick shouldn't also
            // immediately pick a fight with a nearby animal.
            bool startedNonPlayerCombat = TryStartAnimalOnSightCombat(survivor, npc);

            if (!startedNonPlayerCombat)
            {
                // Only tries a scientist target if no animal was
                // closer/already engaged this tick - same one-fight-
                // at-a-time reasoning as the player-vs-animal ordering
                // above, keeps this simple rather than picking between
                // two simultaneous on-sight candidates.
                startedNonPlayerCombat = TryStartScientistOnSightCombat(survivor, npc);
            }

            // Bot-vs-bot on-sight aggression (2026-08-24, Lucas's own
            // explicit request) - previously explicitly deferred entirely
            // ("bot-vs-bot on-sight detection is explicitly deferred").
            // Real bug fix (2026-09-01, live report: rifle-kitted and
            // melee-kitted debug-test bots never fought each other at all)
            // - this branch's own doc comment always claimed "a survivor
            // that's just roaming still prioritises combat unconditionally
            // on sight," matching the real-player branch above, but the
            // actual condition required CurrentTask == LootForResources to
            // even ATTEMPT bot-vs-bot combat - the exact opposite of the
            // real-player branch's own "only SKIP while looting and the
            // roll fails" structure. Any survivor whose CurrentTask was
            // never set to LootForResources at all (every debug spawn-kit
            // command - /lr.spawn.ak, /lr.debug.spawnmeleekit, etc. - none
            // of them call StartLootForResourcesTask) was silently
            // excluded from bot-vs-bot detection entirely, not just
            // deprioritized. TryStartBotOnSightCombat's own internal
            // GetLootAbandonChance gate (GearScore <= 14 bails before ever
            // scanning) already scopes who's even eligible, so this just
            // calls it unconditionally now, same as the real-player branch
            // already effectively does.
            if (!startedNonPlayerCombat)
            {
                TryStartBotOnSightCombat(survivor, npc);
            }
        }
    }

    /// <summary>
    /// Probability a survivor abandons its current loot task to engage a
    /// spotted player or survivor bot, as a function of its own gear score -
    /// 2026-08-24, Lucas's own explicit banding (replacing the original
    /// single smoothstep ramp, which undershot his actual intent at the
    /// high end - gear 51 landed around 17%, not "will break sight almost
    /// every time"):
    ///   0-14:  0% - "nothing to lose, may as well try find a gun and make
    ///          a play" - stays on task rather than picking a fight it has
    ///          no real chance of winning; getting geared up IS the play.
    ///   15-30: 0% -> 40%, linear.
    ///   31-50: 30% -> 70%, linear (deliberately overlaps the tail of the
    ///          15-30 band by a smidge rather than picking up exactly
    ///          where it left off - Lucas's own explicit correction).
    ///   51-100: flat 90% - "will break sight instantly and want to
    ///           confirm more kills," deliberately not 100% - even a
    ///           well-geared bot occasionally stays on a genuinely good
    ///           loot lead instead.
    /// Piecewise rather than one smooth curve specifically because Lucas
    /// gave real percentage anchors at each band boundary, not just a
    /// general shape - fitting those exactly mattered more here than the
    /// single-function elegance GetTierWeights/TacticalFalloffWeight use
    /// elsewhere. Still no hard knife-edge ANYWHERE within a band (each
    /// segment ramps continuously) even though the bands themselves have
    /// sharp edges at 30/31 and 50/51, per Lucas's own explicit numbers.
    /// Re-rolled once per real scan tick (OnSightDetectionIntervalSeconds)
    /// for as long as the sighting holds, so a mid-band bot doesn't get
    /// exactly one shot at the roll and then never again.
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

    // How much higher the spotting bot's own GearScore needs to be, on its
    // own (no ranged-weapon gap involved), to count as a genuine outmatch
    // (2026-09-01) - the ranged-vs-unarmed case below is certain regardless
    // of this gap; this is the fallback for two otherwise similarly-armed
    // survivors where one is still meaningfully ahead.
    private const int OutmatchGearScoreAdvantage = 20;

    /// <summary>
    /// Real relative combat-advantage check (2026-09-01, Lucas's own
    /// explicit example: "I have a bow, that bot doesn't appear to have a
    /// ranged weapon, let's attack!"). Two ways to outmatch: npc holds a
    /// real ranged weapon (BaseProjectile - the same check StartFleeingFromThreat
    /// already uses to detect "found a weapon mid-flight") while otherNpc
    /// doesn't, regardless of the raw GearScore gap; or npc's own GearScore
    /// is at least OutmatchGearScoreAdvantage higher, for the case where
    /// both are similarly armed but one is genuinely better equipped
    /// overall (armor, sustain).
    /// </summary>
    private bool HasOutmatchingCombatAdvantage(BasePlayer npc, BasePlayer otherNpc)
    {
        bool hasRanged = npc.GetHeldEntity() is BaseProjectile;
        bool otherHasRanged = otherNpc.GetHeldEntity() is BaseProjectile;

        if (hasRanged && !otherHasRanged)
        {
            return true;
        }

        // Real melee outmatch (2026-09-01, Lucas's own explicit example
        // for melee specifically: "if a bot does get a melee weapon, and
        // another bot walks near it... attack the other bot"). Only
        // counts if NEITHER has a ranged weapon (the ranged-vs-unarmed
        // check above already covers any matchup where one side is
        // armed with a gun/bow) - a real, non-rock melee tool
        // (HasNonRockMeleeTool) against someone with nothing but a rock
        // or bare hands is a clear, certain advantage the same way ranged-
        // vs-unarmed already is.
        if (!hasRanged && !otherHasRanged && HasNonRockMeleeTool(npc) && !HasNonRockMeleeTool(otherNpc))
        {
            return true;
        }

        return GetGearScore(npc) - GetGearScore(otherNpc) >= OutmatchGearScoreAdvantage;
    }

    /// <summary>
    /// Real server-wide "let the population actually bootstrap before it
    /// starts eating itself" grace period (2026-09-01, Lucas's own explicit
    /// spec: "an OVERALL 15 minute buffer period for bots to only fight
    /// each other IF they get attacked... a generic 15 minute grace period
    /// that avoids all bots combatting each other" - added after a live
    /// 150-bot trace found the "merciless game of Rust" change (below)
    /// meant a bot with even a single real weapon would proactively chain-
    /// engage every weaker nearby bot indefinitely - 369 melee engagements
    /// across 95 distinct bots in one 8-minute window, ZERO checklist
    /// completions or base-gather transitions in the same window. Lucas's
    /// own framing ("day 3 of the server... different story") is
    /// explicitly about real SERVER age, not each individual bot's own
    /// lifetime/spawn time - Time.realtimeSinceStartup is this process's
    /// real uptime, which survives every plugin hot-reload during dev
    /// (Carbon reloads the plugin, not the whole Rust server), only ever
    /// resetting on a genuine server restart, matching that intent
    /// exactly. Deliberately scoped to PROACTIVE bot-vs-bot aggression
    /// only (this function's only caller) - reactive self-defense
    /// (OnEntityTakeDamage -> StartCombat, matching Lucas's own "IF they
    /// get attacked" carve-out) and any real-player interaction are both
    /// completely unaffected.
    ///
    /// Raised from 15 to 30 minutes (2026-09-07, Lucas's own explicit
    /// follow-up, same session as the target-side "don't gang up on a bot
    /// with nothing worth fighting over" protection just above) - a live
    /// ~2-hour trace found the underlying problem this grace period exists
    /// for (armed bots repeatedly hunting down unarmed/progressing bots,
    /// directly preventing bases from ever getting built) simply resumed
    /// once the original 15-minute window closed. A longer window buys
    /// more real uninterrupted progression time before "merciless" rules
    /// resume, working alongside (not instead of) the target-side
    /// protection above, which now also covers the rest of a session past
    /// this window closing.
    /// </summary>
    private const float BotVsBotAggressionGracePeriodSeconds = 1800f;

    // See the bot-onsight-diag Puts call site's own doc comment further
    // down - throttles repeat logging of the SAME still-not-engaging pair,
    // not the actual detection/combat-roll (unchanged, still runs every
    // OnSightDetectionIntervalSeconds tick).
    private const float BotOnSightDiagLogCooldownSeconds = 15f;

    private readonly Dictionary<(Guid, Guid), float> _lastBotOnSightDiagLogTime = new();

    private void TryStartBotOnSightCombat(Survivor survivor, BasePlayer npc)
    {
        if (_engine == null)
        {
            return;
        }

        // Airdrop hot zone (2026-09-21, Lucas's own explicit spec: PvP on
        // sight is prioritised at an airdrop - "king of the hill"). A
        // participant skips the grace period, the primitive-tools carve-out
        // and every abandon-chance roll below, and simply engages the
        // nearest visible living survivor.
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

        // See BotVsBotAggressionGracePeriodSeconds's own doc comment.
        // _skipBotVsBotGracePeriod (LivingRust.Debug.cs) exists purely so
        // this is actually testable in a dev session without either
        // restarting the real Rust server or waiting out a genuine 15
        // real minutes.
        if (!_skipBotVsBotGracePeriod && Time.realtimeSinceStartup < BotVsBotAggressionGracePeriodSeconds)
        {
            return;
        }

        // Real "rock-only means focus on progressing, not fighting" carve-
        // out (2026-09-01, Lucas's own explicit correction right after the
        // "merciless game of Rust" change below: "if the bot has just a
        // rock, it should not worry about pvp or pve, it should just focus
        // on progressing specifically"). A survivor with no real ranged
        // weapon AND no real non-rock melee weapon never even considers
        // PROACTIVELY picking a fight - its priority is getting a real
        // tool/base/progression going, not scrapping over a rock. This is
        // deliberately scoped to on-sight AGGRESSION only: reactive self-
        // defense (OnEntityTakeDamage -> StartCombat, which still fights
        // back with the rock via StartMeleeCombat if actually attacked)
        // and proactive fleeing from a real predator (TryStartAnimalOnSightCombat,
        // a genuinely separate self-preservation mechanism) are both
        // unaffected - "not worrying about it" means not seeking it out or
        // accepting an optional fight, not refusing to defend itself.
        if (npc.GetHeldEntity() is not BaseProjectile && !HasNonRockMeleeTool(npc))
        {
            return;
        }

        // Real "merciless game of Rust" correction (2026-09-01, Lucas's
        // own explicit spec: "if a bot can see that another bot or player
        // has less gearscore... they should want to try fight"). The old
        // early-return here (abandonChance <= 0f, GearScore <= 14) blocked
        // a genuinely undergeared survivor from EVER even scanning nearby
        // survivors - including the real outmatch check below, which
        // exists specifically to catch a clear, certain advantage
        // (ranged-vs-unarmed, melee-vs-nothing, a real GearScore gap)
        // regardless of either side's own absolute score. A bot with
        // "nothing to lose" per the old framing still has plenty to gain
        // from finishing off someone with LESS than it has - that's
        // exactly the case this was accidentally blocking. abandonChance
        // itself still naturally gates the PROBABILISTIC (non-outmatch)
        // roll below to 0% for a genuinely undergeared bot - only a real
        // outmatch can still push a fight through for one, which is the
        // intended behaviour now, not a bug.
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

            if (Vector3.Distance(npc.transform.position, otherNpc.transform.position) > BotOnSightDetectionRange * GetBiomeVisibilityFactor(npc.transform.position, otherNpc.transform.position))
            {
                continue;
            }

            if (!HasCombatLineOfSight(npc, otherNpc))
            {
                continue;
            }

            // Real "don't gang up on a bot that's just trying to progress"
            // protection (2026-09-07, Lucas's own explicit follow-up after
            // a live trace found 'RowdyMiner' - genuinely unarmed, gear 0,
            // mid-base-gather - repeatedly hunted down by three separate
            // armed bots in a row and finally killed right as it was making
            // real progress). Mirrors this function's own top-of-function
            // rock-only carve-out (a rock-only survivor never PROACTIVELY
            // attacks), just applied to the TARGET side instead of the
            // attacker: an armed survivor still won't go out of its way to
            // finish off someone who has nothing worth fighting over
            // either. Reactive self-defense is completely unaffected (a
            // target that actually fights back still triggers the real
            // OnEntityTakeDamage -> StartCombat path regardless of this
            // check) - this only stops the PROACTIVE pile-on that was
            // directly preventing bases from ever getting built.
            if (otherNpc.GetHeldEntity() is not BaseProjectile && !HasNonRockMeleeTool(otherNpc))
            {
                continue;
            }

            // Real outmatch check (2026-09-01, Lucas's own explicit spec:
            // "the bots reaction to other bots should be case-by-case
            // basis... if the bot is passive and doesn't fight, don't
            // fight it UNLESS the bot gear score outmatches it i.e. 'I
            // have a bow, that bot doesn't appear to have a ranged
            // weapon, let's attack!'"). A genuine outmatch is a CERTAIN
            // engage, not another roll of abandonChance - the existing
            // gear-threshold roll below still covers the closer/ambiguous
            // matchups exactly as it already did, this only short-circuits
            // it for the clear-cut case the example describes.
            bool outmatches = HasOutmatchingCombatAdvantage(npc, otherNpc);
            bool tookTheFight = outmatches || UnityEngine.Random.value <= abandonChance;

            // Diagnostic-only (2026-08-24) - Lucas's own live report: "the
            // bots don't seem to be fighting each other." A real candidate
            // (in range, real LOS, both looting) reaching this point at all
            // is itself rare on a large map with survivors spread across
            // independent loot routes - Puts, not VerbosePuts, so this
            // survives regardless of the verbose toggle while confirming
            // whether the scan ever even finds an opportunity, separately
            // from whether the gear-score roll then takes it.
            //
            // Per-pair throttled (2026-09-19) - that diagnostic already did
            // its job back in August (bot-vs-bot detection confirmed
            // working) and this line was never revisited afterward. Live
            // report while a real player was playing alongside a 150-200
            // bot population: sustained 60-80+ of these lines PER SECOND
            // for the whole session (a mutually-in-sight pair re-logs the
            // identical "still not engaging" outcome every single
            // OnSightDetectionIntervalSeconds tick for as long as they stay
            // in range/LOS), a real, previously-undiagnosed contributor to
            // server hitching/rubber-banding, entirely separate from the
            // O(n²) scan's own real CPU cost (distance+LOS checks, still
            // unthrottled/unchanged here - this only throttles the LOGGING
            // of a repeat non-event). tookTheFight always bypasses the
            // cooldown - a real engage is a genuine state change, never
            // suppressed.
            (Guid, Guid) botOnSightDiagKey = (survivor.Character.Id, other.Character.Id);
            float nowForDiag = Time.realtimeSinceStartup;

            if (tookTheFight
                || !_lastBotOnSightDiagLogTime.TryGetValue(botOnSightDiagKey, out float lastDiagLogTime)
                || nowForDiag - lastDiagLogTime >= BotOnSightDiagLogCooldownSeconds)
            {
                _lastBotOnSightDiagLogTime[botOnSightDiagKey] = nowForDiag;
                Puts($"bot-onsight-diag: '{survivor.Character.Alias}' (gear {GetGearScore(npc)}, {abandonChance:P0} chance{(outmatches ? ", OUTMATCHES" : "")}) spotted '{other.Character.Alias}' (gear {GetGearScore(otherNpc)}) - {(tookTheFight ? "engaging" : "roll failed, staying on task")}.");
            }

            if (!tookTheFight)
            {
                // Spotted, but this roll didn't take the fight - stays on
                // task, gets another real chance next scan tick while
                // still in sight rather than being locked out permanently.
                continue;
            }

            StartCombat(survivor, otherNpc);
            return;
        }
    }

    /// <summary>
    /// Scans for the nearest huntable predator within AnimalOnSightRange -
    /// each concrete animal type is queried separately via BaseEntity.Query.
    /// Server.GetInSphere&lt;T&gt; (Rust's own real spatial index, same
    /// approach TryFindNearestCollectible already uses) rather than a single
    /// call against a shared base type, since Bear/Polarbear/Boar
    /// (BaseAnimalNPC) and Wolf2/Crocodile (Rust.Ai.Gen2.BaseNPC2) are two
    /// genuinely distinct class hierarchies with no single common ancestor
    /// closer than BaseCombatEntity itself. Returns whether combat was
    /// actually started, so the caller can skip the scientist scan this
    /// tick if an animal already won it.
    /// </summary>
    private bool TryStartAnimalOnSightCombat(Survivor survivor, BasePlayer npc)
    {
        List<BaseCombatEntity> candidates = Pool.Get<List<BaseCombatEntity>>();

        try
        {
            CollectEntitiesInRange<Bear>(npc.transform.position, AnimalOnSightRange, candidates);
            CollectEntitiesInRange<Polarbear>(npc.transform.position, AnimalOnSightRange, candidates);
            CollectEntitiesInRange<Boar>(npc.transform.position, AnimalOnSightRange, candidates);
            CollectEntitiesInRange<Stag>(npc.transform.position, AnimalOnSightRange, candidates);
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
    /// Same shape as TryStartAnimalOnSightCombat, own category per Lucas's
    /// own explicit framing (2026-08-14: "let's also add something separate
    /// to aggressive animals, all scientists") - own flat range
    /// (ScientistOnSightRange, 50m vs 30m for animals), own hostility
    /// filter (IsHostileScientist, which excludes the two known passive/
    /// safe-zone prefabs). Two concrete types cover every real scientist
    /// NPC (see IsHostileScientist's own doc comment for the class-
    /// hierarchy details) - queried then filtered by IsHostileScientist
    /// rather than five separate GetInSphere calls, since both types need
    /// the same passive-prefab exclusion applied afterward regardless.
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

    // Real per-tick combat cadence. NOT just a reassessment interval - it's
    // also an upper bound on fire rate, since FireAt can only ever be called
    // once per tick regardless of how quickly weapon.HasAttackCooldown()
    // itself clears. Live report 2026-08-13 (Lucas, comparing a real MP5
    // full-auto burst against SneakyVulture956's bot MP5, and separately
    // noting the M16A2 sounded like it was firing semi-auto rather than its
    // real fixed 3-round burst): both symptoms trace to the same root cause
    // - the old 0.15s value is SLOWER than several real weapons' own
    // repeatDelay (MP5/M249/LR300 all sit well under 0.1s between real
    // shots), so this tick was silently capping every fast weapon's fire
    // rate at ~6.7 rounds/sec regardless of its real, faster cadence - a
    // burst weapon's 3 rounds specifically get spaced a full tick apart
    // instead of back-to-back, which is exactly what reads as "semi-auto"
    // instead of a tight burst. 0.05s (20Hz) sits comfortably below every
    // real Rust firearm's repeatDelay while still being cheap per-tick work
    // (a handful of concurrent fights at most, not hundreds).
    private const float CombatTickInterval = 0.05f;

    // Fixed disengage-and-flee threshold - every survivor is always
    // initialized to InitializeHealth(100f, 100f) (SpawnSurvivor/
    // RespawnSurvivor), so an absolute value is exact, not an assumed
    // fraction of some variable max.
    private const float CombatFleeHealthThreshold = 25f;

    /// <summary>
    /// Self-medicate below this health (2026-08-23, Lucas's own long-
    /// flagged top priority - "using medical supplies during a fight" -
    /// bots previously had zero self-healing at all, just disengaging at
    /// CombatFleeHealthThreshold with nothing to show for it, confirmed
    /// live 2026-08-22: a bot needed Lucas personally healing it through
    /// a single Scientist to survive at all). Deliberately well above
    /// CombatFleeHealthThreshold (same fixed 0-100 scale, see that
    /// const's own comment) - a real player tops up well before
    /// critical, not only once already about to die.
    ///
    /// Raised 70 -> 95 (2026-09-19, Lucas's own explicit ask: "they should
    /// want to try to get to 100 hp asap"). The old 70 sat BELOW
    /// HurtBandageSupplyHealthThreshold (80, LivingRust.Crafting.cs - the
    /// threshold that sends a survivor out looking for bandage materials
    /// in the first place) - a real, live gap: a survivor at 70-79 health
    /// already carrying a bandage from that exact supply run just wouldn't
    /// use it, since this threshold hadn't been crossed yet. This function
    /// is called unconditionally every loot-task cycle (not just in
    /// combat - see ContinueLootTask's own unconditional TryUseMedicalItemIfHurt
    /// call), so raising it means a survivor genuinely tops off close to
    /// 100 whenever it's carrying supply, whether it's farming, building,
    /// or mid-fight - not just "well before critical" anymore, closer to
    /// "always wants full health." Left a few points below 100 (not 100
    /// flat) purely to avoid a heal chain re-triggering on trivial
    /// scratch damage every single tick.
    /// </summary>
    private const float CombatHealHealthThreshold = 95f;

    /// <summary>
    /// Once a heal chain starts, keep using items back-to-back (no
    /// re-equipping the weapon in between) until health reaches this or
    /// the survivor runs out of medical items - Lucas's own explicit
    /// spec, 2026-08-23: "if I was on 20 health mid fight, I would be
    /// spam using the medical syringes until I'm at least 80 health," not
    /// swap back to the weapon after every single use only to immediately
    /// re-trigger another heal. Deliberately higher than
    /// CombatHealHealthThreshold (the trigger to START a chain) so a
    /// chain doesn't stop the instant it barely crosses the start
    /// threshold again.
    /// </summary>
    private const float CombatHealDoneThreshold = 80f;

    private const float CombatHealCooldownSeconds = 4f;

    private readonly Dictionary<Guid, float> _lastSelfHealTime = new();

    /// <summary>
    /// Per-survivor "mid heal-animation" window (2026-08-23) - set the
    /// instant a heal fires, cleared implicitly once real time passes
    /// medicalTool.healDurationSelf. Checked at the very top of the combat
    /// tick's aim/fire logic, same shape as _combatRetreatUntilTime right
    /// above it - a bot mid-syringe-animation has swapped its active item
    /// AWAY from its real weapon, so without this gate the very next tick's
    /// "currentNpc.GetHeldEntity() is not BaseProjectile weapon" check
    /// would read that as having lost its weapon and disengage the fight
    /// entirely, mid-heal.
    /// </summary>
    private readonly Dictionary<Guid, float> _healingUntilTime = new();

    /// <summary>
    /// Per-survivor monotonic counter, incremented once per fresh heal
    /// chain start (TryUseMedicalItemIfHurt only, never on a chain's own
    /// natural recursive continuation) - 2026-08-23, real live bug caught
    /// via trace: two full heal chains fired within the same second and
    /// fought over the same active-item slot ("is using 'syringe.medical'"
    /// logged twice back to back). Root cause: the "too low on health"
    /// disengage check runs every combat tick UNCONDITIONALLY, even
    /// mid-heal (it isn't gated behind _healingUntilTime the way aim/fire
    /// is) - a real trace showed health hovering right at
    /// CombatFleeHealthThreshold, repeatedly engaging/disengaging, which
    /// tore down combat (EndCombat) while a heal chain was still mid-
    /// flight, orphaning its pending timers; a fresh re-engagement moments
    /// later then started an independent NEW chain that collided with the
    /// still-pending orphaned one. Every deferred continuation in
    /// UseOneMedicalItemThenMaybeContinue now captures the generation it
    /// was started with and re-checks it's still current before touching
    /// item/weapon state - an orphaned chain from a torn-down engagement
    /// quietly stops instead of fighting a newer one for the same item.
    /// </summary>
    private readonly Dictionary<Guid, int> _healChainGeneration = new();

    /// <summary>
    /// Real held items only - syringe first, matching the priority this
    /// project's own belt-slot medical stocking already established
    /// (OrganizeMedicalAndExplosiveSlots' doc comment: "medical syringe
    /// trumps large medkit... a syringe gives an instant heal plus a
    /// passive regen-over-time effect a medkit doesn't have"). Deliberately
    /// excludes largemedkit - confirmed via its own bundled item JSON
    /// (Bundles/items/largemedkit.json) it's "isHoldable":false, a
    /// fundamentally different class of item (heals passively from sitting
    /// in the toolbar while wounded, not an active click-to-use MedicalTool
    /// like bandage/syringe are - confirmed via decompile, see
    /// TryUseMedicalItemIfHurt's own doc comment) - out of scope for this
    /// active mid-fight heal, a genuinely separate mechanic if ever built.
    /// </summary>
    private static readonly string[] CombatHealItemPriority = { "syringe.medical", "bandage" };

    /// <summary>
    /// Real held-item self-heal via MedicalTool - 2026-08-23, several real
    /// bugs found and fixed across a live testing pass, full history kept
    /// here since each one taught something real about MedicalTool's
    /// actual behavior (confirmed via full ilspycmd decompile, not just
    /// method signatures):
    ///
    /// (1) Calling the non-public GiveEffectsTo directly only applies the
    /// raw heal effect - item consumption (UseItemAmount), the real
    /// per-item attack cooldown (StartAttackCooldown), and the animation
    /// trigger (SignalBroadcast(Signal.Attack, "")) all live in the PUBLIC
    /// ServerUse wrapper instead. (2) Applying the heal effect in the same
    /// call as the animation signal gave the bot an unfair advantage over
    /// a real player (Lucas's own explicit fairness call: "the healing
    /// effect doesn't actually occur until the animation is completed
    /// with a real player") - fixed by deferring ONLY the actual health/
    /// metabolism change to the end of the real animation duration, while
    /// item consumption/cooldown/signal still fire immediately (confirmed
    /// live: a bare SignalBroadcast alone, with nothing else, only played
    /// the draw animation, not the "use" animation - the client evidently
    /// needs the item-amount/cooldown network update alongside the signal
    /// to show the full clip). (3) The deferred effect application can't
    /// safely call a method ON the MedicalTool entity later, since
    /// consuming the LAST unit of a stack destroys that entity outright
    /// (UseItemAmount's own decompiled body calls DestroyThis() at 0) -
    /// fixed by capturing the item's ItemModConsumable effects data BEFORE
    /// consuming, then manually re-applying the exact same effect loop
    /// BasePlayer.OnMedicalToolApplied runs (Health -> health += amount,
    /// everything else -> metabolism.ApplyChange), independent of whether
    /// the item/entity still exist by the time it fires. (4) The original
    /// cooldown was set at the START of a heal, running concurrently with
    /// the animation - by the time one heal finished, the cooldown had
    /// ALSO just expired, so if still hurt it chain-triggered another heal
    /// with zero gap to fight back (confirmed live: a bot chained syringe
    /// -> syringe -> bandage -> bandage -> bandage, ~28 real seconds
    /// straight before ever firing again).
    ///
    /// (5) Lucas's own explicit design correction once he saw that last
    /// bug in action: rather than treat it as purely a bug to eliminate,
    /// swapping back to the weapon after EVERY single use (only to
    /// immediately re-equip the syringe again next tick) was itself the
    /// wrong shape - "if I was on 20 health mid fight, I would be spam
    /// using the medical syringes until I'm at least 80 health," not
    /// swap-heal-swap-heal-swap. This is now built as a real CHAIN:
    /// TryUseMedicalItemIfHurt is the entry point (checks
    /// CombatHealHealthThreshold + the real per-CHAIN cooldown), and
    /// UseOneMedicalItemThenMaybeContinue does one real use, and in its
    /// deferred completion callback either recurses immediately into
    /// another use (no cooldown gate - still mid-chain) if health is
    /// still below CombatHealDoneThreshold and another item is available,
    /// or stops and restores the combat weapon exactly once. The weapon
    /// now only leaves the survivor's hand once per chain, not once per
    /// item.
    ///
    /// (6) FINAL FIX, 2026-08-23 - everything above from (2)/(3)/(4) (the
    /// deferred-effect ItemModConsumable replication, the repeated 0.5s
    /// signal-rebroadcast loop) has been REMOVED and replaced. Real root
    /// cause of the actual visual bug: not the signal mechanism at all,
    /// but TIMING - every earlier version called ServerUse (or its own
    /// animation signal) essentially instantly after UpdateActiveItem,
    /// while the client's own draw animation was still playing, so there
    /// was nothing for the "use" trigger to land on. Found by decompiling
    /// the real HumanNPC.Heal() Facepunch already ships for scientist
    /// NPCs (Lucas's own live observation that scientistnpc_roam_nvg
    /// visibly uses a syringe was the actual lead - a real, server-driven,
    /// third-person-visible reference implementation, not another guess):
    /// equip, wait a real 1s for the draw to settle
    /// (MedicalItemPreUseDelaySeconds), THEN call the real ServerUse()
    /// (which correctly handles consumption/cooldown/signal/effect all
    /// together on its own), hold 2s more
    /// (MedicalItemPostUseHoldSeconds), then either chain into another
    /// item or restore the weapon. Deliberately does NOT copy
    /// HumanNPC.Heal's own `Heal(MaxHealth())` full-heal shortcut - real
    /// survivors heal by the syringe/bandage's own actual configured
    /// amount via ServerUse's real GiveEffectsTo call, not a free full
    /// top-up, since real item economy (not NPC-convenience balance) is
    /// the whole point here.
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

        // Real race caught live (2026-08-23): _lastSelfHealTime only gets
        // set once a whole CHAIN stops (see UseOneMedicalItemThenMaybeContinue),
        // not after each individual item within it - so while a chain is
        // still mid-flight (waiting out its own internal timers), this
        // cooldown check alone passes right through, and the combat
        // tick's OWN top-of-tick _healingUntilTime gate has a same-instant
        // race right at the moment one chain step's window expires and the
        // next one hasn't been armed yet. Confirmed via trace: two
        // separate "is using 'syringe.medical'" lines fired at the
        // identical timestamp - two full chains running concurrently,
        // fighting over the same active-item slot, which alone is enough
        // to wreck any animation regardless of every other fix already
        // applied. This is the real, authoritative "already busy" check -
        // explicit and unconditional, not inferred from a cooldown gap.
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
    /// Does exactly one real MedicalTool use (equip/consume/cooldown/
    /// signal immediately, the actual health/metabolism effect deferred
    /// to the end of the real animation - see TryUseMedicalItemIfHurt's
    /// own doc comment for the full history of why), then decides whether
    /// to keep chaining. Called both from TryUseMedicalItemIfHurt (the
    /// gated entry point) and recursively from its own completion
    /// callback while still mid-chain - only the FIRST call goes through
    /// the cooldown/threshold gate, every recursive continuation is
    /// already known to be safe (still hurt, still armed with an item).
    /// </summary>
    /// <summary>
    /// Pre-use draw settle, matching real Facepunch NPC code exactly
    /// (2026-08-23, decompiled straight from HumanNPC.Heal - Lucas's own
    /// live observation that scientistnpc_roam_nvg visibly uses a syringe
    /// animation was the actual lead that cracked this, after several
    /// failed guesses at the real trigger mechanism):
    ///   UpdateActiveItem(item.uid);
    ///   yield return new WaitForSeconds(1f);
    ///   heldItem.ServerUse();
    ///   yield return new WaitForSeconds(2f);
    ///   EquipWeapon();
    /// The real bug in every earlier version was timing, not mechanism -
    /// calling ServerUse (or any animation signal) essentially instantly
    /// after UpdateActiveItem fires it while the client's own draw
    /// animation is still playing, so the "use" trigger has nothing to
    /// land on and reads as a frozen/empty-handed pose. Facepunch's own
    /// NPCs wait a full real second for the draw to actually finish
    /// first. This also means the whole manual ItemModConsumable-effect-
    /// replication and repeated-signal-rebroadcast machinery from earlier
    /// versions is no longer needed - calling the real public ServerUse()
    /// at the RIGHT MOMENT does the correct thing on its own (consumption,
    /// cooldown, animation signal, and the real heal effect, all in one
    /// real call, exactly as intended).
    /// </summary>
    private const float MedicalItemPreUseDelaySeconds = 1f;

    /// <summary>
    /// Post-use hold before re-equipping the weapon - same real value
    /// HumanNPC.Heal uses for its own post-heal pause.
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

        // REMOVED 2026-08-24 (Lucas's own live report: "the healing whilst
        // running to cover isn't happening for sure") - this used to call
        // CancelActiveMovement(survivor) here, a leftover from BEFORE
        // heal-while-moving was authorized (Lucas's own explicit ruling
        // earlier the same day: "players CAN heal and run simultaneously...
        // the heal while moving animation issue should not be an issue").
        // Real root cause of the live bug: StartTacticalRetreat
        // (LivingRust.CombatTacticalDecisions.cs) starts the retreat walk
        // via StartWalkingWithRecovery, THEN immediately calls
        // TryUseMedicalItemIfHurt in the same tick - with the old
        // CancelActiveMovement call still here, the heal chain killed the
        // walk it was supposed to run alongside the instant it started,
        // so the bot stood still and healed instead of healing on the
        // move. Deliberately not replaced with anything - concurrent
        // movement/healing was already an accepted, explicitly authorized
        // trade-off, this was just dead code from an earlier design that
        // never got cleaned up when that design changed.
        Item healItem = FindBestHealItem(npc);

        if (healItem == null)
        {
            // Genuinely out of medical items mid-chain - stop here and
            // get back to fighting with whatever health was reached.
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

        // A real, separately-networked field (confirmed via decompile,
        // see SetAimingIn's own doc comment) - combat keeps this true for
        // almost the entire fight (SetAimingIn(currentNpc, !sprinting)),
        // and each weapon's own ironsight/ADS animation plays off it. If
        // it's still true from the fight the instant the syringe gets
        // equipped, the client's animator is likely straddling two
        // conflicting states (a leftover aim pose + an attack signal for
        // an item with no ADS animation at all) - 2026-08-23, live report:
        // the "use" animation never visually plays, just a frozen stand.
        // Explicitly clearing it here removes that possible conflict; the
        // next combat tick's own AimAtPlayer naturally re-sets it once
        // aim/fire resumes after the weapon's back out, so nothing needs
        // to restore it manually.
        SetAimingIn(npc, false);

        ItemId healItemUid = healItem.uid;
        string healItemShortname = healItem.info.shortname;

        // Real per-item value (healDurationSelf, e.g. 4f for a syringe -
        // confirmed by Lucas's own live in-game timing check, "the
        // animation itself for using a syringe is 4s"), not the fixed
        // Facepunch NPC 1f+2f=3f total this used to hardcode - HumanNPC's
        // own timings are tuned for scientist AI convenience, not
        // guaranteed to match the real player-visible animation length
        // for every item. Pre-delay stays fixed (real draw-settle time,
        // not item-specific), post-hold absorbs whatever's left of the
        // item's own real duration so the total always matches it.
        float postUseHold = Mathf.Max(equippedTool.healDurationSelf - MedicalItemPreUseDelaySeconds, MedicalItemPostUseHoldSeconds);
        float totalDuration = MedicalItemPreUseDelaySeconds + postUseHold;

        _healingUntilTime[characterId] = Time.realtimeSinceStartup + totalDuration;

        VerbosePuts($"'{survivor.Character.Alias}' is using '{healItemShortname}' ({totalDuration:F1}s)...");

        timer.Once(MedicalItemPreUseDelaySeconds, () =>
        {
            BasePlayer useNpc = survivor.Player;

            // Generation check first - a torn-down-and-restarted
            // engagement (see this method's own doc comment, and
            // _healChainGeneration's) means a NEWER chain may already own
            // this survivor's item/weapon state by the time this fires;
            // an orphaned older chain must not touch anything at all, not
            // even re-equip the weapon, or it'll stomp whatever the
            // current chain is doing.
            if (useNpc == null || useNpc.IsDestroyed || survivor.Character.State == CharacterState.Dead || !IsCurrentHealGeneration(characterId, generation))
            {
                return;
            }

            // Re-verify still actually holding this exact item as a real
            // MedicalTool - a real second passed, long enough for it to
            // have been interrupted (death, item dropped/moved, a plugin
            // reload).
            if (useNpc.inventory.FindItemByUID(healItemUid) == null || useNpc.GetHeldEntity() is not MedicalTool heldTool || heldTool.GetOwnerItem()?.uid != healItemUid)
            {
                EquipBestWeaponForDisplay(survivor);
                return;
            }

            float healthBefore = useNpc.health;

            heldTool.ServerUse();

            Puts($"heal-diag: '{survivor.Character.Alias}' used '{healItemShortname}' - health {healthBefore:F0} -> {useNpc.health:F0}.");

            timer.Once(postUseHold, () =>
            {
                BasePlayer laterNpc = survivor.Player;

                if (laterNpc == null || laterNpc.IsDestroyed || survivor.Character.State == CharacterState.Dead || !IsCurrentHealGeneration(characterId, generation))
                {
                    return;
                }

                // Keep chaining (no re-equip, no cooldown yet) if still
                // below the DONE threshold and another item is available -
                // matches Lucas's explicit "spam using... until at least
                // 80 health" spec. _lastSelfHealTime (the real per-CHAIN
                // cooldown) is only ever set once the chain actually
                // stops, here or in the "ran out of items"/interrupted
                // branches above. Same generation carries through - this
                // is a CONTINUATION of the same chain, not a fresh start.
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
    /// A fight with no real line of sight on the attacker for this long
    /// gives up entirely, regardless of distance (2026-08-16, Lucas's own
    /// explicit request/live bug: noclipping under the map left a bot
    /// permanently locked in combat, since none of the existing disengage
    /// checks - attacker dead/destroyed, out of pursueRange, own health,
    /// lost weapon - ever fire against a target that's simply unreachable
    /// but still technically alive and in range). Real players still get a
    /// full chase via StartFollowing/lastKnownPosition while this window is
    /// still open - this only fires once genuinely NO real engagement
    /// happened for the whole 15s, not on the first LOS loss. Skipped for
    /// animal targets (IsHuntablePredator) - same "attempts to kill it at
    /// any cost" philosophy the pursueRange/low-health bypasses already
    /// apply to animals specifically.
    /// </summary>
    private const float NoRealEngagementDisengageSeconds = 15f;

    /// <summary>
    /// How far to back away from the attacker when forced to reload mid-
    /// fight with a completely empty magazine (2026-08-16, Lucas's own
    /// explicit request/real live report: a bot died reloading in the open
    /// right as a second attacker engaged it). Scoped deliberately narrow -
    /// see the mid-fight reload branch's own doc comment for exactly when
    /// this fires and when it deliberately doesn't (a fight already being
    /// won never needs it).
    /// </summary>
    private const float ReloadRetreatDistance = 8f;

    /// <summary>
    /// How long the retreat itself lasts once actually triggered (see
    /// TryTriggerReloadRetreat) - deliberately a fixed duration, not tied
    /// to weapon.reloadTime, since by the time this fires the real reload
    /// has already finished (it's synchronous/instant in code - only the
    /// visual animation takes real time) - this window is purely "how long
    /// to keep creating distance after getting hit," a separate concern.
    /// </summary>
    private const float ReloadRetreatDuration = 2.5f;

    /// <summary>
    /// Per-survivor real-time deadline for "I just reloaded a completely
    /// empty weapon mid-fight and am still exposed" - set by the mid-fight
    /// reload branch (Time.realtimeSinceStartup + weapon.reloadTime),
    /// checked (and consumed) by OnEntityTakeDamage against real incoming
    /// damage. Deliberately NOT what actually triggers the retreat by
    /// itself - reloading unopposed (nothing shooting at it right now)
    /// never retreats, only real damage landing inside this window does
    /// (see TryTriggerReloadRetreat).
    /// </summary>
    private readonly Dictionary<Guid, float> _reloadExposureWindowUntil = new();

    /// <summary>
    /// Per-survivor real-time deadline the combat tick's own aim/fire/
    /// follow logic is suspended until - set only by TryTriggerReloadRetreat,
    /// read by the combat tick's own retreat gate. A dictionary rather than
    /// a closure-local variable specifically because it needs to be set
    /// from OUTSIDE the combat tick (OnEntityTakeDamage, a completely
    /// separate hook) - see this whole feature's own history for why a
    /// closure-local first attempt didn't work for that.
    /// </summary>
    private readonly Dictionary<Guid, float> _combatRetreatUntilTime = new();

    /// <summary>
    /// Actually triggers the reload retreat - called from OnEntityTakeDamage
    /// the moment real damage lands while _reloadExposureWindowUntil is
    /// still open for this survivor (2026-08-16, Lucas's own explicit
    /// correction: "it should only retreat IF it gets shot or damaged
    /// WHILST reloading"). Consumes the exposure window (removes it) so a
    /// second unrelated hit later doesn't re-trigger off a stale window.
    /// Backs directly away from wherever the damage actually came from
    /// (the real attacker, not necessarily the same one the combat tick's
    /// own `attacker` closure variable is tracking - a second, different
    /// shooter hitting during the reload window is just as real a reason
    /// to create distance).
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

    // Floor on the distance-banded burst system's pause between volleys -
    // see its own comment for why a bare repeatDelay*2 wasn't enough on
    // its own for fast automatics.
    private const float BurstPauseFloorSeconds = 1f;

    // Prototype 17 specifically (Lucas's own spec, 2026-08-11): burst mode
    // 0-20m, single/semi-auto beyond that.
    private const float P17BurstSwitchDistance = 20f;

    // P17's own single-fire (semi-auto, beyond P17BurstSwitchDistance) pace
    // floor - Lucas's own explicit follow-up, 2026-08-13: "upped by about
    // 50% across all distances" after the previous pass (which gave it the
    // same BurstPauseFloorSeconds=1f floor every other single/burst path
    // uses) still read as too slow specifically for P17's semi-auto mode.
    // A 50% FASTER rate means roughly 1/1.5 the delay, not 1.5x it -
    // BurstPauseFloorSeconds / 1.5 rounds to this. Deliberately its own
    // dedicated constant rather than reusing BurstPauseFloorSeconds
    // directly, since that constant is still the correct, unrelated floor
    // for every other weapon's burst-to-burst pause.
    private const float P17SingleFirePaceSeconds = 0.67f;

    // Live report 2026-08-11: a bot that settles at its engagement range
    // just holds there forever, even if the target keeps retreating to
    // stay just outside "ideal" range - a real trace confirmed a bot
    // frozen at the exact same spot for 200+ real seconds. Every
    // AdvanceIntervalSeconds, nudges its own held distance a random
    // AdvanceStepMin-MaxMeters closer (down to AdvanceMinimumDistance) and
    // re-issues StartFollowing at that tighter distance - a real player
    // pushing a fight rather than passively camping at max range. Distance
    // BANDS for firing/burst purposes still use the real live distance,
    // not this shrinking hold distance - only where the bot tries to
    // stand changes, not what it's mechanically allowed to fire at.
    // Widened from 3-5m per Lucas's own live-test feedback 2026-08-13 - the
    // original step read as too passive/slow to actually feel like a bot
    // pressing an advantage in a fight.
    private const float AdvanceIntervalSeconds = 4f;
    private const float AdvanceStepMinMeters = 10f;
    private const float AdvanceStepMaxMeters = 20f;

    // Shotgun-only advance cadence - Lucas's own explicit instruction,
    // 2026-08-13: "strictly with shotguns also, close the distance quick."
    // A shotgun bot re-evaluates and pushes closer roughly 2.5x as often as
    // every other weapon's shared AdvanceIntervalSeconds (4s) - see
    // isShotgunWeapon's own doc comment in StartCombat for the full
    // reasoning (also forces shotguns onto this periodic-timer path at all,
    // bypassing the smaller reactive post-burst nudge other banded weapons
    // now use).
    private const float ShotgunAdvanceIntervalSeconds = 1.5f;

    // Real shortnames this project's own combat system treats as shotguns -
    // used to force the aggressive ShotgunAdvanceIntervalSeconds cadence
    // above regardless of whether a given shotgun (M4/SPAS-12) happens to
    // have real Bands that would otherwise route it into the slower
    // reactive post-burst nudge.
    private static readonly HashSet<string> ShotgunShortnames = new()
    {
        "shotgun.pump",
        "shotgun.m4",
        "shotgun.spas12",
        "shotgun.double",
    };
    private const float AdvanceMinimumDistance = 5f;

    // Reactive "close a few metres" nudge applied the instant a burst
    // finishes, while LOS holds - Lucas's own explicit correction
    // 2026-08-13: the periodic AdvanceIntervalSeconds timer above fires on
    // its own fixed clock, completely independent of whether a burst had
    // actually just landed - a live trace showed bots interrupting an
    // active exchange to sprint (locked out of firing entirely - see
    // SprintFireSettleSeconds) toward a tighter hold distance even mid-
    // burst, reading as "the bot cares about closing distance more than
    // shooting." For any weapon that actually fires in a burst-then-pause
    // pattern (weaponUsesBurstPacing below), gap-closing while LOS holds
    // now happens ONLY in the natural pause between volleys instead, using
    // a smaller, more deliberate step than the periodic timer's - a real
    // player reloading/resetting their aim after a burst, not covering
    // ground mid-trigger-pull. Continuous-fire weapons (shotguns, snipers)
    // have no such pause to hook into, so they keep using the periodic
    // timer exactly as before.
    private const float PostBurstAdvanceStepMinMeters = 3f;
    private const float PostBurstAdvanceStepMaxMeters = 6f;

    // Grace period after modelState.sprinting last read true before firing
    // is allowed again. Live report 2026-08-13: a trace still showed rounds
    // firing while the bot was visibly mid-sprint-animation, despite the
    // sprinting check below already gating on the server's own
    // modelState.sprinting flag directly. Root cause isn't the gate itself -
    // StartFollowing flips modelState.sprinting the same tick it decides to
    // stop and pushes it immediately (SendModelState(true)) - it's that a
    // client's sprint-to-standing transition animation takes a moment to
    // actually play out, the same kind of visual-lags-behind-server gap
    // this project has hit before (AimAtPlayer's own SendNetworkUpdate fix).
    // A real player can't snap from a dead sprint to a stable firing stance
    // instantly either, so this settle window doubles as realistic weapon-
    // raise behaviour, not just a visual patch.
    private const float SprintFireSettleSeconds = 0.3f;

    // Reaction-time delay before a bot acts on REGAINING line of sight -
    // see hasLos's own doc comment in the combat tick for why (losing LOS
    // stays instant, only regaining it holds off). Distance-scaled per
    // Lucas's own explicit spec, 2026-08-13, re-tuned to a finer 6-band
    // table the same day after a live trace showed the original 2-tier
    // 0.75-1.5s/1.5-3s split was actively swallowing real, brief LOS
    // windows during a tight close-range kite - by the time the delay
    // would've elapsed, LOS had usually already broken again, so hasLos
    // could go multiple SECONDS without ever confirming a regain even
    // though genuinely visible for short windows throughout.
    //
    // Expanded to 18 five-metre-wide bands (10-100m) same day, per Lucas's
    // own explicit ask for a much more granular table (10-20 "variants" was
    // his own framing) - the goal being reaction time that reads as
    // genuinely, smoothly quicker the closer the fight is, rather than
    // jumping between a handful of coarse tiers. Then scaled down again to
    // roughly a quarter of that pass's own values (Lucas's own explicit
    // "let's lower the delays... 0.0X" request) - near-instant up close,
    // topping out around 0.3s by 100m instead of over a full second.
    // Ordered ascending by MaxDistance, same shape as WeaponFireProfile's
    // own Bands - checked against the live distance at the exact moment
    // LOS returns.
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

    // Beyond the last LosRegainReactionBands entry (100m+) - Lucas's own
    // explicit floor, 2026-08-13: a flat minimum rather than a randomized
    // range, on the reasoning that human reaction time falls off further
    // still at this range, and ammo is too precious to spend on optimistic
    // pot-shots this far out. Left untouched through both the 18-band
    // expansion and the later scale-down above - still 3s flat.
    private const float LosRegainReactionExtremeSeconds = 3f;

    // Global multiplier over LosRegainReactionBands only - Lucas's own
    // explicit follow-up, 2026-08-13: "30% slower on all distances" right
    // after the scale-down pass above. Deliberately does NOT apply to
    // LosRegainReactionExtremeSeconds (100m+) - that flat 3s floor has
    // been treated as its own separate, deliberately-preserved value
    // through every other pass so far, so it stays untouched here too.
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
    /// Real per-weapon-family fire behaviour (2026-08-11, replaces the
    /// original flat 4-tier engagement-range system) - Lucas's own explicit
    /// distance/burst spec, not guessed. EngagementRange is both how far
    /// StartFollowing tries to hold at AND the outer distance the combat
    /// tick gives up at entirely - a single source of truth so a bot can
    /// never fire (or keep chasing) beyond its current weapon's realistic
    /// max range regardless of LOS, closing the "sniping from 600m away"
    /// concern by construction rather than a separate check.
    ///
    /// Bands is an ordered list of (maxDistance, minBurst, maxBurst),
    /// checked against the CURRENT live distance each time a new volley
    /// starts (not fixed for the whole fight) - a bot that closes distance
    /// mid-fight gets a bigger burst on its next volley. A null/empty
    /// Bands array means continuous fire - keep shooting every cooldown-
    /// cleared tick with no volley/pause bookkeeping at all (pump shotgun,
    /// SPAS-12, double barrel, snipers - a sniper's own real repeatDelay is
    /// already slow enough that this naturally reads as "one shot at a
    /// time" without an artificial cap). A band's own minBurst of -1 is the
    /// same "continuous, no cap" sentinel scoped to just that one band -
    /// only the M4 shotgun uses this today, continuous within 10m but a
    /// real capped 1-3 burst beyond it.
    /// </summary>
    private readonly struct WeaponFireProfile
    {
        public readonly float EngagementRange;
        public readonly float PursueRange;
        public readonly (float MaxDistance, int MinBurst, int MaxBurst)[] Bands;

        /// <summary>
        /// pursueRange defaults to engagementRange (the common case for
        /// every weapon whose real firing range is already generous enough
        /// to double as a sensible chase distance - rifles/pistols/
        /// snipers). Explicit separate pursueRange exists specifically for
        /// short-range weapons like shotguns (2026-08-13 live report): a
        /// bot shot at from well beyond its shotgun's real ~15-20m range
        /// was disengaging on the very first combat tick - EngagementRange
        /// doubled as both "how close do I need to be to fire" AND "how far
        /// will I chase before giving up," so a shot landing from outside
        /// that tiny range ended combat before StartFollowing ever got a
        /// tick to actually close the gap. Splitting these means a shotgun
        /// bot can still be told "someone's shooting at me from 40m, go
        /// deal with it" and genuinely try to close in, even though it
        /// won't actually fire until real range - same distinction as a
        /// real player closing distance with a shotgun during a fight.
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

    // Generic chase distance for short-range weapons whose real firing
    // range is too small to also double as a sensible "how far will I
    // pursue before giving up" threshold - see WeaponFireProfile's own
    // pursueRange doc comment.
    private const float ShotgunPursueRange = 80f;

    // 0-30m split into three finer sub-bands (Lucas's own explicit spec,
    // 2026-08-13) - the original single 0-30m/3-10 tier didn't distinguish
    // "just inside 30m" from "right on top of them," which mattered once
    // the post-burst advance nudge (see PostBurstAdvanceStepMinMeters) was
    // taught to stop pushing closer once already in the best band - a
    // single wide near band meant the bot considered 29m "good enough" and
    // never had reason to press all the way in for the genuinely bigger
    // bursts a real close-range fight calls for.
    // minBurst floored at 0 for every band beyond the closest tier (Lucas's
    // own explicit request, 2026-08-13, applied "across the board... all
    // weapons") - a real chance the bot rolls nothing this volley and just
    // holds fire rather than always guaranteeing at least one shot,
    // imitating a real "not worth the shot right now" hesitation. The
    // closest tier (0-10m here) deliberately keeps its real minimum (10) -
    // Lucas's own explicit carve-out: at point-blank a bot should never
    // roll a genuine zero, only "how many," never "none at all."
    private static readonly WeaponFireProfile AutoRifleProfile = new(100f, (10f, 10, 15), (20f, 0, 12), (30f, 0, 10), (60f, 0, 6), (100f, 0, 3));

    // AK/LR300 only - own dedicated profile split off from AutoRifleProfile
    // (Lucas's own explicit request, 2026-08-13: "up the amount of bullets
    // per burst the AK and LR300 fire at distances 50 metres and beyond by
    // 30%") - MP5/Thompson/M16A2 still share the plain AutoRifleProfile
    // above, untouched, since bumping that directly would have silently
    // buffed all of them too. The old 30-60m band (3-6) is split at 50m so
    // "50m and beyond" can be isolated cleanly: 30-50m keeps the original
    // 3-6 unchanged, 50-60m and 60-100m both get the +30% bump (3-6 ->
    // 4-8, 1-3 -> 1-4).
    private static readonly WeaponFireProfile AkLr300Profile = new(100f, (10f, 10, 15), (20f, 0, 12), (30f, 0, 10), (50f, 0, 6), (60f, 0, 8), (100f, 0, 4));

    // M249/HMLMG only - own dedicated profile rather than sharing
    // AutoRifleProfile, since M249/HMLMG previously shared that profile
    // with AK/LR300/MP5/Thompson/M16A2 and buffing a shared profile would
    // silently buff all of those too. Went through two straight +30-35%
    // bumps on AutoRifleProfile's original numbers (2026-08-13 live
    // testing) before Lucas asked for a full ground-up revamp with its own
    // 7-band spread and a genuinely distinct 0-10m tier: real belt-fed LMG
    // sustained-fire behaviour - mag-dump until the target's dead, no
    // burst-then-pause at all this close (the same -1/-1 continuous
    // sentinel the M4 shotgun's own 0-10m band uses). Bands 10m and beyond
    // are Lucas's own explicit numbers, not scaled from anything.
    private static readonly WeaponFireProfile LmgProfile = new(100f, (10f, -1, -1), (20f, 0, 26), (30f, 0, 24), (50f, 0, 18), (75f, 0, 15), (90f, 0, 10), (100f, 0, 6));

    // First band split at exactly 10m (was one flat 0-15m tier) so the
    // "0-10m never rolls zero, everything past it can" rule applies at the
    // right distance instead of the whole original band's own boundary -
    // 0-10m keeps its original 8-12 minimum, 10-15m keeps the same max but
    // can now roll 0.
    private static readonly WeaponFireProfile CustomSmgProfile = new(100f, (10f, 8, 12), (15f, 0, 12), (30f, 0, 10), (60f, 0, 5), (100f, 0, 2));

    // Same 10m split as CustomSmgProfile above - original first band was
    // 0-30m.
    private static readonly WeaponFireProfile SemiAutoRifleProfile = new(100f, (10f, 3, 5), (30f, 0, 5), (60f, 0, 4), (100f, 0, 3));

    // Same 10m split as CustomSmgProfile above - original first band was
    // 0-30m.
    private static readonly WeaponFireProfile PistolProfile = new(100f, (10f, 5, 10), (30f, 0, 10), (60f, 0, 5), (100f, 0, 3));

    // Python only - own dedicated profile split off from PistolProfile
    // (Lucas's own explicit request, 2026-08-13): "toned down from less
    // than 30m" with a full 6-band spread of its own, noticeably more
    // conservative than the shared PistolProfile's 5-10/2-5/1-3 - a real
    // revolver's own limited (typically 6-round) cylinder and slow reload
    // makes "keep firing indefinitely at range" unrealistic the way an
    // auto-loading pistol can. 0-10m is a genuine continuous mag(cylinder)
    // dump (the same -1/-1 sentinel the LMG's own 0-10m band and the M4
    // shotgun's 0-10m sub-band use) - Lucas's own explicit numbers below,
    // not scaled from PistolProfile's.
    private static readonly WeaponFireProfile PythonProfile = new(100f, (10f, -1, -1), (20f, 0, 4), (30f, 0, 3), (40f, 0, 2), (50f, 0, 2), (100f, 0, 2));
    // Pump Shotgun + M4 Shotgun + SPAS-12 all share this now (Lucas's own
    // explicit 5-tier spec, 2026-08-13) - Pump was originally kept
    // separate/hardcoded continuous-only ("as it is a pump shotgun... no
    // realistic way to fire a burst"), but Lucas's own follow-up explicitly
    // asked for it to share this exact same banded shape too, so it's
    // merged in here rather than kept as its own now-redundant profile.
    // Double Barrel is still deliberately NOT part of this - see its own
    // profile below for why (real 2-shell break-action can't sustain a
    // variable burst the way a magazine/tube-fed shotgun can).
    //
    // EngagementRange went 30m -> 45m -> back down to 35m, all same day -
    // Lucas's own explicit follow-ups ("start shooting distance from 45m
    // instead of 30," then "let's drop the engagement distance from 45m to
    // start engaging from 30-35m"). The old 30-45m tier is now 30-35m
    // instead, same 0-1 rounds as before - just a narrower outer band, not
    // a different burst count.
    private static readonly WeaponFireProfile ShotgunBurstProfile = new(35f, ShotgunPursueRange, (10f, -1, -1), (15f, 2, 4), (20f, 1, 3), (25f, 0, 2), (30f, 0, 1), (35f, 0, 1));

    // Double Barrel only - explicitly kept separate (Lucas's own
    // instruction, 2026-08-13): "only ever continuous fire at less than 10
    // metres. No other variants of burst fire... for obvious reasons" - a
    // real break-action double barrel only ever holds 2 shells, so a
    // multi-round "burst" tier doesn't make real sense the way it does for
    // a magazine-fed M4/SPAS-12. EngagementRange dropped to 10m (from the
    // old shared 20m) to match - it simply doesn't fire at all beyond that.
    private static readonly WeaponFireProfile DoubleBarrelProfile = new(10f, ShotgunPursueRange);
    private static readonly WeaponFireProfile SniperProfile = new(150f);
    private static readonly WeaponFireProfile DefaultFireProfile = new(20f, ShotgunPursueRange);

    // Real shortnames confirmed via Bundles\items\*.json, not guessed -
    // ValidateCombatFireProfiles (called from OnServerInitialized alongside
    // the other three existing shortname validators) catches a typo here at
    // boot instead of it silently never matching.
    //
    // m16a2/pistol.prototype17 ARE included here, but only for their
    // EngagementRange/PursueRange - their actual burst COUNT still comes
    // entirely from the dedicated isM16A2/isP17 fixed-3-round path further
    // down (which runs and returns before the Bands on these two profiles
    // are ever consulted), untouched by this change. Live report
    // 2026-08-13: both weapons were missing from this table entirely, so
    // GetCombatFireProfile fell back to DefaultFireProfile's 20m range (the
    // same cautious default used for genuinely unknown weapons) - Lucas
    // caught this directly for the M16A2 ("it is a rifle... it shouldn't be
    // needing to come that close"), but it silently also capped the P17's
    // own single-fire-beyond-20m fallback (below) to never actually reach
    // beyond 20m in the first place, since the outer inEngagementRange gate
    // was blocking it before that logic ever mattered.
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

        // Live report 2026-08-13 - a real, live-fired weapon this table
        // was missing entirely (confirmed via Bundles\items\t1_smg.json:
        // "Handmade SMG", "Low damage and accuracy"). Grouped with the
        // Custom SMG rather than the named-SMG/auto-rifle tier - its own
        // real in-game description matches Custom SMG's already-established
        // "much lower tier weapon" characterization, not a proper SMG's.
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
    /// Distance-scaled bullet spread, 2026-08-13 - Lucas's own explicit
    /// request after live-fire testing: real weapon spread should be
    /// tighter close, wider far, rather than the "beam that always
    /// connects regardless of distance" every shot was before this. Real
    /// per-weapon aimCone/spread values aren't recoverable from this
    /// project's own tooling (they're compiled into the asset bundles, not
    /// exposed in Bundles\items\*.json the way shortnames/stats are, and
    /// GetAimCone() always takes Rust's own tight, server-authoritative
    /// aiming-tier branch regardless of a shooter's real aim state - see
    /// its own doc comment in FireAt). A live-fire test (Lucas personally
    /// unloading on stationary invincible bots at varying ranges, every
    /// weapon this table covers) was tried as a real calibration source,
    /// but came back essentially flat (~0.35-0.45m average offset-from-
    /// center at EVERY tested range, 20m through 100m+) - a human
    /// correcting their own aim shot-to-shot masks the weapon's real
    /// distance-scaling almost entirely, so it couldn't be used as a
    /// direct data-fit. Instead, deliberately designed: MinDegrees (spread
    /// at point-blank) to MaxDegrees (spread at 100m), linearly
    /// interpolated by live distance - the live-fire test's own flat
    /// ~0.4m baseline was used as a sanity anchor for the middle of each
    /// curve, and relative TIER ordering follows Lucas's own explicit
    /// real-world weapon knowledge: rifles stay controllable furthest,
    /// Thompson noticeably worse than AK/LR300/MP5 despite sharing a burst
    /// profile, pistols land in between, Custom SMG/Handmade SMG (already
    /// both real in-game "low tier, low accuracy" weapons) are the worst.
    /// Shotguns/snipers deliberately excluded - a shotgun's own multi-
    /// pellet ammo already has real spread built in, doubling up on top of
    /// that would be redundant; a bolt-action sniper being tack-precise is
    /// the whole point of the weapon class, not something to loosen.
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

        // Thompson - Lucas's own explicit call-out: noticeably worse than
        // AK/LR300/MP5 at range despite sharing the same burst profile.
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

        // Bows/crossbows (2026-09-21, Lucas's own live report: arrows read
        // as "a laser beam" - no spread, no drop). A bow is a hand-drawn
        // weapon, so it's the loosest tier here; the crossbows are steadier.
        // Arrow drop is layered on separately in FireAt.
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

    // Simulated arrow flight (2026-09-21). A bot's shot is resolved as an
    // instant server-side hit, so nothing physically drops an arrow; this
    // emulates it by aiming lower than a perfect line by whatever a
    // shooter fails to compensate for (each shot randomly compensates
    // 70-100% of the true drop, like a decent-but-not-perfect archer).
    private const float ArrowMuzzleSpeed = 55f;
    private const float ArrowGravity = 9.81f;
    private const float ArrowCompensationMin = 0.7f;

    /// <summary>
    /// Per-weapon fire-rate slowdown, 2026-08-13 - Lucas's own live-test
    /// report: the Python (a real revolver, single-action trigger pull per
    /// shot) was firing noticeably too fast at 30m+, reading unrealistic
    /// for the weapon class. Unlike the burst-band system (which only
    /// paces the GAP BETWEEN volleys), individual shots within a volley -
    /// and every shot for weapons using the generic distance-banded path -
    /// are otherwise gated purely by the real weapon.HasAttackCooldown()
    /// (unmodified repeatDelay), which this project has already confirmed
    /// (see CombatTickInterval's own doc comment) can read as unrealistically
    /// fast for some weapons. The multiplier here means an extra fraction
    /// of repeatDelay added on top of the real cooldown before the NEXT
    /// shot is allowed - applied via FireWithPacing below, not by touching
    /// repeatDelay itself (which would also affect real players using the
    /// same weapon). Empty/1f for anything not listed - no slowdown by
    /// default.
    ///
    /// Went through two flat (distance-independent) bumps first (1.3, then
    /// 1.3*1.2=1.56) before Lucas's own explicit correction, same day:
    /// under 30m was "fine" all along and shouldn't have been slowed at
    /// all - only 30m+ was ever the real complaint. NearMultiplier now
    /// stays at the weapon's real, unmodified rate (1f) below
    /// FarThresholdMeters; FarMultiplier (1.56 * 1.4 = 2.184, "an
    /// additional 40%" on top of the previous flat value) applies at/
    /// beyond it - see GetFireRateMultiplier below for how these two are
    /// actually selected per shot.
    /// </summary>
    private static readonly Dictionary<string, (float NearMultiplier, float FarMultiplier, float FarThresholdMeters)> WeaponFireRateMultiplier = new()
    {
        ["pistol.python"] = (1f, 2.184f, 30f),
    };

    /// <summary>
    /// See WeaponFireRateMultiplier's own doc comment. 1f (no slowdown) for
    /// any weapon not in the table at all.
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
    /// Linear interpolation between a weapon's own MinDegrees (point-blank)
    /// and MaxDegrees (100m+) - distance clamped to [0, 100] first so
    /// nothing beyond 100m extrapolates past the designed curve. Weapons
    /// with no entry (shotguns, snipers, anything unlisted) get 0 - no
    /// artificial spread added on top of whatever Rust's own real ammo/
    /// aimCone mechanics already do for them.
    /// </summary>
    // Global multiplier over the whole WeaponSpreadDegrees table - Lucas's
    // own live-test follow-up, 2026-08-13: even with distance-scaled spread
    // in, it was still landing ~8/10 rounds, reading as noticeably too
    // accurate. A flat widening applied here (rather than hand-editing
    // every tier's Min/Max) keeps the whole table's relative shape/tiering
    // intact while making every weapon uniformly less precise - the
    // correct single place to keep tuning this from if it still isn't
    // enough after another test pass. Started at 1.1 (+10%), bumped again
    // same day to 1.1 * 1.05 = 1.155 (+5% more, compounding on the first
    // pass, not a fresh +5% off the original table).
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
    /// -1/-1 is the "continuous, no burst cap" sentinel - either the whole
    /// profile has no Bands at all, or (M4 shotgun specifically) the
    /// current distance falls in a band explicitly marked continuous.
    /// Falls back to the longest-range band if distance somehow exceeds
    /// every defined band's own MaxDistance - shouldn't happen since the
    /// outer EngagementRange disengage check already prevents combat from
    /// reaching here at all beyond that, but a safe default regardless.
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
    /// Real Carbon/Oxide hook - fires for every damage application on any
    /// BaseCombatEntity, so this filters down to just our own survivors
    /// (FindSurvivorByPlayer, same pattern OnPlayerWound/OnPlayerDeath
    /// already use) taking damage from a real attacker (a live player or
    /// another one of our own bots - HitInfo.InitiatorPlayer is null for
    /// environmental damage like falls/fire/drowning, which shouldn't start
    /// a fight against nothing).
    /// </summary>
    private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
    {
        if (entity is not BasePlayer player)
        {
            return;
        }

        // Diagnostic only (2026-08-13, live report: "the bot seems to be
        // doing less damage than I do to it, per shot") - logs BOTH
        // directions (damage a survivor takes, and damage a survivor's own
        // shot deals to whoever it hit) so a real side-by-side per-shot
        // comparison is possible next test instead of guessing. Checked
        // FireAt/ServerUse first: damageModifier is a full 1f (no scale-
        // down applied there), and GetAimCone() always takes the tighter
        // aiming/server-authoritative branch server-side regardless of our
        // bot's real aiming state, so nothing found there artificially
        // narrows or widens the bot's own spread either - if there's a
        // real gap it's more likely hit-location variance (this session
        // already found real facing/aim-tracking gaps that could mean more
        // limb hits than headshots) or ordinary distance falloff, not an
        // explicit multiplier. This log is what actually settles it.
        if (info != null && info.damageTypes.Total() > 0f)
        {
            BasePlayer damageAttacker = info.InitiatorPlayer;
            Survivor attackerSurvivor = damageAttacker != null ? FindSurvivorByPlayer(damageAttacker) : null;
            Survivor victimSurvivor = FindSurvivorByPlayer(player);

            // shot-spread-diag (2026-08-13, Lucas's own follow-up request:
            // real bullet spread is "just a beam that always connects
            // regardless of distance," wants distance-scaled spread
            // implemented FROM real empirical data, not guessed values).
            // offsetFromCenter is the actual landed hit position's distance
            // from the target's own CenterPoint() - a neutral "how far off
            // dead-center did this shot land" measure regardless of
            // intended aim height (headshot vs body), paired with the real
            // shooter-to-target distance at the moment of the hit. Covers
            // hits only (a miss never reaches OnEntityTakeDamage at all) -
            // OnWeaponFired below covers total shots fired so a hit/miss
            // RATE per distance can still be worked out even without a
            // logged line for the misses themselves.
            float shotDistance = damageAttacker != null ? Vector3.Distance(damageAttacker.transform.position, player.transform.position) : -1f;
            float offsetFromCenter = Vector3.Distance(info.HitPositionWorld, player.CenterPoint());

            if (attackerSurvivor != null && victimSurvivor == null)
            {
                Puts($"damage-diag: '{attackerSurvivor.Character.Alias}' dealt {info.damageTypes.Total():F1} damage to '{player.displayName}' (hit: {info.boneArea}, distance: {shotDistance:F1}m, offset-from-center: {offsetFromCenter:F2}m).");
            }
            else if (victimSurvivor != null && attackerSurvivor == null && damageAttacker != null)
            {
                Puts($"damage-diag: '{victimSurvivor.Character.Alias}' took {info.damageTypes.Total():F1} damage from '{damageAttacker.displayName}' (hit: {info.boneArea}, distance: {shotDistance:F1}m, offset-from-center: {offsetFromCenter:F2}m).");
            }

            // Piece 2 of the tactical-decision-system foundation
            // (2026-08-24, see LivingRust.CombatDecisionTracking.cs's own
            // doc comment) - deliberately a SEPARATE check from the two
            // above (not folded into either), since those two are gated on
            // "exactly one side is a survivor" (avoids double-logging a
            // bot-vs-bot fight) but this tally needs to fire for EVERY hit
            // a survivor lands on its own current combat target,
            // regardless of what the target actually is (real player,
            // hostile scientist, another bot).
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

        // See _botsInvincible's own doc comment (LivingRust.Debug.cs) -
        // restores full health (and clears wounded, in case a single hit
        // already dropped it that far) on every hit while testing is
        // active, rather than trying to cancel the damage at its source.
        if (_botsInvincible)
        {
            player.InitializeHealth(player.MaxHealth(), player.MaxHealth());

            if (player.IsWounded())
            {
                player.StopWounded();
            }
        }

        // Initiator is a plain BaseEntity - InitiatorPlayer (used
        // previously) only ever resolves for a real player attacker
        // (Initiator.ToPlayer(), confirmed via decompile), so it silently
        // returned null for an animal attacker and this hook never fired
        // for one at all. IsHuntablePredator narrows this to the animal
        // interaction scope (Bear/Polarbear/Wolf2 only, reactive-only) -
        // see its own doc comment.
        BaseEntity initiator = info?.Initiator;
        BaseCombatEntity attacker = initiator as BasePlayer;

        // See _disablePlayerCombat's own doc comment (LivingRust.Debug.cs) -
        // a real player attacker is dropped entirely while this is on;
        // never suppresses an animal attacker below.
        if (attacker != null && _disablePlayerCombat)
        {
            attacker = null;
        }

        // IsHuntableThreat (not IsHuntablePredator alone) - a hostile
        // scientist shooting a bot in the back needs to trigger the exact
        // same "turn and fight" reactive response a player or animal
        // attacker already gets (Lucas's own explicit 2026-08-14 spec) -
        // this is the whole mechanism that makes that work, no LOS/facing
        // check involved here at all.
        if (attacker == null && IsHuntableThreat(initiator))
        {
            attacker = initiator as BaseCombatEntity;
        }

        if (attacker == null || attacker == player)
        {
            return;
        }

        // See TryTriggerReloadRetreat's own doc comment - real damage
        // landing while a recent mid-fight reload's exposure window is
        // still open is what actually triggers the retreat, not the
        // reload itself.
        TryTriggerReloadRetreat(survivor, player, attacker);

        StartCombat(survivor, attacker);
    }

    /// <summary>
    /// shot-fired-diag (2026-08-13) - the counterpart to shot-spread-diag
    /// in OnEntityTakeDamage above. Real Carbon/Oxide hook, patched from
    /// BaseProjectile.CLProject - the server-side handler for a real
    /// CLIENT's own fire RPC. Confirmed via the Carbon hook patch metadata
    /// this is keyed specifically off CLProject, which structurally never
    /// fires for our own bots (FireAt calls weapon.ServerUse() directly,
    /// same as every other bot-firing path this project uses - no client
    /// RPC involved at all) - so this hook, by construction, only ever
    /// logs a genuine human player's own real gunfire. Exactly the
    /// reference data Lucas asked for ("trace me shooting to see where my
    /// bullets land") - every fired shot logs here regardless of hit or
    /// miss, while only hits get a matching shot-spread-diag line, so a
    /// real hit/miss ratio at any given distance can be reconstructed from
    /// the two logs together even without an impact position for the
    /// misses themselves.
    /// </summary>
    private void OnWeaponFired(BaseProjectile projectile, BasePlayer player, ItemModProjectile mod, ProtoBuf.ProjectileShoot shoot)
    {
        Puts($"shot-fired-diag: '{player.displayName}' fired '{projectile.GetOwnerItem()?.info.shortname}' from {player.transform.position}.");
    }

    /// <summary>
    /// Equips whichever real ranged weapon this survivor owns that (a) has
    /// the highest WeaponGearScore and (b) actually has usable ammo right
    /// now (loaded, or reloadable from the rest of the inventory) -
    /// 2026-08-16, Lucas's own explicit request: "the bot equips the
    /// highest scored weapon that it has within its inventory at the time,
    /// if it runs out of ammunition for that weapon system it swaps to the
    /// next best available scored weapon (5.56 runs out, it swaps to a
    /// thompson and uses that, if no pistol ammunition it swaps to melee /
    /// tools)."
    ///
    /// Tries candidates in descending score order (WeaponGearScore,
    /// unlisted weapons scoring 0 - same fallback GetWeaponGearScore
    /// already uses), moving each into the belt slot and equipping it
    /// live (UpdateActiveItem) to get a real BaseProjectile with an actual
    /// magazine to check - an unequipped Item has no live magazine of its
    /// own to query directly, so this is the only reliable way to test
    /// "does this weapon actually have ammo" rather than guessing at ammo-
    /// type matching ourselves. Real ServerTryReload (already used
    /// elsewhere in this file) tops it off from inventory first, so a
    /// weapon with an empty clip but loose matching ammo elsewhere still
    /// counts as usable. Visibly cycles through candidates one at a time
    /// (brief weapon-switch flicker) - acceptable since this only runs at
    /// combat start and on an actual ammo-out swap, not every tick.
    /// Returns false (equips nothing further) if NOTHING owned has any
    /// ammo at all - callers fall back to EquipBestWeaponForDisplay/melee,
    /// same "no melee-vs-players yet" scope this file already has.
    /// </summary>
    private bool TryEquipBestArmedWeapon(Survivor survivor, out BaseProjectile weapon)
    {
        weapon = null;
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return false;
        }

        // Fast path: the weapon already held needs no MoveToContainer/
        // UpdateActiveItem churn at all if it already has (or can reload
        // into having) ammo - by far the common case, since this is
        // usually called with an already-loaded weapon in hand (fight just
        // started, or the swap check only runs at all because the PREVIOUS
        // weapon just ran dry, meaning whatever's held now is exactly what
        // we're being asked to verify). Skipping the loop entirely here
        // also means the known belt-shuffle glitch below basically never
        // triggers in practice, not just gets patched over.
        if (npc.GetHeldEntity() is BaseProjectile currentWeapon
            && Array.IndexOf(WeaponPriority, currentWeapon.GetOwnerItem()?.info.shortname) >= 0)
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

        // Excludes anything also in MeleeToolPriority (2026-08-21, real
        // live bug - QuietLooter observed visibly stuck flickering between
        // mace and knife.combat while KyGotDSL's kept it pinned in an
        // ongoing firefight with zero ranged ammo left anywhere). WeaponPriority's
        // own tail deliberately includes every melee weapon too (for the
        // still-deferred melee-vs-players scope), but this loop calls
        // UpdateActiveItem on a candidate BEFORE checking whether it's
        // actually a BaseProjectile - a melee item always fails that check
        // and gets discarded, but not before visibly becoming the held
        // item for a frame. With zero ranged ammo owned, every single one
        // of those melee entries got tried and discarded in descending
        // WeaponPriority order EVERY time this re-ran (every ammo-out
        // mid-fight swap, see its own call site's doc comment) - a real
        // visible equip/discard churn through mace, then knife.combat,
        // then the rest, repeating for as long as the fight continued.
        // Melee items have no magazine to test in the first place, so they
        // never belonged in this candidate list at all.
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
                // Same known glitch OrganizeBelt's own fix already exists
                // for (see its doc comment: repeated MoveToContainer/
                // UpdateActiveItem churn - which trying multiple candidates
                // here is exactly - "can leave the client's held-item
                // VISUAL out of sync... a bot visibly swinging an
                // 'invisible hand'"). One more explicit UpdateActiveItem for
                // the FINAL winner, as its own separate step after all the
                // candidate-cycling above has settled (not interleaved with
                // it), matching OrganizeBelt's own proven fix pattern
                // exactly rather than trusting the in-loop call alone. Still
                // reported live even with this in place, though (2026-08-16
                // screenshot evidence) - ForceRefreshHeldEntity
                // (LivingRust.Looting.cs) is the real, stronger fix:
                // UpdateActiveItem alone apparently doesn't reliably push
                // the held entity's own fresh network snapshot for a
                // connectionless bot, same underlying gap every ModelState
                // fix in this project already has to work around.
                npc.UpdateActiveItem(candidateWeapon.GetOwnerItem().uid);
                ForceRefreshHeldEntity(npc);

                weapon = candidateWeapon;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Below this required turn (degrees), a fresh engagement's first shot
    /// fires with no telegraph at all - see
    /// FirstShotTelegraphDegreesPerSecond's own doc comment for the live
    /// bug this whole telegraph exists to fix. A small correction (already
    /// roughly facing the attacker) doesn't need one; a real client's own
    /// rotation interpolation keeps up fine within a single tick for a
    /// turn this small.
    /// </summary>
    private const float FirstShotTelegraphMinTurnDegrees = 20f;

    /// <summary>
    /// Real turn-and-aim time budget for a fresh engagement's first shot
    /// (2026-08-21, Lucas's own explicit live report: a bot facing away
    /// from a Scientist "instantly shoots... without facing it," visually
    /// "aim its rifle at NOTHING and shoot backwards"). AimAtPlayer/
    /// FireAt's own diagnostics (los-diag/aim-diag/fire-diag) confirmed
    /// the SERVER-side rotation is correct the instant it's set (delta
    /// consistently under 0.2 deg across an entire live fight) - the real
    /// gap is that a fresh engagement can require a huge instant rotation
    /// (attacker behind/beside the bot) that a real player physically
    /// couldn't snap-turn through in zero time, so Rust's client almost
    /// certainly smooths/interpolates a change that large over several
    /// frames the same way it would a real player's mouse movement -
    /// firing the same tick the rotation is SET (not yet visually
    /// rendered) means the muzzle flash/shot can reach an observer's
    /// client before the turn has visually finished, reading as "fired
    /// without looking." Scaled by how far the bot actually needs to turn
    /// (a 180-degree behind-the-back shot gets noticeably longer than a
    /// 30-degree correction), capped at FirstShotTelegraphMaxSeconds -
    /// not Rust's own real turn speed (no public figure for that), a
    /// reasonable approximation tuned to be visually believable rather
    /// than measured.
    /// </summary>
    private const float FirstShotTelegraphDegreesPerSecond = 540f;

    private const float FirstShotTelegraphMaxSeconds = 0.6f;

    /// <summary>
    /// Tiny REAL position nudge applied every tick during the telegraph,
    /// straight toward the attacker - Lucas's own explicit suggestion
    /// (2026-08-21): reuse the same "the client's own animator plausibly
    /// keys off genuine velocity, not a static rotation flag alone"
    /// insight already documented for ghost-route movement (StartGhostRoute's
    /// own investigation notes: native NavMeshAgent produces real
    /// continuous velocity every frame; a bare MovePosition-only step does
    /// not). A rotation-only snap during the telegraph might not be enough
    /// on its own for the client to actually process/re-render the new
    /// facing - imperceptibly small in position (same idea as a manual
    /// /lr.follow nudge), but real movement every tick, giving the same
    /// velocity-driven update path a visibly-moving character already
    /// uses correctly.
    /// </summary>
    private const float FirstShotTelegraphNudgeDistance = 0.02f;

    /// <summary>
    /// Per-side amplitude of the combat jitter strafe (2026-08-25) -
    /// derived from Lucas's own real /lr.debug.traceme trace of himself
    /// strafing side to side (roughly a 1-1.3m half-swing, not a tiny
    /// wobble) rather than an arbitrary guess. See its own use site's doc
    /// comment for the full reasoning.
    /// </summary>
    private const float CombatJitterAmplitude = 1.2f;

    /// <summary>
    /// Full oscillation period - matches the real cadence observed in
    /// Lucas's own trace (roughly 1.8-2s peak-to-peak).
    /// </summary>
    private const float CombatJitterPeriodSeconds = 1.8f;

    /// <summary>
    /// Minimum angle off the survivor's own real pre-engagement facing for
    /// a fight's start to count as a genuine ambush rather than a fight the
    /// survivor was already looking toward - see wasAttackedFromBehind's
    /// own doc comment (StartCombat) for the full reasoning. 110 degrees
    /// (not a flat 90) so a shot from slightly off to the side, still
    /// within a real peripheral glance, doesn't trigger the bigger reaction
    /// burst below - this is specifically for "didn't see it coming."
    /// </summary>
    private const float RearAmbushAngleThreshold = 110f;

    /// <summary>
    /// Extra rounds added to BOTH ends of the normal distance-banded burst
    /// range on the very first volley of a fight that started as a genuine
    /// ambush (2026-08-24, Lucas's own explicit request: "return fire with
    /// a medium-large burst of ammunition"). A flat bonus rather than a
    /// multiplier - simpler, and avoids a weapon whose normal band is
    /// already 0 (e.g. a pistol at long range) staying at 0*multiplier=0
    /// despite the reaction. Capped by RearAmbushBurstCap so this never
    /// reads as an absurd full-mag dump regardless of weapon.
    /// </summary>
    private const int RearAmbushBurstBonusRounds = 3;

    private const int RearAmbushBurstCap = 10;

    /// <summary>
    /// Entry point into Phase 1 combat. Cancels whatever the survivor was
    /// doing (movement/attack - the loot task's own state, held only in a
    /// closure chain per ContinueLootTask's own doc comment, has no external
    /// resume point, so this only remembers CurrentTask and restarts
    /// StartLootForResourcesTask fresh on disengage rather than trying to
    /// resume mid-search) and starts a real per-tick fight against attacker.
    /// Bails out entirely (no melee-vs-players fallback yet - explicitly
    /// deferred) if the survivor doesn't currently own/equip a real ranged
    /// weapon.
    /// </summary>
    private void StartCombat(Survivor survivor, BaseCombatEntity attacker)
    {
        Guid characterId = survivor.Character.Id;

        if (_activeCombat.ContainsKey(characterId))
        {
            // Animal attacks interrupt an ongoing PLAYER fight - Lucas's
            // own explicit spec, 2026-08-14: "bears, crocs and wolves can
            // pose a proper threat if not handled quickly... regardless if
            // a real player is nearby or not." Doesn't retarget the other
            // direction (already fighting an animal, a player also
            // attacks) - the animal fight runs to completion first, same
            // "keeps this simple" reasoning as before for every other
            // retarget case. The interrupted player is remembered
            // (_pendingResumeCombatTarget) so EndCombat can snap straight
            // back to it - "re-engage the player as quick as possible" -
            // the instant the animal fight ends, instead of falling
            // through to the normal loot-task resume.
            if (IsHuntablePredator(attacker)
                && _activeCombatTarget.TryGetValue(characterId, out BaseCombatEntity currentTarget)
                && currentTarget is BasePlayer
                && currentTarget != attacker)
            {
                _pendingResumeCombatTarget[characterId] = currentTarget;
                CancelActiveCombat(characterId);
                // Falls through to the normal setup below, now targeting
                // the animal instead of returning.
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

        // Hard avoid (2026-09-15) - Bear/Crocodile never get fought here,
        // regardless of what's checked below (armed, melee tool owned,
        // already mid-fight-with-a-player retarget above, any of it). Every
        // call path that can reach StartCombat with one of these two as the
        // attacker funnels through this single check - reactive
        // (OnEntityTakeDamage), on-sight (TryStartAnimalOnSightCombat), and
        // deliberate meat/hide hunting (StartAnimalHuntEngagement, though
        // TryFindNearestHuntableAnimal's own candidate list no longer offers
        // these two up in the first place - see its doc comment,
        // LivingRust.HomeSiteStrategy.cs) - so this one branch is the real
        // source of truth for "never fight these two," not something each
        // caller needs to remember to check itself. See IsHardAvoidAnimal's
        // own doc comment for why these two specifically.
        if (IsHardAvoidAnimal(attacker, npc))
        {
            _pendingResumeCombatTarget.Remove(characterId);
            StartFleeingFromThreat(survivor, attacker, $"'{survivor.Character.Alias}' spotted a '{attacker.ShortPrefabName}' - hard avoid, fleeing rather than fighting it.", safeDistanceOverride: HardAvoidAnimalRadius);
            return;
        }

        // Weapon check moved BEFORE both Cancel calls below - live report
        // 2026-08-14, a real regression from on-sight detection (added the
        // same day): RunOnSightDetectionScan runs every
        // OnSightDetectionIntervalSeconds (1s) and calls StartCombat on
        // ANY survivor not already in _activeCombat within range/LOS of a
        // real player - but a survivor with no ranged weapon NEVER actually
        // enters _activeCombat (this exact check below bails first), so
        // every single scan tick kept re-finding the same unarmed survivor
        // and re-running this method from the top. With the Cancel calls
        // ahead of this check, that meant CancelActiveMovement was wiping
        // out an unarmed bot's in-progress loot-task walk once every
        // second, forever, for as long as it stayed unarmed and in sight -
        // which is exactly the situation a fresh spawn is in until it
        // manages to loot a real weapon. Confirmed via trace/log: several
        // bots showed zero loot-task progress and never fired a single
        // shot despite being "in sight" the whole time, with no evidence
        // their walk ever legitimately timed out or hit Stuck/NoPath long
        // enough to trigger the loot task's own stuck-recovery ladder -
        // it was being cancelled out from under it every second instead.
        // Now this returns before EVER touching movement/attack state if
        // the survivor can't actually fight, so on-sight detection (and
        // the identical reactive OnEntityTakeDamage path) can no longer
        // repeatedly interrupt an unarmed bot's current task for nothing.
        //
        // TryEquipBestArmedWeapon (2026-08-16), not the older
        // EquipBestWeaponForDisplay, decides what to fight with now - see
        // its own doc comment. Genuinely nothing armed (owns no ranged
        // weapon with any usable ammo at all) still gets
        // EquipBestWeaponForDisplay's own best-effort pick for DISPLAY
        // purposes (a bot standing there holding its empty rifle still
        // looks more correct than bare hands), it just can't actually
        // fight with it - same unarmed branch as before.
        bool hasArmedWeapon = TryEquipBestArmedWeapon(survivor, out BaseProjectile equippedWeapon);

        if (!hasArmedWeapon)
        {
            EquipBestWeaponForDisplay(survivor);

            // Real melee-vs-players/animals (2026-09-01, Lucas's own
            // explicit request: "work on melee pvp/pve... important for
            // bots to farm animals for cloth and to protect themselves") -
            // fights back with whatever real melee tool it owns (rock
            // included - see HasAnyMeleeTool's own doc comment) rather
            // than silently bailing/fleeing, which is what this branch
            // used to do unconditionally for anything without a real
            // ranged weapon ("melee-vs-players is a separate, still-
            // deferred scope" per this project's own prior comment here).
            // Checked BEFORE the animal-flee branch below - a melee-armed
            // survivor should fight a real threat, not run from it.
            if (HasAnyMeleeTool(npc))
            {
                StartMeleeCombat(survivor, attacker);
                _pendingResumeCombatTarget.Remove(characterId);
                return;
            }

            // Genuinely nothing to fight with at all (lost even the
            // starting rock somehow) - unchanged fallback behaviour.
            //
            // Stag explicitly excluded (2026-08-15, Lucas's own correction) -
            // it's IsHuntablePredator (a valid on-sight ENGAGE target for an
            // ARMED bot hunting it for meat/hide) but never actually attacks
            // a survivor and flees FROM players/bots itself, same as real
            // Rust deer AI. Removing the armed-only gate on animal on-sight
            // detection earlier this session meant unarmed bots started
            // proactively fleeing Stag too via this exact branch, which
            // isn't correct - a passive animal doesn't warrant a flee
            // reaction just because it's nearby.
            if (IsHuntableThreat(attacker) && attacker is not Stag)
            {
                StartFleeingFromThreat(survivor, attacker);
            }

            // A player interrupt that couldn't actually be completed (see
            // the retarget branch above) leaves no dangling state - this
            // can only be reached here if the survivor lost its weapon in
            // the same tick the interrupt fired, an extreme edge case, but
            // cheap to guard regardless.
            _pendingResumeCombatTarget.Remove(characterId);

            return;
        }

        CancelActiveMovement(survivor);
        CancelActiveAttack(characterId);
        CancelActiveRecycling(characterId);
        StopExtendedOreSearch(characterId);

        TaskType previousTask = survivor.Character.CurrentTask;

        Puts($"'{survivor.Character.Alias}' engaging '{GetAttackerDisplayName(attacker)}' in combat.");

        // Movement is handled entirely by StartFollowing (LivingRust.
        // Commands.cs) now, not a bespoke system here - see its own doc
        // comment for why. No NavMeshAgent-disable needed either; that was
        // only ever there to stop the agent fighting a manual rotation
        // trick that's no longer used. StartFollowing manages the agent
        // itself as part of normal native movement, and runs on its own
        // independent tick cadence - fire-and-forget from here, no need to
        // re-issue it from the combat tick below. VISIBLE FACING while
        // stopped is deliberately NOT left to StartFollowing though -
        // skipIdleFacing:true below hands that entirely to this file's own
        // AimAtPlayer instead, see skipIdleFacing's own doc comment for why
        // the two fighting over the same viewAngles value was itself the
        // bug (stale walk-facing visible for seconds while already firing).
        string weaponShortname = equippedWeapon.GetOwnerItem()?.info.shortname ?? string.Empty;
        WeaponFireProfile fireProfile = GetCombatFireProfile(weaponShortname);
        float engagementRange = fireProfile.EngagementRange;
        float pursueRange = fireProfile.PursueRange;

        // Recomputed on an ammo-out weapon swap now too (2026-08-16 - see
        // the out-of-ammo branch below), not truly fixed for the whole
        // fight anymore - see PostBurstAdvanceStepMinMeters' own doc
        // comment for the burst-vs-continuous distinction itself. True for
        // anything that actually fires in bursts (M16A2/P17's own fixed
        // mechanic, or any profile with real Bands - the M4 shotgun's
        // 10-20m sub-band counts even though its 0-10m sub-band is
        // continuous, since SOME of its engagement range does burst); false
        // for genuinely continuous weapons (pump/SPAS/double shotgun,
        // snipers), which have no burst-completion event to hook into and
        // so keep using the periodic timer for gap-closing instead.
        bool isShotgunWeapon = ShotgunShortnames.Contains(weaponShortname);

        // Real "counter-snipe" carve-out (2026-08-24, Lucas's own explicit
        // request): "if it gets hit by any weapon it CAN engage the player
        // or bot with a semi auto or full auto weapon at any distance,
        // shotguns etc obviously can't because unrealistic." Excludes
        // shotguns (already excluded above from burst pacing for the same
        // real-world "useless past 10-20m" reasoning) AND the two bolt-
        // action sniper shortnames - deliberately NOT because a sniper
        // can't shoot far (SniperProfile's own 150m EngagementRange is
        // already the most generous in the whole table), but because
        // Lucas's own wording specifically named "semi auto or full auto,"
        // and a bolt-action's single-shot-per-trigger-pull cadence has no
        // real "furthest tier burst rate" to fall back to the way an
        // auto/semi-auto weapon's Bands table does. Every other weapon
        // (pistols/revolvers/SMGs/rifles) qualifies.
        bool supportsLongRangeCounterFire = !isShotgunWeapon
            && weaponShortname != "rifle.l96"
            && weaponShortname != "rifle.bolt";

        // Shotguns explicitly excluded even though the M4/SPAS-12 now have
        // real Bands (would otherwise flag them true) - Lucas's own
        // explicit instruction, 2026-08-13: "strictly with shotguns also,
        // close the distance quick." The smaller, reactive post-burst nudge
        // (PostBurstAdvanceStepMinMeters, 3-6m) exists specifically to NOT
        // interrupt an active exchange for weapons that are actually
        // effective at range - a shotgun is the opposite case: it's
        // useless outside ~10-20m to begin with, so aggressively closing
        // distance on every tick of a short, fast timer (see
        // ShotgunAdvanceIntervalSeconds below) matters far more than
        // avoiding a mid-burst interruption.
        bool weaponUsesBurstPacing = !isShotgunWeapon
            && (weaponShortname == "m16a2"
                || weaponShortname == "pistol.prototype17"
                || (fireProfile.Bands != null && fireProfile.Bands.Length > 0));

        // Shotguns get a much shorter cadence than every other weapon's
        // shared AdvanceIntervalSeconds (4s) - same "close the distance
        // quick" instruction. Combined with weaponUsesBurstPacing being
        // forced false above, a shotgun bot re-evaluates and pushes closer
        // via the periodic-timer path (below) roughly 2.5x as often as
        // anything else in this system.
        float advanceIntervalSeconds = isShotgunWeapon ? ShotgunAdvanceIntervalSeconds : AdvanceIntervalSeconds;

        StartFollowing(survivor, attacker, engagementRange, skipIdleFacing: true);

        // Advance-over-time state, per fight - see AdvanceIntervalSeconds'
        // own doc comment for why this exists.
        float currentHoldDistance = engagementRange;
        float nextAdvanceTime = Time.realtimeSinceStartup + advanceIntervalSeconds;

        // Set the instant a burst finishes (see PostBurstAdvanceStepMinMeters'
        // own doc comment) - consumed and reset every tick regardless of
        // whether it was actually acted on.
        bool pendingPostBurstAdvance = false;

        // Last position the bot actually SAW the attacker at, refreshed
        // every tick LOS holds. Lucas's own explicit correction 2026-08-13:
        // pushing closer while blind shouldn't mean literally tracking the
        // attacker's live, real-time position through a wall (that's a
        // wallhack, not "pushing a fight") - it should mean walking to
        // wherever they were last actually seen, the same information a
        // real player would have. Starts at the attacker's position right
        // now since combat only ever starts from taking a hit, which is
        // itself pretty good evidence of a clear line at that instant.
        Vector3 lastKnownPosition = attacker.transform.position;

        // See FirstShotTelegraphDegreesPerSecond's own doc comment - the
        // survivor's real facing right at the moment combat starts,
        // captured BEFORE AimAtPlayer ever touches it this fight, so the
        // first real shot attempt can measure how far it actually needs to
        // turn. hasFiredFirstShotThisFight/firstShotTelegraphEndsAt are
        // per-fight (this StartCombat call's own closure), not per-target -
        // a rapid re-engagement (a fresh StartCombat call) gets its own
        // fresh telegraph, same as a genuinely new fight would.
        float preEngagementFacingYaw = npc.transform.rotation.eulerAngles.y;
        bool hasFiredFirstShotThisFight = false;
        float firstShotTelegraphEndsAt = -1f;

        // Real ambush detection (2026-08-24, Lucas's own explicit request) -
        // checked ONCE at the exact instant this fight starts, against the
        // survivor's own real facing right then, before AimAtPlayer ever
        // touches it (same moment preEngagementFacingYaw is captured from,
        // right above). A genuine shot from outside the survivor's forward
        // view - an on-sight engagement, or a player peeking someone who's
        // already looking at them, naturally comes in near 0 degrees here
        // and never counts. Only drives the bigger first-volley reaction
        // below (hasFiredReactionBurstThisFight) - the gradual "turn around,
        // not instantly" itself is already handled for free by the existing
        // FirstShotTelegraph mechanic right above (a real ambush naturally
        // rolls a bigger turnNeededDegrees there too). Everything after the
        // reaction burst - taking cover if hurt enough, re-engaging normally -
        // is already emergent from the existing tactical decision system
        // (LivingRust.CombatTacticalDecisions.cs) once this tick loop is
        // running as an ordinary fight, no new state needed for that part.
        Vector3 rearCheckDirection = attacker.transform.position - npc.transform.position;
        rearCheckDirection.y = 0f;
        bool wasAttackedFromBehind = rearCheckDirection.sqrMagnitude > 0.01f
            && Vector3.Angle(npc.transform.forward, rearCheckDirection) >= RearAmbushAngleThreshold;
        bool hasFiredReactionBurstThisFight = false;

        // Tracks whether the PREVIOUS tick had LOS, so a change can be
        // reacted to immediately (see the movement-mode block below) rather
        // than only ever noticed the next time the periodic advance timer
        // happens to fire. Starts true for the same reason lastKnownPosition
        // does - combat only ever starts from taking a hit.
        bool wasLosLastTick = true;

        // Seeded as "already regained, zero delay" rather than the -1f
        // sentinel (which means "not currently regaining LOS," used further
        // down once combat is underway) - live report 2026-08-14: bots were
        // visibly hesitating "a second or two" before returning fire,
        // sometimes much longer. Root cause: the -1f sentinel here meant the
        // very FIRST combat tick, if the attacker was already visible (the
        // normal case - you're either reacting to being shot by someone you
        // can see, or on-sight detection already confirmed a live sightline
        // before ever calling StartCombat), got treated identically to
        // "just regained LOS after losing it mid-fight" and rolled a full
        // reaction-time band (up to LosRegainReactionExtremeSeconds, 3s
        // flat, at 100m+) before the bot was even allowed to fire back. That
        // band exists to simulate the beat a player needs to notice a
        // target has come back into view - it was never meant to gate the
        // very first shot of a fight the bot is already looking straight
        // at. If the attacker ISN'T visible on that first tick, the
        // existing "else" branch below (rawLos false) immediately resets
        // this back to the real -1f sentinel anyway, so genuine
        // lost-then-regained LOS later in the same fight is completely
        // unaffected by this change.
        float losRegainedAt = Time.realtimeSinceStartup;
        float currentLosRegainDelay = 0f;

        // Last real-time a rawLos check actually came back true (2026-08-16,
        // Lucas's own explicit request: "if the bot doesn't engage any
        // player within 15 seconds it should de-aggro") - starts now for
        // the same "combat only ever starts from taking a hit" reasoning
        // every other seed value here uses. See the NoRealEngagementSeconds
        // disengage check below for what this actually gates.
        float lastRealLosTime = Time.realtimeSinceStartup;

        // Burst-fire state, per fight - covers both the M16A2/Prototype17
        // fixed-3-round path (untouched, shipped previously) and the new
        // distance-banded random-burst path below. See decompile note on
        // FireAt's own doc comment: Rust's server never auto-fires a burst
        // on its own, ServerUse() is always exactly one shot, so any
        // multi-shot volley needs to be tracked ourselves regardless of
        // which of the two systems is driving it.
        int burstShotsFired = 0;
        int burstTargetCount = 0;
        float nextBurstAllowedTime = 0f;

        // See SprintFireSettleSeconds' own doc comment. NegativeInfinity so
        // a fight that never sprints at all (already in range when combat
        // starts) doesn't need to wait out a settle window it never
        // triggered.
        float lastSprintingTime = float.NegativeInfinity;

        // See WeaponFireRateMultiplier's own doc comment - an extra, OUR
        // OWN cooldown layered on top of the real weapon.HasAttackCooldown()
        // gate, for weapons whose real repeatDelay alone still fires too
        // fast. 0f/no-op for anything not in that table.
        float nextShotAllowedTime = 0f;

        // Every FireAt call in this fight goes through here instead of
        // calling it directly, so WeaponFireRateMultiplier's extra pacing
        // applies uniformly regardless of which of the (currently 4) paths
        // below actually fires - fixed-burst, paced-single, continuous, or
        // the generic distance-banded system.
        void FireWithPacing(BasePlayer shooterNpc, BaseProjectile shooterWeapon, BaseCombatEntity shooterTarget, float shooterDistance)
        {
            FireAt(shooterNpc, shooterWeapon, shooterTarget, weaponShortname, shooterDistance);

            float multiplier = GetFireRateMultiplier(weaponShortname, shooterDistance);

            if (multiplier > 1f)
            {
                nextShotAllowedTime = Time.realtimeSinceStartup + shooterWeapon.repeatDelay * (multiplier - 1f);
            }

            // Realistic bow cadence (2026-09-21, Lucas's own live report: bots
            // fire bows far faster than is physically possible). A bow needs
            // a draw/nock between arrows, which the raw weapon cooldown
            // doesn't model, so a floor on the time between shots applies.
            if (BowMinShotIntervalSeconds.TryGetValue(weaponShortname, out float minInterval))
            {
                nextShotAllowedTime = Mathf.Max(nextShotAllowedTime, Time.realtimeSinceStartup + minInterval);
            }
        }

        // Combat jitter strafe (2026-08-25, Lucas's own explicit request +
        // real evidence from his own /lr.debug.traceme trace: "the bot
        // still just hangs and stands still whilst firing... use a jitter
        // movement like my trace... a back and forth motion to be hard to
        // shoot"). A pure per-tick sine-DERIVATIVE delta (not an absolute
        // position write) - bounded and self-correcting by construction,
        // so it can run every tick for as long as it's called with no
        // drift, no anchor point to track/reset. Deliberately reads the
        // SHAPE of Lucas's own trace (a real lateral strafe, ~1-1.3m
        // half-amplitude, ~1.8-2s period), not the literal coordinates -
        // see CombatJitterAmplitude/CombatJitterPeriodSeconds' own values.
        // Perpendicular to the CURRENT direction toward the attacker every
        // call (not a fixed world-space axis), so it always reads as
        // strafing relative to keeping them in view even if either
        // position has shifted. A local function (not inline) specifically
        // so it can be called from both the normal firing-hold path AND
        // the reload-exposure-window path (Lucas's own explicit follow-up:
        // "it still stands still whilst reloading... at least use the
        // strafing whilst reloading") without duplicating the math.
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

            float distance = Vector3.Distance(currentNpc.transform.position, attacker.transform.position);

            // Give-up distance is PursueRange, not the real firing
            // EngagementRange - see WeaponFireProfile's own doc comment on
            // why these are separate. Using EngagementRange here (a
            // shotgun's real ~15-20m) meant a bot shot at from further away
            // than that disengaged on the very first tick, before
            // StartFollowing (already told to hold at EngagementRange) ever
            // got a chance to close the gap - looked exactly like "gives up
            // and goes back to looting instead of chasing." Skipped
            // entirely for animal targets (Lucas's own explicit 2026-08-14
            // wording: "close the distance on animals / follow them to
            // ensure they kill them") - real animal AI flees when hurt, so
            // without this a bot could lose a wounded, easily-finishable
            // animal to PursueRange the instant it bolted, right before the
            // planned harvesting follow-up would ever get a corpse to work
            // with. StartFollowing itself is what actually closes the
            // distance (already re-issued toward the target's live
            // position via the advance system below) - this bypass just
            // stops the fight ending before that chase gets a real chance.
            // supportsLongRangeCounterFire also bypasses the distance
            // give-up entirely (2026-08-24, Lucas's own explicit request:
            // "if the bot gets damaged outside of its engage distance...
            // it engages the fighting player... 200 metres away or 1000m
            // doesn't matter") - a semi/full-auto-armed bot that's actively
            // being shot at from extreme range shouldn't give up on the
            // fight just because PursueRange (a much shorter "is this
            // worth physically chasing" distance) says it's far. The fight
            // still ends the normal ways otherwise - attacker dies/leaves,
            // own health too low, or NoRealEngagementDisengageSeconds if
            // real LOS genuinely never comes back at all.
            if (distance > pursueRange && !IsHuntablePredator(attacker) && !supportsLongRangeCounterFire)
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - '{GetAttackerDisplayName(attacker)}' got too far away ({distance:F0}m).");
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            // "Attempts to kill it at any cost" (Lucas's own explicit
            // 2026-08-14 wording for animal on-sight engagement) - an armed
            // bot that picked a fight with a predator doesn't get the same
            // low-health bail-out a player fight does. Real players still
            // disengage normally below this threshold; only animal targets
            // skip it.
            if (currentNpc.health <= CombatFleeHealthThreshold && !IsHuntablePredator(attacker) && !_disableLowHealthDisengage)
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - too low on health ({currentNpc.health:F0}) to keep fighting.");
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            // See NoRealEngagementDisengageSeconds' own doc comment - a
            // fight with genuinely no line of sight on the attacker for the
            // whole window gives up, regardless of distance. Catches an
            // attacker that's technically alive and in pursueRange but
            // permanently unreachable (noclipped under the map, wedged
            // somewhere with no sightline) - every other disengage check
            // above only reacts to the attacker actually dying/leaving
            // range/the bot's own health, none of which ever fire here.
            if (Time.realtimeSinceStartup - lastRealLosTime >= NoRealEngagementDisengageSeconds && !IsHuntablePredator(attacker))
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - no real shot at '{GetAttackerDisplayName(attacker)}' in {NoRealEngagementDisengageSeconds:F0}s (out of reach?).");
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            // See ReloadRetreatDistance's own doc comment - mid-fight only,
            // and only ever set by OnEntityTakeDamage reacting to REAL
            // incoming damage during a reload's exposure window (2026-08-16,
            // Lucas's own explicit scoping, two rounds: "only in SOME
            // situations... if the bot was winning a fight why would it
            // retreat whilst reloading" and then "it should only retreat IF
            // it gets shot or damaged WHILST reloading. It shouldn't just
            // default to 'I'm reloading, run away.'"). Every disengage
            // check above still runs every tick even while retreating
            // (attacker dying/fleeing/the bot's own health should still end
            // the fight normally), this just skips the aim/fire/follow
            // logic below for the retreat's own duration - the walk itself
            // is a completely separate movement already running in
            // _activeMovement.
            if (_combatRetreatUntilTime.TryGetValue(characterId, out float retreatUntilTime) && Time.realtimeSinceStartup < retreatUntilTime)
            {
                return;
            }

            // See _healingUntilTime's own doc comment - a bot mid-syringe/
            // bandage animation has its active item swapped away from its
            // real weapon, so aim/fire (and re-triggering another heal)
            // both need to stay paused for the animation's own duration,
            // same shape as the retreat gate right above.
            if (_healingUntilTime.TryGetValue(characterId, out float healingUntilTime) && Time.realtimeSinceStartup < healingUntilTime)
            {
                return;
            }

            // REMOVED 2026-08-25 (Lucas's own live report + log trace: bots
            // "falling back... no real cover found" then dying at 2-6m
            // point blank seconds later, "the whole lose health = run away
            // and heal looks quite odd"). This used to unconditionally
            // return here for the WHOLE TacticalActionDurationSeconds (6s)
            // window any time a tactical action was active - not just while
            // actually mid-heal-animation - meaning a bot retreating
            // WITHOUT real cover (the common case away from monuments) went
            // completely dark: no return fire, no LOS/lastKnownPosition
            // tracking, for up to 6 real seconds, while only gaining
            // TacticalFallbackRetreatDistance (10m) with no LOS break at
            // all. A real player closing distance during that window
            // trivially ran it down. The ONLY genuine reason this gate ever
            // existed - avoiding a same-tick "lost its ranged weapon"
            // misread right as a heal chain swaps the active item away
            // (TryStartTacticalRepositioning's own doc comment above) - is
            // already fully covered by _healingUntilTime's OWN gate right
            // above this one, which only ever fires while a medical item is
            // actually equipped; Flank and a Retreat that never triggers
            // healing (hasCover=true and not hurt enough) never touch the
            // held weapon at all, so they never needed this gate either.
            // Removing it lets aim/fire run normally throughout a
            // retreat/flank exactly like it already does during an ordinary
            // StartFollowing approach - real cover naturally stops fire on
            // its own once it genuinely breaks LOS (hasLos below goes
            // false), and the existing SprintFireSettleSeconds/
            // modelState.sprinting gate further down already prevents
            // firing while actively sprinting away, matching the real
            // "can't run and gun, but CAN dodge/heal and still shoot once
            // you're not sprinting" rule this project already established.
            bool rawLos = HasCombatLineOfSight(currentNpc, attacker);

            if (rawLos)
            {
                lastKnownPosition = attacker.transform.position;
                lastRealLosTime = Time.realtimeSinceStartup;

                // Real sight regained - stop any hunt-search sweep still
                // chaining itself (PushTowardLastKnownPosition's own
                // ContinueHuntSearch) before it fights StartFollowing for
                // movement control. Cheap no-op dictionary removal on every
                // tick real LOS already holds, not just the instant it's
                // regained.
                StopHuntSearch(characterId);

                if (losRegainedAt < 0f)
                {
                    losRegainedAt = Time.realtimeSinceStartup;

                    // Rolled once per regain, using the distance AT THE
                    // MOMENT LOS came back (not re-rolled every tick while
                    // waiting out the delay) - see LosRegainReactionBands'
                    // own doc comment for the reasoning behind each band.
                    currentLosRegainDelay = RollLosRegainDelay(distance);
                }
            }
            else
            {
                losRegainedAt = -1f;
            }

            // hasLos below is the CONFIRMED, reaction-delayed value actually
            // acted on - losing LOS is instant (losRegainedAt resets the
            // moment rawLos drops), but REGAINING it holds off for
            // currentLosRegainDelay after rawLos first came back, per
            // Lucas's own live-test feedback 2026-08-13: the previous
            // instant-react version worked, but read as inhumanly fast - a
            // real player needs a beat to actually process "wait, I can see
            // them again" before reacting, not an instant snap the exact
            // frame a sightline opens up.
            bool hasLos = rawLos && Time.realtimeSinceStartup - losRegainedAt >= currentLosRegainDelay;

            // Bail out of a blind no-LOS push the instant it's standing in
            // known-bad monument geometry (2026-08-22, live death -
            // 'HazyRaider6' at Nuclear Missile Silo's first tunnel
            // stretch). Without LOS, the branch below just calls
            // StartWalking(survivor, lastKnownPosition) to close the gap -
            // fine normally, but if the survivor is already standing inside
            // a CONFIRMED monument avoid zone (see IsInMonumentAvoidZone),
            // that push is walking straight back into the same terrain that
            // just blocked it, with TryGetNextStep's sidestep-rescue unable
            // to find any clear candidate either (the whole pocket is bad,
            // not one obstacle to walk around) - the bot sat there taking
            // unreturned fire for several real seconds before dying, faster
            // than NoRealEngagementDisengageSeconds' own 15s window could
            // ever catch it. Disengaging here hands control back to
            // whatever task was running before combat (the ghost route's
            // own resume, for a monument mid-route) - which already knows
            // how to walk this exact stretch safely via its spliced trace,
            // instead of the raw "walk straight at the attacker" line this
            // system falls back to.
            if (!hasLos && IsInMonumentAvoidZone(currentNpc.transform.position))
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - can't safely push toward '{GetAttackerDisplayName(attacker)}' without LOS through known-bad terrain here.");
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            // Tactical decision system, Piece 3 (2026-08-24) - replaces the
            // old unconditional "heal below 70 health, regardless of
            // exposure" trigger entirely (see
            // LivingRust.CombatTacticalDecisions.cs's own top-of-file doc
            // comment for the full design history/reasoning). Runs every
            // tick regardless of hasLos (reacting to danger while NOT
            // visible to the attacker, e.g. mid-approach, is exactly the
            // real-player instinct this is meant to capture), but its own
            // re-evaluation cadence internally means it only actually
            // rolls a fresh decision every couple of real seconds, not
            // every 0.05s tick.
            //
            // Real bug this shape avoids (originally found with the old
            // heal-only trigger, still applies here): an action that
            // starts THIS tick can swap the active item away from the real
            // weapon (a heal chain calls UpdateActiveItem synchronously,
            // before the deferred _healingUntilTime write further down
            // this same tick has even been set), so the "lost its ranged
            // weapon" check later in this SAME tick would otherwise still
            // see the freshly-equipped MedicalTool (not a BaseProjectile)
            // and immediately disengage mid-action. Returning immediately
            // the instant a tactical action actually starts closes that
            // one-tick gap - every SUBSEQUENT tick is already covered by
            // _healingUntilTime's own gate once it's actually set (see that
            // gate's own doc comment for why the broader
            // _tacticalActionUntilTime blanket-suppression this used to
            // also rely on was removed 2026-08-25).
            if (TryStartTacticalRepositioning(survivor, currentNpc, attacker))
            {
                return;
            }

            // Reacts to a CONFIRMED LOS change on the tick it happens
            // (Lucas's own explicit correction, 2026-08-13) - the periodic
            // advance block further down is fine for "slowly tighten the
            // hold distance during an ongoing exchange," but that same
            // AdvanceIntervalSeconds cadence was ALSO the only thing
            // switching between live pursuit and a blind push toward
            // lastKnownPosition, meaning regaining LOS mid-push (player
            // re-peeks) still left the bot committed to blindly sprinting
            // at the OLD position - unable to even face/fire (both gated on
            // not sprinting) - for up to 4 more real seconds. That's a
            // player getting to freely re-peek and punish a bot that's
            // functionally frozen despite already having a clean shot
            // available.
            bool losChanged = hasLos != wasLosLastTick;

            // Diagnostic-only (2026-08-21) - Lucas's own explicit request,
            // built to catch a live report of a bot killing a Scientist
            // "without looking at it" (visually aiming/facing nowhere near
            // the real target at the moment of the kill). Logs every real
            // hasLos transition with enough state to reconstruct the exact
            // sequence afterward - cross-reference against aim-diag/
            // fire-diag's own timestamps to see whether a real FireAt ever
            // lands in the window between a "lost" transition and the next
            // AimAtPlayer call, which is exactly the gap AimAtPlayer's own
            // doc comment already documents as "never runs at all without
            // [LOS]." Puts, not VerbosePuts - this needs to survive
            // regardless of _verboseLootLogging while under investigation.
            if (losChanged)
            {
                Puts($"los-diag: '{survivor.Character.Alias}' hasLos {(hasLos ? "REGAINED" : "LOST")} vs '{GetAttackerDisplayName(attacker)}' at distance {distance:F1}m (rawLos={rawLos}, currentLosRegainDelay={currentLosRegainDelay:F2}s).");
            }

            // Already in the closest/best-burst band (Bands is ordered
            // ascending, so Bands[0] is always the nearest tier) - Lucas's
            // own explicit correction, 2026-08-13: closing further past
            // this point doesn't make the bot any deadlier (it's already
            // getting the biggest burst its weapon offers), so the ONLY
            // thing further advancing accomplished was more time spent
            // sprint-locked out of firing for zero real benefit. Computed
            // here (before BOTH movement blocks below) rather than only
            // ahead of the post-burst nudge further down - a live report
            // 2026-08-13 caught shotguns (which skip the post-burst nudge
            // entirely and always use the periodic-timer block instead,
            // see weaponUsesBurstPacing's own doc comment) still visibly
            // favouring closing distance over firing even once already
            // well inside their own best (0-10m continuous) tier, because
            // the periodic-timer block below had no equivalent "already
            // close enough" check of its own at all.
            bool alreadyInBestBand = fireProfile.Bands != null
                && fireProfile.Bands.Length > 0
                && distance <= fireProfile.Bands[0].MaxDistance;

            // Tracked here (moved earlier than the sprint-fire-settle check
            // further down that originally owned this) specifically so the
            // periodic advance block right below can use it too. Live
            // report 2026-08-14 (trace evidence): the earlier
            // !currentNpc.modelState.sprinting-only gate was satisfied by a
            // single transient tick right at the moment distance crossed
            // below the old hold distance - real trace data showed target
            // distance shrinking smoothly and continuously from 43m to ~5m
            // with almost no genuine pause, meaning that one-tick blip was
            // immediately followed by another shrink-and-resume-sprint
            // cycle before the bot had any real time to attempt firing at
            // all. Requiring a genuine SprintFireSettleSeconds of
            // continuous non-sprinting (the same real "can't fire straight
            // off a dead sprint" window firing itself already respects)
            // before the advance system is allowed to shrink further gives
            // every distance tier an actual chance to fire first.
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
                // Only shrinks the hold distance once the bot has actually
                // CAUGHT UP to and settled at the current one (not
                // sprinting) - live report 2026-08-13, shotguns
                // specifically: ShotgunAdvanceIntervalSeconds (1.5s) is
                // fast enough that the existing 10-20m step is bigger than
                // a bot can even physically sprint-cover in that time
                // (~8m at RunSpeed) - the old purely time-based condition
                // kept re-shrinking the target out from under it every
                // tick, so it never actually stopped to fire at all until
                // the shrinking finally bottomed out at
                // AdvanceMinimumDistance (point-blank) - reading exactly
                // like "storms straight to melee range, ignores its own
                // engagement range entirely." Requiring !sprinting first
                // means every distance tier gets a genuine chance to
                // settle and fire before the bot is allowed to push closer
                // again, for every weapon on this path, not just shotguns.
                //
                // Periodically tighten the held distance and re-issue
                // movement toward it - see AdvanceIntervalSeconds' own doc
                // comment for why this exists at all. Only advances if
                // there's actually room left to (currentHoldDistance
                // already at/near AdvanceMinimumDistance means it's already
                // about as close as it's going to get) - re-issuing
                // movement at an unchanged distance would just be pointless
                // churn. Runs regardless of hasLos when the weapon doesn't
                // burst-pace itself (a target behind cover hasn't given up,
                // and a real player wouldn't either) - but while LOS holds
                // AND the weapon fires in bursts, this is deliberately
                // skipped in favour of the smaller, burst-reactive nudge
                // below (see PostBurstAdvanceStepMinMeters' own doc comment)
                // instead of interrupting an active exchange on a fixed
                // clock.
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

                // Not due for another 4s of its own accord right after this -
                // avoids a redundant periodic nudge stacking immediately on
                // top of the one just applied.
                nextAdvanceTime = Time.realtimeSinceStartup + advanceIntervalSeconds;
            }

            pendingPostBurstAdvance = false;

            if (!hasLos)
            {
                // Stays engaged (doesn't disengage over a momentary LOS
                // break) but doesn't fire blind either - just waits for a
                // clear shot while the push above closes the gap toward
                // lastKnownPosition. Nothing to aim in on without a target
                // in sight.
                SetAimingIn(currentNpc, false);
                return;
            }

            bool inEngagementRange = distance <= engagementRange * GetBiomeVisibilityFactor(currentNpc.transform.position, attacker.transform.position);

            // Runs every combat tick whenever LOS is confirmed (we're
            // already past the !hasLos early-return above by this point),
            // regardless of sprinting - previously gated on
            // `!currentNpc.modelState.sprinting`, which caused a real,
            // reported bug (live trace evidence, 2026-08-14): StartFollowing
            // runs its OWN independent per-tick movement loop
            // (WalkTickInterval, same 0.05s cadence as CombatTickInterval)
            // that unconditionally calls FaceDirection(npc, velocity)
            // (travel-direction facing) the entire time it's actively
            // steering the bot toward its hold distance - with AimAtPlayer
            // shut off for that whole stretch, facing went stale (pinned to
            // travel direction) for however long the approach took, then
            // did one single instant Quaternion.LookRotation snap onto the
            // now-far-away true bearing the moment sprinting flipped false -
            // read exactly as the reported "1 or 2 times large jitter turn,"
            // only visible with LOS (AimAtPlayer never runs at all without
            // it) and specifically at the moving-to-static transition
            // (matches Lucas's own observation). A real player's mouse-aim
            // is independent of WASD movement - they can track a target
            // while running, they just can't fire accurately doing it (see
            // SprintFireSettleSeconds below, which already gates FIRING
            // separately and is untouched by this). Letting AimAtPlayer run
            // continuously means it re-corrects toward the target's true
            // bearing every single tick, so the two writers can still
            // occasionally trade a tick, but only ever by however much the
            // true bearing moved in ~0.05s - never by a whole accumulated
            // sprint's worth of drift. The old inEngagementRange-freeze
            // concern this sprinting gate was originally added for (see the
            // superseded comment this replaced) doesn't apply here - this
            // condition was never gated on range, so an unreachable/kiting
            // target still gets tracked every tick same as before.
            AimAtPlayer(currentNpc, attacker);

            // Aim-in pose stays sprint-gated (real Rust constraint: sprinting
            // locks weapon use, so the ADS pose doesn't make sense while
            // sprinting) - this is a cosmetic bool toggle, not an
            // interpolated rotation, so it doesn't carry the same staleness/
            // snap risk AimAtPlayer had.
            SetAimingIn(currentNpc, !currentNpc.modelState.sprinting);

            if (currentNpc.GetHeldEntity() is not BaseProjectile weapon)
            {
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - lost its ranged weapon.");
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            if (weapon.primaryMagazine.contents <= 0)
            {
                // Real reload - finds and consumes matching ammo from the
                // survivor's own inventory automatically (confirmed via
                // decompile: ServerTryReload internally calls
                // FindItemsByItemID/FindAmmo against the given
                // IAmmoContainer, no manual ammo lookup needed here).
                weapon.ServerTryReload(currentNpc.inventory);

                if (weapon.primaryMagazine.contents > 0)
                {
                    // Real reload succeeded, but this weapon was completely
                    // dry a moment ago - marks a real-time EXPOSURE window
                    // (the length of the real reload animation,
                    // weapon.reloadTime) rather than retreating immediately.
                    // 2026-08-16, Lucas's own explicit correction: "it
                    // should only retreat IF it gets shot or damaged WHILST
                    // reloading. It shouldn't just default to 'I'm
                    // reloading, run away.'" OnEntityTakeDamage checks this
                    // window against real incoming damage and is what
                    // actually triggers the retreat (TryTriggerReloadRetreat)
                    // - reloading unopposed (attacker already dead/fled/
                    // missing entirely) never retreats at all, exactly the
                    // "a fight already being won doesn't need this" scoping
                    // from earlier.
                    _reloadExposureWindowUntil[characterId] = Time.realtimeSinceStartup + weapon.reloadTime;

                    // "It still stands still whilst reloading... at least
                    // use the strafing whilst reloading" (Lucas's own
                    // explicit follow-up, 2026-08-25) - reloading doesn't
                    // wait for the sprint-settle gate the normal firing-hold
                    // jitter call does (real Rust lets you reload while
                    // moving, unlike firing, so gating this behind "not
                    // sprinting" would delay a real reload attempt for no
                    // reason) - a bit of jitter layered on top of active
                    // sprint movement is harmless background motion, not a
                    // conflict.
                    ApplyCombatJitter(currentNpc, attacker);
                    return;
                }

                // Still empty after a real reload attempt - genuinely no
                // matching ammo left for THIS weapon system anywhere in
                // inventory. Swaps to the next best scored weapon that
                // still has usable ammo instead of just standing there
                // retrying a reload forever (2026-08-16, Lucas's own
                // explicit request: "if it runs out of ammunition for that
                // weapon system it swaps to the next best available scored
                // weapon"). Every derived-from-weapon piece of this fight's
                // own state gets recomputed for the new weapon, not just the
                // reference itself - a Thompson fights nothing like an AK.
                if (TryEquipBestArmedWeapon(survivor, out BaseProjectile nextWeapon))
                {
                    weaponShortname = nextWeapon.GetOwnerItem()?.info.shortname ?? string.Empty;
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

                // Nothing owned has any ammo left at all - no melee-vs-
                // players fallback yet (same deferred scope StartCombat's
                // own unarmed branch already documents), so this is a real
                // give-up, not a stall. Still equips the best-effort DISPLAY
                // weapon rather than leaving whatever empty gun it's
                // holding, same reasoning as StartCombat's own initial
                // unarmed branch.
                VerbosePuts($"'{survivor.Character.Alias}' disengaging - no ammo left for anything it owns.");
                EquipBestWeaponForDisplay(survivor);
                EndCombat(characterId, combatTimer, survivor, previousTask);
                return;
            }

            if (weapon.HasAttackCooldown())
            {
                // Real per-weapon fire-rate gate - same AttackEntity.
                // repeatDelay mechanism the melee loop already relies on.
                return;
            }

            if (Time.realtimeSinceStartup < nextShotAllowedTime)
            {
                // See WeaponFireRateMultiplier's own doc comment - OUR
                // OWN extra pacing on top of the real cooldown just
                // checked above, for weapons whose real repeatDelay alone
                // still reads as too fast.
                return;
            }

            if (!inEngagementRange && !supportsLongRangeCounterFire)
            {
                // Live report 2026-08-11: bots were firing (and landing
                // hits, since FireAt's originOverride ignores real range
                // falloff) on an attacker well beyond the weapon's own
                // intended engagement range - a shotgun should never be
                // meaningfully effective at 25m+. StartFollowing is already
                // closing the gap in the background; just wait for it
                // rather than sniping with a shotgun from outside its
                // realistic range.
                return;
            }

            // supportsLongRangeCounterFire lets a qualifying weapon fire
            // straight through the engagement-range gate above - see its
            // own doc comment. GetBurstBandForDistance (used further down
            // for the actual shot) already falls back to the FURTHEST
            // defined band once distance exceeds every band's own
            // MaxDistance, so "fires at the furthest distance tier's own
            // rate" (Lucas's own explicit wording) needs no extra logic
            // here at all - it's already what that fallback does.

            // lastSprintingTime itself is now tracked earlier in this tick
            // (see settledLongEnoughToAdvance's own doc comment above) so
            // the advance system can use it too - this just reuses that
            // already-current value.
            if (Time.realtimeSinceStartup - lastSprintingTime < SprintFireSettleSeconds)
            {
                // Real Rust constraint (Lucas's own explicit correction,
                // 2026-08-11): a player physically cannot fire accurately
                // while sprinting - sprinting locks weapon use. Skip this
                // tick rather than firing while still closing distance at a
                // run; StartFollowing settles into a walk/stop once close
                // enough (see its own CatchUpDistance/ResumeWalkDistance
                // hysteresis), at which point this naturally clears - plus a
                // short settle window afterward (SprintFireSettleSeconds)
                // covering the client's own sprint-to-stand transition
                // animation, not just the server-side flag.
                return;
            }

            // Applied here specifically because everything above this
            // point already confirms the survivor has genuinely settled
            // (not sprinting, not mid-heal/retreat/telegraph) - exactly
            // Lucas's own framing, "IF they are intending to stand still."
            // See ApplyCombatJitter's own doc comment for the full
            // reasoning/history.
            ApplyCombatJitter(currentNpc, attacker);

            // First-shot turn telegraph (2026-08-21) - see
            // FirstShotTelegraphDegreesPerSecond's own doc comment for the
            // live bug this fixes. Everything above this point (LOS, ammo,
            // range, sprint-settle) is already satisfied, so this is
            // genuinely the moment the fight's first real shot would fire -
            // hold it back proportional to how far the survivor actually
            // had to turn, nudging a tiny real step toward the attacker
            // each tick in the meantime so the client gets genuine velocity
            // to key its own rotation interpolation off, not just a static
            // snap. Only ever runs once per StartCombat call (a rapid re-
            // engagement gets its own fresh telegraph, same as a genuinely
            // new fight would) - every shot after the first in this fight
            // only ever needs a small corrective turn, which AimAtPlayer's
            // own per-tick tracking already keeps visually current without
            // needing this.
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

            // M16A2/P17 dedicated path, gated by EXACT real shortname
            // (2026-08-13, tightened from the generic isBurstWeapon/
            // canChangeFireModes flag checks used previously) - those
            // flags describe a real internal-burst CAPABILITY, not "is
            // this specifically one of our two special-cased weapons."
            // Live report 2026-08-13: a bot was firing continuously with
            // no burst pause at all - if any OTHER weapon happens to share
            // either flag for an unrelated reason, the old flag-based gate
            // would have swept it into this branch too and skipped the
            // distance-banded system entirely, matching exactly that
            // symptom. Checking the real shortname directly removes any
            // ambiguity about what these flags actually mean on a weapon
            // this table was never designed around.
            bool isP17 = weaponShortname == "pistol.prototype17";
            bool isM16A2 = weaponShortname == "m16a2";

            // p17InBurstRange (OUR OWN distance check) drives which code
            // path P17 actually takes below - NOT weapon.UsingBurstMode().
            // Live report 2026-08-13: P17 was firing at "almost MP5 full-
            // auto rate" and "doesn't seem to change firing modes at close
            // range" - the real internal toggle below (Flags.Reserved6,
            // the same flag a real client's fire-mode-switch RPC flips)
            // wasn't reliably flipping UsingBurstMode()'s own return value,
            // so the old `&& weapon.UsingBurstMode()` gate below was
            // silently never true even under 20m, permanently routing P17
            // into the single-fire path with no pacing at all. Still
            // attempted below as a best-effort cosmetic sync (so the real
            // client-side fire sound/animation matches where possible), but
            // no longer trusted to actually decide which of our OWN paths
            // runs - that's now purely our own already-known-good distance
            // check, exactly like every other weapon in this system.
            bool p17InBurstRange = isP17 && distance <= P17BurstSwitchDistance;

            if (isP17)
            {
                bool wantBurst = p17InBurstRange;

                if (weapon.UsingBurstMode() != wantBurst)
                {
                    // SetFlag renamed to SetFlagLocal in a Rust/Carbon update
                    // (2026-09-07, confirmed via ilspycmd decompile of the
                    // current Assembly-CSharp.dll - SetFlag no longer exists
                    // on BaseEntity at all, only SetFlagLocal(Flags, bool,
                    // recursive) remains, same signature otherwise). Still
                    // calls InvalidateNetworkCache() internally exactly like
                    // the old SetFlag did, so this is a straight rename for
                    // this use case, not a behavior change.
                    weapon.SetFlagLocal(BaseEntity.Flags.Reserved6, !weapon.HasFlag(BaseEntity.Flags.Reserved6));
                }
            }

            if (isM16A2 || p17InBurstRange)
            {
                // Real fixed-3-round mechanic (one trigger pull fires all 3
                // shots in rapid succession, not 3 separate pulls) - the
                // M16A2 always lands here (fixed), the P17 whenever it's
                // within P17BurstSwitchDistance. Enforces the same shape
                // Rust's own client would: allow up to GetBurstModeCount()
                // (real value, 3) shots, then force TimeBetweenBursts()
                // (real value, repeatDelay * 2f) of silence before the next
                // volley starts.
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
                // P17 beyond P17BurstSwitchDistance - real semi-auto, fired
                // in genuine grouped bursts now (Lucas's own explicit
                // follow-up, 2026-08-13: "roll it similar to how the M92
                // pistol is shooting" instead of a flat single-shot-every-
                // pause loop - the previous version fired exactly one round
                // per pause cycle, reading as an evenly-spaced
                // "1.....2.....3....." rather than a real trigger-pull
                // rhythm). P17 already maps to PistolProfile (the same
                // Bands table the M92 and every other pistol use) via
                // WeaponFireProfiles, so this reuses that exact same table
                // through GetBurstBandForDistance - a real "few rounds in
                // quick succession, then pause" group, not a bespoke
                // number invented just for P17.
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
                    // Still P17SingleFirePaceSeconds (not the shared
                    // BurstPauseFloorSeconds) - keeps the "50% faster"
                    // pacing intent from the previous pass even though a
                    // "burst" can now be more than one round.
                    nextBurstAllowedTime = Time.realtimeSinceStartup + Mathf.Max(weapon.repeatDelay * 2f, P17SingleFirePaceSeconds);
                    pendingPostBurstAdvance = true;
                }

                return;
            }

            // Distance-banded burst system (2026-08-11, Lucas's own spec) -
            // every other ranged weapon. GetBurstBandForDistance re-reads
            // the CURRENT live distance each time a new volley starts (not
            // once for the whole fight), so a bot that closes distance
            // mid-fight gets a bigger burst on its very next volley.
            (int minBurst, int maxBurst) = GetBurstBandForDistance(fireProfile, distance);

            if (minBurst < 0)
            {
                // Continuous fire, no volley/pause bookkeeping at all - the
                // whole profile has no Bands (pump shotgun, SPAS-12, double
                // barrel, snipers), or the current band is explicitly
                // continuous (M4 shotgun within 10m). Just keep shooting
                // every cooldown-cleared tick until the target - or the bot
                // itself - is dead.
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

                // Starting a fresh volley. Live report 2026-08-11: bots
                // weren't visibly sticking to "fire X, pause, fire X" -
                // root cause was using the bare `repeatDelay * 2f` formula
                // here (the same one the M16A2's OWN real burst pause
                // above uses, correctly, for that one specific weapon's
                // real fire rate). For a fast automatic (MP5, AK, M249...)
                // repeatDelay is tiny, so that pause was real but far too
                // short to read as a deliberate pause rather than
                // continuous fire. Flooring it at BurstPauseFloorSeconds
                // keeps the same "slower weapon gets a proportionally
                // longer pause" shape while guaranteeing every weapon gets
                // an actually-perceptible gap between volleys. Distance-
                // scaling this pause is still explicitly parked for a
                // later pass (Lucas, 2026-08-11).
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
                    // A real possible roll for the Custom SMG's longer-range
                    // bands (0-5/0-2) - "unreliable at range" for a real
                    // low-tier weapon, not a bug. Already scheduled
                    // nextBurstAllowedTime above so the next attempt waits
                    // properly instead of re-rolling every single tick.
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

        // Fresh engagement against a genuinely NEW attacker (not just
        // continuing to fight the same one) - reset the damage-dealt tally
        // (LivingRust.CombatDecisionTracking.cs) so it reflects THIS fight,
        // not carried over from whoever was fought last.
        if (!_activeCombatTarget.TryGetValue(characterId, out BaseCombatEntity previousAttacker) || previousAttacker != attacker)
        {
            ResetDamageDealtToCurrentAttacker(characterId);
        }

        _activeCombat[characterId] = combatTimer;
        _activeCombatTarget[characterId] = attacker;
    }

    // Real "catch my breath" beat between a fight actually ending and
    // resuming whatever the survivor was doing before it - Lucas's own
    // explicit spec, 2026-08-13: a bot that just won (or otherwise
    // disengaged from) a fight shouldn't robotically snap straight back to
    // looting the instant the attacker's gone - a real player reloads,
    // takes a breath, THEN moves on. Not gated on which disengage reason
    // fired (won the fight, target fled too far, own health too low) -
    // "if it needs to" reload applies identically regardless of why the
    // fight ended, and the settle beat reads as natural either way.
    // 2026-08-24, Lucas's own explicit request: bump every "how long before
    // I should loot" delay by 50% (2f -> 3f) - a large-scale fight means
    // several bots disengaging near each other at once, and this project's
    // whole reactive-combat/on-sight system already means a bot that starts
    // looting too early is one more real re-engagement/interrupt away from
    // getting caught mid-item-transfer ("pants down") by whoever's still
    // fighting nearby.
    private const float PostCombatSettleSeconds = 3f;

    /// <summary>
    /// How close a still-alive, still-visible attacker needs to be to
    /// postpone the post-combat reload (2026-08-22, live report + trace:
    /// 'BrokenRock31' disengaged at low health, reloaded immediately per
    /// the old unconditional logic, and took another hit from the exact
    /// same Scientist one real second later, still well within LOS/range -
    /// reads as "inhumane," since a real player breaks contact before
    /// fumbling with a mag change, not mid-firefight with the same threat
    /// still bearing down on them). Matches ReloadRetreatDistance's own
    /// general engagement-adjacent scale - this is "is the thing that just
    /// shot me still right here," not "could a threat exist somewhere on
    /// the map."
    /// </summary>
    private const float PostCombatReloadSafetyRadius = 30f;

    private const float PostCombatReloadSafetyRecheckInterval = 1f;

    /// <summary>
    /// Caps how long the reload can be deferred waiting for safety - after
    /// this many rechecks (~8s), reload anyway rather than leaving the bot
    /// permanently under-loaded because a threat never fully clears (e.g.
    /// a scientist camping just at the edge of LOS/range).
    /// </summary>
    private const int PostCombatReloadSafetyMaxRechecks = 8;

    /// <summary>
    /// Flat floor before the FIRST reload attempt is even checked
    /// (2026-08-22, Lucas's own explicit follow-up idea) - even when
    /// nothing's visibly threatening right at the moment combat ends,
    /// reloading the instant a fight is over is still risky: if a fresh
    /// threat catches the bot within the next couple seconds, it'd rather
    /// still have whatever's left in its current mag chambered and ready
    /// than be mid-reload-animation with a stripped mag. Stacks with (does
    /// not replace) PostCombatReloadSafetyRadius's own per-attempt LOS/
    /// distance check below - this is a "take a breath before even
    /// considering it" beat that applies every time, not just when a
    /// threat happens to still be visible.
    /// </summary>
    // Same 50% bump as PostCombatSettleSeconds (2026-08-24) - 3f -> 4.5f.
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
    /// Polls once a second (see PostCombatReloadSafetyRecheckInterval)
    /// until the just-fought attacker is no longer an immediate threat (or
    /// the recheck cap is hit), then performs the real reload - the same
    /// ServerTryReload this project always used, just no longer forced to
    /// happen in the open the instant a fight ends regardless of what's
    /// still shooting. Deliberately doesn't block/delay the survivor's own
    /// task-resume timer (see EndCombat) - a bot fleeing at low health
    /// should still get moving immediately with whatever's in its
    /// magazine; this only decides WHEN it's safe enough to top that
    /// magazine back up.
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
            Puts($"reload-diag: '{survivor.Character.Alias}' deferred post-combat reload on '{weapon.GetOwnerItem()?.info.shortname}' (waited for safety, attempt {attempt}) - contents {contentsBefore} -> {weapon.primaryMagazine.contents} (capacity {weapon.primaryMagazine.capacity}).");
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

        // Real reload duration, only set when a reload actually fires below
        // - used to stretch the post-combat settle beat so the bot isn't
        // seen walking off mid-reload-animation. Lucas's own explicit
        // request, 2026-08-14: "add a reload animation in between combat
        // disengage." The animation itself was already firing correctly
        // (ServerTryReload internally calls SignalBroadcast(Signal.Reload) -
        // confirmed via decompile, the exact real mechanism every other
        // reload in this project already relies on, no separate trigger
        // needed) - what was actually missing is that nothing paced the
        // BOT'S OWN next action to match: PostCombatSettleSeconds was a
        // flat 2s completely disconnected from the weapon's real
        // reloadTime, so a slower-reloading weapon (bolt rifles etc,
        // several seconds) would still have the bot walking away well
        // before its own reload animation actually finished.
        float reloadDuration = 0f;

        if (npc != null && !npc.IsDestroyed && npc.GetHeldEntity() is BaseProjectile weapon && weapon.primaryMagazine.contents < weapon.primaryMagazine.capacity)
        {
            // "if it needs to" - a bot that disengaged with a topped-up mag
            // (killed its attacker in far fewer rounds than it was
            // carrying) has nothing real to reload, so this only fires
            // when there's an actual real gap to fill, same as the mid-
            // fight reload check already does.
            //
            // Never reloads synchronously in EndCombat anymore (2026-08-22) -
            // always waits out PostCombatReloadMinimumDelaySeconds first,
            // THEN checks IsUnsafeToReloadRightNow (see its own doc comment -
            // a live trace caught a bot reloading in the open with the very
            // attacker it just "disengaged" from still alive, close, and
            // shooting it a second later) before actually committing to the
            // reload. Trade-off accepted: reloadDuration stays 0f now (no
            // longer stretches the settle-beat below to match the reload
            // animation's real length), since the reload itself is always
            // async from here on - a minor cosmetic loss (the bot may
            // occasionally walk off while a reload plays a beat later) in
            // exchange for never reloading blind in the open again.
            VerbosePuts($"'{survivor.Character.Alias}' will reload once it's had a moment to settle.");
            timer.Once(PostCombatReloadMinimumDelaySeconds, () => ReloadWhenSafe(survivor, lastAttacker, 0));
        }

        // "Re-engage the player as quick as possible" (Lucas's own explicit
        // 2026-08-14 wording) - a player fight an animal interrupted
        // resumes IMMEDIATELY here, deliberately bypassing both
        // PostCombatSettleSeconds (that breather is for "won a fight, catch
        // a breath before going back to LOOTING," not for leaving an
        // already-active threat waiting) and the loot-task resume path
        // below entirely - StartCombat re-establishes movement/facing/fire
        // state against the player fresh, exactly as if the animal had
        // never interrupted.
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

        // No longer requires previousTask == LootForResources (2026-08-15) -
        // that guard meant a survivor whose task had already gone to
        // TaskType.None (the "genuinely done, nothing left nearby" outcome
        // several loot-escalation give-up paths set) got silently stranded
        // forever if it then survived a fight: EndCombat cleared
        // _activeCombat correctly, but nothing else was left driving it -
        // no movement, no re-task, no further log output at all. Live
        // report matched this exactly: a fully healthy, non-combat,
        // non-fleeing survivor frozen in place with total log silence for
        // several minutes. TaskType only has two values (None/
        // LootForResources - see its own doc comment) and /lr.stop never
        // sets CurrentTask at all (only cancels movement), so there's no
        // legitimate "stay idle forever" case this override could actually
        // break - same "always resume something productive" philosophy
        // RespawnSurvivor and EndFlee's identical fix already apply.
        if (survivor.Player == null
            || survivor.Player.IsDestroyed
            || survivor.Character.State == CharacterState.Dead)
        {
            return;
        }

        float settleDelay = Mathf.Max(PostCombatSettleSeconds, reloadDuration);

        timer.Once(settleDelay, () =>
        {
            // Re-checked after the pause, not just at the moment combat
            // ended - a lot can happen in PostCombatSettleSeconds (a fresh
            // attacker, a death, a plugin reload dropping the character
            // entirely). _activeCombat specifically guards against
            // stomping a NEW fight that started during this exact pause -
            // without it, this delayed callback would yank a survivor back
            // into looting mid-way through defending itself against
            // whoever just shot it again.
            if (survivor.Player == null
                || survivor.Player.IsDestroyed
                || survivor.Character.State == CharacterState.Dead
                || _activeCombat.ContainsKey(characterId))
            {
                return;
            }

            // See PostCombatResumeCooldownSeconds' own doc comment
            // (LivingRust.Recycling.cs) - records the moment THIS fight
            // ended so ResumeOrStartLootTask can hold off attempting any
            // real walk/recovery for a short quiet window, rather than
            // immediately retrying a walk that might walk it straight back
            // into the same attacker.
            _lastCombatEndTime[characterId] = Time.realtimeSinceStartup;

            // See ResumeOrStartLootTask's own doc comment - sends the
            // survivor back to finish a recycler it already fed/started
            // before this fight interrupted it, instead of always starting
            // a completely fresh task and abandoning that loot.
            ResumeOrStartLootTask(survivor);
        });
    }

    /// <summary>
    /// Real root cause of the visible-tracking bug, corrected 2026-08-11
    /// (an earlier doc comment here blamed ServerFinalizePlayers stomping
    /// the value back - decompile confirmed that's wrong: a disconnected
    /// bot never sends a real PlayerTick RPC, so it's structurally excluded
    /// from that whole reconciliation path entirely, meaning nothing was
    /// ever stomping this). The actual cause: BasePlayer.GetNetworkRotation()
    /// (what BaseEntity.Save's network snapshot actually reports to
    /// observers) reads `viewAngles` specifically, not `transform.rotation` -
    /// and this codebase's own movement code only ever gets that value
    /// broadcast to other clients "for free" because its own per-step
    /// position sync already calls SendNetworkUpdate/Immediate for the
    /// position change, and viewAngles piggybacks along in the same
    /// snapshot. This combat aim update never called anything to push a
    /// network snapshot out at all - the server-side value was correct the
    /// whole time (matches Lucas's own report: "the server might be
    /// thinking it is correct... this is purely client side"), it just
    /// never got sent to any observer's screen. SendNetworkUpdate() below
    /// is the actual fix.
    /// </summary>
    /// <summary>
    /// Toggles the shared ModelState.aiming flag (confirmed via decompile
    /// as a real, already-networked field - NPCPlayer's own thrown-weapon
    /// code uses the exact same `modelState.aiming = true/false` pattern
    /// around a throw) and pushes it out via SendModelState(true) so other
    /// clients actually see the change - same broadcast mechanism this
    /// codebase already relies on for sprinting/ducked/onLadder. Generic
    /// across every weapon (it's a player-level pose flag, not a per-weapon
    /// one), which is exactly why this is the right lever for "aim in when
    /// shooting, for all weapons" rather than anything weapon-specific -
    /// each weapon's own existing ironsight/ADS animation just plays off
    /// this same flag the way it would for a real connected player holding
    /// right-click. No-ops if the flag already matches, so this can be
    /// called every combat tick without spamming redundant RPCs.
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

        // Diagnostic-only (2026-08-21) - see the los-diag line's own doc
        // comment (the per-tick combat loop, above) for what this is
        // built to catch. Every real call here is a tick where the
        // visual facing/aim WAS pushed out to observers - if a live
        // "shot without looking" report's timestamp doesn't have a
        // matching aim-diag line within the last real tick or two before
        // it, that's the confirmation the visual genuinely never updated
        // for that shot.
        // Downgraded to VerbosePuts (2026-09-19, live rubber-banding
        // report) - fires every aim tick for every survivor currently in
        // combat, a real unconditional per-frame cost with no way to turn
        // it off; the 2026-08-13 investigation it was written for is long
        // since resolved.
        VerbosePuts($"aim-diag: '{npc.displayName}' AimAtPlayer -> facing yaw {aimRotation.eulerAngles.y:F1} deg toward '{GetAttackerDisplayName(target)}'.");

        // Live report 2026-08-13 (video evidence): body/head facing was
        // already correct (this method's own prior fix), but the visible
        // WEAPON hold/aim pose kept pointing a completely different
        // direction than the body during/after firing, only lining back up
        // once the fire/reload animation finished. Confirmed via decompile
        // (BasePlayer) - a real client's weapon-aim IK is driven by
        // `tickViewAngles`, populated ONLY from a real client's own
        // PlayerTick RPC (`tickViewAngles = tick.inputState.aimAngles`) -
        // a completely separate field from the plain `viewAngles` this
        // method already sets above. A real player's client keeps sending
        // fresh aimAngles continuously all through their own fire/reload
        // animation, so their weapon IK target is already correct the
        // instant the animation ends - our bot never sends that RPC at
        // all, so tickViewAngles was never being touched by anything here,
        // leaving whatever stale/default value it last held as the
        // weapon's real IK target regardless of how often viewAngles
        // itself got updated. Setting it directly alongside viewAngles is
        // the same fix applied to the field real gameplay actually uses
        // for this - unproven until live-tested, but grounded in the
        // decompiled source, not guessed.
        npc.tickViewAngles = aimRotation.eulerAngles;

        // The actual fix - GetNetworkRotation() (what every observer's
        // client actually receives) reads viewAngles, but nothing before
        // this line ever pushes a network snapshot out to broadcast that
        // updated value. Immediate, not the batched/delayed variant - a
        // fast-moving fight shouldn't wait for some unrelated future
        // network tick to carry this along incidentally.
        npc.SendNetworkUpdateImmediate();
    }

    /// <summary>
    /// The real fix for aim accuracy (2026-08-11). Confirmed via decompile:
    /// BaseProjectile.ServerUse's zero-arg call always fires along
    /// ownerPlayer.eyes.BodyForward() whenever HeldEntity.useOwnerForward
    /// is true (the default) - a field derived from the exact eyes/
    /// viewAngles state AimAtPlayer's own doc comment just established
    /// never reliably updates for a disconnected bot, regardless of an
    /// originOverride being supplied. Rust's own NpcShootingComponent
    /// (Rust.Ai.Gen2) never relies on eyes/transform at all - it builds a
    /// Matrix4x4.TRS(origin, LookRotation(target - origin), Vector3.one)
    /// and passes it as originOverride, the same approach used here.
    /// useOwnerForward is force-disabled first so that override can't be
    /// silently discarded the way the zero-arg call's default behaviour
    /// discards it. useProtectionForNPCs:false + useBulletThickness:true
    /// (matching the decompiled recommendation) keeps this real PvP-shaped
    /// damage, not NPC-scaled.
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

        // Distance-scaled spread (2026-08-13) - see WeaponSpreadDegrees'
        // own doc comment for the full reasoning/calibration story. Applied
        // here rather than left to Rust's own real GetAimCone() precisely
        // because that always resolves to the tight, server-authoritative
        // aiming-tier value regardless of the bot's real aim state -
        // reusing AimConeUtil (the same real utility ServerUse's own
        // internals use) keeps the actual random-cone MATH identical to
        // real Rust, just fed a distance-aware angle of our own design
        // instead of the flat per-weapon value ServerUse would otherwise
        // use unconditionally.
        float spreadDegrees = GetWeaponSpreadDegrees(weaponShortname, distance);
        Vector3 fireDirection = spreadDegrees > 0f
            ? AimConeUtil.GetModifiedAimConeDirection(spreadDegrees, perfectDirection)
            : perfectDirection;

        Matrix4x4 originOverride = Matrix4x4.TRS(origin, Quaternion.LookRotation(fireDirection), Vector3.one);

        // Diagnostic-only (2026-08-21) - see los-diag/aim-diag's own doc
        // comments for what this is built to catch. Logs the REAL fire
        // direction (what the shot actually uses, via originOverride -
        // this is what determines the hit, completely independent of the
        // model's own visual rotation) alongside the model's current
        // VISUAL facing (npc.transform.rotation) at the exact moment of
        // the shot - a live report of a bot killing something "without
        // looking at it" should show a real, large gap between these two
        // yaw values on the shot that did it, confirming the visual
        // simply never caught up rather than the hit itself being wrong.
        float realFireYaw = Quaternion.LookRotation(fireDirection).eulerAngles.y;
        float visualFacingYaw = npc.transform.rotation.eulerAngles.y;
        // Same downgrade, same reasoning as aim-diag just above (2026-09-19).
        VerbosePuts($"fire-diag: '{npc.displayName}' fired '{weaponShortname}' at '{GetAttackerDisplayName(target)}' ({distance:F1}m) - real fire yaw {realFireYaw:F1} deg, visual facing yaw {visualFacingYaw:F1} deg (delta {Mathf.DeltaAngle(realFireYaw, visualFacingYaw):F1} deg).");

        weapon.ServerUse(new HeldEntityServerUseParams(1f, 1f, originOverride, useBulletThickness: true, useProtectionForNPCs: false));

        // Live report 2026-08-11: weapon durability never decreased.
        // Confirmed via decompile - ServerUse() never applies condition
        // loss for anyone (real players included); that's only ever
        // triggered from BaseProjectile.CLProject, the server-side handler
        // for a real CLIENT's own fire RPC, which calls this exact public
        // method (UpdateItemCondition - flat base loss + the ammo's own
        // barrelConditionLoss). A bot never sends that RPC, so nothing was
        // ever calling this on its behalf - calling it directly here is
        // the real fix, not a workaround.
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

    // How far each individual flee leg runs (re-issued on every
    // FleeReassessIntervalSeconds tick using the animal's CURRENT position,
    // same "target moves too" reasoning combat's own advance system uses)
    // before being told to keep running. Rescaled from 25f to 15f
    // alongside FleeSafeDistance's own rescale (2026-08-15) - a single leg
    // longer than the new 20f safe distance no longer made sense at this
    // tighter 10m-aggro scale.
    private const float FleeRunDistance = 15f;

    // Scientist flee tuning (2026-09-21) - see StartFleeingFromThreat.
    private const float ScientistFleeMinDistance = 50f;
    private const float ScientistFleeMaxDistance = 100f;
    private const float ScientistFleePoisonRadius = 40f;
    private const float ScientistFleePoisonDurationSeconds = 300f;

    // Once the animal is this far away, the survivor stops running and
    // resumes whatever it was doing - matches PursueRange's role for player
    // combat (a real give-up distance, distinct from the run leg length).
    //
    // Must exceed ScientistOnSightRange (50f) - originally 35f, which sat
    // UNDER that range. Harmless while on-sight detection was armed-only
    // (an unarmed bot could only flee reactively, from actually being hit,
    // so this gap was never exercised), but removing that gate the same
    // session so unarmed bots flee proactively too turned it into a real,
    // constantly-firing loop: a flee ending at exactly 35m left the bot
    // still well inside a stationary scientist's 50m detection radius, so
    // the very next on-sight scan tick (1s later) re-detected the same
    // scientist and re-triggered the whole flee cycle again - confirmed
    // live, some bots logged 50-60+ separate flee triggers against the
    // same scientist in a few minutes, and repeated fleeing that never
    // actually escapes detection range is exactly what stranded 20+ bots
    // in one shoreline pocket (water avoidance also added this session
    // constraining which directions "away" could even go). Was raised to
    // 60f to safely clear the old on-sight ranges (30f animal/50f
    // scientist); now that those are both down to 10f (Lucas's own
    // explicit request, same reasoning as AnimalOnSightRange's own doc
    // comment), 60f would have a 10m aggro send a bot sprinting a
    // mismatched 60m - rescaled to stay proportionate while still safely
    // clearing the new 10f detection radius with real margin.
    private const float FleeSafeDistance = 20f;

    // Lowered from 2f to 0.75f (2026-08-15, live report: bears "hit them
    // again" mid-flee repeatedly, run-stop-hit-run-stop) - RunAwayFromThreat
    // computes a straight-line escape vector AWAY FROM THE THREAT'S
    // POSITION AT THE MOMENT THIS TICK FIRES, then commits to that exact
    // heading for the full interval before recalculating. At 2f, a fast
    // pursuer (a bear closing distance faster than an unarmed bot's
    // RunSpeed) had a full 2 real seconds to cut the corner on a now-stale
    // straight line before the bot ever adjusted course - a real player
    // would juke/redirect far more often than that. Doesn't fix the
    // underlying fact that an unarmed bot flat-out can't outrun something
    // faster than it in a straight line (that's real Rust balance, not a
    // bug), but keeps the escape heading honest far more often instead of
    // committing blind for two whole seconds at a time.
    private const float FleeReassessIntervalSeconds = 0.75f;

    // Safety cap only - a real chase should end via FleeSafeDistance well
    // before this, but a survivor cornered against terrain/water (unable to
    // actually put distance between itself and the animal) shouldn't run in
    // place forever.
    private const float FleeMaxDurationSeconds = 20f;

    /// <summary>
    /// Real players can't fight back if they're empty-handed near a bear/
    /// wolf/hostile scientist - they run. Mirrors StartCombat's own shape
    /// (cancel current task, remember it, resume once safe) but ends in
    /// flight instead of gunfire: repeatedly sprints away from the
    /// threat's live position rather than a single one-shot walk, since
    /// the threat itself is very likely still moving/chasing too.
    /// </summary>
    // Renamed from StartFleeingFromAnimal (2026-08-14, scientist support) -
    // threat widened from BaseAnimalNPC to BaseCombatEntity earlier the
    // same day (Wolf2/Crocodile are Rust.Ai.Gen2.BaseNPC2, a completely
    // separate hierarchy from BaseAnimalNPC/Bear/Polarbear/Boar, so the old
    // BaseAnimalNPC-only signature meant an unarmed bot could never flee a
    // wolf or crocodile at all), then generalized in name too now that a
    // hostile scientist can trigger this exact same path. This method only
    // ever touches IsDestroyed/IsAlive()/transform.position, all on
    // BaseCombatEntity - nothing here is animal-specific.
    private void StartFleeingFromThreat(Survivor survivor, BaseCombatEntity threat, string reason = null, float? safeDistanceOverride = null)
    {
        Guid characterId = survivor.Character.Id;

        if (_activeFlee.ContainsKey(characterId))
        {
            // Already fleeing this (or another) threat - the existing timer
            // already re-evaluates the live threat position every tick, no
            // need to restart it.
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

        // safeDistanceOverride (2026-08-22) - the default FleeSafeDistance
        // (20m) is tuned for the original unarmed-vs-scientist/animal case
        // (their own aggro/engagement ranges are small). A BradleyAPC's own
        // BradleyDangerRadius is 100m (see TryFindNearbyBradley's own doc
        // comment) - ending the flee at a plain 20m clear would leave the
        // survivor still well within Bradley's danger radius, so the very
        // next ghost-route scan tick would immediately re-detect it and
        // re-flee, reading as a repeating stutter instead of one clean
        // escape.
        float safeDistance = safeDistanceOverride ?? FleeSafeDistance;

        // Scientist-specific flee (2026-09-21, Lucas's own explicit spec): a
        // survivor with no weapon that's being shot at by a hostile
        // scientist gains nothing from turning around and running a few
        // metres - it just gets shot in the back. It now runs 50-100m clear
        // and writes the spot off for itself (only - zones are per-survivor)
        // for 5 minutes, then carries on with whatever it was doing.
        bool scientistFlee = safeDistanceOverride == null && IsHostileScientist(threat);

        if (scientistFlee)
        {
            safeDistance = UnityEngine.Random.Range(ScientistFleeMinDistance, ScientistFleeMaxDistance);
        }

        // reason (2026-08-22) - the original wording only ever fit the
        // unarmed-vs-scientist case this function was built for; a fully
        // armed bot fleeing a BradleyAPC (see TryFindNearbyBradley) has a
        // weapon, it's just useless against Bradley, so the old fixed
        // "has no weapon to fight off" line would have been actively
        // misleading there. Defaults to the original message so the
        // existing on-sight-animal/unarmed call site is unchanged.
        Puts(reason ?? $"'{survivor.Character.Alias}' has no weapon to fight off '{GetAttackerDisplayName(threat)}' - fleeing.");

        // See ThreatFleePoisonRadius's own doc comment (LivingRust.
        // Looting.cs) - live trace evidence, 2026-08-15: without this, the
        // loot task EndFlee resumes walks straight back into the same
        // danger (a hostile scientist guarding a silo, most often) and
        // gets shot again, repeatedly, for the same survivor.
        if (scientistFlee)
        {
            PoisonAreaFromThreatFlee(survivor, npc.transform.position, ScientistFleePoisonRadius, ScientistFleePoisonDurationSeconds);
        }
        else
        {
            PoisonAreaFromThreatFlee(survivor, npc.transform.position);
        }

        // Real cover-seeking flee (2026-09-01, Lucas's own explicit ask:
        // "the same strafe or find cover ladder escalation rather than
        // just turnaround and run for 10m then stop"). Reuses TryFindCoverPoint
        // (Piece 1 of the armed tactical decision system, this file's own
        // "Tactical decision-making" section further down) exactly the way
        // StartTacticalRetreat already does for an ARMED bot - this
        // UNARMED/can't-fight flee path never had it, hence the crude
        // straight-line-and-stop behaviour. desperate: true always (not
        // conditional on health like the armed system's own desperate
        // flag) - a survivor already fleeing because it has NO weapon (or
        // is running from something unfightable like Bradley) is ALWAYS in
        // the "last-ditch, any real cover beats none" situation
        // TryFindCoverPoint's own desperate mode relaxes for. Re-evaluated
        // every real reassess tick (FleeReassessIntervalSeconds, this
        // function is called again from the timer below) - as the
        // survivor moves, new cover options come into range and get
        // picked up automatically, the same "adapts as the situation
        // changes" quality the armed tactical ladder already has, not a
        // single one-shot destination.
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

            // Directly on top of each other (near-zero direction) - pick an
            // arbitrary escape direction rather than dividing by ~zero.
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
                // Diagnostic only (2026-08-15) - live evidence of a
                // residual repeated-flee pattern against the same
                // 'Scientist' (5-9x per bot in a few minutes, down from
                // 50-60+ pre-FleeSafeDistance-fix) with a suspiciously
                // regular ~20s cadence, but neither of the two LOGGED
                // flee-end reasons ("safely clear"/"giving up on the
                // chase") ever appeared for it - meaning it's ending via
                // one of these three previously-silent paths. Logging
                // which one actually fires so the next trace can tell
                // "real player killed the scientist mid-chase" apart from
                // "something's actually wrong here" instead of guessing.
                VerbosePuts($"'{survivor.Character.Alias}' flee ended (npc gone/wounded/dead) after {Time.realtimeSinceStartup - fleeStartedAt:F0}s.");
                EndFlee(characterId, fleeTimer, survivor, previousTask);
                return;
            }

            // Got a real weapon mid-flight (looted one while running, or
            // was already mid-loot-task) - fight instead of continuing to
            // run. Re-enters through StartCombat's normal weapon-armed path
            // exactly as if this were a fresh attack.
            if (currentNpc.GetHeldEntity() is BaseProjectile)
            {
                EndFlee(characterId, fleeTimer, survivor, previousTask);
                StartCombat(survivor, threat);
                return;
            }

            if (threat == null || threat.IsDestroyed || !threat.IsAlive())
            {
                // See the npc-gone/wounded/dead branch above for why this is
                // now logged too. threat itself may be the null/destroyed
                // case being reported here, so GetAttackerDisplayName isn't
                // safe to call unconditionally.
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

        // No longer requires previousTask == LootForResources (2026-08-15) -
        // see EndCombat's identical fix for the full reasoning. A survivor
        // whose task had already gone to TaskType.None before fleeing
        // started got silently stranded forever once the flee ended -
        // exactly the "bots everywhere are just standing still" pattern,
        // and with on-sight flee now firing far more often (armed-only
        // gate removed earlier this session), this path is hit constantly.
        if (survivor.Player == null
            || survivor.Player.IsDestroyed
            || survivor.Character.State == CharacterState.Dead)
        {
            return;
        }

        // See PostCombatResumeCooldownSeconds' own doc comment
        // (LivingRust.Recycling.cs) - same reasoning as EndCombat's
        // identical line.
        _lastCombatEndTime[characterId] = Time.realtimeSinceStartup;

        // See ResumeOrStartLootTask's own doc comment - same "don't
        // abandon an already-started recycler" fix as EndCombat.
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
    // Tactical decision-making, Piece 1: cover-point discovery
    // (2026-08-23) - foundation for the still-unbuilt cover/flee/flank
    // scoring system Lucas and I scoped out this session. Deliberately
    // built and verified standalone (via /lr.debug.findcover) before
    // anything else depends on it, same "prove the hard/novel piece
    // works first" approach this whole project already follows.
    // ============================================================

    /// <summary>
    /// Real cover means solid geometry physically between the survivor and
    /// the attacker's eyes - Lucas's own explicit correction early in this
    /// design: a bot (or a player) just turning around does NOT break line
    /// of sight, since Rust's real combat is a raycast to the body, not a
    /// facing check. This deliberately does NOT try to recognize what kind
    /// of object provides cover (rock, tree, wall, hill) - it treats all
    /// solid Rust geometry uniformly via the same real physics query
    /// everything else in this codebase already uses for obstacles
    /// (compare NonSteppableColliderNames/ground probes - always
    /// geometry-driven, never a semantic prop list). A candidate point only
    /// counts as cover if a Linecast from ITS eye-height to the attacker's
    /// eyes is genuinely blocked - the exact same raycast
    /// HasCombatLineOfSight already uses to decide whether the survivor can
    /// see/be seen right now, just evaluated from a hypothetical point
    /// instead of the survivor's live position.
    ///
    /// Distances checked nearest-first, angles checked "straight away from
    /// the attacker" first - a real retreat that finds equally-valid cover
    /// closer or more directly away is preferred over a technically-valid
    /// but needlessly distant/roundabout one.
    /// </summary>
    private static readonly float[] CoverSearchDistances = { 5f, 8f, 12f };

    /// <summary>
    /// 2026-08-24, narrowed from a full 360-degree fan (which used to
    /// include 135/-135/180) after Lucas's own live report: cover picked
    /// from near-directly-behind the survivor meant turning its back on
    /// the attacker and sprinting away exposed the whole approach - "that
    /// instantly makes the bot vulnerable." Now stays within a forward-
    /// biased 180-degree arc centred on "away from the attacker" (matches
    /// CoverMaxDirectionAngle's own use in the monument-cache lookup,
    /// which needs an explicit angle filter since it isn't iterating a
    /// fan to begin with).
    /// </summary>
    private static readonly float[] CoverSearchAngleOffsets = { 0f, 30f, -30f, 60f, -60f, 90f, -90f };

    /// <summary>
    /// Full 360-degree fan, only used when the live search is called in
    /// "desperate" mode (critically low health - see TryFindCoverPoint's
    /// own doc comment). The normal forward-biased arc above exists so a
    /// healthy bot doesn't turn its back on an attacker for ordinary
    /// tactical cover; a bot that's about to die anyway has nothing left
    /// to lose by doing exactly that if it's the only way to break LOS at
    /// all.
    /// </summary>
    private static readonly float[] CoverSearchAngleOffsetsDesperate = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f };

    /// <summary>
    /// Reject a candidate whose real ground height differs from the
    /// survivor's own CURRENT height by more than this - 2026-08-23, live
    /// report: a candidate landed on top of a ~20m rock formation (blocks
    /// LOS from up there, technically passes the raycast test, but is
    /// physically unreachable on foot from ground level) and StartWalking
    /// correctly gave up on it as unreachable. TryFindGroundBelow's own
    /// search window can find a real, walkable surface that's still
    /// nowhere near the survivor's actual current elevation (a cliff top,
    /// a boulder's flat crown) - this keeps candidates to "roughly the
    /// same level the survivor is already standing at," which is what's
    /// actually reachable by ordinary ground movement, not just "some real
    /// surface exists somewhere in the vertical search window."
    /// </summary>
    private const float CoverMaxVerticalDelta = 4f;

    /// <summary>
    /// Backs a found cover point off slightly toward the survivor's own
    /// current position (2026-08-23, live report: the bot ended up
    /// standing flush against the blocking wall/rock surface itself,
    /// "aggressively hugging" it) - the raw candidate point sits exactly
    /// at whatever distance was sampled, which can land right at the
    /// blocking geometry's own surface. Pulling back along the same
    /// direction the candidate was projected out on gives a small real
    /// gap without needing a second geometry query.
    /// </summary>
    private const float CoverStandoffDistance = 0.5f;

    /// <summary>
    /// Real max distance worth traveling for cover at all (2026-08-24,
    /// Lucas's own explicit call: "the bot should only use cover within
    /// 10-20 metres of itself... running 40-50m away to find cover... has
    /// it sometimes turning away and running to that cover which instantly
    /// makes the bot vulnerable"). The live fan search already stays
    /// within this by construction (CoverSearchDistances tops out at 12m),
    /// but the monument-cache lookup has no such natural ceiling - it just
    /// sorts whatever cached points exist by distance, which without an
    /// explicit cap could reach for a technically-valid point far outside
    /// what's actually tactically useful mid-fight.
    /// </summary>
    private const float CoverMaxUsefulDistance = 20f;

    /// <summary>
    /// Max angle from "directly away from the attacker" a cover point can
    /// sit at and still be considered, for the monument-cache lookup
    /// specifically - the live fan search achieves the same real limit
    /// structurally via CoverSearchAngleOffsets' own narrowed range
    /// (+/-90 degrees either side of dead-away = 180 degrees total), so
    /// this uses the identical effective arc for consistency between the
    /// two search paths.
    /// </summary>
    private const float CoverMaxDirectionAngle = 100f;

    /// <summary>
    /// Real cover means a MAJORITY of the body shielded, not a fully
    /// unbroken sightline - 2026-08-24, Lucas's own explicit correction:
    /// "the bot shouldn't have to theoretically find cover that breaks LOS
    /// but cover that covers majority of its body," using concrete
    /// concrete bollards as the example (shield the legs/lower body,
    /// leave the torso/head exposed - genuinely useful real cover despite
    /// never fully blocking a headshot-height sightline). Replaces the old
    /// single eye-height Linecast (which demanded TOTAL occlusion, ruling
    /// out low cover like bollards/sandbags/waist-high walls entirely)
    /// with two checks at proportional body-height fractions of the
    /// survivor's own real eye height - low (~ankle/lower-leg) and mid
    /// (~waist/torso). Both need to be blocked to count; the eye-height
    /// sightline itself is deliberately NOT required to be blocked at all,
    /// since real low cover never blocks that and it's still worth using.
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
    /// desperate (2026-08-24, Lucas's own explicit request) relaxes the
    /// direction/angle restriction ONLY, both here and in
    /// TryFindCoverPointNearMonument - real geometry/ground validation and
    /// the distance cap (CoverMaxUsefulDistance) are unchanged either way.
    /// See TryStartTacticalRepositioning's own call site for exactly when
    /// this gets set.
    /// </summary>
    private bool TryFindCoverPoint(BasePlayer npc, BaseCombatEntity attacker, out Vector3 coverPoint, bool desperate = false)
    {
        coverPoint = default;

        if (npc == null || npc.IsDestroyed || attacker == null || attacker.IsDestroyed)
        {
            return false;
        }

        // Prefer the pre-scanned monument cache when near a known monument
        // (see LivingRust.MonumentCoverPoints.cs's own top-of-file doc
        // comment) - only falls through to the live fan search below if
        // there's no monument nearby, or that monument type has no cached
        // points (never scanned, or genuinely has no qualifying structure).
        // TryGetNearestMonumentForCover scales its own search radius to
        // each monument's REAL size (not a single flat constant) - see its
        // own doc comment for the real live bug this fixes (a flat 100m
        // check silently failed anywhere in most of Launch Site's own
        // ~200m real footprint).
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
            // Standing right on top of the attacker (near-zero direction) -
            // pick the survivor's own current facing as a fallback rather
            // than dividing by ~zero, same pattern StartFleeingFromThreat's
            // own near-zero fallback already uses.
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
                    // Real ground, but not reachable-by-walking real ground -
                    // see CoverMaxVerticalDelta's own doc comment.
                    continue;
                }

                Vector3 groundPoint = new Vector3(candidateXZ.x, groundY, candidateXZ.z);

                if (_engine.NavigationManager.IsBodyOverlapping(groundPoint))
                {
                    // Real solid geometry occupies the candidate spot itself
                    // (inside a rock/wall), not just nearby - can't actually
                    // stand there.
                    continue;
                }

                if (IsPartialCoverPoint(groundPoint, attackerEyePos, eyeHeight))
                {
                    // Genuinely blocks a majority of the body - real cover,
                    // found via physics, not via recognizing what's
                    // actually in the way. Backed off slightly toward the
                    // survivor's own current position - see
                    // CoverStandoffDistance's own doc comment - rather than
                    // standing flush against whatever's blocking.
                    float standoffFraction = Mathf.Clamp01(1f - CoverStandoffDistance / distance);
                    coverPoint = Vector3.Lerp(selfPos, groundPoint, standoffFraction);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// How close to lastKnownPosition counts as "arrived" for the hunt-
    /// search behaviour below - real ground-navigation slop (a walk target
    /// rarely lands on the exact meter), not a tight precision check.
    /// </summary>
    private const float HuntSearchArrivalDistance = 5f;

    /// <summary>
    /// Real radius for the hunt-search sweep (2026-08-24, Lucas's own
    /// explicit request/wording: "walk to a random coordinate within 20
    /// metres of that last known position rather than standing still...
    /// that way it looks like the bot is properly hunting").
    /// </summary>
    private const float HuntSearchRadius = 20f;

    /// <summary>
    /// Whether a survivor currently has a hunt-search loop actively
    /// chaining itself via StartWalkingWithRecovery's own onArrived/
    /// onFailed callbacks - both a "don't start a second loop on top of an
    /// already-running one" guard (PushTowardLastKnownPosition can be
    /// called several times a second from the combat tick) and the actual
    /// stop signal ContinueHuntSearch checks before each new leg. Cleared
    /// the instant real LOS is regained (see StartCombat's own rawLos
    /// block) or combat ends (EndCombat) - a stray onArrived/onFailed
    /// firing after either just quietly no-ops instead of fighting
    /// whatever movement is running next, same defensive-generation-token
    /// shape as every other chained-timer state in this file.
    /// </summary>
    private readonly Dictionary<Guid, bool> _huntSearchActive = new();

    /// <summary>
    /// A blind push toward a lost attacker's last known position - 2026-08-24,
    /// Lucas's own explicit request: "I want the bots to still push to the
    /// last known position... but try and use cover along the way to that
    /// position, it doesn't need to be overly complex" (specifically NOT
    /// "hug every wall till I get to that last known position," which would
    /// read as predictable/robotic in the opposite direction). Only ever
    /// tries the monument cover-point cache (TryFindCoverPointAlongPath,
    /// LivingRust.MonumentCoverPoints.cs) - open terrain has no cache to
    /// draw from, so a straight walk there is already the right call, same
    /// as it always was. Falls straight through to the original plain walk
    /// whenever no monument is nearby or no cached point happens to sit
    /// reasonably on the route - this is opportunistic, not a guarantee.
    ///
    /// Once genuinely AT lastKnownPosition (within HuntSearchArrivalDistance)
    /// with still no LOS, hands off to a self-sustaining hunt-search loop
    /// (ContinueHuntSearch) instead of just standing there re-affirming the
    /// same destination every tick - "properly hunting" per Lucas's own
    /// framing, not freezing the instant the trail goes cold.
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
    /// One leg of the hunt-search sweep - walks to a fresh random point
    /// within HuntSearchRadius of the ORIGINAL lastKnownPosition (the
    /// anchor, not the survivor's own drifting current position, so the
    /// search stays centred on where the target was actually last seen
    /// rather than wandering progressively further away), ground-snapped
    /// the same way live cover candidates already are. Chains into another
    /// leg via onArrived/onFailed - genuinely self-sustaining, no outer
    /// timer re-driving it - until _huntSearchActive says to stop (LOS
    /// regained or combat over).
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
