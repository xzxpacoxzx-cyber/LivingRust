using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace LivingRust.Core
{
    /// <summary>
    /// Rust's October 1, 2026 "Livestock Update" made TerrainPath.Monuments/.Roads
    /// internal (confirmed via reflection against the updated Assembly-CSharp.dll -
    /// the fields themselves still exist with the same List&lt;MonumentInfo&gt;/
    /// List&lt;PathList&gt; shapes, just no longer public), breaking every direct
    /// TerrainMeta.Path.Monuments/.Roads read across the project. Cached on first
    /// use since neither list changes after world generation; a map wipe restarts
    /// the whole plugin anyway, which clears this cache along with everything else.
    /// A plain static utility (not part of the LivingRust partial class) so both
    /// the Carbon.Plugins.LivingRust plugin code and LivingRust.Navigation's
    /// NavigationManager (a separate class/namespace) can reach it.
    /// </summary>
    public static class MonumentAccess
    {
        // MonumentInfo is a real scene Component (confirmed - this compiles and
        // returns results), so the standard Unity scene query stands in for the
        // now-internal field directly.
        private static List<MonumentInfo> _cachedMonuments;

        public static List<MonumentInfo> GetAllMonuments()
        {
            return _cachedMonuments ??= Object.FindObjectsOfType<MonumentInfo>().ToList();
        }

        // PathList is a plain System.Object, not a scene Component (confirmed via
        // decompile - FindObjectsOfType<PathList>() doesn't even compile), so there's
        // no scene-query substitute. It only ever existed as entries in TerrainPath's
        // own Roads list, so reflection against the live TerrainMeta.Path singleton
        // is the only way left to reach it.
        private static readonly FieldInfo RoadsField =
            typeof(TerrainPath).GetField("Roads", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static List<PathList> _cachedRoads;

        public static List<PathList> GetAllRoads()
        {
            if (_cachedRoads != null)
            {
                return _cachedRoads;
            }

            if (TerrainMeta.Path != null && RoadsField?.GetValue(TerrainMeta.Path) is List<PathList> roads)
            {
                _cachedRoads = roads;
                return _cachedRoads;
            }

            // TerrainMeta.Path not ready yet - don't cache a permanent empty result,
            // so a later call (once the world has actually loaded) can still succeed.
            return new List<PathList>();
        }
    }
}
