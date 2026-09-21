namespace LivingRust.Models
{
    /// <summary>
    /// Represents the permanent behavioural preferences of a Character.
    /// These values rarely change and define how the Character tends to
    /// approach situations throughout its lifetime.
    /// </summary>
    public class CharacterProfile
    {
        /// <summary>
        /// Confidence when making decisions.
        /// Higher confidence generally means taking initiative.
        /// </summary>
        public float Confidence { get; set; }

        /// <summary>
        /// Willingness to accept danger.
        /// High values may rush monuments or PvP.
        /// </summary>
        public float RiskTolerance { get; set; }

        /// <summary>
        /// Interest in fighting other players or AI.
        /// </summary>
        public float CombatInterest { get; set; }

        /// <summary>
        /// Interest in expanding and improving bases.
        /// </summary>
        public float BuildingInterest { get; set; }

        /// <summary>
        /// Interest in farming natural resources.
        /// </summary>
        public float FarmingInterest { get; set; }

        /// <summary>
        /// Interest in exploring the map.
        /// </summary>
        public float ExplorationInterest { get; set; }

        /// <summary>
        /// Interest in visiting monuments.
        /// </summary>
        public float MonumentInterest { get; set; }

        /// <summary>
        /// Interest in raiding other bases.
        /// </summary>
        public float RaidingInterest { get; set; }
    }
}