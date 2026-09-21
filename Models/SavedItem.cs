namespace LivingRust.Models
{
    /// <summary>
    /// A single serializable snapshot of a live Rust Item - enough to
    /// recreate it via ItemManager.CreateByItemID and put it back exactly
    /// where it was. Deliberately a plain DTO rather than persisting the
    /// real Item/ItemContainer objects directly, which are live game
    /// objects tied to the running server, not sensible to round-trip
    /// through JSON.
    /// </summary>
    public class SavedItem
    {
        /// <summary>ItemDefinition.itemid - the stable numeric item type.</summary>
        public int ItemId { get; set; }

        public int Amount { get; set; }

        /// <summary>Item.condition - current durability.</summary>
        public float Condition { get; set; }

        public ulong SkinId { get; set; }

        /// <summary>Slot index within its container, for restoring exact layout.</summary>
        public int Position { get; set; }

        public InventorySlot Container { get; set; }
    }
}
