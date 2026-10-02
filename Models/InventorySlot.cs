namespace LivingRust.Models
{
    /// <summary>
    /// Identifies which inventory container (main, belt, or wear) a saved item
    /// belongs to, so it can be restored to the correct place.
    /// </summary>
    public enum InventorySlot
    {
        Main,
        Belt,
        Wear
    }
}
