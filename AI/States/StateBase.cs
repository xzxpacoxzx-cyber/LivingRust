namespace LivingRust.AI.States
{
    /// <summary>
    /// Base class for AI states.
    /// This class provides a framework for different AI states that NPCs can be in.
    /// </summary>
    public abstract class StateBase
    {
        /// <summary>
        /// Called when entering the state.
        /// </summary>
        public abstract void Enter();

        /// <summary>
        /// Called when updating the state.
        /// </summary>
        public abstract void Update();

        /// <summary>
        /// Called when exiting the state.
        /// </summary>
        public abstract void Exit();
    }
}