using LivingRust.Models;
using LivingRust.Core;

namespace LivingRust.Managers
{
    /// <summary>
    /// Handles combat interactions between Characters.
    /// </summary>
    public class CombatManager
    {
        public CombatManager()
        {
            Logger.Info("CombatManager initialized.");
        }

        public void StartCombat(Character attacker, Character defender)
        {
            Logger.Info($"{attacker.Alias} has engaged {defender.Alias}.");
        }

        private int CalculateDamage(Character attacker, Character defender)
        {
            // Placeholder until the combat system is implemented.
            return 0;
        }

        public void EndCombat(Character winner, Character loser)
        {
            Logger.Info($"{winner.Alias} defeated {loser.Alias}.");
        }
    }
}