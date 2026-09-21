using UnityEngine;

namespace LivingRust.Models
{
    /// <summary>
    /// Represents a Character currently being simulated
    /// within the LivingRust world.
    /// </summary>
    public class Survivor
    {
        /// <summary>
        /// Permanent character data.
        /// </summary>
        public Character Character { get; }

        /// <summary>
        /// Current world position.
        /// </summary>
        public Vector3 Position { get; set; }

        /// <summary>
        /// Whether this survivor currently exists as a Rust entity.
        /// </summary>
        public bool Spawned { get; set; }

        /// <summary>
        /// Future reference to the Rust BasePlayer.
        /// </summary>
        public BasePlayer Player { get; set; }

        /// <summary>
        /// The most recent position where real, unblocked movement
        /// progress was confirmed (see ApplyMovementStep) - used as the
        /// first, safest candidate when stuck-recovery escalates to an
        /// emergency relocation, preferred over a blind random-direction
        /// teleport since it's a position already known to be walkable.
        /// Null until the survivor has taken at least one real step this
        /// life.
        /// </summary>
        public Vector3? LastKnownGoodPosition { get; set; }

        /// <summary>
        /// The real water surface height (minus the swim submersion
        /// offset) captured ONCE, the moment this survivor started
        /// swimming - held fixed for the rest of that swim rather than
        /// recomputed every tick, which is what let earlier attempts keep
        /// fighting Unity's own continuous underwater drift (round 7).
        /// Locking in per-swim-session (not one global constant) handles a
        /// lake/river sitting at a real elevation different from the main
        /// ocean's own sea level correctly, unlike a flat hardcoded value
        /// (round 8) - Lucas's own explicit follow-up: "that could
        /// definitely happen." Cleared back to null the moment the
        /// survivor stops swimming, so the NEXT time it enters water
        /// (possibly a completely different body of water) gets a fresh
        /// reading instead of reusing a stale one.
        /// </summary>
        public float? LockedSwimY { get; set; }

        public Survivor(Character character)
        {
            Character = character;

            Position = Vector3.zero;

            Spawned = false;

            Player = null;
        }
    }
}