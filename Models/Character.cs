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
        /// Permanent Rust userID assigned once at creation and reused across every
        /// spawn, so blueprint unlocks and building privileges persist through deaths.
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
        /// Facing direction at last save, restored on respawn so the survivor faces
        /// the same direction it left off.
        /// </summary>
        public Quaternion Rotation { get; set; } = Quaternion.identity;

        /// <summary>
        /// Health at last save, restored when a still-alive survivor comes back
        /// after a server restart.
        /// </summary>
        public float Health { get; set; } = 100f;

        /// <summary>
        /// Snapshot of everything the survivor was carrying or wearing at last save,
        /// restored so it comes back with the same items.
        /// </summary>
        public List<SavedItem> Inventory { get; set; } = new();

        /// <summary>
        /// Whether the survivor currently exists as a spawned Rust entity. Recomputed
        /// from live world state rather than set directly.
        /// </summary>
        public bool Spawned { get; set; }

        /// <summary>
        /// The survivor's current self-directed task, if any. Persisted across
        /// restarts, though an in-progress task is not automatically resumed.
        /// </summary>
        public TaskType CurrentTask { get; set; } = TaskType.None;

        /// <summary>
        /// Set permanently once this character has completed at least one tier0 or
        /// tier1 base build, and never cleared by later builds. Used to gate
        /// tier2+ progression.
        /// </summary>
        public bool HasCompletedLowerTierBaseBuild { get; set; }

        /// <summary>
        /// This character's current home base, if any, used for depositing loot and
        /// crafting. Updated whenever a new base is completed.
        /// </summary>
        public HomeBase Home { get; set; }

        /// <summary>
        /// Whether this character has already rolled its home-site strategy for the
        /// current life. Persisted so a plugin reload doesn't cause a re-roll, and
        /// reset to false on death.
        /// </summary>
        public bool HasRolledHomeSiteStrategy { get; set; }

        /// <summary>
        /// Whether this character has already rolled whether to rush a monument
        /// before base-building, for the current life. Persisted to survive reloads
        /// and reset to false on death.
        /// </summary>
        public bool HasRolledMonumentRush { get; set; }

        /// <summary>
        /// True while this character is actively rushing a monument before
        /// base-building. MonumentRushDeadline is a wall-clock safety net for a
        /// rush that never naturally concludes.
        /// </summary>
        public bool PursuingMonumentRushGoal { get; set; }
        public float MonumentRushDeadline { get; set; }

        /// <summary>
        /// The base design this character has committed to gathering toward, if any.
        /// Persisted so the choice of design survives deaths and reloads.
        /// </summary>
        public string RolledBaseTier { get; set; }

        public string RolledBaseDesignPath { get; set; }

        public Dictionary<string, int> RolledBaseCost { get; set; }

        /// <summary>
        /// True while this character is actively pursuing the base-gather goal.
        /// Persisted alongside the rolled base design fields for the same reason.
        /// </summary>
        public bool PursuingBaseGatherGoal { get; set; }

        /// <summary>
        /// Consecutive number of times the tier-upgrade affordability check has
        /// failed for this character, used as a pity-grant counter. Persists through
        /// death and restarts, and resets to 0 once an upgrade becomes affordable.
        /// </summary>
        public int TierUpgradeStruggleCount { get; set; }

        /// <summary>
        /// Set permanently once this character has completed at least one trip
        /// depositing loot at its base. Used with Home to determine whether a
        /// survivor has passed the early-game restrictions.
        /// </summary>
        public bool HasDepositedInitialLoot { get; set; }

        /// <summary>
        /// Ammo types this character has unlocked by holding a firearm that uses
        /// them. Never shrinks, so known ammo types keep being restocked.
        /// </summary>
        public List<string> KnownAmmoTypes { get; set; } = new();

        /// <summary>
        /// How many free blueprint researches this character has already been granted from
        /// its scrap hoard (one per 150 scrap sitting in its base storage). Never shrinks, so
        /// spending or recycling the scrap later doesn't hand the same entitlement out twice.
        /// </summary>
        public int FreeResearchesUsed { get; set; }

        /// <summary>
        /// Last place this character died and what killed it, used to stop it revisiting a
        /// death site that killed it the same way twice. Persisted so a reload doesn't forget.
        /// </summary>
        public Vector3 LastDeathPosition { get; set; }

        public string LastDeathCause { get; set; }

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
    /// A character's persisted home base, storing enough information to find it
    /// again and re-resolve its entities after a server restart.
    /// </summary>
    public class HomeBase
    {
        public Vector3 Position { get; set; }

        public int TierRank { get; set; } = -1;

        public ulong CupboardNetId { get; set; }

        public ulong WorkbenchNetId { get; set; }

        /// <summary>
        /// The computed door-crossing route for this base, calculated once when it
        /// finishes building. DoorPosition is the door's location; InsidePoint and
        /// OutsidePoint sit either side of it. A zero value means no door was found.
        /// </summary>
        public Vector3 DoorPosition { get; set; }

        public Vector3 InsidePoint { get; set; }

        public Vector3 OutsidePoint { get; set; }

        /// <summary>
        /// The placement anchor this instance was built from, letting a route
        /// authored against one instance of a design be re-projected onto others.
        /// </summary>
        public Vector3 BuildOriginPosition { get; set; }

        /// <summary>
        /// The design file this instance was built from, used to match door routes
        /// to other instances of the same design.
        /// </summary>
        public string SourceDesignPath { get; set; }

        /// <summary>
        /// Hardcoded door ghost routes recorded for this base, re-projected onto
        /// this instance. Empty until a route has been recorded, in which case
        /// callers fall back to the computed door route above.
        /// </summary>
        public List<HomeDoorRoute> DoorRoutes { get; set; } = new();
    }

    /// <summary>
    /// One authored door route, re-projected into this HomeBase instance's world
    /// space.
    /// </summary>
    public class HomeDoorRoute
    {
        /// <summary>
        /// The recorded path through this door, in order, walkable in either
        /// direction.
        /// </summary>
        public List<Vector3> Waypoints { get; set; } = new();

        /// <summary>
        /// The nearest door to this route's path when loaded, re-resolved each time
        /// it's used since the entity may have been replaced.
        /// </summary>
        public Vector3 DoorAnchorPosition { get; set; }
    }
}