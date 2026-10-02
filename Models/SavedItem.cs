namespace LivingRust.Models
{
    /// <summary>
    /// A serializable snapshot of a Rust item, used to recreate and restore it later.
    /// </summary>
    public class SavedItem
    {
        /// <summary>The item's numeric type ID.</summary>
        public int ItemId { get; set; }

        public int Amount { get; set; }

        /// <summary>Current durability of the item.</summary>
        public float Condition { get; set; }

        public ulong SkinId { get; set; }

        /// <summary>Slot index within its container, used to restore exact placement.</summary>
        public int Position { get; set; }

        public InventorySlot Container { get; set; }
    }
}
