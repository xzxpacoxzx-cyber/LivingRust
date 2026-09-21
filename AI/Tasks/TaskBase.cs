namespace LivingRust.AI.Tasks
{
    /// <summary>
    /// Base class for tasks that NPCs can perform.
    /// This class provides a framework for modular task management.
    /// </summary>
    public abstract class TaskBase
    {
        public abstract void Execute();
        public abstract bool IsComplete { get; }
    }
}