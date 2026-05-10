using System.Collections.Generic;
using UnityEngine;

namespace TestProject.Player
{
    public class PlayerInventory : MonoBehaviour
    {
        [SerializeField] private int maxSlots = 20;
        private List<ItemData> _items = new();

        public int ItemCount => _items.Count;
        public bool IsFull => _items.Count >= maxSlots;

        public bool AddItem(ItemData item)
        {
            if (IsFull) return false;
            _items.Add(item);
            return true;
        }

        public void RemoveItem(ItemData item) { _items.Remove(item); }
    }
}
