using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Reduces visibility/engagement checks by up to JungleMaxVisibilityLoss when the
    // shooter, target, or ground between them is in jungle, fading in smoothly at
    // biome edges.
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
