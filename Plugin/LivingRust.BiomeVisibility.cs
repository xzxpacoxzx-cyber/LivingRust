using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Jungle sight-line penalty (2026-09-21, Lucas's own live report: shot at
    // by a bot from "a ridiculous distance in dense vegetation" in the jungle
    // biome). Real jungle foliage cuts visibility far more than the flat
    // detection/engagement ranges assume, so every range check that decides
    // "can I see/engage them" is scaled down by up to JungleMaxVisibilityLoss
    // when the shooter or the target (or the ground between them) is in
    // jungle. Biome weight is 0-1 (blends at biome edges), so the penalty
    // fades in rather than snapping.
    private const float JungleMaxVisibilityLoss = 0.5f;

    private static float GetBiomeVisibilityFactor(Vector3 from, Vector3 to)
    {
        if (TerrainMeta.BiomeMap == null)
        {
            return 1f;
        }

        float weight = (TerrainMeta.BiomeMap.GetBiome(from, TerrainBiome.JUNGLE)
            + TerrainMeta.BiomeMap.GetBiome(Vector3.Lerp(from, to, 0.5f), TerrainBiome.JUNGLE)
            + TerrainMeta.BiomeMap.GetBiome(to, TerrainBiome.JUNGLE)) / 3f;

        return 1f - JungleMaxVisibilityLoss * Mathf.Clamp01(weight);
    }
}
