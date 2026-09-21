namespace LivingRust.Models
{
    /// <summary>
    /// A survivor's current self-directed job, distinct from the (still
    /// unwired) Needs/GoalType scaffold under AI/ - that system is about
    /// moment-to-moment reactive decisions (hunger/thirst/fatigue
    /// thresholds), this is about a longer-running activity a survivor
    /// sees through across multiple steps (walk to a monument, loot every
    /// reachable container, stop).
    /// </summary>
    public enum TaskType
    {
        None,

        /// <summary>
        /// Walk to the nearest monument and loot every reachable container
        /// there, taking everything found (not just resources - the task
        /// decides where to go, not what's worth carrying once there).
        /// </summary>
        LootForResources,

        /// <summary>
        /// Full inventory, nothing left to do but free up space - walking
        /// to a recycler, feeding it junk, and standing guard while it
        /// runs (see LivingRust.Recycling.cs).
        /// </summary>
        Recycling,
    }
}
