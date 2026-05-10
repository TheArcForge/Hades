using UnityEngine;

namespace TestProject.Systems
{
    [CreateAssetMenu(menuName = "TestProject/Item Data")]
    public class ItemData : ScriptableObject
    {
        public string itemName;
        public string description;
        public Sprite icon;
        public int maxStack = 1;
        public float weight;
    }
}
