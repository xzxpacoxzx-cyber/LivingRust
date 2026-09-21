namespace LivingRust.World
{
    /// <summary>
    /// Represents a meaningful terrain region.
    /// </summary>
    public class TerrainRegion
    {
        public Biome Biome { get; set; }

        public string Name { get; set; }

        public float Area { get; set; }
    }
}