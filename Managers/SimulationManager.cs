using LivingRust.Core;
using LivingRust.Models;
using LivingRust.AI;

namespace LivingRust.Managers
{
    /// <summary>
    /// Advances the LivingRust simulation.
    /// </summary>
    public class SimulationManager
    {
        private readonly CharacterManager _characterManager;

        public SimulationManager(CharacterManager characterManager)
        {
            _characterManager = characterManager;

            Logger.Info("SimulationManager initialized.");
        }

        /// <summary>
        /// Advances the simulation by one tick.
        /// </summary>
        public void Tick()
        {
            Logger.Info("----- Simulation Tick -----");

            foreach (Character character in _characterManager.GetAllCharacters())
            {
                Decision decision = character.Brain.Think();

                Logger.Info($"{character.Alias} decided to {decision}.");
            }
        }
    }
}