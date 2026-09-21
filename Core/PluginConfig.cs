namespace LivingRust.Core
{
    /// <summary>
    /// Global configuration for LivingRust.
    /// These values can later be loaded from a JSON configuration file.
    /// </summary>
    public class PluginConfig
    {
        /// <summary>
        /// Enables additional debug logging.
        /// </summary>
        public bool DebugMode { get; set; } = true;

        /// <summary>
        /// Maximum number of persistent Characters allowed.
        /// </summary>
        public int MaximumCharacters { get; set; } = 50;

        /// <summary>
        /// Automatically spawn Characters when the plugin starts.
        /// </summary>
        public bool AutoSpawnCharacters { get; set; } = true;

        /// <summary>
        /// Interval (seconds) between AI updates.
        /// Lower values are more responsive but use more CPU.
        /// </summary>
        public float AIUpdateInterval { get; set; } = 1.0f;

        /// <summary>
        /// Enables experimental AI features.
        /// Useful while developing LivingRust.
        /// </summary>
        public bool ExperimentalFeatures { get; set; } = false;
    }
}