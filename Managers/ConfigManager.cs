using LivingRust.Core;

namespace LivingRust.Managers
{
    /// <summary>
    /// Manages the LivingRust configuration.
    /// </summary>
    public class ConfigManager
    {
        public PluginConfig Config { get; private set; }

        public ConfigManager()
        {
            Logger.Info("ConfigManager initialized.");

            Config = new PluginConfig();
        }

        public void Load()
        {
            // TODO
        }

        public void Save()
        {
            // TODO
        }
    }
}