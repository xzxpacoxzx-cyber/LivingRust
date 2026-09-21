using System.Collections.Generic;

namespace LivingRust.Inventory
{
    /// <summary>
    /// Manages the inventory system for players and NPCs, handling item collection and usage.
    /// </summary>
    public class InventoryManager
    {
        private Dictionary<string, int> items;

        public InventoryManager()
        {
            items = new Dictionary<string, int>();
        }

        /// <summary>
        /// Adds an item to the inventory.
        /// </summary>
        /// <param name="itemName">The name of the item to add.</param>
        /// <param name="quantity">The quantity of the item to add.</param>
        public void AddItem(string itemName, int quantity)
        {
            if (items.ContainsKey(itemName))
            {
                items[itemName] += quantity;
            }
            else
            {
                items[itemName] = quantity;
            }
        }

        /// <summary>
        /// Removes an item from the inventory.
        /// </summary>
        /// <param name="itemName">The name of the item to remove.</param>
        /// <param name="quantity">The quantity of the item to remove.</param>
        public void RemoveItem(string itemName, int quantity)
        {
            if (items.ContainsKey(itemName))
            {
                items[itemName] -= quantity;
                if (items[itemName] <= 0)
                {
                    items.Remove(itemName);
                }
            }
        }

        /// <summary>
        /// Gets the quantity of a specific item in the inventory.
        /// </summary>
        /// <param name="itemName">The name of the item to check.</param>
        /// <returns>The quantity of the item.</returns>
        public int GetItemQuantity(string itemName)
        {
            return items.ContainsKey(itemName) ? items[itemName] : 0;
        }

        /// <summary>
        /// Gets all items in the inventory.
        /// </summary>
        /// <returns>A dictionary of items and their quantities.</returns>
        public Dictionary<string, int> GetAllItems()
        {
            return new Dictionary<string, int>(items);
        }
    }
}