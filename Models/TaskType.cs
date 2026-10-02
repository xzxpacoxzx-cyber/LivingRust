namespace LivingRust.Models
{
    /// <summary>
    /// A survivor's current self-directed, longer-running activity, as opposed to
    /// moment-to-moment reactive decisions.
    /// </summary>
    public enum TaskType
    {
        None,

        /// <summary>
        /// Walk to the nearest monument and loot every reachable container there.
        /// </summary>
        LootForResources,

        /// <summary>
        /// Walk to a recycler to free up inventory space by recycling unneeded items.
        /// </summary>
        Recycling,
    }
}
