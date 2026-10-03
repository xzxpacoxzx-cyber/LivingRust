using System;
using System.IO;
using Newtonsoft.Json;

namespace Carbon.Plugins;

/// <summary>
/// Detects a genuine fresh wipe (a real new map/save, not a plugin reload or an ordinary server
/// restart on the same ongoing map) and gives collectible entities (hemp/wood/stone/ore piles,
/// berries, mushrooms) a wider search radius for a short window right after - 2026-10-03, Lucas's
/// own explicit spec: they're a real early-game resource boost worth prioritizing right after a
/// wipe, while deliberately NOT re-arming on every plugin reload during that same wipe.
///
/// Wipe detection: World.Seed/World.Size identify a specific map generation and stay the same
/// across restarts of that same map; they only change on a genuine new wipe. The combination,
/// plus the real wall-clock moment first seen, is persisted to LivingRust/wipe_state.json (same
/// convention as monument_loot_zones.json/monument_cover_points.json) so a plugin reload mid-wipe
/// reads the SAME already-recorded wipe start time back rather than re-arming a fresh 15 minutes -
/// Time.realtimeSinceStartup/Carbon's own in-memory state can't be used for this since both reset
/// on every reload, exactly what needs to be told apart from a real wipe here.
/// </summary>
public partial class LivingRust
{
    private const string WipeStateSaveFile = "LivingRust/wipe_state.json";

    // How long after a fresh wipe the collectible radius boost stays active.
    private const float FreshWipeCollectibleBoostSeconds = 900f;

    // Applied to CollectibleSearchRadius/EnRouteCollectibleDetectionRadius and the "everything
    // else" (hemp/wood/stone/ore) tier of GetCollectibleDivertRadius during the boost window.
    // Berries/mushrooms are left at their normal tight radii - they're common low-value
    // consumables, not the early-game building-block resources this boost is actually for.
    private const float FreshWipeCollectibleRadiusMultiplier = 2f;

    private sealed class WipeState
    {
        public uint Seed;
        public uint Size;
        public DateTime WipeStartUtc;
    }

    private WipeState _wipeState;

    /// <summary>
    /// Called once from OnServerInitialized. Loads the persisted wipe marker, compares it against
    /// this map's real seed/size, and either confirms the existing window (same map, could be a
    /// reload or a restart) or records a brand new one (seed/size changed, or nothing on disk yet
    /// - a genuine fresh wipe).
    /// </summary>
    private void DetectFreshWipe()
    {
        uint seed = World.Seed;
        uint size = World.Size;

        WipeState loaded = TryLoadWipeState();

        if (loaded != null && loaded.Seed == seed && loaded.Size == size)
        {
            _wipeState = loaded;

            float remaining = FreshWipeCollectibleBoostSeconds - (float)(DateTime.UtcNow - _wipeState.WipeStartUtc).TotalSeconds;

            if (remaining > 0f)
            {
                Puts($"fresh-wipe: same map as last boot (seed {seed}, size {size}) - collectible boost still has {remaining:F0}s left.");
            }

            return;
        }

        // Seed/size changed (or no marker on disk at all) - a genuine new wipe.
        _wipeState = new WipeState
        {
            Seed = seed,
            Size = size,
            WipeStartUtc = DateTime.UtcNow,
        };

        SaveWipeState();
        Puts($"fresh-wipe: new map detected (seed {seed}, size {size}) - boosting collectible search radius {FreshWipeCollectibleRadiusMultiplier:F0}x for the next {FreshWipeCollectibleBoostSeconds:F0}s.");
    }

    private bool IsWithinFreshWipeCollectibleBoost()
    {
        return _wipeState != null
            && (DateTime.UtcNow - _wipeState.WipeStartUtc).TotalSeconds < FreshWipeCollectibleBoostSeconds;
    }

    // Multiplies a base collectible radius by FreshWipeCollectibleRadiusMultiplier while still
    // within the fresh-wipe boost window; returns it unchanged otherwise.
    private float ApplyFreshWipeCollectibleBoost(float baseRadius)
    {
        return IsWithinFreshWipeCollectibleBoost() ? baseRadius * FreshWipeCollectibleRadiusMultiplier : baseRadius;
    }

    private WipeState TryLoadWipeState()
    {
        try
        {
            if (!File.Exists(WipeStateSaveFile))
            {
                return null;
            }

            return JsonConvert.DeserializeObject<WipeState>(File.ReadAllText(WipeStateSaveFile));
        }
        catch (Exception ex)
        {
            Puts($"WARNING: fresh-wipe: couldn't read {WipeStateSaveFile} ({ex.Message}) - treating this as a fresh wipe.");
            return null;
        }
    }

    private void SaveWipeState()
    {
        try
        {
            if (!Directory.Exists("LivingRust"))
            {
                Directory.CreateDirectory("LivingRust");
            }

            File.WriteAllText(WipeStateSaveFile, JsonConvert.SerializeObject(_wipeState, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Puts($"WARNING: fresh-wipe: couldn't save {WipeStateSaveFile} ({ex.Message}).");
        }
    }
}
