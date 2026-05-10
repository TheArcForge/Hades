using UnityEngine;
using TestProject.Systems;

namespace TestProject.Enemy
{
    public class EnemyHealth : MonoBehaviour
    {
        [SerializeField] private int maxHealth = 50;
        [SerializeField] private EventChannel onEnemyDiedChannel;
        private int _currentHealth;

        void Start() => _currentHealth = maxHealth;

        public void TakeDamage(int amount)
        {
            _currentHealth -= amount;
            if (_currentHealth <= 0)
            {
                onEnemyDiedChannel?.Raise();
                Destroy(gameObject);
            }
        }
    }
}
