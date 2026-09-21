using System.Collections.Generic;
using System.IO;
using LivingRust.Core;
using LivingRust.Models;
using Newtonsoft.Json;

namespace LivingRust.Managers
{
    /// <summary>
    /// Responsible for saving and loading the LivingRust world.
    /// </summary>
    public class SaveManager
    {
        private const string SaveDirectory = "LivingRust";
        private const string SaveFile = "LivingRust/world.json";

        public SaveManager()
        {
            Logger.Info("SaveManager initialized.");

            if (!Directory.Exists(SaveDirectory))
            {
                Directory.CreateDirectory(SaveDirectory);
            }
        }

        public void SaveCharacters(IEnumerable<Character> characters)
        {
            Logger.Info("Saving characters...");

            string json = JsonConvert.SerializeObject(characters, Formatting.Indented);

            File.WriteAllText(SaveFile, json);

            Logger.Info("Characters saved successfully.");
        }

        public List<Character> LoadCharacters()
        {
            Logger.Info("Loading characters...");

            if (!File.Exists(SaveFile))
            {
                Logger.Info("No save file exists.");

                return new List<Character>();
            }

            string json = File.ReadAllText(SaveFile);

            List<Character>? characters =
                JsonConvert.DeserializeObject<List<Character>>(json);

            Logger.Info($"Loaded {characters?.Count ?? 0} characters.");

            return characters ?? new List<Character>();
        }
    }
}