using System;
using System.Collections.Generic;
using UnityEngine;
using LivingRust.AI;

namespace LivingRust.Models
{
    /// <summary>
    /// Represents a persistent survivor within the LivingRust world.
    /// </summary>
    public class Character
    {
        public Guid Id { get; set; }

        public string Alias { get; set; }

        /// <summary>
        /// Permanent Rust userID for this character's BasePlayer, assigned
        /// once at creation and reused for every spawn/respawn across its
        /// whole life (including deaths) - not regenerated per spawn. Rust
        /// ties blueprint unlocks and building privilege authorization to
        /// userID, so keeping this stable is what makes those persist
        /// across a bot's deaths the same way they would for a real player
        /// respawning, rather than resetting every time a fresh BasePlayer
        /// entity gets created for this character.
        /// </summary>
        public ulong BotId { get; set; }

        public CharacterProfile Profile { get; set; }

        public NeedState Needs { get; }

        public WorldKnowledge WorldKnowledge { get; }

        public CharacterBrain Brain { get; }

        /// <summary>
        /// Current life state.
        /// </summary>
        public CharacterState State { get; set; }

        /// <summary>
        /// World position.
        /// This is LivingRust's authoritative position.
        /// </summary>
        public Vector3 Position { get; set; }

        /// <summary>
        /// Facing at last save - restored on respawn/reload so a survivor
        /// comes back looking the same direction it left off, not always
        /// facing Rust's default identity rotation.
        /// </summary>
        public Quaternion Rotation { get; set; } = Quaternion.identity;

        /// <summary>
        /// Health at last save - restored when bringing a still-alive
        /// survivor's BasePlayer back after a real server restart (as
        /// opposed to a death respawn, which always starts fresh at full
        /// health via InitializeHealth).
        /// </summary>
        public float Health { get; set; } = 100f;

        /// <summary>
        /// Full snapshot of everything this survivor was carrying/wearing
        /// at last save (main, belt, and wear containers) - captured just
        /// before Unload/Stop and restored on the next Start so a still-
        /// alive survivor comes back with exactly what it had, rather than
        /// the plugin either losing it or re-granting the starting kit
        /// (which is only correct after an actual death).
        /// </summary>
        public List<SavedItem> Inventory { get; set; } = new();

        /// <summary>
        /// Whether the survivor currently exists as a spawned Rust entity.
        /// Recomputed from the live world every time state is captured
        /// (see LivingRust.Persistence.cs's CaptureAllLiveState), not
        /// something callers should set directly - a stale true here (e.g.
        /// from a death that never got the chance to flip it back) would
        /// make a future restore try to respawn a survivor that's actually
        /// mid-death, not genuinely still alive.
        /// </summary>
        public bool Spawned { get; set; }

        /// <summary>
        /// The survivor's current self-directed job, if any - see
        /// TaskType's own doc comment for how this differs from the
        /// Needs/GoalType scaffold. Persisted so a restart doesn't silently
        /// forget what a survivor was in the middle of, though as of this
        /// field's introduction nothing yet resumes an in-progress task
        /// after a restart (the walk/loot state itself is runtime-only) -
        /// a survivor would come back still flagged with its task but sit
        /// idle until re-triggered.
        /// </summary>
        public TaskType CurrentTask { get; set; } = TaskType.None;

        /// <summary>
        /// True forever once this character has completed at least one
        /// real tier0 or tier1 base build (LivingRust.BaseBuilding.cs's
        /// own AdvanceBuildReplay completion branch sets this) - the real
        /// persistent form of the tier2+ prerequisite gate (2026-08-29,
        /// Lucas's own explicit ask: "if the bot has built a base of any
        /// tier, it is persisted throughout server restarts"). Never
        /// cleared by a later, bigger build (see Home below, which DOES
        /// update to the latest base) - the historical fact of having
        /// built a lower tier once is what the gate actually asks for,
        /// not "is my CURRENT home tier0/1."
        /// </summary>
        public bool HasCompletedLowerTierBaseBuild { get; set; }

        /// <summary>
        /// This character's current real home base, if it's built one -
        /// null until the first successful replay places a real tool
        /// cupboard (2026-08-29, Lucas's own explicit ask: "this is where
        /// the bot will deposit loot and store items etc, as well as
        /// craft higher tiered items... you can't constantly carry
        /// everything everywhere"). Overwritten by whatever base was most
        /// recently completed - a survivor that later builds a bigger
        /// base treats THAT as home going forward, unlike
        /// HasCompletedLowerTierBaseBuild above which never resets.
        /// </summary>
        public HomeBase Home { get; set; }

