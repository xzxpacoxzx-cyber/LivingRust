using LivingRust.Core;
using LivingRust.World;

namespace LivingRust.Managers
{
    /// <summary>
    /// Owns LivingRust's representation of the Rust world.
    /// </summary>
    public class WorldManager
    {
        private readonly WorldScanner _scanner;

        /// <summary>
        /// LivingRust's current understanding of the world.
        /// </summary>
        public WorldMap Map { get; private set; }

        public WorldManager()
        {
            Logger.Info("WorldManager created.");

            _scanner = new WorldScanner();

            Map = new WorldMap();
        }

        /// <summary>
        /// Starts the world manager and scans the Rust map.
        /// </summary>
        public void Start()
        {
            Logger.Info("Starting WorldManager...");

            Map = _scanner.Scan();

            Logger.Info($"World initialized with {Map.MonumentCount} monuments.");
        }

        public void Stop()
        {
            Logger.Info("Stopping WorldManager...");
        }
    }
}