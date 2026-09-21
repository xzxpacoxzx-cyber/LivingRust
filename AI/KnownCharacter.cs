using System;

namespace LivingRust.AI
{
    /// <summary>
    /// Represents another survivor this Character knows.
    /// </summary>
    public class KnownCharacter
    {
        public Guid CharacterId { get; set; }

        public string Alias { get; set; }

        /// <summary>
        /// -100 = sworn enemy
        /// 0 = neutral
        /// +100 = trusted friend
        /// </summary>
        public float Relationship { get; set; }

        public bool IsAlive { get; set; } = true;
    }
}