using UnityEngine;

namespace LivingRust.World
{
    /// <summary>
    /// Represents a Rust monument known to LivingRust.
    /// </summary>
    public class Monument
    {
        /// <summary>
        /// Monument category.
        /// </summary>
        public MonumentType Type { get; set; }

        /// <summary>
        /// Human readable monument name.
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Centre position of the monument.
        /// </summary>
        public Vector3 Position { get; set; }

        /// <summary>
        /// Radius of the monument.
        /// This will later be used by navigation, spawning and AI.
        /// </summary>
        public float Radius { get; set; }
    }
}