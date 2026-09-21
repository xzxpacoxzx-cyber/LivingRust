namespace LivingRust.Models
{
    /// <summary>
    /// Which of a BasePlayer's three inventory containers a SavedItem
    /// belongs in - PlayerInventory.containerMain/containerBelt/
    /// containerWear are separate ItemContainers (confirmed via reflection
    /// over Assembly-CSharp.dll), so a saved item needs to record which one
    /// it came from to be restored to the right place.
    /// </summary>
    public enum InventorySlot
    {
        Main,
        Belt,
        Wear
    }
}
