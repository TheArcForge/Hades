using System.Collections.Generic;
using UnityEngine;

namespace TestProject.Systems
{
    public class SpawnManager : MonoBehaviour
    {
        [SerializeField] private GameObject enemyPrefab;
        [SerializeField] private Transform[] spawnPoints;
        [SerializeField] private float spawnInterval = 3f;
        [SerializeField] private int maxEnemies = 10;

        private List<GameObject> _activeEnemies = new();
        private float _timer;

        void Update()
        {
            _timer += Time.deltaTime;
            if (_timer >= spawnInterval && _activeEnemies.Count < maxEnemies)
            {
                SpawnEnemy();
                _timer = 0;
            }
        }

        void SpawnEnemy()
        {
            var point = spawnPoints[Random.Range(0, spawnPoints.Length)];
            var enemy = Instantiate(enemyPrefab, point.position, point.rotation);
            _activeEnemies.Add(enemy);
        }
    }
}
