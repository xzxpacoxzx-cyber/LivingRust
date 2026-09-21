using UnityEngine;
using Object = UnityEngine.Object;
using LivingRust.Core;

namespace LivingRust.World
{
    /// <summary>
    /// Responsible for discovering information about the Rust world.
    /// This is the only class that should directly interact with Rust APIs.
    /// </summary>
    public class WorldScanner
    {
        public WorldScanner()
        {
            LivingRust.Core.Logger.Info("WorldScanner initialized.");
        }

        /// <summary>
        /// Scans the Rust world and builds a LivingRust WorldMap.
        /// </summary>
        public WorldMap Scan()
        {
            LivingRust.Core.Logger.Info("Beginning world scan...");

            WorldMap map = new WorldMap();

            MonumentInfo[] monuments = Object.FindObjectsByType<MonumentInfo>(FindObjectsSortMode.None);

            LivingRust.Core.Logger.Info($"Found {monuments.Length} Rust monuments.");

            foreach (MonumentInfo monument in monuments)
            {
                Monument worldMonument = new Monument
                {
                    DisplayName = monument.name,
                    Position = monument.transform.position,
                    Radius = monument.Bounds.extents.magnitude,
                    Type = MonumentType.Unknown
                };

                map.AddMonument(worldMonument);

                LivingRust.Core.Logger.Info($"Registered monument: {worldMonument.DisplayName}");
            }

            LivingRust.Core.Logger.Info($"World scan complete. Registered {map.MonumentCount} monuments.");

            return map;
        }
    }
}