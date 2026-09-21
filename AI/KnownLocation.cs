namespace LivingRust.AI
{
    /// <summary>
    /// Represents what a Character knows about a location.
    /// </summary>
    public class KnownLocation
    {
        /// <summary>
        /// Human-readable name.
        /// Example: Launch Site
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// How valuable this location is for loot.
        /// </summary>
        public float LootValue { get; set; }

        /// <summary>
        /// Estimated danger.
        /// </summary>
        public float Danger { get; set; }

        /// <summary>
        /// Estimated travel cost.
        /// Initially this can simply be distance.
        /// </summary>
        public float TravelCost { get; set; }

        /// <summary>
        /// How familiar this Character is with the location.
        /// </summary>
        public float Familiarity { get; set; }
    }
}