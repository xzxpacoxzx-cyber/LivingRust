namespace LivingRust.AI
{
    /// <summary>
    /// Represents the current needs of a Character.
    /// Values range from 0 (satisfied) to 100 (critical).
    /// </summary>
    public class NeedState
    {
        public float Hunger { get; set; } = 0f;

        public float Thirst { get; set; } = 0f;

        public float Fatigue { get; set; } = 0f;

        public float Safety { get; set; } = 0f;
    }
}