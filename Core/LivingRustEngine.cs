using System.Collections.Generic;
using LivingRust.Managers;
using LivingRust.Models;
using LivingRust.Navigation;

namespace LivingRust.Core
{
    /// <summary>
    /// The heart of the LivingRust simulation.
    /// </summary>
    public class LivingRustEngine
    {
        public CharacterManager CharacterManager { get; }
        public SurvivorManager SurvivorManager { get; }
        public CombatManager CombatManager { get; }
        public SaveManager SaveManager { get; }
        public NavigationManager NavigationManager { get; }

        public LivingRustEngine()
        {
            Logger.Info("Creating LivingRust Engine...");

            CharacterManager = new CharacterManager();
            SurvivorManager = new SurvivorManager();

            CombatManager = new CombatManager();
            SaveManager = new SaveManager();
            NavigationManager = new NavigationManager();

            Logger.Info("LivingRust Engine created.");
        }

        public void Start()
        {
            Logger.Info("Starting LivingRust Engine...");

            // Load previously saved characters so RestoreSpawnedSurvivors can
            // re-link/respawn them. New characters only ever come from an
            // explicit spawn command - this never auto-pads the population.
            List<Character> characters = SaveManager.LoadCharacters();

            foreach (Character character in characters)
            {
                CharacterManager.RegisterCharacter(character);

                SurvivorManager.Create(character);
            }

            if (characters.Count > 0)
            {
                Logger.Info($"Loaded {CharacterManager.Count} persistent survivors.");
            }
            else
            {
                Logger.Info("No existing world found.");
            }

            Logger.Info("LivingRust Engine started.");
        }

        public void Stop()
        {
            Logger.Info("Stopping LivingRust Engine...");

            SaveManager.SaveCharacters(CharacterManager.GetAllCharacters());

            Logger.Info("LivingRust Engine stopped.");
        }
    }
}