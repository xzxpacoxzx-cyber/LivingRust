using LivingRust.Core;
using LivingRust.Models;

namespace LivingRust.AI
{
    /// <summary>
    /// Responsible for making decisions for a Character.
    /// </summary>
    public class CharacterBrain
    {
        private readonly Character _character;

        public CharacterBrain(Character character)
        {
            _character = character;
        }

        /// <summary>
        /// Gives the Character an opportunity to think.
        /// </summary>
        public Decision Think()
        {
            GoalType goal = DetermineGoal();

            Logger.Info(
                $"{_character.Alias} | " +
                $"Hunger: {_character.Needs.Hunger:0} | " +
                $"Thirst: {_character.Needs.Thirst:0} | " +
                $"Fatigue: {_character.Needs.Fatigue:0} | " +
                $"Goal: {goal}");

         return new Decision(
            goal,
            $"Hunger={_character.Needs.Hunger:0}, Thirst={_character.Needs.Thirst:0}, Fatigue={_character.Needs.Fatigue:0}"
         );
        }

        /// <summary>
        /// Chooses the most important goal based on the Character's current needs.
        /// </summary>
        private GoalType DetermineGoal()
        {
            if (_character.Needs.Hunger >= 75)
                return GoalType.FindFood;

            if (_character.Needs.Thirst >= 75)
                return GoalType.FindWater;

            if (_character.Needs.Fatigue >= 75)
                return GoalType.Sleep;

            return GoalType.Explore;
        }
    }
}