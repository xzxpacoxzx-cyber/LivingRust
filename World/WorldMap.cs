using System.Collections.Generic;

namespace LivingRust.World
{
    /// <summary>
    /// Represents LivingRust's understanding of the Rust world.
    /// This class is populated by WorldScanner and consumed by the AI.
    /// </summary>
    public class WorldMap
    {
        /// <summary>
        /// Every known monument.
        /// </summary>
        private readonly List<Monument> _monuments = new();

        /// <summary>
        /// Every known terrain region.
        /// </summary>
        private readonly List<TerrainRegion> _terrainRegions = new();

        /// <summary>
        /// Read-only collection of monuments.
        /// </summary>
        public IReadOnlyList<Monument> Monuments => _monuments;

        /// <summary>
        /// Read-only collection of terrain regions.
        /// </summary>
        public IReadOnlyList<TerrainRegion> TerrainRegions => _terrainRegions;

        /// <summary>
        /// Number of discovered monuments.
        /// </summary>
        public int MonumentCount => _monuments.Count;

        /// <summary>
        /// Number of discovered terrain regions.
        /// </summary>
        public int TerrainRegionCount => _terrainRegions.Count;

        /// <summary>
        /// Adds a monument discovered during scanning.
        /// </summary>
        public void AddMonument(Monument monument)
        {
            _monuments.Add(monument);
        }

        /// <summary>
        /// Adds a terrain region discovered during scanning.
        /// </summary>
        public void AddTerrainRegion(TerrainRegion region)
        {
            _terrainRegions.Add(region);
        }
    }
}