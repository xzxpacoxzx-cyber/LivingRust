using LivingRust.Core;

namespace LivingRust.Managers
{
    /// <summary>
    /// Responsible for deciding how many persistent survivors
    /// should exist within the LivingRust world.
    /// </summary>
    public class PopulationManager
    {
        public PopulationManager()
        {
            Logger.Info("PopulationManager initialized.");
        }

        /// <summary>
        /// Returns the desired survivor population.
        /// This will eventually become configurable and dynamic.
        /// </summary>
        public int GetTargetPopulation()
        {
            return 25;
        }

        /// <summary>
        /// Returns how many additional survivors
        /// need to be created.
        /// </summary>
        public int GetRequiredSurvivors(int currentPopulation)
        {
            int targetPopulation = GetTargetPopulation();

            return targetPopulation - currentPopulation;
        }
    }
}