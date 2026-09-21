using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingRust.Models;
using LivingRust.Navigation;
using Oxide.Plugins;
using Rust;
using Rust.Ai.Gen2;
using UnityEngine;
using UnityEngine.AI;

namespace Carbon.Plugins;

public partial class LivingRust
{
    /// <summary>
    /// How far from wherever a survivor currently stands (re-centered on
    /// wherever the last looted container was, each cycle) to keep looking
    /// for the next container. Deliberately NOT monument-seeking - loot
    /// containers (barrels, crates, junkpile_a through junkpile_e) spawn
    /// along roads and paths too, not only inside monuments, so anchoring
    /// this to "nearest monument" would bake in a wrong assumption about
    /// where loot actually exists. A plain radius scan around wherever the
    /// survivor already is works everywhere, monument or not.
    /// </summary>
    private const float LootSearchRadius = 50f;

    /// <summary>
    /// Real "how far away could I plausibly have noticed a body" range
    /// (2026-08-25, Lucas's own explicit correction) - a corpse/dropped
    /// bag search reusing the full LootSearchRadius (50m) read as
    /// unrealistic: "the bot is unrealistically searching for corpse bags/
    /// lootable bodies at pretty absurd distances... it overrode the
    /// gather ore command to loot bodies about 30-40 metres away." Applies
    /// to ordinary AMBIENT corpse/bag discovery only - a survivor's own
    /// genuine kill (see _recentOwnKillPositions/IsRememberedOwnKill's own
    /// doc comment below) is deliberately exempt from this cap, since
    /// Lucas's own framing was explicit: "the bot would know where the
    /// player died if it won the fight," a real distinction between
    /// stumbling onto a random body versus walking back to one it made
    /// itself.
    /// </summary>
    private const float CorpseAmbientAwarenessRadius = BotOnSightDetectionRange;

    /// <summary>
    /// How long a survivor's own kill stays reachable at range before this
    /// project stops treating it as "known" - a real player wouldn't
    /// remember/care about a fight from 20 minutes ago forever, and this
    /// also bounds the dictionary from growing unboundedly for a
    /// long-running server. Cleared immediately on the survivor's own
    /// death regardless (OnPlayerDeath, LivingRust.Hooks.cs) - a fresh
    /// respawn has no memory of a previous life's kills.
    /// </summary>
    private const float OwnKillMemoryDurationSeconds = 300f;

    /// <summary>
    /// How close a candidate corpse's real position needs to be to a
    /// remembered kill's death position to count as "that kill" - position-
    /// based matching (not a direct entity/NetworkableId reference)
    /// specifically because the real corpse doesn't necessarily spawn at
    /// the EXACT death position (ragdoll settling, a body corpse's own
    /// spawn offset) and OnEntityDeath fires before that's necessarily
    /// resolved - a small real-world tolerance is simpler and more robust
    /// than trying to chase down the exact corpse reference at hook time.
    /// </summary>
    private const float OwnKillMemoryMatchRadius = 5f;

    /// <summary>
    /// Per-survivor: (real death position of something they personally
    /// killed, when this memory expires) - written by OnEntityDeath
    /// (LivingRust.Hooks.cs), read by IsRememberedOwnKill below. Only ever
    /// holds the MOST RECENT kill per survivor (a fresh kill overwrites
    /// the previous one) - deliberately simple rather than a full history,
    /// since "the bot would know where the player died if it won the
    /// fight" only really needs the last fight, not a lifetime log.
    /// </summary>
    // Holds several recent kills per survivor, not just the last (2026-09-21):
    // a survivor that wins a string of fights used to remember only the
    // final body and walk past every earlier one.
    private const int OwnKillMemoryMaxEntries = 8;
    private readonly Dictionary<Guid, List<(Vector3 Position, float ExpiresAt)>> _recentOwnKillPositions = new();

    private void RememberOwnKill(Guid characterId, Vector3 position)
    {
        if (!_recentOwnKillPositions.TryGetValue(characterId, out List<(Vector3 Position, float ExpiresAt)> kills))
        {
            kills = new List<(Vector3, float)>();
            _recentOwnKillPositions[characterId] = kills;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;
        kills.RemoveAll(k => k.ExpiresAt <= now);
        kills.Add((position, now + OwnKillMemoryDurationSeconds));

        if (kills.Count > OwnKillMemoryMaxEntries)
        {
            kills.RemoveAt(0);
        }
    }

    /// <summary>
    /// See _recentOwnKillPositions' own doc comment. Self-expiring - a
    /// stale entry past OwnKillMemoryDurationSeconds is removed the first
    /// time anything actually checks it, no separate cleanup timer needed.
    /// </summary>
    private bool IsRememberedOwnKill(Guid characterId, Vector3 corpsePosition)
    {
        if (!_recentOwnKillPositions.TryGetValue(characterId, out List<(Vector3 Position, float ExpiresAt)> kills))
        {
            return false;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;

        foreach ((Vector3 position, float expiresAt) in kills)
        {
            if (expiresAt > now && Vector3.Distance(corpsePosition, position) <= OwnKillMemoryMatchRadius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deliberately much smaller than LootSearchRadius - see
    /// foundCollectible's own doc comment in ContinueLootTask for why.
    /// This is now just the outer search ceiling (the widest any single
    /// collectible type is allowed - see GetCollectibleDivertRadius,
    /// 2026-08-18) - the real per-type cutoff (berries 3m, mushrooms 5m,
    /// real resources - hemp/wood/stone/metal ore/sulfur ore - 15m) is
    /// enforced per-candidate in the search filter, same split
    /// EnRouteCollectibleDetectionRadius handles for the en-route detour
    /// search.
    /// </summary>
    private const float CollectibleSearchRadius = 15f;

    /// <summary>
    /// 2026-08-18, Lucas's own explicit redesign of the strict type-tier
    /// system below - see ContinueLootTask's own doc comment at the
    /// selection block for the full reasoning ("he got too focused on go
    /// to monument rather than loot what's in front of me first").
    /// Anything within this distance wins outright over the old fixed
    /// tier order, regardless of type - whichever's genuinely closest.
    /// </summary>
    private const float NearbyLootPriorityRadius = 30f;

    /// <summary>
    /// How often to poll whether the equipped tool's real swing cooldown
    /// has cleared - not the swing cadence itself, which now comes
    /// entirely from the tool's own real AttackEntity.repeatDelay (read at
    /// runtime via BaseMelee.HasAttackCooldown(), see StartAttackingContainer).
    /// Deliberately short so a hit lands promptly once the real cooldown
    /// actually clears, rather than adding its own extra polling delay on
    /// top of the tool's real one.
    /// </summary>
    private const float AttackHitInterval = 0.1f;

    /// <summary>
    /// Fallback damage per hit, only used if the survivor somehow has no
    /// melee tool equipped at all (shouldn't normally happen -
    /// GiveStartingKit always gives a rock). Real damage now comes from
    /// GetToolDamage's read of the actually-equipped tool's own real
    /// stats - this constant used to be applied unconditionally
    /// regardless of tool, and at 40 was simply too high against a real
    /// barrel's health (35-50, per a live report) - one hit reliably
    /// one/two-shot it outright, nothing to do with the separate
    /// double-damage bug ServerUse_Strike's cancelled invoke fixes below.
    /// </summary>
    private const float AttackDamagePerHit = 6f;

    /// <summary>
    /// How long a direct loot (crates/boxes - see RequiresDestructionToLoot)
    /// takes before completing, rather than transferring instantly on
    /// arrival - an instant transfer read as "inhuman" next to the
    /// barrel/roadsign combat loop, which naturally takes a few seconds
    /// of real swinging.
    /// </summary>
    private const float DirectLootDelay = 2f;

    /// <summary>
    /// Real max distance a survivor can be from a container and still
    /// loot/attack it - neither StartAttackingContainer (HasLineOfSight
    /// only) nor LootContainerDirectly (no check at all) ever verified
    /// actual proximity before this existed, trusting "arrival" alone. A
    /// live test caught a survivor looting a container ~10-15m away,
    /// stuck on the far side of a sandbag wall the whole time - a clear
    /// line of sight over/through the sandbags was all HasLineOfSight
    /// needed, and this project's own GetApproachPoint doc comment already
    /// flagged the exact same class of bug once before ("a bot destroyed
    /// two barrels through a solid wall"). GetApproachPoint's own standoff
    /// distance is 0.6m from the container's bounds, so this is generous
    /// slack for agent radius/rounding, not an invitation to loot from
    /// across a room.
    /// </summary>
    private const float LootInteractionRange = 3f;

    /// <summary>
    /// Safety cap on hits against a single container - guards against a
    /// container whose health doesn't actually drop for some reason (a
    /// protection/invulnerability edge case) leaving a survivor stuck
    /// swinging forever instead of giving up and moving on.
    /// </summary>
    private const int MaxHitsPerContainer = 20;

    /// <summary>
    /// Same safety-cap idea as MaxHitsPerContainer, but door barricades
    /// (Barricade class - confirmed via decompile, defaults 100 max
    /// health, no protection scaling seen) are a beefier, structural
    /// obstacle a real player expects to spend more hits on than a
    /// wooden barrel.
    /// </summary>
    private const int MaxHitsPerBarricade = 40;

    /// <summary>
    /// Search radius EscalateStuckRecovery uses to look for a real nearby
    /// Barricade - wider than LootInteractionRange (2026-08-16, was equal
    /// to it originally) since a live test showed a stuck survivor can be
    /// several metres off from the barricade that's actually the root
    /// cause by the time this fires, not necessarily standing right
    /// against it - see TryFindBlockingBarricade's own doc comment.
    /// </summary>
    private const float BarricadeAttackDetectionRange = 6f;

    // Widened from Door's own real RPC_Server.MaxDistance (3f, confirmed
    // via decompile) to match BarricadeAttackDetectionRange's own
    // precedent (2026-08-29, fourth round - Lucas's own report: a bot
    // stuck oscillating outside a foundation, near a wall, never actually
    // near enough to the real door for the original 3f OverlapSphere to
    // ever find it - closed security doors disable their own NavMeshLink,
    // so native pathfinding routes AROUND the whole structure looking for
    // another way in rather than walking up to the door itself, same root
    // cause BarricadeAttackDetectionRange's own doc comment already
    // documents for barricades: "a stuck survivor can be several metres
    // off from the barricade that's actually the root cause." This call
    // only ever directly sets Door.SetOpen server-side (no RPC involved),
    // so the real 3f interaction range was never actually a hard
    // requirement here in the first place.
    private const float DoorOpenDetectionRange = 6f;

    // Small buffer past the door's own exact center (2026-08-29, second
    // round) - guarantees the survivor ends up clearly clear of the
    // frame's own collider before handing back to normal collision-
    // respecting movement, rather than potentially still overlapping it
    // right at the door's own pivot point.
    //
    // Widened from 1.5f (2026-08-29, seventh round - Lucas's own live
    // report + trace confirmation: a bot crossing its own GROUND FLOOR
    // door correctly landed at the door's real height both phase legs,
    // then immediately after SetOpen(false) closed it behind itself,
    // its very next walk tick showed it sitting on the SECOND STORY
    // floor slab instead, ~3.1m up - "visually phasing in and out of the
    // doorway and ontop of the base build"). 1.5f wasn't real clearance
    // of a door's actual frame/hinge collider (door.transform.position is
    // the hinge pivot, not necessarily the true geometric center of the
    // full frame+swing bounds) - the survivor was still overlapping the
    // door's collider the instant it resolidified, and Unity's physics
    // resolved that overlap by pushing the capsule out - straight up onto
    // the floor slab directly overhead on a 2-story design, since that's
    // the nearest free space in that direction, rather than sideways.
    private const float DoorGhostClearanceDistance = 2.5f;

    // Real safety-net tolerance (2026-08-29, seventh round) - after
    // closing the door behind a just-completed ground-level crossing, the
    // survivor's real height is already precisely known (the door's own
    // real Y, just phased to) - if it differs from that by more than a
    // normal step's worth, something (the closing door's own physics
    // push, most likely) moved it somewhere it shouldn't be, and this is
    // corrected directly rather than trusting whatever SnapToGround's own
    // generic consensus probe would find nearby (which is exactly what
    // let a push onto an overhead floor slab go uncorrected in the first
    // place - a real steppable surface, just the wrong one).
    private const float DoorCloseHeightCorrectionTolerance = 0.5f;

    // Real max height a survivor should ever "jump" up to reach a door
    // (2026-08-29, fifth round - Lucas's own explicit framing: a flat
    // foundation on sloped terrain always has ITS door end up higher
    // above the ground on whichever edge lands on the downhill side -
    // "hard to tell when they will build it which way" since that
    // depends entirely on the site's own terrain, not a controllable
    // choice - so a real player just jumps up into the frame instead,
    // "a real player could still technically make it through the door by
    // jumping up into the frame." Roughly a real player's own jump-plus-
    // grab reach in Rust - well past FoundationGroundClearance's own
    // deliberate 1m bias (BaseBuilding.cs) plus normal extra slope
    // variance, but nowhere near tall enough to paper over a genuinely
    // broken multi-metre build-height bug (GroundHeightMismatchTolerance
    // already exists to catch and abort THOSE separately).
    private const float DoorJumpableHeight = 2f;

    /// <summary>
    /// Real short, fully-known crossing (2026-08-29, Lucas's own explicit
    /// ask - see EscalateStuckRecovery's own doc comment at the one call
    /// site for the full reasoning and the explicit "never for a door it
    /// didn't place" security boundary this is scoped behind).
    ///
    /// Second round (2026-08-29, Lucas's own live report: "still seems to
    /// be teleporting around quite a bit" after the first version) - the
    /// original approach continued in whatever direction the survivor was
    /// heading TOWARD destination, which could clip alongside the wall
    /// instead of straight through the actual opening whenever destination
    /// wasn't well-aligned with the door's own axis, missing the doorway
    /// and re-triggering the same block repeatedly (visibly reading as
    /// erratic repeated hops, exactly what got reported). Phasing straight
    /// to the door's own known transform.position first removes that
    /// guesswork entirely - that position IS the doorway opening,
    /// regardless of the survivor's approach angle or where it's ultimately
    /// headed beyond it. A short second leg then clears it of the door's
    /// own collider before handing back to normal collision-respecting
    /// movement for the rest of the real journey to destination.
    /// </summary>
    private void GhostThroughOwnDoor(Survivor survivor, Door door, Vector3 destination, Action onArrived, Action onFailed, int recoveryTier)
    {
        BasePlayer npc = survivor.Player;
        Vector3 doorCenter = door.transform.position;

        Vector3 direction = destination - doorCenter;
        direction.y = 0f;

        if (direction.sqrMagnitude < 0.01f)
        {
            direction = npc.transform.forward;
        }

        Vector3 clearOfFrame = doorCenter + direction.normalized * DoorGhostClearanceDistance;
        clearOfFrame.y = doorCenter.y;

        // Real live ask (2026-08-29, second round - Lucas's own explicit
        // request: "the bot will have to open and close the door upon
        // leaving the base and also when entering... if the doors are
        // left open it defeats the purpose of having doors with
        // codelocks") - closed again once clear of the frame, same real
        // security restored either direction as CrossHomeDoor's own
        // precomputed route already does.
        void CloseDoorAndContinue()
        {
            if (!door.IsDestroyed && door.IsOpen())
            {
                door.SetOpen(false);
                door.SendNetworkUpdate();
            }

            // See DoorCloseHeightCorrectionTolerance's own doc comment -
            // closing the door can physically shove the survivor if it's
            // still overlapping the door's own collider, most often
            // straight up onto an overhead floor slab on a multi-story
            // design. doorCenter.y is the one height already known for
            // certain to be correct here (the survivor was just phased
            // to it), so any real drift beyond a normal step gets written
            // back directly rather than trusting a fresh ground probe.
            if (npc != null && !npc.IsDestroyed && Mathf.Abs(npc.transform.position.y - doorCenter.y) > DoorCloseHeightCorrectionTolerance)
            {
                Vector3 corrected = npc.transform.position;
                corrected.y = doorCenter.y;
                npc.transform.position = corrected;
                npc.MovePosition(corrected);
            }

            StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier);
        }

        StartLevelledPhasing(
            survivor,
            doorCenter,
            onArrived: () => StartPhasingToDestination(
                survivor,
                clearOfFrame,
                onArrived: CloseDoorAndContinue,
                onFailed: CloseDoorAndContinue),
            onFailed: CloseDoorAndContinue);
    }

    /// <summary>
    /// Real "can this survivor legitimately open this door" check
    /// (2026-08-29, Lucas's own explicit ask: "how the bot gets in and
    /// out of the base without teleporting"). Matches real Rust's own
    /// access rule, confirmed via decompile - opening a door has nothing
    /// to do with OwnerID/building privilege, only whether it's currently
    /// locked: no lock at all, or a lock that isn't engaged (IsLocked()
    /// false), means ANY player could open it; a locked CodeLock only
    /// yields if this specific survivor is on its real whitelistPlayers
    /// list (the same list PlaceCodeLockReplayRow itself adds a bot's own
    /// userID to when it builds and locks its own door).
    /// </summary>
    /// <summary>
    /// Real "stand square in front of the door, not off to the side"
    /// point (2026-08-29, fourth round) - offsets from the door's own
    /// center along whichever of the door's local axes best separates it
    /// from the survivor's current position (its real forward/back facing
    /// if the survivor is roughly ahead/behind it, otherwise its
    /// perpendicular right/left), staying on the survivor's OWN current
    /// side (never crossing the door's plane - this is a plain walk, not
    /// a phase). Locks Y to the door's own transform.position the same
    /// way GhostThroughOwnDoor's clearOfFrame already does, so the
    /// approach itself never tries to climb/dive at an angle toward it.
    /// </summary>
    private Vector3 ComputeDoorApproachPoint(BasePlayer npc, Door door)
    {
        Vector3 doorCenter = door.transform.position;
        Vector3 toNpc = npc.transform.position - doorCenter;
        toNpc.y = 0f;

        Vector3 doorForward = door.transform.forward;
        doorForward.y = 0f;
        doorForward = doorForward.sqrMagnitude > 0.01f ? doorForward.normalized : Vector3.forward;

        // Whichever of the door's own forward/back axis the survivor is
        // more aligned with wins - keeps the approach point flush with
        // the doorway's real opening axis instead of an arbitrary side.
        float alignment = toNpc.sqrMagnitude > 0.01f ? Vector3.Dot(toNpc.normalized, doorForward) : 1f;
        Vector3 approachDirection = alignment >= 0f ? doorForward : -doorForward;

        Vector3 approachPoint = doorCenter + approachDirection * DoorGhostClearanceDistance;
        approachPoint.y = doorCenter.y;
        return approachPoint;
    }

    private bool CanOpenDoor(BasePlayer npc, Door door)
    {
        BaseLock doorLock = door.GetSlot(BaseEntity.Slot.Lock) as BaseLock;

        if (doorLock == null || !doorLock.IsLocked())
        {
            return true;
        }

        return doorLock is CodeLock codeLock && codeLock.whitelistPlayers.Contains(npc.userID);
    }

    /// <summary>
    /// Real nearby-closed-door check (2026-08-29) - same real Construction
    /// layer every other construction-adjacent check in this project
    /// already scans (barricades/cactus etc use their own dedicated
    /// masks; doors sit on Construction like any other BuildingBlock-
    /// family piece).
    /// </summary>
    private bool TryFindBlockingClosedDoor(BasePlayer npc, out Door blockingDoor)
    {
        Collider[] hits = Physics.OverlapSphere(npc.transform.position, DoorOpenDetectionRange, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

        foreach (Collider hit in hits)
        {
            Door candidate = hit.GetComponentInParent<Door>();

            if (candidate == null || candidate.IsDestroyed || candidate.IsOpen())
            {
                continue;
            }

            if (CanOpenDoor(npc, candidate))
            {
                blockingDoor = candidate;
                return true;
            }
        }

        blockingDoor = null;
        return false;
    }

    /// <summary>
    /// Known melee-capable tool shortnames, best (fastest/most efficient)
    /// first - a real player would grab whatever's quickest for the job
    /// rather than sticking with the starting rock once something better
    /// turns up. The original list guessed underscore-joined names
    /// (e.g. "combat_knife") - a live test confirmed via real looted-item
    /// logs that this was silently wrong for every dotted item on the
    /// list (Rust's real convention: "knife.combat", not "combat_knife" -
    /// confirmed live shortnames were icepick.salvaged, knife.combat,
    /// salvaged.cleaver, salvaged.sword), meaning EquipBestMeleeTool never
    /// recognized ANY of them and silently kept the starting rock
    /// equipped the entire game, even after looting a real cleaver/mace.
    /// "mace" (a real, separately-existing melee weapon) was also missing
    /// entirely. OnServerInitialized now validates every entry here
    /// against ItemManager.FindItemDefinition on startup and logs a
    /// warning for anything that still doesn't resolve, specifically so
    /// this exact failure mode - a wrong shortname masquerading as a
    /// working one - can't hide silently again.
    /// </summary>
    /// <summary>
    /// Comprehensive as of a 2026-08-09 full scan of every real
    /// Category:"Weapon"/"Tool" item in the bundled item database (grep
    /// across every Bundles/items/*.json for its real Category field,
    /// cross-checked against decompiled Assembly-CSharp.dll for anything
    /// ambiguous) - Lucas's explicit request that this stop being
    /// effectively hardcoded to whichever few items happened to show up
    /// in a live test so far. Deliberately excludes anything NOT
    /// confirmed real melee-attack-capable: mounted/siege
    /// weapons (50cal.mounted, ballista, batteringram, catapult,
    /// siegetower - not inventory-holdable items at all) and power tools
    /// (chainsaw, jackhammer - real, but their actual attack mechanism
    /// wasn't verified as a real BaseMelee cast the way EquipBestMeleeTool
    /// needs; see this method's own doc comment on why a wrong guess here
    /// silently breaks barrel destruction rather than erroring loudly).
    /// Power tools are still ranked in GatherToolPriority below, which
    /// doesn't have that same casting requirement.
    /// </summary>
    private static readonly string[] MeleeToolPriority =
    {
        "salvaged.sword",
        "longsword",
        "salvaged.cleaver",
        "machete",
        "mace",
        "mace.baseballbat",
        "knife.combat",
        "spear.stone",
        "spear.wooden",
        "spear.cny",
        "knife.butcher",
        "pitchfork",
        "pickaxe",
        "stone.pickaxe",
        "concretepickaxe",
        "diverpickaxe",
        "lumberjack.pickaxe",
        "hatchet",
        "stonehatchet",
        "concretehatchet",
        "diverhatchet",
        "lumberjack.hatchet",
        "frontier_hatchet",
        "axe.salvaged",
        "icepick.salvaged",
        "bone.club",
        "knife.bone",
        "knife.bone.obsidian",
        "knife.skinning",
        "candycaneclub",
        "vampire.stake",
        "sunken.knife",
        "boomerang",
        "paddle",
        "rock",
    };

    /// <summary>
    /// Checks every MeleeToolPriority entry against the real, live item
    /// database (ItemManager.FindItemDefinition) at startup and logs a
    /// warning for anything that doesn't resolve - built specifically
    /// after a live test found half this list silently wrong (underscore-
    /// joined guesses instead of Rust's real dotted shortnames), which
    /// meant EquipBestMeleeTool never recognized several real looted tools
    /// at all, and nothing ever logged that fact. Deliberately checked
    /// once at startup rather than trusted forever - a future edit to this
    /// list re-guessing a shortname should be caught immediately on the
    /// next boot, not discovered again by a bot dying with the wrong
    /// weapon equipped.
    /// </summary>
    private void ValidateMeleeToolPriority()
    {
        List<string> unresolved = new();

        foreach (string shortname in MeleeToolPriority)
        {
            if (ItemManager.FindItemDefinition(shortname) == null)
            {
                unresolved.Add(shortname);
            }
        }

        if (unresolved.Count > 0)
        {
            Puts($"WARNING: MeleeToolPriority contains {unresolved.Count} shortname(s) that don't resolve to any real item - EquipBestMeleeTool will never recognize these: {string.Join(", ", unresolved)}.");
        }
        else
        {
            Puts($"MeleeToolPriority validated - all {MeleeToolPriority.Length} shortnames resolve to real items.");
        }
    }

    /// <summary>
    /// Best-to-worst ranking for belt SLOT 0 ("main weapon") specifically -
    /// deliberately a separate list from MeleeToolPriority, not an
    /// extension of it. MeleeToolPriority drives EquipBestMeleeTool's
    /// actual combat-equip behaviour, which casts the result to BaseMelee
    /// (npc.GetHeldEntity() as BaseMelee) to call ServerUse() - a gun or
    /// bow isn't a BaseMelee, so that cast would silently fail and break
    /// barrel/roadsign destruction if ranged weapons were mixed into that
    /// same list. This list only decides which item LOOKS like the
    /// survivor's main weapon for belt layout purposes (Lucas's own
    /// framing: "assault rifle is better than a bow and arrows") -
    /// OrganizeBelt only ever repositions items, it never changes what's
    /// equipped as the active item.
    ///
    /// Comprehensive as of a 2026-08-09 full scan of every real
    /// Category:"Weapon" item in the bundled item database - Lucas's
    /// explicit request that this stop being effectively hardcoded to
    /// whichever few items happened to show up in a live test so far
    /// ("that way it isn't hardcoded to L96 and LR300 rifle"). Deliberately
    /// excludes: mounted/siege weapons (50cal.mounted, ballista,
    /// batteringram, catapult, siegetower, homingmissile.launcher - not
    /// inventory-holdable items at all), weapon.mod.* (attachments, not
    /// weapons themselves), and thrown/deployed explosives (grenades,
    /// rocket launchers, supply/rf signals - a real weapon in the loose
    /// sense, but not something a player "wields as their main weapon" the
    /// way this list's purpose means, and not real BaseMelee/ranged-aim
    /// items this project's combat code has ever touched). Reskinned
    /// workshop variants of the same base weapon (rifle.ak.ice,
    /// rifle.lr300.space, ...) are deliberately NOT separately listed -
    /// they're rare skin-specific spawns, not a distinct weapon tier, and
    /// would just bloat this list without changing any real ranking
    /// decision.
    /// </summary>
    private static readonly string[] WeaponPriority =
    {
        "lmg.m249",
        "hmlmg",
        "minigun",
        "smg.thompson",
        "smg.mp5",
        "smg.2",
        "t1_smg",
        "rifle.ak",
        "rifle.lr300",
        "rifle.m39",
        "m16a2",
        "rifle.semiauto",
        "rifle.sks",
        "shotgun.m4",
        "krieg.shotgun",
        "rifle.l96",
        "rifle.bolt",
        "shotgun.spas12",
        "shotgun.pump",
        "shotgun.double",
        "shotgun.waterpipe",
        "pistol.m92",
        "revolver.hc",
        "pistol.python",
        "pistol.revolver",
        "pistol.semiauto",
        "pistol.semiauto.a.m15",
        "pistol.prototype17",
        "pistol.nailgun",
        "pistol.eoka",
        "bow.compound",
        "crossbow",
        "crossbowbowless",
        "bow.hunting",
        "minicrossbow",
        "speargun",
        "blowpipe",
        "salvaged.sword",
        "longsword",
        "salvaged.cleaver",
        "machete",
        "mace",
        "mace.baseballbat",
        "knife.combat",
        "spear.stone",
        "spear.wooden",
        "spear.cny",
        "knife.butcher",
        "pitchfork",
        "bone.club",
        "knife.bone",
        "knife.bone.obsidian",
        "knife.skinning",
        "candycaneclub",
        "vampire.stake",
        "sunken.knife",
        "boomerang",
        "paddle",
        "snowballgun",
        "paintballgun",
        "gun.water",
        "pistol.water",
    };

    /// <summary>
    /// Real fully-automatic firearms (sustained full-auto fire, confirmed
    /// via decompiling Assembly-CSharp.dll / known real Rust weapon
    /// behaviour - m16a2 deliberately excluded despite being select-fire,
    /// since its real Rust implementation is 3-round burst, not sustained
    /// automatic). Lucas's own named examples ("thompson, m249, rifle.ak,
    /// lr300 etc"), extended to the rest of the real automatic roster
    /// (mp5, the custom smg.2, hmlmg, minigun, t1_smg) per the same
    /// 2026-08-09 full item-database scan WeaponPriority's own doc
    /// comment describes. Used purely to decide belt slot 2's "offsider"
    /// pick: if the best owned weapon (slot 1) is one of these, slot 2
    /// should be the best NON-automatic weapon instead (a sniper, pistol,
    /// or shotgun - Lucas's own examples of "predominantly offsider
    /// weapons"), not a second automatic. Deliberately NOT a rewrite of
    /// WeaponPriority's own ranking - both lists stay independent, this
    /// is purely a category tag layered on top.
    /// </summary>
    private static readonly string[] AutomaticWeaponShortnames =
    {
        "lmg.m249",
        "hmlmg",
        "minigun",
        "smg.thompson",
        "smg.mp5",
        "smg.2",
        "t1_smg",
        "rifle.ak",
        "rifle.lr300",
    };

    /// <summary>
    /// Real gathering tools, best-first, for belt slot 5 - kept separate
    /// from WeaponPriority (which is real weapons only now - see its own
    /// doc comment) so a pickaxe/hatchet chosen here doesn't duplicate
    /// whatever OrganizeBelt already placed as the primary weapon/tool.
    /// Comprehensive as of the same 2026-08-09 full Category:"Tool" scan -
    /// power tools (jackhammer, chainsaw) ranked first as genuinely
    /// better at their job than a hand tool, then the base pickaxe/
    /// hatchet, then every real skinned/material variant of each (stone,
    /// concrete/salvaged, diver, lumberjack, frontier - all real, separate
    /// shortnames, not reskins of the same item), then icepick.salvaged.
    /// No dedicated per-task tool selection yet (which specific resource a
    /// survivor is actively trying to gather isn't tracked anywhere in
    /// this project - see [[project-livingrust-roadmap]]'s Current-task
    /// entry) - pickaxes ranked marginally above hatchets as the more
    /// generally useful of the two (stone/sulfur/metal ore, Lucas's own
    /// examples) until real task-awareness exists to actually swap this
    /// per-job.
    /// </summary>
    private static readonly string[] GatherToolPriority =
    {
        "jackhammer",
        "chainsaw",
        "pickaxe",
        "stone.pickaxe",
        "concretepickaxe",
        "diverpickaxe",
        "lumberjack.pickaxe",
        "hatchet",
        "stonehatchet",
        "concretehatchet",
        "diverhatchet",
        "lumberjack.hatchet",
        "frontier_hatchet",
        "axe.salvaged",
        "icepick.salvaged",
    };

    private const string MedicalSyringeShortname = "syringe.medical";
    private const string BandageShortname = "bandage";

    // Belt layout, per Lucas's explicit spec (2026-08-09): slot 1 = best
    // weapon overall ("priority is just best weapon at the time"), or the
    // best tool instead if the survivor genuinely owns zero real weapons,
    // not even a bow; slot 2 = the best "offsider" weapon (a different
    // category from slot 1 - see AutomaticWeaponShortnames); slot 3 =
    // medical; slot 4 = bandages; slot 5 = a gathering tool; slot 6 = any
    // of the above, order doesn't matter (plain overflow).
    private const int BeltWeaponSlot = 0;
    private const int BeltOffsiderSlot = 1;
    private const int BeltMedicalSlot = 2;
    private const int BeltBandageSlot = 3;
    private const int BeltGatherToolSlot = 4;
    private static readonly int[] BeltOverflowSlots = { 5 };

    /// <summary>
    /// Checks every WeaponPriority entry against the real item database at
    /// startup - same reasoning, same failure mode this guards against, as
    /// ValidateMeleeToolPriority's own doc comment.
    /// </summary>
    private void ValidateWeaponPriority()
    {
        List<string> unresolved = new();

        foreach (string shortname in WeaponPriority)
        {
            if (ItemManager.FindItemDefinition(shortname) == null)
            {
                unresolved.Add(shortname);
            }
        }

        if (unresolved.Count > 0)
        {
            Puts($"WARNING: WeaponPriority contains {unresolved.Count} shortname(s) that don't resolve to any real item - OrganizeBelt will never recognize these: {string.Join(", ", unresolved)}.");
        }
        else
        {
            Puts($"WeaponPriority validated - all {WeaponPriority.Length} shortnames resolve to real items.");
        }
    }

    /// <summary>
    /// Arranges the survivor's belt the way a real player deliberately
    /// would, per Lucas's explicit spec (2026-08-09): slot 1 = the single
    /// best weapon owned overall ("priority is just best weapon at the
    /// time"), or the best gathering tool instead if the survivor
    /// genuinely owns zero real weapons, not even a bow; slot 2 = the
    /// best "offsider" - a weapon from a different category than slot 1
    /// (if slot 1 is a fully-automatic weapon, the offsider is the best
    /// NON-automatic one - a sniper, pistol, or shotgun; his own named
    /// example: "m249 first slot... l96 sniper rifle in second slot as
    /// an offsider"); slot 3 = medical, and slot 4 = bandages - both now a
    /// real priority cascade rather than a single fixed item each, see
    /// OrganizeMedicalAndExplosiveSlots' own doc comment for the full
    /// F1-grenade/syringe/large-medkit/bandage rules; slot 5 = a gathering
    /// tool; slot 6 = any of the above, order doesn't matter (plain
    /// overflow). Slots are 0-indexed here (BeltWeaponSlot=0
    /// is the game's slot 1, etc.) Every placement uses Item.MoveToContainer
    /// with allowSwap:true, the same real mechanism a client drag-and-drop
    /// uses - whatever was already sitting in the target slot gets swapped
    /// elsewhere rather than needing to be manually evacuated first.
    ///
    /// Slot 1 is now a ONE-TIME assignment, not continuously re-evaluated
    /// (2026-08-16, Lucas's own explicit request: "the main weapon (1st
    /// slot) never gets replaced with any other weapon... regardless of
    /// gear score, that way all looted items just go straight to the
    /// inventory" - this, plus EquipBestWeaponForDisplay's identical
    /// change, is also the real fix for repeated live reports of a bot's
    /// held weapon visually going invisible: both methods being called
    /// after almost every loot pickup, each independently re-picking
    /// "best," was producing exactly the rapid MoveToContainer/
    /// UpdateActiveItem churn this project's own OrganizeBelt comment
    /// already identified as the root cause of that glitch). Once
    /// something real already occupies slot 1, it's kept there
    /// permanently regardless of what gets looted afterward - only a
    /// genuinely empty slot 1 (nothing real ever equipped yet) still picks
    /// via WeaponPriority/GatherToolPriority. Real combat can still swap
    /// the equipped weapon out from under this when the primary runs dry
    /// (TryEquipBestArmedWeapon, LivingRust.Combat.cs) - that's a
    /// deliberate, separate exception Lucas explicitly asked to keep for
    /// survivability, not something this method fights.
    /// </summary>
    private void OrganizeBelt(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        // Snapshot of the ACTIVE item's own slot before any of this
        // method's own MoveToContainer calls run - see the final
        // re-confirm step at the bottom for why this is captured here
        // rather than just re-fetched fresh at the end.
        Item activeItemBefore = npc.GetActiveItem();
        int? activeItemPositionBefore = activeItemBefore?.position;

        List<Item> allItems = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .ToList();

        Item currentSlotOne = npc.inventory.containerBelt.itemList.FirstOrDefault(item => item.position == BeltWeaponSlot);
        bool slotOneAlreadyCommitted = currentSlotOne != null
            && (Array.IndexOf(WeaponPriority, currentSlotOne.info.shortname) >= 0 || Array.IndexOf(GatherToolPriority, currentSlotOne.info.shortname) >= 0);

        Item primaryWeapon;
        Item primaryTool;
        Item primary;

        if (slotOneAlreadyCommitted)
        {
            primary = currentSlotOne;
            primaryWeapon = Array.IndexOf(WeaponPriority, currentSlotOne.info.shortname) >= 0 ? currentSlotOne : null;
            primaryTool = primaryWeapon == null ? currentSlotOne : null;
        }
        else
        {
            primaryWeapon = FindBestByPriority(allItems, WeaponPriority, exclude: null);

            // No real weapon at all, not even a bow - the best tool takes the
            // primary slot instead. Lucas's own explicit rule.
            primaryTool = primaryWeapon == null ? FindBestByPriority(allItems, GatherToolPriority, exclude: null) : null;

            primary = primaryWeapon ?? primaryTool;

            // Checks parent AND position, not position alone (2026-08-16 -
            // see EquipBestWeaponForDisplay's identical fix/doc comment for
            // the real live bug this same mistake caused there: position
            // is only meaningful within an item's own current container, so
            // a main-inventory item at position 0 isn't "already in belt
            // slot 1" just because the numbers match).
            bool primaryAlreadyPlaced = primary != null && primary.parent == npc.inventory.containerBelt && primary.position == BeltWeaponSlot;

            if (primary != null && !primaryAlreadyPlaced && !primary.MoveToContainer(npc.inventory.containerBelt, BeltWeaponSlot))
            {
                // Logged rather than silently ignored - a live report of the
                // belt ending up nothing like this method intends (rock still
                // in slot 1, the actual best weapon elsewhere) needs real
                // evidence of WHERE this breaks down rather than another guess.
                Puts($"WARNING: '{survivor.Character.Alias}' - couldn't move '{primary.info.shortname}' into belt slot {BeltWeaponSlot + 1} (already at position {primary.position}, parent {(primary.parent == npc.inventory.containerBelt ? "belt" : primary.parent == npc.inventory.containerMain ? "main" : "other")}).");
            }
        }

        // Offsider only applies when slot 1 is a genuine weapon - a
        // fallback primary tool has no "different category" counterpart.
        if (primaryWeapon != null)
        {
            Item offsider = FindBestOffsider(allItems, primaryWeapon);

            if (offsider != null && offsider.position != BeltOffsiderSlot && !offsider.MoveToContainer(npc.inventory.containerBelt, BeltOffsiderSlot))
            {
                Puts($"WARNING: '{survivor.Character.Alias}' - couldn't move '{offsider.info.shortname}' into belt slot {BeltOffsiderSlot + 1} (already at position {offsider.position}, parent {(offsider.parent == npc.inventory.containerBelt ? "belt" : offsider.parent == npc.inventory.containerMain ? "main" : "other")}).");
            }
        }

        OrganizeMedicalAndExplosiveSlots(npc);

        // Excludes primaryTool specifically (not primaryWeapon, which
        // could never match GatherToolPriority anyway now that
        // WeaponPriority is real weapons only) - covers the "owns two
        // tools" case, where one became the primary-slot fallback and the
        // other still gets its own dedicated gather-tool slot.
        Item gatherTool = FindBestByPriority(allItems, GatherToolPriority, exclude: primaryTool);

        if (gatherTool != null && gatherTool.position != BeltGatherToolSlot && !gatherTool.MoveToContainer(npc.inventory.containerBelt, BeltGatherToolSlot))
        {
            Puts($"WARNING: '{survivor.Character.Alias}' - couldn't move '{gatherTool.info.shortname}' into belt slot {BeltGatherToolSlot + 1} (already at position {gatherTool.position}, parent {(gatherTool.parent == npc.inventory.containerBelt ? "belt" : gatherTool.parent == npc.inventory.containerMain ? "main" : "other")}).");
        }

        DeclutterBeltOfWearables(npc);

        FillOverflowBeltSlots(npc);

        // Re-confirms whatever's actually active with the client, but ONLY
        // if this method's own MoveToContainer calls above actually moved
        // the active item's own slot (2026-08-16 - originally this fired
        // UNCONDITIONALLY every single call, on the theory that belt
        // repositioning "can leave the client's held-item VISUAL out of
        // sync... a bot visibly swinging an invisible hand." Real live
        // report after that fix shipped: the glitch was still happening,
        // specifically and reliably right when looting a corpse during the
        // ghost route - and corpse loot from a scientist is very often a
        // Weapon/Ammunition-category item, meaning PerformReorganizationCheck
        // (and this unconditional resend) was firing on almost every single
        // corpse. With the weapon slot now pinned (2026-08-16, same
        // session) the active item's slot essentially never changes here
        // anymore in the common case, so forcing a resend regardless was
        // itself turning into the same repeated-churn problem this was
        // meant to fix, just from a different trigger. Now this only
        // forces a refresh on the actual rare case that matters - the
        // active item's slot genuinely moved during this call.
        Item currentActive = npc.GetActiveItem();

        if (currentActive != null && currentActive.position != activeItemPositionBefore)
        {
            // Temporary diagnostic (2026-08-16) - real live reports of the
            // invisible-weapon glitch kept recurring even after this block
            // was gated to only fire on an actual slot change, with no way
            // to confirm from the log alone whether THIS is still firing or
            // something else entirely is now the cause. Pin down which
            // before guessing at another fix.
            Puts($"weapon-refresh-diag: '{survivor.Character.Alias}' active item '{currentActive.info.shortname}' moved from slot {activeItemPositionBefore} to slot {currentActive.position} during OrganizeBelt - forcing a held-entity refresh.");

            npc.UpdateActiveItem(currentActive.uid);
            ForceRefreshHeldEntity(npc);
        }
    }

    private static Item FindBestByPriority(List<Item> items, string[] priority, Item exclude)
    {
        Item best = null;
        int bestRank = int.MaxValue;

        foreach (Item item in items)
        {
            if (item == exclude)
            {
                continue;
            }

            int rank = Array.IndexOf(priority, item.info.shortname);

            if (rank >= 0 && rank < bestRank)
            {
                bestRank = rank;
                best = item;
            }
        }

        return best;
    }

    /// <summary>
    /// Best belt-slot-2 "offsider" for primaryWeapon - a real weapon from
    /// a different category, per Lucas's own explicit examples: an
    /// automatic primary (see AutomaticWeaponShortnames) pairs with the
    /// best NON-automatic weapon (sniper, pistol, shotgun); anything else
    /// primary just pairs with the next-best weapon overall (which, since
    /// there's no second copy of a non-automatic category to prefer, is
    /// already the closest real "offsider" available). Falls back to the
    /// next-best weapon overall if literally every other owned weapon is
    /// also automatic (e.g. two SMGs and nothing else) - Lucas's rule is
    /// "predominantly offsider weapons" go in slot 2, not "slot 2 must be
    /// empty if no true offsider exists."
    /// </summary>
    private static Item FindBestOffsider(List<Item> items, Item primaryWeapon)
    {
        bool primaryIsAutomatic = Array.IndexOf(AutomaticWeaponShortnames, primaryWeapon.info.shortname) >= 0;

        if (primaryIsAutomatic)
        {
            Item nonAutomaticOffsider = items
                .Where(item => item != primaryWeapon
                    && Array.IndexOf(WeaponPriority, item.info.shortname) >= 0
                    && Array.IndexOf(AutomaticWeaponShortnames, item.info.shortname) < 0)
                .OrderBy(item => Array.IndexOf(WeaponPriority, item.info.shortname))
                .FirstOrDefault();

            if (nonAutomaticOffsider != null)
            {
                return nonAutomaticOffsider;
            }
        }

        return items
            .Where(item => item != primaryWeapon && Array.IndexOf(WeaponPriority, item.info.shortname) >= 0)
            .OrderBy(item => Array.IndexOf(WeaponPriority, item.info.shortname))
            .FirstOrDefault();
    }

    private const string F1GrenadeShortname = "grenade.f1";
    private const string LargeMedkitShortname = "largemedkit";

    /// <summary>
    /// Fills BeltMedicalSlot and BeltBandageSlot together, per Lucas's
    /// explicit priority-cascade spec (2026-08-09):
    ///
    /// BeltMedicalSlot ("slot 3"): F1 grenades - and ONLY F1 grenades, no
    /// other thrown explosive - override syringes here if the survivor
    /// owns any; syringes take it if no F1; bandages take it as the final
    /// fallback once BOTH F1 and syringes are completely gone ("syringes
    /// out? completely out? bandages take over medical slots").
    ///
    /// BeltBandageSlot ("slot 4"): a syringe displaced by F1 taking the
    /// medical slot claims this one instead, ahead of a large medkit -
    /// Lucas's own explicit ranking, confirmed twice: first "I prioritise
    /// med syringes over bandages as they provide better healing
    /// effects," then confirmed again specifically against large medkits
    /// too ("medical syringe trumps large medkit" - instant heal plus a
    /// passive regen-over-time effect a medkit doesn't have, and a medkit
    /// costs meaningfully more to craft for a smaller instant boost, so
    /// its real overall priority is lower). Large medkit only wins this
    /// slot over a plain bandage, never over a syringe.
    ///
    /// Every check is against CURRENT ownership, re-evaluated fresh on
    /// every call rather than tracked with separate state - exactly what
    /// makes "once depleted, the original priority takes priority again"
    /// true for free: the moment Rust's own item system removes a
    /// fully-used stack, the next OrganizeBelt pass simply won't find it
    /// anymore and the cascade naturally falls through to the next tier.
    /// </summary>
    private void OrganizeMedicalAndExplosiveSlots(BasePlayer npc)
    {
        List<Item> allItems = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .ToList();

        Item grenade = allItems.FirstOrDefault(item => item.info.shortname == F1GrenadeShortname);
        Item syringe = allItems.FirstOrDefault(item => item.info.shortname == MedicalSyringeShortname);
        Item largeMedkit = allItems.FirstOrDefault(item => item.info.shortname == LargeMedkitShortname);
        Item bandage = allItems.FirstOrDefault(item => item.info.shortname == BandageShortname);

        Item medicalSlotItem = grenade ?? syringe ?? bandage;

        Item bandageSlotItem = medicalSlotItem == grenade && syringe != null
            ? syringe
            : largeMedkit ?? (bandage != medicalSlotItem ? bandage : null);

        if (medicalSlotItem != null && medicalSlotItem.position != BeltMedicalSlot)
        {
            medicalSlotItem.MoveToContainer(npc.inventory.containerBelt, BeltMedicalSlot);
        }

        if (bandageSlotItem != null && bandageSlotItem.position != BeltBandageSlot)
        {
            bandageSlotItem.MoveToContainer(npc.inventory.containerBelt, BeltBandageSlot);
        }
    }

    /// <summary>
    /// Moves any wearable (armor/clothing) still sitting in a belt slot
    /// back into main inventory - the belt's only intended occupants are
    /// the weapon/medical/gather-tool roles above (plus spare medical
    /// overflow). A live report caught real armor parked in a belt slot,
    /// put there by Rust's own raw loot-transfer placement before this
    /// method ever ran (PlayerInventory.GiveItem can land an item on
    /// either container depending on which had space at that exact
    /// moment). EvaluateAndUpgradeArmor - which runs earlier in
    /// OnLootObtained, and as of the same fix now also scans the belt,
    /// not just main - already had its own chance to wear anything here
    /// if it was a real upgrade; anything still sitting here afterward
    /// wasn't worth wearing right now, and belongs in main inventory as
    /// spare/backup material, not occupying a belt slot reserved for
    /// something else.
    /// </summary>
    private void DeclutterBeltOfWearables(BasePlayer npc)
    {
        List<Item> wearablesOnBelt = npc.inventory.containerBelt.itemList
            .Where(item => item.info.GetComponent<ItemModWearable>() != null)
            .ToList();

        foreach (Item item in wearablesOnBelt)
        {
            item.MoveToContainer(npc.inventory.containerMain);
        }
    }

    /// <summary>
    /// Backs up any belt slots OrganizeBelt didn't already claim
    /// (BeltOverflowSlots) with spare medical stacks pulled from main
    /// inventory - Lucas's own "if the slots are free" framing, and
    /// medical specifically since it's the one item type worth having
    /// multiple ready stacks of rather than just one. Only ever touches a
    /// slot that's genuinely empty - never displaces whatever a survivor
    /// might already be carrying there.
    /// </summary>
    private void FillOverflowBeltSlots(BasePlayer npc)
    {
        foreach (int slot in BeltOverflowSlots)
        {
            if (npc.inventory.containerBelt.GetSlot(slot) != null)
            {
                continue;
            }

            Item extraSyringe = npc.inventory.containerMain.itemList
                .FirstOrDefault(item => item.info.shortname == MedicalSyringeShortname);

            if (extraSyringe == null)
            {
                break;
            }

            extraSyringe.MoveToContainer(npc.inventory.containerBelt, slot, allowStack: true, ignoreStackLimit: false, sourcePlayer: null, allowSwap: false);
        }
    }

    /// <summary>
    /// Main-inventory neatness pass - Lucas's own framing: "not a massive
    /// implementation", but grouped by real category now, not just by
    /// exact shortname - medical items sit together, weapons sit
    /// together, resources sit together, rather than only guaranteeing
    /// adjacency for exact duplicate stacks (the original version sorted
    /// by shortname alone, which put e.g. "bandage" and "syringe.medical"
    /// nowhere near each other despite both being medical). Uses Rust's
    /// own real ItemCategory (Weapon/Construction/Items/Resources/Attire/
    /// Tool/Medical/Food/Ammunition/Traps/Misc/...) as the grouping key -
    /// the same real category ConsumeFoodImmediately's ItemCategory.Food
    /// filter already relies on - rather than inventing a separate
    /// ad-hoc taxonomy to build and maintain. Sorted by shortname within
    /// each category, same as before, so exact-duplicate stacks still
    /// land adjacent to each other too. Uses the current position of each
    /// item (not a stale precomputed one) since MoveToContainer's
    /// allowSwap:true can relocate an item this loop hasn't reached yet.
    /// </summary>
    private void TidyMainInventory(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        List<Item> sorted = npc.inventory.containerMain.itemList
            .OrderBy(item => (int)item.info.category)
            .ThenBy(item => item.info.shortname, StringComparer.Ordinal)
            .ToList();

        for (int targetPosition = 0; targetPosition < sorted.Count; targetPosition++)
        {
            Item item = sorted[targetPosition];

            if (item.position != targetPosition)
            {
                item.MoveToContainer(npc.inventory.containerMain, targetPosition);
            }
        }
    }

    /// <summary>
    /// Safety cap on how many times ConsumeFoodImmediately will call
    /// ItemModConsume.DoAction on a single item stack - guards against a
    /// runaway loop if CanDoAction somehow kept returning true without
    /// DoAction ever reducing item.amount (shouldn't happen given the real
    /// DoAction always calls item.UseItem, but cheap insurance against a
    /// stuck survivor endlessly "eating" the same stack).
    /// </summary>
    private const int MaxFoodConsumeActionsPerItem = 50;

    /// <summary>
    /// Hunger/thirst is explicitly out of scope as its own system for now
    /// (Lucas's own framing: "it really isn't a MASSIVE component of rust
    /// gameplay, it is just a side thing") - rather than build metabolism
    /// tracking, food just gets eaten the instant it enters inventory,
    /// using the exact real consume action a client's "Consume" button
    /// ultimately triggers (BasePlayer's own SV_Drink RPC handler follows
    /// this identical CanDoAction/DoAction pattern for water, confirmed
    /// via decompiling Assembly-CSharp.dll) - DoAction itself already
    /// applies real metabolism effects (calories/hydration/health) and
    /// consumes the item via Item.UseItem, so this is genuine eating, not
    /// a synthetic shortcut.
    ///
    /// Filtered to ItemCategory.Food specifically (a real, separate
    /// category from Medical - confirmed via decompiling ItemCategory) so
    /// this never touches medical syringes/bandages, which also use
    /// ItemModConsume but are deliberately kept as reserved belt stock by
    /// OrganizeBelt instead of being eaten on sight.
    ///
    /// Looped per item rather than one DoAction call, since DoAction only
    /// consumes up to the item's own amountToConsume (usually 1) per call -
    /// a stack of several cooked meat needs several calls to actually
    /// clear the whole stack, same as a real player pressing Consume
    /// repeatedly. CanDoAction naturally stops this once
    /// player.metabolism.CanConsume() says the survivor is already full,
    /// so a stack that's actually more than currently needed just
    /// partially consumes rather than force-feeding the rest.
    /// </summary>
    private void ConsumeFoodImmediately(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        // Water bottles are also real ItemCategory.Food (confirmed via
        // its own bundled item JSON) but excluded here and handled
        // separately by DrinkWaterBottles - see that method's own doc
        // comment for why a reusable container needs different handling
        // after drinking than a food stack does.
        //
        // Worms excluded too (2026-08-28, Lucas's own live report: bots
        // kept eating one right after every single hemp-bush gather) -
        // worms are real incidental ground clutter picked up alongside
        // hemp/grass, not something a survivor deliberately went and
        // foraged for, so a fresh one getting auto-eaten on nearly every
        // hemp cycle read as repetitive/undesirable rather than the
        // "genuinely hungry, ate what's on hand" behaviour this system is
        // meant to model.
        List<Item> foodItems = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => item.info.category == ItemCategory.Food
                && item.info.shortname != WaterBottleShortname
                && item.info.shortname != WormShortname)
            .ToList();

        foreach (Item item in foodItems)
        {
            int actions = ConsumeViaItemModConsume(item, npc);

            if (actions > 0)
            {
                VerbosePuts($"'{survivor.Character.Alias}' ate '{item.info.shortname}' ({actions}x) on pickup.");
            }
        }
    }

    private const string WaterBottleShortname = "smallwaterbottle";
    private const string WormShortname = "worm";

    /// <summary>
    /// Shared real "press Consume" loop - ItemModConsume.CanDoAction/
    /// DoAction, the same mechanism BasePlayer's own SV_Drink RPC handler
    /// uses (confirmed via decompiling Assembly-CSharp.dll). Looped since
    /// DoAction only consumes up to the item's own amountToConsume
    /// (usually 1) per call, not the whole stack/amount at once - a real
    /// player would press Consume repeatedly too. CanDoAction naturally
    /// stops this once player.metabolism.CanConsume() says the survivor's
    /// already full, so this can't force-feed past that point. Returns
    /// how many times DoAction actually fired, 0 if item has no
    /// ItemModConsume component at all.
    /// </summary>
    private int ConsumeViaItemModConsume(Item item, BasePlayer npc)
    {
        ItemModConsume consume = item.info.GetComponent<ItemModConsume>();

        if (consume == null)
        {
            return 0;
        }

        int actions = 0;

        while (item.amount > 0 && actions < MaxFoodConsumeActionsPerItem && consume.CanDoAction(item, npc))
        {
            consume.DoAction(item, npc);
            actions++;
        }

        return actions;
    }

    /// <summary>
    /// Water bottles (real shortname "smallwaterbottle") are deliberately
    /// excluded from ConsumeFoodImmediately's generic loop and handled
    /// here instead - Lucas's explicit request specifically wanted the
    /// drink animation, which ConsumeViaItemModConsume's DoAction already
    /// fires for free (a real SignalBroadcast(Signal.Gesture, eatGesture)
    /// internally, confirmed via decompiling ItemModConsume), so nothing
    /// extra was needed there. What food doesn't need is the step after:
    /// a real bottle is a reusable container in vanilla Rust (refillable
    /// via right-click), not consumed/removed the way a food stack unit
    /// is, so it can still exist - now empty - after drinking. The bot
    /// has no use for an empty bottle it can never refill on its own, so
    /// it gets dropped for real, same Item.Drop mechanism
    /// DropUnneededLightSource already uses for the torch, not a delete.
    /// </summary>
    private const string WaterJugShortname = "waterjug";

    private void DrinkWaterBottles(Survivor survivor, BasePlayer npc)
    {
        List<Item> bottles = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => item.info.shortname == WaterBottleShortname)
            .ToList();

        // Lucas's explicit request (2026-08-11): owning a real water jug -
        // strictly bigger capacity, same refillable-container role a bottle
        // fills - makes every small bottle pure inventory clutter, full or
        // empty, so they all get dropped outright rather than drunk-then-
        // dropped one at a time. Checked fresh every call (not a one-time
        // rule), so a jug picked up later still cleans out whatever bottles
        // were accumulated before it.
        bool ownsJug = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Any(item => item.info.shortname == WaterJugShortname);

        if (ownsJug)
        {
            foreach (Item bottle in bottles)
            {
                Vector3 jugDropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
                Vector3 jugDropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

                bottle.Drop(jugDropPosition, jugDropVelocity);

                VerbosePuts($"'{survivor.Character.Alias}' dropped a water bottle - already carrying a water jug.");
            }

            return;
        }

        foreach (Item bottle in bottles)
        {
            int actions = ConsumeViaItemModConsume(bottle, npc);

            if (actions == 0)
            {
                continue;
            }

            // Still present (and presumably now empty) means it's the
            // real refillable-container behaviour, not a single-use item
            // Rust's own UseItem already removed - only drop what's
            // actually still there.
            Item stillHeld = npc.inventory.FindItemByUID(bottle.uid);

            if (stillHeld != null)
            {
                Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
                Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

                stillHeld.Drop(dropPosition, dropVelocity);
            }

            VerbosePuts($"'{survivor.Character.Alias}' drank a water bottle ({actions}x) and dropped it.");
        }
    }

    private const string RadiationPillsShortname = "antiradpills";

    /// <summary>
    /// Radiation pills (real shortname "antiradpills") get taken the
    /// instant they enter inventory, same immediate-consumption shape as
    /// ConsumeFoodImmediately/DrinkWaterBottles and the same explicit
    /// reasoning: no real metabolism/radiation-exposure tracking exists
    /// (see the Needs roadmap entry), so there's nothing to gain by
    /// hoarding a stack instead of taking it right away, and taking it
    /// immediately reads as a survivor actually using what it finds
    /// rather than just carrying medicine around forever. Reuses
    /// ConsumeViaItemModConsume, the same real "press Consume"
    /// ItemModConsume.CanDoAction/DoAction loop food/water already use -
    /// genuine consumption, not a synthetic shortcut.
    /// </summary>
    private void ConsumeRadiationPillsImmediately(Survivor survivor, BasePlayer npc)
    {
        List<Item> pillStacks = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => item.info.shortname == RadiationPillsShortname)
            .ToList();

        foreach (Item item in pillStacks)
        {
            int actions = ConsumeViaItemModConsume(item, npc);

            if (actions > 0)
            {
                VerbosePuts($"'{survivor.Character.Alias}' took {actions}x radiation pills on pickup.");
            }
        }
    }

    /// <summary>
    /// How many loot attempts in a row (walk-unreachable or no-line-of-
    /// sight) before the whole surrounding area gets treated as poisoned
    /// rather than continuing to individually try/skip whatever's left in
    /// it. Added after a live report: a survivor camped near junkpile_j
    /// thrashed through a long chain of individually-doomed candidates
    /// (junkpile_j's own van/gravel/terrain scatter obstructing line of
    /// sight and paths to nearby, otherwise-unrelated containers) before
    /// finally exhausting the search radius. Excluding junkpile_j's own
    /// containers (IsInJunkpileJVan) didn't help, since the survivor
    /// wasn't stuck on junkpile_j's loot - it was stuck near junkpile_j's
    /// obstructive geometry while trying to reach something else nearby.
    /// </summary>
    private const int ConsecutiveFailuresBeforeAvoidingArea = 4;

    /// <summary>
    /// Radius poisoned around the survivor's position once
    /// ConsecutiveFailuresBeforeAvoidingArea is hit. Originally 20m -
    /// tuned to clear junkpile_j's own footprint (its "Prevent Building"
    /// trigger alone is a 10m-radius sphere per a live /lr.debug.look
    /// scan) plus margin - but that turned out to over-exclude in denser
    /// container layouts (e.g. deliberately scattered test crates only
    /// 5-10m apart), catching legitimate nearby unrelated containers in a
    /// single poisoning event. Settled on 12m: enough margin over
    /// junkpile_j's 10m footprint to still clear it properly, while
    /// staying far more surgical than the original 20m for dense
    /// layouts. This only ever filters which container gets picked as
    /// the NEXT candidate, never blocks actually walking/pathing through
    /// a poisoned area to reach something beyond it, so a smaller radius
    /// is lower-risk, not higher. The real failure mode this doesn't
    /// protect against - an entire room becoming unreachable because its
    /// one doorway is genuinely unwalkable, not because of poisoning - is
    /// a separate, already-tracked navigation issue (see the
    /// awning/doorway local-stepping misjudgment), not something this
    /// radius controls either way.
    /// </summary>
    private const float PoisonedZoneRadius = 12f;

    /// <summary>
    /// How long a poisoned zone stays in effect - deliberately time-
    /// limited rather than permanent for the rest of the task run (user's
    /// own correction): a monument or roadside area a survivor briefly
    /// struggled in isn't necessarily bad forever, and permanently
    /// avoiding it for the whole run risked skipping perfectly legitimate
    /// loot if the survivor ever wandered back that way later in the same
    /// run.
    /// </summary>
    private const float PoisonedZoneDuration = 25f;

    /// <summary>
    /// A crate/barrel-type container more than this far ABOVE the
    /// survivor's current standing height gets excluded from search
    /// entirely - added 2026-08-15 after a live trace report
    /// (9376SilentVulture, powerplant): a crate sitting ~5m above the
    /// survivor on a platform with no real navmesh connection got
    /// targeted, walked toward, failed (genuine PathInvalid - confirmed
    /// via the log, "destination 1.61m from nearest navmesh point...
    /// origin 0.35m from nearest navmesh point," a real ~7m vertical gap
    /// between the two), poisoned for PoisonedZoneDuration (25s), then
    /// re-targeted again the moment that expired - a slow-motion infinite
    /// loop, not a one-off stuck episode (the existing poison IS real and
    /// DOES work, it just isn't durable enough to survive a genuinely
    /// permanent, physically unreachable case - only a temporary "give
    /// this a while" pause). Lucas's own proposed fix: a simple, cheap
    /// pre-filter - most genuinely elevated loot that a bot actually
    /// should reach sits inside a structure the bot would already be
    /// standing at a similar height within (having climbed real stairs to
    /// get there first), so a bot down at ground level looking at
    /// something 3m+ above it is, in practice, looking at exactly this
    /// class of unreachable platform loot far more often than a
    /// legitimately climbable case.
    ///
    /// Deliberately scoped to crate_*/barrel_* containers ONLY (Lucas's
    /// own explicit correction, same day) - NOT corpses, dropped bags,
    /// standalone dropped items, or collectibles, which were pulled back
    /// out of this check entirely rather than left in. Those other loot
    /// types can have their own legitimate reasons to sit elevated (a
    /// corpse on a rooftop where someone actually died, say) that a crate
    /// spawn point doesn't - "we can try figure out everything else later"
    /// was the explicit framing, so this stays narrow to the actually-
    /// reported case for now. A cheap heuristic, not a real navmesh-aware
    /// check - deliberately so, matching every other exclusion in this
    /// search (IsInPoisonedZone/IsNearJunkpileJVan/etc are all cheap
    /// heuristics too, not perfect verification).
    /// </summary>
    private const float LootVerticalReachLimit = 3f;

    private static bool IsUnreachableCrateOrBarrel(Vector3 npcPosition, StorageContainer candidate)
    {
        if ((candidate.transform.position.y - npcPosition.y) <= LootVerticalReachLimit)
        {
            return false;
        }

        string name = candidate.ShortPrefabName;

        return name.IndexOf("crate", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("barrel", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Same vertical pre-filter as IsUnreachableCrateOrBarrel, extended to
    /// dropped items (2026-08-15) - this is exactly the "everything else
    /// later" IsUnreachableCrateOrBarrel's own doc comment deferred, now
    /// backed by real live evidence: '3RaggedBuilder' at Abandoned
    /// Supermarket found and repeatedly re-targeted a real dropped item
    /// sitting ~3.9m up on/near a ceiling air-duct fixture
    /// (air_duct_crn_150x150) - genuinely unreachable ("step too high"),
    /// but the existing 25s local poison (PoisonAreaNow) isn't durable
    /// enough to survive a genuinely PERMANENT case, so the bot re-found
    /// and re-attempted the exact same item every cycle once the poison
    /// expired - a slow-motion infinite loop, matching Lucas's own live
    /// description ("run to the counter, get stuck, move to the other
    /// side, get stuck again"). Unlike crates/barrels this has no
    /// shortname restriction - a dropped item's shortname says nothing
    /// about whether it's sitting somewhere climbable, so the height check
    /// alone is the whole filter here.
    /// </summary>
    private static bool IsUnreachableDroppedItem(Vector3 npcPosition, Vector3 candidatePosition)
    {
        return (candidatePosition.y - npcPosition.y) > LootVerticalReachLimit;
    }

    /// <summary>
    /// A DIFFERENT, deliberately separate poisoning mechanism from
    /// PoisonedZones above (2026-08-15, live trace report) - that one is
    /// per-LootTaskState (a fresh, empty one every time StartLootForResourcesTask
    /// runs, including the one EndFlee kicks off right after a threat flee
    /// ends), so anything recorded there would be instantly forgotten the
    /// moment the survivor resumed looting - no help at all for the actual
    /// problem. Live trace evidence: an unarmed survivor near
    /// nuclear_missile_silo got shot by a hostile scientist, fled
    /// (StartFleeingFromThreat, correct), then EndFlee resumed looting,
    /// which walked it straight back into the same scientist's engagement
    /// range and got it shot again - repeated 3+ times for the same
    /// survivor. This is keyed per-survivor (Character.Id) instead, at the
    /// PLUGIN level, so it survives across separate loot-task instances the
    /// way an actual memory of "I got hurt here" should. Deliberately NOT
    /// reusing PoisonAreaNow itself - that method also registers a
    /// container retry and feeds the permanent, monument-relative avoid-
    /// zone system, both of which are about genuinely bad NAVIGATION
    /// (unreachable geometry), not "a hostile NPC is here right now" -
    /// conflating the two would permanently blacklist a perfectly walkable,
    /// perfectly lootable spot just because a scientist happened to be
    /// guarding it during one specific encounter.
    /// </summary>
    private const float ThreatFleePoisonRadius = 20f;

    /// <summary>
    /// How long a threat-flee poison zone lasts - Lucas's own explicit
    /// number ("maybe 30 seconds at a 20m radius"). Long enough that the
    /// SAME loot-task resume doesn't immediately walk back into it, short
    /// enough that a survivor doesn't permanently write off a real loot-
    /// dense spot (a missile silo's own containers) just because a guard
    /// happened to be nearby once.
    /// </summary>
    private const float ThreatFleePoisonDuration = 30f;

    private readonly Dictionary<Guid, List<(Vector3 Center, float Radius, float ExpiresAt)>> _threatFleeZones = new();

    /// <summary>
    /// Called once from StartFleeingFromThreat (LivingRust.Combat.cs) the
    /// moment a flee actually starts - marks roughly where the survivor
    /// was when it got attacked as temporarily worth avoiding, so the loot
    /// search that resumes once the flee ends doesn't immediately walk it
    /// back into the same danger.
    /// </summary>
    private void PoisonAreaFromThreatFlee(Survivor survivor, Vector3 position, float radius = ThreatFleePoisonRadius, float duration = ThreatFleePoisonDuration)
    {
        Guid characterId = survivor.Character.Id;

        if (!_threatFleeZones.TryGetValue(characterId, out List<(Vector3 Center, float Radius, float ExpiresAt)> zones))
        {
            zones = new List<(Vector3, float, float)>();
            _threatFleeZones[characterId] = zones;
        }

        float expiresAt = UnityEngine.Time.realtimeSinceStartup + duration;
        zones.Add((position, radius, expiresAt));

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' will avoid the area around {position} ({radius:F0}m) for the next {duration:F0}s after fleeing.");
    }

    /// <summary>
    /// Lazily expires stale entries on read (same pattern IsInPoisonedZone
    /// already uses) rather than needing a separate cleanup timer - this
    /// list per survivor is small and short-lived, not worth a dedicated
    /// tick.
    /// </summary>
    /// <summary>
    /// Real ocean-level safety filter (2026-08-28, Lucas's own explicit
    /// request after a live incident: a survivor's approach-point navmesh
    /// snap (SnapApproachPointToNavMesh, this file - a real, separate,
    /// still-open bug) landed on a cave/tunnel navmesh layer at Y=-40 while
    /// it was just trying to reach an ordinary surface tree, phased down
    /// into it, and was killed there by a real Tunnel Dweller NPC before
    /// ever getting the chance to recover. Rather than (or in addition to)
    /// fixing that specific snap bug, this is a blanket safety net any
    /// loot/resource candidate now has to clear: real sea level in Rust is
    /// Y=0, so anything more than SafeLootDepthBelowSeaLevel (15m, Lucas's
    /// own figure) below that is either a genuine underwater wreck/lab
    /// area or - far more likely for anything this shallow-sounding a
    /// depth catches - a cave/tunnel system these overworld survivor bots
    /// were never meant to path into at all. Checked the exact same way
    /// IsInMonumentAvoidZone already is, at every candidate filter site
    /// that checks that.
    /// </summary>
    private const float SafeLootDepthBelowSeaLevel = -15f;

    private static bool IsBelowSafeLootDepth(Vector3 position)
    {
        return position.y < SafeLootDepthBelowSeaLevel;
    }

    private bool IsInThreatFleeZone(Guid characterId, Vector3 position)
    {
        if (!_threatFleeZones.TryGetValue(characterId, out List<(Vector3 Center, float Radius, float ExpiresAt)> zones))
        {
            return false;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;

        zones.RemoveAll(zone => zone.ExpiresAt <= now);

        foreach ((Vector3 center, float radius, float _) in zones)
        {
            if (Vector3.Distance(center, position) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Per-task-run scratch state threaded through the whole find-walk-
    /// loot chain - which containers are already handled, and which areas
    /// have proven repeatedly unreachable and should stop being
    /// considered for a while. Not persisted (see Character.CurrentTask's
    /// doc comment) - purely runtime, like PathFollower is for a single
    /// walk.
    /// </summary>
    private sealed class LootTaskState
    {
        public readonly HashSet<NetworkableId> Visited = new();
        public readonly List<(Vector3 Center, float Radius, float ExpiresAt)> PoisonedZones = new();
        public int ConsecutiveFailures;

        // See RetrySuccessesBeforeRevisit's own doc comment - a container
        // that failed once stays in Visited (still excluded) while it
        // counts down here, then gets removed from Visited (eligible
        // again) once it hits zero. AlreadyRetried caps this at exactly
        // one retry per container.
        public readonly Dictionary<NetworkableId, int> PendingRetry = new();
        public readonly HashSet<NetworkableId> AlreadyRetried = new();

        // See RoadFollowDistances' own doc comment - counts total
        // road-following hops across the WHOLE task's lifetime (not
        // per-escalation-call), so the bot can't chain an unbounded
        // number of them just because each individual hop happens to
        // land somewhere with nothing to loot.
        public int RoadFollowAttempts;

        // See EscalateSearchToMonumentZone's own doc comment - which zone
        // indices (into _monumentLootZones[CurrentMonumentZoneKey]) this
        // task has already walked to and searched from, so the same
        // monument doesn't get visited in an infinite loop once every
        // zone's been checked. Reset whenever the nearest monument changes
        // (a survivor that wanders from one monument's vicinity into
        // another's starts fresh against the new one).
        public string? CurrentMonumentZoneKey;
        public readonly HashSet<int> VisitedMonumentZoneIndices = new();

        // See GetMonumentDwellSeconds' own doc comment - the monument this
        // task has committed to actively working, and the real-time deadline
        // (UnityEngine.Time.realtimeSinceStartup-based) that commitment
        // holds until. Set once, the first time EscalateSearchToMonumentZone
        // starts checking a given monument's zones this task - not touched
        // again until either the deadline passes or the task moves to a
        // genuinely different monument.
        public string? CommittedMonumentName;
        public float CommittedMonumentDeadline;

        // See EscalateSearchToKnownMonument's own doc comment - caps THIS
        // task to at most one long cross-country trip toward a distant
        // known monument, rather than potentially chaining an unbounded
        // sequence of them if the first one's own zones also come up dry.
        public bool TraveledToDistantMonument;

        // Whichever container/corpse/bag THIS survivor currently has a
        // shared claim on (see _lootClaims' own doc comment) - null
        // whenever nothing's claimed right now. Only ever one at a time:
        // ContinueLootTask releases the previous claim (if any) the
        // instant it's called again, before picking a new target.
        public NetworkableId? ClaimedTargetId;

        // Which monument name(s) this task has already run an authored
        // ghost route through (see MonumentGhostRoutes/TryGetGhostRouteForMonument,
        // 2026-08-16) - caps it to one detour per monument per task, so
        // finishing the route and resuming ContinueLootTask nearby can't
        // immediately re-trigger the same detour in a loop.
        public readonly HashSet<string> GhostRouteVisitedMonuments = new();

        // Same one-attempt-per-monument-per-task cap as GhostRouteVisitedMonuments,
        // for TryStartCardPuzzleDetour (LivingRust.CardPuzzles.cs, 2026-08-17).
        public readonly HashSet<string> CardPuzzleVisitedMonuments = new();
    }

    /// <summary>
    /// How many OTHER containers must be successfully looted nearby before
    /// a container that previously failed (unreachable, no line of sight,
    /// too far) gets exactly one retry - user-requested, after a live test
    /// showed a survivor permanently give up on a container it could
    /// clearly see but not reach (behind a sandbag wall). By the time
    /// RetrySuccessesBeforeRevisit other containers are done, the survivor
    /// has likely moved to a meaningfully different physical position, so
    /// the retry naturally approaches from a different angle rather than
    /// immediately re-trying the exact same failed geometry. Only one
    /// retry ever, per container (AlreadyRetried) - if that also fails,
    /// it's skipped for good, same as before this existed.
    /// </summary>
    private const int RetrySuccessesBeforeRevisit = 3;

    /// <summary>
    /// Per-character active "breaking open a container" loop, mirroring
    /// _activeMovement's own per-character timer-tracking pattern in
    /// LivingRust.Commands.cs - lets an in-progress attack be cancelled
    /// (e.g. the survivor dies mid-swing) without leaving a dangling timer
    /// still ticking against a destroyed BasePlayer.
    /// </summary>
    private readonly Dictionary<Guid, Timer> _activeAttacks = new();

    /// <summary>
    /// Shared "someone's already heading to this" registry, keyed by the
    /// container/corpse/bag's own net ID, valued by when the claim expires.
    /// Fixes a real live-observed bug: two survivors spawned near each
    /// other independently pick the exact same nearest barrel (then the
    /// exact same next crate right after) since each bot's own
    /// LootTaskState.Visited only tracks ITS OWN history, nothing shared -
    /// confirmed via a cross-referenced tracemany trace showing two bots
    /// standing 0.02-0.06m apart for ~80s while both "looted" the same
    /// barrel then the same crate back to back. Lucas's own framing once
    /// this was diagnosed: a bot that finds its target already claimed
    /// should "give up and try for another barrel or container" - not
    /// abandon the whole task, just skip that one candidate, which is
    /// exactly what checking this registry in the search predicates
    /// (alongside the existing Visited/poisoned-zone exclusions) does
    /// naturally: the next-nearest unclaimed candidate gets picked instead.
    /// Deliberately NOT a literal "compare my distance to theirs" contest -
    /// the game loop is single-threaded, so claim-first-wins has no real
    /// race condition to resolve, and it's self-healing (TTL expiry below
    /// covers the case where a claim never gets explicitly released, e.g.
    /// a task restarting mid-claim after the old LootTaskState is
    /// discarded) without needing to track/compare other survivors'
    /// live distances anywhere.
    /// </summary>
    private readonly Dictionary<NetworkableId, float> _lootClaims = new();

    /// <summary>
    /// Shared "someone's already heading toward this road/trail point"
    /// registry, keyed by the claiming survivor's own Character ID (not
    /// the point itself - unlike loot targets, a road point isn't a
    /// stable entity to key off, so this is "the most recent destination
    /// each survivor claimed" instead, checked by proximity). Stopgap per
    /// Lucas's explicit request (2026-08-10), deliberately NOT meant to
    /// be the permanent design: without this, multiple bots that all run
    /// out of nearby loot near each other independently compute the same
    /// "follow the road N metres further" hop and converge on it
    /// together - same shape of problem the loot-target claim system
    /// (_lootClaims) already fixed for containers/corpses/bags, just for
    /// road-following destinations instead. Released unconditionally at
    /// the top of every ContinueLootTask cycle (mirrors ReleaseLootClaim),
    /// so no separate expiry/TTL is needed - a claim never outlives the
    /// cycle that made it.
    /// </summary>
    private readonly Dictionary<Guid, Vector3> _roadHopClaims = new();

    // How close another survivor's claimed road-hop destination needs to
    // be before this one is considered "contested" and gets nudged
    // further along the road instead.
    private const float RoadHopContentionRadius = 30f;

    // Lucas's own numbers - "the same road or path but just 50-100m
    // away" - how far along the road (in the same direction already
    // chosen) to nudge a contested destination.
    private const float RoadHopContentionOffsetMin = 50f;
    private const float RoadHopContentionOffsetMax = 100f;

    /// <summary>
    /// Whether position is close enough to another survivor's currently-
    /// claimed road-hop destination to count as contested - see
    /// _roadHopClaims' own doc comment.
    /// </summary>
    private bool IsRoadDestinationContested(Vector3 position, Guid selfCharacterId)
    {
        foreach (KeyValuePair<Guid, Vector3> claim in _roadHopClaims)
        {
            if (claim.Key != selfCharacterId && Vector3.Distance(position, claim.Value) <= RoadHopContentionRadius)
            {
                return true;
            }
        }

        return false;
    }

    // Generous upper bound on how long a real claim should ever legitimately
    // live - full StartWalkingWithRecovery escalation (wiggle, navmesh
    // nudge, emergency teleport) plus StartAttackingContainerWithReposition's
    // own reposition attempts plus the actual swing/loot time can
    // plausibly add up to 30-40s in a genuinely bad case, so this is
    // purely a safety net against a leaked claim (state discarded before
    // its ContinueLootTask cycle could release it), not a normal expiry
    // path - every ordinary success/failure already releases explicitly
    // well before this.
    private const float LootClaimTtlSeconds = 60f;

    private bool IsLootTargetClaimed(NetworkableId id)
    {
        if (_lootClaims.TryGetValue(id, out float expiresAt))
        {
            if (expiresAt > UnityEngine.Time.realtimeSinceStartup)
            {
                return true;
            }

            _lootClaims.Remove(id);
        }

        return false;
    }

    private void ClaimLootTarget(LootTaskState state, NetworkableId id)
    {
        _lootClaims[id] = UnityEngine.Time.realtimeSinceStartup + LootClaimTtlSeconds;
        state.ClaimedTargetId = id;
    }

    private void ReleaseLootClaim(LootTaskState state)
    {
        if (state.ClaimedTargetId.HasValue)
        {
            _lootClaims.Remove(state.ClaimedTargetId.Value);
            state.ClaimedTargetId = null;
        }
    }

    /// <summary>
    /// Manually triggers the loot-for-resources task on a survivor -
    /// there's no autonomous need-based trigger yet (see TaskType's doc
    /// comment), so this is how the task gets exercised/tested for now,
    /// same "debug command before autonomy" pattern as claimbag/giveitem.
    /// </summary>
    [ChatCommand("lr.debug.settask")]
    private void CmdDebugSetTask(BasePlayer player, string command, string[] args)
    {
        RunDebugSetTask(player, args.Length > 0 ? string.Join(" ", args) : null);
    }

    [ConsoleCommand("lr.debug.settask")]
    private void CmdDebugSetTaskConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player == null)
        {
            return;
        }

        string aliasFilter = arg.HasArgs()
            ? string.Join(" ", arg.Args.Select(a => a.ToString()))
            : null;

        RunDebugSetTask(player, aliasFilter);
    }

    private void RunDebugSetTask(BasePlayer player, string aliasFilter)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor survivor = string.IsNullOrWhiteSpace(aliasFilter)
            ? FindNearestSpawnedSurvivor(player.transform.position)
            : FindSpawnedSurvivorByAlias(aliasFilter);

        if (survivor == null)
        {
            string message = string.IsNullOrWhiteSpace(aliasFilter)
                ? "No spawned survivor nearby. Use /lr.spawn first."
                : $"No spawned survivor named '{aliasFilter}'.";

            player.ChatMessage($"[LivingRust] {message}");
            Puts($"settask: {message}");
            return;
        }

        StartLootForResourcesTask(survivor);

        string confirm = $"'{survivor.Character.Alias}' is now looting for resources.";
        player.ChatMessage($"[LivingRust] {confirm}");
    }

    /// <summary>
    /// Kicks off the scan-vicinity-then-loot-everything-reachable loop.
    /// The whole loop is driven by StartWalking's onArrived callback
    /// chaining into the next step (find container -> walk to it -> loot
    /// it -> find the next one), the same pattern every other multi-step
    /// movement in this plugin already uses - no separate task-tick timer.
    /// </summary>
    private void StartLootForResourcesTask(Survivor survivor)
    {
        if (ShouldHoldForAirdrop(survivor.Character.Id))
        {
            return;
        }

        survivor.Character.CurrentTask = TaskType.LootForResources;

        // See RollPrimitiveGoalIfFreshLife's own doc comment
        // (LivingRust.Crafting.cs) - StartLootForResourcesTask is called
        // from many places (recycling finished, a monument route
        // completing, EndCombat/EndFlee resuming, not just a fresh spawn),
        // so the roll itself is internally gated to only ever fire once
        // per life rather than needing a bespoke spawn-only call site.
        RollPrimitiveGoalIfFreshLife(survivor);

        // Same once-per-life gating pattern, same reasoning (LivingRust.
        // HomeSiteStrategy.cs's own doc comment) - now takes a callback
        // (2026-09-01, live report: bots "piling up in certain areas...
        // all appear to be farming around the same area" - an inland roll
        // used to only ever remember the target for LATER, at base-build
        // time, so it never actually walked there and just farmed wherever
        // it already was, same as every coastal-rolled survivor). The rest
        // of this function's own body now only runs once the roll (and, for
        // an inland pick, the real walk there) is actually done.
        RollHomeSiteStrategyIfFreshLife(survivor, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                Puts($"loot-task: '{survivor.Character.Alias}' has no live BasePlayer, can't start.");
                survivor.Character.CurrentTask = TaskType.None;
                return;
            }

            DropUnneededLightSource(survivor, npc);

            // Confirm kit is actually in order before heading off, not just
            // reactively after each pickup - see PerformInventoryCheck's
            // own doc comment.
            PerformInventoryCheck(survivor, npc);

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' scanning a {LootSearchRadius:F0}m radius for containers to loot.");

            // Real root-cause fix (2026-09-01, live report: "any reason
            // bots are trying to loot mining outpost and not going
            // straight for the unconditional checklist").
            // TryStartWithGearWeightedDestination can roll a real monument
            // and commit to a genuine StartLongDistanceWalk toward it
            // BEFORE ContinueLootTask (where the _pursuingPrimitiveGoals
            // priority check actually lives) ever runs a single time -
            // that check only ever gets consulted once the destination
            // walk ARRIVES, not before it starts. A fresh survivor flagged
            // as pursuing the checklist just two lines above could still
            // end up walking halfway across the map to a monument first,
            // checklist priority notwithstanding. Skipping straight to
            // ContinueLootTask (forceLocalScan, same as the "nothing worth
            // a destination" branches inside TryStartWithGearWeightedDestination
            // already use) for a checklist-pursuing survivor means the
            // checklist priority branch is the very first thing that
            // actually runs, matching what its own doc comment already
            // claimed ("BEFORE the normal six-category loot search") but
            // didn't actually guarantee for this specific call site until
            // now.
            if (_pursuingPrimitiveGoals.Contains(survivor.Character.Id))
            {
                ContinueLootTask(survivor, new LootTaskState(), forceLocalScan: true);
                return;
            }

            // Same priority-before-destination-roll fix, extended to base-
            // gathering (2026-09-19, live report: 'RecklessScrapper615'
            // found dead with a broken hatchet, an inventory full of stone
            // and wood, and no base anywhere - live trace showed it had
            // already rolled a design and started walking to build, got
            // stuck on forest geometry en route, and was eventually stall-
            // rescued by the watchdog straight back into
            // StartLootForResourcesTask. This exact check only ever covered
            // _pursuingPrimitiveGoals, so a rescued base-gathering survivor
            // fell through to the gear-weighted roll below same as any
            // ordinary looter - happily wandering off toward a monument or
            // road instead of resuming (or retrying) its own base attempt,
            // with Character.Home still null and nowhere to ever deposit
            // what it was carrying. See TryPursueBaseGatherGoal's own doc
            // comment (LivingRust.HomeSiteStrategy.cs) for the matching fix
            // that stopped _pursuingBaseGatherGoal from being cleared
            // before a build actually succeeds - this check is what
            // actually lets that retry happen instead of being ignored.
            if (_pursuingBaseGatherGoal.Contains(survivor.Character.Id))
            {
                ContinueLootTask(survivor, new LootTaskState(), forceLocalScan: true);
                return;
            }

            // Gear-weighted starting destination (2026-08-15) - see
            // LivingRust.GearScore.cs's own doc comment for the full spec.
            // Replaces the old unconditional "just search right here"
            // start - now that's still the single most likely outcome
            // (Local is always in the weighted roll), but no longer the
            // ONLY outcome, which is what let 200 simultaneous beach
            // spawns all converge on whatever was nearest to the beach
            // every single time.
            TryStartWithGearWeightedDestination(survivor, npc);
        });
    }

    private void ContinueLootTask(Survivor survivor, LootTaskState state, bool forceLocalScan = false)
    {
        // Combat takes over entirely once it starts (2026-08-11) - a live
        // report caught bots "sometimes re-engage in looting whilst
        // firing," confirmed via log: a corpse-loot timer or movement step
        // already in flight the instant StartCombat's CancelActiveMovement/
        // CancelActiveAttack calls fire has nothing registered yet to
        // cancel (it's mid-setup, not yet holding its own timer), so it
        // completes obliviously and calls straight back into this exact
        // method, resuming the loot chain in parallel with an active fight.
        // ContinueLootTask is the single funnel every loot step eventually
        // re-enters (see its own architecture note - a callback chain, not
        // a ticking loop), so checking here catches every path, not just
        // the ones already covered by the two cancel calls at combat start.
        if (_activeCombat.ContainsKey(survivor.Character.Id))
        {
            return;
        }

        // Releases whatever this survivor claimed last cycle before doing
        // anything else - unconditionally, even if npc turns out to be
        // dead/gone below, so a claim never outlives the survivor that
        // held it. See _lootClaims' own doc comment for why this exists.
        ReleaseLootClaim(state);

        // Same release-at-the-top pattern for road-hop destinations - see
        // _roadHopClaims' own doc comment.
        _roadHopClaims.Remove(survivor.Character.Id);

        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.None;
            return;
        }

        // Real "should want to be at 100 health all the time" proactive
        // self-heal (2026-09-07, Lucas's own explicit spec, after a live
        // trace found 90%+ of all deaths were bleeding out with literally
        // no attacker attached - see OnPlayerDeath's own UNRECORDED-damage
        // doc comment, LivingRust.Hooks.cs). TryUseMedicalItemIfHurt used
        // to have exactly one caller in the whole project - StartTacticalRetreat,
        // itself only reached on a probabilistic tactical-decision roll
        // DURING active combat - so a survivor that took damage anywhere
        // else (a losing fight the tactical roll didn't retreat from, an
        // animal bite, whatever) had no path to ever bandage itself back
        // up at all, regardless of what it was doing (farming, building,
        // just walking). Fire-and-forget, same "heal while moving/working
        // is the intended behaviour" reasoning StartTacticalRetreat's own
        // doc comment already established - it no-ops instantly if health
        // is fine, already mid-chain, on cooldown, or has nothing to heal
        // with, so this is cheap in the overwhelmingly common case. Safe
        // to call unconditionally here specifically because ContinueLootTask
        // itself already returns early whenever _activeCombat is active
        // (immediately above) - this can never double-fire against the
        // combat-retreat call, the two are mutually exclusive by
        // construction.
        TryUseMedicalItemIfHurt(survivor);

        // Top priority after a fight-up kill - see LivingRust.KillLoot.cs.
        if (TryPursuePriorityKillLoot(survivor, npc, state))
        {
            return;
        }

        // Airdrop priority - see ShouldHoldForAirdrop.
        if (ShouldHoldForAirdrop(survivor.Character.Id))
        {
            return;
        }

        // Real standing "never let a survivor go fully toolless" safety net
        // (2026-09-01, Lucas's own explicit spec: "the bot still will
        // require tools regardless and if the bot ever has no tools (after
        // checking all of these things), it will still need to get tools
        // to farm more efficiently"). Checked UNCONDITIONALLY every cycle,
        // ahead of the primitive-checklist/base-gather branches below - a
        // survivor that skipped the checklist entirely (ShouldSkipPrimitiveChecklist),
        // already finished it, or is off doing normal looting/base-gathering
        // could all still end up with nothing but the starting rock (lost
        // its tool, never found one, whatever) and this is the one place
        // that keeps catching it regardless of which of those states it's
        // actually in. Cheap in the common case - HasAnyToolOfFamily short-
        // circuits immediately once a real tool of either family exists, so
        // this only ever actually claims a cycle for a genuinely toolless
        // survivor.
        if (!HasAnyToolOfFamily(npc, HatchetFamily) && TryPursueOneOffToolGoal(survivor, npc, state, StoneHatchetShortname, "stone hatchet"))
        {
            return;
        }

        if (!HasAnyToolOfFamily(npc, PickaxeFamily) && TryPursueOneOffToolGoal(survivor, npc, state, StonePickaxeShortname, "stone pickaxe"))
        {
            return;
        }

        // Real "passively set, not forcefully done" bandage-supply priority
        // (2026-09-07, Lucas's own explicit spec, same session/reasoning as
        // TryUseMedicalItemIfHurt's own new unconditional call above) - a
        // survivor that's genuinely hurt AND has nothing left to heal with
        // at all (not just "below full," which the proactive heal call
        // above already handles on its own once real supply exists) rolls
        // THIS as a real priority, same tier as the toolless safety net
        // just above - not an urgent interrupt, just the next thing it
        // reaches for instead of normal looting/gathering this cycle.
        if (TryPursueBandageSupplyIfHurt(survivor, npc, state))
        {
            return;
        }

        // Standing survival-kit upkeep (2026-09-21) - see
        // TryPursueSurvivalKitUpkeep's own doc comment.
        if (TryPursueSurvivalKitUpkeep(survivor, npc, state))
        {
            return;
        }

        // Real checklist-retry re-entry (2026-09-01, Lucas's own explicit
        // ask - see the timeout branch below, and _primitiveGoalRetryTime's
        // own doc comment, LivingRust.Crafting.cs, for the full "revert to
        // looting, retry from a different spot later" mechanism). Checked
        // ahead of the priority branch itself so a survivor whose retry
        // just came due re-enters checklist priority THIS cycle rather
        // than needing one more normal-looting cycle first. No-ops (and
        // clears the schedule) if the survivor already found a home in the
        // meantime - nothing left to retry toward.
        if (_primitiveGoalRetryTime.TryGetValue(survivor.Character.Id, out float retryTime) && UnityEngine.Time.realtimeSinceStartup >= retryTime)
        {
            _primitiveGoalRetryTime.Remove(survivor.Character.Id);

            if (survivor.Character.Home == null)
            {
                _pursuingPrimitiveGoals.Add(survivor.Character.Id);
                _primitiveGoalDeadline[survivor.Character.Id] = UnityEngine.Time.realtimeSinceStartup + PrimitiveGoalTimeLimitSeconds;
                Puts($"craft-task: '{survivor.Character.Alias}' is retrying its primitive checklist from its new location.");
            }
        }

        // Real spawn-time priority roll (2026-08-28, Lucas's own explicit
        // request) - see RollPrimitiveGoalIfFreshLife's own doc comment
        // (LivingRust.Crafting.cs) for the roll itself. A survivor that
        // rolled into this checks its primitive starter checklist (bag,
        // bow, arrows, stone tools) BEFORE the normal six-category loot
        // search below, rather than only as a last resort once nothing's
        // left nearby - deliberately reversed from TryStartCraftingFallback's
        // own default placement, per Lucas's own framing ("prioritise...
        // as a separate roll the dice to decide what I want to do when I
        // spawn"). Once every goal on the checklist is actually satisfied,
        // this permanently stops checking for the rest of that life -
        // TryStartCraftingFallback further down still runs its own normal
        // last-resort check afterward, same as any other survivor.
        if (_pursuingPrimitiveGoals.Contains(survivor.Character.Id))
        {
            // Real "give up on the coast, go inland" stall redirect
            // (2026-09-01, Lucas's own explicit spec - see
            // TryRedirectStalledCoastalBotInland's own doc comment,
            // LivingRust.HomeSiteStrategy.cs, for the full mechanism).
            // Checked before the 15-minute timeout below - a Coastal
            // survivor genuinely making zero gathering progress for 2.5
            // minutes shouldn't need to wait out the full 15-minute window
            // before something changes; this gets it moving toward a real
            // inland site well before that, same as if it had rolled
            // Inland from the start.
            if (TryRedirectStalledCoastalBotInland(survivor, npc))
            {
                return;
            }

            // Real 15-minute give-up (2026-09-01, Lucas's own explicit ask:
            // "if the bots aren't able to effectively complete this
            // checklist in 15 minutes have them move onto farm for wood
            // and stones for a base") - checked BEFORE trying another
            // craft cycle, so a survivor that's been stuck (unreachable
            // ingredient, whatever) for the full window switches over
            // immediately rather than needing one more failed cycle first.
            if (UnityEngine.Time.realtimeSinceStartup >= GetOrSetPrimitiveGoalDeadline(survivor.Character.Id))
            {
                // Real "revert to normal looting, retry later" change
                // (2026-09-01, Lucas's own explicit ask: "if the bot finds
                // no stones nearby or trees to pass that 15 minute mark...
                // have it revert to the generic looting task, then after
                // 15 minutes it should theoretically be in a different
                // location to retry that primitive checklist" -
                // previously this jumped straight to base-gathering
                // instead, which could just as easily get stuck on the
                // SAME missing ingredient in the SAME spot for the exact
                // same reason). Normal looting naturally relocates a
                // survivor over time (monument travel, road-following),
                // so scheduling a real retry after another
                // PrimitiveGoalTimeLimitSeconds gives the checklist a
                // genuinely different location and a fresh full window to
                // work with, rather than a one-shot attempt.
                _pursuingPrimitiveGoals.Remove(survivor.Character.Id);
                _primitiveGoalRetryTime[survivor.Character.Id] = UnityEngine.Time.realtimeSinceStartup + PrimitiveGoalTimeLimitSeconds;
                Puts($"craft-task: '{survivor.Character.Alias}' didn't finish its primitive checklist within 15 minutes - reverting to normal looting, will retry the checklist in {PrimitiveGoalTimeLimitSeconds:F0}s from wherever it ends up.");
            }
            else if (TryStartCraftingFallback(survivor, npc, state))
            {
                return;
            }
            // TryStartCraftingFallback returning false does NOT mean the
            // checklist is done - see HasCompletedPrimitiveGoals's own doc
            // comment (LivingRust.Crafting.cs) for the real live bug this
            // fixes (a survivor kicked out of primitive-goal priority
            // permanently the first time a needed ingredient just wasn't
            // reachable nearby, having completed nothing). Only actually
            // clears the flag once every real item is confirmed owned;
            // otherwise falls through to normal looting for just this one
            // cycle (which can turn up loose cloth/wood/stone too - see
            // Lucas's own "loot collectable entities on the way" framing)
            // while staying in the priority set to keep trying next cycle.
            else if (HasCompletedPrimitiveGoals(survivor, npc))
            {
                _pursuingPrimitiveGoals.Remove(survivor.Character.Id);

                // Real "next step is base building" (2026-09-01, Lucas's
                // own explicit spec: "if the bot manages to hit the
                // checklist its next step is base building... realistically
                // looting is fine, however if a bot just indefinitely loots
                // and it gets killed it loses potentially hours of progress
                // with nowhere to store loot"). A genuinely finished
                // checklist now leads into gathering for a base, same as
                // the 15-minute-timeout path above and ShouldSkipPrimitiveChecklist's
                // own skip branch (LivingRust.Crafting.cs) - all three real
                // ways a survivor can be "done with" the checklist funnel
                // into the same next step. No-ops if it somehow already has
                // one (shouldn't happen here, but matches the other two
                // call sites' own guard).
                if (survivor.Character.Home == null)
                {
                    _pursuingBaseGatherGoal.Add(survivor.Character.Id);
                    Puts($"craft-task: '{survivor.Character.Alias}' has everything from its primitive starter checklist - moving on to gather for a base.");
                }
                else
                {
                    Puts($"craft-task: '{survivor.Character.Alias}' has everything from its primitive starter checklist - back to normal looting.");
                }
            }
        }

        // Real "gather for a base" fallback (2026-09-01, Lucas's own
        // explicit spec) - see TryPursueBaseGatherGoal's own doc comment
        // (LivingRust.HomeSiteStrategy.cs) for the full checklist->design
        // roll->gather->build pipeline this drives.
        if (_pursuingBaseGatherGoal.Contains(survivor.Character.Id))
        {
            if (TryPursueBaseGatherGoal(survivor, npc, state))
            {
                return;
            }
        }

        // Main inventory alone, not IsInventoryFull's "both main AND belt"
        // (2026-08-15, Lucas's own explicit correction) - a full toolbelt
        // with room left in main isn't actually a survivor that's out of
        // carrying capacity, since new loot lands in main first anyway
        // (see TryTransferSingleItem). See LivingRust.Recycling.cs -
        // replaces the old outright idle-forever give-up with a real
        // recycler trip when one's reachable and worth the walk.
        if (IsMainInventoryFullIncludingBackpack(npc))
        {
            if (!TryStartRecyclingTask(survivor))
            {
                // Real fix (2026-09-19, Lucas's own explicit ask: "a bot
                // getting a full inventory... they should look to go back
                // to their base they built and deposit loot then re-roll
                // to go loot roads, monuments etc"). A based survivor with
                // nothing worth recycling nearby used to just idle forever
                // here instead - now takes the exact same real "go home,
                // deposit everything, resume looting" trip
                // GhostReturnHomeAndDeposit already gives a post-recycling
                // survivor (LivingRust.Recycling.cs's own FinishRecycling),
                // just triggered directly rather than via a recycler visit.
                if (survivor.Character.Home != null)
                {
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' is full up with nothing worth recycling nearby - heading home to deposit.");
                    GhostReturnHomeAndDeposit(survivor, () => StartLootForResourcesTask(survivor));
                }
                // Real fix (2026-09-19, live report: bots genuinely stuck
                // in an infinite loop near Harbor's beach - "stuck doing
                // nothing" while actually teleporting every ~15-20s
                // forever. Root cause: a base-less survivor with a full
                // inventory and nothing worth recycling nearby had no way
                // forward at all under the old idle fallback - it went
                // idle, the life-stall watchdog treated that as "stuck"
                // and relocated it, which just re-discovered the
                // identical full inventory instantly and went idle again,
                // live evidence: '18QuietNomad' cycling this exact loop
                // for 6+ minutes straight. A real player in this spot just
                // drops junk to keep moving - reuses DropLowerPriorityItem
                // (this file's own existing eviction logic, already
                // keycard-protection-aware) to free exactly one slot from
                // the single least valuable thing owned, sentinel tier
                // -1 so literally anything (even Other-tier junk) is
                // eligible, then resumes looting instead of parking
                // forever.
                // Cooldown-gated (2026-09-19, live report: this exact
                // fallback firing 12 times in under 2 minutes for one
                // survivor separately stuck on a real navmesh/movement
                // problem near monument dressing props - dropping another
                // item every retry never helps a bot that can't actually
                // MOVE, just adds real per-call inventory-scan cost on top
                // of an already-failing situation. A genuinely full,
                // still-progressing survivor only needs this rarely
                // regardless, so the cooldown costs it nothing real.
                else if (Time.realtimeSinceStartup - _lastFullInventoryDropTime.GetValueOrDefault(survivor.Character.Id) >= FullInventoryDropCooldownSeconds
                    && DropLowerPriorityItem(npc, (LootPriorityTier)(-1)))
                {
                    _lastFullInventoryDropTime[survivor.Character.Id] = Time.realtimeSinceStartup;
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' is full up with nothing worth recycling and no base yet - dropped its least valuable item to keep moving.");
                    StartLootForResourcesTask(survivor);
                }
                else
                {
                    // Genuinely nothing evictable, or still on cooldown
                    // from a recent drop - the one case still left idle,
                    // same as before. A bot idling here for real (not
                    // stuck-and-retrying) still gets picked up by the
                    // life-stall watchdog's own rescue chain as usual.
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' is full up - done looting.");
                    survivor.Character.CurrentTask = TaskType.None;
                }
            }

            return;
        }

        // Restores the display weapon before every new search/walk cycle,
        // regardless of what the previous cycle actually was - covers the
        // gap PerformInventoryCheck alone doesn't: a container attempt
        // that failed before ever landing a hit or looting anything (no
        // line of sight, too far, reposition exhausted) never calls
        // OnLootObtained at all, so nothing else would ever swap the
        // melee tool back out in that case. See EquipBestWeaponForDisplay's
        // own doc comment.
        RunLootHookSafely(survivor, nameof(EquipBestWeaponForDisplay), () => EquipBestWeaponForDisplay(survivor));

        // Without any melee tool at all (a bare-handed survivor - not the
        // normal case, since GiveStartingKit always gives a rock, but
        // reachable by e.g. dropping/losing it), only ever consider
        // containers that don't require destruction - there's nothing
        // productive about repeatedly "swinging" at a barrel with empty
        // hands. Once a tool turns up in a looted crate (EquipBestMeleeTool
        // runs right after every direct-loot transfer too), barrels become
        // fair game again on the next search.
        bool hasMeleeTool = HasAnyMeleeTool(npc);

        // Only computed if actually needed below (roadsign candidates
        // specifically) - HasNonRockMeleeTool does its own inventory scan,
        // no reason to pay for that on every single search cycle when
        // most won't even have a roadsign candidate nearby.
        bool? hasNonRockMeleeTool = null;

        // 50/50 coin flip between a local radius search (step 1) and
        // heading straight for a known monument loot zone (step 2),
        // 2026-08-15 - Lucas's own explicit call: roads/paths were getting
        // "way too congested/contested with bots," with a lot of them
        // visibly stalling on shorelines/paths/roads once every junkpile
        // nearby was already farmed out. Previously step 1 ran
        // unconditionally every single cycle and only fell through to step
        // 2 once it came up completely empty, so every bot in a played-out
        // area kept re-scanning the same dead ground before ever moving on.
        // Skipping the local scan outright half the time gets bots off
        // exhausted local ground and onto real, spread-out monument
        // destinations sooner - "a clear separation of bots" per Lucas's
        // own framing, rather than everyone lingering in lockstep on the
        // same nearby roads. A real container/corpse/dropped item right
        // next to the bot can still be missed on any given cycle, but
        // ContinueLootTask runs again after every step of the monument-zone
        // path too, so it's never missed for long.
        //
        // forceLocalScan (2026-08-15, real live bug: 'RustyScav' - a fresh
        // spawn HasNearbyLootWorthStartingLocally had just confirmed real
        // loot within 50m of, one cycle earlier, immediately rolled tails
        // here, found no cached monument zone nearby, and walked 152m away
        // to a road without ever actually looking at the loot it was JUST
        // confirmed to be standing next to) - the "start right here"
        // decision (LivingRust.GearScore.cs) and this dice roll were two
        // completely uncoordinated decisions that could directly
        // contradict each other. Every caller that just decided "search
        // right here" now forces this one specific cycle's local scan to
        // actually run, guaranteeing the loot that justified starting here
        // gets a real look before any dice roll can skip past it - only
        // this first guaranteed cycle is forced, every cycle after reverts
        // to the normal coin flip.
        //
        // Also forced whenever the survivor is actually inside/near a real
        // (non-excluded) monument right now (2026-08-15, Lucas's own
        // explicit refinement) - the dice roll's whole point is spreading
        // bots off ALREADY-exhausted local ground onto monuments sooner,
        // which doesn't apply to a bot already standing inside one; there
        // it should thoroughly work the monument's actual containers every
        // cycle, not randomly skip past them to re-check the SAME
        // monument's cached zone list instead. Same 60m detection radius
        // EscalateSearchToMonumentZone itself uses for "is a monument even
        // here" - reusing it keeps this consistent with what "checking a
        // known loot zone" actually means. Falls back to the normal 50/50
        // once nothing's within that radius - by then either the local
        // area's genuinely played out or the survivor's mid-road-following,
        // which is exactly the situation the dice roll was built for.
        //
        // Also checks the wider CommittedMonumentRangeRadius (150m) against
        // whichever monument this task has already committed to, not just
        // the generic 60m nearest-any-monument check (2026-08-15, Lucas's
        // own explicit request) - a survivor working the far side of a
        // large complex is still very much "inside the monument" even
        // though it may have wandered past 60m from that monument's own
        // transform origin.
        bool nearRealMonument = (state.CommittedMonumentName != null && TryGetCommittedMonument(npc.transform.position, state, out _))
            || (TryGetNearestMonument(npc.transform.position, MonumentLootZoneDetectionRadius, out MonumentInfo nearbyMonument)
                && !IsMonumentExcludedFromAutonomy(nearbyMonument));

        if (!forceLocalScan && !nearRealMonument && UnityEngine.Random.value < 0.5f)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' skipped the local scan this cycle (dice roll) - checking known loot zones instead.");
            EscalateSearchToMonumentZone(survivor, state);
            return;
        }

        // Computed once per cycle, reused across all three searches below -
        // see GetOccupiedPowerlineTowers' own doc comment.
        List<Vector3> occupiedPowerlineTowers = GetOccupiedPowerlineTowers(survivor, npc.transform.position);

        // Computed once per cycle, reused across every search below - see
        // GetNearbyVisibleHostileScientistPositions' own doc comment
        // (LivingRust.Combat.cs). Lucas's own explicit spec (2026-08-15):
        // avoid looting containers near a visible hostile scientist unless
        // armed with a ranged weapon that actually has ammo.
        List<Vector3> nearbyVisibleHostileScientists = GetNearbyVisibleHostileScientistPositions(npc);

        // Split into two separate tiers, 2026-08-14 (Lucas's own explicit
        // priority ordering) - direct-loot containers (crates, lockers, ...)
        // now rank strictly above anything requiring destruction (barrels,
        // roadsigns), rather than the two being one combined "whichever's
        // closest" search the way they used to be. LootContainerAndContinue
        // itself already branches on RequiresDestructionToLoot internally,
        // so both tiers below can keep sharing that same real loot method -
        // only the SEARCH/priority ordering changed, not how either actually
        // gets looted once reached.
        bool foundContainer = _engine.NavigationManager.TryFindNearestLootContainer(
            npc.transform.position,
            LootSearchRadius,
            out StorageContainer container,
            candidate => !state.Visited.Contains(candidate.net.ID)
                && !IsLootTargetClaimed(candidate.net.ID)
                && candidate.inventory != null
                && candidate.inventory.itemList.Count > 0
                // See TryFindEnRouteLootCandidate's identical check for the
                // full reasoning (2026-08-15 live bug) - a container made
                // entirely of NeverLootShortnames-excluded items (e.g.
                // vehicle_parts' engine components) always transfers 0
                // items and never empties, so without this it can still
                // get re-picked across separate fresh tasks (state.Visited
                // only protects within a single task/state).
                && candidate.inventory.itemList.Any(item => !IsNeverLootItem(item.info.shortname))
                && !IsNearJunkpileJVan(candidate.transform.position)
                && !IsInPoisonedZone(candidate.transform.position, state)
                && !IsUnreachableCrateOrBarrel(npc.transform.position, candidate)
                && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)
                && !IsVehicleFuelStorage(candidate)
                && !IsHotAirBalloonStorage(candidate)
                && !IsRowboatStorage(candidate)
                && !IsMailbox(candidate)
                && !IsNearCardReader(candidate.transform.position)
                && !IsNearOccupiedPowerlineTower(candidate.transform.position, occupiedPowerlineTowers)
                && !IsNearVisibleHostileScientist(candidate.transform.position, nearbyVisibleHostileScientists)
                && !RequiresDestructionToLoot(candidate));

        bool foundBarrel = _engine.NavigationManager.TryFindNearestLootContainer(
            npc.transform.position,
            LootSearchRadius,
            out StorageContainer barrel,
            candidate => !state.Visited.Contains(candidate.net.ID)
                && !IsLootTargetClaimed(candidate.net.ID)
                && candidate.inventory != null
                && candidate.inventory.itemList.Count > 0
                // See TryFindEnRouteLootCandidate's identical check for the
                // full reasoning (2026-08-15 live bug) - a container made
                // entirely of NeverLootShortnames-excluded items (e.g.
                // vehicle_parts' engine components) always transfers 0
                // items and never empties, so without this it can still
                // get re-picked across separate fresh tasks (state.Visited
                // only protects within a single task/state).
                && candidate.inventory.itemList.Any(item => !IsNeverLootItem(item.info.shortname))
                && !IsNearJunkpileJVan(candidate.transform.position)
                && !IsInPoisonedZone(candidate.transform.position, state)
                && !IsUnreachableCrateOrBarrel(npc.transform.position, candidate)
                && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)
                && !IsVehicleFuelStorage(candidate)
                && !IsHotAirBalloonStorage(candidate)
                && !IsRowboatStorage(candidate)
                && !IsMailbox(candidate)
                && !IsNearCardReader(candidate.transform.position)
                && !IsNearOccupiedPowerlineTower(candidate.transform.position, occupiedPowerlineTowers)
                && !IsNearVisibleHostileScientist(candidate.transform.position, nearbyVisibleHostileScientists)
                && RequiresDestructionToLoot(candidate)
                && (IsRoadsign(candidate)
                    ? (hasNonRockMeleeTool ??= HasNonRockMeleeTool(npc))
                    : hasMeleeTool));

        // Corpses (player, scientist/NPC, animal) are a completely separate
        // search - see TryFindNearestLootableCorpse's own doc comment for
        // why they can't just be folded into the container filter above.
        // Real "rewarded for a kill" behaviour, per Lucas's own framing:
        // one survivor's death (or a scientist's) becomes a real pickup
        // opportunity for whichever survivor finds it first.
        //
        // Safe-zone ownership exclusion (2026-08-11, Lucas's live report -
        // JitterySquatter was stuck trying to reach a corpse it could never
        // actually loot): confirmed via decompile that PlayerCorpse (real
        // class for any BasePlayer's death, ours included) and
        // DroppedItemContainer both genuinely enforce this in vanilla Rust -
        // PlayerCorpse.OnStartBeingLooted blocks looting whenever the corpse
        // (or looter) is InSafeZone() and the looter isn't playerSteamID's
        // owner. Plain world loot (LootableCorpse subclasses with no real
        // owner, e.g. a dead scientist/animal, and every ordinary
        // StorageContainer barrel/crate) is untouched - ownerless corpses
        // never hit this branch and ordinary containers use a completely
        // separate search above that was never touched.
        bool foundCorpse = _engine.NavigationManager.TryFindNearestLootableCorpse(
            npc.transform.position,
            LootSearchRadius,
            out LootableCorpse corpse,
            candidate => !state.Visited.Contains(candidate.net.ID)
                && !IsLootTargetClaimed(candidate.net.ID)
                && candidate.containers != null
                && candidate.containers.Any(c => c != null && c.itemList.Count > 0)
                && !IsInPoisonedZone(candidate.transform.position, state)
                && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)
                && !IsNearCardReader(candidate.transform.position)
                && !IsNearOccupiedPowerlineTower(candidate.transform.position, occupiedPowerlineTowers)
                && !IsNearVisibleHostileScientist(candidate.transform.position, nearbyVisibleHostileScientists)
                && (candidate is not PlayerCorpse || candidate.playerSteamID == survivor.Character.BotId || !candidate.InSafeZone())
                // See CorpseAmbientAwarenessRadius/IsRememberedOwnKill's own
                // doc comments - a corpse the survivor genuinely killed
                // stays reachable beyond the tightened ambient range,
                // anything else has to actually be nearby.
                && (Vector3.Distance(npc.transform.position, candidate.transform.position) <= CorpseAmbientAwarenessRadius
                    || IsRememberedOwnKill(survivor.Character.Id, candidate.transform.position)));

        // Despawned/destroyed bodies (fire, explosives, or a corpse's own
        // timer) convert into a real lootable bag - a completely separate
        // class from both StorageContainer and LootableCorpse (see
        // TryFindNearestDroppedItemContainer's own doc comment), so this
        // needs its own third search the exact same way corpses needed a
        // second one.
        bool foundBag = _engine.NavigationManager.TryFindNearestDroppedItemContainer(
            npc.transform.position,
            LootSearchRadius,
            out DroppedItemContainer bag,
            candidate => !state.Visited.Contains(candidate.net.ID)
                && !IsLootTargetClaimed(candidate.net.ID)
                && candidate.inventory != null
                && candidate.inventory.itemList.Count > 0
                && !IsInPoisonedZone(candidate.transform.position, state)
                && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)
                && !IsNearCardReader(candidate.transform.position)
                && !IsNearOccupiedPowerlineTower(candidate.transform.position, occupiedPowerlineTowers)
                && !IsNearVisibleHostileScientist(candidate.transform.position, nearbyVisibleHostileScientists)
                && (candidate.playerSteamID == 0 || candidate.playerSteamID == survivor.Character.BotId || !candidate.InSafeZone())
                // Same ambient-awareness gate the corpse search above uses -
                // see CorpseAmbientAwarenessRadius/IsRememberedOwnKill's own
                // doc comments.
                && (Vector3.Distance(npc.transform.position, candidate.transform.position) <= CorpseAmbientAwarenessRadius
                    || IsRememberedOwnKill(survivor.Character.Id, candidate.transform.position)));

        // A genuinely standalone loose item - see TryFindNearestDroppedItem's
        // own doc comment (a fifth distinct real entity type, confirmed live
        // via /lr.debug.scan finding a dropped rifle.ak/shotgun.m4 sitting
        // right next to - but NOT inside - a real DroppedItemContainer bag
        // at the same spot). IsNeverLootItem pre-filters here (the SAME
        // exclusion list TryTransferSingleItem itself checks again on
        // arrival) specifically so a survivor doesn't waste a walk toward
        // something it was always going to refuse - Lucas's own framing:
        // "pickup everything... except a few specific items."
        bool foundDroppedItem = _engine.NavigationManager.TryFindNearestDroppedItem(
            npc.transform.position,
            LootSearchRadius,
            out DroppedItem droppedItem,
            candidate => !state.Visited.Contains(candidate.net.ID)
                && !IsLootTargetClaimed(candidate.net.ID)
                && !IsNeverLootItem(candidate.item.info.shortname)
                && !IsInPoisonedZone(candidate.transform.position, state)
                && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)
                && !IsNearCardReader(candidate.transform.position)
                && !IsNearOccupiedPowerlineTower(candidate.transform.position, occupiedPowerlineTowers)
                && !IsNearVisibleHostileScientist(candidate.transform.position, nearbyVisibleHostileScientists)
                && !IsUnreachableDroppedItem(npc.transform.position, candidate.transform.position));

        // 2026-08-18, Lucas's own explicit redesign - previously only ever
        // searched at all once every other loot source came up completely
        // empty within the full 50m LootSearchRadius (2026-08-14: "only
        // grab if basically on the way"). Real live report: a bot at
        // Junkyard looted each junkpile's spawned container, but ignored
        // every barrel right next to it (and a whole second junkpile
        // within 20m) because a container kept turning up SOMEWHERE within
        // 50m each cycle, permanently starving the barrel/collectible
        // tiers - "he got too focused on go to monument rather than loot
        // what's in front of me first." Now always searched (own much
        // tighter CollectibleSearchRadius, unchanged - 10m, "to detract
        // from the bot diverting off course way too much") so it can
        // participate in the new proximity-priority check below alongside
        // everything else, rather than being structurally unreachable
        // whenever any container exists anywhere in the wider radius.
        bool foundCollectible = _engine.NavigationManager.TryFindNearestCollectible(
            npc.transform.position,
            CollectibleSearchRadius,
            out CollectibleEntity collectible,
            candidate => !state.Visited.Contains(candidate.net.ID)
                && !IsLootTargetClaimed(candidate.net.ID)
                && candidate.itemList != null
                && candidate.itemList.Length > 0
                && !IsInPoisonedZone(candidate.transform.position, state)
                && !IsInThreatFleeZone(survivor.Character.Id, candidate.transform.position)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && !IsBelowSafeLootDepth(candidate.transform.position)
                && !IsNearVisibleHostileScientist(candidate.transform.position, nearbyVisibleHostileScientists)
                && !IsExcludedCollectibleType(candidate)
                && IsResourceAllowedForHighGear(survivor, npc, candidate.itemList)
                && Vector3.Distance(npc.transform.position, candidate.transform.position) <= GetCollectibleDivertRadius(candidate));

        if (!foundContainer && !foundBarrel && !foundCorpse && !foundBag && !foundDroppedItem && !foundCollectible)
        {
            // Real crafting (2026-08-28) - see TryStartCraftingFallback's
            // own doc comment (LivingRust.Crafting.cs) for why this is
            // checked before active resource gathering: crafting is
            // effectively free (instant, zero movement) whenever the
            // survivor already holds enough raw material, so it's worth
            // deciding before ever walking anywhere for more.
            if (TryStartCraftingFallback(survivor, npc, state))
            {
                return;
            }

            // Real active resource gathering (2026-08-25) - see
            // TryStartResourceGatheringFallback's own doc comment
            // (LivingRust.ResourceGathering.cs) for why this is checked
            // here specifically, right before falling back to monument-zone
            // road-following, rather than blended into the six-category
            // search above.
            if (TryStartResourceGatheringFallback(survivor, npc, state))
            {
                return;
            }

            // Checked before road-following - a monument's interior often
            // has no real road running through it at all, and a known loot
            // cluster elsewhere in the SAME monument (e.g. a roof room once
            // the ground floor's exhausted) is a much more targeted next
            // step than blindly following whatever road happens to be
            // nearby. See EscalateSearchToMonumentZone's own doc comment.
            EscalateSearchToMonumentZone(survivor, state);
            return;
        }

        float containerDistance = foundContainer ? Vector3.Distance(npc.transform.position, container.transform.position) : float.MaxValue;
        float barrelDistance = foundBarrel ? Vector3.Distance(npc.transform.position, barrel.transform.position) : float.MaxValue;
        float bagDistance = foundBag ? Vector3.Distance(npc.transform.position, bag.transform.position) : float.MaxValue;
        float corpseDistance = foundCorpse ? Vector3.Distance(npc.transform.position, corpse.transform.position) : float.MaxValue;
        float droppedItemDistance = foundDroppedItem ? Vector3.Distance(npc.transform.position, droppedItem.transform.position) : float.MaxValue;
        float collectibleDistance = foundCollectible ? Vector3.Distance(npc.transform.position, collectible.transform.position) : float.MaxValue;

        // Proximity override, 2026-08-18 (Lucas's own explicit redesign,
        // replacing the old ALWAYS-strict tier order below for anything
        // genuinely close) - real live report: a bot at Junkyard looted
        // each junkpile's spawned container but walked straight past every
        // barrel sitting right next to it, and past a whole second
        // junkpile within 20m, because a container kept turning up
        // somewhere within the full 50m LootSearchRadius every single
        // cycle - the old strict tiers meant "any container anywhere in
        // 50m" permanently outranked "a barrel 2m away." Lucas's own
        // framing: "if something is within a 10-30 metre distance of the
        // bot, it should prioritise those loot containers, barrels,
        // corpses etc... loot as much as they can on the way" - real
        // players sweep everything genuinely close before moving on, they
        // don't beeline past it for a "higher tier" find much farther
        // away. Anything within NearbyLootPriorityRadius (30m) now wins
        // outright, whichever's actually closest regardless of type;
        // collectibles use their own much tighter CollectibleSearchRadius
        // (10m) for this same check. Only when NOTHING is close enough to
        // trigger this does the original strict type-tier order below
        // apply, for opportunistic finds further out.
        float bestNearbyDistance = float.MaxValue;

        if (foundContainer && containerDistance <= NearbyLootPriorityRadius) bestNearbyDistance = Mathf.Min(bestNearbyDistance, containerDistance);
        if (foundBarrel && barrelDistance <= NearbyLootPriorityRadius) bestNearbyDistance = Mathf.Min(bestNearbyDistance, barrelDistance);
        if (foundCorpse && corpseDistance <= NearbyLootPriorityRadius) bestNearbyDistance = Mathf.Min(bestNearbyDistance, corpseDistance);
        if (foundBag && bagDistance <= NearbyLootPriorityRadius) bestNearbyDistance = Mathf.Min(bestNearbyDistance, bagDistance);
        if (foundDroppedItem && droppedItemDistance <= NearbyLootPriorityRadius) bestNearbyDistance = Mathf.Min(bestNearbyDistance, droppedItemDistance);
        // No extra radius check needed here (unlike the other 5) - the
        // search predicate above already enforces the real per-type cutoff
        // (GetCollectibleDivertRadius) directly, so any foundCollectible is
        // already guaranteed close enough to matter.
        if (foundCollectible) bestNearbyDistance = Mathf.Min(bestNearbyDistance, collectibleDistance);

        bool proximityOverrideActive = bestNearbyDistance < float.MaxValue;

        // Strict priority tiers, 2026-08-14 (Lucas's own explicit ordering -
        // superseding the previous "corpse/bag/dropped-item are one combined
        // distance-based tier" rule): a standalone dropped item now
        // unconditionally outranks EVERYTHING else, regardless of distance -
        // whichever's closest between corpse and bag still only matters as
        // the tier right below it. Still the fallback whenever nothing
        // triggered the proximity override above.
        bool preferDroppedItemOverOthers = proximityOverrideActive ? droppedItemDistance == bestNearbyDistance : foundDroppedItem;
        bool preferCorpseOverOthers = proximityOverrideActive
            ? corpseDistance == bestNearbyDistance
            : !preferDroppedItemOverOthers && foundCorpse && (!foundBag || corpseDistance <= bagDistance);
        bool preferBagOverOthers = proximityOverrideActive
            ? bagDistance == bestNearbyDistance
            : !preferDroppedItemOverOthers && !preferCorpseOverOthers && foundBag;
        bool preferContainerOverOthers = proximityOverrideActive
            ? containerDistance == bestNearbyDistance
            : !preferDroppedItemOverOthers && !preferCorpseOverOthers && !preferBagOverOthers && foundContainer;
        bool preferBarrelOverOthers = proximityOverrideActive
            ? barrelDistance == bestNearbyDistance
            : !preferDroppedItemOverOthers && !preferCorpseOverOthers && !preferBagOverOthers && !preferContainerOverOthers && foundBarrel;
        bool preferCollectibleOverOthers = proximityOverrideActive && collectibleDistance == bestNearbyDistance;
        //
        // No shouldWalkCarefully needed here - StartWalking now sprints by
        // default for every walk (user's correction: real Rust players
        // run essentially all the time, not just when motivated by a
        // specific "I can see the loot" trigger), and already eases off
        // automatically near any destination (ApproachSlowdownDistance),
        // so the container-specific line-of-sight/distance gating this
        // used to need is gone - it's just the new universal default now.
        if (preferCorpseOverOthers)
        {
            state.Visited.Add(corpse.net.ID);
            ClaimLootTarget(state, corpse.net.ID);

            Vector3 corpseApproachPoint = GetApproachPoint(corpse, npc);

            StartWalkingWithRecovery(
                survivor,
                corpseApproachPoint,
                onArrived: () => LootCorpseAndContinue(survivor, corpse, state),
                onFailed: () =>
                {
                    // Same reasoning as the container onFailed below -
                    // StartWalkingWithRecovery already exhausted every
                    // cheaper recovery option by the time this runs. Never
                    // trust the outer npc local here - this fires well
                    // after the recovery chain finishes (real seconds
                    // later), long enough for the survivor to have died/
                    // respawned/despawned in the meantime, and touching a
                    // destroyed BasePlayer's .transform throws (confirmed
                    // live via a real NRE crash trace). Re-fetch fresh.
                    BasePlayer liveNpc = survivor.Player;

                    if (liveNpc == null || liveNpc.IsDestroyed)
                    {
                        ContinueLootTask(survivor, state);
                        return;
                    }

                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach a corpse even after trying to recover - skipping it.");
                    PoisonAreaNow(survivor, state, liveNpc.transform.position);
                    ContinueLootTask(survivor, state);
                });

            return;
        }

        if (preferBagOverOthers)
        {
            // Reached whenever bag "won" the corpse-vs-bag comparison
            // above (or no corpse was found at all) - unconditionally
            // beats an ordinary container, same as corpse's own original
            // behavior, extended equally to bag now that the two are
            // treated as peer-tier "fresh loot" sources (both real signals
            // someone died/lost their stuff here) rather than corpse alone
            // being special-cased. Avoids an inconsistent edge case where
            // corpse loses to a closer bag, but that bag then also loses
            // to an even-closer plain container, leaving neither picked
            // even though the corpse alone would have beaten that same
            // container under the old rule.
            state.Visited.Add(bag.net.ID);
            ClaimLootTarget(state, bag.net.ID);

            Vector3 bagApproachPoint = GetApproachPoint(bag, npc);

            StartWalkingWithRecovery(
                survivor,
                bagApproachPoint,
                onArrived: () => LootDroppedItemContainerAndContinue(survivor, bag, state),
                onFailed: () =>
                {
                    // See the corpse onFailed above for why the outer npc
                    // local can't be trusted here - re-fetch fresh.
                    BasePlayer liveNpc = survivor.Player;

                    if (liveNpc == null || liveNpc.IsDestroyed)
                    {
                        ContinueLootTask(survivor, state);
                        return;
                    }

                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach a dropped bag even after trying to recover - skipping it.");
                    PoisonAreaNow(survivor, state, liveNpc.transform.position);
                    ContinueLootTask(survivor, state);
                });

            return;
        }

        if (preferDroppedItemOverOthers)
        {
            // A standalone loose item, not a container - only ever ONE real
            // Item to actually decide on (TryTransferSingleItem, the same
            // never-loot/duplicate/inferior-armor checks a corpse or bag's
            // own per-item transfer already applies), so this skips the
            // paced multi-item LootMultiContainerEntityAndContinue flow
            // entirely in favour of its own much simpler single-pickup one.
            state.Visited.Add(droppedItem.net.ID);
            ClaimLootTarget(state, droppedItem.net.ID);

            Vector3 droppedItemApproachPoint = GetApproachPoint(droppedItem, npc);

            StartWalkingWithRecovery(
                survivor,
                droppedItemApproachPoint,
                onArrived: () => PickupDroppedItemAndContinue(survivor, droppedItem, state),
                onFailed: () =>
                {
                    // See the corpse onFailed above for why the outer npc
                    // local can't be trusted here - re-fetch fresh.
                    BasePlayer liveNpc = survivor.Player;

                    if (liveNpc == null || liveNpc.IsDestroyed)
                    {
                        ContinueLootTask(survivor, state);
                        return;
                    }

                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach a dropped item even after trying to recover - skipping it.");
                    PoisonAreaNow(survivor, state, liveNpc.transform.position);
                    ContinueLootTask(survivor, state);
                });

            return;
        }

        if (preferContainerOverOthers)
        {
            state.Visited.Add(container.net.ID);
            ClaimLootTarget(state, container.net.ID);

            Vector3 approachPoint = GetApproachPoint(container, npc);

            StartWalkingWithRecovery(
                survivor,
                approachPoint,
                onArrived: () => LootContainerAndContinue(survivor, container, state),
                onFailed: () =>
                {
                    // container is already in Visited, so simply moving on to
                    // the next nearest one won't retry this same unreachable
                    // spot - matches the line-of-sight give-up in
                    // StartAttackingContainer (same "skip it, don't get stuck
                    // on one container forever" principle). Poisons
                    // immediately (PoisonAreaNow, not the counted
                    // RegisterLootFailure) - by the time StartWalkingWithRecovery
                    // calls this, it's already exhausted wiggling, a navmesh
                    // nudge, AND an emergency teleport, so this single failure
                    // is already strong evidence the whole area is bad, not
                    // just this one container.
                    // See the corpse onFailed above for why the outer npc
                    // local can't be trusted here - re-fetch fresh.
                    BasePlayer liveNpc = survivor.Player;

                    if (liveNpc == null || liveNpc.IsDestroyed)
                    {
                        ContinueLootTask(survivor, state);
                        return;
                    }

                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach '{container.ShortPrefabName}' even after trying to recover - skipping it.");
                    PoisonAreaNow(survivor, state, liveNpc.transform.position, container);
                    ContinueLootTask(survivor, state);
                });

            return;
        }

        if (preferBarrelOverOthers)
        {
            // Reached either because it won the proximity-priority check
            // above (closest real loot within NearbyLootPriorityRadius,
            // 2026-08-18), or - the original 2026-08-14 fallback, still
            // intact for anything found further out - once every higher
            // tier (dropped item, corpse, bag, direct-loot container) came
            // up empty. Shares LootContainerAndContinue with the
            // direct-loot tier above unchanged (it already branches on
            // RequiresDestructionToLoot internally) - only which tier gets
            // searched/preferred first changed, not how a barrel is
            // actually broken into once reached.
            state.Visited.Add(barrel.net.ID);
            ClaimLootTarget(state, barrel.net.ID);

            Vector3 barrelApproachPoint = GetApproachPoint(barrel, npc);

            StartWalkingWithRecovery(
                survivor,
                barrelApproachPoint,
                onArrived: () => LootContainerAndContinue(survivor, barrel, state),
                onFailed: () =>
                {
                    // See the container onFailed above for the same
                    // reasoning - re-fetch fresh, poison immediately.
                    BasePlayer liveNpc = survivor.Player;

                    if (liveNpc == null || liveNpc.IsDestroyed)
                    {
                        ContinueLootTask(survivor, state);
                        return;
                    }

                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach '{barrel.ShortPrefabName}' even after trying to recover - skipping it.");
                    PoisonAreaNow(survivor, state, liveNpc.transform.position, barrel);
                    ContinueLootTask(survivor, state);
                });

            return;
        }

        // Reached either because it won the proximity-priority check above
        // (closest real loot within its own tighter CollectibleSearchRadius,
        // 2026-08-18) or - the original 2026-08-14 fallback, still intact -
        // as the absolute last resort once nothing else was found at all.
        // No explicit "if (preferCollectibleOverOthers)" guard needed here:
        // the top-of-function gate already guarantees at least one of the
        // six foundX flags is true, and every OTHER flag's own prefer-check
        // above returns before ever reaching this point, so getting here at
        // all already proves collectible is the one that won.
        state.Visited.Add(collectible.net.ID);
        ClaimLootTarget(state, collectible.net.ID);

        Vector3 collectibleApproachPoint = GetApproachPoint(collectible, npc);

        // Plain StartWalking, not StartWalkingWithRecovery, and a short
        // CollectibleWalkTimeoutSeconds cap - Lucas's own explicit request
        // (2026-08-15): a collectible (mushroom/hemp/stone/sulfur/ore/
        // berries) is the lowest-value loot source this project searches,
        // sitting deep in dense foliage/trees often enough that the FULL
        // wiggle/navmesh-nudge/emergency-teleport recovery ladder (several
        // real seconds per tier) was visibly wasting time on something not
        // worth that effort - bots looked "stuck inside foliage" for far
        // longer than a berry bush warrants. A real player would just give
        // up on an awkward one almost immediately, not fight the geometry.
        StartWalking(
            survivor,
            collectibleApproachPoint,
            onArrived: () => PickupCollectibleAndContinue(survivor, collectible, state),
            onFailed: () =>
            {
                BasePlayer liveNpc = survivor.Player;

                if (liveNpc == null || liveNpc.IsDestroyed)
                {
                    ContinueLootTask(survivor, state);
                    return;
                }

                VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach a collectible within {CollectibleWalkTimeoutSeconds:F0}s - skipping it.");
                PoisonAreaNow(survivor, state, liveNpc.transform.position);
                ContinueLootTask(survivor, state);
            },
            maxSeconds: CollectibleWalkTimeoutSeconds);
    }

    // See the StartWalking call above for the full reasoning - deliberately
    // much shorter than the default 200s walk timeout, and deliberately
    // skips the full stuck-recovery ladder entirely (plain StartWalking,
    // not StartWalkingWithRecovery).
    private const float CollectibleWalkTimeoutSeconds = 2f;

    /// <summary>
    /// How far to look for ANY real road when the immediate area has
    /// nothing left to loot - deliberately generous, since the whole
    /// point is finding a road to walk toward when there's genuinely
    /// nothing nearby, not a tight proximity check.
    /// </summary>
    private const float RoadSearchDetectionRadius = 60f;

    /// <summary>
    /// Genuine last-resort search radius, tried only once RoadSearchDetectionRadius
    /// comes up completely empty - Lucas's own live report: bots spawned
    /// on remote shorelines (ToxicWolf, CrazyCamper, LuckyTorch794) with
    /// no loot AND no road within 60m just stood still forever, since
    /// EscalateSearchAlongRoad's old behaviour was to give up outright at
    /// that point. This is deliberately wide - large enough to plausibly
    /// reach a real road from almost anywhere on a 1500-size map, even a
    /// remote beach far from the nearest cluster - since the alternative
    /// is a permanently idle survivor. Only spent once per "found nothing
    /// nearby" episode (see the fallback walk below), not repeated every
    /// cycle, so the one-time wider search cost is acceptable.
    /// </summary>
    private const float RoadSearchFallbackRadius = 800f;

    /// <summary>
    /// Cumulative distances (metres) tried, one per road-following
    /// escalation, before really giving up - Lucas's own example numbers
    /// ("10, 20, 30, 40 metres"). Not a hard "stay on the road" rule
    /// (Lucas's own framing: "it shouldn't be like I HAVE to stay on the
    /// road... it's the path in which I will find other lootable
    /// containers") - ContinueLootTask's normal radius scan still runs at
    /// every new point this walks to, and can find loot anywhere within
    /// it, on or off the road; this only decides where to walk next once
    /// that scan has already come up empty right where the survivor is
    /// standing. Real Rust loot (barrels, junkpiles) spawns disproportionately
    /// along roads/paths, so following one is a genuine "where am I likely
    /// to find more" heuristic, not an arbitrary wander.
    /// </summary>
    // Widened 2026-08-10 (10/20/30/40 -> 20/50/75/150, 100m total -> 350m
    // total) - Lucas's own framing: 100m cumulative is negligible on a
    // 1500-size map, nowhere near enough to actually cross the gaps
    // between real loot clusters on a large map.
    private static readonly float[] RoadFollowDistances = { 20f, 50f, 75f, 150f };

    /// <summary>
    /// Called once ContinueLootTask's normal radius scan comes up
    /// completely empty (no containers, no corpses) - rather than giving
    /// up immediately, checks for a real nearby road and walks further
    /// along it before conceding, mirroring how a real player would keep
    /// moving toward wherever loot tends to spawn instead of stopping
    /// dead in an empty patch of terrain.
    ///
    /// Each call re-finds the nearest road point fresh from wherever the
    /// survivor currently stands (not a remembered anchor from the first
    /// call) - since a successful hop already moves the survivor further
    /// along, this naturally produces a real cumulative progression
    /// (hop 1 walks 10m from the start; by hop 2 the survivor's already
    /// 10m in, so walking another 20m from THERE lands ~30m from the
    /// original spot) without needing to track a fixed anchor point or
    /// direction explicitly. state.RoadFollowAttempts is what actually
    /// bounds the total, incremented once per call regardless of whether
    /// the walk itself succeeds - a walk failure still "uses up" an
    /// attempt rather than letting the bot retry the same hop forever.
    ///
    /// Direction (forward vs backward along the spline) is re-decided
    /// every call too, preferring whichever side of the nearest point
    /// still has more real road left - avoids repeatedly aiming at a
    /// road's own dead-end.
    /// </summary>
    private void EscalateSearchAlongRoad(Survivor survivor, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.None;
            return;
        }

        if (state.RoadFollowAttempts >= RoadFollowDistances.Length)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' followed the road as far as it's going to ({RoadFollowDistances[^1]:F0}m total) and found nothing more.");
            EscalateSearchToKnownMonument(survivor, state);
            return;
        }

        if (!_engine.NavigationManager.TryFindNearestRoadPoint(npc.transform.position, RoadSearchDetectionRadius, out _, out PathInterpolator road, out float distanceAlongRoad))
        {
            // Genuine last resort before giving up outright - see
            // RoadSearchFallbackRadius's own doc comment. Walks straight
            // to the nearest point on whatever road this wider search
            // finds (not a hop-distance extension - the normal escalation
            // above doesn't apply here, this is "get to ANY road at all"),
            // then resumes the ordinary loot search from there.
            if (!_engine.NavigationManager.TryFindNearestRoadPoint(npc.transform.position, RoadSearchFallbackRadius, out Vector3 fallbackPoint, out PathInterpolator fallbackRoad, out float fallbackDistanceAlongRoad))
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' found nothing left to loot nearby, and no road within {RoadSearchFallbackRadius:F0}m to follow either.");
                EscalateSearchToKnownMonument(survivor, state);
                return;
            }

            // Same contention nudge as the normal hop path below - see
            // _roadHopClaims' own doc comment.
            if (IsRoadDestinationContested(fallbackPoint, survivor.Character.Id))
            {
                bool fallbackForward = fallbackRoad.Length - fallbackDistanceAlongRoad >= fallbackDistanceAlongRoad;
                float fallbackOffset = UnityEngine.Random.Range(RoadHopContentionOffsetMin, RoadHopContentionOffsetMax);
                float nudgedDistance = Mathf.Clamp(
                    fallbackForward ? fallbackDistanceAlongRoad + fallbackOffset : fallbackDistanceAlongRoad - fallbackOffset,
                    0f,
                    fallbackRoad.Length);
                fallbackPoint = fallbackRoad.GetPoint(nudgedDistance);
            }

            _roadHopClaims[survivor.Character.Id] = fallbackPoint;

            Vector3 fallbackDestination = fallbackPoint;

            if (_engine.NavigationManager.TryFindGroundBelow(fallbackPoint + Vector3.up * 4f, 4f, 6f, out float fallbackGroundY, out _))
            {
                fallbackDestination.y = fallbackGroundY;
            }

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' found nothing left to loot nearby, and no road within the normal {RoadSearchDetectionRadius:F0}m range - heading for the nearest road it can find at all ({Vector3.Distance(npc.transform.position, fallbackPoint):F0}m away).");

            // StartLongDistanceWalkDirect, not plain StartWalkingWithRecovery
            // (2026-08-15, same fix as the normal hop branch below) - see
            // that branch's own comment for why.
            StartLongDistanceWalkDirect(
                survivor,
                fallbackDestination,
                onArrived: () => ContinueLootTask(survivor, state),
                onFailed: () =>
                {
                    VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach the nearest distant road either.");
                    EscalateSearchToKnownMonument(survivor, state);
                });

            return;
        }

        bool forward = road.Length - distanceAlongRoad >= distanceAlongRoad;
        float hopDistance = RoadFollowDistances[state.RoadFollowAttempts];
        state.RoadFollowAttempts++;

        float targetDistance = Mathf.Clamp(
            forward ? distanceAlongRoad + hopDistance : distanceAlongRoad - hopDistance,
            0f,
            road.Length);

        Vector3 roadPoint = road.GetPoint(targetDistance);

        // If another survivor's already heading for roughly this same
        // spot, nudge further along the road instead of converging on it
        // together - stopgap per Lucas's explicit request, deliberately
        // NOT meant to be the permanent design (see _roadHopClaims' own
        // doc comment). Only tries once, not a loop - good enough to
        // break up the common "two bots ran out of loot near each other"
        // case without turning this into its own escalation chain.
        if (IsRoadDestinationContested(roadPoint, survivor.Character.Id))
        {
            float contentionOffset = UnityEngine.Random.Range(RoadHopContentionOffsetMin, RoadHopContentionOffsetMax);
            targetDistance = Mathf.Clamp(
                forward ? targetDistance + contentionOffset : targetDistance - contentionOffset,
                0f,
                road.Length);
            roadPoint = road.GetPoint(targetDistance);
        }

        _roadHopClaims[survivor.Character.Id] = roadPoint;

        // Real ground height at the road point via a downward raycast,
        // not the raw spline Y - same reasoning ComputeApproachPoint's
        // own doc comment gives for containers on elevated platforms;
        // roads are usually already close to real ground, but this is
        // the established pattern for "trust a computed point's Y",
        // not this project's own guess.
        Vector3 destination = roadPoint;

        if (_engine.NavigationManager.TryFindGroundBelow(roadPoint + Vector3.up * 4f, 4f, 6f, out float groundY, out _))
        {
            destination.y = groundY;
        }

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' found nothing left to loot nearby - following the road {hopDistance:F0}m further to look for more ({state.RoadFollowAttempts}/{RoadFollowDistances.Length}).");

        // StartLongDistanceWalkDirect, not plain StartWalkingWithRecovery
        // (2026-08-15) - confirmed live gap: road-hop following (this
        // branch, by far the most common/longest travel step in the whole
        // ladder) was the ONE place that skipped the en-route "grab
        // something on the way" scanner entirely, despite being exactly
        // where it matters most (a long walk along a real road, past real
        // containers). Only the gear-weighted start, monument-zone
        // escalation, and distant-monument escalation went through
        // StartLongDistanceWalk before this - Lucas's own question
        // ("shouldn't they loot along the way regardless of which
        // escalation step they're on?") caught it.
        StartLongDistanceWalkDirect(
            survivor,
            destination,
            onArrived: () => ContinueLootTask(survivor, state),
            onFailed: () =>
            {
                // Still counts as this attempt used up (RoadFollowAttempts
                // already incremented above) - re-running the normal scan
                // from wherever the survivor actually ended up (not
                // necessarily destination) either finds something nearby
                // after all, or escalates again for the next hop.
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't reach the next point along the road - trying the next stretch.");
                ContinueLootTask(survivor, state);
            });
    }

    /// <summary>
    /// Counts a failed loot attempt toward the current streak, and once
    /// ConsecutiveFailuresBeforeAvoidingArea is hit, poisons the area - see
    /// ConsecutiveFailuresBeforeAvoidingArea's own doc comment for the
    /// junkpile_j case this fixes. Used for lighter failures that never
    /// went through the full stuck-recovery escalation (currently the
    /// line-of-sight/too-far give-ups in StartAttackingContainer and
    /// LootContainerDirectly) - a single one of those isn't strong
    /// evidence the whole area is bad, just that this one container's
    /// angle was bad. Contrast with PoisonAreaNow, used when a walk
    /// already exhausted every recovery option first. container is
    /// optional (registers a retry attempt - see
    /// RetrySuccessesBeforeRevisit's own doc comment - when given).
    /// </summary>
    private void RegisterLootFailure(Survivor survivor, LootTaskState state, Vector3 position, StorageContainer container = null)
    {
        RegisterContainerRetry(state, container);

        state.ConsecutiveFailures++;

        if (state.ConsecutiveFailures < ConsecutiveFailuresBeforeAvoidingArea)
        {
            return;
        }

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' had {ConsecutiveFailuresBeforeAvoidingArea} loot failures in a row.");
        PoisonAreaNow(survivor, state, position);
    }

    /// <summary>
    /// Poisons the area immediately, no streak needed - used when a walk
    /// already exhausted the full stuck-recovery escalation (wiggle,
    /// navmesh nudge, emergency teleport - see StartWalkingWithRecovery)
    /// before failing. That's already much stronger evidence the whole
    /// surrounding area is bad than a single ordinary failure, confirmed
    /// via a live trace: two survivors each repeated the ENTIRE expensive
    /// escalation, once per nearby container, because
    /// ConsecutiveFailuresBeforeAvoidingArea previously required 4
    /// separate full exhaustions before poisoning ever kicked in - a lot
    /// of wasted real time re-litigating the same bad area. container is
    /// optional (registers a retry attempt - see
    /// RetrySuccessesBeforeRevisit's own doc comment - when given).
    /// </summary>
    private void PoisonAreaNow(Survivor survivor, LootTaskState state, Vector3 position, StorageContainer container = null)
    {
        RegisterContainerRetry(state, container);

        float expiresAt = UnityEngine.Time.realtimeSinceStartup + PoisonedZoneDuration;

        state.PoisonedZones.Add((position, PoisonedZoneRadius, expiresAt));
        state.ConsecutiveFailures = 0;

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' avoiding the area around {position} ({PoisonedZoneRadius:F0}m) for the next {PoisonedZoneDuration:F0}s.");

        // On top of the temporary, this-task-only poisoning above: also
        // feeds this into the permanent, monument-relative avoid-zone
        // system (LivingRust.MonumentAvoidZones.cs) - a full recovery
        // exhaustion is exactly the strong per-incident evidence that
        // system needs, and unlike the poisoning above, a confirmed zone
        // there is remembered for every survivor and never expires.
        RecordPotentialAvoidZone(position);
    }

    /// <summary>
    /// Marks container as eligible for exactly one retry once
    /// RetrySuccessesBeforeRevisit other containers have been
    /// successfully looted (see AdvanceRetryCountdown). Stays in Visited
    /// (still excluded from candidate search) until that countdown
    /// actually elapses - this only starts the countdown. A container
    /// that already used its one retry (AlreadyRetried) is left alone -
    /// permanently skipped, same as before this system existed. No-op if
    /// container is null (callers that don't have one to hand, e.g. the
    /// old area-only poisoning path).
    /// </summary>
    private void RegisterContainerRetry(LootTaskState state, StorageContainer container)
    {
        if (container == null)
        {
            return;
        }

        if (state.AlreadyRetried.Contains(container.net.ID))
        {
            // The one retry this container ever gets has already been
            // used, and it failed again. AdvanceRetryCountdown removed it
            // from Visited once already, specifically to grant that one
            // retry - nothing else re-adds it, so without this a
            // container that fails its retry too becomes permanently
            // re-searchable instead of permanently skipped, contradicting
            // this method's own original intent. Confirmed live via a
            // 35-bot trace: 'LuckyGoblin' re-attempted the exact same
            // 'oil_barrel' a third time, immediately after its one retry
            // had already failed all 4 reposition angles too. Re-add to
            // Visited for real, permanent exclusion this time.
            state.Visited.Add(container.net.ID);
            return;
        }

        state.PendingRetry[container.net.ID] = RetrySuccessesBeforeRevisit;
        state.AlreadyRetried.Add(container.net.ID);
    }

    /// <summary>
    /// Called after every successfully completed container (looted or
    /// broken open) - counts down every container currently waiting on a
    /// retry, and un-skips (removes from Visited) any that reach zero, so
    /// the next search picks them up again like any other candidate. By
    /// then the survivor has moved on to loot elsewhere, so a retry
    /// naturally approaches from wherever it happens to be standing next -
    /// a different physical angle than whatever failed the first time,
    /// without needing to explicitly compute or remember one.
    /// </summary>
    private void AdvanceRetryCountdown(LootTaskState state)
    {
        if (state.PendingRetry.Count == 0)
        {
            return;
        }

        List<NetworkableId> ready = null;

        foreach (NetworkableId id in new List<NetworkableId>(state.PendingRetry.Keys))
        {
            int remaining = --state.PendingRetry[id];

            if (remaining <= 0)
            {
                (ready ??= new List<NetworkableId>()).Add(id);
            }
        }

        if (ready == null)
        {
            return;
        }

        foreach (NetworkableId id in ready)
        {
            state.PendingRetry.Remove(id);
            state.Visited.Remove(id);
        }
    }

    private bool IsInPoisonedZone(Vector3 position, LootTaskState state)
    {
        float now = UnityEngine.Time.realtimeSinceStartup;

        foreach ((Vector3 center, float radius, float expiresAt) in state.PoisonedZones)
        {
            if (expiresAt > now && Vector3.Distance(position, center) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How close a candidate container needs to be to a "van_d_white"
    /// collider (junkpile_j's van, confirmed by name via a live
    /// /lr.debug.look scan) to count as "inside/right next to the van"
    /// and get excluded. The van's own bounds span ~6.8m x 5.7m
    /// (confirmed via that same scan), so this comfortably covers
    /// anything actually sitting in or immediately beside it without
    /// reaching far enough to exclude unrelated loot scattered elsewhere
    /// around the broader junkpile.
    /// </summary>
    private const float JunkpileJVanAvoidRadius = 6f;

    /// <summary>
    /// Rejects any container physically near junkpile_j's van - unlike
    /// other junkpile variants, that one's barrels/crates sit physically
    /// inside the van model rather than out on the ground, which made
    /// bots reliably get stuck trying to reach them (user report,
    /// confirmed via a live trace: a survivor oscillated in place for
    /// over a minute trying to close a final 2.2m gap before being
    /// killed).
    ///
    /// Originally implemented by walking the candidate's own transform-
    /// parent chain looking for "junkpile_j" in an ancestor's name - that
    /// didn't actually work (a second live trace showed a bot still
    /// targeting a van-interior item), almost certainly because Rust's
    /// spawn system places spawned loot as independent, unparented
    /// entities near the junkpile rather than as literal children of it,
    /// so there was nothing "junkpile_j"-named in the candidate's own
    /// ancestry to find. This checks physical proximity to the van's own
    /// collider instead - doesn't depend on any assumption about how
    /// Rust's spawner actually parents things, just where the van
    /// physically is relative to the candidate.
    /// </summary>
    private bool IsNearJunkpileJVan(Vector3 position)
    {
        Collider[] hits = Physics.OverlapSphere(position, JunkpileJVanAvoidRadius, ~0, QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            if (hit.gameObject.name.IndexOf("van_d_white", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How close a candidate needs to be to real powerline-tower geometry
    /// to count as "physically at/on this tower" - small and tight,
    /// deliberately just the structure's own footprint, not a wide area
    /// around it (that's PowerlineOccupancyRadius's job, applied
    /// separately once a tower's actually confirmed occupied).
    /// </summary>
    private const float PowerlineTowerProximityRadius = 8f;

    /// <summary>
    /// Once a powerline tower is confirmed occupied by another survivor,
    /// how wide an area around its own root position gets excluded from
    /// every OTHER survivor's loot search - covers the tower's full
    /// base-to-top climb path, not just its ground footprint, since the
    /// whole point is keeping other bots from converging on the same
    /// structure while one's already climbing it. Deliberately kept
    /// tight (was 40f, dropped to match CardReaderAvoidRadius) per
    /// Lucas's own concern: too wide an exclusion risks swallowing
    /// unrelated nearby loot, or a fresh /lr.debug.spawnmany beach spawn
    /// landing inside it outright.
    /// </summary>
    private const float PowerlineOccupancyRadius = 20f;

    /// <summary>
    /// Finds the real powerline-tower structure (if any) physically at
    /// position, returning its own stable per-instance root position.
    /// Powerline towers aren't registered Rust monuments (confirmed via
    /// /lr.monument.where - no TerrainMeta.Path.Monuments entry, see
    /// MonumentRoutes.cs's own doc comment on why its authored route is
    /// keyed by GameObject name instead), so this can't use
    /// TryGetNearestMonument/MonumentInfo the way MonumentAvoidZones does.
    /// Every collider on a real powerline tower reports a "powerline_*"
    /// GameObject as its ultimate root ancestor (confirmed live via
    /// /lr.debug.nearby) - Transform.root gives that directly, matched by
    /// substring so this covers every real variant (powerline_a/b/c/d/...)
    /// per Lucas's own "any powerline monument" framing, not just
    /// powerline_a specifically.
    /// </summary>
    private bool TryFindPowerlineTowerRoot(Vector3 position, out Vector3 towerRootPosition)
    {
        Collider[] hits = Physics.OverlapSphere(position, PowerlineTowerProximityRadius, ~0, QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            Transform root = hit.transform.root;

            if (root.name.IndexOf("powerline", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                towerRootPosition = root.position;
                return true;
            }
        }

        towerRootPosition = default;
        return false;
    }

    /// <summary>
    /// How wide a sweep the cheap "is anything powerline-related even
    /// nearby" gate uses - deliberately NOT LootSearchRadius (50f). A
    /// real, severe live bug: the first version gated on the full
    /// LootSearchRadius with an unfiltered (~0) layer mask, and near
    /// powerline_a specifically - a large, dense, many-collider structure
    /// (MonumentRoutes.cs's own doc comment: "every collider on the
    /// Powerline tower reports 'powerline_a' as its ultimate parent") - a
    /// single 50m-radius all-layers OverlapSphere near it was expensive
    /// enough to synchronously stall a bot's very first ContinueLootTask
    /// cycle. Confirmed live: three bots spawned near powerline_a
    /// (FastBuilder8205, DirtyHunter4167, MadAK) never logged so much as
    /// their first "scanning" line - frozen from the moment they spawned.
    /// PowerlineOccupancyRadius (20f) plus a little approach buffer is
    /// all this gate actually needs to stay correct for candidates within
    /// a bot's real search range - it doesn't need LootSearchRadius's
    /// full reach, since a tower further out than this can't have any
    /// candidate within PowerlineOccupancyRadius of it that also falls
    /// inside this gate's sweep anyway in the cases that matter live.
    /// </summary>
    private const float PowerlineGateRadius = 25f;

    /// <summary>
    /// Cheap gate for GetOccupiedPowerlineTowers - whether ANY real
    /// powerline-tower geometry exists within radius of position at all,
    /// regardless of which instance or whether anyone's on it. A single
    /// OverlapSphere, not one per other survivor - if this comes back
    /// false, none of this bot's own loot candidates could plausibly be
    /// near a tower either, so the far more expensive per-survivor
    /// occupancy scan isn't worth paying for at all. See
    /// PowerlineGateRadius's own doc comment for why radius here is
    /// deliberately much smaller than LootSearchRadius.
    /// </summary>
    private bool IsAnyPowerlineTowerWithinRadius(Vector3 position, float radius)
    {
        Collider[] hits = Physics.OverlapSphere(position, radius, ~0, QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            if (hit.transform.root.name.IndexOf("powerline", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly List<Vector3> NoOccupiedPowerlineTowers = new();

    /// <summary>
    /// Every distinct powerline tower another currently-spawned survivor
    /// is physically at right now, as a small list of tower root
    /// positions - computed once per ContinueLootTask cycle (not once per
    /// candidate) and reused across all three candidate filters, same
    /// "compute the expensive check once, not per-candidate" shape
    /// hasNonRockMeleeTool already uses. Fixes a real live problem: with
    /// many survivors all searching simultaneously, multiple bots could
    /// independently target loot on/inside the same powerline tower,
    /// converge on it together, and each individually get stuck on its
    /// known-difficult geometry (see MonumentRoutes.cs's own doc comment -
    /// "took a full session of individual bug fixes"). No claim/release
    /// bookkeeping needed - this is a live check against real survivor
    /// positions every search cycle, same self-cleaning shape
    /// IsNearCardReader already uses.
    ///
    /// Real performance bug fixed same day it shipped: the very first
    /// version unconditionally scanned every OTHER survivor with its own
    /// Physics.OverlapSphere call (TryFindPowerlineTowerRoot), on every
    /// single ContinueLootTask cycle, for every bot - O(bots^2) physics
    /// queries per cycle. Confirmed live: with a 35-bot spawnmany batch,
    /// most bots effectively froze (only ~12 of 35 showed any movement/
    /// loot activity across a full minute of log). Fixed with a cheap
    /// early-exit: skip the whole per-survivor scan unless there's
    /// actually a powerline tower within THIS bot's own LootSearchRadius
    /// to begin with - one OverlapSphere instead of up to 34, for the
    /// overwhelming majority of cycles where no tower is anywhere nearby
    /// at all.
    /// </summary>
    private List<Vector3> GetOccupiedPowerlineTowers(Survivor self, Vector3 selfPosition)
    {
        if (!IsAnyPowerlineTowerWithinRadius(selfPosition, PowerlineGateRadius))
        {
            return NoOccupiedPowerlineTowers;
        }

        List<Vector3> occupied = new();

        foreach (Survivor other in _engine.SurvivorManager.GetAll())
        {
            if (other == self)
            {
                continue;
            }

            BasePlayer otherNpc = other.Player;

            if (otherNpc == null || otherNpc.IsDestroyed)
            {
                continue;
            }

            if (TryFindPowerlineTowerRoot(otherNpc.transform.position, out Vector3 towerRootPosition)
                && !occupied.Any(existing => Vector3.Distance(existing, towerRootPosition) < 1f))
            {
                occupied.Add(towerRootPosition);
            }
        }

        return occupied;
    }

    private bool IsNearOccupiedPowerlineTower(Vector3 position, List<Vector3> occupiedPowerlineTowers)
    {
        return occupiedPowerlineTowers.Any(towerPosition => Vector3.Distance(position, towerPosition) <= PowerlineOccupancyRadius);
    }

    /// <summary>
    /// How far out to avoid any loot near a real CardReader (green/blue/
    /// red keycard swipe) - a stopgap per Lucas's explicit request until
    /// real puzzle-solving exists (see the Monuments roadmap entry: a
    /// bot can't yet check its own keycard tier, walk to the reader, and
    /// swipe it). Without this, a bot repeatedly walks toward loot sitting
    /// behind a locked puzzle door it structurally cannot ever open right
    /// now, fails, and either gets stuck cycling through the normal stuck-
    /// recovery chain or burns through the ordinary (temporary)
    /// PoisonAreaNow cycle over and over on the same permanently-
    /// unreachable spot. 20m is Lucas's own number - generous enough to
    /// cover a whole puzzle room, not just the door itself.
    /// </summary>
    private const float CardReaderAvoidRadius = 20f;

    // CardReader's own collider sits on the "World" layer (confirmed via
    // a live /lr.debug.scan at the Ferry Terminal monument's puzzle room).
    private static readonly int CardReaderLayerMask = LayerMask.GetMask("World");

    /// <summary>
    /// Whether position is within CardReaderAvoidRadius of a real
    /// CardReader - deliberately NOT the temporary PoisonedZone mechanism
    /// (LootTaskState.PoisonedZones, which expires after
    /// PoisonedZoneDuration and is per-survivor memory only). This is a
    /// live check against real CardReader entities every single search
    /// cycle, for every survivor - functionally permanent ("indefinitely,
    /// for now" per Lucas's own framing) without needing any expiry or
    /// state to track at all, and shared automatically across every bot
    /// rather than each one having to independently rediscover the same
    /// unreachable puzzle room.
    /// </summary>
    private bool IsNearCardReader(Vector3 position)
    {
        Collider[] hits = Physics.OverlapSphere(position, CardReaderAvoidRadius, CardReaderLayerMask, QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            if (hit.GetComponentInParent<CardReader>() != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Alternate angles (degrees, around the container's own center) to
    /// try when the closest-side approach looks obstructed - 0 is the
    /// closest side itself (already tried before this list is consulted),
    /// the rest fan out to cover the remaining sides roughly evenly.
    /// </summary>
    private static readonly float[] AlternateApproachAngles = { 90f, -90f, 45f, -45f, 135f, -135f, 180f };

    /// <summary>
    /// A point just outside a container's own bounds, on a side that
    /// looks clear of solid obstruction - not always the side closest to
    /// fromPosition. The closest side is tried first (cheap, usually
    /// fine), but a live report showed bots repeatedly oscillating in
    /// place for over a minute near junkpile-style clutter, unable to
    /// close the final 1-2m of a walk - the closest side was often
    /// pointed straight into a rock/gravel/junk pile the survivor's
    /// current position happened to be on the wrong side of. Checking a
    /// handful of other angles around the container first (a cheap
    /// Linecast each, not a real path-planning search) and picking one
    /// with a clear line from the survivor's current position meaningfully
    /// reduces the odds of committing to a walk that dead-ends in mess,
    /// without needing genuine route-around-obstacles pathfinding - that
    /// remains real future work if this still isn't enough in practice.
    /// StartWalking's own stuck-detection is still the final safety net
    /// either way.
    /// </summary>
    // Real melee reach is short - BaseMelee.maxDistance defaults to 1.5f
    // and AttackEntity.effectiveRange to 1f (confirmed via decompiling
    // Assembly-CSharp.dll) - a real player stands close to swing, not
    // 0.6m+ back from the container's own bounds. Combined with
    // WaypointArriveDistance's own tolerance on top, the old 0.6f read as
    // roughly half a metre too far in a live test. Still enough standoff
    // for the bot's own collision radius, just not the extra padding a
    // real swing never needed.
    private const float ContainerApproachStandoffDistance = 0.25f;

    private Vector3 GetApproachPoint(BaseEntity entity, BasePlayer npc, float standoffDistance = ContainerApproachStandoffDistance)
    {
        Vector3 fromPosition = npc.transform.position;
        OBB bounds = entity.WorldSpaceBounds();

        Vector3 closestSide = ComputeApproachPoint(entity, bounds, fromPosition, standoffDistance);

        if (IsPathClear(fromPosition, closestSide))
        {
            return SnapApproachPointToNavMesh(npc, closestSide);
        }

        foreach (float angle in AlternateApproachAngles)
        {
            Vector3 rotatedReference = RotatePointAround(fromPosition, bounds.position, angle);
            Vector3 candidate = ComputeApproachPoint(entity, bounds, rotatedReference, standoffDistance);

            if (IsPathClear(fromPosition, candidate))
            {
                return SnapApproachPointToNavMesh(npc, candidate);
            }
        }

        // Nothing looked clear from any angle - fall back to the closest
        // side anyway rather than refusing to try at all. StartWalking's
        // stuck-detection (onFailed) still catches a genuinely bad pick.
        return SnapApproachPointToNavMesh(npc, closestSide);
    }

    // Tight-then-generous two-tier radius, exactly matching real Scientist2
    // AI's own destination-picking pattern (confirmed via decompiling
    // Assembly-CSharp.dll's State_ScientistRush.GetMoveDestination).
    private const float ApproachPointNavMeshSnapTightRadius = 3.5f;
    private const float ApproachPointNavMeshSnapWideRadius = 20f;

    // Real cross-layer snap guard (2026-08-28, Lucas's own live incident:
    // '198NumbWarden' tried to approach an ordinary surface tree, the wide-
    // radius sample below landed on a completely different navmesh layer -
    // a cave/tunnel system sitting Y=-40 directly underneath - it phased
    // down into that on the resulting stuck-recovery escalation, and was
    // killed there by a real Tunnel Dweller NPC before ever getting a
    // chance to recover). NavMesh.SamplePosition (what RustNavMeshAgent.
    // SamplePosition wraps) measures plain 3D Euclidean distance within
    // its radius sphere - it has no concept of "same walkable surface,"
    // so a generous 20m search radius can genuinely reach a vertically
    // stacked navmesh island (a tunnel, a basement, a cave ceiling/floor)
    // that happens to sit within that sphere even though it's nothing a
    // real player would ever consider "nearby." A real single footstep's
    // worth of vertical error is well under a couple of metres; anything
    // beyond this is treated as a different layer entirely, not a minor
    // ground-height correction.
    private const float ApproachPointMaxVerticalSnapDelta = 6f;

    /// <summary>
    /// Snaps a computed approach point onto the real baked navmesh
    /// (2026-08-15) - a real, precisely-identified gap found via decompile:
    /// GetApproachPoint's own geometry (bounds edge + standoff, obstruction-
    /// checked via Linecast) can pass every geometric check and still land
    /// somewhere the navmesh simply doesn't cover - a doorway threshold, a
    /// wall lip, a raised curb - which native RustNavMeshAgent pathing then
    /// rejects outright ("PathInvalid... no navmesh surface found within
    /// Xm"), a failure signature seen constantly across this session's live
    /// traces. Real Scientist2 NPCs never hand a raw target position to
    /// their own movement - every single destination gets run through
    /// RustNavMeshAgent.SamplePosition first (State_ScientistRush.
    /// GetMoveDestination: tight 3.5m radius, falling back to a generous
    /// 20m radius), using the SAME RustNavMeshAgent component this
    /// project's own bots already carry (Scientist2FSM itself declares
    /// RustNavMeshAgent as a SoftRequireComponent - confirmed via decompile
    /// this isn't a separate/exclusive system). This is that identical
    /// validation step, applied to every approach point this project
    /// computes, now with an added vertical sanity check (see
    /// ApproachPointMaxVerticalSnapDelta's own doc comment) that a plain
    /// distance-only sample can't provide on its own. Falls back to the
    /// original unsnapped point (not some invented fallback) if nothing
    /// real and same-level is found nearby at all - StartWalking's own
    /// stuck-recovery ladder remains the final safety net for a genuinely
    /// bad pick, same as before this existed.
    /// </summary>
    private Vector3 SnapApproachPointToNavMesh(BasePlayer npc, Vector3 worldPosition)
    {
        EnsureNativeNavAgent(npc, out RustNavMeshAgent agent, out _);

        Vector3 positionNS = npc.WorldToNavMeshSpace.MultiplyPoint(worldPosition);

        if (TryGetSameLevelNavMeshSnap(npc, agent, positionNS, worldPosition, ApproachPointNavMeshSnapTightRadius, out Vector3 tightSnap))
        {
            return tightSnap;
        }

        if (TryGetSameLevelNavMeshSnap(npc, agent, positionNS, worldPosition, ApproachPointNavMeshSnapWideRadius, out Vector3 wideSnap))
        {
            return wideSnap;
        }

        return worldPosition;
    }

    /// <summary>
    /// One radius attempt for SnapApproachPointToNavMesh above - splits out
    /// purely so the vertical-layer rejection (see
    /// ApproachPointMaxVerticalSnapDelta's own doc comment) applies
    /// identically to both the tight and wide radius passes, rather than
    /// only guarding the outer function's final return.
    /// </summary>
    private bool TryGetSameLevelNavMeshSnap(BasePlayer npc, RustNavMeshAgent agent, Vector3 positionNS, Vector3 originalWorldPosition, float radius, out Vector3 result)
    {
        result = default;

        if (!agent.SamplePosition(positionNS, out NavMeshHit hitNS, radius))
        {
            return false;
        }

        Vector3 candidateWorldPosition = npc.NavMeshToWorldSpace.MultiplyPoint(hitNS.position);

        if (Mathf.Abs(candidateWorldPosition.y - originalWorldPosition.y) > ApproachPointMaxVerticalSnapDelta)
        {
            return false;
        }

        result = candidateWorldPosition;
        return true;
    }

    /// <summary>
    /// The actual closest-point-on-bounds + standoff + real-ground-height
    /// computation, parameterized by whatever reference position "closest"
    /// is measured from - GetApproachPoint calls this once for the
    /// survivor's real position, then again for each rotated candidate
    /// angle it's trying.
    /// </summary>
    private Vector3 ComputeApproachPoint(BaseEntity entity, OBB bounds, Vector3 referencePosition, float standoffDistance)
    {
        Vector3 closest = bounds.ClosestPoint(referencePosition);

        Vector3 outward = closest - bounds.position;
        outward.y = 0f;

        if (outward.sqrMagnitude < 0.01f)
        {
            outward = referencePosition - entity.transform.position;
            outward.y = 0f;
        }

        if (outward.sqrMagnitude < 0.01f)
        {
            outward = Vector3.forward;
        }

        Vector3 approach = closest + outward.normalized * standoffDistance;

        // Real ground/floor height at this point via a downward raycast,
        // not the coarse terrain heightmap - which only ever reports raw
        // terrain elevation and would badly misplace this for a container
        // sitting on an elevated platform (a monument's upper level,
        // exactly the powerline_a case this originally fixed), same
        // reasoning TryFindGroundBelow's own doc comment gives.
        // approach.y is already ~= closest.y at this point (outward.y was
        // zeroed), a reasonable search-origin height for the raycast.
        if (_engine.NavigationManager.TryFindGroundBelow(approach, 4f, 6f, out float groundY, out _))
        {
            approach.y = groundY;
        }
        else
        {
            approach.y = closest.y;
        }

        return approach;
    }

    private static Vector3 RotatePointAround(Vector3 point, Vector3 pivot, float angleDegrees)
    {
        return pivot + Quaternion.Euler(0f, angleDegrees, 0f) * (point - pivot);
    }

    /// <summary>
    /// A cheap sanity check, not a real path guarantee - just whether a
    /// straight line between two points is free of solid obstruction, the
    /// same idea (and mask) as HasLineOfSight uses for the attack-range
    /// check, reused here to pre-screen candidate approach angles before
    /// ever committing a walk to one.
    /// </summary>
    private bool IsPathClear(Vector3 from, Vector3 to)
    {
        return !Physics.Linecast(from + Vector3.up * 0.5f, to + Vector3.up * 0.5f, LineOfSightBlockingMask, QueryTriggerInteraction.Ignore);
    }

    private void LootContainerAndContinue(Survivor survivor, StorageContainer container, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            survivor.Character.CurrentTask = TaskType.None;
            return;
        }

        if (container == null || container.IsDestroyed || container.inventory == null)
        {
            ContinueLootTask(survivor, state);
            return;
        }

        if (!RequiresDestructionToLoot(container))
        {
            LootContainerDirectly(survivor, container, state);
            return;
        }

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' is breaking open '{container.ShortPrefabName}'.");

        StartAttackingContainerWithReposition(
            survivor,
            container,
            onSuccess: () =>
            {
                state.ConsecutiveFailures = 0;
                AdvanceRetryCountdown(state);
                ContinueLootTask(survivor, state);
            },
            onFailed: () =>
            {
                // Fires well after StartAttackingContainerWithReposition
                // exhausts its repositioning attempts (real seconds
                // later) - the outer npc local can be stale by then (the
                // exact NRE crash trace this comment is fixing: survivor
                // died/respawned/despawned mid-attack, then this closure
                // touched a destroyed BasePlayer's .transform). Re-fetch
                // fresh instead of trusting the captured npc.
                BasePlayer liveNpc = survivor.Player;

                if (liveNpc == null || liveNpc.IsDestroyed)
                {
                    ContinueLootTask(survivor, state);
                    return;
                }

                RegisterLootFailure(survivor, state, liveNpc.transform.position, container);
                ContinueLootTask(survivor, state);
            });
    }

    /// <summary>
    /// Whether this container needs to be destroyed to get at its
    /// contents, matching real Rust behaviour - barrels and roadsigns
    /// genuinely have no interact-to-loot option and only drop their
    /// contents when broken with a tool; crates/boxes (crate_normal,
    /// crate_tools, crate_food_1/2, crate_ammunition, crate_mine,
    /// crate_elite, crate_fuel, crate_shore, crate_medical, foodbox,
    /// vehicle_parts, ...) are opened and looted intact, never attacked -
    /// user's explicit correction after scanning several crate types that
    /// should never be hit. Matched by shortname substring rather than an
    /// exhaustive list, since every crate variant seen so far shares
    /// neither "barrel" nor "roadsign" in its name and new crate types
    /// are far more likely to appear than new barrel types.
    /// </summary>
    private bool RequiresDestructionToLoot(StorageContainer container)
    {
        string name = container.ShortPrefabName;

        return name.IndexOf("barrel", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("roadsign", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Real roadsign HP is high enough that mining one with just the
    /// starting rock takes a genuinely long time - Lucas's own framing:
    /// it leaves the bot standing out in the open, exposed, for way
    /// longer than a barrel takes. Barrels stay rock-eligible (unchanged,
    /// HasAnyMeleeTool below still covers them); this is specifically a
    /// stricter gate for roadsigns only, requiring a real tool.
    /// </summary>
    private bool IsRoadsign(StorageContainer container)
    {
        return container.ShortPrefabName.IndexOf("roadsign", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// A vehicle/deployable's own fuel tank (modular car, minicopter,
    /// RHIB, snowmobile, DPV, submarine, a placed quarry/pump jack, ...) -
    /// a real StorageContainer, so it passes every other loot filter, but
    /// it's not free-standing world loot the way a barrel or crate is: a
    /// live report caught a survivor targeting one as if it were. Matched
    /// by shortname substring against Rust's real prefab naming (confirmed
    /// via the bundled AssetSceneManifest.json - "fuelstorage" for older
    /// prefabs like the quarry's tank, "fuel_storage" for every vehicle
    /// variant added since, e.g. modular_car_fuel_storage, fuel_storage_attackheli),
    /// same substring-matching approach as RequiresDestructionToLoot uses
    /// for barrels/crates.
    /// </summary>
    private bool IsVehicleFuelStorage(StorageContainer container)
    {
        string name = container.ShortPrefabName;

        return name.IndexOf("fuelstorage", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("fuel_storage", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// The hot air balloon's own attached loot container (real shortname
    /// "hab_storage", confirmed via a live /lr.debug.scan) - excluded the
    /// same way IsVehicleFuelStorage/IsVehiclePartsContainer are: it's a
    /// real StorageContainer so it'd otherwise pass every other loot
    /// filter, but it sits inside the balloon's own dense cage/gondola
    /// collider cluster (Cage, Corners, Entrance, GasCollider, a
    /// SnareTrigger, several Prevent_move zones - all confirmed via the
    /// same scan), which is exactly the kind of tightly-packed local
    /// geometry a cactus's own thin/jutting colliders already proved can
    /// wedge a bot in place (see IsBlockedByCactus's own doc comment).
    /// Removing the incentive to walk in there at all is the real fix -
    /// IsBlockedByHotAirBalloon below is a second, general safety net for
    /// a bot just passing near one for an unrelated reason.
    /// </summary>
    private bool IsHotAirBalloonStorage(StorageContainer container)
    {
        return container.ShortPrefabName.IndexOf("hab_storage", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// A rowboat's own cargo storage (real shortname "rowboat_storage",
    /// confirmed via AssetSceneManifest.json - shared across every real
    /// skin variant: Rowboat, MetalRowboat, and the weathered/beached
    /// "washed up" wreck version - all reuse the same SubEnts/
    /// rowboat_storage.prefab). Separate from the boat's own fuel tank,
    /// which IsVehicleFuelStorage already excludes. Lucas's own live
    /// report: a bot (BrokenRock) was attempting to loot a washed-up
    /// small "tinny" rowboat wreck - excluded the same way
    /// IsVehicleFuelStorage/IsVehiclePartsContainer/IsHotAirBalloonStorage
    /// are, a real StorageContainer that'd otherwise pass every other
    /// filter.
    /// </summary>
    private bool IsRowboatStorage(StorageContainer container)
    {
        return container.ShortPrefabName.IndexOf("rowboat_storage", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// A player-owned mailbox (real prefab "mailbox.deployed", confirmed
    /// via AssetSceneManifest.json) is a real StorageContainer that'd
    /// otherwise pass every other loot filter - excluded the same way
    /// IsVehicleFuelStorage/IsHotAirBalloonStorage/IsRowboatStorage are.
    /// It's base furniture tied to a specific player's ownership, not
    /// scavengeable loot.
    /// </summary>
    private bool IsMailbox(StorageContainer container)
    {
        return container.ShortPrefabName.IndexOf("mailbox", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Loots a container by opening it directly, the way a real player
    /// interacting with a crate would - no combat, no destruction. The
    /// container itself still needs to go away afterward the same way it
    /// would for a real player: LootContainer.PlayerStoppedLooting is
    /// what normally triggers destroyOnEmpty's auto-cleanup once someone
    /// closes the loot panel on an empty container, but nothing here ever
    /// opens a real loot panel, so that native cleanup never fires on its
    /// own - Kill() (default DestroyMode.None, not Gib - this is a closed
    /// container disappearing, not something broken) replicates it
    /// directly once actually empty.
    ///
    /// Delayed by DirectLootDelay rather than completing the instant it
    /// arrives - an instant transfer read as "inhuman" (user's own word)
    /// next to the barrel/roadsign combat loop, which naturally takes a
    /// few seconds of real swinging. No timer-cancellation bookkeeping
    /// for this delay (unlike movement/attacks, which get cancelled via
    /// _activeMovement/_activeAttacks) - the callback re-checks npc/
    /// container validity itself before touching either, so a survivor
    /// dying or the container vanishing mid-delay just falls through to
    /// ContinueLootTask harmlessly instead of needing to be cancelled.
    /// </summary>
    private void LootContainerDirectly(Survivor survivor, StorageContainer container, LootTaskState state, Action onDone = null)
    {
        Action resume = onDone ?? (() => ContinueLootTask(survivor, state));

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' is looting '{container.ShortPrefabName}'.");

        timer.Once(DirectLootDelay, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || container == null || container.IsDestroyed || container.inventory == null)
            {
                resume();
                return;
            }

            if (!IsWithinLootRange(npc, container))
            {
                // Nothing here previously verified actual proximity before
                // looting, only that a walk had reported "arrived" - a
                // live test caught a survivor looting a container 10-15m
                // away, stuck the whole time on the far side of a sandbag
                // wall. See LootInteractionRange's own doc comment.
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' can't reach '{container.ShortPrefabName}' - too far away ({Vector3.Distance(npc.transform.position, container.transform.position):F1}m, obstacle in the way?). Skipping it.");
                RegisterLootFailure(survivor, state, npc.transform.position, container);
                resume();
                return;
            }

            int moved = TransferAllItems(container.inventory, npc.inventory, npc, out List<string> movedShortnames);

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' looted {moved} item stack(s) from '{container.ShortPrefabName}' (opened, not destroyed): {string.Join(", ", movedShortnames)}.");

            if (moved > 0)
            {
                OnLootObtained(survivor, npc, movedShortnames);
            }

            if (container is LootContainer lootContainer && lootContainer.destroyOnEmpty && container.inventory.itemList.Count == 0)
            {
                container.Kill();
            }

            state.ConsecutiveFailures = 0;
            AdvanceRetryCountdown(state);
            resume();
        });
    }

    /// <summary>
    /// Loots a real corpse - a player, scientist/NPC, or animal death, the
    /// "rewarded for combat" case Lucas asked for. Paced one item at a
    /// time across every entry in LootableCorpse.containers (up to 3 real
    /// ItemContainers for a player corpse - main inventory, wear, belt) -
    /// see LootMultiContainerEntityAndContinue's own doc comment for the
    /// shared pacing logic.
    /// </summary>
    private void LootCorpseAndContinue(Survivor survivor, LootableCorpse corpse, LootTaskState state)
    {
        List<ItemContainer> containers = corpse.containers != null
            ? new List<ItemContainer>(corpse.containers)
            : new List<ItemContainer>();

        LootMultiContainerEntityAndContinue(survivor, corpse, containers, "a corpse", state);
    }

    /// <summary>
    /// Loots a real dropped bag - what a destroyed/despawned body (fire,
    /// explosives, or a corpse's own despawn timer) converts into. A
    /// completely separate class from both StorageContainer and
    /// LootableCorpse - see TryFindNearestDroppedItemContainer's own doc
    /// comment - but its actual loot content is functionally identical to
    /// a corpse's (a real player's dropped items), so it shares the exact
    /// same paced per-item loot logic via LootMultiContainerEntityAndContinue,
    /// just with a single real ItemContainer (DroppedItemContainer.inventory)
    /// instead of a corpse's array of up to 3.
    /// </summary>
    private void LootDroppedItemContainerAndContinue(Survivor survivor, DroppedItemContainer bag, LootTaskState state)
    {
        LootMultiContainerEntityAndContinue(survivor, bag, new List<ItemContainer> { bag.inventory }, "a dropped bag", state);
    }

    /// <summary>
    /// A standalone loose item (a fifth real loot-source class, see
    /// TryFindNearestDroppedItem's own doc comment) - Lucas's own explicit
    /// framing, 2026-08-14: "pickup everything... except a few specific
    /// items... prioritise them in the inventory," meaning this should use
    /// exactly the same real decision TryTransferSingleItem already makes
    /// for every item pulled from a corpse or bag (IsNeverLootItem/
    /// ShouldSkipDuplicateItem/ShouldSkipInferiorArmor), not a separate
    /// bespoke rule - and OnLootObtained afterward is the same real
    /// equip-priority pass that already decides whether a looted weapon/
    /// armor piece actually gets worn/wielded. No per-item pacing timer
    /// needed here (unlike LootMultiContainerEntityAndContinue) since
    /// there's only ever the one real Item to consider.
    /// </summary>
    private void PickupDroppedItemAndContinue(Survivor survivor, DroppedItem droppedItem, LootTaskState state, Action onDone = null)
    {
        Guid characterId = survivor.Character.Id;
        Action resume = onDone ?? (() => ContinueLootTask(survivor, state));

        CancelActiveAttack(characterId);

        float pickupDelay = DirectLootDelay;

        if (droppedItem?.item != null && IsBackpackItem(droppedItem.item))
        {
            // A backpack off the ground is a timed (3-5s) pickup, same as a
            // real player's (2026-09-21).
            pickupDelay += UnityEngine.Random.Range(3f, 5f);
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' is picking up a dropped backpack (takes a few seconds).");
        }

        timer.Once(pickupDelay, () =>
        {
            // See LootMultiContainerEntityAndContinue's own identical guard
            // for why this exact gap needs its own check - combat starting
            // during this delay has nothing registered yet to cancel it.
            if (_activeCombat.ContainsKey(characterId))
            {
                return;
            }

            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || droppedItem == null || droppedItem.IsDestroyed || droppedItem.item == null)
            {
                resume();
                return;
            }

            if (!IsWithinLootRange(npc, droppedItem))
            {
                // Real live gap found 2026-08-15 - this line never
                // identified WHAT the dropped item actually was or where,
                // so a live report of "it tried to loot something and
                // failed" was impossible to verify against what was
                // actually visible in-game. Now logs the real shortname
                // and position.
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' can't reach a dropped item ('{droppedItem.item.info.shortname}' at {droppedItem.transform.position}) - too far away ({Vector3.Distance(npc.transform.position, droppedItem.transform.position):F1}m, obstacle in the way?). Skipping it.");
                RegisterLootFailure(survivor, state, npc.transform.position);
                resume();
                return;
            }

            Item item = droppedItem.item;
            string shortname = item.info.shortname;

            if (TryTransferSingleItem(item, npc.inventory, npc))
            {
                // The real Item now belongs to the survivor's inventory -
                // RemoveItem() just clears the world entity's own reference
                // to it (doesn't touch the Item itself, matching the real
                // Pickup(RPCMessage) flow this mirrors), then the now-empty
                // world prop is cleaned up explicitly rather than left
                // behind as inert clutter.
                droppedItem.RemoveItem();
                droppedItem.Kill();

                VerbosePuts($"loot-task: '{survivor.Character.Alias}' picked up a dropped '{shortname}'.");
                OnLootObtained(survivor, npc, new List<string> { shortname });
            }
            else
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' left a dropped '{shortname}' - not worth picking up.");

                // Real live infinite loop (2026-08-28, Lucas's own report:
                // 'ScrappyWeasel' stuck endlessly re-detouring to the same
                // dropped pickaxe on a long walk). TryFindEnRouteLootCandidate
                // (below) already excludes anything RecentlyFailedEnRouteLoot,
                // and MarkEnRouteLootFailure already exists for exactly this
                // purpose - but was only ever called from the walk's own
                // onFailed (couldn't physically reach it). Reaching the item
                // and then rejecting it (TryTransferSingleItem returning
                // false - already holding something better, inventory full,
                // etc.) was never treated as a failure at all, so the exact
                // same dropped item stayed a valid en-route candidate
                // forever: detour to it, reject it, resume the original
                // walk, immediately re-spot the same item still sitting
                // right there, detour again. Marking it here breaks the
                // loop the same way an unreachable item already does - the
                // per-item state/detourState this fires from gets discarded
                // either way (see StartLongDistanceWalkDirect's own doc
                // comment on why detourState is throwaway), so this is the
                // only place a rejection can actually be remembered.
                MarkEnRouteLootFailure(droppedItem.net.ID);
            }

            state.ConsecutiveFailures = 0;
            AdvanceRetryCountdown(state);
            resume();
        });
    }

    /// <summary>
    /// Hemp, corn, pumpkin, mushroom, small stone/metal/sulfur surface
    /// deposits, fallen wood - all the same real CollectibleEntity class
    /// (see TryFindNearestCollectible's own doc comment), lowest priority
    /// of every loot source this project searches. DoPickup is CollectibleEntity's
    /// own real, public method (confirmed via decompile) - it already
    /// creates the right items, gives them to the receiver, plays the real
    /// pickup effect, and kills itself, so there's no manual item-transfer
    /// logic needed here the way DroppedItem's own pickup needed (no
    /// never-loot/duplicate/inferior-armor concept applies to raw gathered
    /// materials the way it does for a found weapon or armor piece).
    /// Shortnames captured before DoPickup runs since it nils out itemList
    /// as part of its own real cleanup.
    /// </summary>
    private void PickupCollectibleAndContinue(Survivor survivor, CollectibleEntity collectible, LootTaskState state, Action onDone = null)
    {
        Guid characterId = survivor.Character.Id;
        Action resume = onDone ?? (() => ContinueLootTask(survivor, state));

        CancelActiveAttack(characterId);

        timer.Once(DirectLootDelay, () =>
        {
            if (_activeCombat.ContainsKey(characterId))
            {
                return;
            }

            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || collectible == null || collectible.IsDestroyed || collectible.itemList == null || collectible.itemList.Length == 0)
            {
                resume();
                return;
            }

            if (!IsWithinLootRange(npc, collectible))
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' can't reach a collectible - too far away ({Vector3.Distance(npc.transform.position, collectible.transform.position):F1}m, obstacle in the way?). Skipping it.");
                RegisterLootFailure(survivor, state, npc.transform.position);
                resume();
                return;
            }

            List<string> shortnames = collectible.itemList
                .Where(itemAmount => itemAmount.itemDef != null)
                .Select(itemAmount => itemAmount.itemDef.shortname)
                .ToList();

            collectible.DoPickup(npc);

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' gathered from a collectible: {string.Join(", ", shortnames)}.");

            if (shortnames.Count > 0)
            {
                OnLootObtained(survivor, npc, shortnames);
            }

            state.ConsecutiveFailures = 0;
            AdvanceRetryCountdown(state);
            resume();
        });
    }

    // 20-30m, Lucas's own explicit range (2026-08-15): "if within LOS and
    // 20-30m away, loot stuff on the way" during a long-distance monument
    // walk. Deliberately smaller than LootSearchRadius (50m) - this is
    // "basically on the way," not a detour search.
    private const float EnRouteLootDetectionRadius = 25f;

    // Collectibles (berries, mushrooms, stone/wood deposits, ...) get their
    // own, much tighter en-route radius (2026-08-18, Lucas's own explicit
    // request) - unlike a real container or a dropped item, a collectible
    // is low-value enough that it's only worth a detour when it's
    // genuinely right next to the path, not merely within LOS 25m away.
    // Containers/dropped items still use EnRouteLootDetectionRadius above,
    // unchanged. This is now just the outer search ceiling (the widest any
    // single type is allowed - see GetCollectibleDivertRadius) - the real
    // per-type cutoff (berries 3m, mushrooms 5m, real resources 15m) is
    // enforced per-candidate in the filter below, same split this radius
    // used to handle alone with one flat number.
    private const float EnRouteCollectibleDetectionRadius = 15f;

    // How often a long walk re-checks for something worth grabbing nearby -
    // every tick would be wasteful (a fresh set of physics queries per bot
    // per WalkTickInterval at 200-bot scale), a periodic sweep is plenty
    // for something that's only ever "was there something on the way,"
    // never time-critical.
    private const float EnRouteLootScanIntervalSeconds = 4f;

    // Short and deliberately skips the full stuck-recovery ladder, same
    // reasoning as CollectibleWalkTimeoutSeconds - an en-route detour that
    // turns out to be awkward to actually reach isn't worth fighting the
    // geometry over when the bot was already headed somewhere specific.
    private const float EnRouteLootWalkTimeoutSeconds = 6f;

    // Real tree/ore node en-route gathering (2026-09-15, Lucas's own
    // explicit spec, first floated 2026-09-07: "have bots divert their
    // course... by farming a stone ore (along the way, if within 20 metres
    // at anypoint during its run to X destination) once... and farming a
    // tree or two", confirmed feasible then, actually built now). Own
    // radius, Lucas's own literal figure - deliberately tighter than
    // EnRouteLootDetectionRadius (25m, real loot) since a full node/tree
    // gather is a genuinely bigger time investment than grabbing a
    // container, so it should only trigger when one is truly right next to
    // the path, not merely nearby.
    private const float EnRouteResourceNodeDetectionRadius = 20f;

    // "just stop at 1 or 2 of each stone ore or tree along its journey... I
    // don't want them stopping at every stone ore along the way and
    // certainly not every tree" - Lucas's own explicit cap, enforced
    // per-walk (reset in StartLongDistanceWalk, NOT StartLongDistanceWalkDirect -
    // the latter is also what every en-route detour's own resumeOriginalWalk
    // calls back into, so resetting there would silently uncap this every
    // single stop instead of across the whole journey).
    private const int EnRouteResourceNodeMaxStopsPerWalk = 2;

    private readonly Dictionary<Guid, int> _enRouteTreeStopsThisWalk = new();
    private readonly Dictionary<Guid, int> _enRouteOreStopsThisWalk = new();

    /// <summary>
    /// How long a failed en-route detour target stays excluded from
    /// re-selection (2026-08-15) - live trace found '8319DirtyLooter' stuck
    /// in a genuine infinite loop: every EnRouteLootScanIntervalSeconds
    /// (4s), the scan re-picked the EXACT SAME unreachable candidate it had
    /// just spent EnRouteLootWalkTimeoutSeconds (6s) failing to reach,
    /// detoured to it again, failed again, forever - net zero forward
    /// progress toward the real destination, reading as "gave up halfway"
    /// even though it never actually gave up, just churned on the same
    /// unreachable item indefinitely. The throwaway detourState passed to
    /// PoisonAreaNow inside the detour is discarded every single detour (a
    /// fresh LootTaskState each time), and TryFindEnRouteLootCandidate
    /// never took any state at all, so nothing was remembering the failure
    /// from one scan tick to the next. Long enough that the bot has clearly
    /// moved on past this spot before the item's eligible again; short
    /// enough that a genuinely temporary obstruction (another bot standing
    /// on it, momentary claim contention) doesn't exclude it forever.
    /// </summary>
    private const float EnRouteLootFailureCooldownSeconds = 90f;

    private readonly Dictionary<NetworkableId, float> _enRouteLootFailures = new();

    private bool RecentlyFailedEnRouteLoot(NetworkableId id)
    {
        if (_enRouteLootFailures.TryGetValue(id, out float expiresAt))
        {
            if (expiresAt > UnityEngine.Time.realtimeSinceStartup)
            {
                return true;
            }

            _enRouteLootFailures.Remove(id);
        }

        return false;
    }

    private void MarkEnRouteLootFailure(NetworkableId id)
    {
        _enRouteLootFailures[id] = UnityEngine.Time.realtimeSinceStartup + EnRouteLootFailureCooldownSeconds;
    }

    private readonly Dictionary<Guid, Timer> _enRouteLootScanTimers = new();

    private void StopEnRouteLootScan(Guid characterId)
    {
        if (_enRouteLootScanTimers.TryGetValue(characterId, out Timer scanTimer))
        {
            scanTimer?.Destroy();
            _enRouteLootScanTimers.Remove(characterId);
        }
    }

    // How many points to sample along the straight line between current
    // position and a candidate long-distance destination, checking each
    // for water - deliberately coarse (not every metre), this only needs
    // to catch a real crossing (a river, bay, stretch of ocean between the
    // survivor and the destination), not graze detection of a single wet
    // footstep.
    private const int WaterCrossingSampleCount = 12;

    /// <summary>
    /// Whether the straight line from -> to passes through water at any
    /// sampled point - the trigger for routing via roads instead of
    /// walking the direct line (2026-08-15, Lucas's own explicit request:
    /// "is there water in between me and the destination I am trying to
    /// go to? yes, go around and follow the roads"). Deliberately a
    /// straight-line heuristic, not real water-body geometry - cheap, and
    /// good enough to catch the common case (a bay/river/inlet directly
    /// between here and there) without needing a real flood-fill or
    /// shoreline polygon lookup.
    /// </summary>
    private bool DoesPathCrossWater(Vector3 from, Vector3 to)
    {
        for (int i = 1; i < WaterCrossingSampleCount; i++)
        {
            Vector3 sample = Vector3.Lerp(from, to, i / (float)WaterCrossingSampleCount);

            if (_engine.NavigationManager.IsWater(sample))
            {
                return true;
            }
        }

        return false;
    }

    // How far to search for a road to route via, once a water crossing is
    // detected - deliberately generous (this is for real cross-map travel,
    // not just "is there a road nearby"), and separate from
    // RoadSearchDetectionRadius (that one's for the loot-search escalation,
    // a different concern with a much tighter radius).
    private const float RoadRouteDetectionRadius = 300f;

    // How far to advance along the road spline per hop while routing
    // around water - large enough that a real road-routed trip makes
    // genuine progress each hop rather than crawling.
    private const float RoadRouteHopDistance = 80f;

    // Hard cap on road hops before just accepting the direct route anyway,
    // water or not - a pure termination guarantee (see
    // MaxDistanceDecisionRerolls's own doc comment for the same reasoning
    // elsewhere in this project) for the rare case a destination genuinely
    // can't be reached by hopping along this particular road network (an
    // island monument, a road that dead-ends short of clearing the water).
    private const int RoadRouteMaxHops = 15;

    /// <summary>
    /// Routes a survivor around a water crossing by hopping along the
    /// nearest road network instead of walking the water-crossing straight
    /// line, re-checking after every hop whether the remaining direct line
    /// to finalDestination has cleared - the moment it has, breaks off onto
    /// the normal direct approach rather than needlessly following the
    /// road all the way to wherever it happens to end. Each hop advances
    /// toward whichever direction along the spline (forward or backward)
    /// is actually closer to finalDestination, so this naturally curves
    /// toward the real target rather than committing to one direction
    /// blindly.
    /// </summary>
    private void StartRoadRouteToward(Survivor survivor, Vector3 finalDestination, string destinationLabel, PathInterpolator road, float distanceAlongRoad, Action onArrived, Action onFailed, int hopsRemaining)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            onFailed?.Invoke();
            return;
        }

        if (hopsRemaining <= 0)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' exhausted its road-routing hops trying to get around water toward {destinationLabel} - continuing the direct route regardless.");
            StartLongDistanceWalkDirect(survivor, finalDestination, onArrived, onFailed);
            return;
        }

        if (!DoesPathCrossWater(npc.transform.position, finalDestination))
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' has a clear line to {destinationLabel} now - breaking off the road route for the direct approach.");
            StartLongDistanceWalkDirect(survivor, finalDestination, onArrived, onFailed);
            return;
        }

        float forwardDistance = Mathf.Min(distanceAlongRoad + RoadRouteHopDistance, road.Length);
        float backwardDistance = Mathf.Max(distanceAlongRoad - RoadRouteHopDistance, 0f);

        Vector3 forwardPoint = road.GetPoint(forwardDistance);
        Vector3 backwardPoint = road.GetPoint(backwardDistance);

        bool goForward = Vector3.Distance(forwardPoint, finalDestination) <= Vector3.Distance(backwardPoint, finalDestination);
        Vector3 nextPoint = goForward ? forwardPoint : backwardPoint;
        float nextDistanceAlongRoad = goForward ? forwardDistance : backwardDistance;

        StartWalkingWithRecovery(
            survivor,
            nextPoint,
            onArrived: () => StartRoadRouteToward(survivor, finalDestination, destinationLabel, road, nextDistanceAlongRoad, onArrived, onFailed, hopsRemaining - 1),
            onFailed: () =>
            {
                // Couldn't reach this particular hop - rather than getting
                // stuck retrying the same road point, just fall through to
                // the direct route from wherever the survivor currently is.
                // The direct walk's own water-depth/distance safety net
                // (WaterAvoidMaxDistance/IsTooDeepUnderwater) still applies
                // if this really does run back into water.
                BasePlayer liveNpc = survivor.Player;

                if (liveNpc == null || liveNpc.IsDestroyed)
                {
                    onFailed?.Invoke();
                    return;
                }

                StartLongDistanceWalkDirect(survivor, finalDestination, onArrived, onFailed);
            });
    }

    /// <summary>
    /// Entry point for every long-distance walk in the project (the
    /// initial gear-weighted destination, monument-zone escalation,
    /// distant-monument escalation). The road-routing detour
    /// (StartRoadRouteToward, DoesPathCrossWater) is DISABLED as of
    /// 2026-08-15 (later same session) - its very first live test caught
    /// a real bug (confirmed via trace: '641ShadyReaper' cycling between
    /// different nearby hop targets multiple times per SECOND, far faster
    /// than any real travel could complete - a bad interaction in the
    /// forward/backward hop-selection logic, not yet root-caused), and
    /// very likely explains a live report of "a lot of bots stuck around
    /// power_sub_big_1" (19 different bots all converged on that exact
    /// monument via EscalateSearchToKnownMonument in the same run). Left
    /// in place (not deleted) in case it's worth debugging properly later,
    /// but bypassed for now - Lucas's own explicit follow-up call: real
    /// swimming instead of routing around water is "honestly better than
    /// just making everything pathfind around an obvious gap." Water
    /// crossings are now just walked through directly (see
    /// WaterAvoidMaxDistance/IsTooDeepUnderwater's own updated doc
    /// comments) rather than turned back OR routed around.
    /// </summary>
    private void StartLongDistanceWalk(Survivor survivor, Vector3 destination, string destinationLabel, Action onArrived, Action onFailed)
    {
        // Real per-walk reset (2026-09-15) - see EnRouteResourceNodeMaxStopsPerWalk's
        // own doc comment for why this has to happen HERE specifically, not
        // in StartLongDistanceWalkDirect.
        Guid characterId = survivor.Character.Id;
        _enRouteTreeStopsThisWalk[characterId] = 0;
        _enRouteOreStopsThisWalk[characterId] = 0;

        StartLongDistanceWalkDirect(survivor, destination, onArrived, onFailed);
    }

    /// <summary>
    /// Drop-in replacement for StartWalkingWithRecovery at the specific
    /// long-distance call sites (the initial gear-weighted destination,
    /// monument-zone escalation, distant-monument escalation) - the walk
    /// itself is completely unchanged, this just runs a periodic en-route
    /// scan alongside it (2026-08-15, Lucas's own explicit request: bots
    /// walked straight past real loot sitting right next to the path on
    /// a long monument trip, only ever searching once they'd fully
    /// arrived). Not used for every walk in the project - a short local
    /// hop to a container the bot already decided on doesn't need this,
    /// only the genuinely long trips where "something was basically on
    /// the way" can plausibly happen.
    /// </summary>
    private void StartLongDistanceWalkDirect(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed)
    {
        Guid characterId = survivor.Character.Id;

        StartWalkingWithRecovery(
            survivor,
            destination,
            onArrived: () =>
            {
                StopEnRouteLootScan(characterId);
                onArrived?.Invoke();
            },
            onFailed: () =>
            {
                StopEnRouteLootScan(characterId);
                onFailed?.Invoke();
            });

        StopEnRouteLootScan(characterId);

        Timer scanTimer = null;

        scanTimer = timer.Every(EnRouteLootScanIntervalSeconds, () =>
        {
            BasePlayer npc = survivor.Player;

            // No longer actually walking (arrived/failed/interrupted by
            // combat/flee/despawn) - nothing left to scan alongside.
            if (npc == null || npc.IsDestroyed || !_activeMovement.ContainsKey(characterId)
                || _activeCombat.ContainsKey(characterId) || _activeFlee.ContainsKey(characterId))
            {
                StopEnRouteLootScan(characterId);
                return;
            }

            if (!TryFindEnRouteLootCandidate(survivor, npc, out BaseEntity candidate, out EnRouteLootKind kind))
            {
                return;
            }

            StopEnRouteLootScan(characterId);

            // Resumes via the direct walk, not the full water-checking
            // StartLongDistanceWalk - the water-crossing decision was
            // already made once for this whole trip, and a short en-route
            // detour resuming from nearly the same spot doesn't need to
            // re-litigate it. Also deliberately does NOT reset the
            // tree/ore stop counters (see EnRouteResourceNodeMaxStopsPerWalk's
            // own doc comment) - this is a resume of the SAME walk, not a
            // new one.
            Action resumeOriginalWalk = () => StartLongDistanceWalkDirect(survivor, destination, onArrived, onFailed);

            // Tree/ore get their own dedicated gather-and-resume path
            // (full node clear via StartGatheringResourceNode, same as the
            // normal loot-task fallback uses) rather than the generic
            // walk-up-and-pick-up flow below every other kind shares - see
            // GatherEnRouteTreeAndResume/GatherEnRouteOreAndResume's own
            // doc comments.
            if (kind == EnRouteLootKind.Tree)
            {
                _enRouteTreeStopsThisWalk[characterId] = _enRouteTreeStopsThisWalk.GetValueOrDefault(characterId) + 1;
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' spotted a tree on the way - stopping to gather wood ({_enRouteTreeStopsThisWalk[characterId]}/{EnRouteResourceNodeMaxStopsPerWalk} this trip).");
                GatherEnRouteTreeAndResume(survivor, (TreeEntity)candidate, resumeOriginalWalk);
                return;
            }

            if (kind == EnRouteLootKind.OreNode)
            {
                _enRouteOreStopsThisWalk[characterId] = _enRouteOreStopsThisWalk.GetValueOrDefault(characterId) + 1;
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' spotted a stone ore node on the way - stopping to mine it ({_enRouteOreStopsThisWalk[characterId]}/{EnRouteResourceNodeMaxStopsPerWalk} this trip).");
                GatherEnRouteOreAndResume(survivor, (OreResourceEntity)candidate, resumeOriginalWalk);
                return;
            }

            VerbosePuts($"loot-task: '{survivor.Character.Alias}' spotted something worth grabbing on the way - detouring.");

            // Throwaway state, purely so the existing Loot*AndContinue
            // machinery (RegisterLootFailure/PoisonAreaNow, etc.) has
            // somewhere to record a failed detour attempt - discarded once
            // this detour resolves, never carried into the resumed walk.
            LootTaskState detourState = new();

            StartWalking(
                survivor,
                GetApproachPoint(candidate, npc),
                onArrived: () =>
                {
                    switch (kind)
                    {
                        case EnRouteLootKind.Container:
                            LootContainerDirectly(survivor, (StorageContainer)candidate, detourState, resumeOriginalWalk);
                            break;
                        case EnRouteLootKind.DroppedItem:
                            PickupDroppedItemAndContinue(survivor, (DroppedItem)candidate, detourState, resumeOriginalWalk);
                            break;
                        case EnRouteLootKind.Collectible:
                            PickupCollectibleAndContinue(survivor, (CollectibleEntity)candidate, detourState, resumeOriginalWalk);
                            break;
                    }
                },
                onFailed: () =>
                {
                    MarkEnRouteLootFailure(candidate.net.ID);
                    resumeOriginalWalk();
                },
                maxSeconds: EnRouteLootWalkTimeoutSeconds);
        });

        _enRouteLootScanTimers[characterId] = scanTimer;
    }

    private enum EnRouteLootKind
    {
        Container,
        DroppedItem,
        Tree,
        OreNode,
        Collectible,
    }

    /// <summary>
    /// Real collectable prefabs (confirmed via Bundles\AssetSceneManifest.
    /// json - Rose-Collectable, Orchid-Collectable, Sunflower-Collectable,
    /// Wheat-Collectable, all under autospawn/collectable) Lucas asked to
    /// skip entirely (2026-08-15): "avoid picking flowers of any kind, and
    /// sunflowers... avoid picking up wheat collectable also," no reasoning
    /// given. Matched by ShortPrefabName substring, case-insensitive, same
    /// approach RequiresDestructionToLoot/IsRoadsign already use for their
    /// own shortname matching - "rose"/"orchid" cover "flowers of any
    /// kind" as currently modeled (those are the two real flower
    /// collectable types that exist).
    /// </summary>
    private static bool IsExcludedCollectibleType(CollectibleEntity candidate)
    {
        string name = candidate.ShortPrefabName;

        return name.IndexOf("rose", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("orchid", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("sunflower", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("wheat", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// How far a specific collectible TYPE is worth diverting for
    /// (2026-08-18, Lucas's own explicit figures) - berries are common
    /// enough/low-value enough to only matter within 3m, mushrooms 5m,
    /// and the real gathering-tier resources (hemp/wood/stone/metal ore/
    /// sulfur ore) get a wider 15m since they're worth a small detour.
    /// Matched by ShortPrefabName substring, same approach
    /// IsExcludedCollectibleType already uses - real confirmed prefab
    /// names via AssetSceneManifest.json: Berry-Red/Blue/Green/Black/
    /// White/Yellow-Collectable, Mushroom-Cluster-5/6, Hemp-Collectable,
    /// Wood-Collectable, Metal-Collectable, Stone-Collectable,
    /// Sulfur-Collectable. Anything not explicitly named (diesel fuel
    /// included - not covered by Lucas's own list) defaults to the wider
    /// 15m tier rather than a narrow one, on the assumption an
    /// unclassified collectible is more likely a genuine resource than a
    /// low-value berry - flag if that default is wrong for something
    /// specific.
    /// </summary>
    private static float GetCollectibleDivertRadius(CollectibleEntity candidate)
    {
        string name = candidate.ShortPrefabName;

        if (name.IndexOf("berry", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 3f;
        }

        if (name.IndexOf("mushroom", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 5f;
        }

        return 15f;
    }

    // How much wood a survivor is allowed to passively stockpile via
    // en-route gathering BEFORE it has rolled a real base design - once a
    // design IS rolled, the design's own real cost (+BaseGatherResourceBuffer,
    // the identical target TryPursueBaseGatherGoal itself uses) becomes the
    // real ceiling instead, so this flat number only matters pre-design
    // (still on the primitive checklist, or between lives). 2026-09-19,
    // Lucas's own explicit number-free framing ("quite overkill") - chosen
    // as a generous-but-real cap for whatever the checklist/early crafting
    // might still need, not a hard gameplay requirement.
    private const int EnRoutePreDesignWoodCap = 1500;

    /// <summary>
    /// See EnRouteTreeStopsThisWalk's own call site doc comment
    /// (TryFindEnRouteLootCandidate) - stops an en-route tree stop from
    /// padding wood indefinitely once the survivor already has enough for
    /// whatever it's actually working toward.
    /// </summary>
    private bool HasEnoughWoodAlready(Survivor survivor, BasePlayer npc)
    {
        int have = ItemManager.FindItemDefinition(WoodShortname) is ItemDefinition woodDef
            ? npc.inventory.GetAmount(woodDef.itemid)
            : 0;

        if (_rolledBaseDesign.TryGetValue(survivor.Character.Id, out (string Tier, string DesignPath, Dictionary<string, int> Cost) rolled)
            && rolled.Cost != null
            && rolled.Cost.TryGetValue(WoodShortname, out int designWoodCost))
        {
            return have >= designWoodCost + BaseGatherResourceBuffer;
        }

        return have >= EnRoutePreDesignWoodCap;
    }

    /// <summary>
    /// Deliberately narrow compared to the main local search
    /// (ContinueLootTask) - only non-destructible containers, standalone
    /// dropped items, and collectibles (mushroom/hemp/stone/etc). Barrels/
    /// roadsigns (destruction takes real time) and corpses (multi-item,
    /// safe-zone nuance) are excluded on purpose - "grab it on the way"
    /// means a quick, casual pickup a real player would make without
    /// stopping to work for it, not a full detour project.
    /// </summary>
    private bool TryFindEnRouteLootCandidate(Survivor survivor, BasePlayer npc, out BaseEntity candidate, out EnRouteLootKind kind)
    {
        if (_engine.NavigationManager.TryFindNearestLootContainer(
            npc.transform.position,
            EnRouteLootDetectionRadius,
            out StorageContainer container,
            c => !IsLootTargetClaimed(c.net.ID)
                && !RecentlyFailedEnRouteLoot(c.net.ID)
                && c.inventory != null
                && c.inventory.itemList.Count > 0
                // Confirmed live (2026-08-15) - a real infinite loop:
                // 'FilthyNomad'/'JitteryReaper' both got stuck endlessly
                // detouring to the same 'vehicle_parts' crate, "looting"
                // it, then immediately treating it as a fresh candidate
                // again seconds later. Root cause: itemList.Count > 0 alone
                // doesn't mean anything actually TRANSFERS - a container
                // made entirely of NeverLootShortnames-excluded items (e.g.
                // vehicle_parts' engine components) always transfers 0
                // items, so it never empties and never stops qualifying as
                // a candidate. This check requires at least one item that
                // would actually be taken before it's worth a detour at
                // all - the main local search doesn't need this since a
                // container like this just gets walked up to and looted
                // for 0 items once, then naturally never revisited (no
                // detour-and-repeat cycle the way en-route candidates get
                // re-evaluated on every scan tick).
                && c.inventory.itemList.Any(item => !IsNeverLootItem(item.info.shortname))
                && !RequiresDestructionToLoot(c)
                && !IsNearJunkpileJVan(c.transform.position)
                && !IsUnreachableCrateOrBarrel(npc.transform.position, c)
                && !IsInMonumentAvoidZone(c.transform.position)
                && !IsBelowSafeLootDepth(c.transform.position)
                && !IsVehicleFuelStorage(c)
                && !IsHotAirBalloonStorage(c)
                && !IsRowboatStorage(c)
                && !IsMailbox(c)
                && !IsNearCardReader(c.transform.position)
                && HasLineOfSight(npc, c)))
        {
            candidate = container;
            kind = EnRouteLootKind.Container;
            return true;
        }

        if (_engine.NavigationManager.TryFindNearestDroppedItem(
            npc.transform.position,
            EnRouteLootDetectionRadius,
            out DroppedItem droppedItem,
            c => !IsLootTargetClaimed(c.net.ID)
                && !RecentlyFailedEnRouteLoot(c.net.ID)
                && !IsNeverLootItem(c.item.info.shortname)
                && !IsInMonumentAvoidZone(c.transform.position)
                && !IsBelowSafeLootDepth(c.transform.position)
                && !IsNearCardReader(c.transform.position)
                && !IsUnreachableDroppedItem(npc.transform.position, c.transform.position)
                && HasLineOfSight(npc, c)))
        {
            candidate = droppedItem;
            kind = EnRouteLootKind.DroppedItem;
            return true;
        }

        Guid enRouteCharacterId = survivor.Character.Id;

        // Trees checked before ore - matches this project's own existing
        // convention (see GatherTreeAndContinue's own doc comment, "wood is
        // the more universally needed resource") - and both checked before
        // Collectible so a real farmable node takes priority over a small
        // ground pickup when both happen to be nearby (2026-09-15, Lucas's
        // own explicit ask: "stone nodes should hold priority over
        // collectable entities"). Each gated on the per-walk cap and on
        // actually owning a real gather-capable tool - a toolless survivor
        // walking up to a node it can't harvest would just waste the
        // detour.
        //
        // HasEnoughWoodAlready (2026-09-19, Lucas's own explicit live
        // report: killed bots carrying "3000-8000 wood... quite overkill
        // for what the bot ACTUALLY needs to build a base") - this en-route
        // stop had zero awareness of what the survivor actually needed,
        // stacking a full tree's worth of wood on top of whatever it
        // already had, once per stop, for its entire life with no ceiling.
        // Now skips the stop entirely once already well-stocked - see its
        // own doc comment for the real target used.
        if (_enRouteTreeStopsThisWalk.GetValueOrDefault(enRouteCharacterId) < EnRouteResourceNodeMaxStopsPerWalk
            && !HasEnoughWoodAlready(survivor, npc)
            && HasAnyGatherCapableTool(npc, TreeGatherToolPriority)
            && IsResourceAllowedForHighGear(survivor, npc, WoodYieldForGate())
            && _engine.NavigationManager.TryFindNearestTreeEntity(
                npc.transform.position,
                EnRouteResourceNodeDetectionRadius,
                out TreeEntity tree,
                c => !IsLootTargetClaimed(c.net.ID)
                    && !RecentlyFailedEnRouteLoot(c.net.ID)
                    && !IsInMonumentAvoidZone(c.transform.position)
                    && !IsBelowSafeLootDepth(c.transform.position)
                    && !IsResourceNodePoisoned(c)))
        {
            candidate = tree;
            kind = EnRouteLootKind.Tree;
            return true;
        }

        if (_enRouteOreStopsThisWalk.GetValueOrDefault(enRouteCharacterId) < EnRouteResourceNodeMaxStopsPerWalk
            && HasAnyGatherCapableTool(npc, OreGatherToolPriority)
            && _engine.NavigationManager.TryFindNearestOreResourceEntity(
                npc.transform.position,
                EnRouteResourceNodeDetectionRadius,
                out OreResourceEntity ore,
                c => !IsLootTargetClaimed(c.net.ID)
                    && IsResourceAllowedForHighGear(survivor, npc, GetNodeYields(c))
                    && !RecentlyFailedEnRouteLoot(c.net.ID)
                    && !IsInMonumentAvoidZone(c.transform.position)
                    && !IsBelowSafeLootDepth(c.transform.position)
                    && !IsResourceNodePoisoned(c)))
        {
            candidate = ore;
            kind = EnRouteLootKind.OreNode;
            return true;
        }

        if (_engine.NavigationManager.TryFindNearestCollectible(
            npc.transform.position,
            EnRouteCollectibleDetectionRadius,
            out CollectibleEntity collectible,
            c => !IsLootTargetClaimed(c.net.ID)
                && !RecentlyFailedEnRouteLoot(c.net.ID)
                && c.itemList != null
                && c.itemList.Length > 0
                && IsResourceAllowedForHighGear(survivor, npc, c.itemList)
                && !IsInMonumentAvoidZone(c.transform.position)
                && !IsBelowSafeLootDepth(c.transform.position)
                && HasLineOfSight(npc, c)
                && !IsExcludedCollectibleType(c)
                && Vector3.Distance(npc.transform.position, c.transform.position) <= GetCollectibleDivertRadius(c)))
        {
            candidate = collectible;
            kind = EnRouteLootKind.Collectible;
            return true;
        }

        candidate = null;
        kind = default;
        return false;
    }

    /// <summary>
    /// How long each individual item takes to loot from a corpse or bag -
    /// Lucas's own real-player comparison: even auto-loot/quick-loot on a
    /// real corpse has a real per-item timer/latency (his estimate: ~0.1s),
    /// so an instant "everything transfers at once" read as unrealistic
    /// for something with potentially many items across multiple real
    /// containers. Starting at 0.2s per Lucas's own explicit "let's start
    /// there and see how we go" - tune from here, not a value confirmed
    /// correct yet.
    /// </summary>
    private const float CorpseLootPerItemDelay = 0.2f;

    /// <summary>
    /// Shared paced multi-container loot logic behind both
    /// LootCorpseAndContinue and LootDroppedItemContainerAndContinue - a
    /// corpse and a dropped bag are different entity types but the actual
    /// looting behaviour (real containers full of real items, no
    /// destruction needed, paced one item at a time) is identical, so
    /// this is generic over any entity + its real ItemContainer list.
    /// Left alone once empty rather than manually cleaned up - both
    /// corpses and bags already despawn on their own via Rust's own
    /// timers, same as they would after a real player looted them.
    ///
    /// Registered in _activeAttacks (same registry StartAttackingContainer
    /// uses) purely so this repeating timer gets torn down by the existing
    /// CancelActiveAttack cleanup path (death, despawnall, ...) - without
    /// that, a survivor removed mid-loot would leave this timer ticking
    /// against a gone Character forever.
    /// </summary>
    private void LootMultiContainerEntityAndContinue(Survivor survivor, BaseEntity entity, List<ItemContainer> containers, string entityLabel, LootTaskState state)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveAttack(characterId);

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' is looting {entityLabel}.");

        timer.Once(DirectLootDelay, () =>
        {
            // Real gap CancelActiveAttack can't reach - this initial
            // timer.Once isn't registered in _activeAttacks until the real
            // per-item lootTimer below is created, so combat starting
            // during this exact delay previously had nothing to cancel and
            // this callback would fire obliviously (confirmed via a live
            // log: "is looting a corpse" then "engaging ... in combat" then
            // "looted 0 item stack(s)" moments later, from the same
            // survivor). Bail here too, same as ContinueLootTask's own
            // entry guard.
            if (_activeCombat.ContainsKey(characterId))
            {
                return;
            }

            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || entity == null || entity.IsDestroyed)
            {
                ContinueLootTask(survivor, state);
                return;
            }

            if (!IsWithinLootRange(npc, entity))
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' can't reach {entityLabel} - too far away ({Vector3.Distance(npc.transform.position, entity.transform.position):F1}m, obstacle in the way?). Skipping it.");
                RegisterLootFailure(survivor, state, npc.transform.position);
                ContinueLootTask(survivor, state);
                return;
            }

            // Snapshot every item across every real container up front -
            // same reasoning TransferAllItems' own doc comment gives:
            // mutating a container's itemList while iterating it directly
            // would skip items.
            List<Item> pending = new();

            foreach (ItemContainer container in containers)
            {
                if (container != null)
                {
                    pending.AddRange(container.itemList);
                }
            }

            if (pending.Count == 0)
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' found {entityLabel} already empty - nothing to loot.");
                state.ConsecutiveFailures = 0;
                AdvanceRetryCountdown(state);
                ContinueLootTask(survivor, state);
                return;
            }

            int index = 0;
            int moved = 0;
            List<string> movedShortnames = new();
            Timer lootTimer = null;

            lootTimer = timer.Every(CorpseLootPerItemDelay, () =>
            {
                // See RunLootHookSafely's own doc comment - the exact
                // class of bug that fix addresses (an uncaught exception
                // silently freezing a survivor's whole task forever)
                // applies equally here: a repeating timer holding
                // references across real ticks, same shape as the
                // confirmed StartAttackingContainerWithReposition crash.
                try
                {
                    BasePlayer currentNpc = survivor.Player;

                    // Not gated on IsInventoryFull anymore - a full
                    // inventory should only skip whatever specific item
                    // can't fit (or can't earn its own room - see
                    // EnsureRoomFor), not end the whole loot session early
                    // while later items in the same corpse/bag might
                    // still be exactly the kind of upgrade worth making
                    // room for. See TransferAllItems' own identical
                    // reasoning for the bulk-loot equivalent of this.
                    if (currentNpc == null || currentNpc.IsDestroyed || index >= pending.Count)
                    {
                        lootTimer.Destroy();
                        _activeAttacks.Remove(characterId);

                        if (currentNpc == null || currentNpc.IsDestroyed)
                        {
                            return;
                        }

                        VerbosePuts($"loot-task: '{survivor.Character.Alias}' looted {moved} item stack(s) from {entityLabel}: {string.Join(", ", movedShortnames)}.");

                        if (moved > 0)
                        {
                            OnLootObtained(survivor, currentNpc, movedShortnames);
                        }

                        state.ConsecutiveFailures = 0;
                        AdvanceRetryCountdown(state);
                        ContinueLootTask(survivor, state);
                        return;
                    }

                    Item item = pending[index];
                    index++;

                    if (EnsureRoomFor(currentNpc, item) && TryTransferSingleItem(item, currentNpc.inventory, currentNpc))
                    {
                        moved++;
                        movedShortnames.Add(item.info.shortname);
                    }
                }
                catch (Exception exception)
                {
                    lootTimer.Destroy();
                    _activeAttacks.Remove(characterId);

                    Puts($"WARNING: '{survivor.Character.Alias}' - {entityLabel} loot tick threw and was aborted: {exception.Message}");

                    ContinueLootTask(survivor, state);
                }
            });

            _activeAttacks[characterId] = lootTimer;
        });
    }

    /// <summary>
    /// Repeatedly swings the survivor's actually-equipped melee tool at a
    /// container - real BaseMelee.ServerUse() calls, the same entry point
    /// a real player's client-sent attack RPC ultimately drives into, not
    /// a synthetic shortcut. This is what makes the swing animation and
    /// swing sound/VFX actually happen for observers (via
    /// BasePlayer.SignalBroadcast(Signal.Attack, ...), which ServerUse
    /// calls internally) - confirmed via decompiling Assembly-CSharp.dll,
    /// after establishing earlier that Rust's real melee combat is
    /// otherwise entirely client-driven (a real player's client decides
    /// when to swing) and that the server-driven alternative Rust's own
    /// AI NPCs use lives in NPCPlayer/HumanNPC, not plain BasePlayer -
    /// ServerUse() turned out to be a plain, public, connection-
    /// independent method callable directly, sidestepping both of those
    /// dead ends. ServerUse() also does its own real raycast hit-test
    /// (from the wielder's eyes, forward) and applies damage itself after
    /// a real swing delay - EquipBestMeleeTool/FaceDirection below exist
    /// to make sure that hit-test actually lands on the intended
    /// container, and TransferAllItems still runs every poll tick
    /// regardless of whether a hit actually lands this tick, so loot keeps
    /// coming out safely ahead of whenever the container actually dies -
    /// same reasoning as the old direct-Hurt() approach this replaces.
    ///
    /// A real player physically can't click faster than their held tool's
    /// own swing/reset animation allows - "hold click, animation begins,
    /// tool connects, animation resets, click again" (Lucas's own
    /// description) - and every hit used to land on a flat 1s tick
    /// regardless of which tool was equipped, reading as unnaturally fast
    /// against slower tools. BaseMelee.ServerUse() already tracks this for
    /// real: every real swing calls StartAttackCooldown(repeatDelay * 2f)
    /// using the actually-equipped item's own real AttackEntity.repeatDelay
    /// (a per-weapon value baked into each tool's prefab, not a constant -
    /// confirmed via decompiling AttackEntity, default 0.5f but overridden
    /// per item). Checking melee.HasAttackCooldown() before landing another
    /// hit reuses that exact same real per-weapon pacing instead of
    /// guessing at a single fixed interval for every tool.
    /// </summary>
    private void StartAttackingContainer(Survivor survivor, StorageContainer container, Action onSuccess, Action onFailed)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveAttack(characterId);

        EquipBestMeleeTool(survivor);

        int hits = 0;
        Timer attackTimer = null;

        attackTimer = timer.Every(AttackHitInterval, () =>
        {
            // Same combat-preemption guard as ContinueLootTask's own entry -
            // see that guard's doc comment for the exact race it closes.
            if (_activeCombat.ContainsKey(characterId))
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                return;
            }

            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || container == null || container.IsDestroyed || container.inventory == null)
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onFailed?.Invoke();
                return;
            }

            if (!HasLineOfSight(npc, container))
            {
                // GetApproachPoint only accounts for the container's own
                // bounds, not what's physically between it and wherever
                // the survivor started from - a container tucked just
                // behind a thin wall could compute an approach point that
                // "arrives" without ever actually walking around it (a
                // live test caught this: a bot destroyed two barrels
                // through a solid wall). Rather than let a swing land
                // through a wall, give up on this specific container
                // instead - it's already in Visited, so the task moves on
                // to the next one. Properly routing around the obstacle
                // to a reachable approach angle is real future work, not
                // something this fixes.
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' can't reach '{container.ShortPrefabName}' - no clear line of sight (wall in the way?). Skipping it.");

                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onFailed?.Invoke();
                return;
            }

            if (!IsWithinLootRange(npc, container))
            {
                // Line of sight alone isn't proximity - a live test caught
                // a survivor looting a container 10-15m away the whole
                // time, stuck on the far side of a sandbag wall it could
                // clearly see over. See LootInteractionRange's own doc
                // comment.
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' can't reach '{container.ShortPrefabName}' - too far away ({Vector3.Distance(npc.transform.position, container.transform.position):F1}m, obstacle in the way?). Skipping it.");

                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onFailed?.Invoke();
                return;
            }

            int moved = TransferAllItems(container.inventory, npc.inventory, npc, out List<string> movedShortnames);

            if (moved > 0)
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' looted {moved} item stack(s) from '{container.ShortPrefabName}': {string.Join(", ", movedShortnames)}.");

                OnLootObtained(survivor, npc, movedShortnames);

                // OnLootObtained's own reorganization pass now equips the
                // best WEAPON for display (EquipBestWeaponForDisplay), not
                // the melee tool - a live report caught this undoing the
                // EquipBestMeleeTool call at the top of this method mid-
                // swing, every tick loot actually transferred, leaving the
                // bot visibly holding its gun while "destroying" the
                // barrel with a GetToolDamage(null) fallback instead of
                // its real tool. Re-asserting the melee tool here restores
                // it before the swing logic below runs.
                EquipBestMeleeTool(survivor);
            }

            BaseMelee melee = npc.GetHeldEntity() as BaseMelee;

            if (melee != null && melee.HasAttackCooldown())
            {
                // Still mid-swing/reset for this exact tool's real
                // repeatDelay - a real player can't click faster than
                // their held tool's own animation allows. See this
                // method's doc comment.
                return;
            }

            hits++;

            // ServerUse_Strike (called from inside ServerUse after a real
            // swing delay) raycasts from the wielder's eyes forward - the
            // survivor needs to actually be looking at the container each
            // tick for that hit-test to land on it, not just have arrived
            // near it once.
            AimAtContainer(npc, container);

            // ServerUse() purely for its visual/audio side effects (the
            // swing animation via SignalBroadcast, plus the configured
            // swing sound/VFX) - not relied on for actual damage anymore.
            // ServerUse() also schedules its own real hit-test
            // (ServerUse_Strike) to fire later via Invoke(ServerUse_Strike,
            // aiStrikeDelay) - if left alone, that delayed hit-test
            // sometimes ALSO lands now that aim is fixed, applying its own
            // damage (scaled by the weapon's own npcDamageScale field,
            // applied unconditionally inside BaseMelee regardless of the
            // attacker's IsNpc status) on top of the controlled Hurt()
            // call below. A live trace showed exactly this: barrels
            // sometimes dying in one hit, inconsistently - whenever that
            // delayed native hit happened to also connect that tick.
            // Cancelling the scheduled invoke immediately after
            // triggering it keeps the animation/sound (both already fired
            // synchronously inside ServerUse() itself) while making sure
            // its delayed damage application never actually runs, so only
            // this method's own Hurt() call ever affects health. melee can
            // still be null here (bare-handed edge case - see
            // AttackDamagePerHit's doc comment) - ServerUse() only applies
            // when there's an actual tool to swing.
            if (melee != null)
            {
                melee.ServerUse();
                melee.CancelInvoke(melee.ServerUse_Strike);
            }

            // Cancelling ServerUse_Strike above also cancels the ONLY place
            // Rust's own hit impact FX/sound (Effect.server.ImpactEffect,
            // called from inside ServerUse_Strike - confirmed via
            // decompiling BaseMelee) ever gets triggered - ServerUse()
            // itself only plays the swing/swoosh, never the impact. Without
            // this, every single swing sounded and looked identical to a
            // clean miss, confirmed by ear in a live test even while damage
            // (via Hurt() below) was landing correctly. Firing the same
            // effect manually, using the same real BaseMelee.GetStrikeEffectPath
            // material lookup ServerUse_Strike itself uses, restores the
            // hit sound/VFX without reintroducing the double-damage bug the
            // cancel above exists to prevent.
            PlayMeleeImpactEffect(npc, melee, container);

            container.Hurt(GetToolDamage(melee), DamageType.Blunt, npc, useProtection: false);

            bool destroyed = container.IsDestroyed || container.Health() <= 0f;

            if (destroyed || hits >= MaxHitsPerContainer)
            {
                string message = destroyed
                    ? $"loot-task: '{survivor.Character.Alias}' broke open '{container.ShortPrefabName}'."
                    : $"loot-task: '{survivor.Character.Alias}' gave up trying to destroy '{container.ShortPrefabName}' after {MaxHitsPerContainer} hits (already looted what it had).";

                VerbosePuts(message);

                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onSuccess?.Invoke();
                return;
            }
        });

        _activeAttacks[characterId] = attackTimer;
    }

    /// <summary>
    /// Hits a real, melee-destructible wood door barricade (the actual
    /// game Barricade class, confirmed via decompile 2026-08-16) blocking
    /// a bot's path, exactly like a real player has to. Root cause behind
    /// what looked all night like a navmesh/pathing bug at a specific
    /// Abandoned Supermarket doorway: TryGetNextStep was correctly
    /// reporting Blocked the whole time - there really was solid,
    /// destructible geometry there, just invisible to every navmesh/
    /// ground-probe fix applied earlier, since none of those could ever
    /// be "wrong" about a real obstacle. Modeled directly on
    /// StartAttackingContainer's real swing loop (manual Hurt() call,
    /// ServerUse_Strike cancelled to avoid double damage, impact FX played
    /// manually) minus the inventory-transfer step, since a barricade has
    /// nothing to loot - just needs breaking so the collider disappears
    /// and normal movement can resume on its own next tick, with zero
    /// need for the bot to separately "remember" it destroyed anything.
    /// </summary>
    private void StartAttackingBarricade(Survivor survivor, Barricade barricade, Action onSuccess, Action onFailed)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveAttack(characterId);

        EquipBestMeleeTool(survivor);

        int hits = 0;
        Timer attackTimer = null;

        attackTimer = timer.Every(AttackHitInterval, () =>
        {
            if (_activeCombat.ContainsKey(characterId))
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                return;
            }

            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || barricade == null || barricade.IsDestroyed)
            {
                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onSuccess?.Invoke();
                return;
            }

            if (!HasLineOfSight(npc, barricade) || !IsWithinLootRange(npc, barricade))
            {
                VerbosePuts($"'{survivor.Character.Alias}' lost line of sight/range on the barricade blocking its way - giving up on breaking it.");

                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);
                onFailed?.Invoke();
                return;
            }

            BaseMelee melee = npc.GetHeldEntity() as BaseMelee;

            if (melee != null && melee.HasAttackCooldown())
            {
                return;
            }

            hits++;

            AimAtContainer(npc, barricade);

            if (melee != null)
            {
                melee.ServerUse();
                melee.CancelInvoke(melee.ServerUse_Strike);
            }

            PlayMeleeImpactEffect(npc, melee, barricade);

            barricade.Hurt(GetToolDamage(melee), DamageType.Blunt, npc, useProtection: false);

            bool destroyed = barricade.IsDestroyed || barricade.Health() <= 0f;

            if (destroyed || hits >= MaxHitsPerBarricade)
            {
                string message = destroyed
                    ? $"'{survivor.Character.Alias}' broke through a barricade blocking its way."
                    : $"'{survivor.Character.Alias}' gave up trying to break a barricade after {MaxHitsPerBarricade} hits.";

                Puts(message);

                attackTimer.Destroy();
                _activeAttacks.Remove(characterId);

                if (destroyed)
                {
                    onSuccess?.Invoke();
                }
                else
                {
                    onFailed?.Invoke();
                }

                return;
            }
        });

        _activeAttacks[characterId] = attackTimer;
    }

    /// <summary>
    /// Angles (degrees, off the straight line from npc to container) tried
    /// in turn once StartAttackingContainer gives up from the current spot
    /// (no line of sight, or out of real melee range). This is a different
    /// failure mode than EscalateStuckRecovery's wiggle/navmesh-nudge/
    /// teleport chain: the survivor isn't physically stuck (it walked to
    /// the approach point fine) - the approach ANGLE is bad, e.g. a barrel
    /// sitting just past a low wall or sandbag lip that GetApproachPoint's
    /// own simple bounds-standoff math didn't account for.
    ///
    /// Deliberately 45°/90° off the direct line, not a straight retreat -
    /// a live report on the separate (but analogous) generic wiggle tier
    /// caught it trying a pure 180° "back" step first, which just retraces
    /// the exact path the survivor arrived by and walks straight back into
    /// the identical block once retried. An angled offset changes the real
    /// approach LINE to the container, which a pure backward-then-forward
    /// retreat never does. 45° tried before 90° - Lucas's own framing
    /// ("maybe even a 45 degree angle, not a 90 degree") - since a wide
    /// swing still keeps some of the original, presumably-mostly-correct
    /// approach direction rather than discarding it outright.
    /// </summary>
    private static readonly float[] ContainerRepositionAngles = { 45f, -45f, 90f, -90f };

    /// <summary>
    /// How far along the rotated direction to walk for each reposition
    /// attempt - widened from an earlier 3-5f after a live report that the
    /// smaller distances weren't reliably clearing whatever was blocking
    /// the original line (Lucas's own estimate: "left or right 5-6
    /// metres").
    /// </summary>
    private const float ContainerRepositionDistance = 5.5f;

    /// <summary>
    /// Wraps StartAttackingContainer with the angled-reposition retries
    /// described by ContainerRepositionAngles before finally giving up on
    /// this container. Deliberately does NOT fall through to
    /// EscalateStuckRecovery/emergency teleport - Lucas's own framing was
    /// explicit that this case is "not physically stuck" (the walk here
    /// already succeeded), so a random relocation isn't the right tool;
    /// once every angle tried still can't see/reach the container, that's
    /// good evidence it's genuinely unreachable from ground level (e.g.
    /// raised on something the bot can't climb) rather than a solvable
    /// positioning problem, and the caller's existing "skip it, move on"
    /// handling (RegisterLootFailure) already does the right thing with
    /// that conclusion.
    /// </summary>
    private void StartAttackingContainerWithReposition(Survivor survivor, StorageContainer container, Action onSuccess, Action onFailed, int repositionAttempt = 0)
    {
        StartAttackingContainer(survivor, container, onSuccess, onFailed: () =>
        {
            BasePlayer npc = survivor.Player;

            // Checked FIRST, before touching container in any way below -
            // a live crash (NullReferenceException in container.transform,
            // reported via Carbon's "Timer ... has failed" log) confirmed
            // this container reference can go stale mid-callback: another
            // survivor (or this same one, on an earlier tick) can destroy
            // the exact container this reposition attempt is still holding
            // a reference to, between StartAttackingContainer's own
            // internal onFailed call and this closure actually running.
            // An uncaught exception here silently breaks the whole loot-task
            // callback chain - ContinueLootTask never gets called again -
            // which is consistent with a separate live report of a
            // survivor freezing mid-task while other survivors kept
            // working normally nearby.
            if (npc == null || npc.IsDestroyed || container == null || container.IsDestroyed)
            {
                onFailed?.Invoke();
                return;
            }

            if (repositionAttempt >= ContainerRepositionAngles.Length)
            {
                VerbosePuts($"'{survivor.Character.Alias}' tried {ContainerRepositionAngles.Length} different angles on '{container.ShortPrefabName}' and still can't reach/see it - not physically stuck, just genuinely unreachable from here. Giving up on it.");
                onFailed?.Invoke();
                return;
            }

            Vector3 towardContainer = container.transform.position - npc.transform.position;
            towardContainer.y = 0f;
            towardContainer = towardContainer.sqrMagnitude > 0.01f ? towardContainer.normalized : npc.transform.forward;

            Vector3 rotatedDirection = Quaternion.Euler(0f, ContainerRepositionAngles[repositionAttempt], 0f) * towardContainer;
            Vector3 candidate = npc.transform.position + rotatedDirection * ContainerRepositionDistance;

            // See StuckReassessPause's own doc comment - a real "let me try
            // a different angle" beat rather than an instant snap into the
            // next attempt.
            FaceDirection(npc, rotatedDirection);

            VerbosePuts($"'{survivor.Character.Alias}' couldn't reach/see '{container.ShortPrefabName}' from here - repositioning ({repositionAttempt + 1}/{ContainerRepositionAngles.Length}, {ContainerRepositionAngles[repositionAttempt]:F0}°) to try a different angle.");

            timer.Once(StuckReassessPause, () =>
            {
                BasePlayer reassessNpc = survivor.Player;

                if (reassessNpc == null || reassessNpc.IsDestroyed)
                {
                    onFailed?.Invoke();
                    return;
                }

                StartWalking(
                    survivor,
                    candidate,
                    onArrived: () => StartAttackingContainerWithReposition(survivor, container, onSuccess, onFailed, repositionAttempt + 1),
                    onFailed: () => StartAttackingContainerWithReposition(survivor, container, onSuccess, onFailed, repositionAttempt + 1));
            });
        });
    }

    /// <summary>
    /// Real per-hit damage from whatever tool is actually equipped -
    /// BaseMelee.damageTypes is a public field holding the same raw
    /// damage sum ServerUse_Strike itself reads before scaling it by the
    /// weapon's own opaque npcDamageScale (confirmed via decompiling
    /// Assembly-CSharp.dll). Reading it directly and applying it
    /// ourselves via the controlled Hurt() call sidesteps that scaling
    /// entirely, while still meaning a better tool (a real sword vs a
    /// rock) actually deals more damage here, not just looks different -
    /// matches the original ask that tool choice should matter for
    /// speed/efficiency, not only which animation plays. Falls back to
    /// AttackDamagePerHit only if there's no melee tool at all or it has
    /// no configured damage.
    /// </summary>
    private float GetToolDamage(BaseMelee melee)
    {
        if (melee == null || melee.damageTypes == null)
        {
            return AttackDamagePerHit;
        }

        float total = 0f;

        foreach (DamageTypeEntry entry in melee.damageTypes)
        {
            total += entry.amount;
        }

        return total > 0f ? total : AttackDamagePerHit;
    }

    /// <summary>
    /// Aims npc's eyes (both the visible body/movement-facing rotation
    /// AND the internal eyes.bodyRotation ServerUse_Strike's hit-test
    /// raycast actually reads) at container's real collision center -
    /// not container.transform.position, which for many prefabs is a
    /// base/pivot point rather than the visual/collision middle.
    ///
    /// Two distinct bugs fixed here, found via a live trace showing 20
    /// swings in a row with zero hits landing:
    ///
    /// 1. The previous aim flattened Y to zero (matching FaceDirection's
    ///    own convention, correct for movement/body-facing where a
    ///    walking bot shouldn't visibly tilt up/down) - but
    ///    ServerUse_Strike's raycast fires from eye height, dead level,
    ///    which sails clean over a low target like a barrel instead of
    ///    hitting its actual hitbox. This uses the real 3D direction,
    ///    pitch included, for the aim/eyes rotation specifically (body
    ///    rotation stays horizontal-only, so the model doesn't visibly
    ///    tilt).
    /// 2. OverrideViewAngles alone only ever sets the viewAngles field -
    ///    eyes.bodyRotation (what BasePlayer.eyes.BodyForward() actually
    ///    reads, confirmed via decompiling PlayerEyes) only gets
    ///    refreshed from viewAngles by Rust's own native per-tick
    ///    "active connected player" batch processing loop, which
    ///    disconnected bots never run through - so eyes.bodyRotation
    ///    just kept pointing wherever it last was, completely
    ///    disconnected from whatever OverrideViewAngles set. Calling
    ///    eyes.NetworkUpdate directly (the same call that native loop
    ///    itself makes) is what actually propagates it.
    /// </summary>
    private void AimAtContainer(BasePlayer npc, BaseEntity container)
    {
        Vector3 targetPoint = container.WorldSpaceBounds().position;
        Vector3 direction = targetPoint - npc.eyes.position;

        if (direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Quaternion aimRotation = Quaternion.LookRotation(direction);

        npc.transform.rotation = Quaternion.Euler(0f, aimRotation.eulerAngles.y, 0f);
        npc.OverrideViewAngles(aimRotation.eulerAngles);
        npc.eyes.NetworkUpdate(aimRotation);
    }

    /// <summary>
    /// Everything that should happen once any loot transfer actually moves
    /// items - regardless of source (destroyed barrel, opened crate, or a
    /// looted corpse/bag - see LootMultiContainerEntityAndContinue).
    /// Food/water consumption always runs (cheap, self-gating - see
    /// ConsumeFoodImmediately's own doc comment), but the heavier
    /// reorganization pass (equip/armor/belt/tidy) only runs if
    /// movedShortnames actually contains something worth reacting to -
    /// see ReactiveLootCategories' own doc comment for why. Lucas's own
    /// explicit request: with many survivors all looting simultaneously,
    /// re-running a full belt/inventory reorganization after literally
    /// every pickup (including a single stack of scrap) was wasted work
    /// and could shuffle item positions for no functional reason.
    /// </summary>
    private void OnLootObtained(Survivor survivor, BasePlayer npc, List<string> movedShortnames)
    {
        PlayPickupGesture(npc);

        // Eat any food/drink any water before bothering to react further -
        // see ConsumeFoodImmediately/DrinkWaterBottles' own doc comments.
        // Always run regardless of category - genuinely cheap (each just
        // scans for its own specific item type and no-ops if none exist),
        // and doesn't reorganize/shuffle anything else on its own.
        RunLootHookSafely(survivor, nameof(ConsumeFoodImmediately), () => ConsumeFoodImmediately(survivor));
        RunLootHookSafely(survivor, nameof(DrinkWaterBottles), () => DrinkWaterBottles(survivor, npc));
        RunLootHookSafely(survivor, nameof(ConsumeRadiationPillsImmediately), () => ConsumeRadiationPillsImmediately(survivor, npc));
        RunLootHookSafely(survivor, nameof(DropOwnedSeeds), () => DropOwnedSeeds(survivor, npc));
        RunLootHookSafely(survivor, nameof(DropBowKitIfArmed), () => DropBowKitIfArmed(survivor, npc));
        RunLootHookSafely(survivor, nameof(EquipBestBackpack), () => EquipBestBackpack(survivor));

        if (ContainsReactiveLootCategory(movedShortnames))
        {
            PerformReorganizationCheck(survivor, npc);
        }
    }

    /// <summary>
    /// Real item categories worth actually reacting to with a full
    /// equip/armor/belt/tidy pass - Weapon/Attire/Tool/Medical/Ammunition
    /// are the only categories that could conceivably change what's
    /// equipped, worn, or how the belt should be laid out. Plain
    /// resources/components/junk (scrap, wood, stone, gears, ...) never
    /// could be an upgrade to anything this project tracks, so reacting
    /// to them is pure wasted work - and with many survivors all looting
    /// at once, real, worth avoiding.
    /// </summary>
    private static readonly ItemCategory[] ReactiveLootCategories =
    {
        ItemCategory.Weapon,
        ItemCategory.Attire,
        ItemCategory.Tool,
        ItemCategory.Medical,
        ItemCategory.Ammunition,
    };

    private bool ContainsReactiveLootCategory(List<string> movedShortnames)
    {
        foreach (string shortname in movedShortnames)
        {
            ItemDefinition definition = ItemManager.FindItemDefinition(shortname);

            if (definition != null && Array.IndexOf(ReactiveLootCategories, definition.category) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The full "am I set up properly" self-check, run unconditionally
    /// once before a task even starts (StartLootForResourcesTask) per
    /// Lucas's explicit request: a survivor shouldn't only tidy up
    /// reactively as loot comes in - it should confirm its own kit is in
    /// order before heading off too. OnLootObtained's own per-pickup
    /// version is conditional (see ReactiveLootCategories' own doc
    /// comment) since it runs far more often; this one-time pre-task
    /// check always runs the full thing regardless, since it only ever
    /// happens once per task, not once per item.
    /// </summary>
    private void PerformInventoryCheck(Survivor survivor, BasePlayer npc)
    {
        RunLootHookSafely(survivor, nameof(ConsumeFoodImmediately), () => ConsumeFoodImmediately(survivor));
        RunLootHookSafely(survivor, nameof(DrinkWaterBottles), () => DrinkWaterBottles(survivor, npc));
        RunLootHookSafely(survivor, nameof(ConsumeRadiationPillsImmediately), () => ConsumeRadiationPillsImmediately(survivor, npc));
        RunLootHookSafely(survivor, nameof(DropOwnedSeeds), () => DropOwnedSeeds(survivor, npc));
        RunLootHookSafely(survivor, nameof(DropBowKitIfArmed), () => DropBowKitIfArmed(survivor, npc));
        RunLootHookSafely(survivor, nameof(EquipBestBackpack), () => EquipBestBackpack(survivor));

        PerformReorganizationCheck(survivor, npc);
    }

    /// <summary>
    /// Real Rust confirmed live (Lucas, 2026-08-11): eating raw pumpkin,
    /// corn, or any berry colour actually yields a real seed item back
    /// (genuine vanilla ItemModConsume byproduct, not something this plugin
    /// creates) - the pre-existing seed.* never-loot rule only stops a
    /// survivor picking a seed up from elsewhere, it does nothing about one
    /// that appears in inventory as a side effect of eating. Same "grade A
    /// dead weight, no use until hemp/cloth gathering exists" reasoning
    /// IsNeverLootItem already applies to seeds, so any owned seed just gets
    /// dropped outright here - same shape as DropUnneededLightSource's
    /// torch drop, just triggered from the reactive inventory-check pass
    /// instead of task-start.
    /// </summary>
    private void DropOwnedSeeds(Survivor survivor, BasePlayer npc)
    {
        List<Item> seeds = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => item.info.shortname.StartsWith(SeedShortnamePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (Item seed in seeds)
        {
            Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
            Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

            seed.Drop(dropPosition, dropVelocity);

            VerbosePuts($"'{survivor.Character.Alias}' dropped '{seed.info.shortname}' - no use for seeds yet.");
        }
    }

    /// <summary>
    /// The actual equip/armor/belt/tidy reorganization steps, shared by
    /// both OnLootObtained (conditionally) and PerformInventoryCheck
    /// (always, for the once-per-task pre-departure check).
    /// </summary>
    private void PerformReorganizationCheck(Survivor survivor, BasePlayer npc)
    {
        // A better weapon than whatever's currently displayed might have
        // just come out of this - see EquipBestWeaponForDisplay's own doc
        // comment for why this (not EquipBestMeleeTool) is what governs
        // the survivor's normal, everyday active item. EquipBestMeleeTool
        // itself is only ever called directly by StartAttackingContainer,
        // for the brief real window of actually swinging at a barrel.
        RunLootHookSafely(survivor, nameof(EquipBestWeaponForDisplay), () => EquipBestWeaponForDisplay(survivor));

        // Same idea for armor - see EvaluateAndUpgradeArmor's own doc
        // comment.
        RunLootHookSafely(survivor, nameof(EvaluateAndUpgradeArmor), () => EvaluateAndUpgradeArmor(survivor));

        // Catches redundant armor EvaluateAndUpgradeArmor's own drop-on-
        // upgrade doesn't (duplicates that never actually competed against
        // each other in a single upgrade event) - see its own doc comment.
        RunLootHookSafely(survivor, nameof(DropRedundantArmor), () => DropRedundantArmor(survivor, npc));

        // Doesn't need the starting rock anymore once something better's
        // actually in hand - see DropRockIfUpgraded's own doc comment.
        RunLootHookSafely(survivor, nameof(DropRockIfUpgraded), () => DropRockIfUpgraded(survivor, npc));

        // Keep the belt laid out like a real player would, and main
        // inventory tidy - see OrganizeBelt/TidyMainInventory's own doc
        // comments.
        RunLootHookSafely(survivor, nameof(OrganizeBelt), () => OrganizeBelt(survivor));
        RunLootHookSafely(survivor, nameof(TidyMainInventory), () => TidyMainInventory(survivor));
    }

    /// <summary>
    /// Isolates each post-loot hook above from the others. A live crash
    /// (a NullReferenceException in a different but structurally similar
    /// method - see StartAttackingContainerWithReposition's own doc
    /// comment) confirmed an uncaught exception in one of these can
    /// silently break the ENTIRE loot-task callback chain:
    /// LootCorpseAndContinue/LootContainerDirectly both call
    /// OnLootObtained BEFORE their own trailing ContinueLootTask call, so
    /// an exception partway through this sequence aborts before that line
    /// ever runs - a survivor that freezes permanently right after its
    /// next loot pickup, matching a live report ("stops and doesn't know
    /// what to do next" after looting a corpse). Catching and logging
    /// per-hook means one broken hook (several of these - armor eval,
    /// food, water, belt layout - are all brand new and not yet fully
    /// live-tested) can't take the whole survivor down with it, and any
    /// future failure gets a real, specific log line to work from instead
    /// of a silent freeze.
    /// </summary>
    private void RunLootHookSafely(Survivor survivor, string hookName, Action hook)
    {
        try
        {
            hook();
        }
        catch (Exception exception)
        {
            Puts($"WARNING: '{survivor.Character.Alias}' - loot hook '{hookName}' threw and was skipped: {exception.Message}");
        }
    }

    /// <summary>
    /// Best-effort real "reaching in and grabbing something" cue, fired
    /// whenever a loot transfer actually moves items - Lucas's own
    /// observation that a real player has a visible pickup/grab animation,
    /// while this bot's loot transfers were a silent, instant inventory
    /// swap. Used BaseEntity.Signal.Gesture, the same real client RPC
    /// mechanism StartAttackingContainer's swing animation relies on
    /// (BaseMelee.ServerUse's SignalBroadcast(Signal.Attack, ...),
    /// confirmed via decompiling Assembly-CSharp.dll) - Signal is a small,
    /// fixed enum (Attack, Reload, Throw, Gesture, Eat, ...; no dedicated
    /// "Pickup" entry exists), and Gesture was the closest generic "play a
    /// body animation" option in it.
    ///
    /// DISABLED 2026-08-16 (Lucas's own live bug report: a bot's held
    /// weapon going invisible - "still holding its hands as if it were
    /// holding it" - right after picking something up, no equip/swap event
    /// anywhere near it in the log). This function's own doc comment
    /// always flagged Signal.Gesture as "not yet visually confirmed" - a
    /// real connected player's own client naturally animates OUT of a
    /// gesture pose once it finishes, but these bots never process a real
    /// client tick, so nothing ever tells the animator the gesture is
    /// over; getting stuck IN the gesture pose (hands up, held item
    /// hidden) forever is a very plausible real explanation, and matches
    /// the report far better than the equip-churn theory this same session
    /// already fixed separately. No-op for now to conclusively test
    /// whether this was the actual cause - re-enable (and this time pace
    /// it with a delayed ForceRefreshHeldEntity call to force the weapon
    /// back once the gesture would have finished) only once that's
    /// confirmed either way.
    /// </summary>
    private void PlayPickupGesture(BasePlayer npc)
    {
    }

    /// <summary>
    /// Manually replays the hit impact FX/sound that BaseMelee.ServerUse_Strike
    /// would normally trigger via Effect.server.ImpactEffect - needed because
    /// StartAttackingContainer cancels ServerUse_Strike entirely (to avoid a
    /// confirmed double-damage bug, see its own call site comment), which
    /// also silently cancels the only place that impact FX/sound gets fired.
    /// Builds its own HitInfo instead of relying on a real physics raycast
    /// (unlike the native code) since the target container is already known
    /// - container.ClosestPoint gives a reasonable stand-in for where a real
    /// swing would have connected. HitMaterial deliberately left at Rust's
    /// "generic" fallback (the same fallback ServerUse_Strike itself uses
    /// when a collider's real material can't be determined) rather than
    /// guessing a specific material - EffectDictionary and
    /// BaseMelee.GetStrikeEffectPath both degrade gracefully for it.
    /// </summary>
    private void PlayMeleeImpactEffect(BasePlayer npc, BaseMelee melee, BaseEntity container)
    {
        if (melee == null)
        {
            return;
        }

        Vector3 hitPositionWorld = container.ClosestPoint(npc.eyes.position);
        Vector3 hitNormalWorld = (npc.eyes.position - hitPositionWorld).normalized;

        HitInfo hitInfo = new HitInfo
        {
            Initiator = npc,
            WeaponPrefab = melee,
            // Effect.server.ImpactEffect (decompiled) branches on
            // "info.WeaponPrefab is AttackEntity" - true for a BaseMelee -
            // but then calls info.Weapon.GetImpactEffectNumberValue(info)
            // and info.Weapon.GetEffectType(info), reading the SEPARATE
            // Weapon field, not WeaponPrefab. Never setting it here meant
            // that branch always dereferenced a null Weapon and threw -
            // confirmed by a live server error: "Object reference not set
            // to an instance of an object" inside Effect+server.ImpactEffect,
            // called from this exact method. Both fields need to point at
            // the same real melee tool.
            Weapon = melee,
            HitEntity = container,
            HitPositionWorld = hitPositionWorld,
            HitPositionLocal = container.transform.InverseTransformPoint(hitPositionWorld),
            HitNormalWorld = hitNormalWorld,
            HitNormalLocal = container.transform.InverseTransformDirection(hitNormalWorld),
            HitMaterial = StringPool.Get("generic"),
        };

        Effect.server.ImpactEffect(hitInfo);
    }

    /// <summary>
    /// Equips whichever known melee tool the survivor is currently
    /// carrying (main or belt) ranks best in MeleeToolPriority - a real
    /// player would grab whatever's quickest for the job rather than
    /// sticking with the starting rock once something better turns up.
    /// Only belt items can be the active held item (confirmed via
    /// decompiling BasePlayer.UpdateActiveItem - it looks the target item
    /// up specifically in inventory.containerBelt), so the chosen item
    /// gets moved there first if it's sitting in main. Every bot starts
    /// with a rock (GiveStartingKit), so there's always at least a
    /// fallback candidate somewhere in inventory - this just prefers
    /// whatever's actually best once something better gets looted.
    /// </summary>
    /// <summary>
    /// Whether the survivor is carrying any known melee tool anywhere
    /// (main or belt) - not the same question as "what's currently
    /// equipped" (a survivor can own a rock without it being the active
    /// item yet, especially right at task start before EquipBestMeleeTool
    /// has ever run this task). Used to decide whether barrels/roadsigns
    /// are worth considering as a target at all.
    /// </summary>
    private bool HasAnyMeleeTool(BasePlayer npc)
    {
        foreach (Item item in npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList))
        {
            if (Array.IndexOf(MeleeToolPriority, item.info.shortname) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Same as HasAnyMeleeTool, but excludes the starting "rock" itself -
    /// see IsRoadsign's own doc comment for why roadsigns specifically
    /// need this stricter gate instead of HasAnyMeleeTool's "literally
    /// anything, rock included" bar.
    /// </summary>
    private bool HasNonRockMeleeTool(BasePlayer npc)
    {
        foreach (Item item in npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList))
        {
            if (item.info.shortname != "rock" && Array.IndexOf(MeleeToolPriority, item.info.shortname) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Equips once and stops - the very first real tool (anything other
    /// than the starting rock) a survivor picks up gets equipped and kept
    /// for the rest of that life, even if something ranked higher turns up
    /// later. User-requested simplification: continuously re-evaluating
    /// and swapping mid-loot-run wasn't wanted for now. Revisit once real
    /// combat behavior (the FSM work) wants genuine tool upgrades
    /// mid-fight.
    /// </summary>
    private void EquipBestMeleeTool(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        Item currentlyEquipped = npc.GetActiveItem();

        if (currentlyEquipped != null && currentlyEquipped.info.shortname != "rock"
            && Array.IndexOf(MeleeToolPriority, currentlyEquipped.info.shortname) >= 0)
        {
            return;
        }

        // Prefer whatever's ALREADY on the belt (almost always the real
        // gather tool sitting in BeltGatherToolSlot - a pickaxe or
        // hatchet) before ever reaching into main inventory for a
        // technically-higher-ranked melee weapon. A live report + its log
        // evidence showed this reaching for a looted salvaged.cleaver
        // (MeleeToolPriority rank 2) over an already-equipped-ready
        // pickaxe/hatchet sitting right there on the belt, repeatedly
        // failing to find room for the swap ("no free belt slot") since
        // OrganizeBelt's scheme now claims all 6 slots - real wasted
        // effort for a marginal combat difference, when a pickaxe/hatchet
        // is "perfectly capable of hitting barrels" (Lucas's own words).
        // Only falls back to the full main+belt search (which can still
        // hit that same capacity edge case) if nothing on the belt
        // qualifies as a melee tool at all.
        Item best = FindBestByPriority(npc.inventory.containerBelt.itemList.ToList(), MeleeToolPriority, exclude: null);

        if (best == null)
        {
            int bestRank = int.MaxValue;

            foreach (Item item in npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList))
            {
                int rank = Array.IndexOf(MeleeToolPriority, item.info.shortname);

                if (rank >= 0 && rank < bestRank)
                {
                    bestRank = rank;
                    best = item;
                }
            }
        }

        if (best == null || (currentlyEquipped != null && currentlyEquipped.uid == best.uid))
        {
            return;
        }

        int bestRankForLog = Array.IndexOf(MeleeToolPriority, best.info.shortname);

        if (!npc.inventory.containerBelt.itemList.Contains(best))
        {
            // OrganizeBelt's newer scheme (weapon/offsider/medical/
            // bandage/tool/overflow) now claims all 6 belt slots, so an
            // auto-position (-1) move has no genuinely free slot to land
            // in - a live report caught this failing every time and
            // silently giving up on the tool swap entirely (leaving melee
            // null for the whole attack, which also bypassed the real
            // attack-cooldown gate below since that's only checked when
            // melee != null - explains both "tool never swaps" and
            // "hitting way too fast" from the same root cause). Targeting
            // whatever's currently equipped's own belt position directly
            // (with allowSwap - real MoveToContainer semantics) guarantees
            // a slot regardless of how full the belt is: it swaps the
            // active item out to wherever the tool used to be, rather
            // than needing an actually-empty slot to exist first.
            int targetPosition = currentlyEquipped != null && currentlyEquipped.parent == npc.inventory.containerBelt
                ? currentlyEquipped.position
                : -1;

            if (!best.MoveToContainer(npc.inventory.containerBelt, targetPosition))
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' has a better tool ('{best.info.shortname}') but no free belt slot to equip it - keeping '{(currentlyEquipped != null ? currentlyEquipped.info.shortname : "nothing")}'.");
                return;
            }
        }

        npc.UpdateActiveItem(best.uid);

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' equipped '{best.info.shortname}' (priority rank {bestRankForLog}).");
    }

    /// <summary>
    /// Equips the survivor's best real WEAPON (WeaponPriority - guns/bows/
    /// dedicated melee weapons) as the active/displayed item for ordinary
    /// exploring and looting - Lucas's own framing: a bot shouldn't be
    /// visibly walking around with a pickaxe out while an M249 sits
    /// unused on its belt "for obvious reasons." EquipBestMeleeTool is
    /// still what actually gets equipped for the brief real window of
    /// swinging at a barrel (StartAttackingContainer calls it directly,
    /// right before the swing loop starts) - this is what restores the
    /// display weapon again once that's done, called from
    /// PerformInventoryCheck and at the top of every ContinueLootTask
    /// cycle so it's never left holding a tool a moment longer than
    /// actually necessary. Falls back to the best gathering tool if the
    /// survivor genuinely owns no real weapon at all, not even a bow -
    /// mirrors OrganizeBelt's own primary-slot fallback rule exactly, so
    /// what's equipped always matches what's actually sitting in belt
    /// slot 1.
    ///
    /// Never swaps AWAY from an already-equipped real weapon anymore
    /// (2026-08-16 - see OrganizeBelt's own doc comment for the full
    /// reasoning/Lucas's exact request and the invisible-weapon glitch
    /// this is also the fix for). If nothing real is currently equipped,
    /// this restores whatever's ALREADY sitting in the committed belt
    /// slot 1 (OrganizeBelt keeps that fixed too) rather than recomputing
    /// "best" from every owned item - only a genuinely empty slot 1 (never
    /// armed at all) falls through to picking a brand new one.
    /// </summary>
    private void EquipBestWeaponForDisplay(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        Item currentlyEquipped = npc.GetActiveItem();

        List<Item> allItems = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .ToList();

        // Best weapon the survivor can actually USE right now (2026-09-21,
        // Lucas's own live report: a bot kept using its bow while carrying a
        // Thompson). A firearm with no ammo doesn't count.
        Item bestUsableWeapon = FindBestByPriority(allItems.Where(item => IsWeaponUsableNow(npc, item)).ToList(), WeaponPriority, exclude: null);
        int bestUsableRank = bestUsableWeapon != null ? Array.IndexOf(WeaponPriority, bestUsableWeapon.info.shortname) : int.MaxValue;

        if (currentlyEquipped != null && Array.IndexOf(WeaponPriority, currentlyEquipped.info.shortname) >= 0)
        {
            // Already holding a listed weapon: only carry on if nothing usable
            // owned is actually better. (Used to return unconditionally, so a
            // bot holding its bow never re-evaluated after picking up a gun.)
            int currentRank = Array.IndexOf(WeaponPriority, currentlyEquipped.info.shortname);

            if (bestUsableWeapon == null || bestUsableRank >= currentRank)
            {
                return;
            }
        }

        Item committedPrimary = npc.inventory.containerBelt.itemList.FirstOrDefault(item =>
            item.position == BeltWeaponSlot
            && (Array.IndexOf(WeaponPriority, item.info.shortname) >= 0 || Array.IndexOf(GatherToolPriority, item.info.shortname) >= 0));

        // The "committed" slot weapon only wins if it's at least as good as
        // the best usable one owned.
        if (committedPrimary != null && bestUsableWeapon != null
            && Array.IndexOf(WeaponPriority, committedPrimary.info.shortname) is int committedRank
            && (committedRank < 0 || bestUsableRank < committedRank))
        {
            committedPrimary = null;
        }

        Item best = committedPrimary
            ?? bestUsableWeapon
            ?? FindBestByPriority(allItems, WeaponPriority, exclude: null)
            ?? FindBestByPriority(allItems, GatherToolPriority, exclude: null);

        if (best == null || (currentlyEquipped != null && currentlyEquipped.uid == best.uid))
        {
            return;
        }

        // Real root cause, found 2026-08-16 via weapon-refresh-diag: this
        // used to check `!containerBelt.itemList.Contains(best)` - "is it
        // ANYWHERE in the belt" - not its actual POSITION. A kit-given
        // weapon that lands in some other belt slot (confirmed live: an
        // AK sitting in slot 2 instead of slot 0) already satisfies
        // Contains(), so the move into BeltWeaponSlot silently never ran -
        // the weapon still equipped and displayed FINE at the time (
        // UpdateActiveItem doesn't care what slot the item is in), but
        // OrganizeBelt's own "is slot 0 already committed" check
        // specifically looks for something AT position BeltWeaponSlot, so
        // it kept seeing slot 0 as empty. The first later loot pickup that
        // triggered OrganizeBelt then moved the ALREADY-ACTIVE weapon for
        // real (a genuine position change, correctly triggering the
        // refresh gate added earlier this session) - and doing that
        // MoveToContainer+UpdateActiveItem+refresh sequence on a weapon
        // that's already equipped and being carried mid-motion is what
        // actually produced the visible invisible-weapon glitch. Checking
        // the real position here, not just belt membership, stops the
        // weapon from ever landing anywhere but slot 0 in the first place,
        // so OrganizeBelt never finds a surprise to correct later.
        // Checks parent AND position - position alone is only meaningful
        // within an item's own current container (a main-inventory item at
        // position 0 isn't "already in belt slot 1" just because the
        // numbers match).
        bool alreadyInWeaponSlot = best.parent == npc.inventory.containerBelt && best.position == BeltWeaponSlot;

        if (!alreadyInWeaponSlot && !best.MoveToContainer(npc.inventory.containerBelt, BeltWeaponSlot))
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' has a better weapon ('{best.info.shortname}') to display but no free belt slot for it - keeping '{(currentlyEquipped != null ? currentlyEquipped.info.shortname : "nothing")}'.");
            return;
        }

        npc.UpdateActiveItem(best.uid);
        ForceRefreshHeldEntity(npc);

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' switched back to displaying '{best.info.shortname}'.");
    }

    /// <summary>
    /// Forces a fresh network snapshot of whatever's currently held, right
    /// after UpdateActiveItem - 2026-08-16, real live bug (screenshot
    /// evidence: a bot visibly in an aiming pose with no weapon model
    /// attached, right after a weapon swap). Same root cause this whole
    /// project's ModelState fixes already document repeatedly (see
    /// StartSurvivorTrace's own SendModelState comment, or EquipBestWeaponForDisplay's
    /// earlier OrganizeBelt "invisible hand" note) - these are connectionless
    /// BasePlayers that never send a real PlayerTick RPC, so nothing else
    /// ever diffs/broadcasts a state change the way a genuine connected
    /// client's own tick processing normally would. UpdateActiveItem
    /// correctly updates SERVER state (spawns/attaches the real held
    /// entity), but apparently doesn't reliably push that entity's own
    /// fresh network snapshot out to other observers on its own for a bot
    /// that never ticks - this closes that same gap for the held entity
    /// specifically, the same way force:true SendModelState already does
    /// for sprinting/ducked/onLadder/etc. No-ops harmlessly if nothing's
    /// currently held.
    /// </summary>
    private void ForceRefreshHeldEntity(BasePlayer npc)
    {
        npc.GetHeldEntity()?.SendNetworkUpdateImmediate();
    }

    /// <summary>
    /// Drops the starting rock once the survivor has a genuinely better
    /// melee/gather tool (anything else in MeleeToolPriority) - Lucas's
    /// own request, same "doesn't need it, drop it for real" framing as
    /// DropUnneededLightSource's torch handling. Deliberately checks the
    /// survivor's whole inventory for a better option, not just whatever's
    /// currently equipped - a better tool sitting unequipped in the main
    /// inventory still makes the rock redundant baggage.
    /// </summary>
    private void DropRockIfUpgraded(Survivor survivor, BasePlayer npc)
    {
        Item rock = npc.inventory.FindItemByItemName("rock");

        if (rock == null)
        {
            return;
        }

        bool hasBetterTool = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Any(item => item.info.shortname != "rock" && Array.IndexOf(MeleeToolPriority, item.info.shortname) >= 0);

        if (!hasBetterTool)
        {
            return;
        }

        // Real "don't strand yourself without a wood/stone fallback" gate
        // (2026-09-01, Lucas's own explicit spec, simplified from an
        // earlier nearby-resource-bootstrap version to a flat rule: "have
        // bots keep their rock UNTIL they get a hatchet and a pickaxe (of
        // any kind). then drop the rock, otherwise it will be stuck trying
        // to gather resources"). The rock is the ONLY tool that can gather
        // both wood AND stone at all - both HatchetFamily and PickaxeFamily
        // entries sit in MeleeToolPriority above, so hasBetterTool alone
        // used to go true (and drop the rock) the instant a survivor picked
        // up just ONE of the two real gather tools, stranding a pickaxe-
        // only survivor with no way left to chop wood at all.
        if (!HasAnyToolOfFamily(npc, HatchetFamily) || !HasAnyToolOfFamily(npc, PickaxeFamily))
        {
            return;
        }

        Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
        Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

        rock.Drop(dropPosition, dropVelocity);

        VerbosePuts($"'{survivor.Character.Alias}' dropped its rock now that it has a real hatchet and pickaxe.");
    }

    /// <summary>
    /// Damage types folded into a single overall "how good is this armor"
    /// score - real per-item protection is per-DamageType
    /// (ProtectionProperties.amounts, 28 entries, confirmed via
    /// decompiling ProtectionProperties), not one scalar. Bullet/Slash/
    /// Blunt cover the combat cases that actually matter for survivability
    /// against players/animals; Cold is included since exposure is a
    /// real, already-partially-scaffolded need (see
    /// [[project-livingrust-roadmap]]'s Needs entry) even though nothing
    /// acts on temperature yet. Deliberately NOT every DamageType
    /// (Radiation, Explosion, etc.) - those are rare enough in this bot's
    /// current combat scope (melee-only, container destruction) that
    /// including them would bias the score toward armor pieces that are
    /// actually a poor general pick right now.
    /// </summary>
    private static readonly DamageType[] ArmorEvaluationDamageTypes =
    {
        DamageType.Bullet,
        DamageType.Slash,
        DamageType.Blunt,
        DamageType.Cold,
    };

    /// <summary>
    /// Bullet gets weighted far above the others - a live report and its
    /// log evidence confirmed a real scoring bug: a flat unweighted sum
    /// let wood.armor.jacket (broad-but-shallow protection across all
    /// four types) outscore metal.plate.torso.icevest outright (0.90 vs
    /// 0.65), so the bot equipped wood over metal and dropped the metal
    /// plate it had just looted - backwards from any real player's
    /// judgement. Rust's own real armor design deliberately concentrates
    /// top-tier pieces (metal plate, roadsign) into strong bullet
    /// resistance specifically, since gunfights are the dominant real
    /// threat/cause of death - a flat sum structurally can't reflect that,
    /// since it lets several mediocre secondary stats numerically outweigh
    /// one excellent primary one. Slash/Blunt/Cold are still real
    /// secondary factors (a tiebreaker between two similarly bullet-
    /// resistant pieces), just no longer able to overrule a clear bullet-
    /// protection gap the way they just did.
    /// </summary>
    private const float BulletProtectionWeight = 4f;

    /// <summary>
    /// How much higher a candidate's score has to be than whatever it
    /// would displace before it's worth the swap - without this, two
    /// pieces with near-identical protection (e.g. both roughly "tier 1
    /// cloth") would thrash back and forth every time a new one is picked
    /// up, never settling.
    /// </summary>
    private const float ArmorUpgradeMinimumScoreGain = 0.05f;

    /// <summary>
    /// Real-world armor tiers, low to high, per Lucas's explicit ranking
    /// (2026-08-09): basic clothing (burlap, pants, hoodies - no special
    /// handling needed, their real protection stats are low enough to
    /// naturally fall at the bottom already) &lt; wood armor &lt; radiation/
    /// hazmat suits (deliberately ranked low but explicitly ABOVE wood -
    /// "however it trumps wooden armour") &lt; roadsign &lt; metal plate &lt;
    /// ballistic/heavy plate (highest). This exists as an explicit tier
    /// list rather than trusting the raw weighted-damage-type score to
    /// happen to land in this order on its own - hazmat suits are the
    /// clearest reason why: their real value is radiation resistance, a
    /// damage type this project doesn't even track (see
    /// ArmorEvaluationDamageTypes' own doc comment for why), so their raw
    /// combat-protection score alone would likely rank them at or below
    /// wood, contradicting Lucas's explicit real-world ranking. Real
    /// shortnames confirmed via a full item-database scan.
    /// </summary>
    private enum ArmorTier
    {
        Basic = 0,
        Wood = 1,
        Hazmat = 2,
        Roadsign = 3,
        MetalPlate = 4,
        TopTier = 5,
    }

    private static readonly string[] WoodArmorShortnames = { "wood.armor.jacket", "wood.armor.pants", "wood.armor.helmet" };
    // coffeecan.helmet added 2026-08-15 (Lucas's own explicit request,
    // "have coffee can helmet added as medium, same as road sign") - real
    // shortname confirmed via Bundles\items\coffeecan.helmet.json.
    private static readonly string[] RoadsignArmorShortnames = { "roadsign.jacket", "roadsign.gloves", "roadsign.kilt", "coffeecan.helmet" };
    private static readonly string[] MetalPlateArmorShortnames = { "metal.plate.torso", "metal.plate.torso.icevest", "metal.facemask", "metal.facemask.hockey", "metal.facemask.icemask" };

    /// <summary>
    /// Ballistic (the current real top tier: vest/helmet/leg armor) and
    /// heavy plate (the older top tier: jacket/pants/helmet) are both
    /// genuinely top-of-game armor - Lucas named "ballistic armour"
    /// specifically as highest but didn't separately place heavy plate,
    /// so both share this tier; GetArmorProtectionScore's own raw
    /// weighted score still breaks ties between them.
    /// </summary>
    private static readonly string[] TopTierArmorShortnames = { "ballistic.vest", "ballistic.helmet", "ballistic.legarmor", "heavy.plate.jacket", "heavy.plate.pants", "heavy.plate.helmet" };

    /// <summary>
    /// Every real radiation/hazmat suit shortname shares this exact
    /// prefix (base hazmatsuit plus every real skinned variant -
    /// arcticsuit, diver, frontier, lumberjack, nomadsuit, pilot,
    /// spacesuit, the scientist variants, ...) - matched by prefix so a
    /// future new skin is covered automatically, same reasoning
    /// SeedShortnamePrefix's own doc comment gives. Deliberately NOT a
    /// bare "hazmat" prefix - that would also catch hazmat.krieg and
    /// hazmat.plushy (real novelty/non-armor items) and
    /// pilot.hazmat.box.wooden (a deployable, not wearable at all).
    /// </summary>
    private const string HazmatSuitShortnamePrefix = "hazmatsuit";

    private static ArmorTier GetArmorTier(string shortname)
    {
        if (Array.IndexOf(TopTierArmorShortnames, shortname) >= 0)
        {
            return ArmorTier.TopTier;
        }

        if (Array.IndexOf(MetalPlateArmorShortnames, shortname) >= 0)
        {
            return ArmorTier.MetalPlate;
        }

        if (Array.IndexOf(RoadsignArmorShortnames, shortname) >= 0)
        {
            return ArmorTier.Roadsign;
        }

        if (shortname.StartsWith(HazmatSuitShortnamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ArmorTier.Hazmat;
        }

        if (Array.IndexOf(WoodArmorShortnames, shortname) >= 0)
        {
            return ArmorTier.Wood;
        }

        return ArmorTier.Basic;
    }

    /// <summary>
    /// Basic (burlap/hoodie/pants/balaclava/etc - the GetArmorTier
    /// fallback) and Wood - genuinely cheap, low-value clothing/armor
    /// Lucas explicitly wants deduplicated on sight, unlike Hazmat/
    /// Roadsign/MetalPlate/TopTier spares (kept regardless of duplicates
    /// - "more expensive [to craft], in game, literally," per Lucas's own
    /// framing). Real live example that prompted this: a bot equipped a
    /// mask.balaclava, then picked up a second one and just carried it as
    /// dead weight - ShouldSkipInferiorArmor's own equal-tier-is-fine
    /// rule (see its own doc comment) correctly keeps a genuine upgrade
    /// reserve for expensive gear, but was never meant to defend a spare
    /// balaclava.
    /// </summary>
    private static bool IsLowTierArmor(string shortname)
    {
        return GetArmorTier(shortname) <= ArmorTier.Wood;
    }

    /// <summary>
    /// Real per-item protection score, built from two layers: ArmorTier
    /// dominates the comparison (a real tier gap always wins, per Lucas's
    /// explicit ranking), and the raw weighted-damage-type score (Bullet
    /// weighted heavily - see BulletProtectionWeight's own doc comment)
    /// only ever breaks a tie WITHIN the same tier (e.g. two different
    /// roadsign pieces, or ballistic vs heavy plate). The real raw score
    /// never exceeds roughly 10 (four damage types, Bullet weighted up to
    /// ~4x a 0-1 fraction), so multiplying tier by 100 keeps tiers as a
    /// hard ceiling no same-tier stat difference could ever cross.
    /// ItemModWearable.GetProtection already folds in condition (a broken
    /// item drops to 25% protection, confirmed via decompiling
    /// ItemModWearable.ConditionProtectionScale), so a damaged piece
    /// correctly scores lower within its tier without any extra logic
    /// here.
    /// </summary>
    private float GetArmorProtectionScore(Item item)
    {
        ItemModWearable wearable = item.info.GetComponent<ItemModWearable>();

        if (wearable == null || !wearable.HasProtections())
        {
            return 0f;
        }

        float rawScore = 0f;

        foreach (DamageType damageType in ArmorEvaluationDamageTypes)
        {
            float protection = wearable.GetProtection(item, damageType);
            rawScore += damageType == DamageType.Bullet ? protection * BulletProtectionWeight : protection;
        }

        return (float)GetArmorTier(item.info.shortname) * 100f + rawScore;
    }

    /// <summary>
    /// Evaluates every wearable currently sitting unworn in the survivor's
    /// main inventory against whatever it would actually displace, and
    /// equips it if it's a real upgrade - the survivor's own version of
    /// "I'm wearing burlap but this roadsign chestplate in my bag is
    /// clearly better, so I'll wear that instead" (Lucas's own framing).
    ///
    /// Real Rust clothing doesn't use a fixed enum slot (Head/Chest/Legs) -
    /// what a piece can be worn WITH is governed by Wearable.occupationOver/
    /// occupationUnder bitflags, exposed via ItemModWearable.CanExistWith
    /// (confirmed via decompiling both). So "what would this replace" is
    /// computed for real - every currently worn item this candidate
    /// conflicts with - rather than assumed from a hardcoded slot name,
    /// which would break the moment a genuinely new armor type (a helmet
    /// that also covers HeadBack, say) didn't match this project's own
    /// guess at Rust's slot layout.
    ///
    /// Equipping itself is just Item.MoveToContainer(containerWear) - the
    /// same real call a client-driven wear action ultimately makes. Its
    /// canAcceptItem hook (PlayerInventory.CanWearItem, canAdjustClothing:
    /// true by default) already handles displacing whatever conflicts,
    /// exactly like a real player dragging a new chestplate onto an
    /// occupied slot - no manual unequip step needed here.
    /// </summary>
    private void EvaluateAndUpgradeArmor(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return;
        }

        EquipBestBackpack(survivor);

        // Also scans containerBelt, not just containerMain - a live report
        // caught real armor sitting unworn in a belt slot that this method
        // never even considered as a candidate. Rust's own loot-transfer
        // placement (PlayerInventory.GiveItem) can land a wearable
        // directly on the belt if main happened to be fuller at that
        // exact moment - nothing about where an item first lands should
        // decide whether it's ever evaluated for wearing.
        List<Item> candidates = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => item.info.GetComponent<ItemModWearable>() != null && !IsBackpackItem(item))
            .ToList();

        foreach (Item candidate in candidates)
        {
            ItemModWearable candidateWearable = candidate.info.GetComponent<ItemModWearable>();

            List<Item> conflicting = npc.inventory.containerWear.itemList
                .Where(worn => !candidateWearable.CanExistWith(worn.info.GetComponent<ItemModWearable>()))
                .ToList();

            float candidateScore = GetArmorProtectionScore(candidate);
            float conflictingScore = conflicting.Sum(GetArmorProtectionScore);

            // The score gate only makes sense when there's actually
            // something worth comparing against - a live report caught a
            // real bug here: mask.balaclava scores 0 across
            // ArmorEvaluationDamageTypes (Bullet/Slash/Blunt/Cold aren't
            // where a face covering's real protection lives), so the old
            // unconditional "candidateScore <= 0f, skip" check silently
            // refused to wear it even into a completely empty slot with
            // nothing to lose by wearing it. A real player wears whatever
            // they've got for an empty slot; only an occupied, conflicting
            // slot needs a genuine improvement to justify displacing
            // something already worn.
            if (conflicting.Count > 0 && candidateScore < conflictingScore + ArmorUpgradeMinimumScoreGain)
            {
                continue;
            }

            if (candidate.MoveToContainer(npc.inventory.containerWear))
            {
                string replacedNote = conflicting.Count > 0
                    ? string.Join(", ", conflicting.Select(item => item.info.shortname))
                    : "nothing (empty slot)";

                VerbosePuts($"loot-task: '{survivor.Character.Alias}' equipped '{candidate.info.shortname}' (protection {candidateScore:F2} vs {conflictingScore:F2}) - replacing: {replacedNote}.");

                // Real dead weight now that it's beaten - Lucas's own
                // framing: "do I have this already? yes? throw it out."
                // MoveToContainer's own swap logic already displaced these
                // out of containerWear (into main, most likely) rather
                // than deleting them - drop them for real instead of
                // letting outclassed armor pile up as clutter. Still
                // valid Item references at this point, just relocated.
                foreach (Item displaced in conflicting)
                {
                    Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
                    Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

                    displaced.Drop(dropPosition, dropVelocity);
                }
            }
        }
    }

    /// <summary>
    /// Declutters armor/clothing that's genuinely worse than what's
    /// currently WORN - Lucas's own corrected framing: "a player with
    /// multiple sets of better armour is good, not bad. Having 3 sets of
    /// wood armour when the bot has metal plate is not good." Only ever
    /// compares an unworn piece against what's actually on the body, not
    /// against other unworn spares - owning several equal-or-better
    /// backup sets is deliberate, not clutter, so this only ever drops a
    /// piece that's strictly outclassed by something already worn (real
    /// CanExistWith slot-occupation check, not a guessed taxonomy).
    /// Separate pass from EvaluateAndUpgradeArmor's own upgrade-and-drop
    /// above, since that one only reacts to a NEW candidate arriving -
    /// this catches anything already sitting in inventory for any other
    /// reason (e.g. armor owned from before this rule existed).
    /// </summary>
    private void DropRedundantArmor(Survivor survivor, BasePlayer npc)
    {
        List<Item> worn = npc.inventory.containerWear.itemList;

        List<Item> unwornCandidates = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => item.info.GetComponent<ItemModWearable>() != null && !IsBackpackItem(item))
            .ToList();

        foreach (Item item in unwornCandidates)
        {
            ItemModWearable wearable = item.info.GetComponent<ItemModWearable>();
            float score = GetArmorProtectionScore(item);

            // Low-tier exact duplicate of something already worn - see
            // IsLowTierArmor's own doc comment. Catches pre-existing
            // duplicates (e.g. from before this rule existed, or a save
            // restored from an older version) that ShouldSkipInferiorArmor
            // now prevents going forward but never retroactively cleans
            // up on its own.
            bool lowTierDuplicateOfWorn = IsLowTierArmor(item.info.shortname)
                && worn.Any(wornItem => wornItem.info.shortname == item.info.shortname);

            bool outclassedByWorn = lowTierDuplicateOfWorn || worn.Any(wornItem =>
            {
                ItemModWearable wornWearable = wornItem.info.GetComponent<ItemModWearable>();

                if (wornWearable == null || wearable.CanExistWith(wornWearable))
                {
                    return false;
                }

                return GetArmorProtectionScore(wornItem) > score;
            });

            if (!outclassedByWorn)
            {
                continue;
            }

            Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
            Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

            item.Drop(dropPosition, dropVelocity);

            string reason = lowTierDuplicateOfWorn ? "a low-tier duplicate of what's already worn" : "worse than what's already worn";
            VerbosePuts($"'{survivor.Character.Alias}' dropped redundant '{item.info.shortname}' - {reason}.");
        }
    }

    /// <summary>
    /// Solid layers a real wall/floor/rock would sit on - deliberately
    /// narrower than NavigationManager's own ObstacleLayerMask (no Tree/
    /// Vehicle/Ragdoll), since those would incorrectly block a legitimate
    /// attack on a container standing near a fallen log or a corpse.
    ///
    /// Combat (LivingRust.Combat.cs) deliberately does NOT reuse this mask
    /// for its own LOS check - see CombatLineOfSightBlockingMask below for
    /// why the two need opposite Tree behaviour.
    /// </summary>
    private static readonly int LineOfSightBlockingMask = LayerMask.GetMask("Terrain", "World", "Construction");

    /// <summary>
    /// Combat's own LOS mask (2026-08-13 live report) - this one DOES
    /// include Tree, the opposite of LineOfSightBlockingMask's own choice
    /// right above, and for the same reason in reverse: a real bullet gets
    /// stopped by a tree trunk (BaseProjectile.ServerUse's own TraceAll
    /// call uses a much broader mask that includes it), so a combat LOS
    /// check that DOESN'T also treat a tree as blocking gives a false
    /// "clear shot" the moment a player ducks behind one - confirmed live:
    /// a player could kite a bot around a tree at 20m+ and bait it into
    /// continuously firing (and burning ammo) at a target it could never
    /// actually hit, since the plain container-LOS mask was never designed
    /// to block on flora at all.
    ///
    /// Default added the same day (Lucas's own follow-up request) for
    /// stone/metal/sulfur ore nodes - confirmed live via /lr.debug.scan
    /// against a real metal-ore node: OreResourceEntity sits on layer 0
    /// (Default), not a dedicated resource layer, so it needed the same
    /// treatment as Tree for the identical reason (a real bullet stops on
    /// it; the plain container-LOS mask never blocked on it either). The
    /// big surrounding rock formation meshes ore nodes usually sit in/near
    /// are already covered - confirmed via the same scan, those sit on
    /// layer 16 (World), already present above.
    /// </summary>
    private static readonly int CombatLineOfSightBlockingMask = LayerMask.GetMask("Terrain", "World", "Construction", "Tree", "Default");

    /// <summary>
    /// Combat-specific sibling of HasLineOfSight(BasePlayer, BaseEntity) -
    /// same Linecast-then-confirm-what-was-hit shape, but against
    /// CombatLineOfSightBlockingMask instead so a tree trunk correctly
    /// counts as blocking. Kept as a separate method (not just a shared
    /// mask parameter) since the two callers' correct behaviour around
    /// Tree is a genuine, deliberate difference, not an oversight either
    /// way - keeping them visually distinct code paths makes that harder
    /// to accidentally collapse back together later.
    /// </summary>
    private bool HasCombatLineOfSight(BasePlayer npc, BaseCombatEntity target)
    {
        Vector3 origin = npc.eyes.position;
        Vector3 targetPoint = GetAimPoint(target);

        if (!Physics.Linecast(origin, targetPoint, out RaycastHit hit, CombatLineOfSightBlockingMask, QueryTriggerInteraction.Ignore))
        {
            return true;
        }

        BaseEntity hitEntity = hit.collider.GetComponentInParent<BaseEntity>();

        return hitEntity != null && hitEntity.EqualNetID(target);
    }

    /// <summary>
    /// Whether npc is actually close enough to container to loot/attack it
    /// - see LootInteractionRange's own doc comment for why this exists as
    /// a hard, independent check rather than trusting "arrival" or line of
    /// sight alone.
    /// </summary>
    private bool IsWithinLootRange(BasePlayer npc, BaseEntity container)
    {
        Vector3 closestPoint = container.WorldSpaceBounds().ClosestPoint(npc.transform.position);

        return Vector3.Distance(npc.transform.position, closestPoint) <= LootInteractionRange;
    }

    /// <summary>
    /// Whether npc has a clear line to container - a Linecast that hits
    /// anything other than the container itself first means something
    /// solid (a wall, most likely) sits between them.
    /// </summary>
    private bool HasLineOfSight(BasePlayer npc, BaseEntity container)
    {
        Vector3 origin = npc.eyes.position;
        Vector3 targetPoint = container.WorldSpaceBounds().ClosestPoint(origin);

        if (!Physics.Linecast(origin, targetPoint, out RaycastHit hit, LineOfSightBlockingMask, QueryTriggerInteraction.Ignore))
        {
            return true;
        }

        BaseEntity hitEntity = hit.collider.GetComponentInParent<BaseEntity>();

        return hitEntity != null && hitEntity.EqualNetID(container);
    }

    private void CancelActiveAttack(Guid characterId)
    {
        if (_activeAttacks.TryGetValue(characterId, out Timer attackTimer))
        {
            attackTimer.Destroy();
            _activeAttacks.Remove(characterId);
        }
    }

    /// <summary>
    /// Moves every item from a container into the survivor's inventory,
    /// preferring main (MoveToContainer, which also preserves the
    /// original slot position where possible) but falling back to
    /// GiveItem - Rust's own general "put it wherever there's room"
    /// placement, which can land in the belt - when main won't take it,
    /// matching RestoreInventory's same two-step pattern. Only stops once
    /// BOTH main and belt are full (IsInventoryFull), not just main -
    /// the loop used to bail the moment main filled up even though the
    /// belt still had room, silently dropping loot on the ground that
    /// GiveItem could have placed.
    /// </summary>
    private int TransferAllItems(ItemContainer from, PlayerInventory to, BasePlayer npc)
    {
        return TransferAllItems(from, to, npc, out _);
    }

    /// <summary>
    /// movedShortnames exists specifically so callers can log what was
    /// actually picked up, not just how many stacks - a live report of
    /// "why isn't a looted tool getting used" was impossible to diagnose
    /// with only a count, since MeleeToolPriority's shortnames were never
    /// verified against a live item database and a silent mismatch there
    /// would look identical to "nothing better was ever looted at all."
    /// </summary>
    private int TransferAllItems(ItemContainer from, PlayerInventory to, BasePlayer npc, out List<string> movedShortnames)
    {
        int moved = 0;
        movedShortnames = new List<string>();

        // Copy first - MoveToContainer mutates from.itemList as it goes,
        // so iterating it directly would skip items.
        var items = new List<Item>(from.itemList);

        foreach (Item item in items)
        {
            // continue, not break - a full inventory should only skip
            // THIS item (unless it's high-priority enough to evict dead
            // weight for - see EnsureRoomFor), not abandon every
            // remaining item in the container, some of which might still
            // be exactly the kind of upgrade worth making room for.
            if (!EnsureRoomFor(npc, item))
            {
                continue;
            }

            if (TryTransferSingleItem(item, to, npc))
            {
                moved++;
                movedShortnames.Add(item.info.shortname);
            }
        }

        return moved;
    }

    /// <summary>
    /// Tool/weapon "families" where owning just ONE is genuinely enough -
    /// Lucas's own explicit framing: a second pickaxe doesn't gather any
    /// faster, a second hatchet doesn't chop any faster, it's pure
    /// inventory clutter. Bows/crossbows get the same treatment (his own
    /// explicit call-out) - unlike a real firearm, a spare bow isn't a
    /// meaningfully better-equipped survivor the way a spare AK is. Every
    /// other real weapon (shotguns, pistols, assault rifles, SMGs, ...) is
    /// deliberately NOT covered by this - his own reasoning: "a bot won't
    /// win a fight with 5 pickaxes, but 2 m249's and an ak... is a lot
    /// better," real Rust logic where extra firearms are genuine
    /// equipment upgrades (backups, ammo-type variety), not clutter.
    /// </summary>
    private static readonly string[] PickaxeFamily = { "pickaxe", "stone.pickaxe", "concretepickaxe", "diverpickaxe", "lumberjack.pickaxe", "icepick.salvaged" };
    private static readonly string[] HatchetFamily = { "hatchet", "stonehatchet", "concretehatchet", "diverhatchet", "lumberjack.hatchet", "frontier_hatchet", "axe.salvaged" };
    private static readonly string[] BowFamily = { "bow.compound", "bow.hunting", "crossbow", "crossbowbowless", "minicrossbow" };

    /// <summary>
    /// Added 2026-08-09 alongside the pickaxe/hatchet/bow families above -
    /// Lucas's own explicit follow-up naming maces specifically. mace and
    /// mace.baseballbat are functionally the same melee role (a real
    /// second one is exactly as redundant as a second hatchet).
    /// </summary>
    private static readonly string[] MaceFamily = { "mace", "mace.baseballbat" };

    /// <summary>
    /// Added 2026-08-10, Lucas's explicit request: bots were looting
    /// spare rocks off other survivors' corpses/bags even while already
    /// owning one - real dead weight, since a rock is the single worst
    /// tool in the game (the only reason a survivor ever holds one at all
    /// is GiveStartingKit's default before anything better turns up) and
    /// a second one adds nothing.
    /// </summary>
    private static readonly string[] RockFamily = { "rock" };

    /// <summary>
    /// Added 2026-08-10, Lucas's explicit request: "avoid looting
    /// multiple water bottle fillable containers." Normally a bottle gets
    /// drunk and dropped the instant it's picked up (DrinkWaterBottles),
    /// so this rarely matters - but if the survivor's already fully
    /// hydrated, ConsumeViaItemModConsume's CanDoAction correctly refuses
    /// to drink it (0 actions), and per DrinkWaterBottles' own doc
    /// comment nothing drops an UNdrunk bottle. Without this, that one
    /// still-full bottle would just sit in inventory while the bot kept
    /// picking up MORE of them from every corpse/bag it passes - this
    /// closes that gap at the pickup filter itself.
    /// </summary>
    private static readonly string[] WaterBottleFamily = { "smallwaterbottle" };

    // Any door counts as "the one door" (2026-09-21, Lucas's own explicit ask:
    // never carry more than one door or one tool cupboard - dead weight).
    private static readonly string[] DoorFamily =
    {
        "door.hinged.wood", "door.hinged.metal", "door.hinged.toptier",
        "door.double.hinged.wood", "door.double.hinged.metal", "door.double.hinged.toptier",
    };

    private static readonly string[][] SingleOwnershipFamilies = { PickaxeFamily, HatchetFamily, BowFamily, MaceFamily, RockFamily, WaterBottleFamily, new[] { HammerShortname }, new[] { BuildingPlannerShortname }, DoorFamily, new[] { ToolCupboardShortname } };

    /// <summary>
    /// Real items never worth picking up at all, regardless of what the
    /// survivor already owns - Lucas's own explicit examples: torches
    /// ("grade A dead weight" - the bot doesn't need light, see
    /// DropUnneededLightSource's own doc comment for why it doesn't even
    /// keep its OWN starting one) and seeds of any kind ("for noting not
    /// for implementing" - hemp/cloth gathering is real future work, but
    /// picking up seeds has no use at all until that exists). Exact
    /// shortnames for the real light-source torches only (confirmed via a
    /// scan of the bundled item database) - deliberately NOT
    /// industrial.torch (a real welding/repair TOOL, not a light source)
    /// or torchholder (a wall-mounted deployable, not a carried item).
    ///
    /// bone.fragments/humanmeat.raw/skull.human/grub/worm added 2026-08-14
    /// (Lucas's own explicit follow-up, "add... to the avoid list for
    /// eating and picking up") - all real, confirmed shortnames (Bundles\
    /// items\*.json). Both grub AND worm included even though Lucas only
    /// said "grubs (worms)" - two genuinely distinct real items ("Grub" and
    /// "Worm"), not two names for the same one, so both are covered rather
    /// than guessing which single one he meant. This same list already
    /// gates BOTH pickup paths Lucas asked about - TryTransferSingleItem
    /// (corpses/bags/containers) and TryFindNearestDroppedItem's own search
    /// filter (standalone dropped items) both check IsNeverLootItem, which
    /// reads straight from this array - no separate change needed for
    /// either.
    ///
    /// heavy.plate.helmet/jacket/pants added 2026-08-14 (Lucas's own
    /// explicit request: "have the bot avoid any heavy plate X armour
    /// (pants, chest, helmet, etc") - real, confirmed shortnames (Bundles\
    /// items\*.json, no separate boots variant exists). Unlike every other
    /// entry here this is actually the single BEST armor in the game
    /// defensively - excluded anyway per explicit request, presumably for
    /// its real, severe movement-speed penalty rather than because it's
    /// useless. Skipped at pickup entirely (not just "never worn") since
    /// there's nowhere for a bot to deposit/sell excess gear yet - same
    /// choke point (TryTransferSingleItem/TryFindNearestDroppedItem) as
    /// every other never-loot entry, so it's never carried at all, not
    /// just never equipped.
    ///
    /// rock added 2026-08-14 (real, confirmed bug report, not a fresh
    /// request) - RockFamily/SingleOwnershipFamilies (ShouldSkipDuplicateItem)
    /// only ever skipped a SECOND rock while the survivor still owned one;
    /// it said nothing about a rock the survivor no longer owns at all. A
    /// bot that drops its own starting rock the instant a real gathering
    /// tool displaces it (see EquipBest*/DropRedundant* elsewhere) leaves
    /// that exact rock sitting on the ground as a genuinely ownerless
    /// DroppedItem - the very next loot cycle's TryFindNearestDroppedItem
    /// search had nothing left excluding it (zero owned rocks = not a
    /// "duplicate"), so it walked back, picked it up, immediately re-
    /// evaluated as dead weight, dropped it again, and repeated forever.
    /// Full IsNeverLootItem exclusion fixes this the same way every other
    /// entry here does - a rock is now never picked up at all, regardless
    /// of current ownership, so there's nothing left to thrash between.
    /// RockFamily/SingleOwnershipFamilies is left in place - dead code for
    /// rock specifically now (IsNeverLootItem is checked first at the same
    /// choke point and already refuses it), but harmless, and still the
    /// live mechanism for the other real duplicate families (pickaxe/
    /// hatchet/bow/mace).
    /// </summary>
    private static readonly string[] NeverLootShortnames =
    {
        "torch", "torch.torch.skull", "divertorch",
        "bone.fragments", "humanmeat.raw", "skull.human", "grub", "worm",
        "heavy.plate.helmet", "heavy.plate.jacket", "heavy.plate.pants",
        "rock", "binoculars", "smallwaterbottle", "egg",
    };

    /// <summary>
    /// Real engine-component items dropped by the "vehicle_parts" loot
    /// crate (confirmed shortnames, Bundles\items\*.json) - Lucas's own
    /// explicit correction 2026-08-14: the whole container used to be
    /// excluded from the loot search entirely (IsVehiclePartsContainer,
    /// now removed), but it also drops real scrap/components a bot DOES
    /// want, so that threw those out along with the parts. Now the
    /// container itself is a normal candidate again (see the two
    /// TryFindNearestLootContainer filters above, IsVehiclePartsContainer
    /// no longer referenced there) and only these specific items are
    /// skipped at the same TryTransferSingleItem/TryFindNearestDroppedItem
    /// choke point every other NeverLootShortnames entry already uses -
    /// scrap and everything else in the crate still gets picked up
    /// normally. All three real Component-tier variants included for each
    /// part (carburetor/crankshaft/piston/sparkplug/valve all go 1-3),
    /// smallengine isn't tiered. "gears" removed from this list
    /// (2026-08-16, Lucas's own explicit request) - still no bot-crafting/
    /// vehicle-repair use case for it, but it's a real inventory item worth
    /// having on hand regardless. Still "grade A dead weight for now"
    /// reasoning for the rest of this list.
    /// </summary>
    private static readonly string[] VehiclePartShortnames =
    {
        "carburetor1", "carburetor2", "carburetor3",
        "crankshaft1", "crankshaft2", "crankshaft3",
        "piston1", "piston2", "piston3",
        "sparkplug1", "sparkplug2", "sparkplug3",
        "valve1", "valve2", "valve3",
        "smallengine",
    };

    /// <summary>
    /// Every real seed shortname shares this exact prefix (confirmed via
    /// a full scan of the bundled item database: seed.hemp, seed.corn,
    /// seed.potato, seed.pumpkin, every berry colour, seed.wheat,
    /// seed.sunflower, seed.rose, seed.orchid, ...) - matched by prefix
    /// rather than an exhaustive list so a future new seed type is
    /// covered automatically, same reasoning RequiresDestructionToLoot's
    /// own doc comment gives for its own substring matching.
    /// </summary>
    private const string SeedShortnamePrefix = "seed.";

    /// <summary>
    /// Every real scientist-exclusive suit shortname confirmed/plausible
    /// via the bundled AssetSceneManifest.json (Suit.Hazmat/Scientist/*,
    /// Suit.HeavyScientist/*, Suit.OutbreakScientist/*) - "hazmatsuit_
    /// scientist" itself confirmed live (a bot, AngryMiner2914, actually
    /// looted and wore one off a scientist corpse). Lucas's own framing:
    /// this genuinely isn't obtainable in real Rust at all - a real
    /// player can never loot a scientist's own worn suit off its corpse,
    /// it's NPC-exclusive gear. Matched by prefix since the confirmed
    /// live case and every other scientist-suit variant in the asset
    /// manifest (arctic/nvgm/naval/peacekeeper) share the "hazmatsuit_
    /// scientist" prefix, with a second prefix for the heavy/outbreak
    /// variants (different naming convention, "scientistsuit" rather
    /// than "hazmatsuit_scientist").
    /// </summary>
    private static readonly string[] ScientistExclusiveSuitPrefixes = { "hazmatsuit_scientist", "scientistsuit" };

    /// <summary>
    /// Decorative/furniture/junk-tier items Lucas explicitly listed
    /// 2026-08-10 as "all just dead weight that has no real application or
    /// would be hard to implement a bot using" - deployable decor
    /// (tables, rugs, BBQ, signs, picture frames, planters, spinning
    /// wheel, water barrel), scrap-tier junk-pile clutter (bone fragments,
    /// plant fiber, empty cans), and a batch of real but low-value/no-use
    /// items (flashlight, flares, handcuffs, blood, boomerang, butcher
    /// knife, eoka pistol, bone club) plus base-defense/barrier props
    /// (window bars, shopfront, shutters, sandbag barricade, floor
    /// spikes) that aren't meaningful without base-building existing yet
    /// (see the Base building roadmap entry - none built). Exact
    /// shortnames are best-effort (this project has no live item-
    /// enumeration helper to cross-check an unfamiliar shortname against,
    /// unlike the WeaponPriority/MeleeToolPriority lists which were built
    /// from a full offline scan) - ValidateNeverLootShortnames below
    /// checks every one against the real live item database at boot and
    /// logs a WARNING for anything that doesn't resolve, same pattern as
    /// ValidateWeaponPriority/ValidateMeleeToolPriority, so a wrong guess
    /// here is caught on the next server start instead of silently doing
    /// nothing forever.
    /// </summary>
    private static readonly string[] JunkDecorationShortnames =
    {
        // "Water Barrel" deliberately omitted - its asset exists in the
        // bundle (LiquidBarrel/waterbarrel.item.prefab) but doesn't
        // resolve to a live ItemDefinition (confirmed via
        // ValidateNeverLootShortnames' own WARNING), suggesting it may be
        // a legacy/unused asset in this Rust version, possibly superseded
        // by the Water Catcher Small/Large items. Flagged for Lucas to
        // confirm the real shortname next time a bot is near one, rather
        // than guessing further.
        "table", "clantable", "rug.bear", "bbq", "spinner.wheel",
        // can.tuna.empty/can.beans.empty (dot-separated, NOT the
        // underscore-separated prefab filename "can_tuna_empty" - tried
        // that first per usual convention, but it failed to resolve at
        // boot while the dot-separated version confirmed clean).
        // "electric.igniter" (real shortname confirmed live 2026-08-10 -
        // GhostBoomer had one in inventory, cross-referenced against
        // another bot's own loot-summary log line for the exact string;
        // the earlier bare "igniter" guess was wrong).
        "bone.fragments", "plantfiber", "can.tuna.empty", "can.beans.empty", "electric.igniter",
        // "fun.guitar" (real shortname confirmed live earlier this
        // session via FeralBandit's own loot-summary log line).
        "fun.guitar",
        "flashlight.held", "flare", "handcuffs", "blood", "boomerang",
        "knife.butcher", "pistol.eoka", "bone.club",
        "wall.window.bars.wood", "shutter.wood.a", "barricade.sandbags", "barricade.stone", "spikes.floor", "spikes.trap",
        "sign.wooden.small", "sign.wooden.medium", "sign.wooden.large", "sign.wooden.huge",
        // 2026-08-10 additions, real shortnames confirmed via AssetSceneManifest.json:
        "tunalight", "bucket.water",
        // "mailbox" (deployable item shortname, confirmed via
        // Bundles/items/mailbox.json - distinct from IsMailbox's own
        // container-prefab exclusion, which stops bots looting FROM a
        // placed mailbox; this stops them picking one UP as loot in the
        // first place, e.g. off a corpse or out of a container).
        "mailbox",
        // "trap.bear" (real item shortname, confirmed via
        // Bundles/items/trap.bear.json - NOT "beartrap", which is only the
        // world-entity prefab filename and doesn't resolve as an item; the
        // boot validator caught this guess wrong on the first try). Loot-
        // only exclusion, deliberately NOT also a physical movement
        // obstacle (Lucas's explicit correction - a placed bear trap in the
        // world isn't something bots need to route around, just something
        // they shouldn't pick up as loot).
        "trap.bear",
    };

    /// <summary>
    /// Prefix-matched siblings of JunkDecorationShortnames, for real
    /// families with several size/variant suffixes ("of any size"/"of any
    /// kind" in Lucas's own phrasing) rather than one exact shortname:
    /// sign posts (single/double/town/town.roof), picture frames
    /// (landscape/portrait/tall/xl/xxl), planter boxes (small/large), any
    /// rug (plain + bear - subsumes rug.bear above, redundant but
    /// harmless), spears (wooden/stone), and shopfront (metal/wood).
    /// Same "future variant covered automatically" reasoning
    /// SeedShortnamePrefix's own doc comment gives - deliberately NOT
    /// validated against the live item database the way the exact list
    /// above is, since a prefix has no single "does this resolve" check
    /// (same precedent as SeedShortnamePrefix/ScientistExclusiveSuitPrefixes).
    /// </summary>
    private static readonly string[] JunkDecorationPrefixes =
    {
        "sign.post", "sign.pictureframe", "planter", "rug", "spear.", "shopfront",
    };

    /// <summary>
    /// Checks every JunkDecorationShortnames entry against the real, live
    /// item database at startup - see JunkDecorationShortnames' own doc
    /// comment for why this list specifically needed it (best-effort
    /// guesses, no offline scan backing them the way WeaponPriority had).
    /// </summary>
    private void ValidateNeverLootShortnames()
    {
        List<string> unresolved = new();

        foreach (string shortname in JunkDecorationShortnames)
        {
            if (ItemManager.FindItemDefinition(shortname) == null)
            {
                unresolved.Add(shortname);
            }
        }

        if (unresolved.Count > 0)
        {
            Puts($"WARNING: JunkDecorationShortnames contains {unresolved.Count} shortname(s) that don't resolve to any real item - these will never be excluded from looting since they don't match anything: {string.Join(", ", unresolved)}.");
        }
        else
        {
            Puts($"JunkDecorationShortnames validated - all {JunkDecorationShortnames.Length} shortnames resolve to real items.");
        }
    }

    /// <summary>
    /// Whether item is on the "never worth picking up" list - see
    /// NeverLootShortnames/SeedShortnamePrefix/
    /// ScientistExclusiveSuitPrefixes's own doc comments. Deliberately
    /// independent of ownership (unlike ShouldSkipDuplicateItem) - these
    /// aren't "fine once, redundant after," they're just never useful
    /// (or, for the scientist suits, never legitimately obtainable at
    /// all) to this bot right now.
    /// </summary>
    private static bool IsNeverLootItem(string shortname)
    {
        return Array.IndexOf(NeverLootShortnames, shortname) >= 0
            || shortname.StartsWith(SeedShortnamePrefix, StringComparison.OrdinalIgnoreCase)
            || Array.Exists(ScientistExclusiveSuitPrefixes, prefix => shortname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || Array.IndexOf(JunkDecorationShortnames, shortname) >= 0
            || Array.Exists(JunkDecorationPrefixes, prefix => shortname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || Array.IndexOf(VehiclePartShortnames, shortname) >= 0;
    }

    /// <summary>
    /// Whether npc should skip picking up shortname because it already
    /// owns something from the same SingleOwnershipFamilies entry - see
    /// that field's own doc comment. Checked against the survivor's whole
    /// inventory (main + belt), not just what's currently equipped, since
    /// a spare hatchet sitting unequipped in the bag is exactly as
    /// redundant as one on the belt.
    /// </summary>
    private bool ShouldSkipDuplicateItem(BasePlayer npc, string shortname)
    {
        // A survivor with a ready firearm has no use for a bow or arrows
        // (2026-09-21, Lucas's own spec).
        if ((Array.IndexOf(NonCombatCapableRangedWeaponShortnames, shortname) >= 0 || shortname.StartsWith("arrow.", StringComparison.Ordinal))
            && HasReadyFirearm(npc))
        {
            return true;
        }

        foreach (string[] family in SingleOwnershipFamilies)
        {
            if (Array.IndexOf(family, shortname) < 0)
            {
                continue;
            }

            return npc.inventory.containerMain.itemList
                .Concat(npc.inventory.containerBelt.itemList)
                .Any(item => Array.IndexOf(family, item.info.shortname) >= 0);
        }

        return false;
    }

    /// <summary>
    /// Whether npc should skip picking up a wearable candidate because it
    /// already owns something (worn OR unworn - a spare in the bag is
    /// just as much "already have this" as one on the body) that
    /// genuinely conflicts with it (real CanExistWith slot check) and
    /// scores as good or better. Lucas's own framing, applied at the
    /// moment of picking the item up rather than after: "do they have
    /// wooden armour and I have road sign jacket? yes? I don't take the
    /// armour." The inverse (a real upgrade) is deliberately NOT decided
    /// here - EvaluateAndUpgradeArmor still does that full evaluation
    /// (and the actual equip + drop-the-old-one) once the item is
    /// actually in inventory; this is purely the "don't even bother
    /// carrying this, it's already outclassed" pre-filter.
    /// </summary>
    private bool ShouldSkipInferiorArmor(BasePlayer npc, Item candidate)
    {
        // A backpack has its own slot and its own rule (2026-09-21) - skip it
        // only if the worn one is at least as big.
        if (IsBackpackItem(candidate))
        {
            Item wornBackpack = GetWornBackpack(npc);

            return wornBackpack != null && GetBackpackCapacity(wornBackpack) >= GetBackpackCapacity(candidate);
        }

        ItemModWearable candidateWearable = candidate.info.GetComponent<ItemModWearable>();

        if (candidateWearable == null)
        {
            return false;
        }

        // Low-tier exact duplicates are skipped outright, regardless of
        // score - see IsLowTierArmor's own doc comment. Checked against
        // everything already owned (worn AND unworn in main/belt), unlike
        // the tier/score comparison below which is worn-only - a spare
        // balaclava already in the bag is just as pointless as a second
        // one on the body.
        if (IsLowTierArmor(candidate.info.shortname)
            && npc.inventory.containerWear.itemList
                .Concat(npc.inventory.containerMain.itemList)
                .Concat(npc.inventory.containerBelt.itemList)
                .Any(owned => owned.info.shortname == candidate.info.shortname))
        {
            return true;
        }

        float candidateScore = GetArmorProtectionScore(candidate);

        // Compared against WORN items only, not everything owned - a live
        // correction: "a player with multiple sets of better armour is
        // good, not bad." Owning a second (or third) piece equal to or
        // better than what's currently worn is a real, deliberate backup/
        // upgrade reserve, not clutter - only something genuinely WORSE
        // than what's already on the body is worth skipping. Strict ">"
        // (not ">=") specifically so an equal-tier piece still gets
        // picked up as a spare, per the same correction - this general
        // rule is what the low-tier exact-duplicate check above
        // deliberately overrides for cheap gear specifically.
        return npc.inventory.containerWear.itemList.Any(worn =>
        {
            ItemModWearable wornWearable = worn.info.GetComponent<ItemModWearable>();

            if (wornWearable == null || candidateWearable.CanExistWith(wornWearable))
            {
                return false;
            }

            return GetArmorProtectionScore(worn) > candidateScore;
        });
    }

    /// <summary>
    /// Full real-world looting priority order, Lucas's own explicit
    /// ranking (2026-08-09), highest to lowest: weapons -&gt; ammunition OR
    /// explosives (grenades, rocket ammo, timed charges/satchels) -&gt;
    /// medical -&gt; armor (handled by its own dedicated logic - see
    /// ShouldSkipInferiorArmor/DropRedundantArmor - so anything that
    /// actually reaches this tier already cleared that bar) -&gt; deployable
    /// workbenches -&gt; scrap metal specifically (not components) -&gt; sulfur
    /// ore/refined sulfur -&gt; metal ore/metal fragments -&gt; wood OR stone -&gt;
    /// everything else real Resources/Component items (sheet metal,
    /// propane tank, metal pipes, ...). Used to decide what gets evicted
    /// to make room for what - Lucas's own example: "I just killed this
    /// player but my inventory is full... drop the excess components or
    /// scrap to replace them with higher tier items."
    /// </summary>
    private enum LootPriorityTier
    {
        Weapon = 0,
        AmmoOrExplosive = 1,
        Medical = 2,
        Armor = 3,
        DeployableWorkbench = 4,
        ScrapMetal = 5,
        Sulfur = 6,
        MetalOreOrFragments = 7,
        WoodOrStone = 8,
        Components = 9,

        /// <summary>
        /// Anything not otherwise classified - foodstuffs, tools (their
        /// own dedicated single-ownership logic already governs whether
        /// they're worth having at all), misc junk. Never evicted FOR
        /// (nothing "unimportant enough" to make room for), but can still
        /// be evicted BY anything above it.
        /// </summary>
        Other = 10,
    }

    private static readonly string[] DeployableWorkbenchShortnames = { "workbench1", "workbench2", "workbench3" };

    /// <summary>
    /// Real per-item tier lookup. Grenades/rocket-ammo/raid charges are
    /// deliberately pulled OUT of their raw ItemCategory (grenades are
    /// real ItemCategory.Weapon; C4/satchel charges are real
    /// ItemCategory.Tool, confirmed via the bundled item database) and
    /// into AmmoOrExplosive specifically - Lucas's own tier list groups
    /// consumable ordnance with ammunition, separate from the reusable
    /// weapons tier above it, which real Rust's own category field
    /// doesn't distinguish on its own.
    /// </summary>
    private static LootPriorityTier GetLootPriorityTier(Item item)
    {
        if (IsBackpackItem(item))
        {
            return LootPriorityTier.Armor;
        }

        string shortname = item.info.shortname;
        ItemCategory category = item.info.category;

        bool isConsumableOrdnance = shortname.StartsWith("grenade.", StringComparison.OrdinalIgnoreCase)
            || shortname == "explosive.timed"
            || shortname == "explosive.satchel";

        if (category == ItemCategory.Weapon && !isConsumableOrdnance)
        {
            return LootPriorityTier.Weapon;
        }

        if (category == ItemCategory.Ammunition || isConsumableOrdnance)
        {
            return LootPriorityTier.AmmoOrExplosive;
        }

        if (category == ItemCategory.Medical)
        {
            return LootPriorityTier.Medical;
        }

        if (item.info.GetComponent<ItemModWearable>() != null)
        {
            return LootPriorityTier.Armor;
        }

        // Blueprint fragments always rank above ores of any kind and components
        // (2026-09-21, Lucas's own spec) - same tier as deployable workbenches.
        if (shortname == "basicblueprintfragment" || shortname == "advancedblueprintfragment")
        {
            return LootPriorityTier.DeployableWorkbench;
        }

        if (Array.IndexOf(DeployableWorkbenchShortnames, shortname) >= 0)
        {
            return LootPriorityTier.DeployableWorkbench;
        }

        if (shortname == "scrap")
        {
            return LootPriorityTier.ScrapMetal;
        }

        if (shortname == "sulfur.ore" || shortname == "sulfur")
        {
            return LootPriorityTier.Sulfur;
        }

        if (shortname == "metal.ore" || shortname == "metal.fragments")
        {
            return LootPriorityTier.MetalOreOrFragments;
        }

        if (shortname == "wood" || shortname == "stones")
        {
            return LootPriorityTier.WoodOrStone;
        }

        if (category == ItemCategory.Resources || category == ItemCategory.Component)
        {
            return LootPriorityTier.Components;
        }

        return LootPriorityTier.Other;
    }

    // Cooldown for ContinueLootTask's own base-less full-inventory drop
    // fallback (2026-09-19) - see its call site's own doc comment.
    private const float FullInventoryDropCooldownSeconds = 10f;

    private readonly Dictionary<Guid, float> _lastFullInventoryDropTime = new();

    /// <summary>
    /// True immediately if there's already room; for a genuinely full
    /// inventory, only tries to make room (by dropping the single lowest-
    /// priority item the survivor owns that's ranked BELOW item's own
    /// tier - DropLowerPriorityItem) if item is high-priority enough to
    /// be worth evicting for at all (anything above LootPriorityTier.Other).
    /// A tier-9 component doesn't evict another tier-9 component just
    /// because inventory's full - only a genuine step up the priority
    /// list earns a swap.
    /// </summary>
    private bool EnsureRoomFor(BasePlayer npc, Item item)
    {
        if (!IsInventoryFull(npc.inventory) || HasBackpackRoom(npc))
        {
            return true;
        }

        LootPriorityTier candidateTier = GetLootPriorityTier(item);

        if (candidateTier == LootPriorityTier.Other)
        {
            return false;
        }

        return DropLowerPriorityItem(npc, candidateTier);
    }

    /// <summary>
    /// Drops exactly one real item ranked below candidateTier (the single
    /// LOWEST-priority one owned, so the least valuable thing goes first)
    /// to free a slot - the actual real Item.Drop mechanism, same as
    /// every other "doesn't need this anymore" case in this project
    /// (torch, rock, outclassed armor), not a delete. Returns false
    /// (nothing dropped, no room made) if the survivor owns nothing
    /// ranked lower than candidateTier at all - a full inventory of
    /// nothing but higher-or-equal-priority items is left alone rather
    /// than sacrificing something that actually matters just as much.
    /// </summary>
    private bool DropLowerPriorityItem(BasePlayer npc, LootPriorityTier candidateTier)
    {
        // Keycards protected from eviction (2026-08-21, Lucas's own
        // explicit request: "bots should never drop keycards, like ever") -
        // real live bug this fixes: keycards have no dedicated
        // LootPriorityTier of their own, so they fell into the catch-all
        // Other tier (lowest priority, first evicted whenever inventory's
        // full) - a live trace showed a survivor repeatedly drop-then-
        // immediately-re-pick-up its own keycard_green/blue/red as
        // inventory oscillated between full and not-full (15+ "picked up a
        // dropped keycard_X" lines in about 10 real seconds), burning
        // nothing functionally but reading as a genuinely broken loop and
        // risking the bot walking off without a keycard a puzzle route
        // still needs.
        //
        // Green and blue are both exceptions (Lucas's own same-day follow-
        // ups - green first, then blue "if a bot does however get multiple
        // blue cards, a max of 3 should be carried with them getting
        // evicted in favour of the same rules applied for green
        // keycards"): ShouldSkipExcessKeycard already caps normal PICKUP at
        // each one's own MaxOwned, but a survivor can still end up owning
        // more (spawncardtest, an admin give, a save from before that cap
        // existed) - anything beyond the real cap is a genuine spare
        // duplicate, not a protected puzzle tool, so it stays evictable in
        // favor of something Lucas explicitly called out as more valuable
        // (a weapon or medium/high-tier armor - both already rank above
        // Other in LootPriorityTier, so they'd win this comparison
        // naturally once the excess copies stop being blanket-protected).
        // Red keycards get NO such exception at all (Lucas's own explicit
        // instruction: "no exception, it never drops them or avoids them")
        // - no cap, no eviction, period, regardless of how many are owned.
        int ownedGreenKeycards = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Count(owned => owned.info.shortname == GreenKeycardShortname);

        int ownedBlueKeycards = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Count(owned => owned.info.shortname == BlueKeycardShortname);

        Item deadWeight = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(owned => GetDropRank(npc, owned) > (int)candidateTier
                && (!KeycardShortnames.ContainsValue(owned.info.shortname)
                    || (owned.info.shortname == GreenKeycardShortname && ownedGreenKeycards > GreenKeycardMaxOwned)
                    || (owned.info.shortname == BlueKeycardShortname && ownedBlueKeycards > BlueKeycardMaxOwned)))
            .OrderByDescending(owned => GetDropRank(npc, owned))
            .FirstOrDefault();

        if (deadWeight == null)
        {
            return false;
        }

        Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
        Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

        deadWeight.Drop(dropPosition, dropVelocity);

        return true;
    }

    /// <summary>
    /// Real green/blue keycard shortnames (Rust's own item database) -
    /// each capped at its own MaxOwned rather than folded into
    /// SingleOwnershipFamilies/ShouldSkipDuplicateItem (2026-08-16, Lucas's
    /// own explicit request for green: "have the bot only pickup 2 green
    /// cards in total, if it has more than 2, avoid them"; extended
    /// 2026-08-21 to blue at a cap of 3, same reasoning, Lucas's own
    /// explicit follow-up) since that system only ever supports a cap of
    /// exactly 1 (any owned = skip), not a genuine numeric limit. Red
    /// deliberately has NO cap/entry here at all (Lucas's own explicit
    /// instruction: "Red keycards, no exception, it never drops them or
    /// avoids them") - red stays fully protected and always pickable,
    /// unlike green/blue which both get sacrificed past their own cap in
    /// favor of something more valuable (see DropLowerPriorityItem's own
    /// doc comment).
    /// </summary>
    private const string GreenKeycardShortname = "keycard_green";
    private const int GreenKeycardMaxOwned = 2;
    private const string BlueKeycardShortname = "keycard_blue";
    private const int BlueKeycardMaxOwned = 3;

    /// <summary>
    /// Whether npc should skip picking up shortname because it already owns
    /// that keycard's own cap (GreenKeycardMaxOwned/BlueKeycardMaxOwned) or
    /// more - checked against the survivor's whole inventory (main + belt),
    /// same scope ShouldSkipDuplicateItem already uses for its own
    /// duplicate checks. Only ever true for green/blue; every other item
    /// (including red, which has no cap) is untouched.
    /// </summary>
    private bool ShouldSkipExcessKeycard(BasePlayer npc, string shortname)
    {
        int maxOwned;

        if (shortname == GreenKeycardShortname)
        {
            maxOwned = GreenKeycardMaxOwned;
        }
        else if (shortname == BlueKeycardShortname)
        {
            maxOwned = BlueKeycardMaxOwned;
        }
        else
        {
            return false;
        }

        int owned = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Count(item => item.info.shortname == shortname);

        return owned >= maxOwned;
    }

    /// <summary>
    /// The actual real per-item transfer both TransferAllItems (bulk) and
    /// LootCorpseAndContinue's paced per-item loop use - pulled out once
    /// the corpse loot pacing needed the exact same "try main inventory,
    /// fall back to GiveItem's own smarter placement" logic one item at a
    /// time instead of all at once. Also where IsNeverLootItem/
    /// ShouldSkipDuplicateItem/ShouldSkipInferiorArmor/
    /// ShouldSkipExcessKeycard are all enforced - a single choke point
    /// every caller shares, so a container full of loot correctly leaves a
    /// torch, a seed packet, a redundant 3rd hatchet, an already-outclassed
    /// armor piece, or a 3rd green keycard behind while still taking
    /// everything else in the same container - applies identically to
    /// barrels/crates, corpses, and dropped bags, since they all funnel
    /// through here.
    /// </summary>
    private bool TryTransferSingleItem(Item item, PlayerInventory to, BasePlayer npc)
    {
        if (IsNeverLootItem(item.info.shortname)
            || ShouldSkipDuplicateItem(npc, item.info.shortname)
            || ShouldSkipInferiorArmor(npc, item)
            || ShouldSkipExcessKeycard(npc, item.info.shortname)
            || ShouldSkipExcessWood(npc, item))
        {
            return false;
        }

        Item wornBackpack = GetWornBackpack(npc);

        // Bulk goods and spares go in the backpack FIRST (2026-09-21, Lucas's
        // own spec: extra weapons, spare armour/clothing, ores, wood, stones
        // etc fill the backpack), leaving the main slots for what the
        // survivor actively uses. Anything better than what it already
        // owns still goes to main so the equip logic (which only looks at
        // main/belt) can see it.
        if (wornBackpack?.contents != null && ShouldPreferBackpack(npc, item) && item.MoveToContainer(wornBackpack.contents))
        {
            return true;
        }

        if (item.MoveToContainer(to.containerMain) || to.GiveItem(item))
        {
            return true;
        }

        return wornBackpack?.contents != null && item.MoveToContainer(wornBackpack.contents);
    }

    // Real cap on wood picked up from containers/corpses/dropped bags
    // (2026-09-21, live report: '51RustyBuzzard' carrying 10,000 wood).
    // HasEnoughWoodAlready only ever gated the ways a survivor FELLS wood;
    // wood also arrives by looting other survivors' corpses and dropped
    // bags (a killed bot's whole stockpile), which had no cap at all, so
    // stockpiles simply snowballed from bot to bot. The biggest real base
    // design needs ~2,100 wood (+1,000 upkeep buffer), so this leaves room.
    private const int WoodPickupCap = 3500;

    private bool ShouldSkipExcessWood(BasePlayer npc, Item item)
    {
        if (item.info.shortname != WoodShortname)
        {
            return false;
        }

        return npc.inventory.GetAmount(item.info.itemid) >= WoodPickupCap;
    }

    private bool IsContainerFull(ItemContainer container)
    {
        return container.itemList.Count >= container.capacity;
    }

    /// <summary>
    /// Whether both main and belt are full - wear isn't checked, since
    /// that's for worn clothing/armor slots specifically, not general
    /// carry capacity the way "toolbelt/inventory" was meant.
    /// </summary>
    private bool IsInventoryFull(PlayerInventory inventory)
    {
        return IsContainerFull(inventory.containerMain) && IsContainerFull(inventory.containerBelt);
    }

    // ---------------------------------------------------------------
    // Stuck recovery - an escalating "try everything before giving up"
    // sequence for when StartWalking's own onFailed fires (genuine
    // Stuck/NoPath, or the no-real-progress give-up), built specifically
    // for the two failure modes actually seen in live traces this
    // session: a bot standing on a small navmesh island disconnected
    // from the rest of the map (confirmed via decompiled-evidence NoPath
    // results at every distance tried), and a bot fighting local
    // obstruction near cluttered terrain (junkpile scatter) for the
    // final meter or two of an otherwise-real path. Currently wired into
    // the loot task only (ContinueLootTask calls StartWalkingWithRecovery
    // instead of StartWalking directly) - kept scoped here rather than
    // promoted to a general Commands.cs primitive until another caller
    // actually needs it.
    //
    // Tier 0 - wiggle: a few short local steps (back, left, right,
    // forward, in that order - backing away first reads as the most
    // natural "oops, blocked" reaction, forward is literally the
    // direction that got it stuck so it's tried last) using the exact
    // same local-stepping primitives ordinary walking already uses
    // (NavigationManager.TryGetNextStep + ApplyMovementStep), spread
    // over real ticks like a normal walk rather than an instant snap -
    // the whole point is to look like a real player trying a different
    // direction, not a teleport in disguise.
    // Tier 1 - navmesh nudge: NavMesh.SamplePosition to find the nearest
    // point actually on Rust's baked navmesh within a growing radius,
    // then a real StartWalking there - aimed specifically at the
    // "standing on a disconnected island" failure mode, since that's a
    // real fix for the actual problem rather than a guess.
    // Tier 2 - emergency teleport (deliberately last resort): a short
    // (3-5m), validated relocation - checked against real ground and a
    // clear landing spot before committing, so it can't relocate a
    // survivor into an equally bad spot. Only reached after both cheaper
    // tiers already failed.
    // Tier 3 - genuine give-up: every option exhausted, hand back to
    // whatever the original caller wanted to happen on failure.
    //
    // Each tier retries the ORIGINAL destination after succeeding
    // locally - the goal at every stage is "become unstuck enough for
    // the real walk to work," not to replace it.
    // ---------------------------------------------------------------

    // Real pause before each escalation tier/wiggle-direction change - a
    // live report noted that when a survivor is genuinely surrounded
    // (every wiggle direction blocked on its very first probe tick, the
    // navmesh nudge unreachable), the WHOLE wiggle -> nudge -> teleport
    // chain could resolve in well under a second - functionally correct,
    // but reads as an instant glitch/warp to a real player watching,
    // rather than a bot visibly trying something. Facing toward the next
    // thing being attempted (the next wiggle direction, or the
    // destination before a tier change) during this pause is what turns
    // it into a believable "looking around, reconsidering" beat instead
    // of a dead, silent delay.
    private const float StuckReassessPause = 0.5f;

    /// <summary>
    /// Angles (degrees, off the straight line from npc to destination)
    /// tried in turn when tier 0 wiggling. Previously a fixed
    /// facing-relative Vector3 set (back/left/right/forward) - a live
    /// report caught the "back" entry doing real damage: it's a pure 180°
    /// reversal of whatever direction the survivor was already facing
    /// (i.e. toward destination), which almost always succeeds (it's
    /// retracing the exact path just walked), reports wiggled=true, and
    /// immediately retries the SAME destination - walking straight back
    /// into the identical block. An angled offset off the destination
    /// direction actually changes the approach line instead of just
    /// retreating and re-approaching the same one. Same fix, same
    /// reasoning as ContainerRepositionAngles - see its own doc comment.
    /// </summary>
    private static readonly float[] WiggleAngles = { 45f, -45f, 90f, -90f, 135f, -135f };
    private const float WiggleProbeDistance = 2f;
    private const int WiggleMaxStepsPerDirection = 20;
    private const float WiggleMinProgressDistance = 1f;

    private static readonly float[] NavMeshNudgeRadii = { 5f, 10f, 15f };

    private const int EmergencyTeleportAttempts = 6;
    private const float EmergencyTeleportMinDistance = 3f;
    private const float EmergencyTeleportMaxDistance = 5f;

    /// <summary>
    /// Character.Id -> the real-time timestamp this survivor entered
    /// stuck-recovery (EscalateStuckRecovery, tier 0) and hasn't yet
    /// resolved - backs /lr.tp.stuck (2026-08-15) so an admin can jump
    /// straight to whichever bot is actually bugged out right now instead
    /// of eyeballing 200 bots on the map. Set/cleared only from
    /// EscalateStuckRecovery itself (see its own doc comment) - a survivor
    /// present here has been stuck continuously since the stored timestamp.
    /// </summary>
    private readonly Dictionary<Guid, float> _stuckSince = new();

    private void MarkStuck(Survivor survivor)
    {
        if (!_stuckSince.ContainsKey(survivor.Character.Id))
        {
            _stuckSince[survivor.Character.Id] = Time.realtimeSinceStartup;
        }
    }

    private void ClearStuck(Survivor survivor)
    {
        _stuckSince.Remove(survivor.Character.Id);
    }

    /// <summary>
    /// Drop-in replacement for StartWalking that runs the full stuck-
    /// recovery escalation (see the section comment above) before
    /// finally surfacing onFailed to the caller.
    /// </summary>
    // How close to home even bothers considering a door crossing at all
    // (2026-08-29) - just a coarse "is this even worth checking" gate for
    // ExitHomeIfInside, wide enough that a survivor anywhere near its own
    // base is covered.
    private const float HomeCrossingRadius = 20f;

    // Real minimum pause after opening a recorded door route's door, before
    // playback starts (2026-09-01, live report: "instant inside->outside in
    // a split second... doors didn't even open"). These recorded routes
    // only span a few metres (the recording itself was slow/careful), and
    // StartGhostRoute always plays back at fixed RunSpeed regardless of the
    // original recorded pacing - covering a few metres at running speed
    // takes well under a second, not enough time for the door's own real
    // swing animation to even be visible before the survivor's already
    // past it. This is a deliberate, isolated fix at the two door-route
    // call sites only - NOT a change to StartGhostRoute's own shared
    // playback speed, which every monument route also depends on and has
    // its own long history of carefully live-tuned fixes.
    private const float DoorRouteOpenAnimationDelay = 0.6f;

    // Real fix for the false-positive door-hijack (2026-09-01, live report:
    // "constantly phasing up and down through the ceiling and opening the
    // door/closing it non-stop" while the bot was actually just walking to
    // an ordinary outdoor task destination - an ore node/fuel point ~8m
    // from the cupboard, well outside the base's actual walls). The ORIGINAL
    // 20m HomeCrossingRadius was being reused as the inside-vs-outside test
    // for BOTH TryGetHomeDoorCrossing and TryStartHomeDoorRouteCrossing -
    // correct for "starting deep inside and heading well outside" as the
    // doc comment above says, but 20m is far wider than any real tier0
    // footprint (doors sit ~4-5m from the cupboard in every recorded base
    // so far), so ANY ordinary task destination that merely happens to
    // land within 20m of the cupboard - not actually inside the walls -
    // got misclassified as "entering home" and forced through the
    // recorded/computed door route, which has no idea the real destination
    // was never past the door at all. Tuned specifically to tier0 (the
    // only tier currently in scope) - comfortably past every recorded
    // tier0 door distance, comfortably short of the false-trigger distance
    // actually observed (~8.3m). Revisit if/when tier1+ bases are added -
    // a larger design could need a bigger value here.
    private const float HomeInteriorRadius = 6f;

    /// <summary>
    /// Real proactive door-crossing detection (2026-08-29, second round -
    /// Lucas's own explicit ask: "is it possible to have a ghostroute
    /// added for the different bases... so it knows how to get in and
    /// out?"). Reactive stuck-detection (EscalateStuckRecovery's own
    /// closed-door branch) turned out not reliable enough on its own -
    /// this catches the crossing BEFORE the survivor ever gets close
    /// enough to trigger a stuck episode at all, using the real computed
    /// route ComputeHomeDoorRoute already built (LivingRust.BaseBuilding.cs)
    /// the moment this specific base finished building. A plain distance-
    /// from-cupboard test on both ends of the walk (not a real geometric
    /// "which side of the door plane" check) - simple, and correct for
    /// exactly the shape of walk this needs to catch: starting deep inside
    /// a base and heading well outside it, or the reverse, not a walk that
    /// merely passes near the perimeter without crossing through.
    /// </summary>
    private bool TryGetHomeDoorCrossing(Survivor survivor, Vector3 destination, out Vector3 nearPoint, out Vector3 farPoint)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || home.DoorPosition == Vector3.zero || npc == null)
        {
            nearPoint = default;
            farPoint = default;
            return false;
        }

        // Real live bug (2026-08-29, second round - Lucas's own report:
        // right after settask, the bot ran the WHOLE crossing dance 2-3
        // times in a row before finally heading off to loot). Root cause:
        // a normal loot decision loop re-evaluates/re-issues a fresh
        // top-level walk request (recoveryTier 0) more than once in quick
        // succession as it settles on a real target - each one
        // independently re-triggered the FULL near-point -> door -> far-
        // point sequence from scratch while a previous one was still
        // running. A DISTANCE-based "already near the door" guard was
        // tried here first and made things WORSE (2026-08-29, third round -
        // Lucas's own report: "just phases through the walls, doesn't
        // even go near the doors... straight through the walls") - a
        // survivor that just finished BUILDING its own door is almost
        // always already standing close to it, so that guard was
        // disabling the crossing on the very first, correct attempt too,
        // silently falling through to the general last-resort phase
        // fallback instead (which has no door awareness at all - hence
        // straight through the nearest wall). A real per-Character busy
        // flag is the correct fix instead - skip only while a crossing
        // for THIS survivor is actually still in flight, never based on
        // where it happens to be standing.
        if (_activeHomeDoorCrossings.Contains(survivor.Character.Id))
        {
            nearPoint = default;
            farPoint = default;
            return false;
        }

        float thresholdSqr = HomeInteriorRadius * HomeInteriorRadius;
        bool currentlyInside = (npc.transform.position - home.Position).sqrMagnitude < thresholdSqr;
        bool destinationInside = (destination - home.Position).sqrMagnitude < thresholdSqr;

        if (currentlyInside == destinationInside)
        {
            nearPoint = default;
            farPoint = default;
            return false;
        }

        // Real live bug (2026-08-29, fourth round - Lucas's own report:
        // "as soon as I do settask they instantly teleport out of the
        // foundation"). This was backwards: nearPoint is meant to be the
        // point reachable via ordinary collision-respecting StartWalking
        // WITHOUT crossing the door (same side the survivor is already
        // on), with CrossHomeDoor only taking over once actually there.
        // Swapped, a currently-inside survivor got handed OutsidePoint -
        // a point on the FAR side of its own closed door/wall - as a
        // plain walk target, which StartWalking's real collision has no
        // way to reach, immediately failing onto a raw walk straight at
        // the real final destination instead (skipping CrossHomeDoor's
        // open/phase/close sequence entirely) and racing up the general
        // stuck-recovery ladder toward its own last-resort teleport/phase
        // tiers almost immediately - reading exactly as "instantly
        // teleports out."
        nearPoint = currentlyInside ? home.InsidePoint : home.OutsidePoint;
        farPoint = currentlyInside ? home.OutsidePoint : home.InsidePoint;

        VerbosePuts($"home-door-crossing: '{survivor.Character.Alias}' computed-geometry crossing triggered (currentlyInside={currentlyInside}) toward {destination}.");

        return true;
    }

    // Real per-Character busy flag (2026-08-29) - see
    // TryGetHomeDoorCrossing's own doc comment for the full story on why
    // this replaced an earlier, broken distance-based guard. Set the
    // moment a crossing starts, cleared once the whole sequence (open,
    // cross, close) genuinely finishes or gives up - never left set on a
    // path that doesn't clear it.
    private readonly HashSet<Guid> _activeHomeDoorCrossings = new();

    /// <summary>
    /// Real precomputed crossing (2026-08-29, second round - Lucas's own
    /// explicit ask: "the bot will have to open and close the door upon
    /// leaving the base and also when entering... if the doors are left
    /// open it defeats the purpose of having doors with codelocks").
    /// Finds the real live Door entity nearest the base's own known
    /// DoorPosition (found fresh each time rather than stored by
    /// NetworkableId, so this self-heals if the door entity ever gets
    /// recreated), opens it if closed, phases straight through the door's
    /// own center and out the far side - the same real "known point, no
    /// pathfinding needed" approach GhostThroughOwnDoor already uses, just
    /// anchored to the design's own precomputed route - then closes it
    /// again once safely clear of the frame, real security restored
    /// either direction.
    /// </summary>
    /// <summary>
    /// Real isolated fallback (2026-09-01, Lucas's own explicit call after
    /// a long session fighting collision/navmesh/network-sync issues on
    /// the "realistic" door crossing: "resort to purely ghostroute into
    /// the base... deposit loot... craft items"). Deliberately touches
    /// NONE of tonight's other crossing machinery - no _activeHomeDoorCrossings
    /// guard, no collision walk, no navmesh dependency, no busy-flag races
    /// with ExitHomeIfInside/TryStartHomeDoorRouteCrossing. Just chains
    /// plain StartPhasingToDestination (the same proven primitive
    /// StartGhostRoute itself is built on, and monument routes have used
    /// reliably all along) across every recorded door route's waypoints in
    /// nearest-first order, ending at the cupboard. Doors still open/close
    /// for real (Lucas's own explicit ask, 2026-09-01: "we still need
    /// doors to open/close once the bot enters and passes them") using the
    /// exact SetOpen+SendNetworkUpdate pair already proven working on
    /// video this session - only the MOVEMENT itself is simplified to
    /// guaranteed-reliable phasing, not the door visuals.
    /// </summary>
    // Real hard ceiling on the WHOLE ghost-enter sequence (2026-09-01,
    // Lucas's own explicit bar: "as long as the bot doesn't just stay
    // there indefinitely... grab tools, deposit loot, head off on a new
    // task... probably a 10-15 second task"). Doesn't matter if a leg is
    // still visually imperfect (see this function's own Y-correction doc
    // comment) - what matters now is the survivor is GUARANTEED to
    // actually finish and move on with its life within a bounded window,
    // never stuck oscillating for 90+ real seconds the way the live trace
    // caught it doing.
    private const float GhostEnterMaxSeconds = 15f;

    private void GhostEnterHomeForDeposit(Survivor survivor, Action onCompleteRaw)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed || home.DoorRoutes.Count == 0)
        {
            onCompleteRaw?.Invoke();
            return;
        }

        bool completed = false;

        void onComplete()
        {
            if (completed)
            {
                return;
            }

            completed = true;
            onCompleteRaw?.Invoke();
        }

        timer.Once(GhostEnterMaxSeconds, () =>
        {
            if (!completed)
            {
                VerbosePuts($"ghost-enter: '{survivor.Character.Alias}' hit the {GhostEnterMaxSeconds:F0}s hard ceiling - forcing completion regardless of where it currently is.");
                onComplete();
            }
        });

        List<HomeDoorRoute> remainingRoutes = new(home.DoorRoutes);

        Door ResolveDoorNear(Vector3 anchor)
        {
            Collider[] hits = Physics.OverlapSphere(anchor, 1f, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

            foreach (Collider hit in hits)
            {
                Door candidate = hit.GetComponentInParent<Door>();

                if (candidate != null && !candidate.IsDestroyed)
                {
                    return candidate;
                }
            }

            return null;
        }

        void PlayGhostWaypoints(List<Vector3> waypoints, int index, Action onRouteComplete)
        {
            if (index >= waypoints.Count)
            {
                onRouteComplete?.Invoke();
                return;
            }

            StartPhasingToDestination(
                survivor,
                waypoints[index],
                onArrived: () => PlayGhostWaypoints(waypoints, index + 1, onRouteComplete),
                onFailed: () => PlayGhostWaypoints(waypoints, index + 1, onRouteComplete));
        }

        // Real fix (2026-09-01, live report: "first door opens ~10m away",
        // "second door doesn't open at all", "ends up on the roof" - trace
        // evidence showed sustained Y oscillation between two floor
        // heights for 90+ real seconds, not a one-time miscalculation).
        // Don't try to correct the WHOLE route's height from wherever the
        // survivor happens to be standing when this starts (unreliable -
        // they might not be anywhere near the route's own start yet, so
        // the "correction" was really just measuring an unrelated height
        // difference and applying it everywhere, compounding across two
        // routes). Instead: phase to the route's own (uncorrected)
        // near-end FIRST - a short hop, low risk even if slightly off -
        // THEN measure the real correction from where the survivor
        // genuinely landed, THEN open the door (only once actually close,
        // fixing the "opens 10m away" report) and play the rest of the
        // route with that correction applied.
        const float DepositApproachDistance = 1.5f;

        void PlayNextRoute()
        {
            if (remainingRoutes.Count == 0)
            {
                // Real fix for "ends up on the roof" (2026-09-01, live
                // report + Lucas's own diagnosis: "is the bot just trying
                // to get to the centre of the toolcupboard? ... offset by
                // 1 metre, it doesn't need to be hugging it"). Phasing
                // exactly to home.Position (the cupboard's own real
                // transform center, a solid object) overlaps its collider;
                // approaching from the survivor's own current direction
                // instead keeps a real gap.
                Vector3 approachDirection = (npc.transform.position - home.Position);
                approachDirection.y = 0f;

                if (approachDirection.sqrMagnitude < 0.01f)
                {
                    approachDirection = npc.transform.forward;
                }

                Vector3 approachPoint = home.Position + approachDirection.normalized * DepositApproachDistance;
                approachPoint.y = home.Position.y;

                StartPhasingToDestination(survivor, approachPoint, onArrived: onComplete, onFailed: onComplete);
                return;
            }

            HomeDoorRoute bestRoute = null;
            bool reversed = false;
            float bestDistSqr = float.MaxValue;
            Vector3 current = npc.transform.position;

            foreach (HomeDoorRoute route in remainingRoutes)
            {
                if (route.Waypoints.Count == 0)
                {
                    continue;
                }

                float distToStart = (route.Waypoints[0] - current).sqrMagnitude;
                float distToEnd = (route.Waypoints[^1] - current).sqrMagnitude;
                float minDist = Mathf.Min(distToStart, distToEnd);

                if (minDist < bestDistSqr)
                {
                    bestDistSqr = minDist;
                    bestRoute = route;
                    reversed = distToEnd < distToStart;
                }
            }

            remainingRoutes.Remove(bestRoute);

            List<Vector3> orderedWaypoints = reversed ? Enumerable.Reverse(bestRoute.Waypoints).ToList() : bestRoute.Waypoints;

            // Real fix (2026-09-01, live report + log-confirmed: "applying
            // 2.99m Y-correction" printed AFTER the survivor had already
            // jumped to Y=29.17/the roof approaching this route's own
            // near-end). The approach hop above used to phase straight to
            // orderedWaypoints[0] AT ITS OWN recorded (possibly wrong,
            // same per-instance mismatch as always) height - so the
            // correction was being measured AFTER already landing
            // somewhere bad, locking in the wrong height as the new
            // "correct" baseline instead of fixing it. Approaching
            // HORIZONTALLY ONLY first - lining up the X/Z while holding
            // the survivor's own CURRENT (known-good) height - means the
            // correction below is always measured from a position that
            // was never touched, not one already corrupted by this hop.
            Vector3 horizontalNearEnd = orderedWaypoints[0];
            horizontalNearEnd.y = current.y;

            StartPhasingToDestination(survivor, horizontalNearEnd, onArrived: () =>
            {
                // Real per-instance height correction, now measured from
                // the survivor's own real height (untouched by the hop
                // above) against the route's recorded near-end height. See
                // this function's own doc comment for why a base (rebuilt
                // per-instance against real terrain) can't reuse
                // monument-style rigid reprojection here.
                float yCorrection = npc.transform.position.y - orderedWaypoints[0].y;
                List<Vector3> waypoints = orderedWaypoints.Select(w => w + new Vector3(0f, yCorrection, 0f)).ToList();

                VerbosePuts($"ghost-enter: '{survivor.Character.Alias}' applying {yCorrection:F2}m Y-correction to this route's {waypoints.Count} waypoint(s).");

                Door routeDoor = ResolveDoorNear(bestRoute.DoorAnchorPosition);

                if (routeDoor != null && !routeDoor.IsOpen())
                {
                    routeDoor.SetOpen(true);
                    routeDoor.SendNetworkUpdate();
                }

                PlayGhostWaypoints(waypoints, 0, () =>
                {
                    Door doorToClose = ResolveDoorNear(bestRoute.DoorAnchorPosition);

                    if (doorToClose != null && doorToClose.IsOpen())
                    {
                        doorToClose.SetOpen(false);
                        doorToClose.SendNetworkUpdate();
                    }

                    // Real safety-net height correction (2026-09-01, live
                    // report + log-confirmed: position jumped 3m UP while
                    // still mid-flight toward a lower destination, right
                    // around when this route's door closed - the exact
                    // signature this project already has a name for,
                    // DoorCloseHeightCorrectionTolerance: Unity physics
                    // shoving a survivor still overlapping a door's
                    // collider onto whatever's above, most often the floor
                    // slab of the story overhead. Applied everywhere else
                    // doors close in this project already - this new
                    // function just never had it. waypoints[^1].y is the
                    // one height already known correct here (the survivor
                    // was just phased to it).
                    BasePlayer closingNpc = survivor.Player;

                    if (closingNpc != null && !closingNpc.IsDestroyed && waypoints.Count > 0 && Mathf.Abs(closingNpc.transform.position.y - waypoints[^1].y) > DoorCloseHeightCorrectionTolerance)
                    {
                        Vector3 corrected = closingNpc.transform.position;
                        corrected.y = waypoints[^1].y;
                        closingNpc.transform.position = corrected;
                        closingNpc.MovePosition(corrected);
                        survivor.Position = corrected;
                        survivor.Character.Position = corrected;

                        VerbosePuts($"ghost-enter: '{survivor.Character.Alias}' corrected a {Mathf.Abs(closingNpc.transform.position.y - waypoints[^1].y):F2}m post-door-close height push back to {waypoints[^1].y:F2}.");
                    }

                    PlayNextRoute();
                });
            },
            onFailed: PlayNextRoute);
        }

        PlayNextRoute();
    }

    // How far from home.Position to look for a survivor's OWN placed
    // storage boxes/furnaces (2026-09-01) - same order of magnitude as
    // CheckUpgradesSearchRadius (LivingRust.Debug.cs), generous enough to
    // cover a whole base interior without picking up a neighbouring base's
    // own storage.
    private const float HomeStorageSearchRadius = 40f;

    // Same 1m stand-off proven live on both the box and furnace walk-
    // deposit test rigs (2026-09-01, Lucas's own ask) - avoids phasing
    // into the container's own collider.
    private const float HomeStorageStandOffDistance = 1f;

    // Real trigger threshold (2026-09-01, Lucas's own explicit numbers):
    // "2000+ wood in storage (not including tool cupboard)" before a
    // furnace trip is worth making, pulling exactly 1000 out per trip.
    private const int HomeFurnaceWoodTrigger = 300;
    private const int HomeFurnaceWoodFillAmount = 500;

    private static readonly string[] SmeltableOreShortnames = { "metal.ore", "sulfur.ore" };

    /// <summary>
    /// Real "what survives the trip home" filter (2026-09-01, Lucas's own
    /// explicit spec): "any components, metal fragments, scrap metal,
    /// cloth etc will go back into the bases chests... not ammo, not med
    /// syringes etc." Reuses GetLootPriorityTier - the same real
    /// categorization already used to decide what a full inventory drops -
    /// rather than a second hand-rolled category check that could quietly
    /// drift out of sync with it. Also keeps a gather tool that's the
    /// survivor's ONLY one of its kind (2026-09-01, Lucas's own follow-up:
    /// "keep those primitive tools on them and not deposit them IF they
    /// have no other tool on them... that way it isn't stuck with a rock")
    /// - deliberately checks the WHOLE pickaxe/hatchet family, not just the
    /// stone tier specifically, so this stays correct if a survivor's only
    /// pickaxe/hatchet ever happens to be a better one instead.
    ///
    /// Weapons/armor were originally excluded entirely too ("not weapons...
    /// not armour IF it intends to go outside again which is 99% of the
    /// time") - revised same session, Lucas's own explicit follow-up: "any
    /// ADDITIONAL looted weapons (ranged or melee) or additional armour/
    /// clothing gets deposited into their chests." "Additional" means a
    /// genuine spare - DepositFilteredItems only ever iterates main+belt
    /// (never containerWear, LivingRust.Debug.cs's own doc comment), so any
    /// Armor-tier item reaching this check is ALREADY unworn by
    /// construction; a Weapon-tier item is kept only if it's the real
    /// currently-active held item.
    /// </summary>
    private static bool ShouldDepositAtBase(Item item, BasePlayer npc)
    {
        LootPriorityTier tier = GetLootPriorityTier(item);

        if (tier == LootPriorityTier.AmmoOrExplosive || tier == LootPriorityTier.Medical)
        {
            return false;
        }

        if (tier == LootPriorityTier.Weapon)
        {
            // A survivor's only bow stays with it (2026-09-21) - otherwise
            // every deposit trip stripped it (a bow isn't the "active" item
            // while a hatchet is in hand) and the survival-kit upkeep
            // crafted a fresh one right after, forever.
            if (Array.IndexOf(NonCombatCapableRangedWeaponShortnames, item.info.shortname) >= 0
                && npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList)
                    .Count(i => Array.IndexOf(NonCombatCapableRangedWeaponShortnames, i.info.shortname) >= 0) <= 1)
            {
                return false;
            }

            Item activeItem = npc.GetActiveItem();
            return activeItem == null || activeItem.uid != item.uid;
        }

        if (tier == LootPriorityTier.Armor)
        {
            return true;
        }

        if (IsOnlyGatherToolOfItsFamily(item, npc))
        {
            return false;
        }

        return true;
    }

    private static bool IsOnlyGatherToolOfItsFamily(Item item, BasePlayer npc)
    {
        string shortname = item.info.shortname;
        string[] family = Array.IndexOf(PickaxeFamily, shortname) >= 0 ? PickaxeFamily
            : Array.IndexOf(HatchetFamily, shortname) >= 0 ? HatchetFamily
            : null;

        if (family == null)
        {
            return false;
        }

        int count = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Count(i => Array.IndexOf(family, i.info.shortname) >= 0);

        return count <= 1;
    }

    /// <summary>
    /// Real "does the survivor own ANY tool from this family at all"
    /// check (2026-09-01) - used both by ShouldSkipPrimitiveChecklist
    /// (LivingRust.Crafting.cs, skip the from-scratch checklist if already
    /// tooled up) and the standing "never let a survivor go fully toolless"
    /// safety net (ContinueLootTask, LivingRust.Looting.cs).
    /// </summary>
    private bool HasAnyToolOfFamily(BasePlayer npc, string[] family)
    {
        return npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Any(i => Array.IndexOf(family, i.info.shortname) >= 0);
    }

    /// <summary>
    /// Real owned-storage lookup (2026-09-01) - "owned" via OwnerID, the
    /// same real field a placed deployable is stamped with at build time
    /// (see IsTooCloseToAnotherCupboard's own doc comment, LivingRust.
    /// BaseBuilding.cs, for the same OwnerID pattern applied to cupboards).
    /// Deliberately box.wooden.large only for now - Lucas's own explicit
    /// framing was "boxes it has made inside of its base" - not every
    /// StorageContainer within range (that would also catch a neighbour's
    /// storage sitting just inside this radius). Sorted nearest-to-home
    /// first so the deposit loop visits the most central boxes first.
    /// </summary>
    private List<StorageContainer> FindOwnedStorageBoxesNear(BasePlayer npc, Vector3 origin, float radius)
    {
        List<StorageContainer> results = new();

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is not StorageContainer container || container.IsDestroyed || container.ShortPrefabName != "box.wooden.large")
            {
                continue;
            }

            if (container.OwnerID != npc.userID || Vector3.Distance(origin, container.transform.position) > radius)
            {
                continue;
            }

            results.Add(container);
        }

        results.Sort((a, b) => Vector3.Distance(origin, a.transform.position).CompareTo(Vector3.Distance(origin, b.transform.position)));
        return results;
    }

    /// <summary>Same idea as FindOwnedStorageBoxesNear, for the survivor's own furnace(s).</summary>
    private List<BaseOven> FindOwnedFurnacesNear(BasePlayer npc, Vector3 origin, float radius)
    {
        List<BaseOven> results = new();

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is not BaseOven oven || oven.IsDestroyed || oven.ShortPrefabName != "furnace")
            {
                continue;
            }

            if (oven.OwnerID != npc.userID || Vector3.Distance(origin, oven.transform.position) > radius)
            {
                continue;
            }

            results.Add(oven);
        }

        results.Sort((a, b) => Vector3.Distance(origin, a.transform.position).CompareTo(Vector3.Distance(origin, b.transform.position)));
        return results;
    }

    /// <summary>
    /// Generalizes DepositAllItems (LivingRust.Debug.cs, built for the box
    /// deposit test) with a predicate - DepositAllItems now just calls this
    /// with an always-true filter, so the two test rigs and this real
    /// production path share one real transfer loop instead of two that
    /// could drift apart.
    /// </summary>
    private int DepositFilteredItems(BasePlayer npc, ItemContainer target, Func<Item, bool> shouldDeposit)
    {
        List<Item> items = new();

        if (npc.inventory.containerMain != null)
        {
            items.AddRange(npc.inventory.containerMain.itemList);
        }

        if (npc.inventory.containerBelt != null)
        {
            items.AddRange(npc.inventory.containerBelt.itemList);
        }

        // Backpack contents are part of what a survivor carries (2026-09-21) -
        // otherwise spares and bulk resources stored there would never be
        // banked at base.
        Item depositBackpack = GetWornBackpack(npc);

        if (depositBackpack?.contents != null)
        {
            items.AddRange(depositBackpack.contents.itemList);
        }

        int moved = 0;

        foreach (Item item in items)
        {
            if (shouldDeposit(item) && item.MoveToContainer(target))
            {
                moved++;
            }
        }

        return moved;
    }

    /// <summary>
    /// Real production deposit trip (2026-09-01, Lucas's own explicit
    /// ask) - phases (zero-collision, same as every other in-base movement
    /// this project uses) to each of the survivor's own boxes in turn,
    /// approaching with the same 1m stand-off proven on the walk-deposit
    /// test rig, and stops early the moment nothing left in inventory
    /// still passes shouldDeposit (no point visiting a second box empty-
    /// handed). Assumes the survivor is ALREADY inside (called after
    /// GhostEnterHomeForDeposit) - doesn't itself cross any door.
    /// </summary>
    private void GhostDepositIntoOwnedBoxes(Survivor survivor, Func<Item, bool> shouldDeposit, Action<int> onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            onComplete?.Invoke(0);
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);

        if (boxes.Count == 0)
        {
            onComplete?.Invoke(0);
            return;
        }

        int totalMoved = 0;

        void DepositIntoNext(int index)
        {
            BasePlayer liveNpc = survivor.Player;

            bool anyLeft = liveNpc != null && !liveNpc.IsDestroyed
                && liveNpc.inventory.containerMain.itemList.Concat(liveNpc.inventory.containerBelt.itemList).Any(shouldDeposit);

            if (index >= boxes.Count || !anyLeft)
            {
                onComplete?.Invoke(totalMoved);
                return;
            }

            StorageContainer box = boxes[index];

            if (box == null || box.IsDestroyed || box.inventory == null)
            {
                DepositIntoNext(index + 1);
                return;
            }

            Vector3 approachDirection = liveNpc.transform.position - box.transform.position;
            approachDirection.y = 0f;

            if (approachDirection.sqrMagnitude < 0.01f)
            {
                approachDirection = liveNpc.transform.forward;
            }

            Vector3 approachPoint = box.transform.position + approachDirection.normalized * HomeStorageStandOffDistance;
            approachPoint.y = box.transform.position.y;

            StartPhasingToDestination(survivor, approachPoint, onArrived: () =>
            {
                BasePlayer arrivedNpc = survivor.Player;

                if (arrivedNpc != null && !arrivedNpc.IsDestroyed && !box.IsDestroyed && box.inventory != null)
                {
                    totalMoved += DepositFilteredItems(arrivedNpc, box.inventory, shouldDeposit);
                }

                DepositIntoNext(index + 1);
            },
            onFailed: () => DepositIntoNext(index + 1));
        }

        DepositIntoNext(0);
    }

    /// <summary>
    /// Withdraws up to amount total of shortname from boxes directly into
    /// destination - a genuine container-to-container transfer (Item.
    /// MoveToContainer doesn't require passing through a player inventory
    /// in between), splitting a stack via Item.SplitItem when a single
    /// box's stack holds more than what's still needed. On a failed move
    /// (destination full/rejects it), the split remainder is merged back
    /// into its source box rather than left orphaned.
    /// </summary>
    private int WithdrawUpToAmount(List<StorageContainer> boxes, string shortname, int amount, ItemContainer destination)
    {
        int remaining = amount;

        foreach (StorageContainer box in boxes)
        {
            if (remaining <= 0)
            {
                break;
            }

            if (box == null || box.IsDestroyed || box.inventory == null)
            {
                continue;
            }

            foreach (Item item in new List<Item>(box.inventory.itemList))
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (item.info.shortname != shortname)
                {
                    continue;
                }

                if (item.amount <= remaining)
                {
                    int amt = item.amount;

                    if (item.MoveToContainer(destination))
                    {
                        remaining -= amt;
                    }
                }
                else
                {
                    Item split = item.SplitItem(remaining);

                    if (split == null)
                    {
                        continue;
                    }

                    if (split.MoveToContainer(destination))
                    {
                        remaining -= split.amount;
                    }
                    else if (!split.MoveToContainer(box.inventory))
                    {
                        split.Drop(box.transform.position, Vector3.zero);
                    }
                }
            }
        }

        return amount - remaining;
    }

    /// <summary>
    /// Moves up to maxStacks whole stacks of shortname out of boxes into
    /// destination (2026-09-01, live-confirmed correction: a real furnace
    /// only has 2 real ore input slots, not unlimited - "can only put 2
    /// stacks... 1000 of two different ores OR 2 stacks of the same
    /// ore... if it has more than this it should just re-deposit excess
    /// ores into a crate it owns"). Deliberately doesn't split stacks the
    /// way WithdrawUpToAmount does for wood - ore stacks are already
    /// capped at 1000 by the server's own stack size, so a whole-stack
    /// move is always exactly one furnace slot. Any stack left over simply
    /// stays in its box untouched (MoveToContainer is never even attempted
    /// once maxStacks is hit) - already exactly "re-deposited" since it
    /// was already sitting there from GhostDepositIntoOwnedBoxes, no
    /// separate action needed.
    /// </summary>
    private (int Amount, int Stacks) WithdrawUpToStackCount(List<StorageContainer> boxes, string shortname, int maxStacks, ItemContainer destination)
    {
        int amount = 0;
        int stacks = 0;

        foreach (StorageContainer box in boxes)
        {
            if (stacks >= maxStacks)
            {
                break;
            }

            if (box == null || box.IsDestroyed || box.inventory == null)
            {
                continue;
            }

            foreach (Item item in new List<Item>(box.inventory.itemList))
            {
                if (stacks >= maxStacks)
                {
                    break;
                }

                if (item.info.shortname != shortname)
                {
                    continue;
                }

                int amt = item.amount;

                if (item.MoveToContainer(destination))
                {
                    amount += amt;
                    stacks++;
                }
            }
        }

        return (amount, stacks);
    }

    /// <summary>
    /// Real furnace-fill trip (2026-09-01, Lucas's own explicit spec):
    /// only worth making once base storage genuinely has 2000+ wood AND
    /// some ore sitting in it - a single trip pulls exactly 1000 wood plus
    /// every ore stack found straight out of the survivor's own boxes
    /// (container-to-container, matching the walk-smelt test rig's own
    /// proven deposit-then-StartCooking order) and ignites. No-ops
    /// (onComplete straight away) if the trigger isn't met or there's no
    /// owned furnace to fill. Assumes the survivor is already inside (same
    /// assumption as GhostDepositIntoOwnedBoxes).
    /// </summary>
    private void TryFillOwnedFurnaces(Survivor survivor, Action onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            onComplete?.Invoke();
            return;
        }

        List<BaseOven> furnaces = FindOwnedFurnacesNear(npc, home.Position, HomeStorageSearchRadius);

        if (furnaces.Count == 0)
        {
            onComplete?.Invoke();
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);
        FillFurnaceAtIndex(survivor, furnaces, boxes, 0, onComplete);
    }

    // Works through every owned furnace in turn (2026-09-21, Lucas's own
    // explicit spec: keep ore + fuel loaded for a steady sulfur/metal
    // fragment supply) - this used to only ever service furnaces[0], so a
    // base's second furnace never smelted anything.
    private void FillFurnaceAtIndex(Survivor survivor, List<BaseOven> furnaces, List<StorageContainer> boxes, int index, Action afterAll)
    {
        BasePlayer npc = survivor.Player;

        if (index >= furnaces.Count || npc == null || npc.IsDestroyed)
        {
            afterAll?.Invoke();
            return;
        }

        BaseOven furnace = furnaces[index];
        Action onComplete = () => FillFurnaceAtIndex(survivor, furnaces, boxes, index + 1, afterAll);

        if (furnace == null || furnace.IsDestroyed)
        {
            onComplete();
            return;
        }

        Vector3 approachDirection = npc.transform.position - furnace.transform.position;
        approachDirection.y = 0f;

        if (approachDirection.sqrMagnitude < 0.01f)
        {
            approachDirection = npc.transform.forward;
        }

        Vector3 approachPoint = furnace.transform.position + approachDirection.normalized * HomeStorageStandOffDistance;
        approachPoint.y = furnace.transform.position.y;

        // Real B step (2026-09-19, Lucas's own explicit spec: "check
        // furnaces and remove loot from smelted ores (metal fragments,
        // charcoal etc) and top up with wood if owned"). The walk-to-
        // furnace step below used to only fire if THIS trip's own
        // wood/ore trigger was met, which meant real finished output from
        // an EARLIER trip's batch could sit uncollected in the furnace
        // indefinitely if storage never again happened to have 2000+ wood
        // AND ore at the same moment. Now visits the furnace unconditionally
        // whenever one's owned, collects anything real ALREADY smelted
        // first (regardless of this trip's own trigger), then still only
        // starts a brand new batch if the trigger's actually met.
        StartPhasingToDestination(survivor, approachPoint, onArrived: () =>
        {
            if (furnace == null || furnace.IsDestroyed || furnace.inventory == null)
            {
                onComplete?.Invoke();
                return;
            }

            int outputCollected = 0;

            foreach (Item furnaceItem in new List<Item>(furnace.inventory.itemList))
            {
                if (furnaceItem.info.shortname == "wood" || Array.IndexOf(SmeltableOreShortnames, furnaceItem.info.shortname) >= 0)
                {
                    // Still-raw fuel/ore, not finished output - leave it to
                    // keep cooking.
                    continue;
                }

                StorageContainer target = boxes.FirstOrDefault(b => b != null && !b.IsDestroyed && b.inventory != null && !IsContainerFull(b.inventory));

                if (target != null && furnaceItem.MoveToContainer(target.inventory))
                {
                    outputCollected++;
                }
            }

            if (outputCollected > 0)
            {
                VerbosePuts($"home-storage: '{survivor.Character.Alias}' collected {outputCollected} smelted item stack(s) from its furnace.");
            }

            int totalWood = boxes.Where(b => b != null && !b.IsDestroyed && b.inventory != null)
                .SelectMany(b => b.inventory.itemList)
                .Where(i => i.info.shortname == "wood")
                .Sum(i => i.amount);

            bool hasOre = boxes.Where(b => b != null && !b.IsDestroyed && b.inventory != null)
                .SelectMany(b => b.inventory.itemList)
                .Any(i => SmeltableOreShortnames.Contains(i.info.shortname));

            if (totalWood < HomeFurnaceWoodTrigger || !hasOre)
            {
                onComplete?.Invoke();
                return;
            }

            int woodMoved = WithdrawUpToAmount(boxes, "wood", HomeFurnaceWoodFillAmount, furnace.inventory);
            int oreMoved = 0;
            int oreStacksMoved = 0;

            // Real live inputSlots on the actually-spawned furnace (2026-09-01,
            // live-confirmed: "can only put 2 stacks... if it has more than
            // this it should just re-deposit excess ores into a crate it
            // owns") - reads the furnace's own real component value rather
            // than hardcoding "2", so this stays correct even if a
            // different furnace tier/config is ever used. Any ore beyond
            // this cap is left untouched in its box - already
            // "re-deposited" there from the earlier GhostDepositIntoOwnedBoxes
            // step, no separate action needed.
            foreach (string oreShortname in SmeltableOreShortnames)
            {
                if (oreStacksMoved >= furnace.inputSlots)
                {
                    break;
                }

                (int amount, int stacks) = WithdrawUpToStackCount(boxes, oreShortname, furnace.inputSlots - oreStacksMoved, furnace.inventory);
                oreMoved += amount;
                oreStacksMoved += stacks;
            }

            if (woodMoved > 0)
            {
                furnace.StartCooking();
            }

            VerbosePuts($"home-storage: '{survivor.Character.Alias}' filled its furnace with {woodMoved}x wood and {oreMoved} ore ({oreStacksMoved}/{furnace.inputSlots} ore slot(s)), IsOn={furnace.IsOn()}.");
            onComplete?.Invoke();
        },
        onFailed: onComplete);
    }

    // How much cloth a survivor pulls from its own storage to top up
    // bandages before heading back out (2026-09-19) - enough for
    // BandageMaxBatches' own real 4x-cloth-per-bandage recipe with room to
    // spare, not a bulk strip-the-base amount.
    private const int BandageStockClothWithdrawAmount = 40;

    /// <summary>
    /// Real D step (2026-09-19, Lucas's own explicit spec: "make sure they
    /// have additional bandages to go out and roam in case they get
    /// attacked... if they run out, find more hemp in the wildlife"). Pulls
    /// cloth from storage first (a real player grabs spare cloth from their
    /// own base before heading out), then reuses TryPursueBandageGoal - the
    /// exact same real craft-from-owned-cloth logic the primitive checklist
    /// already relies on - rather than re-deriving bandage-crafting from
    /// scratch. allowGather deliberately false here: sending the survivor
    /// off to find real hemp mid-base-visit would skip the door-exit step
    /// still to come (ExitHomeIfInside) further up this chain. A genuinely
    /// empty cloth supply is already handled the normal way once the
    /// survivor's back out roaming (TryPursueBandageSupplyIfHurt/
    /// TryUseMedicalItemIfHurt, both unconditional every cycle) - this is
    /// purely an opportunistic top-up from whatever's already sitting at
    /// home for free.
    /// </summary>
    private void TryStockBandagesBeforeLeaving(Survivor survivor, Action onComplete)
    {
        BasePlayer npc = survivor.Player;
        HomeBase home = survivor.Character.Home;

        if (npc == null || npc.IsDestroyed || home == null)
        {
            onComplete?.Invoke();
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);
        int clothWithdrawn = WithdrawUpToAmount(boxes, ClothShortname, BandageStockClothWithdrawAmount, npc.inventory.containerMain);

        if (clothWithdrawn > 0)
        {
            TryPursueBandageGoal(survivor, npc, new LootTaskState(), allowGather: false);
        }

        onComplete?.Invoke();
    }

    /// <summary>
    /// Real C step (2026-09-19, Lucas's own explicit spec: "upgrade
    /// gearscore (if possible) with items/clothing/tools/weapons in
    /// storage containers"). Armor reuses the exact same score/conflict
    /// comparison EvaluateAndUpgradeArmor already applies to a freshly-
    /// looted candidate (GetArmorProtectionScore + real CanExistWith slot
    /// check + ArmorUpgradeMinimumScoreGain), just sourced from owned
    /// storage instead of "just picked up" - only genuine upgrades are
    /// ever moved, so a spare/equal set sitting in storage is correctly
    /// left alone rather than churned every visit. A displaced WORN piece
    /// goes back into storage (not dropped the way EvaluateAndUpgradeArmor
    /// does for a fresh field pickup) - it's already home, keeping the
    /// spare costs nothing. Weapon uses the same WeaponPriority ranking
    /// GetWeaponGearScore/TryEquipBestArmedWeapon already use, just
    /// comparing storage's own best against whatever's already owned.
    /// </summary>
    private void TryUpgradeGearFromStorage(Survivor survivor, Action onComplete)
    {
        BasePlayer npc = survivor.Player;
        HomeBase home = survivor.Character.Home;

        if (npc == null || npc.IsDestroyed || home == null)
        {
            onComplete?.Invoke();
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);
        int armorUpgraded = 0;

        foreach (StorageContainer box in boxes)
        {
            if (box == null || box.IsDestroyed || box.inventory == null)
            {
                continue;
            }

            foreach (Item candidate in new List<Item>(box.inventory.itemList))
            {
                ItemModWearable candidateWearable = candidate.info.GetComponent<ItemModWearable>();

                if (candidateWearable == null)
                {
                    continue;
                }

                List<Item> conflicting = npc.inventory.containerWear.itemList
                    .Where(worn => !candidateWearable.CanExistWith(worn.info.GetComponent<ItemModWearable>()))
                    .ToList();

                float candidateScore = GetArmorProtectionScore(candidate);
                float conflictingScore = conflicting.Sum(GetArmorProtectionScore);

                if (conflicting.Count > 0 && candidateScore < conflictingScore + ArmorUpgradeMinimumScoreGain)
                {
                    continue;
                }

                if (!candidate.MoveToContainer(npc.inventory.containerWear))
                {
                    continue;
                }

                armorUpgraded++;
                VerbosePuts($"home-storage: '{survivor.Character.Alias}' equipped '{candidate.info.shortname}' from its own storage (protection {candidateScore:F2} vs {conflictingScore:F2}).");

                foreach (Item displaced in conflicting)
                {
                    StorageContainer target = boxes.FirstOrDefault(b => b != null && !b.IsDestroyed && b.inventory != null && !IsContainerFull(b.inventory));

                    if (target == null || !displaced.MoveToContainer(target.inventory))
                    {
                        displaced.Drop(npc.transform.position, Vector3.zero);
                    }
                }
            }
        }

        List<Item> allStoredItems = boxes.Where(b => b != null && !b.IsDestroyed && b.inventory != null)
            .SelectMany(b => b.inventory.itemList)
            .ToList();

        Item bestStoredWeapon = FindBestByPriority(allStoredItems, WeaponPriority, exclude: null);
        Item bestOwnedWeapon = FindBestByPriority(
            npc.inventory.containerBelt.itemList.Concat(npc.inventory.containerMain.itemList).ToList(),
            WeaponPriority,
            exclude: null);

        bool weaponUpgraded = false;

        if (bestStoredWeapon != null
            && (bestOwnedWeapon == null || Array.IndexOf(WeaponPriority, bestStoredWeapon.info.shortname) < Array.IndexOf(WeaponPriority, bestOwnedWeapon.info.shortname)))
        {
            if (bestStoredWeapon.MoveToContainer(npc.inventory.containerMain))
            {
                weaponUpgraded = true;
                VerbosePuts($"home-storage: '{survivor.Character.Alias}' grabbed '{bestStoredWeapon.info.shortname}' from its own storage - a real upgrade over what it's carrying.");
            }
        }

        if (armorUpgraded > 0 || weaponUpgraded)
        {
            EquipBestWeaponForDisplay(survivor);
        }

        onComplete?.Invoke();
    }

    /// <summary>
    /// Finds the survivor's own real tool cupboard by the NetID recorded
    /// at build time (HomeBase.CupboardNetId) - more precise than the
    /// OwnerID+proximity scan boxes/furnaces use, and there's only ever
    /// one real cupboard per base anyway.
    /// </summary>
    private BuildingPrivlidge FindOwnedCupboard(HomeBase home)
    {
        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is BuildingPrivlidge cupboard && !cupboard.IsDestroyed && cupboard.net != null && cupboard.net.ID.Value == home.CupboardNetId)
            {
                return cupboard;
            }
        }

        return null;
    }

    /// <summary>
    /// Real tool cupboard deposit (2026-09-01, Lucas's own explicit
    /// correction: "Tool cupboards accept arbitrary tools (building plans,
    /// hammer)... it has a separate area within the tool cupboard to store
    /// them" - confirmed via decompiling BuildingPrivlidge: it's a real
    /// StorageContainer with a general 24-slot area (slots 0-23, accepts
    /// anything - hammer/building.planner land here) PLUS reserved upkeep-
    /// only slots (24-28, gated to whatever allowedConstructionItems the
    /// live prefab is actually configured with - real wood/stone/metal.
    /// fragments/HQM). Reads allowedConstructionItems live off the real
    /// spawned cupboard rather than hardcoding a guessed shortname list, so
    /// this stays correct even if that set is ever reconfigured. Runs
    /// BEFORE the general box deposit in GhostReturnHomeAndDeposit -
    /// whatever lands here physically leaves the survivor's inventory, so
    /// the box step naturally never sees it again, no exclusion logic
    /// needed on that side.
    /// </summary>
    private void GhostDepositIntoOwnedCupboard(Survivor survivor, Action<int> onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            onComplete?.Invoke(0);
            return;
        }

        BuildingPrivlidge cupboard = FindOwnedCupboard(home);

        if (cupboard == null || cupboard.inventory == null)
        {
            onComplete?.Invoke(0);
            return;
        }

        Vector3 approachDirection = npc.transform.position - cupboard.transform.position;
        approachDirection.y = 0f;

        if (approachDirection.sqrMagnitude < 0.01f)
        {
            approachDirection = npc.transform.forward;
        }

        Vector3 approachPoint = cupboard.transform.position + approachDirection.normalized * HomeStorageStandOffDistance;
        approachPoint.y = cupboard.transform.position.y;

        StartPhasingToDestination(survivor, approachPoint, onArrived: () =>
        {
            BasePlayer arrivedNpc = survivor.Player;

            if (arrivedNpc == null || arrivedNpc.IsDestroyed || cupboard.IsDestroyed || cupboard.inventory == null)
            {
                onComplete?.Invoke(0);
                return;
            }

            // Hammer/planner still deposit in FULL (a whole tool, not a
            // percentage) - real upkeep resources (wood/stone/metal.
            // fragments/HQM) now only trickle 10% (DepositUpkeepPortion's
            // own doc comment) rather than depositing the whole stack the
            // way this used to.
            int moved = DepositFilteredItems(arrivedNpc, cupboard.inventory, item =>
                item.info.shortname == HammerShortname
                || item.info.shortname == BuildingPlannerShortname);

            moved += DepositUpkeepPortion(arrivedNpc, cupboard);

            onComplete?.Invoke(moved);
        },
        onFailed: () => onComplete?.Invoke(0));
    }

    // Real upkeep-trickle fraction (2026-09-01, Lucas's own explicit spec:
    // "have the bot deposit 10% of its stones/ores/metal/wood everytime it
    // returns from a task outside of the base. That way it has upkeep and
    // is slowly progressing its base hoarding"). Deliberately only a SLICE,
    // not the whole stack - the other 90% keeps flowing into the normal box
    // deposit right after this (GhostDepositIntoOwnedBoxes), so the hoard
    // TryPursueTierUpgrade checks against keeps growing at the same time
    // the cupboard's own real decay-prevention timer gets fed.
    private const float HomeUpkeepDepositFraction = 0.1f;

    private int DepositUpkeepPortion(BasePlayer npc, BuildingPrivlidge cupboard)
    {
        List<Item> items = npc.inventory.containerMain.itemList
            .Concat(npc.inventory.containerBelt.itemList)
            .Where(item => cupboard.allowedConstructionItems.Contains(item.info))
            .ToList();

        int moved = 0;

        foreach (Item item in items)
        {
            int portion = Mathf.Clamp(Mathf.RoundToInt(item.amount * HomeUpkeepDepositFraction), 1, item.amount);

            if (portion >= item.amount)
            {
                if (item.MoveToContainer(cupboard.inventory))
                {
                    moved++;
                }

                continue;
            }

            ItemContainer source = item.parent;
            Item split = item.SplitItem(portion);

            if (split == null)
            {
                continue;
            }

            if (split.MoveToContainer(cupboard.inventory))
            {
                moved++;
            }
            else if (source == null || !split.MoveToContainer(source))
            {
                split.Drop(npc.transform.position, Vector3.zero);
            }
        }

        return moved;
    }

    /// <summary>
    /// Top-level real "go home and deposit" trip (2026-09-01, Lucas's own
    /// explicit roadmap ask, points A and B) - ghost-enters the base (same
    /// door-route chaining as every other GhostEnterHomeForDeposit caller),
    /// deposits hammer/building plan/real upkeep materials into the tool
    /// cupboard first (GhostDepositIntoOwnedCupboard), then everything else
    /// except the active weapon/worn armor/ammo/medical into the survivor's
    /// own boxes, then - in the SAME trip, rather than a separate
    /// standalone check - tops up an owned furnace if the
    /// 2000+-wood/has-ore trigger is met. Both A ("recycle then deposit")
    /// and B ("excess wood fills the furnace") share this one trip: A
    /// triggers it (see FinishRecycling, LivingRust.Recycling.cs), and B's
    /// own condition is just checked as a natural part of every trip this
    /// causes, rather than needing its own separate periodic trigger.
    /// </summary>
    private void GhostReturnHomeAndDeposit(Survivor survivor, Action onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;
        MarkBaseVisit(survivor.Character.Id);

        if (home == null || npc == null || npc.IsDestroyed)
        {
            onComplete?.Invoke();
            return;
        }

        GhostEnterHomeForDeposit(survivor, () =>
        {
            GhostDepositIntoOwnedCupboard(survivor, cupboardMoved =>
            {
                VerbosePuts($"home-storage: '{survivor.Character.Alias}' deposited {cupboardMoved} item stack(s) into its own tool cupboard.");

                // Re-reads survivor.Player fresh via the closure (not the
                // captured npc above) - matches this project's general
                // pattern of never trusting a BasePlayer reference across a
                // multi-tick operation, and lets IsOnlyGatherToolOfItsFamily
                // see the survivor's real current inventory at each deposit
                // decision.
                GhostDepositIntoOwnedBoxes(survivor, item => ShouldDepositAtBase(item, survivor.Player), deposited =>
                {
                    VerbosePuts($"home-storage: '{survivor.Character.Alias}' deposited {deposited} item stack(s) into its own base storage.");

                    TryFillOwnedFurnaces(survivor, () =>
                    {
                        // C then D (2026-09-19, Lucas's own explicit
                        // A-through-F spec for this whole trip) - gear
                        // upgrade from storage, then a bandage top-up,
                        // both right here between the furnace step (B)
                        // and the existing sleeping-bag/tier-upgrade/exit
                        // tail.
                        TryUpgradeGearFromStorage(survivor, () =>
                        {
                            TryStockBandagesBeforeLeaving(survivor, () =>
                            {
                                TryPlaceSleepingBagAtBase(survivor, () =>
                                {
                                    // Real fix (2026-09-19, Lucas's own live
                                    // report: "ensure that they close their doors
                                    // when they leave otherwise their base is wide
                                    // open"). TryPursueTierUpgrade's own common
                            // case (no upgrade available/affordable - the
                            // vast majority of deposit trips) just called
                            // onComplete directly with no exit step at all
                            // - GhostEnterHomeForDeposit opens whatever
                            // doors it needs to get IN, but nothing on this
                            // path ever closed them again before the
                            // survivor left for its next task. The ONE
                            // place doors reliably got closed was
                            // AdvanceBuildReplay's own one-time build-
                            // completion settle-in (ExitHomeIfInside) -
                            // every ROUTINE deposit trip afterward (which
                            // happens far more often than that one-time
                            // event) had no matching exit. Wrapping
                            // onComplete here instead of passing it through
                            // directly means ExitHomeIfInside always runs
                            // first regardless of why TryPursueTierUpgrade
                            // finished - if it actually started a real
                            // upgrade build instead, it deliberately never
                            // calls this callback at all (that whole
                            // separate replay already closes the door via
                            // its own AdvanceBuildReplay completion), so
                            // this wrapper is a no-op for that case.
                            TryPursueTierUpgrade(survivor, () => ExitHomeIfInside(survivor, home, onComplete));
                                });
                            });
                        });
                    });
                });
            });
        });
    }

    /// <summary>
    /// Real "respawn point at base" placement (2026-09-01, Lucas's own
    /// explicit spec: "have the bots place an additional sleeping bag
    /// inside or near their base... that way if they die they can go
    /// 'cool, let me check my storage crates for items, re-equip, go
    /// continue'"). Deliberately a SEPARATE bag from the primitive
    /// checklist's own one (placed wherever the survivor happened to be
    /// early in life, likely nowhere near the eventual base) - a real
    /// player owns both simultaneously the same way (Rust's own respawn
    /// UI lets you pick among every bag you own), so this doesn't remove
    /// or replace that one. Gated on real proximity to an owned SleepingBag
    /// entity (HasSleepingBagNearHome) rather than a HashSet flag - a
    /// HashSet would need its own persistence/reload story the way
    /// _hasPlacedSleepingBag already quietly doesn't have (see
    /// ShouldSkipPrimitiveChecklist's own doc comment on that exact class
    /// of bug); checking the real world state instead means this is
    /// naturally correct across reloads with no extra bookkeeping. Reuses
    /// the exact same real deploy mechanism DeploySleepingBagAndAssign
    /// already uses (LivingRust.Crafting.cs), just triggered here instead
    /// (this trip already has the survivor standing right at its base,
    /// which is exactly where this bag needs to land) and without
    /// touching _hasPlacedSleepingBag (a genuinely separate one-off flag,
    /// conflating the two would incorrectly satisfy HasCompletedPrimitiveGoals'
    /// own bag check for a survivor that skipped the checklist entirely).
    /// Opportunistic - if there's not enough cloth on hand right now, just
    /// tries again on the next trip home rather than blocking this one.
    /// </summary>
    private bool HasSleepingBagNearHome(BasePlayer npc, HomeBase home)
    {
        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is SleepingBag bag && !bag.IsDestroyed && bag.OwnerID == npc.userID
                && Vector3.Distance(bag.transform.position, home.Position) <= HomeStorageSearchRadius)
            {
                return true;
            }
        }

        return false;
    }

    private void TryPlaceSleepingBagAtBase(Survivor survivor, Action onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed || HasSleepingBagNearHome(npc, home))
        {
            onComplete?.Invoke();
            return;
        }

        ItemDefinition bagDef = ItemManager.FindItemDefinition(SleepingBagShortname);
        ItemBlueprint bp = bagDef?.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bp == null || crafter == null || !crafter.CanCraft(bp, SleepingBagTargetBatches, free: false))
        {
            onComplete?.Invoke();
            return;
        }

        crafter.CraftItem(bp, npc, amount: SleepingBagTargetBatches);
        Puts($"home-storage: '{survivor.Character.Alias}' is crafting a second sleeping bag to place at its base.");

        int ticks = 0;
        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer liveNpc = survivor.Player;

            if (liveNpc == null || liveNpc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                onComplete?.Invoke();
                return;
            }

            Item bagItem = liveNpc.inventory.FindItemByItemID(bagDef.itemid);

            if (bagItem != null)
            {
                pollTimer.Destroy();
                DeployBaseSleepingBag(survivor, liveNpc, bagDef, bagItem);
                onComplete?.Invoke();
                return;
            }

            if (++ticks >= SleepingBagDeployWaitMaxTicks)
            {
                pollTimer.Destroy();
                onComplete?.Invoke();
            }
        });
    }

    private void DeployBaseSleepingBag(Survivor survivor, BasePlayer npc, ItemDefinition bagDef, Item bagItem)
    {
        ItemModDeployable modDeployable = bagDef.GetComponent<ItemModDeployable>();

        if (modDeployable == null || !_engine.NavigationManager.TryGetGroundHeight(npc.transform.position, out float groundHeight))
        {
            return;
        }

        Vector3 position = npc.transform.position;
        position.y = groundHeight;
        Quaternion rotation = Quaternion.LookRotation(Vector3.up, npc.eyes.BodyForward()) * Quaternion.Euler(90f, 0f, 0f);

        BaseEntity bagEntity = GameManager.server.CreateEntity(modDeployable.entityPrefab.resourcePath, position, rotation);

        if (bagEntity == null)
        {
            Puts($"home-storage: '{survivor.Character.Alias}' failed to create a base sleeping bag entity ('{modDeployable.entityPrefab.resourcePath}').");
            return;
        }

        bagEntity.skinID = bagItem.skin;
        bagEntity.SendMessage("SetDeployedBy", npc, SendMessageOptions.DontRequireReceiver);
        bagEntity.OwnerID = npc.userID;
        bagEntity.Spawn();
        bagItem.UseItem(1);

        Puts($"home-storage: '{survivor.Character.Alias}' placed a sleeping bag at its base.");
    }

    /// <summary>
    /// Real continuous tier-progression check (2026-09-01, Lucas's own
    /// explicit spec: "constantly farming or attempting to progress to the
    /// next tier of base level... go back to base, check its boxes and go
    /// 'do I have stuff to make the next base tier?' no? go back to
    /// another monument or farm roads/paths... or farm more stone/ore").
    /// Piggybacks on the SAME real return-home trip every recycling
    /// completion already triggers (GhostReturnHomeAndDeposit) - the
    /// natural "loot/recycle, come home, check" cycle the spec describes
    /// already happens for free once this is added at the END of that
    /// trip, no separate periodic scheduler needed. Checks affordability
    /// against real BASE STORAGE totals (owned boxes), not carried
    /// inventory - inventory is usually near-empty right after a deposit
    /// trip, the real wealth sits in the boxes. If NOT yet affordable, this
    /// is a genuine no-op - the survivor just resumes whatever it was
    /// doing (normal looting, which already includes monument farming and
    /// opportunistic recycling - see ContinueLootTask's own six-category
    /// search and TryStartRecyclingTask), satisfying "go get more" without
    /// needing new dedicated farming logic. If affordable, withdraws
    /// exactly what's needed straight out of the boxes (WithdrawUpToAmount,
    /// the same container-to-container transfer TryFillOwnedFurnaces
    /// already uses) and starts a real new build via the same
    /// TryStartAutonomousBaseBuild pipeline the very first base used -
    /// this builds a genuinely NEW structure (a new cupboard, new site) at
    /// wherever the survivor currently is (right by its old base, and
    /// IsTooCloseToAnotherCupboard's own site-selection check will
    /// relocate it if needed) rather than upgrading the existing one in
    /// place piece-by-piece - this project's CSV-replay system has no
    /// concept of an in-place tier conversion between two different
    /// literal floor plans, so a fresh higher-tier structure is the
    /// closest real equivalent. The old base (and its own boxes/furnace/
    /// cupboard) is left behind, still owned, once Character.Home
    /// reassigns to the new one on build completion (ReplayBuildTrace's
    /// own existing behavior, unchanged).
    /// </summary>
    private void TryPursueTierUpgrade(Survivor survivor, Action onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            onComplete?.Invoke();
            return;
        }

        int nextTierRank = home.TierRank + 1;
        string nextTierFolder = $"tier{nextTierRank}";
        string nextTierDirectory = $"{BaseDesignsDirectory}/{nextTierFolder}";

        if (!Directory.Exists(nextTierDirectory))
        {
            onComplete?.Invoke();
            return;
        }

        string[] designs = Directory.GetFiles(nextTierDirectory, "*.csv");

        if (designs.Length == 0)
        {
            onComplete?.Invoke();
            return;
        }

        if (nextTierRank >= 2 && !IsTierUnlocked(survivor, npc, nextTierRank, out string _))
        {
            onComplete?.Invoke();
            return;
        }

        string designPath = designs[UnityEngine.Random.Range(0, designs.Length)];
        Dictionary<string, int> cost = CalculateTraceResourceRequirements(designPath, freeFirstTwoDoorsAndLocks: true);
        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);

        bool canAfford = cost.Count > 0 && cost.All(requirement =>
        {
            int itemId = ItemManager.FindItemDefinition(requirement.Key)?.itemid ?? 0;

            if (itemId == 0)
            {
                return false;
            }

            int have = boxes.Where(b => b != null && !b.IsDestroyed && b.inventory != null)
                .SelectMany(b => b.inventory.itemList)
                .Where(i => i.info.itemid == itemId)
                .Sum(i => i.amount);

            return have >= requirement.Value;
        });

        if (!canAfford)
        {
            onComplete?.Invoke();
            return;
        }

        Puts($"tier-upgrade: '{survivor.Character.Alias}' has enough stored for '{nextTierFolder}/{Path.GetFileNameWithoutExtension(designPath)}' - withdrawing and starting the upgrade.");

        foreach (KeyValuePair<string, int> requirement in cost)
        {
            WithdrawUpToAmount(boxes, requirement.Key, requirement.Value, npc.inventory.containerMain);
        }

        // Deliberately does NOT call onComplete - building the upgrade IS
        // the survivor's next task now, same contract TryPursueBaseGatherGoal
        // itself already uses (readiness replaces "resume the old task,"
        // it doesn't chain after it). oldHomeToMigrate (2026-09-01, Lucas's
        // own explicit caveat) - passing the CURRENT home (about to be
        // overwritten the instant the new base finishes building) is what
        // lets TryStartAutonomousBaseBuild's own completion route back to
        // collect everything from it afterward.
        TryStartAutonomousBaseBuild(survivor, npc, designPath, survivor.Character.Id, oldHomeToMigrate: home);
    }

    private void CrossHomeDoor(Survivor survivor, HomeBase home, Vector3 farPoint, Vector3 finalDestination, Action onArrived, Action onFailed)
    {
        _activeHomeDoorCrossings.Add(survivor.Character.Id);

        Door ResolveDoor()
        {
            Collider[] hits = Physics.OverlapSphere(home.DoorPosition, 1f, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

            foreach (Collider hit in hits)
            {
                Door candidate = hit.GetComponentInParent<Door>();

                if (candidate != null && !candidate.IsDestroyed)
                {
                    return candidate;
                }
            }

            return null;
        }

        Door door = ResolveDoor();

        if (door != null && !door.IsOpen())
        {
            door.SetOpen(true);
            door.SendNetworkUpdate();
        }

        void FinishCrossing()
        {
            // Real re-resolve, not the captured reference above - the
            // whole crossing takes a couple of real seconds
            // (StartPhasingToDestination steps at running speed, not
            // instantly), long enough that the original Door reference
            // could theoretically have been destroyed/replaced in the
            // meantime (an upgrade, a raid) - closing whatever's actually
            // there now is more robust than trusting a stale reference.
            Door doorToClose = ResolveDoor();

            if (doorToClose != null && doorToClose.IsOpen())
            {
                doorToClose.SetOpen(false);
                doorToClose.SendNetworkUpdate();
            }

            // See DoorCloseHeightCorrectionTolerance's own doc comment
            // (GhostThroughOwnDoor) - same real fix here: closing the
            // door can physically shove the survivor if it's still
            // overlapping the door's own collider, most often straight
            // up onto an overhead floor slab on a multi-story design.
            // home.DoorPosition.y is the one height already known for
            // certain to be correct immediately after this crossing.
            BasePlayer crossingNpc = survivor.Player;

            if (crossingNpc != null && !crossingNpc.IsDestroyed && Mathf.Abs(crossingNpc.transform.position.y - home.DoorPosition.y) > DoorCloseHeightCorrectionTolerance)
            {
                Vector3 corrected = crossingNpc.transform.position;
                corrected.y = home.DoorPosition.y;
                crossingNpc.transform.position = corrected;
                crossingNpc.MovePosition(corrected);
            }

            _activeHomeDoorCrossings.Remove(survivor.Character.Id);
            StartWalkingWithRecovery(survivor, finalDestination, onArrived, onFailed, recoveryTier: 1);
        }

        StartLevelledPhasing(
            survivor,
            home.DoorPosition,
            onArrived: () => StartPhasingToDestination(
                survivor,
                farPoint,
                onArrived: FinishCrossing,
                onFailed: FinishCrossing),
            onFailed: FinishCrossing);
    }

    /// <summary>
    /// Real explicit "leave home before doing anything else" gate
    /// (2026-08-29, eighth round - Lucas's own proposed fix: "I have
    /// finished task -> am I inside of the base I built? Yes? -> exit
    /// base using ghostroute in reverse... re-roll task for whatever I
    /// want to do next"). See AdvanceBuildReplay's own call site doc
    /// comment (BaseBuilding.cs) for the specific bug this closes - a
    /// survivor's very next task decision kicking off a normal walk
    /// whose native-movement setup silently Warps it outside before any
    /// of this project's own door-crossing checks ever get a chance to
    /// run. Deliberately synchronous with build completion rather than
    /// folded into the general walk system - the one moment a survivor
    /// is GUARANTEED to be standing inside its own home, handled before
    /// anything else touches its movement. Same route-vs-computed-
    /// geometry priority as TryStartHomeDoorRouteCrossing below, just
    /// with no further destination to continue toward afterward - the
    /// caller's own onComplete is exactly "now go decide what to do
    /// next," same as if nothing needed leaving at all.
    /// </summary>
    private void ExitHomeIfInside(Survivor survivor, HomeBase home, Action onCompleteRaw)
    {
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            VerbosePuts($"exit-home-gate: '{survivor?.Character?.Alias}' skipping - home or npc missing.");
            onCompleteRaw?.Invoke();
            return;
        }

        // Real shared busy-guard (2026-09-01, live report + phase-diag
        // proof: 'GhostTorch' oscillated between TWO different phase
        // targets forever, the logged destination alternating every single
        // line, because ExitHomeIfInside and TryStartHomeDoorRouteCrossing
        // had SEPARATE notions of "busy" - TryStartHomeDoorRouteCrossing
        // checks/sets _activeHomeDoorCrossings, this function checked/set
        // nothing at all, so both could genuinely run concurrently on the
        // same survivor, each call's own CancelActiveMovement wiping out
        // whatever phase timer the OTHER one had in flight before it could
        // ever finish - neither could make real progress, forever.
        // Sharing the same guard means whichever one starts first
        // genuinely owns the survivor's movement until it's actually done.
        if (_activeHomeDoorCrossings.Contains(survivor.Character.Id))
        {
            VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' skipping - a door crossing is already in flight for this survivor.");
            onCompleteRaw?.Invoke();
            return;
        }

        _activeHomeDoorCrossings.Add(survivor.Character.Id);

        // Every onComplete?.Invoke() below this point (there are several
        // exit paths through this function) now goes through this wrapper,
        // so the busy flag is always released exactly once, regardless of
        // which path was taken - no need to touch every individual call
        // site.
        Action onComplete = () =>
        {
            _activeHomeDoorCrossings.Remove(survivor.Character.Id);
            onCompleteRaw?.Invoke();
        };

        // HomeInteriorRadius, not the wider HomeCrossingRadius - this gate
        // means "am I actually inside my base right now," not "merely
        // somewhere near it" (2026-09-01, same fix/reasoning as
        // TryStartHomeDoorRouteCrossing's own doc comment above). Using
        // the 20m radius here meant a survivor that finished its build
        // task already standing outside (e.g. stuck 8m away failing to
        // path to a placement point) still "passed" this check and tried
        // to walk to a route's near end and play it OUTWARD again, which
        // never made sense for a survivor that was never inside to begin
        // with.
        float thresholdSqr = HomeInteriorRadius * HomeInteriorRadius;
        float distSqr = (npc.transform.position - home.Position).sqrMagnitude;

        if (distSqr >= thresholdSqr)
        {
            VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' not actually inside home (dist={Mathf.Sqrt(distSqr):F1}m, radius={HomeInteriorRadius:F1}m) - nothing to exit.");
            onComplete?.Invoke();
            return;
        }

        VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' is inside home (dist={Mathf.Sqrt(distSqr):F1}m) - {home.DoorRoutes.Count} hardcoded route(s) available.");

        if (home.DoorRoutes.Count > 0)
        {
            HomeDoorRoute bestRoute = null;
            bool reversed = false;
            float bestDistSqr = float.MaxValue;

            foreach (HomeDoorRoute route in home.DoorRoutes)
            {
                if (route.Waypoints.Count == 0)
                {
                    continue;
                }

                float distToStart = (route.Waypoints[0] - npc.transform.position).sqrMagnitude;
                float distToEnd = (route.Waypoints[^1] - npc.transform.position).sqrMagnitude;
                float minDist = Mathf.Min(distToStart, distToEnd);

                if (minDist < bestDistSqr)
                {
                    bestDistSqr = minDist;
                    bestRoute = route;
                    reversed = distToEnd < distToStart;
                }
            }

            if (bestRoute != null)
            {
                List<Vector3> orderedWaypoints = reversed ? Enumerable.Reverse(bestRoute.Waypoints).ToList() : bestRoute.Waypoints;
                Vector3 nearEnd = orderedWaypoints[0];
                Vector3 doorAnchor = bestRoute.DoorAnchorPosition;

                VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' phasing to route near-end {nearEnd} (reversed={reversed}) before playing the hardcoded route out.");

                Door ResolveRouteDoor()
                {
                    Collider[] hits = Physics.OverlapSphere(doorAnchor, 1f, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

                    foreach (Collider hit in hits)
                    {
                        Door candidate = hit.GetComponentInParent<Door>();

                        if (candidate != null && !candidate.IsDestroyed)
                        {
                            return candidate;
                        }
                    }

                    return null;
                }

                // StartLevelledPhasing, not StartWalking (2026-09-01, live
                // test: every single test bot failed this leg - "destination
                // 4.68m from nearest navmesh point" - because nearEnd sits
                // inside the player-built base, which the static navmesh has
                // zero knowledge of, same root cause as this whole session's
                // NavMeshAgent.Warp fix. Ordinary collision-based pathing can
                // never reach an interior point; phasing (already used for
                // every other interior leg in this system) sidesteps the
                // navmesh dependency entirely, and nearEnd is always close
                // by construction (picked as the closest route endpoint,
                // gated by HomeInteriorRadius), so this is always a short
                // hop, not a long warp.
                StartLevelledPhasing(
                    survivor,
                    nearEnd,
                    onArrived: () =>
                    {
                        Door routeDoor = ResolveRouteDoor();

                        VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' reached route near-end - door at anchor {doorAnchor} {(routeDoor != null ? $"FOUND (open={routeDoor.IsOpen()})" : "NOT FOUND")}, playing the route.");

                        if (routeDoor != null && !routeDoor.IsOpen())
                        {
                            routeDoor.SetOpen(true);

                            // Real fix for "door never visibly opens" (2026-
                            // 09-01, live report + frame-by-frame video
                            // review: across the ENTIRE crossing, both doors
                            // showed as visually closed on Lucas's own
                            // client the whole time, despite Door.IsOpen()
                            // correctly reporting true server-side the whole
                            // time too). SetOpen() alone doesn't guarantee
                            // an immediate network broadcast of the state
                            // change - a well-documented Rust/Oxide modding
                            // gotcha (entity state changes made server-side,
                            // outside the normal player-interaction RPC
                            // path, need an explicit SendNetworkUpdate to
                            // actually reach observing clients). This was
                            // very likely the real root cause behind EVERY
                            // "teleports through a closed door" report this
                            // whole session, regardless of which movement
                            // mechanism was used - the door itself was never
                            // visibly opening for anyone watching.
                            routeDoor.SendNetworkUpdate();
                        }

                        // Real delay before moving, then a real COLLISION walk
                        // (2026-09-01, live report: "instant inside->outside
                        // in a split second... doors didn't even open" -
                        // still true even once the door was confirmed
                        // genuinely toggling open server-side, because
                        // StartGhostRoute's phase movement never actually
                        // depends on collision or the door's state at all -
                        // see StartCollisionWalkRoute's own doc comment).
                        // Giving the door a moment to actually swing open
                        // before the walk starts, then walking the recorded
                        // path with real collision, makes the crossing look
                        // like a real player using the door instead of a
                        // glide that happens to have a door animation next
                        // to it.
                        timer.Once(DoorRouteOpenAnimationDelay, () => StartCollisionWalkRoute(survivor, orderedWaypoints, 0, () =>
                        {
                            Door doorToClose = ResolveRouteDoor();

                            VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' finished the route - door at anchor {doorAnchor} {(doorToClose != null ? $"FOUND (open={doorToClose.IsOpen()})" : "NOT FOUND")}.");

                            if (doorToClose != null && doorToClose.IsOpen())
                            {
                                doorToClose.SetOpen(false);
                                doorToClose.SendNetworkUpdate();
                            }

                            onComplete?.Invoke();
                        }));
                    },
                    onFailed: () =>
                    {
                        VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' failed to phase to route near-end {nearEnd} - giving up on the hardcoded route this time.");
                        onComplete?.Invoke();
                    });

                return;
            }
        }

        if (home.DoorPosition == Vector3.zero)
        {
            VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' has no hardcoded routes and no computed DoorPosition either - nothing to exit through.");
            onComplete?.Invoke();
            return;
        }

        VerbosePuts($"exit-home-gate: '{survivor.Character.Alias}' falling back to computed-geometry exit (no hardcoded route matched).");

        Door ResolveHomeDoor()
        {
            Collider[] hits = Physics.OverlapSphere(home.DoorPosition, 1f, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

            foreach (Collider hit in hits)
            {
                Door candidate = hit.GetComponentInParent<Door>();

                if (candidate != null && !candidate.IsDestroyed)
                {
                    return candidate;
                }
            }

            return null;
        }

        // StartLevelledPhasing, not StartWalking - same off-static-navmesh
        // reasoning as the DoorRoutes branch above's own doc comment
        // (InsidePoint is just as much an interior point as any recorded
        // route waypoint).
        StartLevelledPhasing(
            survivor,
            home.InsidePoint,
            onArrived: () =>
            {
                Door door = ResolveHomeDoor();

                if (door != null && !door.IsOpen())
                {
                    door.SetOpen(true);
                    door.SendNetworkUpdate();
                }

                StartLevelledPhasing(
                    survivor,
                    home.DoorPosition,
                    onArrived: () => StartPhasingToDestination(
                        survivor,
                        home.OutsidePoint,
                        onArrived: () =>
                        {
                            Door doorToClose = ResolveHomeDoor();

                            if (doorToClose != null && doorToClose.IsOpen())
                            {
                                doorToClose.SetOpen(false);
                                doorToClose.SendNetworkUpdate();
                            }

                            onComplete?.Invoke();
                        },
                        onFailed: onComplete),
                    onFailed: onComplete);
            },
            onFailed: onComplete);
    }

    /// <summary>
    /// Real hardcoded door-route crossing (2026-08-29, seventh round) -
    /// checked BEFORE the computed-geometry TryGetHomeDoorCrossing below,
    /// since an authored recording (see HomeBase.DoorRoutes' own doc
    /// comment) is the whole point of the fallback - once one exists for
    /// this design, it should always win over guessing from geometry.
    /// Same "is this walk actually crossing between inside and outside"
    /// gate and per-Character busy flag as TryGetHomeDoorCrossing (a
    /// crossing already in flight must never be re-triggered mid-route).
    /// Walks normally (real collision) to whichever end of the chosen
    /// route is closer, then plays the recorded path itself via
    /// StartGhostRoute - the same proven waypoint-phase engine already
    /// used for every monument ghost route, chosen specifically because
    /// it phases (no collision) rather than trusting the door's own
    /// collider/physics through the crossing itself, sidestepping every
    /// one of this session's door/physics failures entirely. Direction-
    /// agnostic: a route recorded walking OUT works equally well for
    /// walking IN, just played in reverse.
    /// </summary>
    private bool TryStartHomeDoorRouteCrossing(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed, Func<BasePlayer, bool> shouldWalkCarefully)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || home.DoorRoutes.Count == 0 || npc == null)
        {
            return false;
        }

        if (_activeHomeDoorCrossings.Contains(survivor.Character.Id))
        {
            return false;
        }

        float thresholdSqr = HomeInteriorRadius * HomeInteriorRadius;
        bool currentlyInside = (npc.transform.position - home.Position).sqrMagnitude < thresholdSqr;
        bool destinationInside = (destination - home.Position).sqrMagnitude < thresholdSqr;

        if (currentlyInside == destinationInside)
        {
            return false;
        }

        VerbosePuts($"home-door-route: '{survivor.Character.Alias}' hardcoded-route crossing triggered (currentlyInside={currentlyInside}) toward {destination}.");

        HomeDoorRoute bestRoute = null;
        bool reversed = false;
        float bestDistSqr = float.MaxValue;

        foreach (HomeDoorRoute route in home.DoorRoutes)
        {
            if (route.Waypoints.Count == 0)
            {
                continue;
            }

            float distToStart = (route.Waypoints[0] - npc.transform.position).sqrMagnitude;
            float distToEnd = (route.Waypoints[^1] - npc.transform.position).sqrMagnitude;
            float minDist = Mathf.Min(distToStart, distToEnd);

            if (minDist < bestDistSqr)
            {
                bestDistSqr = minDist;
                bestRoute = route;
                reversed = distToEnd < distToStart;
            }
        }

        if (bestRoute == null)
        {
            return false;
        }

        List<Vector3> orderedWaypoints = reversed ? Enumerable.Reverse(bestRoute.Waypoints).ToList() : bestRoute.Waypoints;
        Vector3 nearEnd = orderedWaypoints[0];

        Door ResolveRouteDoor()
        {
            Collider[] hits = Physics.OverlapSphere(bestRoute.DoorAnchorPosition, 1f, LayerMask.GetMask("Construction"), QueryTriggerInteraction.Ignore);

            foreach (Collider hit in hits)
            {
                Door candidate = hit.GetComponentInParent<Door>();

                if (candidate != null && !candidate.IsDestroyed)
                {
                    return candidate;
                }
            }

            return null;
        }

        void PlayRoute()
        {
            Door routeDoor = ResolveRouteDoor();

            VerbosePuts($"home-door-route: '{survivor.Character.Alias}' door at anchor {bestRoute.DoorAnchorPosition} {(routeDoor != null ? $"FOUND (open={routeDoor.IsOpen()})" : "NOT FOUND")}, playing the route.");

            if (routeDoor != null && !routeDoor.IsOpen())
            {
                routeDoor.SetOpen(true);

                // Real fix for "door never visibly opens" - see
                // ExitHomeIfInside's identical fix/doc comment (2026-09-01)
                // for the full reasoning (frame-by-frame video review
                // confirmed the door stayed visually closed the whole
                // crossing despite IsOpen() correctly reporting true
                // server-side).
                routeDoor.SendNetworkUpdate();
            }

            // Real delay before moving, then a real COLLISION walk - see
            // ExitHomeIfInside's identical fix/doc comment (2026-09-01) for
            // the full reasoning: StartGhostRoute's phase movement never
            // actually depends on collision or the door's state, so it
            // looked like a teleport even with the door genuinely open.
            timer.Once(DoorRouteOpenAnimationDelay, () => StartCollisionWalkRoute(survivor, orderedWaypoints, 0, () =>
            {
                Door doorToClose = ResolveRouteDoor();

                if (doorToClose != null && doorToClose.IsOpen())
                {
                    doorToClose.SetOpen(false);
                    doorToClose.SendNetworkUpdate();
                }

                _activeHomeDoorCrossings.Remove(survivor.Character.Id);

                // Real "last mile" fix (2026-09-01, live report: door route
                // plays cleanly - doors genuinely open now - but the survivor
                // then endlessly re-triggers the SAME crossing every ~10s,
                // "phasing to spots randomly around the base, rinse and
                // repeat"). Root cause: the route's own recorded endpoint
                // isn't necessarily the task's real destination (e.g. a
                // step further toward the cupboard) - this handoff used to
                // ALWAYS fall through to StartWalkingWithRecovery's ordinary
                // recoveryTier:1 path, which deliberately skips the door-
                // route system entirely and uses plain navmesh-based
                // walking - the exact "destination off the static navmesh"
                // wall this whole session has been fighting, for whatever
                // interior distance remains. That failure was escalating
                // into the old generic stuck-recovery ladder, which most
                // likely snapped the survivor back outside, so the task
                // loop just re-triggered the whole crossing from scratch
                // forever. If the real destination is STILL an interior
                // point (within HomeInteriorRadius of the cupboard), phase
                // the rest of the way directly instead of handing off to a
                // system that can't reach it.
                if (home != null && (destination - home.Position).sqrMagnitude < HomeInteriorRadius * HomeInteriorRadius)
                {
                    VerbosePuts($"home-door-route: '{survivor.Character.Alias}' route done, destination {destination} is still interior - phasing the rest of the way instead of handing off to ordinary walking.");
                    StartLevelledPhasing(survivor, destination, onArrived, onFailed);
                }
                else
                {
                    StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 1);
                }
            }));
        }

        _activeHomeDoorCrossings.Add(survivor.Character.Id);

        VerbosePuts($"home-door-route: '{survivor.Character.Alias}' phasing to route near-end {nearEnd} (reversed={reversed}) before playing the hardcoded route.");

        // StartLevelledPhasing, not StartWalking - same off-static-navmesh
        // reasoning as ExitHomeIfInside's own doc comment above (nearEnd is
        // an interior/route point the static navmesh doesn't know about
        // whenever the survivor starts out currently inside).
        StartLevelledPhasing(
            survivor,
            nearEnd,
            onArrived: () =>
            {
                VerbosePuts($"home-door-route: '{survivor.Character.Alias}' reached route near-end - opening door and playing the route.");
                PlayRoute();
            },
            onFailed: () =>
            {
                // Deliberately NOT falling through to a plain StartWalking/
                // EscalateStuckRecovery chain here (2026-09-01, live report:
                // "teleports from ground level to inside the ceiling and
                // back... doesn't properly know how to integrate with
                // player-built objects"). That generic ladder's own last-
                // resort tiers (wiggle/navmesh-nudge/emergency-teleport)
                // have zero concept of ghost routes or player-built
                // interiors - handing it a destination this deep inside a
                // private structure is exactly what produced the wild
                // vertical teleport chaos. If the crossing itself couldn't
                // even reach the route's own near-end, just admit this
                // attempt failed and let the caller's own task loop retry
                // later, rather than escalating into a system that was
                // never built to handle this case.
                VerbosePuts($"home-door-route: '{survivor.Character.Alias}' failed to phase to route near-end {nearEnd} - giving up on this crossing attempt (not escalating to generic stuck-recovery).");
                _activeHomeDoorCrossings.Remove(survivor.Character.Id);
                onFailed?.Invoke();
            });

        return true;
    }

    private void StartWalkingWithRecovery(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed, int recoveryTier = 0, Func<BasePlayer, bool> shouldWalkCarefully = null)
    {
        // Checked only on a genuinely fresh walk request (tier 0), never
        // on a stuck-recovery continuation - see TryGetHomeDoorCrossing's
        // own doc comment for the full reasoning.
        if (recoveryTier == 0 && TryStartHomeDoorRouteCrossing(survivor, destination, onArrived, onFailed, shouldWalkCarefully))
        {
            return;
        }

        if (recoveryTier == 0 && TryGetHomeDoorCrossing(survivor, destination, out Vector3 nearPoint, out Vector3 farPoint))
        {
            HomeBase home = survivor.Character.Home;

            // StartLevelledPhasing, not StartWalking - nearPoint is
            // home.InsidePoint whenever the survivor starts out currently
            // inside, an interior point the static navmesh doesn't know
            // about (same off-navmesh reasoning as TryStartHomeDoorRoute
            // Crossing's own doc comment above).
            // Deliberately NOT escalating to EscalateStuckRecovery on
            // failure here either - same reasoning as TryStartHomeDoorRoute
            // Crossing's own doc comment (2026-09-01): that ladder's real
            // teleport fallback has no concept of player-built interiors,
            // and nearPoint can be an interior point (home.InsidePoint)
            // just like a route's near-end.
            StartLevelledPhasing(
                survivor,
                nearPoint,
                onArrived: () => CrossHomeDoor(survivor, home, farPoint, destination, onArrived, onFailed),
                onFailed: onFailed);

            return;
        }

        StartWalking(survivor, destination, onArrived, onFailed: () => EscalateStuckRecovery(survivor, destination, onArrived, onFailed, recoveryTier), shouldWalkCarefully);
    }

    private void EscalateStuckRecovery(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed, int recoveryTier)
    {
        // Single chokepoint every stuck-recovery path (walk, follow, loot)
        // already funnels through - see /lr.tp.stuck's own doc comment for
        // why this is where the stuck flag gets set/cleared rather than at
        // each individual "got stuck"/"wiggled free" log line scattered
        // across callers.
        if (recoveryTier == 0)
        {
            MarkStuck(survivor);

            Action originalOnArrived = onArrived;
            Action originalOnFailed = onFailed;

            onArrived = () => { ClearStuck(survivor); originalOnArrived?.Invoke(); };
            onFailed = () => { ClearStuck(survivor); originalOnFailed?.Invoke(); };
        }

        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            onFailed?.Invoke();
            return;
        }

        // Wounded/downed - pause entirely rather than fight Rust's own
        // incapacitated state machine, same rule as the main walk timer's
        // own IsWounded() check (see StartWalking's doc comment). A live
        // report caught this whole chain missing that check: wiggle/
        // navmesh-nudge/emergency-teleport could all still fire on a downed
        // survivor, dragging or flat-out teleporting a wounded ragdoll's
        // transform.position around mid-bleedout - fighting Rust's own
        // incapacitated handling badly enough to leave the survivor stuck
        // unkillable, and (via TryEmergencyTeleport's direct position
        // write) visibly flung to wherever the recovery chain landed once
        // despawnall finally killed it. Re-polls on the same cadence until
        // the survivor either gets back up (resumes this exact tier) or
        // dies (OnPlayerDeath's CancelActiveMovement stops this chain the
        // next time it checks npc.IsDestroyed above).
        if (npc.IsWounded())
        {
            timer.Once(StuckReassessPause, () => EscalateStuckRecovery(survivor, destination, onArrived, onFailed, recoveryTier));
            return;
        }

        // Checked on EVERY stuck episode regardless of tier (2026-08-16,
        // was "only at tier 0" - real live bug: a monument doorway can
        // have MORE THAN ONE real barricade in sequence, and gating this
        // to tier 0 only meant that once a survivor broke through the
        // FIRST one and resumed at tier 1+ (see below), it permanently
        // lost the ability to ever detect a SECOND one for the rest of
        // that walk - confirmed live, 'NumbJackal89' broke barricade #1
        // cleanly, then spent 3+ full 200s timeout cycles endlessly
        // nudging/retrying against barricade #2 a few metres further in,
        // since every subsequent stuck episode re-entered at tier 1+ and
        // never re-checked. No infinite-loop risk from checking every
        // time - TryFindBlockingBarricade's own IsDestroyed/Health()<=0
        // filter means an already-broken barricade simply stops being
        // found, same as any other now-cleared obstacle. See
        // StartAttackingBarricade's own doc comment for the full story on
        // why this is a genuine, correctly-reported Blocked in the first
        // place, not a bug in any of the navmesh/ground-probe fixes from
        // the previous session.
        //
        // Continuation tier: advances 0 -> 1 (so a later episode doesn't
        // re-wrap MarkStuck/ClearStuck a second time - see the "wiggled
        // free" continuation just below for the identical convention),
        // but otherwise preserves whatever tier this episode was ALREADY
        // at rather than resetting backward - breaking barricade #2 while
        // already escalated shouldn't un-escalate the survivor.
        // Real "the actual obstruction is my own closed door" check
        // (2026-08-29, Lucas's own explicit ask: "how the bot gets in and
        // out of the base without teleporting"). Checked on every stuck
        // episode, same reasoning as the barricade check right below - a
        // closed door genuinely blocking a survivor's step is a totally
        // legitimate, common, and TRIVIAL fix (no swing/attack loop
        // needed, just SetOpen) compared to everything else this ladder
        // exists for, so it's worth resolving before any of the heavier
        // wiggle/nudge/teleport machinery ever gets a chance to fire.
        // Without this, a survivor standing inside the base it just
        // finished building had no way out except phasing through its
        // own walls the instant normal tasks resumed and pathed it
        // straight into its own closed front door.
        if (TryFindBlockingClosedDoor(npc, out Door blockingDoor))
        {
            blockingDoor.SetOpen(true);
            blockingDoor.SendNetworkUpdate();

            int doorContinuationTier = recoveryTier == 0 ? 1 : recoveryTier;

            // Real live bug (2026-08-29, second round - Lucas's own
            // report: "sometimes able to walk through... very
            // temperamental... when I walk outside, the bot also
            // teleports out"). SetOpen alone fixes the door's own COLLIDER
            // state, but the doorway threshold's real ground-probe/local-
            // stepping unreliability (the same class of bug
            // IsNonSteppableCollider's own doc comment documents
            // repeatedly for other geometry) can still misjudge the
            // handful of steps actually crossing the threshold even once
            // it's open - resuming through the normal ladder afterward
            // still risked eventually exhausting it and phasing, exactly
            // what's meant to be reserved for genuinely unreachable
            // destinations, not a door the survivor legitimately owns.
            //
            // Lucas's own proposed fix: treat crossing a door it BUILT
            // ITSELF like this project's existing authored ghost routes
            // (StartGhostRoute/MonumentRoutes.cs) - a short, fully-known
            // hop doesn't need general pathfinding's uncertainty at all,
            // since the survivor is already walking directly at the
            // door and doorways are flush with the floor either side (no
            // real elevation change to get wrong). Explicitly reserved for
            // OwnerID matching this survivor - a door it didn't place
            // still only gets the plain SetOpen+resume treatment above,
            // never this guaranteed hop, so this can never become a way
            // to bypass another base's real security (Lucas's own
            // explicit boundary: "we don't want the bot potentially
            // phasing through doors that it never placed, defeats the
            // purpose of building a base to secure your loot").
            if (blockingDoor.OwnerID == npc.userID)
            {
                // Real hardcoded door-route preference (2026-08-29,
                // seventh round) - see TryStartHomeDoorRouteCrossing's own
                // doc comment. Tried FIRST here too, same as the proactive
                // StartWalkingWithRecovery entry point - an authored
                // recording should win over the geometry-based
                // GhostThroughOwnDoor fallback below whenever one exists
                // for this design, not just on a fresh top-level walk.
                if (TryStartHomeDoorRouteCrossing(survivor, destination, onArrived, onFailed, null))
                {
                    return;
                }

                VerbosePuts($"'{survivor.Character.Alias}' opened its own door and is stepping straight through it.");
                GhostThroughOwnDoor(survivor, blockingDoor, destination, onArrived, onFailed, doorContinuationTier);
                return;
            }

            // Real live bug (2026-08-29, fourth round - Lucas's own
            // report: approaching a closed door it doesn't own "side on"
            // instead of square-on left it oscillating rather than ever
            // cleanly stepping through, even once opened - a real player
            // walks up to face a door before opening it, never sidesteps
            // through at an angle). Square up to the door FIRST, on
            // whichever side the survivor is already standing (a plain
            // real walk, no phasing - this never crosses the door's own
            // plane, so it stays well inside the "never for a door it
            // didn't place" boundary above), then open it, then hand back
            // to the normal ladder for the rest of the real journey -
            // approaching flush with the doorway's own axis is exactly
            // what already works reliably for GhostThroughOwnDoor's own
            // door-center anchoring above.
            Vector3 doorApproachPoint = ComputeDoorApproachPoint(npc, blockingDoor);

            void OpenDoorAndContinue()
            {
                if (!blockingDoor.IsDestroyed && !blockingDoor.IsOpen())
                {
                    blockingDoor.SetOpen(true);
                    blockingDoor.SendNetworkUpdate();
                }

                VerbosePuts($"'{survivor.Character.Alias}' opened a closed door that was blocking its path.");
                StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: doorContinuationTier);
            }

            // Real live bug (2026-08-29, fifth round - Lucas's own
            // explicit framing: a flat foundation built on sloped ground
            // always ends up with ITS door sitting some real height above
            // the ground on whichever edge happened to land on the
            // downhill side - not a build mistake, just unavoidable given
            // flat foundations on uneven terrain, and "hard to tell when
            // they will build it which way" since that depends on the
            // site's own slope, not a choice this project controls for.
            // "A real player could still technically make it through the
            // door by jumping up into the frame" - a plain collision-
            // respecting StartWalking has no equivalent (real per-step
            // climb height is far smaller than a real player's jump), so
            // it just oscillates at the base of the gap forever, same
            // failure StartLevelledPhasing already solves for
            // GhostThroughOwnDoor's own OWNED-door case. Reused here too,
            // capped to DoorJumpableHeight so this never becomes a way to
            // silently no-clip past a genuinely broken/unreachable height
            // difference - only ever a real player's own realistic jump.
            float approachHeightGap = Mathf.Abs(npc.transform.position.y - doorApproachPoint.y);

            if (approachHeightGap > DoorVerticalAlignmentThreshold && approachHeightGap <= DoorJumpableHeight)
            {
                StartLevelledPhasing(survivor, doorApproachPoint, onArrived: OpenDoorAndContinue, onFailed: OpenDoorAndContinue);
            }
            else
            {
                StartWalking(survivor, doorApproachPoint, onArrived: OpenDoorAndContinue, onFailed: OpenDoorAndContinue);
            }

            return;
        }

        if (_engine.NavigationManager.TryFindBlockingBarricade(npc.transform.position, BarricadeAttackDetectionRange, out Barricade blockingBarricade))
        {
            VerbosePuts($"'{survivor.Character.Alias}' found a real barricade nearby - approaching it to break through instead of trying to route around.");

            int continuationTier = recoveryTier == 0 ? 1 : recoveryTier;

            // StartAttackingBarricade assumes it's ALREADY in range (same
            // contract as StartAttackingContainer) - it doesn't walk
            // closer itself. Given TryFindBlockingBarricade's detection
            // radius (6m) is now deliberately wider than melee range
            // (2026-08-16, widened after the barricade wasn't being found
            // at the old 3m directional-raycast range), the survivor needs
            // a real approach leg first, same as any other container/
            // corpse interaction, before the swing loop can assume
            // IsWithinLootRange will actually pass on its first tick.
            Vector3 approachPoint = GetApproachPoint(blockingBarricade, npc);

            StartWalking(
                survivor,
                approachPoint,
                onArrived: () => StartAttackingBarricade(survivor, blockingBarricade,
                    onSuccess: () => StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: continuationTier),
                    onFailed: () => EscalateStuckRecovery(survivor, destination, onArrived, onFailed, continuationTier)),
                onFailed: () => EscalateStuckRecovery(survivor, destination, onArrived, onFailed, continuationTier));

            return;
        }

        // Real "am I stuck ON a tree/ore node itself" check (2026-08-28,
        // Lucas's own live report: bots caught oscillating in place within
        // ~1m of a resource node rather than being genuinely blocked by
        // anything else). Checked on every stuck episode, same reasoning
        // as the barricade check just above (a survivor can clear one
        // obstruction only to catch a second later in the same walk). If a
        // gather-capable survivor is standing this close to a live tree/
        // ore node, the node itself is almost certainly what's actually
        // blocking the path rather than a real navmesh/geometry problem -
        // gather it, then resume the original destination at an advanced
        // tier exactly like the barricade branch does. Trees checked
        // before ore, same priority order TryStartResourceGatheringFallback
        // already uses.
        if (_engine.NavigationManager.TryFindNearestTreeEntity(npc.transform.position, StuckResourceNodeDetectionRadius, out TreeEntity blockingTree, candidate => !IsInMonumentAvoidZone(candidate.transform.position) && !IsResourceNodePoisoned(candidate))
            && HasAnyGatherCapableTool(npc, TreeGatherToolPriority))
        {
            VerbosePuts($"'{survivor.Character.Alias}' is stuck right next to a live tree ('{blockingTree.ShortPrefabName}') - gathering it before continuing.");

            int nodeContinuationTier = recoveryTier == 0 ? 1 : recoveryTier;

            StartGatheringResourceNode(
                survivor,
                blockingTree,
                TreeGatherToolPriority,
                onSuccess: () => StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: nodeContinuationTier),
                onFailed: () => StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: nodeContinuationTier));

            return;
        }

        if (_engine.NavigationManager.TryFindNearestOreResourceEntity(npc.transform.position, StuckResourceNodeDetectionRadius, out OreResourceEntity blockingOre, candidate => !IsInMonumentAvoidZone(candidate.transform.position) && !IsResourceNodePoisoned(candidate))
            && HasAnyGatherCapableTool(npc, OreGatherToolPriority))
        {
            VerbosePuts($"'{survivor.Character.Alias}' is stuck right next to a live ore node ('{blockingOre.ShortPrefabName}') - gathering it before continuing.");

            int nodeContinuationTier = recoveryTier == 0 ? 1 : recoveryTier;

            StartGatheringResourceNode(
                survivor,
                blockingOre,
                OreGatherToolPriority,
                onSuccess: () => StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: nodeContinuationTier),
                onFailed: () => StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: nodeContinuationTier));

            return;
        }

        if (recoveryTier == 0)
        {
            VerbosePuts($"'{survivor.Character.Alias}' got stuck - trying to wiggle free before giving up.");

            // See StuckReassessPause's own doc comment - a believable
            // "notice I'm stuck, look around" beat before the wiggle
            // itself starts, rather than instantly snapping into motion
            // the same tick the block was detected.
            FaceDirection(npc, destination - npc.transform.position);

            timer.Once(StuckReassessPause, () =>
            {
                BasePlayer reassessNpc = survivor.Player;

                if (reassessNpc == null || reassessNpc.IsDestroyed)
                {
                    onFailed?.Invoke();
                    return;
                }

                if (reassessNpc.IsWounded())
                {
                    EscalateStuckRecovery(survivor, destination, onArrived, onFailed, recoveryTier);
                    return;
                }

                TryWiggleFree(survivor, destination, wiggled =>
                {
                    if (wiggled)
                    {
                        VerbosePuts($"'{survivor.Character.Alias}' wiggled free - retrying its destination.");
                    }

                    StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 1);
                });
            });

            return;
        }

        if (recoveryTier == 1)
        {
            FaceDirection(npc, destination - npc.transform.position);

            timer.Once(StuckReassessPause, () =>
            {
                BasePlayer reassessNpc = survivor.Player;

                if (reassessNpc == null || reassessNpc.IsDestroyed)
                {
                    onFailed?.Invoke();
                    return;
                }

                if (reassessNpc.IsWounded())
                {
                    EscalateStuckRecovery(survivor, destination, onArrived, onFailed, recoveryTier);
                    return;
                }

                // NavMesh.SamplePosition only checks proximity to A
                // navmesh surface, not whether it's actually reachable
                // from where the survivor currently is - a genuinely
                // isolated position (the whole point of this tier) can
                // have a "nearby" navmesh point that's just as
                // unreachable, for the identical underlying reason. A
                // live trace showed exactly this: every nudge attempt
                // immediately failed with the same NoPath, wasting a full
                // real-time walk attempt each time. Checking
                // TryCalculatePath first - the real pathing engine, not
                // distance-based sampling - skips straight past a doomed
                // attempt instead of pretending it might work.
                if (TryFindNavMeshNudgePoint(reassessNpc.transform.position, out Vector3 navMeshPoint)
                    && _engine.NavigationManager.TryCalculatePath(reassessNpc.transform.position, navMeshPoint, new RustNavMeshPath(), out _))
                {
                    VerbosePuts($"'{survivor.Character.Alias}' still stuck - trying to reach the nearest real, reachable navmesh point at {navMeshPoint}.");

                    StartWalking(
                        survivor,
                        navMeshPoint,
                        onArrived: () =>
                        {
                            VerbosePuts($"'{survivor.Character.Alias}' reached solid navmesh - retrying its original destination.");
                            StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 2);
                        },
                        onFailed: () => StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 2));

                    return;
                }

                StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 2);
            });

            return;
        }

        if (recoveryTier == 2)
        {
            FaceDirection(npc, destination - npc.transform.position);

            timer.Once(StuckReassessPause, () =>
            {
                BasePlayer reassessNpc = survivor.Player;

                if (reassessNpc == null || reassessNpc.IsDestroyed)
                {
                    onFailed?.Invoke();
                    return;
                }

                if (reassessNpc.IsWounded())
                {
                    EscalateStuckRecovery(survivor, destination, onArrived, onFailed, recoveryTier);
                    return;
                }

                if (TryEmergencyTeleport(survivor))
                {
                    VerbosePuts($"'{survivor.Character.Alias}' exhausted wiggling and a navmesh nudge - emergency-relocated a short distance to recover.");
                    StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 3);
                    return;
                }

                StartWalkingWithRecovery(survivor, destination, onArrived, onFailed, recoveryTier: 3);
            });

            return;
        }

        // Real "the actual obstruction is a cactus, not the destination
        // itself" check (2026-08-28, Lucas's own live report: two smaller-
        // form-factor cactus variants - cactus-3/cactus-7, confirmed via
        // his own /lr.debug.scan - wedged a survivor exactly the way the
        // original single-trunk cactus already proved capable of
        // (IsBlockedByCactus's own doc comment), which then invoked this
        // exact phase-through as a "last resort" and walked it straight
        // through solid ground. The RecordPotentialAvoidZone(destination)
        // call below this branch doesn't help here - destination is
        // whatever this survivor was ORIGINALLY trying to reach (confirmed
        // live: 'BluntRunner655' got wedged against 'DE_Cactus_Part_04',
        // then phased 75m to a completely unrelated destination), not the
        // cactus's own position, so nothing about the actual bad spot ever
        // got learned. Checked here instead - IsBlockedByCactus's own 2m
        // radius easily covers being "wedged against" one - and gives up
        // cleanly rather than phasing, same as any other genuinely
        // unreachable case, while feeding the survivor's OWN current
        // position (where the cactus actually is) into the avoid-zone
        // system so this exact spot stops trapping every survivor that
        // ever walks near it.
        if (IsBlockedByCactus(npc.transform.position))
        {
            VerbosePuts($"'{survivor.Character.Alias}' is wedged against a cactus - giving up on this destination rather than phasing through the ground to reach it.");
            RecordPotentialAvoidZone(npc.transform.position);
            onFailed?.Invoke();
            return;
        }

        VerbosePuts($"'{survivor.Character.Alias}' exhausted every real-movement recovery option (wiggle, navmesh nudge, emergency teleport) - phasing directly to the destination as a genuine last resort.");

        // Feeds the same self-learning monument-avoid-zone system
        // PoisonAreaNow already uses (LivingRust.MonumentAvoidZones.cs) -
        // but keyed to destination here, not the survivor's own give-up
        // position. PoisonAreaNow only ever fires from an onFailed branch,
        // yet phasing calls onArrived on success (it always "succeeds" by
        // walking straight through geometry) - so a destination that
        // genuinely has no legitimate path (sealed room, isolated navmesh
        // island, a fragment below the map) was never being recorded at
        // all, letting the exact same bad destination get phased-to over
        // and over by every survivor that ever targets it. destination
        // itself is the fixed anchor (derived from the same container/node's
        // position each retry), unlike the stuck position which drifts
        // attempt to attempt - so repeat offenders now merge into one
        // confirmed zone after AvoidZoneConfirmThreshold hits, same as any
        // other avoid zone, and every existing container/tree/ore candidate
        // filter already checks IsInMonumentAvoidZone, so a confirmed spot
        // is excluded everywhere for free.
        RecordPotentialAvoidZone(destination);
        StartPhasingToDestination(survivor, destination, onArrived, onFailed);
    }

    /// <summary>
    /// True last resort (2026-08-16, Lucas's own explicit request) once
    /// wiggle/navmesh-nudge/emergency-teleport have ALL failed - moves the
    /// survivor directly toward destination via a real, raw
    /// transform.position write (the same mechanism TryEmergencyTeleport
    /// already uses for its own instant relocation, confirmed the actual
    /// way to bypass collision - npc.MovePosition() ALONE is NOT a true
    /// bypass, it's still collision-resolved, confirmed live earlier this
    /// session when an early noclip debug tool using MovePosition alone
    /// got physically caught on top of a car mid-flight), stepped at
    /// normal running speed instead of one big teleport jump - so it reads
    /// as ordinary movement to anyone watching, not a warp. Completely
    /// bypasses TryGetNextStep/IsBodyOverlapping/CalculatePath - by design,
    /// this is the option for when a destination is real and reachable in
    /// principle (a real player can walk there) but every one of this
    /// project's own collision/pathing systems has already been given a
    /// fair, repeated chance and still can't find a way, most likely
    /// because of real, dense interior clutter (the Abandoned Supermarket
    /// keycard room: barricade -> desk -> nearby crates all in one tight
    /// space) rather than a further bug worth chasing. Damage/combat
    /// hitboxes are completely untouched - this only ever writes position,
    /// nothing about the survivor's actual collider/hurtbox changes.
    /// </summary>
    // Below this height difference, StartPhasingToDestination's own
    // single diagonal leg is fine as-is (real steps/thresholds/small
    // terrain noise) - StartLevelledPhasing only kicks in for a genuine
    // floor-height mismatch, matching Lucas's own "if it isn't the same
    // Y axis as the bot" phrasing (2026-08-29, third round).
    private const float DoorVerticalAlignmentThreshold = 0.3f;

    /// <summary>
    /// Real fix for "phase up and ontop of the building" (2026-08-29,
    /// third round - Lucas's own live report after his own traceme: a
    /// real player walking in/out of a doorway holds essentially flat Y
    /// the whole way, only ever a normal step's worth of vertical change).
    /// GhostThroughOwnDoor/CrossHomeDoor's own single-leg
    /// StartPhasingToDestination call moves both axes at once - straight
    /// diagonally toward the door's exact transform.position - and
    /// because phasing has zero collision, if the survivor's own current
    /// Y is off from the door's real floor height (stuck below/above its
    /// own foundation, a stale precomputed DoorPosition, a multi-level
    /// base), that diagonal climbs/dives straight through the roof/floor
    /// instead of being blocked by it, and can end WaypointArriveDistance-
    /// close to the target while still well off the real floor, with nothing
    /// nearby for SnapToGround to catch. Splitting into a vertical-only
    /// leg (stationary horizontally, snapping to the door's own foundation
    /// height first) then a horizontal-only leg locked to that same height
    /// removes the diagonal entirely - matches Lucas's own proposed fix
    /// exactly ("gradually incline to the door... then lock the Y axis of
    /// whatever the current foundation is"). Skipped entirely (falls
    /// straight through to a plain StartPhasingToDestination) when the
    /// two Ys already roughly match, since real doors on a single-level
    /// base never need this at all.
    /// </summary>
    private void StartLevelledPhasing(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed || Mathf.Abs(npc.transform.position.y - destination.y) < DoorVerticalAlignmentThreshold)
        {
            StartPhasingToDestination(survivor, destination, onArrived, onFailed);
            return;
        }

        Vector3 verticalAlignPoint = npc.transform.position;
        verticalAlignPoint.y = destination.y;

        StartPhasingToDestination(
            survivor,
            verticalAlignPoint,
            onArrived: () => StartPhasingToDestination(survivor, destination, onArrived, onFailed),
            onFailed: () => StartPhasingToDestination(survivor, destination, onArrived, onFailed));
    }

    // Real safety-valve timeout (2026-09-01, live report: a door-route
    // crossing's phase-to-near-end leg silently hung for 14 real seconds
    // with zero log output - no error, no arrival, no failure - before
    // whatever eventually noticed fell back to the old generic stuck-
    // recovery ladder, producing wild Y-axis teleporting). Prime suspect:
    // `if (npc.IsWounded()) { return; }` below has no time limit at all -
    // if the survivor takes damage mid-phase (a real live risk here, since
    // Lucas tests right next to these bots and this session's own memory
    // notes confirm he deliberately shoots bots to end broken tests), this
    // loop just spins forever doing nothing the whole time it's wounded,
    // with no escape. This applies everywhere StartPhasingToDestination is
    // used, not just door routes, but door routes are the one place that
    // previously had NO bound at all downstream either. 8s is generous for
    // any real phase leg (all observed ones finish in 1-3s) while still
    // being far short of "hangs indefinitely."
    private const float PhaseToDestinationMaxSeconds = 8f;

    private void StartPhasingToDestination(Survivor survivor, Vector3 destination, Action onArrived, Action onFailed)
    {
        Guid characterId = survivor.Character.Id;

        CancelActiveMovement(survivor);

        Timer phaseTimer = null;
        float deadline = UnityEngine.Time.realtimeSinceStartup + PhaseToDestinationMaxSeconds;
        float nextDiagLog = UnityEngine.Time.realtimeSinceStartup;

        phaseTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                phaseTimer.Destroy();
                _activeMovement.Remove(characterId);
                onFailed?.Invoke();
                return;
            }

            // Real concurrency-detection safety net (2026-09-01, live
            // report + log-confirmed: position jumped ~3.8m in a single
            // tick while GhostEnterHomeForDeposit's own step logic never
            // writes anything but small incremental steps - something ELSE
            // was concurrently touching this same survivor's position,
            // most likely its normal background task loop deciding to
            // move it for an unrelated reason mid-ghost-route, since that
            // new isolated function deliberately doesn't register in any
            // of the guards the rest of this project's movement respects).
            // _activeMovement[characterId] always points at whichever
            // timer currently "owns" this survivor's movement - if it no
            // longer points at THIS phaseTimer, someone else has already
            // taken over without our knowledge, so stop fighting for
            // control immediately rather than continuing to write
            // positions on top of whatever that other system is doing.
            if (!_activeMovement.TryGetValue(characterId, out Timer registeredTimer) || !ReferenceEquals(registeredTimer, phaseTimer))
            {
                phaseTimer.Destroy();
                VerbosePuts($"'{survivor.Character.Alias}' phase-to-destination lost ownership of its own movement mid-flight (another system took over) - bailing out.");
                onFailed?.Invoke();
                return;
            }

            // Real diagnostic-only throttled trajectory log (2026-09-01,
            // live report: a phase hangs completely for a full 8s timeout,
            // confirmed NOT wounded - damage-diag shows zero damage during
            // the actual hang window). One line/second is enough to see
            // whether the real position is genuinely stuck at one spot
            // (something resetting the write), drifting the WRONG way, or
            // just never getting network-close enough - can't tell which
            // from the timeout message alone.
            if (UnityEngine.Time.realtimeSinceStartup >= nextDiagLog)
            {
                nextDiagLog = UnityEngine.Time.realtimeSinceStartup + 1f;
                VerbosePuts($"phase-diag: '{survivor.Character.Alias}' pos={npc.transform.position}, destination={destination}, remaining={Vector3.Distance(npc.transform.position, destination):F2}m, wounded={npc.IsWounded()}.");
            }

            if (UnityEngine.Time.realtimeSinceStartup >= deadline)
            {
                phaseTimer.Destroy();
                _activeMovement.Remove(characterId);
                VerbosePuts($"'{survivor.Character.Alias}' phase-to-destination timed out after {PhaseToDestinationMaxSeconds:F0}s (likely wounded/stuck the whole time) - giving up.");
                onFailed?.Invoke();
                return;
            }

            if (npc.IsWounded())
            {
                return;
            }

            Vector3 current = npc.transform.position;
            Vector3 toDestination = destination - current;
            float remaining = toDestination.magnitude;

            if (remaining < WaypointArriveDistance)
            {
                phaseTimer.Destroy();
                _activeMovement.Remove(characterId);
                SnapToGround(npc);

                VerbosePuts($"'{survivor.Character.Alias}' reached its destination (phased through).");
                onArrived?.Invoke();
                return;
            }

            float stepDistance = RunSpeed * WalkTickInterval;
            Vector3 next = remaining <= stepDistance ? destination : current + toDestination.normalized * stepDistance;

            // Field set/order matches the native StartFollowing branch
            // exactly now - see StartGhostRoute's identical fix/doc comment
            // for the full investigation notes (2026-08-16): modelState
            // parity with native has been ruled out as the actual cause,
            // most likely structural (native's real velocity vs a discrete
            // position teleport), not fixed yet.
            npc.modelState.sprinting = true;
            npc.modelState.ducked = false;
            npc.modelState.ducking = 0f;
            npc.modelState.waterLevel = npc.WaterFactor();
            npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
            npc.SendModelState(true);

            FaceDirection(npc, toDestination);

            // No SendNetworkUpdateImmediate here (2026-08-16 fix) - unlike
            // TryEmergencyTeleport, where an instant visible snap is the
            // whole point, this is supposed to read as ordinary walking.
            // Confirmed live: including it made every step look like a
            // sudden pop/disappear-reappear instead of smooth motion -
            // every OTHER hand-built movement step in this project (see
            // ApplyMovementStep) only ever calls plain MovePosition each
            // tick and lets Rust's own normal interpolation carry it
            // smoothly between updates, which is what this needs too.
            npc.transform.position = next;
            npc.MovePosition(next);

            // Real diagnostic-only check (2026-09-01, live report + math
            // proof: logged Y sat frozen at 24.34 for over a minute
            // straight despite writing a clearly lower next.y every single
            // tick - remaining never shrank at all). This confirms WITHIN
            // THE SAME TICK whether npc.MovePosition(next) itself is
            // immediately rejecting/overriding the write we just made
            // (Rust's own collision resolution snapping back up onto a
            // floor slab above), or whether the position holds here and
            // gets reset later, between ticks, by something outside our
            // control entirely.
            if (Vector3.Distance(npc.transform.position, next) > 0.05f)
            {
                VerbosePuts($"phase-diag: '{survivor.Character.Alias}' MovePosition rejected the write THIS TICK - wrote {next}, actual is now {npc.transform.position} (diff {Vector3.Distance(npc.transform.position, next):F2}m).");
            }

            survivor.Position = next;
            survivor.Character.Position = next;
        });

        _activeMovement[characterId] = phaseTimer;
    }

    /// <summary>
    /// Authored ghost routes per monument, keyed by a case-insensitive
    /// substring match against the real monument name (same matching style
    /// as AutonomyExcludedMonumentSubstrings) against a FOLDER under
    /// TraceDirectory rather than individual filenames (2026-08-16, Lucas's
    /// own explicit refinement of the original per-file list design):
    /// "Make a overarching folder called Supermarket_A that the bots refer
    /// to each time it rolls for Supermarket." Every .csv directly inside
    /// that folder counts as one candidate route for this monument - one is
    /// picked at random each time it's triggered (see
    /// TryGetGhostRouteForMonument). Adding a further recorded route (up to
    /// Lucas's planned 5 total for Supermarket_A) is then just dropping a
    /// new .csv into the folder - no code change needed. "_A" distinguishes
    /// this specific supermarket instance's name/layout if a second,
    /// differently-laid-out Abandoned Supermarket ever needs its own "_B"
    /// folder.
    /// </summary>
    private static readonly Dictionary<string, string> MonumentGhostRouteFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["supermarket"] = "Supermarket_A",

        // Scaffolded 2026-08-16 for Lucas's next tracing pass - folder
        // exists under LivingRust/traces/ already but starts empty
        // (TryGetGhostRouteForMonument returns false for an empty folder,
        // so this is a silent no-op until real .csv routes land in it).
        // "lighthouse" is ALSO still in AutonomyExcludedMonumentSubstrings
        // (LivingRust.GearScore.cs) - that's deliberate, not an oversight:
        // it's excluded for a genuine navigation reason (sits on its own
        // island, no land route, and this project has no ocean/water
        // movement yet), not the "safezone, no loot" reason most of that
        // list's other entries have. Remove it from
        // AutonomyExcludedMonumentSubstrings only once this folder actually
        // has traces in it AND ocean/water movement exists to get a
        // survivor there in the first place - a ghost route alone can't
        // solve the "how does it even reach the island" half of this one.
        //
        // Ranch/Barn/Compound/Bandit Camp/Fishing Village were removed from
        // this registry 2026-08-16 (originally scaffolded here by mistake) -
        // see AutonomyExcludedMonumentSubstrings' own doc comment: all five
        // are real safezones with no lootable containers at all, so a ghost
        // route through them would have nothing to actually accomplish.
        // Underwater Lab and Apartments Complex skipped by Lucas's own
        // explicit request (underwater entry, and no stated reason
        // respectively) - not registered here for now either.
        ["lighthouse"] = "Lighthouse_A",

        // Scaffolded 2026-08-16 - Medium/High loot-tier monuments
        // (GetMonumentTier's own tier lists, LivingRust.GearScore.cs), all
        // currently open to autonomy already (none of these are in
        // AutonomyExcludedMonumentSubstrings), so unlike lighthouse above,
        // a populated folder here takes effect immediately - no exclusion-
        // list change needed to activate one. Folders start empty and are
        // silent no-ops until Lucas records real traces into them. Unlike
        // Abandoned Supermarket, none of these have confirmed navigation
        // problems yet - they're candidates by loot value, not by hard
        // evidence of being broken, so it's worth treating each one as
        // "record a trace, see if bots actually needed it" rather than
        // assuming every one of these folders will end up used.
        ["military_tunnel"] = "MilitaryTunnel_A",
        ["launch_site"] = "LaunchSite_A",
        // Traced 2026-08-21 - Nuclear Missile Silo, Tier 3/High monument.
        // Single way in and out, so this one trace covers both looting and
        // the card puzzle (see CardPuzzleRouteFolders' own
        // "nuclear_missile_silo" entry, red keycard only, no fuse). 1
        // path. Heavy elevator use throughout - not yet handled, flagged
        // for a dedicated pass.
        ["nuclear_missile_silo"] = "NuclearMissileSilo_A",
        // Traced 2026-08-20 - Airfield. Initially assumed loot-less (no
        // crates visible at the time), corrected same day once a real loot
        // fill-group refresh spawned 3 crates - all on one side of the
        // monument, so the route is deliberately linear with minimal
        // variance between its 2 paths rather than the usual 5, matching
        // what's actually there. Also has its own card puzzle - see
        // CardPuzzleRouteFolders' "airfield" entry (Cardreader_K,
        // green+blue+2 fuses), LivingRust.CardPuzzles.cs.
        ["airfield"] = "Airfield_A",
        ["military_base"] = "MilitaryBase_A",
        // Traced 2026-08-21 - Trainyard, Tier 2/Medium monument. 4 loot
        // paths. Also has its own card puzzle - see CardPuzzleRouteFolders'
        // "trainyard" entry (Cardreader_M, green+blue+1 fuse),
        // LivingRust.CardPuzzles.cs.
        ["trainyard"] = "TrainYard_A",
        // Traced 2026-08-19 - Powerplant, Tier 2/Medium monument. Reuses
        // the same confirmed-good trace as the puzzle route
        // (CardPuzzleRouteFolders' own "powerplant" entry, Cardreader_H) -
        // same reasoning as Military Tunnel's identical repoint: the
        // recorded path already covers the real loot along the way, so no
        // separate recording is needed. 1 path.
        ["powerplant"] = "PowerPlant_A",
        // Traced 2026-08-20 - Water Treatment Plant, Tier 2/Medium
        // monument. Puzzle needs blue keycard + 1 fuse only, no green -
        // includes a real WheelSwitch-driven roller door and a PressButton
        // door-release (see CardPuzzleRouteFolders' own "water_treatment_plant"
        // entry, Cardreader_L, LivingRust.CardPuzzles.cs). 4 loot paths
        // recorded by Lucas.
        ["water_treatment_plant"] = "WaterTreatmentPlant_A",

        // Traced 2026-08-19 - Arctic Research Base, Tier 2/Medium monument.
        // Puzzle needs blue keycard ONLY, no fuse (CardPuzzleRouteFolders'
        // own "arctic_research_base" entry, Cardreader_J - the first
        // RequiresFuse: false exception). 1 dedicated loot path recorded by
        // Lucas.
        ["arctic_research_base"] = "ArcticResearchBase_A",
        // Traced 2026-08-19 - Sewer Branch, reclassified from Medium to
        // Low/Tier 1 same day (see MediumTierMonumentSubstrings' own doc
        // comment). 1 dedicated loot path plus its puzzle trace
        // (CardPuzzleRouteFolders' "radtown_small" entry, Cardreader_I)
        // reused here too - same reasoning as Military Tunnel/Powerplant's
        // identical repoint. 2 paths total.
        ["radtown_small"] = "SewerBranch_A",

        // Traced 2026-08-17 - Oxum's Gas Station (roadside/gas_station_1.prefab,
        // confirmed via scanmonumentloot log output), 5 paths recorded by Lucas.
        ["gas_station"] = "GasStation_A",

        // Traced 2026-08-17 - Harbor (specifically harbor_2, NOT harbor_1
        // or ferry_terminal_1 - all three are real distinct monuments
        // sharing the same "harbor" folder in the asset path, confirmed via
        // scanmonumentloot log output, so the substring here is
        // deliberately "harbor_2" and not the broader "harbor" to avoid
        // misrouting the other two onto this specific layout's trace). Only
        // 3 paths recorded (not the usual 5) - Lucas's own explicit call:
        // larger monuments have less loot-position randomness and a more
        // fixed traversal path in practice, so fewer variants are needed to
        // cover the real spread.
        ["harbor_2"] = "Harbor2_A",

        // Scaffolded 2026-08-18 - Harbor (specifically harbor_1, a distinct
        // real monument from harbor_2/ferry_terminal_1 - same "harbor"
        // asset-folder caveat as Harbor2_A above). Folder starts empty -
        // silent no-op until Lucas records real traces into it.
        ["harbor_1"] = "Harbor1_A",

        // Scaffolded 2026-08-17 - Mining Outpost, a Low/Tier-1 monument
        // (unlisted in MediumTierMonumentSubstrings/HighTierMonumentSubstrings,
        // LivingRust.GearScore.cs, so it defaults to Low) and already open
        // to autonomy (not in AutonomyExcludedMonumentSubstrings). Folder
        // starts empty - silent no-op (TryGetGhostRouteForMonument returns
        // false for an empty folder) until Lucas records real traces into
        // it, same pattern as every other scaffolded entry above.
        ["mining_outpost"] = "MiningOutpost_A",

        // Scaffolded 2026-08-18 - Satellite Dish, a Low/Tier-1 monument
        // (unlisted in MediumTierMonumentSubstrings/HighTierMonumentSubstrings)
        // and already open to autonomy. Folder starts empty - silent no-op
        // until Lucas records real traces into it.
        ["satellite_dish"] = "SatelliteDish_A",

        // Scaffolded 2026-08-18 - Radtown (real prefab "radtown_1", distinct
        // from "radtown_small"/Sewer Branch, a Medium-tier monument).
        // Folder starts empty - silent no-op until Lucas records traces.
        ["radtown_1"] = "Radtown_A",

        // Traced 2026-08-18 - Sphere Tank ("Dome", real prefab
        // monument/small/sphere_tank.prefab), 2 paths recorded by Lucas -
        // small monument, just one way up and a couple of ways down, no
        // need for more variety than that.
        ["sphere_tank"] = "SphereTank_A",

        // Traced 2026-08-18 - Junkyard (real prefab
        // monument/medium/junkyard_1.prefab - sits in the "medium" asset
        // folder, but Lucas's own explicit call: keep it Tier 1, rolled
        // just as easily as Harbor/Supermarket, NOT added to
        // MediumTierMonumentSubstrings). 5 paths recorded by Lucas. No
        // card-reader puzzle at this monument - deliberately no
        // Cardreader_X folder/registry entry for it.
        ["junkyard"] = "Junkyard_A",

        // Traced 2026-08-19 - Ferry Terminal (specifically ferry_terminal_1,
        // a distinct real monument from harbor_1/harbor_2 - same "harbor"
        // asset-folder caveat as Harbor2_A above), 4 paths recorded by
        // Lucas. Also has its own card puzzle - see CardPuzzleRouteFolders'
        // ferry_terminal entry (Cardreader_G, green tier), LivingRust.CardPuzzles.cs.
        ["ferry_terminal"] = "FerryTerminal_A",

        // Scaffolded 2026-08-19 - Oil Rig (real prefab substrings
        // "oilrig_1" = small, "oilrig_2" = large, confirmed via
        // monument_loot_zones.json's own scanned monument names -
        // deliberately registered by exact substring, not the broader
        // "oilrig" HighTierMonumentSubstrings already uses for gear-score
        // tiering, so small/large get their own separate trace folders).
        // Both parked in AutonomyExcludedMonumentSubstrings for now, Lucas's
        // own explicit call - real prerequisites (boat travel, fending off
        // RHIB scientist NPCs) don't exist yet, and getting there will be
        // its own dedicated test->fix->test->fix pass, deliberately saved
        // for later rather than half-built now. Folders start empty -
        // silent no-op either way until both the prerequisites AND real
        // traces exist.
        ["oilrig_1"] = "OilRigSmall_A",
        ["oilrig_2"] = "OilRigLarge_A",
    };

    /// <summary>
    /// Picks a random authored ghost route .csv for monumentName out of its
    /// registered folder (see MonumentGhostRouteFolders), if one's
    /// registered and the folder actually has at least one .csv in it right
    /// now. Returns false with no output for any monument without a
    /// registered/populated folder - callers fall back to the fully generic
    /// search/movement path exactly as before this existed. traceFilePath
    /// is a full relative path (TraceDirectory/folder/file.csv), ready to
    /// pass straight to TryLoadTraceWaypoints.
    /// </summary>
    private bool TryGetGhostRouteForMonument(string monumentName, out string traceFilePath)
    {
        traceFilePath = null;

        if (string.IsNullOrEmpty(monumentName))
        {
            return false;
        }

        foreach (KeyValuePair<string, string> entry in MonumentGhostRouteFolders)
        {
            if (monumentName.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string folderPath = $"{TraceDirectory}/{entry.Value}";

            if (!Directory.Exists(folderPath))
            {
                return false;
            }

            string[] candidates = Directory.GetFiles(folderPath, "*.csv");

            if (candidates.Length == 0)
            {
                return false;
            }

            string chosen = candidates[UnityEngine.Random.Range(0, candidates.Length)];
            traceFilePath = chosen.Replace('\\', '/');
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the nearest real monument to origin that ACTUALLY has a
    /// registered, populated ghost route - not just the nearest monument of
    /// any type (2026-08-16, Lucas's own explicit fix: "make ghostroute
    /// specifically use the closest monument and then roll the dice to
    /// whatever path it should take at that monument"). Standing near two
    /// different monuments, where the CLOSER one has no registered route
    /// and a slightly farther one does, previously fell all the way back to
    /// the hardcoded DefaultGhostRouteTraceFile instead of finding the
    /// farther-but-actually-usable one - this searches every monument
    /// matching ANY MonumentGhostRouteFolders key, not just whichever one
    /// happens to be nearest overall, and rolls the dice only once it's
    /// found the nearest one that qualifies.
    /// </summary>
    private bool TryGetGhostRouteForNearestMonument(Vector3 origin, float maxDistance, out string traceFilePath, out MonumentInfo monument)
    {
        traceFilePath = null;
        monument = null;

        if (TerrainMeta.Path?.Monuments == null)
        {
            return false;
        }

        float bestDistanceSqr = maxDistance * maxDistance;
        MonumentInfo nearestCandidate = null;

        foreach (MonumentInfo candidate in TerrainMeta.Path.Monuments)
        {
            if (candidate == null
                || !MonumentGhostRouteFolders.Keys.Any(substring => candidate.name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                continue;
            }

            float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

            if (distanceSqr < bestDistanceSqr)
            {
                bestDistanceSqr = distanceSqr;
                nearestCandidate = candidate;
            }
        }

        if (nearestCandidate == null || !TryGetGhostRouteForMonument(nearestCandidate.name, out string rolledFilePath))
        {
            // Either nothing registered is within range, or the nearest
            // registered monument's own folder is currently empty (a
            // monument can be "registered" here before it has any real
            // traces yet - see MonumentGhostRouteFolders' own doc comment).
            return false;
        }

        traceFilePath = rolledFilePath;
        monument = nearestCandidate;
        return true;
    }

    /// <summary>
    /// How close two consecutive rows of a real /lr.debug.traceme CSV need
    /// to be to collapse into a single ghost-route waypoint - a real trace
    /// samples every WalkTickInterval (0.05s) regardless of whether the
    /// admin was moving or standing still looting something, so a genuine
    /// multi-second stop (the crate, the keycard) shows up as dozens of
    /// near-identical rows in a row. Collapsing those down to one waypoint
    /// keeps the route exactly as fine-grained as the real walk during
    /// actual movement, without the phase-through logic wasting ticks
    /// "arriving" at the same point over and over.
    ///
    /// Widened from 0.15m to 0.4m (2026-08-16, real live bug: "bots still
    /// definitely aren't running during the ghostroute... moving at
    /// walking speed") - at real running speed, consecutive raw trace rows
    /// land about RunSpeed * WalkTickInterval = 0.275m apart, which is
    /// ABOVE the old 0.15m threshold, so almost none of them were actually
    /// collapsing during real forward movement - nearly every single raw
    /// sample became its own waypoint. That first fix undershot its own
    /// target though (2026-08-19, symptom recurred: "only sort of jog...
    /// quicker than walk but slower than run") - it aimed to beat the
    /// natural per-tick step (0.275m) but never checked it against
    /// WaypointArriveDistance (0.5m), the actual arrival threshold
    /// StartGhostRoute tests against. 0.4m is BELOW that 0.5m threshold, so
    /// most collapsed waypoints still sat within arrival range of each
    /// other from the very first tick toward them - the phase-through loop
    /// kept counting itself as "arrived" before taking a real step, and an
    /// arrival tick does real work (LootWhateverIsHereNow's physics scans,
    /// tearing down and rebuilding the movement timer) INSTEAD OF stepping
    /// that tick, not in addition to it. Raised to 0.6m - safely above
    /// WaypointArriveDistance itself, not just the per-tick step size - so
    /// a waypoint can no longer be pre-satisfied by arrival distance before
    /// the bot has actually covered real ground toward it.
    /// </summary>
    private const float GhostRouteWaypointCollapseDistance = 0.6f;

    /// <summary>
    /// One collapsed ghost-route stop - the real recorded position, plus
    /// how long the admin actually stood there in the source trace
    /// (DwellSeconds, summed from every consecutive raw row collapsed into
    /// this waypoint - see TryLoadTraceWaypoints). Added 2026-08-16, Lucas's
    /// own explicit request after confirming the route itself was "literally
    /// perfect": the bot "seamlessly walk[ing] and loot[ing] with no issues"
    /// read as inhuman precisely because it skipped every real pause a human
    /// naturally takes while looting/reading a container - replaying those
    /// same pauses (see StartGhostRoute) closes that gap.
    /// </summary>
    private readonly struct GhostRouteWaypoint
    {
        public readonly Vector3 Position;
        public readonly float DwellSeconds;

        public GhostRouteWaypoint(Vector3 position, float dwellSeconds)
        {
            Position = position;
            DwellSeconds = dwellSeconds;
        }

        public GhostRouteWaypoint WithPosition(Vector3 position) => new(position, DwellSeconds);
    }

    /// <summary>
    /// How far from a trace's own first recorded point (or, for the manual
    /// /lr.debug.ghostroute command and TryGetGhostRouteForNearestMonument,
    /// the caller's own position) to search for a real registered monument.
    /// Widened from 60m to 150m (2026-08-16, real live bug: testing at
    /// Military Tunnels - a "large" monument, confirmed via
    /// /lr.debug.scanmonumentloot as
    /// 'assets/bundled/prefabs/autospawn/monument/large/military_tunnel_1.prefab' -
    /// found nothing within 60m and silently fell back to the hardcoded
    /// Supermarket default, even though the registered "military_tunnel"
    /// substring itself was correct). Large monuments can have their
    /// registered transform origin genuinely far from wherever a player or
    /// bot is actually standing inside/around them - matches
    /// CommittedMonumentRangeRadius (LivingRust.MonumentLootZones.cs),
    /// the same wider radius already established elsewhere in this project
    /// specifically for "is this position still meaningfully inside a large
    /// monument."
    /// </summary>
    private const float GhostRouteRecordingMonumentSearchRadius = 150f;

    /// <summary>
    /// Parses a real /lr.debug.traceme CSV (elapsed_s,x,y,z,... - see
    /// RunDebugTraceMe's own writer) into a clean waypoint list - the
    /// actual real path an admin walked, used verbatim rather than a
    /// hand-picked approximation of it. Lucas's own explicit framing
    /// (2026-08-16): "I want the bot to take this EXACT path... to avoid
    /// jittering, oscillating etc" - since this is real recorded ground
    /// truth through a monument's own worst navigation trouble spot, using
    /// it directly sidesteps needing to guess at waypoints by hand.
    ///
    /// Returned waypoints are MONUMENT-RELATIVE, not raw world coordinates,
    /// whenever the trace was recorded near a real monument (recordedAtMonument
    /// comes back non-null) - 2026-08-16, Lucas's own explicit question:
    /// "these traces... are hardcoded to specific coordinates on THIS map,
    /// how do we alleviate this for other maps? and what if there are
    /// multiple of these supermarkets on the map... there are 2?" Same fix
    /// for both: every position gets converted via
    /// recordedAtMonument.transform.InverseTransformPoint into an offset
    /// relative to the monument's OWN transform (position + rotation),
    /// exactly the way MonumentLootZone.LocalOffset already works for
    /// container clusters. A monument's interior layout is a fixed prefab -
    /// only its placement/rotation on the terrain differs per seed/instance -
    /// so a route recorded at one instance re-projects correctly onto ANY
    /// instance of the same monument type (ProjectGhostRouteToMonument),
    /// whether that's a second supermarket on this same map or the only one
    /// on a completely different map. recordedAtMonument comes back null
    /// (waypoints stay raw world coordinates, unchanged from before this
    /// existed) only if the trace genuinely wasn't recorded near any real
    /// monument at all.
    /// </summary>
    /// <summary>
    /// requiredMonumentName (2026-08-18, real live bug fix): when provided,
    /// the anchor search below only considers monuments whose .name EXACTLY
    /// matches it (the same real monument TYPE the trace is being loaded
    /// for, e.g. every instance of "harbor/harbor_2.prefab" specifically) -
    /// not literally whichever MonumentInfo happens to be nearest the
    /// trace's first waypoint. Confirmed live: Harbor2_A was recorded
    /// standing right next to a small power substation that happens to sit
    /// closer to the recording's own first waypoint than Harbor2's own
    /// (much larger) MonumentInfo origin - the old unfiltered
    /// TryGetNearestMonument call anchored the WHOLE route to that
    /// substation instead, so reprojecting onto the real Harbor2 monument
    /// sent survivors ~130m off and ~4m below the real floor (confirmed via
    /// the navmesh's own nearest-point probe reporting real ground level at
    /// that X/Z), which the pathfinder correctly refused to route to -
    /// exhausting every stuck-recovery tier and ending in the last-resort
    /// phase-through-geometry recovery cutting straight down through the
    /// floor toward that bad point (JumpyTorch4/CrazyTorch, live-observed).
    /// null (the debug command's own manual-filename path, which has no
    /// specific monument type to require) falls back to the old unfiltered
    /// "just find something nearby" behaviour.
    /// </summary>
    private bool TryLoadTraceWaypoints(string filePath, out List<GhostRouteWaypoint> waypoints, out MonumentInfo recordedAtMonument, string requiredMonumentName = null)
    {
        waypoints = new List<GhostRouteWaypoint>();
        recordedAtMonument = null;

        if (!File.Exists(filePath))
        {
            return false;
        }

        string[] lines = File.ReadAllLines(filePath);

        for (int i = 1; i < lines.Length; i++)
        {
            string[] columns = lines[i].Split(',');

            if (columns.Length < 4
                || !float.TryParse(columns[1], out float x)
                || !float.TryParse(columns[2], out float y)
                || !float.TryParse(columns[3], out float z))
            {
                continue;
            }

            Vector3 point = new Vector3(x, y, z);

            if (waypoints.Count == 0 || Vector3.Distance(waypoints[^1].Position, point) >= GhostRouteWaypointCollapseDistance)
            {
                waypoints.Add(new GhostRouteWaypoint(point, 0f));
            }
            else
            {
                // Close enough to the last kept waypoint to collapse into it
                // rather than becoming a waypoint of its own - but every raw
                // row still spent here is real, live time the admin stood
                // at this exact spot (the trace samples every WalkTickInterval
                // regardless of movement), so it accumulates as that
                // waypoint's own dwell time instead of just being discarded.
                GhostRouteWaypoint last = waypoints[^1];
                waypoints[^1] = new GhostRouteWaypoint(last.Position, last.DwellSeconds + WalkTickInterval);
            }
        }

        // Y-only smoothing pass, MEDIAN not average (2026-08-16, second
        // iteration - Lucas's own framing: "physically lock the bot's Y
        // axis when it goes through these friction points"). A moving
        // AVERAGE still lets one real outlier sample pull the result
        // partway, which is exactly why the first version still visibly
        // flickered at doorways - a real admin's recorded Y naturally
        // wobbles a few cm from foot placement/camera bob/brushing clutter,
        // invisible on a real player since their own animation absorbs it,
        // but confirmed live to still play back as an up/down flicker after
        // averaging. A median is a much closer match to "lock unless it's
        // real" - it completely ignores brief spikes that don't make up a
        // majority of the window, and only moves once a change is
        // genuinely sustained across most of it (a real staircase climbs
        // steadily across many consecutive waypoints, so it still comes
        // through untouched). X/Z are left exactly as recorded - only
        // vertical noise caused the visible flicker.
        if (waypoints.Count > GhostRouteSmoothingWindow)
        {
            float[] smoothedY = new float[waypoints.Count];
            float[] windowBuffer = new float[GhostRouteSmoothingWindow + 1];

            for (int i = 0; i < waypoints.Count; i++)
            {
                int windowStart = Mathf.Max(0, i - GhostRouteSmoothingWindow / 2);
                int windowEnd = Mathf.Min(waypoints.Count - 1, i + GhostRouteSmoothingWindow / 2);
                int count = 0;

                for (int j = windowStart; j <= windowEnd; j++)
                {
                    windowBuffer[count] = waypoints[j].Position.y;
                    count++;
                }

                Array.Sort(windowBuffer, 0, count);
                smoothedY[i] = windowBuffer[count / 2];
            }

            for (int i = 0; i < waypoints.Count; i++)
            {
                Vector3 position = waypoints[i].Position;
                waypoints[i] = waypoints[i].WithPosition(new Vector3(position.x, smoothedY[i], position.z));
            }
        }

        // Convert from raw world coordinates to monument-relative offsets -
        // see this method's own doc comment for why. Done last, after
        // collapsing/smoothing (which both only care about relative
        // distances between points, so operating in world space first
        // doesn't change their result), against whichever real monument
        // sits nearest the trace's own first waypoint.
        if (waypoints.Count > 0 && TryGetNearestMonumentOfType(waypoints[0].Position, GhostRouteRecordingMonumentSearchRadius, requiredMonumentName, out MonumentInfo nearestToRecording))
        {
            recordedAtMonument = nearestToRecording;

            for (int i = 0; i < waypoints.Count; i++)
            {
                Vector3 localOffset = nearestToRecording.transform.InverseTransformPoint(waypoints[i].Position);
                waypoints[i] = waypoints[i].WithPosition(localOffset);
            }
        }

        return waypoints.Count > 0;
    }

    /// <summary>
    /// Re-projects a monument-relative waypoint list (see
    /// TryLoadTraceWaypoints' own doc comment) onto a SPECIFIC real
    /// monument instance's actual world transform - the other half of the
    /// map/instance-portability fix. Safe to call even on a waypoint list
    /// that was never localized to begin with (recordedAtMonument came back
    /// null) - callers just skip calling this in that case and use the raw
    /// world-space waypoints as-is.
    /// </summary>
    private List<GhostRouteWaypoint> ProjectGhostRouteToMonument(List<GhostRouteWaypoint> localWaypoints, MonumentInfo targetMonument)
    {
        List<GhostRouteWaypoint> projected = new(localWaypoints.Count);

        foreach (GhostRouteWaypoint waypoint in localWaypoints)
        {
            Vector3 worldPosition = targetMonument.transform.TransformPoint(waypoint.Position);
            projected.Add(waypoint.WithPosition(worldPosition));
        }

        return projected;
    }

    /// <summary>
    /// Nearest real monument to origin whose own name EXACTLY matches
    /// monumentName (not a substring match like TryGetNearestMonument/
    /// AutonomyExcludedMonumentSubstrings use) - for re-projecting a ghost
    /// route recorded at one specific monument instance onto whichever
    /// instance of that SAME exact monument type is actually nearest a
    /// given position (e.g. the manual /lr.debug.ghostroute command,
    /// testing near a different supermarket instance than the one the
    /// trace was recorded at).
    /// </summary>
    private bool TryGetNearestMonumentByExactName(Vector3 origin, string monumentName, float maxDistance, out MonumentInfo monument)
    {
        monument = null;

        if (string.IsNullOrEmpty(monumentName) || TerrainMeta.Path?.Monuments == null)
        {
            return false;
        }

        float bestDistanceSqr = maxDistance * maxDistance;

        foreach (MonumentInfo candidate in TerrainMeta.Path.Monuments)
        {
            if (candidate == null || candidate.name != monumentName)
            {
                continue;
            }

            float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

            if (distanceSqr < bestDistanceSqr)
            {
                bestDistanceSqr = distanceSqr;
                monument = candidate;
            }
        }

        return monument != null;
    }

    /// <summary>
    /// Centred moving-average window size for the Y-smoothing pass above -
    /// small enough (1s either side at the real 0.05s trace sample rate)
    /// to not blur a genuine staircase's own real slope into something
    /// mushy, large enough to actually average out the kind of rapid
    /// multi-sample noise confirmed live at the doorway/counter.
    /// </summary>
    private const int GhostRouteSmoothingWindow = 20;

    /// <summary>
    /// Caps how many corpses LootWhateverIsHereNow will empty in one call -
    /// pure safety net against a pathological loop (there's no real
    /// scenario with more than a couple of fresh kills piled at one exact
    /// spot), not an expected real limit.
    /// </summary>
    private const int GhostRouteMaxCorpsesPerStop = 5;

    /// <summary>
    /// Direct "loot whatever's within real reach right now, no travel
    /// needed" pass for a ghost-route stop - deliberately NOT the full
    /// ContinueLootTask search/filter/claim machinery (avoid-zones, safe-
    /// zone ownership, other survivors' claims, card-reader exclusion) -
    /// none of that applies here, the whole point of a ghost route is a
    /// short, pre-validated, already-known-safe real path, not organic
    /// search. Containers here are looted directly (TransferAllItems) with
    /// no destruction/approach-angle step, matching what's already been
    /// confirmed live for this specific room's containers (they loot
    /// cleanly with no "breaking open" needed once actually in range).
    ///
    /// Corpses added 2026-08-16 (Lucas's own explicit request: "if it kills
    /// scientists, it should still loot the bodies, but afterwards, go back
    /// to ghostrouting") - a scientist killed mid-combat right on or near
    /// the route counts as exactly the kind of "whatever's here" this
    /// method already exists for. Uses the same direct bulk transfer as
    /// containers (TransferAllItems per real container on the corpse, not
    /// the paced per-item animation LootCorpseAndContinue uses for organic
    /// search) - same "instant, pre-validated path" reasoning the rest of
    /// this method already follows. Looped rather than a single check since
    /// a corpse becomes exempt from TryFindNearestLootableCorpse's own
    /// filter the moment it's actually empty, so this naturally stops on
    /// its own once nothing's left - GhostRouteMaxCorpsesPerStop is purely
    /// a safety cap, not an expected real limit.
    /// </summary>
    private void LootWhateverIsHereNow(Survivor survivor, BasePlayer npc)
    {
        List<StorageContainer> containers = new();

        _engine.NavigationManager.GetAllLootContainersInRange(
            npc.transform.position,
            LootInteractionRange,
            candidate => candidate.inventory != null && candidate.inventory.itemList.Count > 0,
            containers);

        foreach (StorageContainer container in containers)
        {
            if (container == null || container.IsDestroyed || container.inventory == null)
            {
                continue;
            }

            int moved = TransferAllItems(container.inventory, npc.inventory, npc, out List<string> movedShortnames);

            if (moved > 0)
            {
                VerbosePuts($"ghost-route: '{survivor.Character.Alias}' looted {moved} item stack(s) from '{container.ShortPrefabName}': {string.Join(", ", movedShortnames)}.");
                OnLootObtained(survivor, npc, movedShortnames);
            }

            // Same fix as LootContainerDirectly's own doc comment
            // (2026-08-18) - nothing here ever opens a real loot panel, so
            // LootContainer.PlayerStoppedLooting's native destroyOnEmpty
            // cleanup (what normally makes an emptied crate/barrel
            // disappear) never fires on its own. Confirmed live: a bot
            // emptying a container mid-ghost-route left it sitting there
            // visibly empty until an admin manually opened and closed it
            // themselves.
            if (container is LootContainer lootContainer && lootContainer.destroyOnEmpty && container.inventory.itemList.Count == 0)
            {
                container.Kill();
            }
        }

        for (int i = 0; i < GhostRouteMaxCorpsesPerStop; i++)
        {
            bool foundCorpse = _engine.NavigationManager.TryFindNearestLootableCorpse(
                npc.transform.position,
                LootInteractionRange,
                out LootableCorpse corpse,
                candidate => candidate.containers != null && candidate.containers.Any(c => c.itemList.Count > 0));

            if (!foundCorpse)
            {
                break;
            }

            List<string> movedFromCorpse = new();
            int movedCount = 0;

            foreach (ItemContainer container in corpse.containers)
            {
                if (container == null)
                {
                    continue;
                }

                movedCount += TransferAllItems(container, npc.inventory, npc, out List<string> containerShortnames);
                movedFromCorpse.AddRange(containerShortnames);
            }

            if (movedCount > 0)
            {
                VerbosePuts($"ghost-route: '{survivor.Character.Alias}' looted {movedCount} item stack(s) from a corpse: {string.Join(", ", movedFromCorpse)}.");
                OnLootObtained(survivor, npc, movedFromCorpse);
            }
            else
            {
                // Genuinely nothing transferable (e.g. every item on it is
                // IsNeverLootItem-excluded) - break rather than looping
                // GhostRouteMaxCorpsesPerStop times against the same corpse,
                // since it'll never pass the filter differently next time.
                break;
            }
        }

        if (_engine.NavigationManager.TryFindNearestDroppedItem(npc.transform.position, LootInteractionRange, out DroppedItem droppedItem, candidate => !IsNeverLootItem(candidate.item.info.shortname)))
        {
            string shortname = droppedItem.item.info.shortname;

            if (TryTransferSingleItem(droppedItem.item, npc.inventory, npc))
            {
                droppedItem.RemoveItem();
                droppedItem.Kill();

                VerbosePuts($"ghost-route: '{survivor.Character.Alias}' picked up a dropped '{shortname}'.");
                OnLootObtained(survivor, npc, new List<string> { shortname });
            }
        }
    }

    /// <summary>
    /// Real dwell time recorded at a waypoint (GhostRouteWaypoint.DwellSeconds)
    /// below this is treated as incidental - a real player's momentary slow-
    /// down while turning a corner or eyeing a doorway, not a genuine stop -
    /// and doesn't pause the route at all.
    /// </summary>
    private const float GhostRoutePauseMinSeconds = 0.35f;

    /// <summary>
    /// Caps how long the bot will ever hold at one waypoint, regardless of
    /// how long the admin's own recorded dwell was there (e.g. genuinely
    /// got distracted mid-recording) - a real pause for looting/reading a
    /// container should read as human, not become its own new stuck-bot
    /// complaint.
    /// </summary>
    private const float GhostRoutePauseMaxSeconds = 4f;

    /// <summary>
    /// Safety valve for a wheel-driven ProgressDoor wait (StartGhostRoute's
    /// own arrival branch, 2026-08-20) - NOT the real completion signal
    /// (that's door.openProgress reaching 1, checked every 0.5s regardless
    /// of how long it takes). This only exists so a genuinely stuck wheel
    /// (its own internal RotateProgress timer silently stopped for some
    /// reason without the door ever finishing) falls back to moving the
    /// route on instead of hanging a survivor at that waypoint forever.
    /// </summary>
    private const float WheelWaitMaxSeconds = 60f;

    /// <summary>
    /// How far a ghost-route survivor will detour to loot a real corpse or
    /// dropped-item bag it notices mid-route (2026-08-21, Lucas's own
    /// explicit request - "20 metres") - see StartGhostRouteLootScan's own
    /// corpse/bag search. Deliberately wider than GhostRouteLootDetourRadius
    /// (the SAME scan's existing 10m container/dropped-item radius) and
    /// LootInteractionRange/LootWhateverIsHereNow's much tighter in-place
    /// pickup radius - Lucas's own specific figure for this case.
    /// </summary>
    private const float GhostRouteCorpseInterruptRadius = 20f;

    /// <summary>
    /// How many raycast HasLineOfSight checks a spotted corpse/bag gets
    /// before TryInterruptGhostRouteForNearbyLoot gives up on it - 2026-08-21,
    /// Lucas's own explicit request/spec ("spread the 3 checks out across
    /// 15, 30, and 45 seconds"). Guards against committing a real walk-
    /// detour (with its own NoPath/Stuck recovery ladder) toward a corpse
    /// that's genuinely behind a wall from the survivor's current position -
    /// spread out rather than checked once, since LOS from far away can
    /// change as the survivor's own ghost route naturally carries it
    /// closer/around a corner in the meantime, without ever actually
    /// interrupting the route to find out.
    /// </summary>
    private const int GhostRouteLosMaxChecks = 3;

    /// <summary>
    /// Real spacing between each of GhostRouteLosMaxChecks' checks - first
    /// check lands at +15s after a corpse/bag is first spotted, second at
    /// +30s, third at +45s (Lucas's own explicit numbers).
    /// </summary>
    private const float GhostRouteLosCheckIntervalSeconds = 15f;

    /// <summary>
    /// How long a target that failed all GhostRouteLosMaxChecks stays
    /// excluded from being re-picked as a FRESH detour candidate - purely a
    /// "don't immediately re-run the same doomed 45s check cycle against
    /// the same still-blocked corpse every tick" throttle, NOT a permanent
    /// blacklist. Deliberately short and deliberately separate from
    /// anything LootWhateverIsHereNow reads - giving up on the ACTIVE
    /// detour never stops the survivor from looting that same corpse for
    /// free the ordinary way if its own route later genuinely carries it
    /// within LootWhateverIsHereNow's own much tighter LootInteractionRange
    /// (a real live question Lucas asked: a corpse that fails LOS 3 times
    /// is NOT nulled out forever - it's just skipped for THIS detour
    /// mechanism for a while, walking directly over it still loots it via
    /// the completely independent close-range pass).
    /// </summary>
    private const float GhostRouteLosBlockedCooldownSeconds = 60f;

    /// <summary>
    /// Per-survivor in-progress LOS recheck state for
    /// TryInterruptGhostRouteForNearbyLoot - which target it's watching,
    /// how many checks have already failed, and when the next one is due.
    /// Only one target tracked at a time per survivor (mirrors
    /// _pendingGhostRouteToResume's own one-at-a-time pattern) - a closer/
    /// different candidate replacing the currently-watched one restarts the
    /// count fresh against the new target rather than carrying over stale
    /// failures against an unrelated entity.
    /// </summary>
    private readonly Dictionary<Guid, (NetworkableId TargetId, int ChecksSoFar, float NextCheckTime)> _pendingGhostRouteLosRecheck = new();

    /// <summary>
    /// Targets that failed all GhostRouteLosMaxChecks recently, keyed by
    /// the corpse/bag's own NetworkableId, valued by when the exclusion
    /// expires - see GhostRouteLosBlockedCooldownSeconds' own doc comment
    /// for why this is short and non-permanent. Global (not per-survivor)
    /// since a target genuinely blocked by geometry is blocked the same way
    /// regardless of which survivor's ghost route notices it next.
    /// </summary>
    private readonly Dictionary<NetworkableId, float> _ghostRouteLosBlockedUntil = new();

    /// <summary>
    /// Which waypoint index a survivor was last heading toward on an
    /// in-progress ghost route (2026-08-16, Lucas's own explicit request:
    /// "if the bot gets engaged in combat, after it disengages... it
    /// returns to its last known position of the ghost route and resumes
    /// the ghostrouting until the route is fully finished"). Updated every
    /// time StartGhostRoute begins moving toward a waypoint, so combat
    /// externally destroying the phase timer mid-flight (StartCombat's own
    /// CancelActiveMovement, same as every other movement type) still
    /// leaves this pointing at the right index to resume from - see
    /// TryResumeGhostRoute, called from ResumeOrStartLootTask
    /// (LivingRust.Recycling.cs) ahead of the normal fresh-task fallback.
    /// Removed once a route genuinely finishes (nothing left to resume) or
    /// once a resume attempt actually starts (avoids a stale double-resume
    /// if somehow triggered twice) - also cleared on death/full reset, see
    /// OnPlayerDeath (LivingRust.Hooks.cs).
    /// </summary>
    private readonly Dictionary<Guid, (List<GhostRouteWaypoint> Waypoints, int Index, Action OnComplete)> _pendingGhostRouteToResume = new();

    /// <summary>
    /// How many times TryResumeGhostRoute has resumed after a combat/loot
    /// detour since the last time the survivor was ACTUALLY back moving on
    /// the route - 2026-08-16, Lucas's own explicit request: "it should
    /// only engage in a maximum 8 of these extra steps per disengagement...
    /// this way the bot can't get overloaded by multiple random containers
    /// etc if it gets dragged off course." Past MaxGhostRouteDetourAttempts,
    /// TryResumeGhostRoute stops looting at the fight-end spot and just
    /// forces its way straight back - real self-defense (StartCombat) is
    /// NOT blocked by this, a bot still fights back if attacked again while
    /// forcing its way back, it just stops rewarding itself with more loot
    /// each time. Reset to 0 the instant it's genuinely back on the route
    /// (StartGhostRoute actually resumes) - "if the bot gets instantly
    /// engaged again after it is on the ghostroute, its event counter is
    /// reset," so a fresh disengagement always gets its own clean budget
    /// rather than accumulating across unrelated interruptions.
    /// </summary>
    private readonly Dictionary<Guid, int> _ghostRouteDetourAttempts = new();

    private const int MaxGhostRouteDetourAttempts = 8;

    /// <summary>
    /// Per-survivor periodic scan timer for real nearby loot while a ghost
    /// route is active - 2026-08-16, Lucas's own explicit request: "have
    /// the bot exit the ghost route upon lootable containers or dropped
    /// items within 10 metres, let it escalate through 8 events of
    /// looting then hardcode back to the ghostroute." Started once per
    /// route commit (StartGhostRouteLootScan, called alongside the initial
    /// _pendingGhostRouteToResume registration - see
    /// EscalateSearchToMonumentZone/RunDebugGhostRoute), not re-created per
    /// waypoint. Stopped the moment the route genuinely finishes, fails, or
    /// the survivor dies/resets - see StopGhostRouteLootScan.
    /// </summary>
    private readonly Dictionary<Guid, Timer> _ghostRouteLootScanTimers = new();

    /// <summary>
    /// How far a real container or dropped item can be from the survivor
    /// before the periodic scan below detours to it - Lucas's own explicit
    /// figure ("within 10 metres").
    /// </summary>
    private const float GhostRouteLootDetourRadius = 10f;

    /// <summary>
    /// How often the ghost-route loot scan checks for nearby loot - same
    /// cadence as the established EnRouteLootScanIntervalSeconds pattern
    /// normal long-distance walks already use for the identical "don't
    /// walk straight past real loot" concern.
    /// </summary>
    private const float GhostRouteLootScanIntervalSeconds = 4f;

    /// <summary>
    /// Monuments where a mid-ghost-route recycler detour is deliberately
    /// suppressed entirely (2026-08-21, Lucas's own explicit request,
    /// after a real live death: HazyCamper74 broke off its Nuclear Missile
    /// Silo route to recycle 205m away, and by the time it walked all the
    /// way back the door it had swiped through had already re-closed
    /// (real ~10-15s open window) - it walked straight into the closed
    /// door's own kill-barrier without any way to know it was there,
    /// "died (Blunt)"). Both of these monuments already have real,
    /// confirmed-rough navmesh (the same tunnel this whole session's
    /// splice/telegraph work has been fighting), so a full inventory here
    /// is better left alone entirely until the route itself finishes,
    /// rather than risking a long, real round-trip through geometry that's
    /// already known to be unreliable - LootWhateverIsHereNow still keeps
    /// picking up whatever's within real reach at every stop regardless,
    /// same as always, only the RECYCLER DETOUR specifically is skipped.
    /// </summary>
    private static readonly string[] RecyclerRiskyMonumentSubstrings = { "nuclear_missile_silo", "launch_site" };

    /// <summary>
    /// How close counts as "near" a RecyclerRiskyMonumentSubstrings entry -
    /// generous enough to cover a monument's whole real footprint (both are
    /// large, multi-level structures), not a precise boundary check.
    /// </summary>
    private const float RecyclerRiskyMonumentCheckRadius = 150f;

    private static bool IsNearRecyclerRiskyMonument(Vector3 position)
    {
        if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null)
        {
            return false;
        }

        foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
        {
            if (monument == null)
            {
                continue;
            }

            bool isRisky = RecyclerRiskyMonumentSubstrings.Any(substring => monument.name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0);

            if (isRisky && Vector3.Distance(position, monument.transform.position) <= RecyclerRiskyMonumentCheckRadius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts (if not already running) a periodic scan that detours a
    /// ghost-routing survivor to any real container or dropped item within
    /// GhostRouteLootDetourRadius, shares the exact same
    /// _ghostRouteDetourAttempts/MaxGhostRouteDetourAttempts budget combat
    /// detours already use (looting a barrel on the way and getting
    /// dragged into 5 fights both count against the same "how far off
    /// course has this excursion gone" total - Lucas's own framing), and
    /// stops offering detours once that budget is spent, same as
    /// TryResumeGhostRoute already does for combat. Skips entirely while
    /// combat owns the survivor (that has its own, separate interruption
    /// path already). Requires no destruction to loot (RequiresDestructionToLoot)
    /// since LootWhateverIsHereNow itself has no barrel-breaking step -
    /// only containers/items it can actually empty instantly are worth
    /// detouring for.
    /// </summary>
    private void StartGhostRouteLootScan(Survivor survivor, Action onRouteComplete)
    {
        Guid characterId = survivor.Character.Id;

        if (_ghostRouteLootScanTimers.ContainsKey(characterId))
        {
            return;
        }

        Timer scanTimer = null;

        scanTimer = timer.Every(GhostRouteLootScanIntervalSeconds, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || !_pendingGhostRouteToResume.TryGetValue(characterId, out var resumeState))
            {
                // Route no longer active (finished/abandoned/survivor gone) -
                // nothing left to scan for.
                scanTimer.Destroy();
                _ghostRouteLootScanTimers.Remove(characterId);
                return;
            }

            if (_activeCombat.ContainsKey(characterId))
            {
                // Combat already owns this survivor's movement right now -
                // its own resume path (TryResumeGhostRoute) handles getting
                // back afterward, this scan just stays quiet until then.
                return;
            }

            // Bradley check (2026-08-22, Lucas's explicit request: "Bradley
            // = Death") - takes priority over every other detour below
            // (recycler, corpse/bag, container) since none of that matters
            // if the survivor gets caught in Bradley's engagement range.
            // Reuses StartFleeingFromThreat (the same mechanism unarmed-vs-
            // scientist/animal encounters already use) rather than new
            // movement code - it already runs away, reassesses on a timer,
            // and resumes via ResumeOrStartLootTask -> TryResumeGhostRoute
            // once clear, exactly the "break from the ghost route, seek
            // clear ground, then pick the route back up" behaviour asked
            // for. Deliberately scoped to the ghost-route scan only for now
            // (matches the literal request) - doesn't yet interrupt an
            // already-active scientist fight to flee a Bradley too.
            if (TryFindNearbyBradley(npc.transform.position, out BradleyAPC bradley))
            {
                VerbosePuts($"ghost-route: '{survivor.Character.Alias}' spotted a Bradley APC within {BradleyDangerRadius:F0}m - breaking the route to get clear.");
                StartFleeingFromThreat(survivor, bradley, $"'{survivor.Character.Alias}' spotted a Bradley APC - getting clear.", safeDistanceOverride: BradleyDangerRadius + 20f);
                return;
            }

            if (survivor.Character.CurrentTask != TaskType.Recycling
                && IsContainerFull(npc.inventory.containerMain)
                && !IsNearRecyclerRiskyMonument(npc.transform.position))
            {
                // Same "main inventory alone, not both main AND belt" rule
                // ContinueLootTask's own full-inventory check already uses
                // (see its doc comment) - 2026-08-17, Lucas's own explicit
                // request: "if the bot has a full inventory... it breaks
                // the ghostroute, does the WHOLE Recycler task... then goes
                // back to the ghost route... the same way that Combat
                // interrupts it." TryStartRecyclingTask's own StartWalking
                // call cancels this route's phase timer for us (every
                // movement-start function does), so there's no separate
                // CancelActiveMovement needed here - _pendingGhostRouteToResume
                // stays untouched throughout, exactly like a combat
                // interruption, so TryResumeGhostRoute (via
                // FinishRecyclerDetourOrIdle, LivingRust.Recycling.cs) can
                // pick the route back up once recycling genuinely finishes.
                // If there's no fodder worth recycling or no recycler in
                // range, TryStartRecyclingTask just returns false and the
                // route carries on as if this tick never happened.
                if (TryStartRecyclingTask(survivor))
                {
                    _ghostRouteRecyclerDetour.Add(characterId);
                    VerbosePuts($"ghost-route: '{survivor.Character.Alias}' is full up - breaking the route to recycle, will resume once done.");
                }

                return;
            }

            int attempts = _ghostRouteDetourAttempts.TryGetValue(characterId, out int existingAttempts) ? existingAttempts : 0;

            if (attempts >= MaxGhostRouteDetourAttempts)
            {
                return;
            }

            // 2026-08-18, Lucas's own explicit request: no opportunistic
            // CONTAINER/DROPPED-ITEM detours while a card-puzzle detour is
            // active ("remove the found loot nearby entirely... it only
            // loots once it's within reach") - a puzzle attempt is spending
            // a genuinely limited-use keycard/fuse, not worth risking on a
            // detour to loot that might sit somewhere the navmesh can't
            // actually reach (see IsUnreachableCrateOrBarrel/
            // IsUnreachableDroppedItem below - real live incident: a
            // dropped item near harbor_2's puzzle room repeatedly pulled
            // every test survivor 20-30m off the route into the same
            // broken navmesh pocket). LootWhateverIsHereNow still picks up
            // anything genuinely within reach at every real stop
            // regardless - only the DETOUR is skipped.
            //
            // Corpses/bags are a deliberate EXCEPTION (2026-08-21, Lucas's
            // own explicit follow-up) - TryHandleGhostRouteCorpseOrBagDetour
            // has its own real HasLineOfSight retry/cooldown safety net
            // this original container rule never had, making the same
            // "dragged into an unreachable navmesh pocket" risk far less
            // likely, so a corpse/bag mid-puzzle is allowed to interrupt
            // same as it would on an ordinary loot-run ghost route.
            if (_activeCardPuzzleSurvivors.Contains(characterId))
            {
                TryHandleGhostRouteCorpseOrBagDetour(survivor, npc, resumeState, attempts);
                return;
            }

            bool foundContainer = _engine.NavigationManager.TryFindNearestLootContainer(
                npc.transform.position,
                GhostRouteLootDetourRadius,
                out StorageContainer container,
                candidate => candidate.inventory != null && candidate.inventory.itemList.Count > 0
                    && !RequiresDestructionToLoot(candidate)
                    && !IsUnreachableCrateOrBarrel(npc.transform.position, candidate));

            DroppedItem droppedItem = null;
            bool foundDropped = !foundContainer && _engine.NavigationManager.TryFindNearestDroppedItem(
                npc.transform.position,
                GhostRouteLootDetourRadius,
                out droppedItem,
                candidate => !IsNeverLootItem(candidate.item.info.shortname)
                    && !IsUnreachableDroppedItem(npc.transform.position, candidate.transform.position));

            if (!foundContainer && !foundDropped)
            {
                TryHandleGhostRouteCorpseOrBagDetour(survivor, npc, resumeState, attempts);
                return;
            }

            Vector3 lootPosition = foundContainer ? container.transform.position : droppedItem.transform.position;
            int thisAttempt = attempts + 1;
            _ghostRouteDetourAttempts[characterId] = thisAttempt;

            VerbosePuts($"ghost-route: '{survivor.Character.Alias}' spotted real loot {Vector3.Distance(npc.transform.position, lootPosition):F0}m away - detouring ({thisAttempt}/{MaxGhostRouteDetourAttempts}).");

            StartLongDistanceWalk(
                survivor,
                lootPosition,
                "ghost route side-loot",
                onArrived: () =>
                {
                    BasePlayer arrivedNpc = survivor.Player;

                    if (arrivedNpc != null && !arrivedNpc.IsDestroyed)
                    {
                        LootWhateverIsHereNow(survivor, arrivedNpc);
                    }

                    StartGhostRoute(survivor, resumeState.Waypoints, resumeState.Index, resumeState.OnComplete);
                },
                onFailed: () => StartGhostRoute(survivor, resumeState.Waypoints, resumeState.Index, resumeState.OnComplete));
        });

        _ghostRouteLootScanTimers[characterId] = scanTimer;
    }

    /// <summary>
    /// Corpse/dropped-item-bag sibling of StartGhostRouteLootScan's own
    /// container/dropped-item detour above - 2026-08-21, Lucas's own
    /// explicit request ("a bot will interrupt a ghost route IF a corpse
    /// lootable container (bag or body) is within 20 metres"). Called from
    /// that same scan tick in two cases: immediately, while a card-puzzle
    /// detour is active (the ONLY opportunistic detour still allowed
    /// during a puzzle attempt - see its own call site's doc comment for
    /// why corpses/bags are exempt from the container/dropped-item
    /// puzzle-safety rule), or otherwise only once NEITHER a container nor
    /// a dropped item was found this cycle - a real container within
    /// GhostRouteLootDetourRadius still wins first outside a puzzle, same
    /// detour budget either way.
    ///
    /// Reuses the same TryFindNearestLootableCorpse/
    /// TryFindNearestDroppedItemContainer searches (and the same
    /// PlayerCorpse safe-zone/ownership exclusion) the ordinary loot-for-
    /// resources task already relies on, and the same shared _lootClaims
    /// registry so two survivors can't converge on the same corpse. Claims
    /// directly (bypassing ClaimLootTarget's LootTaskState parameter, which
    /// a ghost route doesn't have).
    ///
    /// Real HasLineOfSight raycast gate (Lucas's own explicit follow-up,
    /// after asking what happens if a spotted corpse turns out to be behind
    /// a wall) - a freshly-spotted target gets up to GhostRouteLosMaxChecks
    /// (3) checks, spaced ~GhostRouteLosCheckIntervalSeconds (15s) apart via
    /// _pendingGhostRouteLosRecheck, landing at roughly +15s/+30s/+45s after
    /// first being noticed (rounded to this scan's own 4s cadence). Only a
    /// target that PASSES a check actually triggers the real walk-detour
    /// below - failing all 3 adds it to _ghostRouteLosBlockedUntil for
    /// GhostRouteLosBlockedCooldownSeconds (60s) and gives up on it for
    /// THIS mechanism only. That cooldown is deliberately short and
    /// deliberately doesn't touch anything LootWhateverIsHereNow reads - a
    /// corpse that fails all 3 LOS checks is NOT permanently blacklisted:
    /// if the survivor's own route later genuinely carries it within
    /// LootWhateverIsHereNow's own much tighter LootInteractionRange (e.g.
    /// walking directly past/over it), that completely independent
    /// close-range pass still loots it for free, same as it always would.
    /// </summary>
    private void TryHandleGhostRouteCorpseOrBagDetour(Survivor survivor, BasePlayer npc, (List<GhostRouteWaypoint> Waypoints, int Index, Action OnComplete) resumeState, int attempts)
    {
        Guid characterId = survivor.Character.Id;
        float now = UnityEngine.Time.realtimeSinceStartup;

        // Real gap fix (2026-09-01, live report + screenshots: 20-40 bots
        // still piling up at Ranch even after the monument-exclusion fix
        // landed) - a bot trace confirmed one survivor genuinely stuck
        // oscillating within a ~6m box at Ranch's own coordinates well
        // AFTER that fix's reload, meaning the pileup wasn't (only) coming
        // from the main scan below (TryFindNearestLootableCorpse in
        // ContinueLootTask, already checks !IsInMonumentAvoidZone) - this
        // SEPARATE ghost-route corpse/bag interrupt had no monument check
        // at all. Once corpses exist at an excluded monument for any
        // reason (players/bots dying there), every OTHER bot merely
        // ghost-routing PAST it within GhostRouteCorpseInterruptRadius
        // would detour in via this completely different, unfiltered path,
        // regardless of whether it would ever have chosen to travel there
        // deliberately - a real self-sustaining snowball this fixes.
        bool foundCorpse = _engine.NavigationManager.TryFindNearestLootableCorpse(
            npc.transform.position,
            GhostRouteCorpseInterruptRadius,
            out LootableCorpse corpse,
            candidate => !IsLootTargetClaimed(candidate.net.ID)
                && (!_ghostRouteLosBlockedUntil.TryGetValue(candidate.net.ID, out float blockedUntil) || blockedUntil <= now)
                && candidate.containers != null
                && candidate.containers.Any(c => c != null && c.itemList.Count > 0)
                && !IsInMonumentAvoidZone(candidate.transform.position)
                && (candidate is not PlayerCorpse || candidate.playerSteamID == survivor.Character.BotId || !candidate.InSafeZone()));

        bool foundBag = _engine.NavigationManager.TryFindNearestDroppedItemContainer(
            npc.transform.position,
            GhostRouteCorpseInterruptRadius,
            out DroppedItemContainer bag,
            candidate => !IsLootTargetClaimed(candidate.net.ID)
                && (!_ghostRouteLosBlockedUntil.TryGetValue(candidate.net.ID, out float blockedUntil) || blockedUntil <= now)
                && candidate.inventory != null
                && candidate.inventory.itemList.Count > 0
                && !IsInMonumentAvoidZone(candidate.transform.position));

        if (!foundCorpse && !foundBag)
        {
            _pendingGhostRouteLosRecheck.Remove(characterId);
            return;
        }

        float corpseDistance = foundCorpse ? Vector3.Distance(npc.transform.position, corpse.transform.position) : float.MaxValue;
        float bagDistance = foundBag ? Vector3.Distance(npc.transform.position, bag.transform.position) : float.MaxValue;
        bool useCorpse = foundCorpse && corpseDistance <= bagDistance;

        NetworkableId targetId = useCorpse ? corpse.net.ID : bag.net.ID;
        BaseEntity targetEntity = useCorpse ? corpse : bag;

        if (!_pendingGhostRouteLosRecheck.TryGetValue(characterId, out var pending) || pending.TargetId != targetId)
        {
            // Freshly spotted (or the previously-watched target is gone/
            // replaced by a closer one) - first check scheduled for
            // +GhostRouteLosCheckIntervalSeconds from now, not immediately
            // (Lucas's own explicit spec: "15, 30, and 45 seconds").
            _pendingGhostRouteLosRecheck[characterId] = (targetId, 0, now + GhostRouteLosCheckIntervalSeconds);
            return;
        }

        if (now < pending.NextCheckTime)
        {
            // Still waiting for the next scheduled check.
            return;
        }

        if (!HasLineOfSight(npc, targetEntity))
        {
            int failedChecks = pending.ChecksSoFar + 1;

            if (failedChecks >= GhostRouteLosMaxChecks)
            {
                VerbosePuts($"ghost-route: '{survivor.Character.Alias}' gave up on a {(useCorpse ? "corpse" : "bag")} after {failedChecks} failed line-of-sight checks - likely behind a wall, skipping for now.");
                _pendingGhostRouteLosRecheck.Remove(characterId);
                _ghostRouteLosBlockedUntil[targetId] = now + GhostRouteLosBlockedCooldownSeconds;
            }
            else
            {
                _pendingGhostRouteLosRecheck[characterId] = (targetId, failedChecks, now + GhostRouteLosCheckIntervalSeconds);
            }

            return;
        }

        // Line of sight confirmed - commit to the real detour, same pattern
        // (attempt budget, StartLongDistanceWalk, LootWhateverIsHereNow on
        // arrival) as the container/dropped-item branch above.
        _pendingGhostRouteLosRecheck.Remove(characterId);
        _lootClaims[targetId] = now + LootClaimTtlSeconds;

        int thisAttempt = attempts + 1;
        _ghostRouteDetourAttempts[characterId] = thisAttempt;

        VerbosePuts($"ghost-route: '{survivor.Character.Alias}' spotted a {(useCorpse ? "corpse" : "bag")} {(useCorpse ? corpseDistance : bagDistance):F0}m away - detouring ({thisAttempt}/{MaxGhostRouteDetourAttempts}).");

        StartLongDistanceWalk(
            survivor,
            GetApproachPoint(targetEntity, npc),
            "ghost route corpse/bag side-loot",
            onArrived: () =>
            {
                BasePlayer arrivedNpc = survivor.Player;

                if (arrivedNpc != null && !arrivedNpc.IsDestroyed)
                {
                    LootWhateverIsHereNow(survivor, arrivedNpc);
                }

                _lootClaims.Remove(targetId);
                StartGhostRoute(survivor, resumeState.Waypoints, resumeState.Index, resumeState.OnComplete);
            },
            onFailed: () =>
            {
                _lootClaims.Remove(targetId);
                StartGhostRoute(survivor, resumeState.Waypoints, resumeState.Index, resumeState.OnComplete);
            });
    }

    /// <summary>
    /// Stops this survivor's ghost-route loot scan, if one's running - see
    /// StartGhostRouteLootScan's own doc comment. Called once the route
    /// genuinely finishes/fails and from the same death/full-reset cleanup
    /// sites _pendingGhostRouteToResume already uses.
    /// </summary>
    private void StopGhostRouteLootScan(Guid characterId)
    {
        if (_ghostRouteLootScanTimers.TryGetValue(characterId, out Timer scanTimer))
        {
            scanTimer.Destroy();
            _ghostRouteLootScanTimers.Remove(characterId);
        }
    }

    /// <summary>
    /// Walks a survivor along a real recorded route (see
    /// TryLoadTraceWaypoints) using the same collision-bypassing
    /// phase-through movement as StartPhasingToDestination, looting
    /// whatever's within reach and pausing for a moment (mirroring the
    /// admin's own real recorded dwell time - see GhostRouteWaypoint,
    /// 2026-08-16 Lucas's own explicit request: "the bot seems to
    /// seamlessly walk and loot with no issues (not really humane)") at
    /// every waypoint along the way. Once the whole route is complete, hands
    /// control straight back to the normal autonomous loot loop
    /// (onComplete) - this is a scripted DETOUR through one specific
    /// known-hard stretch, not a replacement for normal behaviour either
    /// side of it.
    /// </summary>
    // How many stuck ticks (no real movement despite MovePosition being
    // called) before giving up on a waypoint and skipping to the next one -
    // see StartCollisionWalkRoute's own doc comment. 20 ticks * WalkTick
    // Interval (0.05s) = 1s, matching this project's general "never hang
    // forever" precedent without needing a long wait to notice a genuine
    // block.
    private const int CollisionWalkRouteStuckTicksBeforeSkip = 20;

    /// <summary>
    /// Real collision-respecting waypoint walk (2026-09-01, Lucas's own
    /// explicit choice after live testing: StartGhostRoute's phase-based
    /// movement made a door-route crossing look like an instant teleport
    /// up close, even once the door was confirmed genuinely toggling open
    /// server-side - phasing (zero collision, a direct transform.position
    /// write) never actually depends on collision or the door's own open
    /// state at all, so the door opening was purely cosmetic dressing on a
    /// glide that looked identical whether it opened or not). This walks
    /// the SAME recorded waypoints - a real, once-human-walked, provably
    /// collision-valid path - but via real MovePosition-based collision
    /// instead of a direct position write, so the survivor genuinely walks
    /// through the now-open door rather than sliding through it regardless.
    /// Deliberately does NOT use CalculatePath/native pathing at all - the
    /// whole point of a recorded route is we already HAVE the path, we're
    /// not asking pathfinding to find one (which is exactly what failed for
    /// these off-static-navmesh interior points in the first place, see
    /// ExitHomeIfInside's own doc comment). If real collision blocks
    /// forward progress for a full second straight (the door didn't
    /// actually finish opening in time, a stray prop, whatever), skips
    /// ahead to the next waypoint rather than hanging forever.
    /// </summary>
    private void StartCollisionWalkRoute(Survivor survivor, List<Vector3> waypoints, int index, Action onComplete)
    {
        Guid characterId = survivor.Character.Id;

        if (index >= waypoints.Count)
        {
            onComplete?.Invoke();
            return;
        }

        CancelActiveMovement(survivor);

        Vector3 target = waypoints[index];
        Timer walkTimer = null;
        int stuckTicks = 0;

        walkTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                walkTimer.Destroy();
                _activeMovement.Remove(characterId);
                onComplete?.Invoke();
                return;
            }

            if (npc.IsWounded())
            {
                return;
            }

            // Real, proven door-open mechanism (2026-09-01) - the same one
            // ApplyMovementStep already calls every tick for ordinary
            // hand-built walking, NPCDoorTriggerBox.TryOpenDoorFor via the
            // survivor's own CURRENT position, not Door.SetOpen against a
            // fixed pre-recorded anchor (which is what this crossing used
            // to do, and could easily open a door that's no longer - or
            // never truly was - the one actually in the survivor's real
            // path, matching the live report: "doors are opening/closing,
            // the bot isn't physically and visually next to the door").
            TryOpenNearbyDoors(npc);

            Vector3 current = npc.transform.position;
            Vector3 toTarget = target - current;
            float remaining = toTarget.magnitude;

            if (remaining < WaypointArriveDistance)
            {
                walkTimer.Destroy();
                _activeMovement.Remove(characterId);
                StartCollisionWalkRoute(survivor, waypoints, index + 1, onComplete);
                return;
            }

            float stepDistance = RunSpeed * WalkTickInterval;
            Vector3 next = remaining <= stepDistance ? target : current + toTarget.normalized * stepDistance;

            npc.modelState.sprinting = true;
            npc.modelState.ducked = false;
            npc.modelState.ducking = 0f;
            npc.modelState.waterLevel = npc.WaterFactor();
            npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
            npc.SendModelState(true);

            FaceDirection(npc, toTarget);

            // Real collision - MovePosition only, no direct transform.
            // position write (that write is what makes StartGhostRoute a
            // true collision bypass; omitting it here is the entire point
            // of this function).
            npc.MovePosition(next);
            survivor.Position = npc.transform.position;
            survivor.Character.Position = npc.transform.position;

            if (Vector3.Distance(npc.transform.position, current) < 0.01f)
            {
                stuckTicks++;
            }
            else
            {
                stuckTicks = 0;
            }

            if (stuckTicks >= CollisionWalkRouteStuckTicksBeforeSkip)
            {
                walkTimer.Destroy();
                _activeMovement.Remove(characterId);
                StartCollisionWalkRoute(survivor, waypoints, index + 1, onComplete);
            }
        });

        _activeMovement[characterId] = walkTimer;
    }

    private void StartGhostRoute(Survivor survivor, List<GhostRouteWaypoint> waypoints, int index, Action onComplete)
    {
        Guid characterId = survivor.Character.Id;

        // Real combat takes over entirely, exactly like ContinueLootTask's
        // own combat gate (see its doc comment) - StartCombat's
        // CancelActiveMovement already destroys this route's phase timer
        // the instant a fight starts (it shares _activeMovement with every
        // other movement type), so mid-step this check is mostly a no-op
        // safety net. It matters most for the gap BETWEEN waypoints (the
        // real recorded-pause timer.Once below) - nothing is registered in
        // _activeMovement during that window, so combat starting there
        // can't cancel anything, and without this check the pause's own
        // callback would blindly resume phasing on top of whatever
        // StartCombat/StartFollowing is now doing with this same survivor.
        if (_activeCombat.ContainsKey(characterId))
        {
            return;
        }

        if (index >= waypoints.Count)
        {
            _pendingGhostRouteToResume.Remove(characterId);
            StopGhostRouteLootScan(characterId);
            VerbosePuts($"'{survivor.Character.Alias}' finished the ghost route - resuming normal behaviour.");
            onComplete?.Invoke();
            return;
        }

        // Recorded every time movement toward a waypoint actually begins,
        // not just once at the start of the whole route - see
        // _pendingGhostRouteToResume's own doc comment. Combat killing this
        // method's phase timer from outside (StartCombat's
        // CancelActiveMovement) leaves this pointing at whichever waypoint
        // was actually in progress when that happened.
        _pendingGhostRouteToResume[characterId] = (waypoints, index, onComplete);

        CancelActiveMovement(survivor);

        Vector3 target = waypoints[index].Position;
        Timer phaseTimer = null;

        phaseTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed)
            {
                phaseTimer.Destroy();
                _activeMovement.Remove(characterId);
                return;
            }

            if (npc.IsWounded())
            {
                return;
            }

            Vector3 current = npc.transform.position;
            Vector3 toTarget = target - current;
            float remaining = toTarget.magnitude;

            // Arrival is now checked AFTER stepping (below), using the
            // POST-step position, not before - 2026-08-19, real live bug:
            // "bot is still stuck in a weird jog pace... I can physically
            // outrun the bot." Widening GhostRouteWaypointCollapseDistance
            // to 0.6m (same day, earlier fix this session) turned out not
            // to be enough on its own: the arrival check used to run FIRST,
            // against the position from BEFORE this tick's step, so any
            // tick where the PREVIOUS tick's movement had already closed
            // the gap to under WaypointArriveDistance (0.5m) did ZERO
            // movement of its own - it just ran arrival housekeeping
            // (LootWhateverIsHereNow, TryHandleCardPuzzleInteractions,
            // pause/next-waypoint scheduling) and returned. Since collapsed
            // waypoints sit only just above the 0.6m collapse threshold,
            // ONE real movement step (RunSpeed * WalkTickInterval ~=
            // 0.275m) was consistently enough to cross under the 0.5m
            // threshold, meaning every waypoint cost exactly 2 ticks - 1
            // that moved, 1 that didn't - for a hard 50% speed tax
            // regardless of how far collapse distance gets raised. Moving
            // the check to after the step means every single tick now
            // contributes real movement; arrival is just "did that step
            // land us close enough," never "skip stepping because we're
            // already close."
            float stepDistance = RunSpeed * WalkTickInterval;
            Vector3 next = remaining <= stepDistance ? target : current + toTarget.normalized * stepDistance;

            // Field set matches the native StartFollowing branch exactly
            // (2026-08-16) - combat movement defaults to NATIVE
            // NavMeshAgent-driven movement, not the hand-built
            // ApplyMovementStep fallback an earlier fix here was compared
            // against; native's own order is SendModelState BEFORE
            // FaceDirection, restored here after briefly trying the
            // opposite order. sprinting/ducked/ducking/waterLevel/onground
            // now match native's own field set exactly either way - see
            // this method's own investigation notes for why modelState
            // parity alone has NOT fixed this (ghost route and native are
            // now essentially identical here), meaning the real cause is
            // most likely structural: native's real Unity NavMeshAgent
            // produces genuine continuous velocity every frame, while this
            // (and every hand-built movement type) is a discrete position
            // teleport (MovePosition) with no real velocity component at
            // all - plausibly what Rust's client animator actually keys
            // off, not the modelState flags alone. Not yet confirmed or
            // fixed - needs real decompile access to pin down what field
            // (if any) carries that signal, which isn't available this
            // session.
            npc.modelState.sprinting = true;
            npc.modelState.ducked = false;
            npc.modelState.ducking = 0f;
            npc.modelState.waterLevel = npc.WaterFactor();
            npc.modelState.onground = npc.modelState.waterLevel < 0.65f;
            npc.SendModelState(true);

            FaceDirection(npc, toTarget);

            // No SendNetworkUpdateImmediate - see StartPhasingToDestination's
            // identical fix/doc comment for why plain MovePosition alone is
            // what reads as smooth ordinary walking to observers.
            npc.transform.position = next;
            npc.MovePosition(next);
            survivor.Position = next;
            survivor.Character.Position = next;

            if (Vector3.Distance(next, target) >= WaypointArriveDistance)
            {
                return;
            }

            phaseTimer.Destroy();
            _activeMovement.Remove(characterId);

            // Deliberately NOT SnapToGround here (2026-08-16, second
            // flicker-fix iteration) - it re-queries the same real
            // ground-height probe every single waypoint arrival (dense
            // near doorways, since that's where the recorded trace has
            // many close samples) and overrides Y with whatever THAT
            // computes, independent of the recorded/smoothed trace Y -
            // confirmed live as the actual flicker source, not sample
            // noise (Y-smoothing alone didn't fix it, because this was
            // never about the trace data at all). The whole point of a
            // ghost route is that the real recorded Y IS the ground
            // truth here - it shouldn't get second-guessed by the same
            // probe system this whole session has been fighting.
            LootWhateverIsHereNow(survivor, npc);

            // Fuse/switch/reader interactions - no-op for every normal
            // ghost route (gated entirely behind _activeCardPuzzleSurvivors,
            // see its own doc comment for why - those consume real fuse/
            // card durability, so only a committed puzzle attempt should
            // ever touch them).
            TryHandleCardPuzzleInteractions(survivor, npc);

            // Wheel/button/elevator interactions - called UNCONDITIONALLY
            // on every ghost route, puzzle or plain loot, since none of
            // them cost an item (2026-08-20, Lucas's own explicit spec:
            // Water Treatment Plant's wheel-driven door needs no keycard/
            // fuse at all, and the buttons that let a bot escape a
            // monument if the doors auto-close are "concurrent throughout
            // almost every monument with a card reader," not puzzle-
            // specific; elevators added 2026-08-21 for Nuclear Missile
            // Silo, same "costs nothing" reasoning). Returns true if
            // anything handled here is still actively pending (an
            // in-progress wheel-turn whose door hasn't reached full
            // openProgress yet, or an elevator still IsBusy() moving) -
            // NOT the specific entity itself, since WheelSwitch.
            // rotateProgress turned out to be an uncapped local animation
            // value with no real ceiling (see
            // TryHandleFreeMonumentInteractions' own doc comment for the
            // live bug this fixed, 2026-08-20).
            bool hasPendingInteraction = TryHandleFreeMonumentInteractions(survivor, npc);

            if (hasPendingInteraction)
            {
                // Real live bug (2026-08-20): the normal recorded-dwell
                // pause below is clamped to GhostRoutePauseMaxSeconds (a
                // flat 4s), but a real wheel hold can genuinely take
                // longer (Lucas's own confirmed 8.86s for one specific
                // wheel) - and a bigger hardcoded cap would just be wrong
                // in the other direction for a faster-tuned wheel
                // elsewhere. Poll the door's own real openProgress instead
                // of trusting a duration at all - advance the instant it's
                // actually open, however long that genuinely takes.
                // WheelWaitMaxSeconds is a safety valve, not the real
                // completion signal - guards against a genuinely stuck
                // wheel hanging the route forever rather than falling back
                // to moving on. Registered in _activeMovement so combat
                // starting mid-turn correctly cancels this the same way it
                // cancels the phase timer above.
                //
                // Re-calls TryHandleFreeMonumentInteractions every poll tick
                // and uses ITS FRESH return value, not the stale
                // hasPendingInteraction captured when the wait started -
                // 2026-08-20, real live bug (two parts). First: a bot
                // arriving 2-3m from the wheel started turning, immediately
                // self-cancelled (real 2m distance check), and this loop
                // just watched the same dead, never-retried timer for the
                // full 60s before giving up - re-invoking each tick means
                // WheelSwitchMaxRotateDistance2D's own pre-check
                // (TryStartWheelTurn) gets a fresh chance every 0.5s.
                // Second, worse bug: even after TryStartWheelTurn/
                // _handledWheelsThisPuzzle correctly finished the real 8s
                // hold and gave up for good, this loop kept checking the
                // ORIGINAL captured door's openProgress - which
                // live-confirmed never actually reaches 1 (stayed flat at
                // 0.00 across every real 8s hold, even ones that visibly
                // lifted the door) - so it just sat frozen until the full
                // 60s WheelWaitMaxSeconds safety valve, not the real "the
                // wheel gave up, move on" signal at all. Re-invoking and
                // reassigning currentlyPending each tick means a false
                // return (nothing left pending - handled, destroyed, or
                // never actually started) is treated as done immediately.
                float wheelWaitDeadline = UnityEngine.Time.realtimeSinceStartup + WheelWaitMaxSeconds;
                Timer wheelWaitTimer = null;
                bool currentlyPending = hasPendingInteraction;

                wheelWaitTimer = timer.Every(0.5f, () =>
                {
                    if (survivor.Player == null || survivor.Player.IsDestroyed)
                    {
                        wheelWaitTimer.Destroy();
                        _activeMovement.Remove(characterId);
                        return;
                    }

                    currentlyPending = TryHandleFreeMonumentInteractions(survivor, survivor.Player);

                    bool doneOrStuck = !currentlyPending
                        || UnityEngine.Time.realtimeSinceStartup >= wheelWaitDeadline;

                    if (doneOrStuck)
                    {
                        wheelWaitTimer.Destroy();
                        _activeMovement.Remove(characterId);
                        StartGhostRoute(survivor, waypoints, index + 1, onComplete);
                    }
                });

                _activeMovement[characterId] = wheelWaitTimer;
                return;
            }

            float pauseSeconds = Mathf.Clamp(waypoints[index].DwellSeconds, 0f, GhostRoutePauseMaxSeconds);

            if (pauseSeconds >= GhostRoutePauseMinSeconds)
            {
                VerbosePuts($"ghost-route: '{survivor.Character.Alias}' pausing {pauseSeconds:F1}s at waypoint {index} - matching the real recorded stop here.");

                timer.Once(pauseSeconds, () =>
                {
                    if (survivor.Player == null || survivor.Player.IsDestroyed)
                    {
                        return;
                    }

                    StartGhostRoute(survivor, waypoints, index + 1, onComplete);
                });
            }
            else
            {
                StartGhostRoute(survivor, waypoints, index + 1, onComplete);
            }
        });

        _activeMovement[characterId] = phaseTimer;
    }

    /// <summary>
    /// Resumes an in-progress ghost route after combat interrupted it, if
    /// this survivor has one recorded (see _pendingGhostRouteToResume's own
    /// doc comment) - 2026-08-16, Lucas's own explicit request. Called from
    /// ResumeOrStartLootTask (LivingRust.Recycling.cs) ahead of its normal
    /// fresh-task fallback, same priority pattern that method already gives
    /// a pending recycler.
    ///
    /// Loots whatever's within reach right where combat just ended first
    /// (LootWhateverIsHereNow, which now includes corpses - "if it kills
    /// scientists, it should still loot the bodies, but afterwards, go back
    /// to ghostrouting") - the thing it just killed is almost always right
    /// there. Then walks back (a real, collision-respecting walk via
    /// StartLongDistanceWalk, same as the original approach to the route -
    /// combat can genuinely drag a survivor off the recorded line) to
    /// exactly the waypoint it was heading toward when interrupted, and
    /// resumes phasing through the route from there to completion. Returns
    /// true the instant it commits to a resume attempt - false, doing
    /// nothing, if there's nothing to resume, letting the caller fall
    /// through to its own normal fresh-task start.
    ///
    /// Deliberately does NOT remove the pending entry here (2026-08-16,
    /// real live bug: multiple scientists near the route - the SECOND
    /// fight interrupted this method's own walk-back before it ever
    /// reached StartGhostRoute again, and since the entry had already been
    /// deleted the instant this method committed to the FIRST resume
    /// attempt, the state was gone for good - the survivor wandered off to
    /// an unrelated fresh task instead of ever finishing the route).
    /// ResumeOrStartLootTask only ever calls this once per genuine
    /// disengage event, so there's no real double-fire risk to guard
    /// against - StartGhostRoute itself keeps this entry current (or
    /// clears it on genuine completion/death), so leaving it alone here
    /// means a resume attempt that gets interrupted YET AGAIN just tries
    /// again from the same still-recorded waypoint next time, instead of
    /// silently losing the route.
    /// </summary>
    private bool TryResumeGhostRoute(Survivor survivor)
    {
        Guid characterId = survivor.Character.Id;

        if (!_pendingGhostRouteToResume.TryGetValue(characterId, out var resumeState))
        {
            return false;
        }

        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            return false;
        }

        // See _ghostRouteDetourAttempts' own doc comment - past the cap,
        // this stops looting at the fight-end spot and just forces its way
        // straight back instead, no more side-loot rewards for getting
        // dragged further off course.
        int attempts = _ghostRouteDetourAttempts.TryGetValue(characterId, out int previousAttempts) ? previousAttempts + 1 : 1;
        _ghostRouteDetourAttempts[characterId] = attempts;

        bool forcedBack = attempts > MaxGhostRouteDetourAttempts;

        if (forcedBack)
        {
            VerbosePuts($"loot-task: '{survivor.Character.Alias}' has been dragged off its ghost route through {attempts - 1} combat/loot detours already - forcing its way straight back now, no more side-loot.");
        }
        else
        {
            LootWhateverIsHereNow(survivor, npc);
        }

        Vector3 resumePosition = resumeState.Waypoints[resumeState.Index].Position;

        VerbosePuts($"loot-task: '{survivor.Character.Alias}' is heading back to its ghost route ({Vector3.Distance(npc.transform.position, resumePosition):F0}m away) after the fight.");
        Puts($"ghostroute: '{survivor.Character.Alias}' resuming its ghost route at waypoint {resumeState.Index} after combat (detour {attempts}/{MaxGhostRouteDetourAttempts}{(forcedBack ? ", forced" : "")}).");

        StartLongDistanceWalk(
            survivor,
            resumePosition,
            "ghost route resume point",
            onArrived: () =>
            {
                // Genuinely back on the route now - a fresh disengagement
                // from here on gets its own clean budget rather than
                // accumulating across unrelated interruptions (see
                // _ghostRouteDetourAttempts' own doc comment).
                _ghostRouteDetourAttempts.Remove(characterId);
                StartGhostRoute(survivor, resumeState.Waypoints, resumeState.Index, resumeState.OnComplete);
            },
            onFailed: () =>
            {
                VerbosePuts($"loot-task: '{survivor.Character.Alias}' couldn't get back to its ghost route - starting a fresh search instead.");
                _ghostRouteDetourAttempts.Remove(characterId);
                _pendingGhostRouteToResume.Remove(characterId);
                StopGhostRouteLootScan(characterId);
                StartLootForResourcesTask(survivor);
            });

        return true;
    }

    /// <summary>
    /// Tier 0. Tries each angle in WiggleAngles in turn, spread
    /// over real ticks (not an instant snap), stopping as soon as one
    /// produces real cumulative displacement (WiggleMinProgressDistance) -
    /// the theory being a fragmented-navmesh dead spot is often only a
    /// meter or two wide, so a short step in almost any direction is
    /// enough to clear it.
    /// </summary>
    private void TryWiggleFree(Survivor survivor, Vector3 destination, Action<bool> onComplete)
    {
        TryWiggleDirection(survivor, destination, 0, onComplete);
    }

    /// <summary>
    /// Real toward-destination direction, recomputed fresh each call
    /// (rather than reusing whatever the survivor happened to be facing)
    /// so WiggleAngles' angles are always relative to the actual
    /// destination, not stale facing left over from before this wiggle
    /// episode started.
    /// </summary>
    private static Vector3 GetWiggleBaseDirection(BasePlayer npc, Vector3 destination)
    {
        Vector3 toward = destination - npc.transform.position;
        toward.y = 0f;

        return toward.sqrMagnitude > 0.01f ? toward.normalized : npc.transform.forward;
    }

    private void TryWiggleDirection(Survivor survivor, Vector3 destination, int directionIndex, Action<bool> onComplete)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            onComplete(false);
            return;
        }

        if (directionIndex >= WiggleAngles.Length)
        {
            onComplete(false);
            return;
        }

        Vector3 start = npc.transform.position;
        Vector3 baseDirection = GetWiggleBaseDirection(npc, destination);
        Vector3 worldDirection = (Quaternion.Euler(0f, WiggleAngles[directionIndex], 0f) * baseDirection).normalized;
        Vector3 probeTarget = start + worldDirection * WiggleProbeDistance;
        float stepDistance = WalkSpeed * WalkTickInterval;

        int stepsTaken = 0;
        Timer wiggleTimer = null;

        wiggleTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer currentNpc = survivor.Player;

            if (currentNpc == null || currentNpc.IsDestroyed || stepsTaken >= WiggleMaxStepsPerDirection)
            {
                wiggleTimer.Destroy();
                FinishWiggleDirection(survivor, destination, start, directionIndex, onComplete);
                return;
            }

            // Wounded/downed - pause entirely rather than fight Rust's own
            // incapacitated state machine, same rule as EscalateStuckRecovery
            // and the main walk timer. This tick loop calls ApplyMovementStep
            // directly, so without this check it would keep dragging a
            // downed survivor's transform around every tick.
            if (currentNpc.IsWounded())
            {
                return;
            }

            StepResult step = _engine.NavigationManager.TryGetNextStep(currentNpc.transform.position, probeTarget, stepDistance, out Vector3 nextStep, out _);

            if (step == StepResult.Blocked)
            {
                wiggleTimer.Destroy();
                FinishWiggleDirection(survivor, destination, start, directionIndex, onComplete);
                return;
            }

            ApplyMovementStep(survivor, currentNpc, currentNpc.transform.position, nextStep, step == StepResult.SteppedUp, sprinting: false, stepDistance);
            stepsTaken++;
        });
    }

    private void FinishWiggleDirection(Survivor survivor, Vector3 destination, Vector3 start, int directionIndex, Action<bool> onComplete)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed)
        {
            onComplete(false);
            return;
        }

        ReleaseMovementState(npc);

        if (Vector3.Distance(start, npc.transform.position) >= WiggleMinProgressDistance)
        {
            onComplete(true);
            return;
        }

        int nextDirectionIndex = directionIndex + 1;

        if (nextDirectionIndex >= WiggleAngles.Length)
        {
            onComplete(false);
            return;
        }

        // See StuckReassessPause's own doc comment - a genuinely stuck
        // survivor (every direction blocked on its very first probe tick)
        // could otherwise blow through all six wiggle angles in a
        // fraction of a second. Turning to actually face the next
        // direction before trying it reads as reconsidering, not
        // flickering.
        Vector3 nextWorldDirection = (Quaternion.Euler(0f, WiggleAngles[nextDirectionIndex], 0f) * GetWiggleBaseDirection(npc, destination)).normalized;
        FaceDirection(npc, nextWorldDirection);

        timer.Once(StuckReassessPause, () => TryWiggleDirection(survivor, destination, nextDirectionIndex, onComplete));
    }

    /// <summary>
    /// Tier 1. Finds the nearest point actually on Rust's baked navmesh
    /// (not just anywhere - the real, walkable, connected surface),
    /// trying a growing radius in case the survivor is standing well
    /// clear of the nearest real navmesh polygon.
    /// </summary>
    private bool TryFindNavMeshNudgePoint(Vector3 origin, out Vector3 navMeshPoint)
    {
        foreach (float radius in NavMeshNudgeRadii)
        {
            if (NavMesh.SamplePosition(origin, out NavMeshHit hit, radius, NavMesh.AllAreas))
            {
                navMeshPoint = hit.position;
                return true;
            }
        }

        navMeshPoint = default;
        return false;
    }

    /// <summary>
    /// Tier 2, deliberately last resort. Tries Survivor.LastKnownGoodPosition
    /// first - a live report showed the old random-direction-only version
    /// could relocate a survivor into an equally bad new spot (e.g. a
    /// tight trash-bag/AC-unit corner) instead of anywhere better, since a
    /// random guess has no idea whether the landing spot is actually any
    /// good. A position the survivor has already really stood on and
    /// walked away from is a much safer bet than a blind guess. Falls back
    /// to the original random-direction search (unchanged) if that known-
    /// good position isn't set, is too close to bother with, or itself
    /// fails validation (something could have changed - a moved player,
    /// new debris). Every candidate, known-good or random, goes through
    /// the same TryValidateEmergencyTeleportCandidate check - real ground
    /// beneath it (TryFindGroundBelow) and a clear landing area (no solid
    /// geometry, no other player) - so this can never relocate a survivor
    /// into a spot it hasn't actually verified. Unlike ApplyMovementStep's
    /// step-by-step movement, this is a deliberate one-shot relocation
    /// (matching how SpawnSurvivor/RespawnSurvivor already position a
    /// fresh BasePlayer directly) - not something that should trip the
    /// "moved too far in one tick" anomaly warning that's meant to catch
    /// bugs in ordinary stepped movement, not intentional repositioning.
    /// </summary>
    private bool TryEmergencyTeleport(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;

        if (survivor.LastKnownGoodPosition.HasValue)
        {
            Vector3 knownGood = survivor.LastKnownGoodPosition.Value;

            if (Vector3.Distance(knownGood, npc.transform.position) >= EmergencyTeleportMinDistance
                && TryValidateEmergencyTeleportCandidate(npc, knownGood, out Vector3 validatedKnownGood))
            {
                npc.transform.position = validatedKnownGood;
                npc.MovePosition(validatedKnownGood);
                npc.SendNetworkUpdateImmediate();

                return true;
            }
        }

        for (int attempt = 0; attempt < EmergencyTeleportAttempts; attempt++)
        {
            float angle = UnityEngine.Random.Range(0f, 360f);
            float distance = UnityEngine.Random.Range(EmergencyTeleportMinDistance, EmergencyTeleportMaxDistance);
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            Vector3 candidate = npc.transform.position + direction * distance;

            if (!TryValidateEmergencyTeleportCandidate(npc, candidate, out Vector3 validated))
            {
                continue;
            }

            npc.transform.position = validated;
            npc.MovePosition(validated);
            npc.SendNetworkUpdateImmediate();

            return true;
        }

        return false;
    }

    // Live report + a live /lr.debug.scan cross-reference (Lucas, 2026-08-11):
    // SlyWanderer ended up standing on an unreachable warehouse rooftop with
    // no stairs/ladder anywhere nearby - the scan confirmed NavMesh.
    // SamplePosition found nothing within 3m of that exact spot, only
    // picking up a real point 5.9m away, meaning the roof carries its own
    // disconnected "island" of baked navmesh with no real walkable
    // connection to the ground. TryValidateEmergencyTeleportCandidate only
    // ever checked "is there solid ground below" and "is the landing clear" -
    // never whether the candidate was actually reachable on foot from where
    // the survivor currently stands, so a random probe landing near that
    // roof island passed both checks and got teleported straight up there.
    // Two guards close this, per Lucas's own proposed fix: (1) a cheap
    // elevation-delta gate first - if a candidate's real ground height
    // differs from the survivor's current height by more than
    // EmergencyTeleportMaxElevationChange, it's rejected outright before
    // ever touching pathfinding, since roofs/upper floors are almost always
    // a big elevation jump from ground level; (2) TryCalculatePath - the
    // real pathing engine, same "don't trust proximity alone" fix already
    // used for the stuck-recovery chain's navmesh-nudge tier - confirms a
    // genuine walkable route exists from the survivor's current position to
    // the candidate before ever relocating it there.
    private const float EmergencyTeleportMaxElevationChange = 3f;

    // maxElevationChange/groundProbeHeight/groundProbeDistance all default
    // to the original tight-radius (3-5m) TryEmergencyTeleport numbers -
    // see TryForceRelocateIgnoringCollision's own doc comment
    // (LivingRust.Commands.cs) for why its own much wider 30-80m search
    // passes larger values here instead: real terrain naturally varies
    // more than 3m of elevation over that distance even on completely
    // ordinary, walkable ground, so reusing the tight default would reject
    // most otherwise-legitimate distant candidates outright. The real
    // safety guarantee either way is TryCalculatePath below, not this
    // elevation gate - it's a cheap pre-filter, not the thing actually
    // proving reachability.
    //
    // requireRealPath (2026-09-07, real regression fix - Lucas's own live
    // report: 270 distinct bots stuck in an unresolved rescue loop, ZERO
    // successful force-relocations, after TryForceRelocateIgnoringCollision
    // started reusing this function's own TryCalculatePath check). That
    // check is exactly right for TryEmergencyTeleport's own short-range use
    // (a survivor that's otherwise navigating fine, just needs a small
    // nudge) - it's actively self-defeating for the wide-search last-resort
    // fallback specifically, since a survivor only ever reaches THAT
    // fallback because it's already stuck somewhere real pathfinding
    // doesn't work. Demanding a real calculated path FROM that same broken
    // origin before ever relocating it is close to guaranteeing failure -
    // if a real path from there worked, it wouldn't be stuck in the first
    // place. Defaults to true (TryEmergencyTeleport's own call is
    // unaffected) - TryForceRelocateIgnoringCollision passes false instead,
    // keeping the ground/elevation/obstruction checks (still real, still
    // what stops it landing on a rock formation's summit) while dropping
    // just the one check that made the whole fallback nearly always fail.
    private bool TryValidateEmergencyTeleportCandidate(BasePlayer npc, Vector3 candidate, out Vector3 validated, float maxElevationChange = EmergencyTeleportMaxElevationChange, float groundProbeHeight = 4f, float groundProbeDistance = 6f, bool requireRealPath = true)
    {
        return TryValidateEmergencyTeleportCandidate(npc, candidate, out validated, out _, maxElevationChange, groundProbeHeight, groundProbeDistance, requireRealPath);
    }

    // Real diagnostic overload (2026-09-07, live report: 270 distinct bots
    // stuck with the wide-search fallback failing 100% of the time even
    // after dropping the connectivity requirement - failureReason exists
    // to find out WHICH of the three remaining checks is actually the
    // culprit instead of guessing through further blind deploy cycles).
    private bool TryValidateEmergencyTeleportCandidate(BasePlayer npc, Vector3 candidate, out Vector3 validated, out string failureReason, float maxElevationChange = EmergencyTeleportMaxElevationChange, float groundProbeHeight = 4f, float groundProbeDistance = 6f, bool requireRealPath = true)
    {
        validated = candidate;

        // Real fix (2026-09-21, live log audit: 287 of 291 stall-rescue
        // wide-search failures were "no real ground found"). The probe
        // itself already lifts its rays ProbeStartHeight (4m) above whatever
        // origin it's given and only reaches ProbeSearchDistance (6m) down -
        // lifting the candidate by groundProbeHeight (4m) on top of that
        // started rays 8m above the candidate and stopped them 2m ABOVE it,
        // so open ground could never be hit (only tall props, which is why
        // this "worked" on rock summits). Passing the candidate as-is puts
        // the surface inside the ray's reach (start +4m, end -2m).
        if (!_engine.NavigationManager.TryFindGroundBelow(candidate, groundProbeHeight, groundProbeDistance, out float groundY, out _))
        {
            failureReason = "no real ground found within the probe window";
            return false;
        }

        float elevationDelta = Mathf.Abs(groundY - npc.transform.position.y);

        if (elevationDelta > maxElevationChange)
        {
            failureReason = $"elevation delta {elevationDelta:F1}m > {maxElevationChange:F1}m cap";
            return false;
        }

        validated.y = groundY;

        bool obstructed = Physics.CheckSphere(validated + Vector3.up * 0.9f, 0.4f, LineOfSightBlockingMask, QueryTriggerInteraction.Ignore)
            || IsBlockedByOtherPlayer(validated, npc, exempt: null);

        if (obstructed)
        {
            failureReason = "candidate position obstructed";
            return false;
        }

        if (requireRealPath && !_engine.NavigationManager.TryCalculatePath(npc.transform.position, validated, new RustNavMeshPath(), out _))
        {
            failureReason = "no real calculated path from current position";
            return false;
        }

        failureReason = null;
        return true;
    }
}
