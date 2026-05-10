using System.Collections.Generic;
using UnityEngine;

namespace TestProject.Systems
{
    public class ObjectPool : MonoBehaviour
    {
        [SerializeField] private GameObject prefab;
        [SerializeField] private int initialSize = 10;
        private Queue<GameObject> _pool = new();

        void Awake()
        {
            for (int i = 0; i < initialSize; i++)
            {
                var obj = Instantiate(prefab, transform);
                obj.SetActive(false);
                _pool.Enqueue(obj);
            }
        }

        public GameObject Get(Vector3 position, Quaternion rotation)
        {
            var obj = _pool.Count > 0 ? _pool.Dequeue() : Instantiate(prefab, transform);
            obj.transform.SetPositionAndRotation(position, rotation);
            obj.SetActive(true);
            return obj;
        }

        public void Return(GameObject obj) { obj.SetActive(false); _pool.Enqueue(obj); }
    }
}