        /// <summary>
        /// True once this character has rolled its home-site strategy for
        /// the current life (LivingRust.HomeSiteStrategy.cs's own
        /// RollHomeSiteStrategyIfFreshLife, backed at runtime by the
        /// in-memory-only _hasRolledHomeSiteStrategy set). Persisted
        /// (2026-09-15) so a plugin reload doesn't silently wipe that
        /// in-memory flag and cause an already-committed, still-alive
        /// survivor to roll a brand new random home-site choice out from
        /// under itself mid-life - live evidence: '18SharpJackal' rolled
        /// home-site strategy 3 separate times without ever dying, purely
        /// because each hot-reload during testing reset the in-memory-only
        /// flag while its Character record (this one) sailed through
        /// untouched. RestoreSpawnedSurvivors (LivingRust.Persistence.cs)
        /// re-seeds the in-memory set from this field on every reload;
        /// OnPlayerDeath still resets it to false for a fresh life, same as
        /// before.
        /// </summary>
        public bool HasRolledHomeSiteStrategy { get; set; }

        /// <summary>
        /// The base design this character has committed to gathering
        /// toward, if any (LivingRust.HomeSiteStrategy.cs's own
        /// TryPursueBaseGatherGoal, backed at runtime by the in-memory-only
        /// _rolledBaseDesign dictionary) - null Tier means nothing rolled
        /// yet. Persisted alongside PursuingBaseGatherGoal below (2026-09-15,
        /// Lucas's own explicit ask: "have it hold that base design it
        /// rolled for through deaths") for the identical reload-safety
        /// reason HasRolledHomeSiteStrategy exists - see its own doc
        /// comment. Real gathered resources still don't survive death
        /// (normal Rust inventory-on-death) - this only keeps the CHOICE of
        /// which design to gather toward stable, so neither a death nor a
        /// reload is also a fresh coin flip on the goal itself.
        /// </summary>
        public string RolledBaseTier { get; set; }

        public string RolledBaseDesignPath { get; set; }

        public Dictionary<string, int> RolledBaseCost { get; set; }

        /// <summary>
        /// True while this character is actively pursuing the base-gather
        /// goal (LivingRust.HomeSiteStrategy.cs's own in-memory-only
        /// _pursuingBaseGatherGoal set) - persisted alongside
        /// RolledBaseTier/RolledBaseDesignPath/RolledBaseCost, same
        /// reasoning; without this a reload could restore the rolled design
        /// correctly but still leave the survivor never actually checking
        /// it (ContinueLootTask only consults TryPursueBaseGatherGoal at
        /// all when this is true).
        /// </summary>
        public bool PursuingBaseGatherGoal { get; set; }

        public Character()
        {
            Id = Guid.NewGuid();

            Needs = new NeedState();

            WorldKnowledge = new WorldKnowledge();

            Brain = new CharacterBrain(this);

            State = CharacterState.Alive;

            Spawned = false;

            Position = Vector3.zero;
        }
    }

    /// <summary>
    /// A character's real persisted home base (2026-08-29) - just enough
    /// to find it again after a restart and know what's there. Stores the
    /// raw ulong value of the cupboard/workbench's real BaseNetworkable.
    /// net.ID.Value (confirmed real via decompile - NetworkableId itself
    /// is just a one-field struct wrapping this same ulong, kept as the
    /// plain primitive here to avoid any dependency on that engine type
    /// from the Models project) - a NetworkableId stays stable across a
    /// server restart for anything that survives Rust's own save/load,
    /// the same identity a real player's own bookmarked base would keep.
    /// Live entity references obviously can't be JSON-serialized and
    /// wouldn't survive the entities themselves being destroyed/respawned
    /// across a restart anyway - a consumer needs to re-resolve these via
    /// BaseNetworkable.serverEntities.Find(new NetworkableId(id)) and
    /// treat a miss (the structure got raided/demolished/never actually
    /// survived the restart) as "no home after all," not assume they're
    /// always still valid.
    /// </summary>
    public class HomeBase
    {
        public Vector3 Position { get; set; }

