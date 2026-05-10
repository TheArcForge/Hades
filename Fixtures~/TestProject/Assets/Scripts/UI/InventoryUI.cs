using UnityEngine;
using TestProject.Player;

namespace TestProject.UI
{
    public class InventoryUI : MonoBehaviour
    {
        [SerializeField] private GameObject slotPrefab;
        [SerializeField] private Transform slotsContainer;
        [SerializeField] private PlayerInventory inventory;

        public void Refresh()
        {
            foreach (Transform child in slotsContainer)
                Destroy(child.gameObject);
        }
    }
}
