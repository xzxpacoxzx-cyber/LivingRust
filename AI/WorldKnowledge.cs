using System.Collections.Generic;

namespace LivingRust.AI
{
    /// <summary>
    /// Everything a Character currently knows about the world.
    /// </summary>
    public class WorldKnowledge
    {
        public List<KnownLocation> KnownLocations { get; } = new();

        public List<KnownCharacter> KnownCharacters { get; } = new();
    }
}