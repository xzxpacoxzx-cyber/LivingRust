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
        /// The most recent confirmed walkable position, used as a safe fallback
        /// during stuck-recovery instead of a random teleport.
        /// </summary>
        public Vector3? LastKnownGoodPosition { get; set; }

        /// <summary>
        /// The water surface height captured once when swimming starts, held fixed
        /// for that swim to avoid drift, and cleared when swimming stops.
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