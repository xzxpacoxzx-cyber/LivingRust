using System.Collections.Generic;

namespace LivingRust.AI
{
    /// <summary>
    /// Represents everything a Character remembers.
    /// This information persists across server restarts.
    /// </summary>
    public class CharacterMemory
    {
        /// <summary>
        /// Locations the Character considers "home".
        /// </summary>
        public List<string> KnownHomes { get; } = new();

        /// <summary>
        /// Known friendly survivors.
        /// </summary>
        public List<string> KnownAllies { get; } = new();

        /// <summary>
        /// Known hostile survivors.
        /// </summary>
        public List<string> KnownEnemies { get; } = new();

        /// <summary>
        /// Places this Character has already explored.
        /// </summary>
        public List<string> VisitedLocations { get; } = new();
    }
}