        public int TierRank { get; set; } = -1;

        public ulong CupboardNetId { get; set; }

        public ulong WorkbenchNetId { get; set; }

        /// <summary>
        /// Real computed door-crossing route (2026-08-29, Lucas's own
        /// explicit ask: "is it possible to have a ghostroute added for
        /// the different bases within each tier? so it knows how to get
        /// in and out?"). Computed ONCE, right when the base finishes
        /// building, straight from the same real trace data the replay
        /// itself used - not authored by hand the way LivingRust.
        /// MonumentRoutes.cs's own ghost routes are, since a base design's
        /// own door position/orientation is already fully known the
        /// instant it's placed, unlike a monument's layout. DoorPosition
        /// is the front door's own real position; InsidePoint/
        /// OutsidePoint sit a short, fixed distance either side of it
        /// along the real building-centroid-to-door axis (computed from
        /// every real piece the trace placed), so the direction is always
        /// genuinely "toward the interior" / "away from the base" instead
        /// of a guess. Vector3.zero on all three (the default) means no
        /// door was ever found for this design - callers must check
        /// DoorPosition != Vector3.zero before trusting this route.
        /// </summary>
        public Vector3 DoorPosition { get; set; }

        public Vector3 InsidePoint { get; set; }

        public Vector3 OutsidePoint { get; set; }

        /// <summary>
        /// The real placement anchor this specific instance was built
        /// from (BaseBuilding.cs's own BuildReplayState.OriginPosition) -
        /// every piece in that replay landed at OriginPosition + (its own
        /// recorded offset from the design's first placed piece), a pure
        /// translation with NO rotation ever applied (confirmed via
        /// ReplayBuildTrace - Quaternion.Euler(row.Rotation) always uses
        /// the design's own raw recorded rotation verbatim). That means a
        /// route authored once against ANY instance of this same design
        /// re-projects onto every OTHER instance with simple addition -
        /// see DoorRoutes below.
        /// </summary>
        public Vector3 BuildOriginPosition { get; set; }

        /// <summary>
        /// Which design file this instance was built from (e.g.
        /// "tier0/base3.csv", relative to BaseBuilding.cs's own
        /// BaseDesignsDirectory) - lets a door route authored against one
        /// live instance of this design be found again for every other
        /// instance of the SAME design.
        /// </summary>
        public string SourceDesignPath { get; set; }

        /// <summary>
        /// Real hardcoded door ghost routes (2026-08-29, seventh round -
        /// Lucas's own explicit fallback after repeated live door/physics
        /// failures: "if we can't fix this in due time, we resort to
        /// hardcoded ghostroutes for entering the bases"). Each entry is
        /// one door's real recorded in/out path (LivingRust.Debug.cs's
        /// /lr.debug.savedoorroute), re-projected onto THIS instance via
        /// BuildOriginPosition above - same proven waypoint-phase engine
        /// (StartGhostRoute) already driving every monument ghost route,
        /// sidestepping the door-collider/physics uncertainty entirely
        /// rather than continuing to chase it. Empty until at least one
        /// route has actually been recorded for this design - callers
        /// fall back to the computed DoorPosition/InsidePoint/OutsidePoint
        /// route above when this is empty, same "folder starts empty,
        /// silent no-op" convention MonumentGhostRouteFolders already
        /// uses.
        /// </summary>
        public List<HomeDoorRoute> DoorRoutes { get; set; } = new();
    }

    /// <summary>
    /// One authored door route, already re-projected into this specific
    /// HomeBase instance's own world space - see HomeBase.DoorRoutes'
    /// own doc comment.
    /// </summary>
    public class HomeDoorRoute
    {
        /// <summary>
        /// The real recorded path through this door, in order - played
        /// forward or reversed depending on which end the survivor is
        /// currently closer to (direction-agnostic by design, since a
        /// route is just as valid walked either way).
        /// </summary>
        public List<Vector3> Waypoints { get; set; } = new();

        /// <summary>
        /// The real live door nearest this route's own path at the
        /// moment it was loaded - re-resolved fresh each time it's
        /// actually used (an upgrade/raid could replace the entity), this
        /// is only a starting point for that lookup, same pattern
        /// CrossHomeDoor's own ResolveDoor already established.
        /// </summary>
        public Vector3 DoorAnchorPosition { get; set; }
    }
}