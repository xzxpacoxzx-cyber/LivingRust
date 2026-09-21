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
        public ConfigManager ConfigManager { get; }
        public SaveManager SaveManager { get; }
        public PopulationManager PopulationManager { get; }
        public SimulationManager SimulationManager { get; }
        public WorldManager WorldManager { get; }
        public NavigationManager NavigationManager { get; }

        public LivingRustEngine()
        {
            Logger.Info("Creating LivingRust Engine...");

            CharacterManager = new CharacterManager();
            SurvivorManager = new SurvivorManager();

            CombatManager = new CombatManager();
            ConfigManager = new ConfigManager();
            SaveManager = new SaveManager();
            PopulationManager = new PopulationManager();
            SimulationManager = new SimulationManager(CharacterManager);
            WorldManager = new WorldManager();
            NavigationManager = new NavigationManager();

            Logger.Info("LivingRust Engine created.");
        }

        public void Start()
        {
            Logger.Info("Starting LivingRust Engine...");

            WorldManager.Start();

            // Load previously saved characters.
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

            // Determine how many new survivors should exist.
            int survivorsToCreate =
                PopulationManager.GetRequiredSurvivors(CharacterManager.Count);

            Logger.Info($"Population requires {survivorsToCreate} additional survivors.");

            for (int i = 0; i < survivorsToCreate; i++)
            {
                Character character = CharacterManager.CreateInitialSurvivor();

                SurvivorManager.Create(character);
            }

            Logger.Info($"Persistent Characters : {CharacterManager.Count}");
            Logger.Info($"Active Survivors      : {SurvivorManager.Count}");

            SimulationManager.Tick();

            Logger.Info("LivingRust Engine started.");
        }

        public void Stop()
        {
            Logger.Info("Stopping LivingRust Engine...");

            WorldManager.Stop();

            SaveManager.SaveCharacters(CharacterManager.GetAllCharacters());

            Logger.Info("LivingRust Engine stopped.");
        }
    }
}