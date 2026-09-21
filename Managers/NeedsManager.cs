using LivingRust.Core;
using LivingRust.Models;

namespace LivingRust.Managers
{
    /// <summary>
    /// Updates the needs of every survivor over time.
    /// </summary>
    public class NeedManager
    {
        public NeedManager()
        {
            Logger.Info("NeedManager initialized.");
        }

        public void UpdateNeeds(Character character)
        {
            character.Needs.Hunger += 1.0f;
            character.Needs.Thirst += 0.8f;
            character.Needs.Fatigue += 0.2f;
        }
    }
}