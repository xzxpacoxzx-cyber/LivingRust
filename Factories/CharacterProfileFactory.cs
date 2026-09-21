using System;
using LivingRust.Models;

namespace LivingRust.Factories
{
    /// <summary>
    /// Creates believable behavioural profiles for Characters.
    /// </summary>
    public static class CharacterProfileFactory
    {
        private static readonly Random Random = new();

        public static CharacterProfile Create()
        {
            float confidence = NextFloat();

            return new CharacterProfile
            {
                Confidence = confidence,

                RiskTolerance = Clamp(confidence + Offset()),

                CombatInterest = Clamp(confidence + Offset()),

                BuildingInterest = NextFloat(),

                FarmingInterest = NextFloat(),

                ExplorationInterest = NextFloat(),

                MonumentInterest = Clamp(confidence + Offset()),

                RaidingInterest = Clamp(confidence + Offset())
            };
        }

        private static float NextFloat()
        {
            return (float)Random.NextDouble();
        }

        /// <summary>
        /// Returns a random offset between -0.25 and +0.25.
        /// </summary>
        private static float Offset()
        {
            return ((float)Random.NextDouble() - 0.5f) * 0.5f;
        }

        private static float Clamp(float value)
        {
            if (value < 0f)
                return 0f;

            if (value > 1f)
                return 1f;

            return value;
        }
    }
}