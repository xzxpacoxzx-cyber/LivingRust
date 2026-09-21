namespace LivingRust.AI
{
    /// <summary>
    /// Represents a decision made by a CharacterBrain.
    /// </summary>
    public class Decision
    {
        /// <summary>
        /// The goal this decision is trying to achieve.
        /// </summary>
        public GoalType Goal { get; }

        /// <summary>
        /// Why this decision was chosen.
        /// Useful for debugging AI behaviour.
        /// </summary>
        public string Reason { get; }

        public Decision(GoalType goal, string reason)
        {
            Goal = goal;
            Reason = reason;
        }

        public override string ToString()
        {
            return $"{Goal} ({Reason})";
        }
    }
}