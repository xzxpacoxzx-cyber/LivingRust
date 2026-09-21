using System;

namespace LivingRust.Models
{
    /// <summary>
    /// Serializable save data for a Character.
    /// This is written to disk and restored when the server starts.
    /// </summary>
    public class CharacterData
    {
        /// <summary>
        /// Permanent unique identifier.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Steam-like display alias.
        /// </summary>
        public string Alias { get; set; }

        /// <summary>
        /// Long-term behavioural preferences.
        /// </summary>
        public CharacterProfile Profile { get; set; }

        /// <summary>
        /// Lifetime statistics.
        /// </summary>
        public int Deaths { get; set; }

        public int Kills { get; set; }

        /// <summary>
        /// UTC timestamp of when this Character was created.
        /// </summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>
        /// UTC timestamp of the last save.
        /// </summary>
        public DateTime LastSeenUtc { get; set; }
    }
}